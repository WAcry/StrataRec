using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

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

        Assert.True(largeBytes - smallBytes < 1024, $"{Small} keys: {smallBytes} B, {Large} keys: {largeBytes} B");
    }

    [Fact]
    public void SynchronouslyAnsweredChunk_AllocatesNothing()
    {
        // Any object per chunk is at least 24 B, so 100 more chunks would show as 2.4 KB or more. The connector is not
        // an async method: Debug builds compile async state machines as classes, one per call.
        const int Keys = 2_000;
        var fewer = Measure(SynchronousRig(maxBatch: 20), Keys);
        var more = Measure(SynchronousRig(maxBatch: 10), Keys);

        Assert.True(more - fewer < 100 * 8, $"100 chunks: {fewer} B, 200 chunks: {more} B");
    }

    /// <summary>
    /// Measured over every thread, since the continuations run on the pool; the same 2,000 keys in 100 or in 200
    /// chunks, so the difference is 100 chunks' worth. About 1.19 KB a chunk on .NET 10, the yielding connector's own
    /// included.
    /// </summary>
    [Fact]
    public async Task AnAsynchronouslyAnsweredChunk_CostsItsWaitAlone()
    {
        const int Keys = 2_000;
        var fewer = await MeasureAllThreadsAsync(AsynchronousRig(maxBatch: 20), Keys);
        var more = await MeasureAllThreadsAsync(AsynchronousRig(maxBatch: 10), Keys);

        var perChunk = (more - fewer) / 100.0;
        Assert.True(perChunk < 1_350, $"100 chunks: {fewer} B, 200 chunks: {more} B, {perChunk:F0} B a chunk");
    }

    [Fact]
    public void LocalRead_AllocatesNothing_AtAnyKeyCount()
    {
        // Measured against a second ensure of the same features, which has nothing to read, so the ensure's own cost
        // (in Debug, its async state machine) cancels out.
        const int Items = 4_000;
        var rig = new FeatureTestRig().LocalSource("item_store", writer =>
        {
            for (var id = 1; id <= Items; id += 2)
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

    [Fact]
    public void CompositeKeyLocalRead_AllocatesNothing_AtAnyKeyCount()
    {
        const int Items = 4_000;
        var byCountry = EntityDefinition.Of(
            "item_country", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.RequestInput("country", EntityKeyType.String));
        var byCategory = EntityDefinition.Of(
            "item_category", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.Feature("item.category", EntityKeyType.String));
        var rig = new FeatureTestRig().LocalSource("item_store", writer =>
        {
            for (var id = 1; id <= Items; id++)
            {
                var builder = writer.Begin(id, 1);
                builder.Set(writer.Schema.FieldIndex("category"), ScalarValue.FromString("c" + (id % 5)));
                builder.Commit();
            }
        }).LocalSource("ic_store", writer =>
        {
            for (var id = 1; id <= Items; id += 2)
            {
                var builder = writer.Begin([EntityKey.FromInt64(id), EntityKey.FromString("US")], 1);
                builder.Set(writer.Schema.FieldIndex("boost"), ScalarValue.FromFloat64(id));
                builder.Commit();
            }
        }).LocalSource("icat_store", writer =>
        {
            for (var id = 1; id <= Items; id++)
            {
                var builder = writer.Begin([EntityKey.FromInt64(id), EntityKey.FromString("c" + (id % 5))], 1);
                builder.Set(writer.Schema.FieldIndex("score"), ScalarValue.FromFloat64(id * 0.5));
                builder.Commit();
            }
        });
        var country = rig.RequestInput("country");
        var features = new[]
        {
            rig.Physical("item_category.score", byCategory, ColumnType.Float64, "icat_store"),
            rig.Physical("item.category", FeatureTestRig.Item, ColumnType.String, "item_store"),
            rig.Physical("item_country.boost", byCountry, ColumnType.Float64, "ic_store", ScalarValue.FromFloat64(-1)),
        };
        rig.Build();

        void Check(TestRequest request)
        {
            Assert.Equal(0.5, request.Value(features[0], 0).AsFloat64());
            Assert.Equal(1.0, request.Value(features[2], 0).AsFloat64());
            Assert.Equal(-1.0, request.Value(features[2], 1).AsFloat64());
        }

        var small = MeasureLocalRead(rig, features, 100, request => request.Context[country] = ScalarValue.FromString("US"), Check);
        var large = MeasureLocalRead(rig, features, Items, request => request.Context[country] = ScalarValue.FromString("US"), Check);

        Assert.True(small <= 0 && large <= 0, $"100 keys: {small} B, {Items} keys: {large} B beyond an ensure with nothing to read");
    }

    [Fact]
    public void CompositeKeyFetch_AllocationDoesNotGrowWithTheKeyCount()
    {
        var byCountry = EntityDefinition.Of(
            "item_country", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.RequestInput("country", EntityKeyType.String));
        var rig = new FeatureTestRig().Source("ic_store", new SynchronousConnector(), maxBatch: 10_000);
        var country = rig.RequestInput("country");
        var score = rig.Physical("item_country.score", byCountry, ColumnType.Float64, "ic_store");
        var clicks = rig.Physical("item_country.clicks", byCountry, ColumnType.Float32, "ic_store", ScalarValue.FromFloat64(0));
        rig.Build();

        var smallBytes = Measure(rig, [score, clicks], 100, request => request.Context[country] = ScalarValue.FromString("US"));
        var largeBytes = Measure(rig, [score, clicks], 4_000, request => request.Context[country] = ScalarValue.FromString("US"));

        Assert.True(largeBytes - smallBytes < 1024, $"100 keys: {smallBytes} B, 4000 keys: {largeBytes} B");
    }

    [Fact]
    public void AFetchKeyedByAContextFeature_AllocationDoesNotGrowWithTheKeyCount()
    {
        var bySegment = EntityDefinition.Of(
            "item_segment", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.ContextFeature("user.segment", EntityKeyType.String));
        var rig = new FeatureTestRig()
            .Source("is_store", new SynchronousConnector(), maxBatch: 10_000)
            .MemorySource("user_store", """{ "7": { "segment": "a" } }""");
        var segment = rig.Physical("user.segment", FeatureTestRig.User, ColumnType.String, "user_store");
        var score = rig.Physical("item_segment.score", bySegment, ColumnType.Float64, "is_store");
        var clicks = rig.Physical("item_segment.clicks", bySegment, ColumnType.Float32, "is_store", ScalarValue.FromFloat64(0));
        rig.Build();

        void StartContext(TestRequest request)
        {
            request.Context[FeatureTestRig.UserIdContext] = ScalarValue.FromInt64(7);
            request.Fetcher.StartContextFetch([segment], CancellationToken.None);
        }

        var smallBytes = Measure(rig, [score, clicks], 100, StartContext);
        var largeBytes = Measure(rig, [score, clicks], 4_000, StartContext);

        Assert.True(largeBytes - smallBytes < 1024, $"100 keys: {smallBytes} B, 4000 keys: {largeBytes} B");
    }

    private static long MeasureLocalRead(
        FeatureTestRig rig, int[] features, int keys, Action<TestRequest>? setup = null, Action<TestRequest>? check = null)
    {
        var candidates = Enumerable.Range(1, keys).Select(i => (long)i).ToArray();
        var best = long.MaxValue;
        for (var round = 0; round < 4; round++)
        {
            var request = rig.NewRequest(candidates);
            setup?.Invoke(request);
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
            if (check is not null)
            {
                check(request);
            }
            else
            {
                Assert.Equal(0.5, request.Value(features[0], 0).AsFloat64());
                Assert.True(request.Value(features[0], 1).IsMissing);
                Assert.Equal(-1.0, request.Value(features[4], 1).AsFloat64());
                Assert.Equal(10.0, request.Value(features[4], 0).AsFloat64());
            }

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

    private static (FeatureTestRig Rig, int[] Features) AsynchronousRig(int maxBatch)
    {
        var rig = new FeatureTestRig().Source("item_store", new YieldingConnector(), maxBatch: maxBatch);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0));
        rig.Build();
        return (rig, new[] { score, clicks });
    }

    private static async Task<long> MeasureAllThreadsAsync((FeatureTestRig Rig, int[] Features) setup, int keys)
    {
        var (rig, features) = setup;
        var candidates = Enumerable.Range(1, keys).Select(i => (long)i).ToArray();
        var best = long.MaxValue;
        for (var round = 0; round < 6; round++)
        {
            var request = rig.NewRequest(candidates);
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var result = await request.Fetcher.EnsureAsync(features, CancellationToken.None);
            var last = ColumnValues.Get(request.Table.TryGetSlot(rig.SlotOf(features[0]))!, candidates.Length - 1);
            request.Memory.Release();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Assert.Equal(FeatureState.Ok, result.State);
            Assert.Equal(candidates[^1] * 0.5, last.AsFloat64());
            if (round > 0)
            {
                best = Math.Min(best, allocated);
            }
        }

        return best;
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
    /// The first rents fill the array pool and JIT-compile the path; an allocation per key or per chunk shows up in
    /// every round.
    /// </summary>
    private static long Measure(FeatureTestRig rig, int[] features, int keys, Action<TestRequest>? setup = null)
    {
        var candidates = Enumerable.Range(1, keys).Select(i => (long)i).ToArray();
        var best = long.MaxValue;
        for (var round = 0; round < 4; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var request = rig.NewRequest(candidates);
            setup?.Invoke(request);
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

    private sealed class YieldingConnector : IFeatureSourceConnector
    {
        private readonly SynchronousConnector _answer = new();

        public string ConnectorType => TestConnectorFactory.TypeName;

        public bool Supports(EntityKeyLayout layout) => _answer.Supports(layout);

        public async ValueTask<FetchStatus> FetchAsync(
            EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return await _answer.FetchAsync(keys, fields, writer, cancellationToken);
        }
    }

    private sealed class SynchronousConnector : IFeatureSourceConnector
    {
        public string ConnectorType => TestConnectorFactory.TypeName;

        public bool Supports(EntityKeyLayout layout) => !layout[0].IsShared;

        public ValueTask<FetchStatus> FetchAsync(
            EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
        {
            var span = keys.Part(0).Span;
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
