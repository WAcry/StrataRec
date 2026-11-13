using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// A candidate fetch must not allocate per key (design 4.8, 15): the key set, its index and the per-chunk arrays are
/// rented from the request memory, and <c>memory@1</c> looks int64 keys up without formatting them.
/// </summary>
[Collection(AllocationMeasurement.Name)]
public class FeatureFetchAllocationTests
{
    [Fact]
    public void CandidateFetch_AllocationDoesNotGrowWithTheKeyCount()
    {
        const int Small = 100;
        const int Large = 4_000;
        var json = new StringBuilder("{");
        for (var id = 1; id <= Large; id++)
        {
            json.Append(id == 1 ? string.Empty : ",").Append('"').Append(id).Append("\": {\"score\": ").Append(id * 0.5).Append(", \"clicks\": ").Append(id).Append('}');
        }

        // One chunk at either size, so the per-call objects (task, lease, staging) are the same.
        var rig = new FeatureTestRig().MemorySource("item_store", json.Append('}').ToString(), maxBatch: 10_000);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0));
        rig.Build();
        var small = Enumerable.Range(1, Small).Select(i => (long)i).ToArray();
        var large = Enumerable.Range(1, Large).Select(i => (long)i).ToArray();
        int[] features = { score, clicks };

        long Measure(long[] candidates)
        {
            var best = long.MaxValue;

            // Best of three rounds after a warm-up: the first rents fill the array pool and JIT-compile the path; an
            // allocation per key shows up in every round.
            for (var round = 0; round < 4; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var request = rig.NewRequest(candidates);
                var fill = request.Fetcher.EnsureAsync(features, CancellationToken.None);
                var completed = fill.IsCompletedSuccessfully; // memory@1 without latency: everything ran on this thread
                var state = completed ? fill.Result.State : FeatureState.Failed;
                var last = ColumnValues.Get(request.Table.TryGetSlot(rig.SlotOf(score))!, candidates.Length - 1);
                request.Memory.Release();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.True(completed);
                Assert.Equal(FeatureState.Ok, state);
                Assert.Equal(candidates[^1] * 0.5, last.AsFloat64());
                if (round > 0)
                {
                    best = Math.Min(best, allocated);
                }
            }

            return best;
        }

        var smallBytes = Measure(small);
        var largeBytes = Measure(large);

        // Before the rented key index, 3,900 more keys cost ~570 KB (dictionary, key list and a string per key); now both
        // sizes allocate the same.
        Assert.True(largeBytes - smallBytes < 1024, $"{Small} keys: {smallBytes} B, {Large} keys: {largeBytes} B");
    }
}
