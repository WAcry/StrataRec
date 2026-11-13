using System.Buffers;
using StrataRec.Core.Hashing;
using StrataRec.Core.Values;

namespace StrataRec.Logging;

/// <summary>
/// Request- and row-level sampling for funnel logs (design 8.2, 8.4). Decisions are stable hashes, so every tap of a
/// group sees the same requests and the same row priorities, and sampled candidates keep complete cross-stage traces.
/// </summary>
public static class CoordinatedSampling
{
    /// <summary>Seed of a funnel group; one per pipeline and group name.</summary>
    public static long GroupSeed(string pipelineId, string group) => StableHash.SeedFromSalt("funnel/" + pipelineId + "/" + group);

    /// <summary>
    /// Request-level decision <c>hash01(seed, keyIndex, key) &lt; rate</c>. <paramref name="keyIndex"/> is the position
    /// of the first non-missing key expression, so user-keyed and session-keyed requests never collide.
    /// </summary>
    public static bool SampleRequest(long groupSeed, int keyIndex, in ScalarValue key, double rate)
    {
        if (rate >= 1.0)
        {
            return true;
        }

        if (rate <= 0.0 || key.IsMissing)
        {
            return false;
        }

        using var hash = new StableHash.Builder();
        hash.Add(groupSeed);
        hash.AddIndex(keyIndex);
        hash.Add(key);
        return StableHash.ToUnit(hash.Finish()) < rate;
    }

    /// <summary>Hash of the request id, computed once per request and shared by all row priorities.</summary>
    public static long RequestHash(string requestId) => StableHash.SeedFromSalt(requestId);

    /// <summary>Row priority <c>u = hash01(groupSeed, request, entity)</c>, identical across taps of a group.</summary>
    public static double RowPriority(long groupSeed, long requestHash, long entityHash) =>
        StableHash.Hash01(groupSeed, requestHash, entityHash);

    /// <summary>
    /// Applies <c>rate</c> then bottom-k by priority (design 8.4). <paramref name="rows"/> and
    /// <paramref name="priorities"/> are parallel; the kept entries are moved to the front in their original order and
    /// their count is returned. <paramref name="rowProbability"/> is the inclusion probability of every kept row:
    /// <c>min(rate, τ)</c> where τ is the (k+1)-th smallest priority among eligible rows, or <c>rate</c> when at most k
    /// rows were eligible.
    /// </summary>
    public static int Select(Span<int> rows, Span<double> priorities, double rate, int? maxRows, out double rowProbability)
    {
        rowProbability = Math.Min(rate, 1.0);
        var count = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            if (priorities[i] < rate)
            {
                rows[count] = rows[i];
                priorities[count] = priorities[i];
                count++;
            }
        }

        if (maxRows is not { } k || count <= k)
        {
            return count;
        }

        // τ = (k+1)-th smallest priority: keep rows with priority < τ (exactly k rows; ties have probability ~0). The
        // sorted copy is pooled scratch: a tap over the rows a stage dropped can see thousands of candidates.
        var sorted = ArrayPool<double>.Shared.Rent(count);
        double tau;
        try
        {
            var copy = sorted.AsSpan(0, count);
            priorities[..count].CopyTo(copy);
            copy.Sort();
            tau = copy[k];
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sorted);
        }

        var kept = 0;
        for (var i = 0; i < count && kept < k; i++)
        {
            if (priorities[i] < tau)
            {
                rows[kept] = rows[i];
                priorities[kept] = priorities[i];
                kept++;
            }
        }

        rowProbability = Math.Min(rowProbability, tau);
        return kept;
    }
}
