using System.Collections.Concurrent;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>The request read path of <c>cache</c> sources (design 6.6 <c>cache</c>): tiers, expiry, failures, write-back.</summary>
public class CacheReadTests
{
    private static readonly long[] Three = [1, 2, 3];

    [Fact]
    public async Task AMiss_IsFetched_WrittenBack_ThenServedFromTheTable_WithoutTheConnector()
    {
        var connector = Clicks();
        var rig = new FeatureTestRig().CacheSource("item_store", connector);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();

        var first = rig.NewRequest(Three);
        var missed = await first.EnsureAsync(clicks);
        Assert.Equal(FeatureState.Ok, missed.State);
        Assert.Equal(new[] { 1.0, 2, 3 }, Values(first, clicks, 3));
        Assert.Equal(new long[] { 1, 2, 3 }, Assert.Single(connector.Calls).Keys.Select(k => k.Int64Value));
        Assert.Equal(1, rig.WriteBack("item_store"));

        var second = rig.NewRequest([3, 2, 4]);
        var hit = await second.EnsureAsync(clicks);

        Assert.Equal(FeatureState.Ok, hit.State);
        Assert.Equal(new[] { 3.0, 2, 4 }, Values(second, clicks, 3));
        Assert.Equal(new long[] { 4 }, connector.Calls.Last().Keys.Select(k => k.Int64Value)); // only the miss
        Assert.Equal(2, connector.Calls.Count);
    }

    [Fact]
    public async Task AMiss_FetchesEveryFieldOfTheTable_SoALaterReadOfAnotherFeatureHits()
    {
        var connector = ScriptedConnector.Values((key, field) => field == "clicks" ? key * 1.0 : key * 10.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var age = rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();

        await rig.NewRequest(Three).EnsureAsync(clicks);
        Assert.Equal(new[] { "age", "clicks" }, Assert.Single(connector.Calls).Fields); // the whole record, not just clicks
        rig.WriteBack("item_store");
        var request = rig.NewRequest(Three);
        await request.EnsureAsync(age);

        Assert.Single(connector.Calls);
        Assert.Equal(new[] { 10.0, 20, 30 }, Values(request, age, 3));
    }

    [Fact]
    public async Task ARecord_IsServedForItsLifetime_TheTtlWithinTenPercentByKey_ThenFetchedAgain()
    {
        const int Keys = 400;
        var connector = Clicks();
        var rig = new FeatureTestRig().CacheSource("item_store", connector, ttlMs: 10_000);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var keys = Enumerable.Range(1, Keys).Select(i => (long)i).ToArray();
        await rig.NewRequest(keys).EnsureAsync(clicks);
        rig.WriteBack("item_store");

        rig.Clock.AdvanceMs(9_000); // within every key's lifetime (at least 9 s)
        await rig.NewRequest(keys).EnsureAsync(clicks);
        Assert.Single(connector.Calls);

        rig.Clock.AdvanceMs(1_000); // 10 s: about half the keys have expired
        await rig.NewRequest(keys).EnsureAsync(clicks);
        var refetched = connector.Calls.Last().Keys.Select(k => k.Int64Value).ToArray();
        Assert.InRange(refetched.Length, Keys / 4, Keys * 3 / 4);
        var cache = rig.Tables["item_store"].Cache!;
        Assert.All(refetched, key => Assert.True(cache.LifetimeMs(EntityKey.FromInt64(key)) < 10_000));
        Assert.All(keys.Except(refetched), key => Assert.True(cache.LifetimeMs(EntityKey.FromInt64(key)) >= 10_000));
        rig.WriteBack("item_store");

        rig.Clock.AdvanceMs(1_001); // past the longest lifetime: every key that was not refetched just now
        await rig.NewRequest(keys).EnsureAsync(clicks);
        Assert.Equal(keys.Except(refetched), connector.Calls.Last().Keys.Select(k => k.Int64Value));
    }

    [Fact]
    public void TheLifetimeOfAKey_IsTheTtlWithinTenPercent_TheSameForTheSameKey_AndSpread()
    {
        var rig = new FeatureTestRig().CacheSource("item_store", Clicks(), ttlMs: 60_000);
        rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var cache = rig.Tables["item_store"].Cache!;

        var lifetimes = Enumerable.Range(1, 10_000).Select(i => cache.LifetimeMs(EntityKey.FromInt64(i))).ToArray();

        Assert.All(lifetimes, l => Assert.InRange(l, 54_000, 66_000));
        Assert.True(lifetimes.Min() < 54_600 && lifetimes.Max() > 65_400, $"{lifetimes.Min()}..{lifetimes.Max()}");
        Assert.InRange(lifetimes.Count(l => l < 60_000), 4_500, 5_500);
        Assert.Equal(cache.LifetimeMs(EntityKey.FromInt64(42)), cache.LifetimeMs(EntityKey.FromInt64(42)));
        Assert.Equal(lifetimes[41], cache.LifetimeMs(EntityKey.FromInt64(42)));
    }

    [Fact]
    public async Task AKeyTheConnectorHasNothingFor_IsCachedAsAbsent()
    {
        var connector = ScriptedConnector.Values((key, _) => key == 2 ? null : key * 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store", ScalarValue.FromFloat64(-1));
        rig.Build();
        await rig.NewRequest(Three).EnsureAsync(clicks);
        rig.WriteBack("item_store");

        var request = rig.NewRequest([2]);
        var result = await request.EnsureAsync(clicks);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Single(connector.Calls); // negative caching: key 2 is not asked again
        Assert.Equal(-1.0, request.Value(clicks, 0).AsFloat64());
        var version = rig.Tables["item_store"].Pin();
        var record = version.Find(2L);
        Assert.True(record.Found);
        Assert.False(version.HasValue(0, record));
    }

    [Fact]
    public async Task WhenTheConnectorFails_AnExpiredRecordWithinStaleIfError_IsServed_AndTheFeaturesAreDegraded()
    {
        var fail = false;
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            if (fail)
            {
                return ValueTask.FromResult(FetchStatus.Failed("item_store is down"));
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 1.0);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().CacheSource("item_store", connector, ttlMs: 1_000, staleIfErrorMs: 10_000);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store", ScalarValue.FromFloat64(-1));
        rig.Build();
        await rig.NewRequest([1, 2]).EnsureAsync(clicks);
        rig.WriteBack("item_store");
        fail = true;

        rig.Clock.AdvanceMs(5_000); // expired, within the stale window
        var stale = rig.NewRequest([1, 2, 3]); // 3 was never fetched
        var degraded = await stale.EnsureAsync(clicks);

        Assert.Equal(FeatureState.Degraded, degraded.State);
        Assert.Contains("item_store is down", degraded.Error);
        Assert.Contains("2 key(s) served stale", degraded.Error);
        Assert.Equal(new[] { 1.0, 2, -1 }, Values(stale, clicks, 3));

        rig.Clock.AdvanceMs(10_000); // past the window too
        var expired = rig.NewRequest([1, 2]);
        var failed = await expired.EnsureAsync(clicks);
        Assert.Equal(FeatureState.Failed, failed.State); // no key got a value
        Assert.Equal("item_store is down", failed.Error);
    }

    [Fact]
    public async Task AFetchThatStartedEarlier_AndAnswersLater_DoesNotReplaceWhatALaterFetchWrote()
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            var value = Interlocked.Increment(ref calls) == 1 ? 1.0 : 2.0;
            if (value == 1.0)
            {
                await slow.Task;
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (_, _) => value);
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().CacheSource("item_store", connector);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();

        var early = rig.NewRequest([7]).EnsureAsync(clicks).AsTask(); // waits for the slow call
        rig.Clock.AdvanceMs(10);
        await rig.NewRequest([7]).EnsureAsync(clicks); // starts later, answers first
        Assert.Equal(1, rig.WriteBack("item_store"));
        slow.SetResult();
        await early;
        Assert.Equal(1, rig.WriteBack("item_store")); // applied, and ignored: its version is older

        var version = rig.Tables["item_store"].Pin();
        var record = version.Find(7L);
        Assert.Equal(2.0, version.Read(0, record).AsFloat64());
        Assert.Equal(rig.Clock.NowMs, version.VersionOf(record));
    }

    [Fact]
    public async Task TheDistributedTier_AnswersAnotherInstancesMisses_AndItsRecordsAreWrittenToTheTable()
    {
        var store = new FakeCacheStore();
        var (writer, writerConnector, writerClicks) = DistributedRig(store);
        await writer.NewRequest(Three).EnsureAsync(writerClicks);
        await store.WhenWrittenAsync(3);
        Assert.True(store.TryGet("item_store:item:2", out _, out var timeToLive));
        var cache = writer.Tables["item_store"].Cache!;
        Assert.Equal(TimeSpan.FromMilliseconds(cache.MaxLifetimeMs + cache.Options.StaleIfErrorMs), timeToLive);

        var (reader, readerConnector, readerClicks) = DistributedRig(store); // another instance: an empty table
        var request = reader.NewRequest([3, 1, 4]);
        var result = await request.EnsureAsync(readerClicks);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new[] { 3.0, 1, 4 }, Values(request, readerClicks, 3));
        Assert.Equal(new long[] { 4 }, Assert.Single(readerConnector.Calls).Keys.Select(k => k.Int64Value));
        Assert.Equal(new[] { "item_store:item:3", "item_store:item:1", "item_store:item:4" }, store.Gets.Last());
        Assert.Equal(1, reader.WriteBack("item_store"));
        var version = reader.Tables["item_store"].Pin();
        Assert.True(version.Find(3L).Found && version.Find(1L).Found && version.Find(4L).Found);
        Assert.Single(writerConnector.Calls);
    }

    [Fact]
    public async Task ADistributedTierThatFails_SendsItsKeysToTheConnector_AndDoesNotDegradeTheFeatures()
    {
        var store = new FakeCacheStore { BeforeGet = _ => throw new InvalidOperationException("connection refused") };
        var (rig, connector, clicks) = DistributedRig(store);

        var request = rig.NewRequest(Three);
        var result = await request.EnsureAsync(clicks);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new[] { 1.0, 2, 3 }, Values(request, clicks, 3));
        Assert.Single(connector.Calls);
    }

    [Fact]
    public async Task ADistributedTierThatDoesNotAnswer_IsWaitedForAtMostItsTimeout_ThenTheConnectorAnswers()
    {
        var never = new TaskCompletionSource();
        var store = new FakeCacheStore { BeforeGet = _ => new ValueTask(never.Task) };
        var (rig, connector, clicks) = DistributedRig(store, distributedTimeoutMs: 50);

        var request = rig.NewRequest(Three);
        var result = await request.EnsureAsync(clicks);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new[] { 1.0, 2, 3 }, Values(request, clicks, 3));
        Assert.Single(connector.Calls);
        var io = request.Fetcher.WhenIoCompleteAsync();
        Assert.False(io.IsCompleted); // the abandoned store read still holds the request memory
        never.SetResult();
        await io;
        request.Memory.Release();
        Assert.True(request.Memory.IsReturned);
    }

    [Fact]
    public async Task ARecordOfAnotherFieldSet_IsAMiss()
    {
        var store = new FakeCacheStore();
        var (writer, _, writerClicks) = DistributedRig(store);
        await writer.NewRequest(Three).EnsureAsync(writerClicks);
        await store.WhenWrittenAsync(3);

        // Another catalog version reads one more field of the source: its records have another fingerprint.
        var connector = Clicks();
        var reader = new FeatureTestRig().CacheSource("item_store", connector, distributed: store);
        var clicks = reader.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        reader.Physical("item.age", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        reader.Build();
        var request = reader.NewRequest(Three);
        await request.EnsureAsync(clicks);

        Assert.Equal(new long[] { 1, 2, 3 }, Assert.Single(connector.Calls).Keys.Select(k => k.Int64Value));
        Assert.Equal(new[] { 1.0, 2, 3 }, Values(request, clicks, 3));
    }

    [Fact]
    public async Task ARecordOfAnotherFieldSet_WhoseNamesSpellTheSameWithTabsAndNewlines_IsAMiss()
    {
        // Review 19: the same number of fields, and the same text once names and types are joined by tabs and newlines.
        const string Joined = "a\tfloat64\nb", Second = "b\tfloat64\nc";
        var store = new FakeCacheStore();
        var writer = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((_, field) => field == "c" ? 22.0 : 11.0), distributed: store);
        writer.Physical("item.left", FeatureTestRig.Item, ColumnType.Float64, "item_store", field: Joined);
        var written = writer.Physical("item.right", FeatureTestRig.Item, ColumnType.Float64, "item_store", field: "c");
        writer.Build();
        await writer.NewRequest([1]).EnsureAsync(written);
        await store.WhenWrittenAsync(1);

        var connector = ScriptedConnector.Values((_, _) => 999.0);
        var reader = new FeatureTestRig().CacheSource("item_store", connector, distributed: store);
        var left = reader.Physical("item.left", FeatureTestRig.Item, ColumnType.Float64, "item_store", field: "a");
        var right = reader.Physical("item.right", FeatureTestRig.Item, ColumnType.Float64, "item_store", field: Second);
        reader.Build();
        var request = reader.NewRequest([1]);
        var result = await request.EnsureAsync(left, right);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal((999.0, 999.0), (request.Value(left, 0).AsFloat64(), request.Value(right, 0).AsFloat64())); // not 11 and 22
        Assert.Single(connector.Calls);
        Assert.NotEqual(writer.Tables["item_store"].Cache!.Fingerprint, reader.Tables["item_store"].Cache!.Fingerprint);
    }

    [Fact]
    public async Task AContextCacheGroup_MissesOnce_ThenHits()
    {
        var connector = ScriptedConnector.Values((key, _) => key / 1000.0);
        var rig = new FeatureTestRig().CacheSource("user_store", connector);
        var ctr = rig.Physical("user.ctr", FeatureTestRig.User, ColumnType.Float64, "user_store");
        rig.Build();

        var first = rig.NewRequest([1], userId: 50);
        first.Fetcher.StartContextFetch(new[] { ctr }, CancellationToken.None);
        Assert.Equal(FeatureState.Ok, (await first.EnsureAsync(ctr)).State);
        Assert.Equal(0.05, first.ContextValue(ctr).AsFloat64());
        rig.WriteBack("user_store");

        var second = rig.NewRequest([1], userId: 50);
        second.Fetcher.StartContextFetch(new[] { ctr }, CancellationToken.None);
        await second.EnsureAsync(ctr);

        Assert.Equal(0.05, second.ContextValue(ctr).AsFloat64());
        Assert.Equal(50, Assert.Single(connector.Calls).ContextKey.Int64Value);
    }

    [Fact]
    public async Task EveryFieldType_ReadsTheSameThroughEachTier_AsAFetch()
    {
        // The connector's answer is encoded for the tiers and decoded into the slots: a miss, an L1 hit and an L2 hit give
        // a fetch's values, types and absences for every storage type.
        var store = new FakeCacheStore();
        var fetched = TypesRig(cache: false, store);
        var missed = TypesRig(cache: true, store);
        var candidates = new long[] { 3, 1, 2, 9 };
        var expected = await Read(fetched, candidates);
        var miss = await Read(missed, candidates);
        missed.Rig.WriteBack("item_store");
        await store.WhenWrittenAsync(3);
        var local = await Read(missed, candidates);
        var distributed = await Read(TypesRig(cache: true, store), candidates);

        Assert.Equal(expected, miss);
        Assert.Equal(expected, local);
        Assert.Equal(expected, distributed);

        static async Task<List<string>> Read((FeatureTestRig Rig, int[] Features) setup, long[] candidates)
        {
            var request = setup.Rig.NewRequest(candidates);
            var result = await request.EnsureAsync(setup.Features);
            Assert.Equal(FeatureState.Ok, result.State);
            return setup.Features.SelectMany(f => Enumerable.Range(0, candidates.Length).Select(r => $"{f}/{r}: {request.Value(f, r)}")).ToList();
        }
    }

    [Fact]
    public async Task ARecordedCacheRead_IsOneCall_WhoseReplayGivesTheSameValuesAndStates()
    {
        var fail = false;
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            if (fail)
            {
                return ValueTask.FromResult(FetchStatus.Failed("down"));
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key == 5 ? null : key * 1.5);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().CacheSource("item_store", connector, ttlMs: 1_000, staleIfErrorMs: 60_000);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store", ScalarValue.FromFloat64(-1));
        rig.Build();
        await rig.NewRequest([1, 2]).EnsureAsync(clicks);
        rig.WriteBack("item_store");
        rig.Clock.AdvanceMs(500);
        await rig.NewRequest([3, 5]).EnsureAsync(clicks); // fresh 3, absent 5
        rig.WriteBack("item_store");
        rig.Clock.AdvanceMs(700); // 1 and 2 expired (within the stale window); 3 and 5 fresh
        fail = true;

        var recorder = new RecordingHook();
        var live = rig.NewRequest([1, 3, 5, 2, 8, 3], hook: recorder);
        var liveResult = await live.EnsureAsync(clicks);
        var replayed = rig.NewRequest([1, 3, 5, 2, 8, 3], hook: recorder.Replayer());
        var replayResult = await replayed.EnsureAsync(clicks);

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(new long[] { 1, 3, 5, 2, 8 }, call.Keys); // the read's distinct keys, in row order
        Assert.Equal(IoOutcome.Partial, call.Outcome);
        Assert.Equal(new double?[] { 1.5, 4.5, null, 3.0, null }, call.Values); // stale, fresh, absent, stale, failed
        Assert.Equal(FeatureState.Degraded, liveResult.State);
        Assert.Equal(liveResult, replayResult);
        Assert.Equal(Values(live, clicks, 6), Values(replayed, clicks, 6));
        Assert.Equal(new[] { 1.5, 4.5, -1, 3.0, -1, 4.5 }, Values(live, clicks, 6));
        Assert.Equal(3, connector.Calls.Count); // the replay called nothing
    }

    [Fact]
    public async Task NonFiniteValues_AreCachedAsStored_AndServedAbsent_OnAMissAndOnAHit()
    {
        var rig = new FeatureTestRig().CacheSource("item_store", ScriptedConnector.Values((key, _) => key == 1 ? double.NaN : double.PositiveInfinity));
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store", ScalarValue.FromFloat64(-1));
        var weight = rig.Physical("item.weight", FeatureTestRig.Item, ColumnType.Float32, "item_store");
        rig.Build();

        var miss = rig.NewRequest([1, 2]);
        Assert.Equal(FeatureState.Ok, (await miss.EnsureAsync(score, weight)).State);
        rig.WriteBack("item_store");
        var hit = rig.NewRequest([1, 2]);
        await hit.EnsureAsync(score, weight);

        foreach (var request in new[] { miss, hit })
        {
            Assert.Equal(new[] { -1.0, -1 }, Values(request, score, 2));
            Assert.True(request.Value(weight, 0).IsMissing && request.Value(weight, 1).IsMissing);
            Assert.Equal(4, request.Fetcher.NonFiniteReplaced);
        }

        var version = rig.Tables["item_store"].Pin();
        Assert.True(double.IsNaN(version.Read(version.Layout.Schema.FieldIndex("score"), version.Find(1L)).AsFloat64()));
    }

    private static ScriptedConnector Clicks() => ScriptedConnector.Values((key, _) => key * 1.0);

    private static double[] Values(TestRequest request, int feature, int rows) =>
        Enumerable.Range(0, rows).Select(r => request.Value(feature, r).AsFloat64()).ToArray();

    private static (FeatureTestRig Rig, ScriptedConnector Connector, int Clicks) DistributedRig(FakeCacheStore store, int? distributedTimeoutMs = null)
    {
        var connector = Clicks();
        var rig = new FeatureTestRig().CacheSource("item_store", connector, distributed: store, distributedTimeoutMs: distributedTimeoutMs);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        return (rig, connector, clicks);
    }

    /// <summary>A source of every storage type: items 1–3 have records (2 without some fields), 9 has none.</summary>
    private static (FeatureTestRig Rig, int[] Features) TypesRig(bool cache, FakeCacheStore store)
    {
        const string Json = """
            {
              "1": { "b": true, "i32": -7, "i64": 9007199254740991, "f32": 1.25, "f64": -2.5, "s": "music", "t": "a long text ✓",
                     "v": [1, 2, 3, 4], "li32": [1, -2], "li64": [-9007199254740991, 0], "ls": ["x", "", "zé"] },
              "2": { "i64": 2, "s": "food", "ls": [] },
              "3": { "b": false, "i32": 0, "f32": 3.5, "f64": 1e300, "t": "", "v": [0, 0, 0, 0], "li32": [], "li64": [5] }
            }
            """;
        var rig = new FeatureTestRig();
        if (cache)
        {
            rig.MemoryCacheSource("item_store", Json, distributed: store);
        }
        else
        {
            rig.MemorySource("item_store", Json);
        }

        var features = new[]
        {
            rig.Physical("item.b", FeatureTestRig.Item, ColumnType.Bool, "item_store"),
            rig.Physical("item.i32", FeatureTestRig.Item, ColumnType.Int32, "item_store", ScalarValue.FromInt64(-1)),
            rig.Physical("item.i64", FeatureTestRig.Item, ColumnType.Int64, "item_store"),
            rig.Physical("item.f32", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0.5)),
            rig.Physical("item.f64", FeatureTestRig.Item, ColumnType.Float64, "item_store"),
            rig.Physical("item.s", FeatureTestRig.Item, ColumnType.String, "item_store", ScalarValue.FromString("unknown")),
            rig.Physical("item.t", FeatureTestRig.Item, ColumnType.Text, "item_store"),
            rig.Physical("item.v", FeatureTestRig.Item, ColumnType.Vector(4), "item_store"),
            rig.Physical("item.li32", FeatureTestRig.Item, ColumnType.ListInt32, "item_store"),
            rig.Physical("item.li64", FeatureTestRig.Item, ColumnType.ListInt64, "item_store"),
            rig.Physical("item.ls", FeatureTestRig.Item, ColumnType.ListString, "item_store"),
            rig.Physical("item.s_again", FeatureTestRig.Item, ColumnType.String, "item_store", field: "s"), // two features, one field
        };
        rig.Build();
        return (rig, features);
    }

    /// <summary>Records each call's keys, first field's values and status; its replayer answers a call with the same keys.</summary>
    private sealed class RecordingHook : IFeatureCallHook
    {
        private int _sequence;

        public ConcurrentQueue<RecordedRead> Calls { get; } = new();

        public bool Replays => false;

        public int Begin(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields) => Interlocked.Increment(ref _sequence) - 1;

        public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
        {
            var keys = call.Batch.CandidateKeys.ToArray().Select(k => k.Int64Value).ToArray();
            double?[]? recorded = null;
            if (values is not null)
            {
                recorded = new double?[keys.Length];
                for (var k = 0; k < keys.Length; k++)
                {
                    var value = values.Get(0, k);
                    recorded[k] = value.IsMissing ? null : value.AsFloat64();
                }
            }

            Calls.Enqueue(new RecordedRead(keys, call.Status, call.Outcome, recorded));
        }

        public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer) =>
            throw new InvalidOperationException("A recorder does not replay.");

        public IFeatureCallHook Replayer() => new ReplayHook(this);

        private sealed class ReplayHook(RecordingHook recording) : IFeatureCallHook
        {
            public bool Replays => true;

            public int Begin(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields) => 0;

            public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
            {
            }

            public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer)
            {
                var keys = batch.CandidateKeys.ToArray().Select(k => k.Int64Value).ToArray();
                var call = recording.Calls.Single(c => c.Keys.SequenceEqual(keys));
                if (call.Outcome is not (IoOutcome.Ok or IoOutcome.Partial))
                {
                    return call.Status;
                }

                for (var k = 0; k < keys.Length; k++)
                {
                    if (call.Values![k] is { } value)
                    {
                        ScriptedConnector.Write(writer, fields[0], k, value);
                    }
                }

                return call.Status;
            }
        }
    }

    private sealed record RecordedRead(long[] Keys, FetchStatus Status, IoOutcome Outcome, double?[]? Values);
}
