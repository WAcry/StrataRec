using System.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Services;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Replay;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>What laying out a call's digests and checking its inputs cost. With the allocation measurements, which run alone.</summary>
[Collection(AllocationMeasurement.Name)]
public sealed class ServiceDigestCostTests
{
    /// <summary>
    /// The items' digests and keys of a call of up to 64 inputs allocate nothing once the pools are warm (review of
    /// 5e07f1b: a call of exactly 32 took its layout from the heap).
    /// </summary>
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(64)]
    public void TheItemDigestsAndKeys_AllocateNothing(int inputCount)
    {
        const int rows = 16;
        var inputs = Enumerable.Range(0, inputCount)
            .Select(i => NamedInput.FromFloat32("f" + i, i % 2 == 0 ? [rows] : [1], Enumerable.Range(0, i % 2 == 0 ? rows : 1).Select(r => (float)r).ToArray()))
            .ToArray();
        var call = new ServiceCall { Service = "m", BatchSize = rows, Items = Enumerable.Range(0, rows).Select(r => EntityKey.FromInt64(r)).ToArray(), Inputs = inputs, Outputs = new[] { "p" } };
        var order = ServiceInputDigest.NameOrder(call);
        var digests = new ulong[rows];
        var keys = new UInt128[rows];
        ServiceInputDigest.Rows(call, order, digests);
        ServiceInputDigest.CacheItemKeys(call, order, "m@1", keys);

        var before = GC.GetAllocatedBytesForCurrentThread();
        ServiceInputDigest.Rows(call, order, digests);
        ServiceInputDigest.CacheItemKeys(call, order, "m@1", keys);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// Two different input names of one hash are compared with each other only: a call of many inputs is checked as fast
    /// as one without (review of 5e07f1b: one collision compared every pair, and 16,384 inputs took 381 ms), and a real
    /// duplicate elsewhere is still found.
    /// </summary>
    [Fact]
    public void InputNamesOfOneHash_AreComparedWithEachOtherOnly()
    {
        var (a, b) = TwoNamesOfOneHash();
        var names = Enumerable.Range(0, 16_384).Select(i => "input_" + i).Where(n => n != a && n != b).Take(16_382).Append(a).Append(b).ToArray();
        var inputs = names.Select(n => NamedInput.FromFloat32(n, [], new[] { 1f })).ToArray();
        var duplicated = inputs.Append(NamedInput.FromFloat32("input_7", [], new[] { 2f })).ToArray();

        var again = Assert.Throws<ArgumentException>(() => OperatorContext.CheckInputs(new ServiceCall { Service = "s", Inputs = duplicated }));
        var start = Stopwatch.GetTimestamp();
        OperatorContext.CheckInputs(new ServiceCall { Service = "s", Inputs = inputs });
        var elapsed = Stopwatch.GetElapsedTime(start);

        Assert.StartsWith("The call names input 'input_7' twice", again.Message, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(100), $"checking 16,384 names took {elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>Two different names whose ordinal hashes are equal in this process (the hash is randomized per process).</summary>
    private static (string A, string B) TwoNamesOfOneHash()
    {
        var seen = new Dictionary<int, string>();
        for (var i = 0; ; i++)
        {
            var name = "collide_" + i;
            if (seen.TryGetValue(string.GetHashCode(name, StringComparison.Ordinal), out var other))
            {
                return (other, name);
            }

            seen[string.GetHashCode(name, StringComparison.Ordinal)] = name;
        }
    }
}
