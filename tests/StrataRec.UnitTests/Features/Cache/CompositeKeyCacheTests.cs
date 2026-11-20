using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>
/// Cache sources of entities keyed by several parts (design 6.6 <c>cache</c>): the in-process table keyed by the tuple,
/// the distributed tier by the key's text, records fingerprinted with the layout, and eviction as for one-part keys.
/// </summary>
[Collection(MetricMeasurement.Name)]
public class CompositeKeyCacheTests
{
    private static readonly EntityDefinition ItemCountry = EntityDefinition.Of(
        "item_country", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.RequestInput("country", EntityKeyType.String));

    private static readonly EntityKeyLayout Layout = ItemCountry.KeyLayout;

    /// <summary>boost = item × 10 + the country's length.</summary>
    private static ScriptedConnector Boosts() => new((keys, fields, writer, _) =>
    {
        for (var k = 0; k < keys.Count; k++)
        {
            ScriptedConnector.Write(writer, fields[0], k, (keys.Key(k, 0).Int64Value * 10.0) + keys.Key(k, 1).StringValue!.Length);
        }

        return ValueTask.FromResult(FetchStatus.Ok);
    });

    private static (FeatureTestRig Rig, ScriptedConnector Connector, int Country, int Boost) Rig(FakeCacheStore? store = null)
    {
        var connector = Boosts();
        var rig = new FeatureTestRig().CacheSource("ic_store", connector, distributed: store);
        var country = rig.RequestInput("country");
        var boost = rig.Physical("item_country.boost", ItemCountry, ColumnType.Float64, "ic_store");
        rig.Build();
        return (rig, connector, country, boost);
    }

    private static async Task<double[]> Read(FeatureTestRig rig, int country, int boost, string code, params long[] items)
    {
        var request = rig.NewRequest(items);
        request.Context[country] = ScalarValue.FromString(code);
        Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(boost)).State);
        return [.. Enumerable.Range(0, items.Length).Select(row => request.Value(boost, row).AsFloat64())];
    }

    [Fact]
    public async Task TheTable_IsKeyedByTheTuple_SoAnotherValueOfTheRequestPartIsAMiss()
    {
        var (rig, connector, country, boost) = Rig();

        Assert.Equal(new[] { 12.0, 22 }, await Read(rig, country, boost, "US", 1, 2));
        Assert.Equal(1, rig.WriteBack("ic_store"));
        Assert.Equal(new[] { 22.0, 12, 32 }, await Read(rig, country, boost, "US", 2, 1, 3)); // 3 misses
        Assert.Equal(new[] { 13.0 }, await Read(rig, country, boost, "FRA", 1)); // another country: a miss

        var calls = connector.Calls.ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Equal(new EntityKey[][] { [EntityKey.FromInt64(3), EntityKey.FromString("US")] }, calls[1].Tuples);
        Assert.Equal(new EntityKey[][] { [EntityKey.FromInt64(1), EntityKey.FromString("FRA")] }, calls[2].Tuples);
        rig.WriteBack("ic_store");
        var version = rig.Tables["ic_store"].Pin();
        Assert.Equal(Layout, rig.Tables["ic_store"].Schema.Key);
        Assert.True(version.Find([EntityKey.FromInt64(1), EntityKey.FromString("US")]).Found);
        Assert.True(version.Find([EntityKey.FromInt64(1), EntityKey.FromString("FRA")]).Found);
        Assert.False(version.Find([EntityKey.FromInt64(2), EntityKey.FromString("FRA")]).Found);
    }

    [Fact]
    public async Task TheDistributedTier_SeesEachKeyAsItsText_AndAnotherInstanceIsServedFromIt()
    {
        var store = new FakeCacheStore();
        var (writer, writerConnector, writerCountry, writerBoost) = Rig(store);
        await Read(writer, writerCountry, writerBoost, "U:S%", 1, 2);
        await store.WhenWrittenAsync(2);
        Assert.Equal(new[] { "ic_store:item_country:1:U%3AS%25", "ic_store:item_country:2:U%3AS%25" }, store.Gets.Single());
        Assert.True(store.TryGet("ic_store:item_country:2:U%3AS%25", out _, out _));

        var (reader, readerConnector, readerCountry, readerBoost) = Rig(store); // another instance: an empty table
        Assert.Equal(new[] { 24.0, 14, 34 }, await Read(reader, readerCountry, readerBoost, "U:S%", 2, 1, 3));

        Assert.Equal(new EntityKey[][] { [EntityKey.FromInt64(3), EntityKey.FromString("U:S%")] }, Assert.Single(readerConnector.Calls).Tuples);
        Assert.Single(writerConnector.Calls);
    }

    /// <summary>
    /// Review 78: 4,096 candidates whose keys share a 128-char request input, fetched in chunks of 512, write back one
    /// batch, which holds that string once. It was charged once per key, about 1.44 MiB, and the table's empty 1 MiB queue
    /// (a sixteenth of 16 MiB) dropped the whole batch: nothing was cached, whatever the table's room.
    /// </summary>
    [Fact]
    public async Task AWriteBack_WhoseKeysShareARequestInput_IsChargedItOnce_AndWritten()
    {
        const int Count = 4096;
        var connector = Boosts();
        var rig = new FeatureTestRig().CacheSource("ic_store", connector, maxBytes: "16MiB", maxBatch: 512);
        var country = rig.RequestInput("country");
        var boost = rig.Physical("item_country.boost", ItemCountry, ColumnType.Float64, "ic_store");
        rig.Build();
        var (table, cache) = (rig.Tables["ic_store"], rig.Tables["ic_store"].Cache!);
        var code = new string('l', 128);

        var request = rig.NewRequest([.. Enumerable.Range(1, Count).Select(i => (long)i)]);
        request.Context[country] = ScalarValue.FromString(code);
        Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(boost)).State);

        Assert.Equal(8, connector.Calls.Count);
        Assert.InRange(cache.QueuedBytes, 1, 512 << 10); // ~0.32 MiB: the keys' slots, the records and the string once
        Assert.Equal(1, cache.WriteQueued(table.Writer));
        Assert.Equal(Count, table.Pin().RecordCount);
        request.Memory.Release();
    }

    [Theory]
    [InlineData(new object[] { 10042L }, "10042")]
    [InlineData(new object[] { -7L }, "-7")]
    [InlineData(new object[] { "a:b%c" }, "a:b%c")] // one part: as is, as before keys had parts
    [InlineData(new object[] { 10042L, "US" }, "10042:US")]
    [InlineData(new object[] { "a:b", 1L }, "a%3Ab:1")]
    [InlineData(new object[] { "a%3Ab", 1L }, "a%253Ab:1")]
    [InlineData(new object[] { "", "", 0L }, "::0")]
    public void AKeysText_JoinsItsPartsByColons_EscapingThemInAStringPart(object[] parts, string text)
    {
        var key = parts.Select(p => p is string s ? EntityKey.FromString(s) : EntityKey.FromInt64((long)p)).ToArray();

        Assert.Equal(text, CacheKeyspace.KeyText(key));
    }

    [Fact]
    public void KeysOfOneLayout_HaveDistinctTexts_WhateverTheirStringsHold()
    {
        var strings = new[] { "", ":", "%", "%3A", "a:", ":a", "a", "%25", "a%3A" };
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in strings)
        {
            foreach (var b in strings)
            {
                Assert.True(texts.Add(CacheKeyspace.KeyText([EntityKey.FromString(a), EntityKey.FromString(b)])), $"({a}, {b})");
            }
        }
    }

    [Fact]
    public void TheFingerprint_CoversTheKeyLayout_ButATablesOfOnePartIsAsBefore()
    {
        LocalTableField[] fields = [new("boost", ColumnType.Float64)];
        ulong Of(EntityKeyLayout layout) => CacheRecord.Fingerprint(new LocalTableSchema("s", "e", layout, LocalTableMode.Cache, fields));

        var fingerprints = new[]
        {
            Of(EntityKeyLayout.OfTypes(EntityKeyType.Int64)),
            Of(EntityKeyLayout.OfTypes(EntityKeyType.String)),
            Of(EntityKeyLayout.OfTypes(EntityKeyType.Int64, EntityKeyType.String)),
            Of(EntityKeyLayout.OfTypes(EntityKeyType.String, EntityKeyType.Int64)),
            Of(EntityKeyLayout.OfTypes(EntityKeyType.Int64, EntityKeyType.String, EntityKeyType.String)),
        };

        Assert.Equal(fingerprints.Length, fingerprints.Distinct().Count());
        Assert.Equal(fingerprints[2], Of(Layout)); // the references do not count, only the stored types
        Assert.Equal(fingerprints[0], CacheRecord.Fingerprint(new LocalTableSchema("s", "e", EntityKeyType.Int64, LocalTableMode.Cache, fields)));
    }

    [Fact]
    public void Eviction_OfTupleKeys_DropsTheOldestWritten_AndTheTableStaysWithinMaxBytes()
    {
        const long MaxBytes = 128 << 10;
        var clock = new ManualClock();
        var schema = new LocalTableSchema("s", "item_country", Layout, LocalTableMode.Cache, [new("name", ColumnType.String)]);
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions
        {
            MaxBytes = MaxBytes,
            PageSize = 16 << 10,
            CompactionMinBytes = 0,
            Cache = new CacheTableOptions { TtlMs = 600_000, Clock = clock },
        });
        var cache = table.Cache!;
        var codec = new CacheRecordWriter(cache.Layout, 64);
        long item;
        for (item = 0; item < 200; item++)
        {
            // About 2 KiB of string per record, distinct per key; two countries per item.
            var keys = new[] { EntityKey.FromInt64(item), EntityKey.FromString("US"), EntityKey.FromInt64(item), EntityKey.FromString("DE") };
            var spans = new ReadOnlyMemory<byte>[2];
            for (var k = 0; k < 2; k++)
            {
                codec.Begin(clock.NowMs, cache.Fingerprint, absent: false);
                codec.String(0, item + "-" + k + new string('n', 1_000));
                var (offset, length) = codec.End();
                spans[k] = codec.Bytes.AsMemory(offset, length).ToArray();
            }

            Assert.True(cache.TryWriteBack(new CacheWriteBatch(keys, spans, 2, partCount: 2)));
            cache.WriteQueued(table.Writer);
            clock.AdvanceMs(1);
            Assert.True(table.Pin().Bytes <= MaxBytes);
        }

        var version = table.Pin();
        Assert.True(version.RecordCount < 400);
        Assert.False(version.Find([EntityKey.FromInt64(0), EntityKey.FromString("US")]).Found); // the oldest
        Assert.True(version.Find([EntityKey.FromInt64(199), EntityKey.FromString("DE")]).Found); // the newest
        Assert.Equal("199-1" + new string('n', 1_000), version.Read(0, version.Find([EntityKey.FromInt64(199), EntityKey.FromString("DE")])).AsString());
    }
}
