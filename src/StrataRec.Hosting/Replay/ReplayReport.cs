using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Hosting.Replay;

/// <summary>Options of <see cref="RecEngine.ReplayAsync"/>.</summary>
public sealed class ReplayOptions
{
    /// <summary>Arms to run with (<c>experiment/arm</c>) instead of the recorded assignment.</summary>
    public IReadOnlyList<string>? Arms { get; init; }
}

/// <summary>A replayed request: the result the configuration produced from the recording, and how it differs.</summary>
public sealed class ReplayOutcome
{
    public required ReplayBundle Bundle { get; init; }

    /// <summary>
    /// The replayed result, with every node's outcome in <see cref="RecResult.Trace"/>. When the replay was aborted (see
    /// <see cref="AbortReason"/>) there is no result: this is a failure with the reason and no items, and the node
    /// decisions up to the abort are in <see cref="ReplayReport.Nodes"/>.
    /// </summary>
    public required RecResult Result { get; init; }

    /// <summary>
    /// Why the replay was aborted, as recorded; null when it returned a result. A recording of an aborted request replays
    /// its decisions up to the point of the abort and stops there (design 13.4).
    /// </summary>
    public string? AbortReason { get; init; }

    public bool Aborted => AbortReason is not null;

    public required ReplayReport Report { get; init; }
}

/// <summary>What changed between a recording and its replay (design 13.4).</summary>
public sealed class ReplayReport
{
    public required string RecordedManifest { get; init; }

    public required string ReplayedManifest { get; init; }

    /// <summary>Artifacts whose content id differs (null where one side does not have the artifact).</summary>
    public required IReadOnlyList<ValueChange> Artifacts { get; init; }

    public required IReadOnlyList<string> RecordedArms { get; init; }

    public required IReadOnlyList<string> ReplayedArms { get; init; }

    /// <summary>Adjustments the replay made to fit the configuration (dropped inputs or arms, runs without a recording).</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Every node of either run with its recorded and replayed outcome.</summary>
    public required IReadOnlyList<NodeComparison> Nodes { get; init; }

    /// <summary>Parameters whose value differs between the recorded and the replayed snapshot.</summary>
    public required IReadOnlyList<ValueChange> Params { get; init; }

    /// <summary>Data the configuration needed and the bundle lacked; each was treated as missing.</summary>
    public required IReadOnlyList<ReplayGap> Gaps { get; init; }

    /// <summary>Requested and missing feature values per field during the replay.</summary>
    public required IReadOnlyList<ReplayFieldStats> FieldStats { get; init; }

    public required ResultDiff Diff { get; init; }

    /// <summary>Engine time of the replay; I/O is answered from the recording, so this excludes all I/O wait.</summary>
    public double ElapsedMs { get; init; }
}

/// <summary>A named value on both sides; null where a side does not have it.</summary>
public sealed record ValueChange(string Name, string? Recorded, string? Replayed);

/// <summary>A node's outcome in the recording and in the replay (category names as in bundles, null where the node did not run).</summary>
public sealed record NodeComparison(string Node, string? Recorded, string? Replayed, string? RecordedDetail, string? ReplayedDetail)
{
    public bool Changed => Recorded != Replayed;
}

/// <summary>A column value that differs at one rank.</summary>
public sealed record ColumnChange(string Column, ScalarValue Recorded, ScalarValue Replayed);

/// <summary>A rank whose item or values differ; a null id means that side has no item at the rank.</summary>
public sealed record ItemChange(int Rank, EntityKey? RecordedId, EntityKey? ReplayedId, IReadOnlyList<ColumnChange> Columns);

/// <summary>Difference of two results: the recorded one and the replayed one (or a baseline and a candidate).</summary>
public sealed class ResultDiff
{
    /// <summary>Both aborted, or neither with the same success, the same items in the same order and every column value equal.</summary>
    public required bool Identical { get; init; }

    public required bool LeftSucceeded { get; init; }

    public required bool RightSucceeded { get; init; }

    /// <summary>The left (recorded) request was aborted and has no result.</summary>
    public bool LeftAborted { get; init; }

    /// <summary>The right (replayed) run stopped where its recording was aborted.</summary>
    public bool RightAborted { get; init; }

    public required int LeftCount { get; init; }

    public required int RightCount { get; init; }

    /// <summary>|top-k ∩ top-k| / k with k the longer list; 1 when both are empty.</summary>
    public required double Overlap { get; init; }

    /// <summary>Kendall τ over the items both lists contain; null with fewer than two in common.</summary>
    public required double? KendallTau { get; init; }

    /// <summary>Output columns only the right (replayed) result has; their values are not compared.</summary>
    public required IReadOnlyList<string> AddedColumns { get; init; }

    /// <summary>Output columns only the left (recorded) result has.</summary>
    public required IReadOnlyList<string> RemovedColumns { get; init; }

    /// <summary>Ranks whose item differs, or whose values differ in a column both results have.</summary>
    public required IReadOnlyList<ItemChange> Changes { get; init; }
}

/// <summary>A result reduced to what comparisons need: ids in rank order and decoded column values.</summary>
public sealed class ComparableResult
{
    public required bool Succeeded { get; init; }

    /// <summary>The request (or replay) was aborted and returned no result.</summary>
    public bool Aborted { get; init; }

    public required IReadOnlyList<EntityKey> Ids { get; init; }

    public required IReadOnlyList<IReadOnlyDictionary<string, ScalarValue>> Columns { get; init; }

    /// <param name="aborted">The replay stopped where its recording was aborted; <paramref name="result"/> is a placeholder failure.</param>
    public static ComparableResult From(RecResult result, bool aborted = false)
    {
        var ids = new List<EntityKey>(result.Items.Count);
        var columns = new List<IReadOnlyDictionary<string, ScalarValue>>(result.Items.Count);
        foreach (var item in result.Items)
        {
            ids.Add(item.Id);
            columns.Add(item.Columns);
        }

        return new ComparableResult { Succeeded = result.Succeeded, Aborted = aborted, Ids = ids, Columns = columns };
    }

    /// <summary>Decodes a recorded result with its recorded column types.</summary>
    /// <exception cref="ReplayBundleException">An id, type or value is malformed.</exception>
    public static ComparableResult From(ReplayResultRecord result, string pointer)
    {
        var types = new Dictionary<string, ColumnType>(StringComparer.Ordinal);
        foreach (var (name, type) in result.ColumnTypes)
        {
            types[name] = ReplayJson.Type(type, pointer + "/column_types/" + Artifacts.Json.JsonPointer.Escape(name));
        }

        var ids = new List<EntityKey>(result.Items.Count);
        var columns = new List<IReadOnlyDictionary<string, ScalarValue>>(result.Items.Count);
        for (var i = 0; i < result.Items.Count; i++)
        {
            var item = result.Items[i];
            var at = $"{pointer}/items/{i}";
            ids.Add(ReplayJson.Key(item.Id, at + "/id"));
            var values = new Dictionary<string, ScalarValue>(StringComparer.Ordinal);
            foreach (var (name, json) in item.Columns)
            {
                if (!types.TryGetValue(name, out var type))
                {
                    throw new ReplayBundleException(at + "/columns/" + Artifacts.Json.JsonPointer.Escape(name), $"column '{name}' has no entry in column_types",
                        "record the type of every output column");
                }

                values[name] = ReplayJson.Value(json, type, at + "/columns/" + Artifacts.Json.JsonPointer.Escape(name));
            }

            columns.Add(values);
        }

        return new ComparableResult { Succeeded = result.Succeeded, Aborted = result.Aborted, Ids = ids, Columns = columns };
    }
}

/// <summary>Result comparison and ranking metrics (design 10.8): Top-K overlap, Kendall τ, score distributions.</summary>
public static class ResultComparison
{
    public static ResultDiff Diff(ComparableResult left, ComparableResult right)
    {
        // A column only one side has (the configuration added or removed an output) is reported once, not per item; a
        // missing value in a column both sides have is a value like any other.
        var leftNames = ColumnNames(left);
        var rightNames = ColumnNames(right);
        var shared = new SortedSet<string>(leftNames, StringComparer.Ordinal);
        shared.IntersectWith(rightNames);
        var added = new List<string>(rightNames);
        added.RemoveAll(shared.Contains);
        var removed = new List<string>(leftNames);
        removed.RemoveAll(shared.Contains);

        var changes = new List<ItemChange>();
        var count = Math.Max(left.Ids.Count, right.Ids.Count);
        for (var i = 0; i < count; i++)
        {
            EntityKey? leftId = i < left.Ids.Count ? left.Ids[i] : null;
            EntityKey? rightId = i < right.Ids.Count ? right.Ids[i] : null;
            var columns = new List<ColumnChange>();
            if (leftId is not null && rightId is not null)
            {
                foreach (var name in shared)
                {
                    left.Columns[i].TryGetValue(name, out var a);
                    right.Columns[i].TryGetValue(name, out var b);
                    if (!a.Equals(b))
                    {
                        columns.Add(new ColumnChange(name, a, b));
                    }
                }
            }

            if (leftId != rightId || columns.Count > 0)
            {
                changes.Add(new ItemChange(i + 1, leftId, rightId, columns));
            }
        }

        return new ResultDiff
        {
            Identical = left.Aborted == right.Aborted && left.Succeeded == right.Succeeded && changes.Count == 0 && added.Count == 0 && removed.Count == 0,
            LeftSucceeded = left.Succeeded,
            RightSucceeded = right.Succeeded,
            LeftAborted = left.Aborted,
            RightAborted = right.Aborted,
            LeftCount = left.Ids.Count,
            RightCount = right.Ids.Count,
            Overlap = Overlap(left.Ids, right.Ids, count),
            KendallTau = KendallTau(left.Ids, right.Ids),
            AddedColumns = added,
            RemovedColumns = removed,
            Changes = changes,
        };
    }

    private static List<string> ColumnNames(ComparableResult result)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var columns in result.Columns)
        {
            names.UnionWith(columns.Keys);
        }

        return new List<string>(names);
    }

    /// <summary>|top-k(a) ∩ top-k(b)| / max(|top-k(a)|, |top-k(b)|); 1 when both are empty.</summary>
    public static double Overlap(IReadOnlyList<EntityKey> a, IReadOnlyList<EntityKey> b, int k)
    {
        var left = Math.Min(k, a.Count);
        var right = Math.Min(k, b.Count);
        var denominator = Math.Max(left, right);
        if (denominator == 0)
        {
            return 1.0;
        }

        var top = new HashSet<EntityKey>();
        for (var i = 0; i < left; i++)
        {
            top.Add(a[i]);
        }

        var shared = 0;
        for (var i = 0; i < right; i++)
        {
            shared += top.Contains(b[i]) ? 1 : 0;
        }

        return (double)shared / denominator;
    }

    /// <summary>
    /// Kendall τ between two rankings over the items both contain: (concordant − discordant) / pairs. Rankings have no
    /// ties, so this is also τ-b. Null when fewer than two items are shared.
    /// </summary>
    public static double? KendallTau(IReadOnlyList<EntityKey> a, IReadOnlyList<EntityKey> b)
    {
        var rankInB = new Dictionary<EntityKey, int>(b.Count);
        for (var i = 0; i < b.Count; i++)
        {
            rankInB.TryAdd(b[i], i);
        }

        var shared = new List<int>(); // rank in b, in a's order
        for (var i = 0; i < a.Count; i++)
        {
            if (rankInB.TryGetValue(a[i], out var rank))
            {
                shared.Add(rank);
            }
        }

        var n = shared.Count;
        if (n < 2)
        {
            return null;
        }

        long concordant = 0;
        long discordant = 0;
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (shared[i] < shared[j])
                {
                    concordant++;
                }
                else
                {
                    discordant++;
                }
            }
        }

        return (double)(concordant - discordant) / (concordant + discordant);
    }

    /// <summary>
    /// Two-sample Kolmogorov–Smirnov statistic: the largest gap between the empirical distributions (0–1), over the
    /// finite values of each sample. NaN and ±∞ have no place in a distribution of scores; they are left out (callers
    /// count them separately), so the statistic is defined and the computation ends for any input.
    /// </summary>
    public static double KolmogorovSmirnov(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var left = SortedFinite(a);
        var right = SortedFinite(b);
        if (left.Length == 0 || right.Length == 0)
        {
            return left.Length == right.Length ? 0.0 : 1.0;
        }

        var i = 0;
        var j = 0;
        var max = 0.0;
        while (i < left.Length && j < right.Length)
        {
            var x = Math.Min(left[i], right[j]);
            while (i < left.Length && left[i] <= x)
            {
                i++;
            }

            while (j < right.Length && right[j] <= x)
            {
                j++;
            }

            max = Math.Max(max, Math.Abs(((double)i / left.Length) - ((double)j / right.Length)));
        }

        return max;
    }

    /// <summary>
    /// Summary of a sample's finite values: count, mean, min, p50, p90, p95, max (nearest-rank percentiles). NaN and ±∞
    /// are left out, as in <see cref="KolmogorovSmirnov"/>.
    /// </summary>
    public static Distribution Summarize(IReadOnlyList<double> values)
    {
        var sorted = SortedFinite(values);
        if (sorted.Length == 0)
        {
            return new Distribution(0, null, null, null, null, null, null);
        }

        var sum = 0.0;
        foreach (var v in sorted)
        {
            sum += v;
        }

        return new Distribution(sorted.Length, sum / sorted.Length, sorted[0], Percentile(sorted, 0.5), Percentile(sorted, 0.9), Percentile(sorted, 0.95),
            sorted[^1]);
    }

    private static double Percentile(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>The finite values, sorted; every iteration of the KS walk then advances at least one index.</summary>
    private static double[] SortedFinite(IReadOnlyList<double> values)
    {
        var count = 0;
        for (var i = 0; i < values.Count; i++)
        {
            count += double.IsFinite(values[i]) ? 1 : 0;
        }

        var array = new double[count];
        var n = 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (double.IsFinite(values[i]))
            {
                array[n++] = values[i];
            }
        }

        Array.Sort(array);
        return array;
    }
}

/// <summary>A sample summary; statistics are null for an empty sample.</summary>
public sealed record Distribution(int Count, double? Mean, double? Min, double? P50, double? P90, double? P95, double? Max);
