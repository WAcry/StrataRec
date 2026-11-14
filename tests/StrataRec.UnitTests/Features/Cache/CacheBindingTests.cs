using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Catalog;
using StrataRec.Features.Runtime;
using StrataRec.Redis;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>The checks of a <c>cache</c> binding (design 6.4, 6.6): every problem at its pointer, with a hint.</summary>
public class CacheBindingTests
{
    [Fact]
    public void ACacheBinding_InFrontOfAConnector_WithAnInProcessTierOnly_Builds()
    {
        var rig = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((_, _) => 1.0));
        rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");

        Assert.False(rig.TryBuild().HasErrors);
        var group = Assert.Single(rig.Plan.Groups);
        Assert.Equal(SupplyMode.Cache, group.Supply.Mode);
        Assert.NotNull(group.Supply.Connector);
        Assert.NotNull(group.Supply.Table);
        Assert.Null(group.Supply.Cache!.Distributed);
        Assert.Equal(60_000, rig.Tables["item_store"].Cache!.Options.TtlMs);
    }

    [Fact]
    public void ACacheBinding_WithoutItsRequiredSettings_ReportsEachAtTheBinding()
    {
        var items = Errors(Build("""{ "mode": "cache" }"""));

        Assert.Equal(
            new[] { ("CONNECTOR_UNKNOWN", "/sources/item_store"), ("LOCAL_MAX_BYTES", "/sources/item_store"), ("CACHE_SETTINGS_MISSING", "/sources/item_store") },
            items.Select(d => (d.Code, d.Pointer)));
        Assert.Contains("mode 'cache'", items[1].Message);
        Assert.Contains("\"ttl_ms\": 60000", items[2].Hint);
    }

    [Theory]
    [InlineData("""{ }""", "/sources/item_store/cache", "CACHE_TTL")]
    [InlineData("""{ "ttl_ms": 0 }""", "/sources/item_store/cache/ttl_ms", "CACHE_TTL")]
    [InlineData("""{ "ttl_ms": 1000, "stale_if_error_ms": -1 }""", "/sources/item_store/cache/stale_if_error_ms", "CACHE_STALE_IF_ERROR")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { } }""", "/sources/item_store/cache/distributed", "DISTRIBUTED_CACHE_UNKNOWN")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@2" } }""", "/sources/item_store/cache/distributed/store", "DISTRIBUTED_CACHE_UNKNOWN")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "timeout_ms": 0 } }""", "/sources/item_store/cache/distributed/timeout_ms", "DISTRIBUTED_CACHE_CONFIG")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1" } }""", "/sources/item_store/cache/distributed", "DISTRIBUTED_CACHE_CONFIG")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "pasword": "x" } }""", "/sources/item_store/cache/distributed/pasword", "DISTRIBUTED_CACHE_CONFIG")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "password": "hunter2" } }""", "/sources/item_store/cache/distributed/password", "DISTRIBUTED_CACHE_CONFIG")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "redis.internal" } }""", "/sources/item_store/cache/distributed/endpoint", "DISTRIBUTED_CACHE_CONFIG")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "database": -1 } }""", "/sources/item_store/cache/distributed/database", "DISTRIBUTED_CACHE_CONFIG")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "tls": "yes" } }""", "/sources/item_store/cache/distributed/tls", "DISTRIBUTED_CACHE_CONFIG")]
    public void AWrongCacheSetting_IsAnErrorAtTheSetting_WithAHint(string cache, string pointer, string code)
    {
        var error = Assert.Single(Errors(Build($$"""{ "mode": "cache", "connector": "memory@1", "max_bytes": "1MiB", "cache": {{cache}} }""")));

        Assert.Equal((code, pointer), (error.Code, error.Pointer));
        Assert.False(string.IsNullOrEmpty(error.Hint), error.ToString());
        Assert.DoesNotContain("hunter2", error.ToString()); // a pasted credential is never quoted
    }

    /// <summary>
    /// Review 19: the distributed tier keeps a record for up to ttl_ms × 1.1 + stale_if_error_ms, given to the store as a
    /// <see cref="TimeSpan"/>. Exactly what one holds is accepted and written back with it; a millisecond more is an error
    /// at the setting, not an overflow on the first write-back.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TheLongestRetentionOfADistributedTier_IsWhatATimeToLiveHolds(long pastIt)
    {
        const long Ttl = 1_000;
        var stale = LocalTableCache.MaxRetentionMs - LocalTableCache.MaxLifetime(Ttl) + pastIt;
        var store = new FakeCacheStore();
        var rig = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((_, _) => 1.0), ttlMs: Ttl, staleIfErrorMs: stale, distributed: store);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        if (pastIt > 0)
        {
            var error = Assert.Single(Errors(rig.TryBuild()));
            Assert.Equal(("CACHE_RETENTION", "/sources/item_store/cache/stale_if_error_ms"), (error.Code, error.Pointer));
            Assert.Contains("ttl_ms × 1.1 + stale_if_error_ms is at most", error.Hint);
            return;
        }

        rig.Build();
        Assert.Equal(FeatureState.Ok, (await rig.NewRequest([1]).EnsureAsync(clicks)).State);
        await store.WhenWrittenAsync(1);
        Assert.True(store.TryGet("item_store:item:1", out _, out var timeToLive));
        Assert.Equal(TimeSpan.FromTicks(LocalTableCache.MaxRetentionMs * TimeSpan.TicksPerMillisecond), timeToLive);
    }

    /// <summary>
    /// Review 21 #1: at the longest retention the binding accepts, the clock steps back during the fetch, so the record's
    /// write time is ahead of the clock when it is written back. It counts as written now, as when it is read: the time to
    /// live is the retention, where it used to be a millisecond more, overflowing to a negative one the store rejects.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(86_400_000)]
    public async Task AtTheLongestRetention_ARecordWrittenAheadOfTheClock_IsKeptForTheRetention(long stepBackMs)
    {
        const long Ttl = 1_000;
        var stale = LocalTableCache.MaxRetentionMs - LocalTableCache.MaxLifetime(Ttl);
        var store = new FakeCacheStore();
        FeatureTestRig? rig = null;
        var stepped = 0;
        rig = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((_, _) =>
        {
            if (Interlocked.Exchange(ref stepped, 1) == 0)
            {
                rig!.Clock.AdvanceMs(-stepBackMs); // after the fetch took its write time, before the write-back
            }

            return 1.0;
        }), ttlMs: Ttl, staleIfErrorMs: stale, distributed: store);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();

        Assert.Equal(FeatureState.Ok, (await rig.NewRequest([1]).EnsureAsync(clicks)).State);

        await store.WhenWrittenAsync(1);
        Assert.True(store.TryGet("item_store:item:1", out _, out var timeToLive));
        Assert.Equal(TimeSpan.FromTicks(LocalTableCache.MaxRetentionMs * TimeSpan.TicksPerMillisecond), timeToLive);
    }

    [Fact]
    public void ATtlWhoseLifetimeAloneIsLongerThanATimeToLive_IsAnErrorAtTheTtl_WhenThereIsADistributedTier()
    {
        const long Ttl = 9_007_199_254_740_991; // 2^53 - 1, the most a configuration writes exactly (RFC 8785)
        var withTier = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((_, _) => 1.0), ttlMs: Ttl, distributed: new FakeCacheStore());
        withTier.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var error = Assert.Single(Errors(withTier.TryBuild()));
        Assert.Equal(("CACHE_RETENTION", "/sources/item_store/cache/ttl_ms"), (error.Code, error.Pointer));

        // In process only, nothing converts it: the table judges ages in milliseconds.
        var inProcess = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((_, _) => 1.0), ttlMs: Ttl);
        inProcess.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        Assert.False(inProcess.TryBuild().HasErrors);
    }

    [Fact]
    public void ADistributedStore_ThisBinaryDoesNotHave_NamesWhatWouldProvideIt()
    {
        var error = Assert.Single(Errors(Build("""{ "mode": "cache", "connector": "memory@1", "max_bytes": "1MiB", "cache": { "ttl_ms": 1, "distributed": { "store": "redis@1" } } }""", redis: false)));

        Assert.Equal(("DISTRIBUTED_CACHE_UNKNOWN", "/sources/item_store/cache/distributed/store"), (error.Code, error.Pointer));
        Assert.Contains("StrataRec.Redis", error.Hint);
    }

    [Fact]
    public void SettingsOfOtherModes_AreErrors_AndTheHintNamesEveryModeTheyBelongTo()
    {
        var cache = Errors(Build("""
            { "mode": "cache", "connector": "memory@1", "max_bytes": "1MiB", "cache": { "ttl_ms": 1000 },
              "lookup": "context", "bootstrap": { "source": "snapshot_files@1", "uri": "x" } }
            """));
        Assert.Equal(new[] { "/sources/item_store/lookup", "/sources/item_store/bootstrap" }, cache.Select(d => d.Pointer));
        Assert.All(cache, d => Assert.Equal("SOURCE_SETTING_WRONG_MODE", d.Code));

        var fetch = Assert.Single(Errors(Build("""{ "connector": "memory@1", "cache": { "ttl_ms": 1000 } }""")));
        Assert.Equal("/sources/item_store/cache", fetch.Pointer);
        Assert.Contains("\"mode\": \"cache\"", fetch.Hint);

        var local = Errors(Build("""{ "mode": "local", "max_bytes": "1MiB", "bootstrap": { "source": "test_updates@1" }, "timeout_ms": 5 }"""));
        var timeout = Assert.Single(local);
        Assert.Contains("mode 'fetch' and 'cache'", timeout.Message);
        Assert.Contains("\"mode\": \"fetch\" or \"mode\": \"cache\"", timeout.Hint);
    }

    [Fact]
    public void ACacheBinding_ForACrossEntity_IsAnErrorAtTheMode()
    {
        var cross = new EntityDefinition
        {
            Name = "user_item", Kind = EntityKind.Cross, ContextKeyInput = "user_id", ContextKeyType = EntityKeyType.Int64,
            CandidateKeyColumn = "entity_id", CandidateKeyType = EntityKeyType.Int64,
        };
        var rig = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((_, _) => 1.0));
        rig.Physical("user_item.clicks", cross, ColumnType.Float64, "item_store");

        var error = Assert.Single(Errors(rig.TryBuild()));

        Assert.Equal(("LOCAL_CROSS_ENTITY", "/sources/item_store/mode"), (error.Code, error.Pointer));
        Assert.Contains("mode 'cache'", error.Message);
    }

    private static DiagnosticBag Build(string binding, bool redis = true)
    {
        var rig = new FeatureTestRig().Bind("item_store", ArtifactSerializer.Deserialize<FeatureSourceBinding>(binding));
        rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        if (redis)
        {
            rig.CacheStores.AddRedis();
        }

        return rig.TryBuild();
    }

    private static List<ConfigDiagnostic> Errors(DiagnosticBag diagnostics) => diagnostics.Items.Where(d => d.IsError).ToList();
}
