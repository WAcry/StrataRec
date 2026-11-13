using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// A candidate fetch must not allocate per key or per synchronously answered chunk (design 4.8, 15): the key set, its
/// index and the per-chunk arrays are rented from the request memory, the chunks share one staging area and one
/// cancellation source, and <c>memory@1</c> looks int64 keys up without formatting them.
/// </summary>
[Collection(AllocationMeasurement.Name)]
public class FeatureFetchAllocationTests
{
    [Fact]
    public void CandidateFetch_AllocationDoesNotGrowWithTheKeyCount()
    {
        const int Small = 100;
        const int Large = 4_000;
        var json = Items(Large);

        // One chunk at either size, so the per-call objects are the same.
        var (rig, features) = Rig(json, maxBatch: 10_000);
        var smallBytes = Measure(rig, features, Small);
        var largeBytes = Measure(rig, features, Large);

        // Before the rented key index, 3,900 more keys cost ~570 KB (dictionary, key list and a string per key); now both
        // sizes allocate the same.
        Assert.True(largeBytes - smallBytes < 1024, $"{Small} keys: {smallBytes} B, {Large} keys: {largeBytes} B");
    }

    [Fact]
    public void SynchronouslyAnsweredChunk_AllocatesNothing()
    {
        // The same 2,000 keys in 100 or in 200 chunks. A request sends hundreds of chunks (candidates / max_batch); one
        // answered synchronously used to cost ~420 B (keys, staging buffer and columns, cancellation source and timer,
        // lease, task). Any object per chunk is at least 24 B, so 100 more chunks would show as 2.4 KB or more. The
        // connector is not an async method: Debug builds compile async state machines as classes, one per call.
        const int Keys = 2_000;
        var fewer = Measure(SynchronousRig(maxBatch: 20), Keys);
        var more = Measure(SynchronousRig(maxBatch: 10), Keys);

        Assert.True(more - fewer < 100 * 8, $"100 chunks: {fewer} B, 200 chunks: {more} B");
    }

    [Fact]
    public void LocalRead_AllocatesNothing_AtAnyKeyCount()
    {
        // A local read (design 6.6) is one lookup and one multi-field fill into the request's columns, with every buffer
        // rented from the request memory: neither the read nor its key count may cost an object. Measured against a
        // second ensure of the same features, which has nothing to read, so the ensure's own cost (in Debug, its async
        // state machine) cancels out. Foreign-key and int64 / float32 / float64 / bool fields, with misses and defaults.
        const int Items = 4_000;
        var rig = new FeatureTestRig().LocalSource("item_store", writer =>
        {
            for (var id = 1; id <= Items; id += 2) // odd items only: half the keys miss
            {
                var builder = writer.Begin(id, 1);
                builder.Set(writer.Schema.FieldIndex("score"), ScalarValue.FromFloat64(id * 0.5));
                builder.Set(writer.Schema.FieldIndex("clicks"), ScalarValue.FromFloat64(id));
                builder.Set(writer.Schema.FieldIndex("author_id"), ScalarValue.FromInt64(id % 100));
                builder.Set(writer.Schema.FieldIndex("safe"), ScalarValue.FromBool(id % 3 != 0));
                builder.Commit();
            }
        }).LocalSource("author_store", writer =>
        {
            for (var id = 0; id < 50; id++)
            {
                var builder = writer.Begin(id, 1);
                builder.Set(writer.Schema.FieldIndex("followers"), ScalarValue.FromFloat64(id * 10.0));
                builder.Commit();
            }
        });
        var features = new[]
        {
            rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store"),
            rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0)),
            rig.Physical("item.author_id", FeatureTestRig.Item, ColumnType.Int64, "item_store"),
            rig.Physical("item.safe", FeatureTestRig.Item, ColumnType.Bool, "item_store", ScalarValue.FromBool(false)),
            rig.Physical("author.followers", FeatureTestRig.ForeignKey("author", "item.author_id"), ColumnType.Float64, "author_store", ScalarValue.FromFloat64(-1)),
        };
        rig.Build();

        var small = MeasureLocalRead(rig, features, 100);
        var large = MeasureLocalRead(rig, features, Items);

        Assert.True(small <= 0 && large <= 0, $"100 keys: {small} B, {Items} keys: {large} B beyond an ensure with nothing to read");
    }

    /// <summary>Bytes the first ensure of <paramref name="features"/> allocates beyond a second one: best of three after a warm-up.</summary>
    private static long MeasureLocalRead(FeatureTestRig rig, int[] features, int keys)
    {
        var candidates = Enumerable.Range(1, keys).Select(i => (long)i).ToArray();
        var best = long.MaxValue;
        for (var round = 0; round < 4; round++)
        {
            var request = rig.NewRequest(candidates);
            foreach (var feature in features)
            {
                request.Table.GetOrCreateSlot(rig.SlotOf(feature)); // the columns exist before any read, as after a fetch
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var read = request.Fetcher.EnsureAsync(features, CancellationToken.None);
            var readBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            var again = request.Fetcher.EnsureAsync(features, CancellationToken.None);
            var againBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(read.IsCompletedSuccessfully && again.IsCompletedSuccessfully);
            Assert.Equal(FeatureState.Ok, read.Result.State);
            Assert.Equal(0.5, request.Value(features[0], 0).AsFloat64());
            Assert.True(request.Value(features[0], 1).IsMissing); // item 2 has no record
            Assert.Equal(-1.0, request.Value(features[4], 1).AsFloat64()); // item 2 has no record, so no author
            Assert.Equal(10.0, request.Value(features[4], 0).AsFloat64()); // item 1: author 1
            request.Memory.Release();
            if (round > 0)
            {
                best = Math.Min(best, readBytes - againBytes);
            }
        }

        return best;
    }

    private static (FeatureTestRig Rig, int[] Features) SynchronousRig(int maxBatch)
    {
        var rig = new FeatureTestRig().Source("item_store", new SynchronousConnector(), maxBatch: maxBatch);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0));
        rig.Build();
        return (rig, new[] { score, clicks });
    }

    private static string Items(int count)
    {
        var json = new StringBuilder("{");
        for (var id = 1; id <= count; id++)
        {
            json.Append(id == 1 ? string.Empty : ",").Append('"').Append(id).Append("\": {\"score\": ").Append(id * 0.5).Append(", \"clicks\": ").Append(id).Append('}');
        }

        return json.Append('}').ToString();
    }

    private static (FeatureTestRig Rig, int[] Features) Rig(string json, int maxBatch)
    {
        var rig = new FeatureTestRig().MemorySource("item_store", json, maxBatch: maxBatch);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0));
        rig.Build();
        return (rig, new[] { score, clicks });
    }

    private static long Measure((FeatureTestRig Rig, int[] Features) setup, int keys) => Measure(setup.Rig, setup.Features, keys);

    /// <summary>
    /// Bytes one request fetching <paramref name="features"/> for keys 1..<paramref name="keys"/> allocates on this thread:
    /// the best of three rounds after a warm-up. The first rents fill the array pool and JIT-compile the path; an
    /// allocation per key or per chunk shows up in every round.
    /// </summary>
    private static long Measure(FeatureTestRig rig, int[] features, int keys)
    {
        var candidates = Enumerable.Range(1, keys).Select(i => (long)i).ToArray();
        var best = long.MaxValue;
        for (var round = 0; round < 4; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var request = rig.NewRequest(candidates);
            var fill = request.Fetcher.EnsureAsync(features, CancellationToken.None);
            var completed = fill.IsCompletedSuccessfully; // memory@1 without latency: everything ran on this thread
            var state = completed ? fill.Result.State : FeatureState.Failed;
            var last = ColumnValues.Get(request.Table.TryGetSlot(rig.SlotOf(features[0]))!, candidates.Length - 1);
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

    /// <summary>Answers every key at once (score = key × 0.5, clicks = key) without allocating.</summary>
    private sealed class SynchronousConnector : IFeatureSourceConnector
    {
        public string ConnectorType => TestConnectorFactory.Type;

        public bool Supports(KeyShape shape) => shape == KeyShape.Many;

        public ValueTask<FetchStatus> FetchAsync(
            EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
        {
            var span = keys.CandidateKeys.Span;
            foreach (var field in fields.Span)
            {
                for (var k = 0; k < span.Length; k++)
                {
                    if (field.Type.Kind == StorageKind.Float64)
                    {
                        writer.GetValues<double>(field.FieldIndex).Span[k] = span[k].Int64Value * 0.5;
                    }
                    else
                    {
                        writer.GetValues<float>(field.FieldIndex).Span[k] = span[k].Int64Value;
                    }

                    writer.SetValid(field.FieldIndex, k);
                }
            }

            return new ValueTask<FetchStatus>(FetchStatus.Ok);
        }
    }
}
