using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Catalog;
using StrataRec.Features.Runtime;
using StrataRec.Redis;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>The checks of a <c>cache</c> binding: every problem at its pointer, with a hint.</summary>
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
        var items = Errors(Build("""{ "cache": { } }"""));

        Assert.Equal(
            new[] { ("BINDING_TYPE_UNKNOWN", "/sources/item_store"), ("CACHE_TTL", "/sources/item_store/cache"), ("CACHE_TIERS", "/sources/item_store/cache") },
            items.Select(d => (d.Code, d.Pointer)));
        Assert.Contains("\"ttl_ms\": 60000", items[1].Hint);
        Assert.Contains("\"memory\": { \"max_bytes\": \"64MiB\" }", items[2].Hint);
    }

    [Fact]
    public void ModeCache_IsNotAMode_AndTheHintSaysWhereACacheGoes()
    {
        var error = Assert.Single(Errors(Build("""{ "mode": "cache", "connector": "memory@1" }""")));

        Assert.Equal(("SOURCE_MODE_UNSUPPORTED", "/sources/item_store/mode"), (error.Code, error.Pointer));
        Assert.Contains("remove \"mode\" and move the cache's settings into a \"cache\" member, \"max_bytes\" into its \"memory\"", error.Hint);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task EitherTierAlone_OrBoth_IsACache(bool memory, bool distributed)
    {
        // A cache with only a distributed tier keeps nothing in process: a second read goes to the store again, where
        // one with a memory tier is answered by its table.
        var store = new FakeCacheStore();
        var connector = ScriptedConnector.Values((_, _) => 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector, distributed: distributed ? store : null, maxBytes: memory ? "64MiB" : null);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        Assert.Equal(memory, rig.Tables["item_store"].Cache!.Memory);

        for (var read = 0; read < 2; read++)
        {
            var request = rig.NewRequest([1]);
            Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(clicks)).State);
            Assert.Equal(1.0, request.Value(clicks, 0).AsFloat64());
            rig.WriteBack("item_store");
            if (distributed)
            {
                await store.WhenWrittenAsync(1);
            }
        }

        Assert.Single(connector.Calls); // a tier answered the second read
        Assert.Equal(memory ? 1 : 0, rig.Tables["item_store"].Pin().RecordCount);
        Assert.Equal(!distributed ? 0 : memory ? 1 : 2, store.Gets.Count);
    }

    [Theory]
    [InlineData("""{ "memory": { "max_bytes": "1MiB" } }""", "/sources/item_store/cache", "CACHE_TTL")]
    [InlineData("""{ "ttl_ms": 0, "memory": { "max_bytes": "1MiB" } }""", "/sources/item_store/cache/ttl_ms", "CACHE_TTL")]
    [InlineData("""{ "ttl_ms": 1000, "stale_if_error_ms": -1, "memory": { "max_bytes": "1MiB" } }""", "/sources/item_store/cache/stale_if_error_ms", "CACHE_STALE_IF_ERROR")]
    [InlineData("""{ "ttl_ms": 1000 }""", "/sources/item_store/cache", "CACHE_TIERS")]
    [InlineData("""{ "ttl_ms": 1000, "memory": { } }""", "/sources/item_store/cache/memory", "CACHE_MAX_BYTES")]
    [InlineData("""{ "ttl_ms": 1000, "memory": { "max_bytes": "0B" } }""", "/sources/item_store/cache/memory/max_bytes", "CACHE_MAX_BYTES")]
    [InlineData("""{ "ttl_ms": 1000, "memory": { "max_bytes": "1B" } }""", "/sources/item_store/cache/memory/max_bytes", "CACHE_MAX_BYTES")]
    [InlineData("""{ "ttl_ms": 1000, "version": "", "memory": { "max_bytes": "1MiB" } }""", "/sources/item_store/cache/version", "CACHE_VERSION")]
    [InlineData("""{ "ttl_ms": 1000, "version": "a:b", "memory": { "max_bytes": "1MiB" } }""", "/sources/item_store/cache/version", "CACHE_VERSION")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { } }""", "/sources/item_store/cache/distributed", "BINDING_TYPE_UNKNOWN")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@2" } }""", "/sources/item_store/cache/distributed/store", "BINDING_TYPE_UNKNOWN")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "timeout_ms": 0 } }""", "/sources/item_store/cache/distributed/timeout_ms", "DISTRIBUTED_CACHE_CONFIG")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1" } }""", "/sources/item_store/cache/distributed", "BINDING_SETTINGS")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "pasword": "x" } }""", "/sources/item_store/cache/distributed/pasword", "BINDING_SETTINGS")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "password": "hunter2" } }""", "/sources/item_store/cache/distributed/password", "BINDING_SETTINGS")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "redis.internal" } }""", "/sources/item_store/cache/distributed/endpoint", "BINDING_SETTINGS")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "database": -1 } }""", "/sources/item_store/cache/distributed/database", "BINDING_SETTINGS")]
    [InlineData("""{ "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379", "tls": "yes" } }""", "/sources/item_store/cache/distributed/tls", "BINDING_SETTINGS")]
    public void AWrongCacheSetting_IsAnErrorAtTheSetting_WithAHint(string cache, string pointer, string code)
    {
        var error = Assert.Single(Errors(Build($$"""{ "connector": "memory@1", "cache": {{cache}} }""")));

        Assert.Equal((code, pointer), (error.Code, error.Pointer));
        Assert.False(string.IsNullOrEmpty(error.Hint), error.ToString());
        Assert.DoesNotContain("hunter2", error.ToString()); // a pasted credential is never quoted
    }

    /// <summary>
    /// The distributed tier keeps a record for up to ttl_ms × 1.1 + stale_if_error_ms, given to the store as a
    /// <see cref="TimeSpan"/>. Exactly what one holds is accepted and written back with it; a millisecond more is an error
    /// at the setting, not an overflow on the first write-back.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TheLongestRetentionOfADistributedTier_IsWhatATimeToLiveHolds(long pastIt)
    {
        const long Ttl = 1_000;
        var stale = CachePolicy.MaxRetentionMs - CachePolicy.MaxLifetime(Ttl) + pastIt;
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
        Assert.True(store.TryGet(rig.Tables["item_store"].Cache!.Keyspace.Prefix + "1", out _, out var timeToLive));
        Assert.Equal(TimeSpan.FromTicks(CachePolicy.MaxRetentionMs * TimeSpan.TicksPerMillisecond), timeToLive);
    }

    /// <summary>
    /// At the longest retention the binding accepts, the clock steps back during the fetch, so the record's
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
        var stale = CachePolicy.MaxRetentionMs - CachePolicy.MaxLifetime(Ttl);
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
        Assert.True(store.TryGet(rig.Tables["item_store"].Cache!.Keyspace.Prefix + "1", out _, out var timeToLive));
        Assert.Equal(TimeSpan.FromTicks(CachePolicy.MaxRetentionMs * TimeSpan.TicksPerMillisecond), timeToLive);
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
        var error = Assert.Single(Errors(Build("""{ "connector": "memory@1", "cache": { "ttl_ms": 1, "distributed": { "store": "redis@1" } } }""", redis: false)));

        Assert.Equal(("BINDING_TYPE_UNKNOWN", "/sources/item_store/cache/distributed/store"), (error.Code, error.Pointer));
        Assert.Contains("StrataRec.Redis", error.Hint);
    }

    [Fact]
    public void SettingsOfOtherModes_AreErrors_AndTheHintNamesEveryModeTheyBelongTo()
    {
        var cache = Errors(Build("""
            { "connector": "memory@1", "cache": { "ttl_ms": 1000, "memory": { "max_bytes": "1MiB" } }, "max_bytes": "1MiB",
              "bootstrap": { "source": "snapshot_files@1", "uri": "x" } }
            """));
        Assert.Equal(new[] { "/sources/item_store/max_bytes", "/sources/item_store/bootstrap" }, cache.Select(d => d.Pointer));
        Assert.All(cache, d => Assert.Equal("SOURCE_SETTING_WRONG_MODE", d.Code));
        Assert.Contains("mode 'local'", cache[0].Message);

        var local = Errors(Build("""
            { "mode": "local", "max_bytes": "1MiB", "bootstrap": { "source": "test_updates@1" }, "timeout_ms": 5,
              "cache": { "ttl_ms": 1000, "memory": { "max_bytes": "1MiB" } } }
            """));
        Assert.Equal(new[] { "/sources/item_store/timeout_ms", "/sources/item_store/cache" }, local.Select(d => d.Pointer));
        Assert.Contains("mode 'fetch' and 'service'", local[0].Message);
        Assert.Contains("\"mode\": \"fetch\" or \"mode\": \"service\"", local[0].Hint);
        Assert.Contains("a setting of mode 'fetch',", local[1].Message); // once: a fetch binding with a cache is a fetch binding
    }

    [Fact]
    public void ACacheBinding_ForAnEntityOfSeveralKeyParts_HasATableOfThatLayout()
    {
        var userItem = EntityDefinition.Of(
            "user_item", EntityKeyPartDefinition.RequestInput("user_id", EntityKeyType.Int64), EntityKeyPartDefinition.EntityId(EntityKeyType.Int64));
        var rig = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((_, _) => 1.0));
        rig.Physical("user_item.clicks", userItem, ColumnType.Float64, "item_store");

        Assert.Empty(Errors(rig.TryBuild()));

        Assert.Equal("(int64,int64)", rig.Tables["item_store"].Schema.Key.TypeText);
        Assert.Equal("(int64,int64)", rig.Tables["item_store"].Cache!.Keyspace.Key.TypeText);
    }

    private static DiagnosticBag Build(string binding, bool redis = true)
    {
        var rig = new FeatureTestRig().Bind("item_store", ArtifactSerializer.Deserialize<FeatureSourceBinding>(binding));
        rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        if (redis)
        {
            rig.Bindings.AddRedis();
        }

        return rig.TryBuild();
    }

    private static List<ConfigDiagnostic> Errors(DiagnosticBag diagnostics) => diagnostics.Items.Where(d => d.IsError).ToList();

    /// <summary>The longest time a cache setting may give is one a policy takes: 2^61 ms was accepted by the check and thrown by the policy.</summary>
    [Fact]
    public void TheLongestTimeTheSettingsAccept_IsOneAPolicyTakes()
    {
        var longest = new CachePolicy(CacheSettings.MaxMs, CacheSettings.MaxMs);

        Assert.Equal((CacheSettings.MaxMs, CacheSettings.MaxMs), (longest.TtlMs, longest.StaleIfErrorMs));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CachePolicy(CacheSettings.MaxMs + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CachePolicy(1, CacheSettings.MaxMs + 1));
    }
}
