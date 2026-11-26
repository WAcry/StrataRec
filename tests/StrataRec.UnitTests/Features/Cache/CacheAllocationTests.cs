using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>
/// What a cache read allocates: a read whose keys are all fresh in the table is a local read and
/// allocates nothing at any key count; a read that misses allocates what resolving the misses needs, measured here.
/// </summary>
[Collection(AllocationMeasurement.Name)]
public class CacheAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void AReadWhoseKeysAreAllFresh_AllocatesNothing_AtAnyKeyCount()
    {
        var (rig, features) = Rig(distributed: null);
        WarmTable(rig, features, 4_000);

        var small = MeasureHits(rig, features, 100);
        var large = MeasureHits(rig, features, 4_000);

        output.WriteLine($"all fresh: 100 keys {small} B, 4000 keys {large} B beyond an ensure with nothing to read");
        Assert.True(small <= 0 && large <= 0, $"100 keys: {small} B, 4000 keys: {large} B beyond an ensure with nothing to read");
    }

    [Fact]
    public void AReadThatMisses_AllocatesPerKey_ForWhatGoesToTheWriters()
    {
        var store = new PreparedStore();
        var (rig, features) = Rig(store);
        var fromStore = new[] { Measure(rig, features, 100), Measure(rig, features, 1_000) };
        var (origin, originFeatures) = Rig(distributed: null);
        var fromOrigin = new[] { Measure(origin, originFeatures, 100), Measure(origin, originFeatures, 1_000) };

        output.WriteLine($"L1 miss, distributed hit: 100 keys {fromStore[0]} B, 1000 keys {fromStore[1]} B ({(fromStore[1] - fromStore[0]) / 900.0:F1} B/key)");
        output.WriteLine($"L1 miss, connector: 100 keys {fromOrigin[0]} B, 1000 keys {fromOrigin[1]} B ({(fromOrigin[1] - fromOrigin[0]) / 900.0:F1} B/key)");

        // The write-back is the per-key cost: a key and a record reference for the table's writer (32 B), and from the
        // connector the encoded record (33 B here; the distributed tier, when there is one, adds its entry). With a
        // distributed tier, each miss's key text too (32 B for these int64 keys; formatted once, for its read and its
        // write-back, where a store used to format it itself). Bounded, so a regression that allocates per field or per row shows.
        Assert.True((fromStore[1] - fromStore[0]) / 900.0 < 96, $"{fromStore[0]} B, {fromStore[1]} B");
        Assert.True((fromOrigin[1] - fromOrigin[0]) / 900.0 < 96, $"{fromOrigin[0]} B, {fromOrigin[1]} B");
    }

    /// <summary>
    /// A record holds every field of its source; a read fills the ones its node reads. A list of strings nobody reads is
    /// walked, not decoded: decoding it cost an array and a string per tag for every record (audit of f47a255).
    /// </summary>
    [Fact]
    public void AStringListNobodyReads_IsNotDecoded()
    {
        const int Keys = 1_000, Tags = 24;
        var store = new RecordStore();
        var rig = new FeatureTestRig().CacheSource("item_store", new SynchronousConnector(), distributed: store);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var tags = rig.Physical("item.tags", FeatureTestRig.Item, ColumnType.List(StorageKind.String), "item_store");
        rig.Build();
        var cache = rig.Tables["item_store"].Cache!;
        var writer = new CacheRecordWriter(cache.Layout, 2 * 1024 * 1024);
        var spans = new (int Offset, int Length)[Keys + 1];
        for (var key = 1; key <= Keys; key++)
        {
            writer.Begin(rig.Clock.NowMs, cache.Fingerprint, absent: false);
            writer.Float64(0, key * 0.5);
            writer.StringList(1, Enumerable.Range(0, Tags).Select(t => $"tag-{key:0000}-{t:00}").ToArray());
            spans[key] = writer.End();
        }

        store.Records = spans.Select(span => (ReadOnlyMemory<byte>)writer.Bytes.AsMemory(span.Offset, span.Length)).ToArray();

        var number = Measure(rig, [score], Keys);
        output.WriteLine($"1000 records with {Tags} tags each: {number} B reading the number alone");

        // What reading the tags too costs is some 1.9 MB; the number alone stays what a record without a list costs.
        Assert.True(number < 200_000, $"{number} B");

        // And a read of the list still gets it.
        var request = rig.NewRequest([7L]);
        Assert.True(request.Fetcher.EnsureAsync([tags], CancellationToken.None).IsCompletedSuccessfully);
        Assert.Equal("tag-0007-23", request.Value(tags, 0).AsStringList()[Tags - 1]);
        request.Memory.Release();
    }

    /// <summary>A distributed tier holding prepared records by int64 key.</summary>
    private sealed class RecordStore : ICacheStore
    {
        public string StoreType => TestCacheStoreFactory.TypeName;

        public ReadOnlyMemory<byte>[] Records { get; set; } = [];

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                records.Span[i] = Records[int.Parse(keys.Span[i], System.Globalization.CultureInfo.InvariantCulture)];
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static (FeatureTestRig Rig, int[] Features) Rig(ICacheStore? distributed)
    {
        var rig = new FeatureTestRig().CacheSource("item_store", new SynchronousConnector(), distributed: distributed);
        var features = new[]
        {
            rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store"),
            rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0)),
        };
        rig.Build();
        if (distributed is PreparedStore store)
        {
            store.Prepare(rig.Tables["item_store"].Cache!, 1_000, rig.Clock.NowMs);
        }

        return (rig, features);
    }

    private static void WarmTable(FeatureTestRig rig, int[] features, int keys)
    {
        var request = rig.NewRequest(Enumerable.Range(1, keys).Select(i => (long)i).ToArray());
        Assert.True(request.Fetcher.EnsureAsync(features, CancellationToken.None).IsCompletedSuccessfully);
        request.Memory.Release();
        rig.WriteBack("item_store");
    }

    /// <summary>Bytes the first ensure allocates beyond a second one with nothing to read: best of three after a warm-up.</summary>
    private static long MeasureHits(FeatureTestRig rig, int[] features, int keys)
    {
        var candidates = Enumerable.Range(1, keys).Select(i => (long)i).ToArray();
        var best = long.MaxValue;
        for (var round = 0; round < 4; round++)
        {
            var request = rig.NewRequest(candidates);
            foreach (var feature in features)
            {
                request.Table.GetOrCreateSlot(rig.SlotOf(feature));
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var read = request.Fetcher.EnsureAsync(features, CancellationToken.None);
            var readBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            var again = request.Fetcher.EnsureAsync(features, CancellationToken.None);
            var againBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(read.IsCompletedSuccessfully && again.IsCompletedSuccessfully);
            Assert.Equal(FeatureState.Ok, read.Result.State);
            Assert.Equal(keys * 0.5, request.Value(features[0], keys - 1).AsFloat64());
            request.Memory.Release();
            if (round > 0)
            {
                best = Math.Min(best, readBytes - againBytes);
            }
        }

        return best;
    }

    /// <summary>
    /// Bytes of a whole request whose keys all miss the table: best of three after a warm-up. The table's writer does not
    /// run, so what the requests hand it waits in its queue and the table stays empty.
    /// </summary>
    private static long Measure(FeatureTestRig rig, int[] features, int keys)
    {
        var candidates = Enumerable.Range(1, keys).Select(i => (long)i).ToArray();
        var best = long.MaxValue;
        for (var round = 0; round < 4; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var request = rig.NewRequest(candidates);
            var read = request.Fetcher.EnsureAsync(features, CancellationToken.None);
            var completed = read.IsCompletedSuccessfully; // the store and the connector answer synchronously
            var last = completed ? request.Value(features[0], keys - 1).AsFloat64() : double.NaN;
            request.Memory.Release();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(completed);
            Assert.Equal(keys * 0.5, last);
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
        public string ConnectorType => TestConnectorFactory.TypeName;

        public bool Supports(EntityKeyLayout layout) => layout.PartCount == 1 && !layout[0].IsShared;

        public ValueTask<FetchStatus> FetchAsync(
            EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
        {
            var span = keys.Keys.Span;
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

    /// <summary>A distributed tier that has a fresh record of keys 1..n, answers at once and allocates nothing.</summary>
    private sealed class PreparedStore : ICacheStore
    {
        private ReadOnlyMemory<byte>[] _records = [];

        public string StoreType => TestCacheStoreFactory.TypeName;

        public void Prepare(LocalTableCache cache, int keys, long writtenMs)
        {
            var writer = new CacheRecordWriter(cache.Layout, keys * 40);
            var spans = new (int Offset, int Length)[keys + 1];
            for (var key = 1; key <= keys; key++)
            {
                writer.Begin(writtenMs, cache.Fingerprint, absent: false);
                writer.Float32(0, key);
                writer.Float64(1, key * 0.5);
                spans[key] = writer.End();
            }

            _records = spans.Select(s => (ReadOnlyMemory<byte>)writer.Bytes.AsMemory(s.Offset, s.Length)).ToArray();
        }

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            var span = keys.Span;
            var into = records.Span;
            for (var i = 0; i < span.Length; i++)
            {
                into[i] = _records[int.Parse(span[i], System.Globalization.CultureInfo.InvariantCulture)]; // the key's text: its decimal
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
