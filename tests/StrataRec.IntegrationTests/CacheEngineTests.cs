using System.Collections.Concurrent;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Cache;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A <c>cache</c> source through the engine (design 6.6): the sample's <c>user_store</c> read through an in-process table
/// in front of its connector, and optionally a distributed tier shared by engines. Served results are those of the fetched
/// source; a recorded request replays exactly without the cache; the distributed tier is a binding instance (design 10.3).
/// Write-backs run in the background, so the tests wait for their metrics instead of timing them.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class CacheEngineTests
{
    private const string Source = "user_store";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ACachedSource_ServesWhatTheFetchedOneServes_AndTheNextRequestFromItsTable()
    {
        await using var reference = MetricsTests.LoadEngine(HomeFeedTestHost.Load()).Engine;
        await using var engine = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root => CacheUsers(root))).Engine;
        using var metrics = new MetricCapture();

        var expected = await reference.ExecuteAsync(MetricsTests.Request("req-fetched"));
        var first = await engine.ExecuteAsync(MetricsTests.Request("req-first"));
        var afterFirst = Keys(metrics);
        await Eventually(() => Writes(metrics, "local", "written") >= 1, "the write-back of user 1001");
        var second = await engine.ExecuteAsync(MetricsTests.Request("req-second"));
        var afterSecond = Keys(metrics);

        Assert.True(expected.Succeeded && first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(Items(expected), Items(first));
        Assert.Equal(Items(expected), Items(second));
        Assert.True(afterFirst.Get("local", "miss") >= 1 && afterFirst.Get("local", "hit") == 0, afterFirst.ToString());
        var secondOnly = afterSecond.Minus(afterFirst);
        Assert.True(secondOnly.Get("local", "hit") >= 1 && secondOnly.Get("local", "miss") == 0, secondOnly.ToString());
        Assert.Equal(0, afterSecond.Get("distributed", "hit") + afterSecond.Get("distributed", "miss")); // no distributed tier
    }

    [Fact]
    public async Task ARecordedCacheRead_ReplaysExactly_WithoutTheTable()
    {
        await using var engine = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root => CacheUsers(root))).Engine;
        using var metrics = new MetricCapture();
        var missed = await engine.ExecuteAsync(ReplayTests.Request("req-miss", 1001, "home", ReplayTests.ControlArms));
        await Eventually(() => Writes(metrics, "local", "written") >= 1, "the write-back of user 1001");
        var hit = await engine.ExecuteAsync(ReplayTests.Request("req-hit", 1001, "home", ReplayTests.ControlArms));
        Assert.True(Keys(metrics).Get("local", "hit") >= 1);

        // A fresh engine on the same configuration: its table is empty, so a replay that read it would differ.
        await using var fresh = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root => CacheUsers(root))).Engine;
        var before = metrics.Measurements.Count;
        foreach (var recorded in new[] { missed, hit })
        {
            Assert.True(recorded.Succeeded, recorded.Error);
            var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
            var outcome = await fresh.ReplayAsync(bundle);
            Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
            Assert.Empty(outcome.Report.Gaps);
            Assert.Equal(Items(recorded), Items(outcome.Result));
        }

        Assert.DoesNotContain(metrics.Measurements.Skip(before), m => m.Instrument.StartsWith("stratarec.cache.", StringComparison.Ordinal));
        var live = Keys(metrics);
        Assert.True((await fresh.ExecuteAsync(MetricsTests.Request("req-live"))).Succeeded);
        var liveOnly = Keys(metrics).Minus(live);
        Assert.True(liveOnly.Get("local", "miss") >= 1 && liveOnly.Get("local", "hit") == 0, liveOnly.ToString()); // the replays wrote nothing back
    }

    [Fact]
    public async Task TwoEnginesSharingADistributedTier_TheSecondServesTheFirstsWriteBack()
    {
        var shared = new SharedStores();
        var root = HomeFeedTestHost.LoadModified(r => CacheUsers(r, $$"""{ "store": "{{SharedStores.Type}}", "generation": 1 }""")).Root;
        await using var first = await Load(root, shared);
        await using var second = await Load(root, shared);
        using var metrics = new MetricCapture();

        var origin = await first.ExecuteAsync(MetricsTests.Request("req-origin"));
        await Eventually(() => Writes(metrics, "distributed", "written") >= 1, "the distributed write-back of user 1001");
        var fromStore = await second.ExecuteAsync(MetricsTests.Request("req-store"));

        Assert.True(origin.Succeeded && fromStore.Succeeded, origin.Error ?? fromStore.Error);
        Assert.Equal(Items(origin), Items(fromStore));
        var keys = Keys(metrics);
        Assert.True(keys.Get("distributed", "miss") >= 1 && keys.Get("distributed", "hit") >= 1, keys.ToString()); // the first missed, the second hit
        Assert.Equal(0, keys.Get("distributed", "error") + keys.Get("distributed", "mismatch"));
        Assert.Equal(2, shared.Created.Count); // one store per engine (each has its own binding host)
    }

    [Fact]
    public async Task TheDistributedTier_IsABindingInstance_SharedWhileUnchanged_ReplacedWhenChanged_DisposedWithTheEngine()
    {
        var shared = new SharedStores();
        var root = HomeFeedTestHost.LoadModified(r => CacheUsers(r, $$"""{ "store": "{{SharedStores.Type}}", "generation": 1 }""")).Root;
        var engine = await Load(root, shared);
        var store = Assert.Single(shared.Created);
        Assert.Equal(1, store.Loads);

        Assert.True((await engine.LoadAsync(Compile(root), ArmRanges(root))).Applied); // the same binding: the same store
        Assert.Single(shared.Created);

        CacheUsers(root, $$"""{ "store": "{{SharedStores.Type}}", "generation": 1, "timeout_ms": 20 }""");
        Assert.True((await engine.LoadAsync(Compile(root), ArmRanges(root))).Applied); // the engine's wait, not the store's identity
        Assert.Single(shared.Created);
        Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-unchanged"))).Succeeded);

        CacheUsers(root, $$"""{ "store": "{{SharedStores.Type}}", "generation": 2 }""");
        Assert.True((await engine.LoadAsync(Compile(root), ArmRanges(root))).Applied);
        var replacement = Assert.Single(shared.Created, s => s != store);
        await store.Disposed.Task.WaitAsync(Wait); // no served plan uses it
        Assert.Equal(1, replacement.Loads);
        Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-changed"))).Succeeded);

        await engine.DisposeAsync();
        await replacement.Disposed.Task.WaitAsync(Wait);
        Assert.Equal(1, store.Disposals);
        Assert.Equal(1, replacement.Disposals);
    }

    /// <summary>
    /// Binds the sample's <c>user_store</c> as a <c>cache</c> in front of its fixture, untimed (a slow machine would only
    /// fetch again what the cache missed), with the distributed tier <paramref name="distributed"/> when given.
    /// </summary>
    internal static void CacheUsers(string root, string? distributed = null) => SampleBindings.Rebind(root, Source, $$"""
        {
          "mode": "cache", "connector": "memory@1", "data_file": "fixtures/users.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}},
          "max_bytes": "4MiB",
          "cache": { "ttl_ms": 600000{{(distributed is null ? string.Empty : ", \"distributed\": " + distributed)}} }
        }
        """);

    internal static async Task<RecEngine> Load(string root, SharedStores stores, FeatureCacheStoreRegistry? registry = null)
    {
        var environment = new PlanEnvironment { BaseDirectory = root, CacheStores = registry ?? new FeatureCacheStoreRegistry().Add(stores) };
        var engine = new RecEngine(environment);
        var load = await engine.LoadAsync(Compile(root), ArmRanges(root));
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    internal static PlanCompileRequest Compile(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return request!;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>> ArmRanges(string root) =>
        ConfigWorkspace.Load(root, new DiagnosticBag()).ArmRanges("home_feed", "local");

    internal static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>The keys of <see cref="Source"/> read so far, by tier and outcome.</summary>
    internal static CacheCounts Keys(MetricCapture metrics)
    {
        var counts = new Dictionary<(string, string), long>();
        foreach (var m in metrics.Of(StrataRecMetrics.Names.CacheKeys))
        {
            if (Of(m) is { } key)
            {
                counts[key] = counts.GetValueOrDefault(key) + (long)m.Value;
            }
        }

        return new CacheCounts(counts);
    }

    internal static long Writes(MetricCapture metrics, string tier, string outcome) =>
        metrics.Of(StrataRecMetrics.Names.CacheWrites).Where(m => Of(m) == (tier, outcome)).Sum(m => (long)m.Value);

    private static (string Tier, string Outcome)? Of(Measured m) =>
        m.Tags.GetValueOrDefault(StrataRecTags.Source) as string == Source
            ? ((string)m.Tags[StrataRecTags.CacheTier]!, (string)m.Tags[StrataRecTags.Outcome]!)
            : null;

    private static List<(long, int, string)> Items(RecResult result) =>
        result.Items.Select(i => (i.Id.Int64Value, i.Rank, string.Join(";", i.Columns.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")))).ToList();

    internal sealed record CacheCounts(Dictionary<(string Tier, string Outcome), long> Counts)
    {
        public long Get(string tier, string outcome) => Counts.GetValueOrDefault((tier, outcome));

        public CacheCounts Minus(CacheCounts earlier) =>
            new(Counts.ToDictionary(c => c.Key, c => c.Value - earlier.Get(c.Key.Tier, c.Key.Outcome)));

        public override string ToString() => string.Join(", ", Counts.OrderBy(c => c.Key).Select(c => $"{c.Key.Tier}/{c.Key.Outcome}={c.Value}"));
    }
}

/// <summary>
/// <c>shared_memory@1</c>: distributed cache stores over one in-memory dictionary, so that engines with their own binding
/// hosts share records as instances share a Redis. Accepts any settings (a test changes one to change the identity) and
/// counts each store's loads and disposals.
/// </summary>
internal sealed class SharedStores : IFeatureCacheStoreFactory
{
    public const string Type = "shared_memory@1";

    private readonly ConcurrentDictionary<string, byte[]> _records = new(StringComparer.Ordinal);

    public string StoreType => Type;

    public ConcurrentQueue<Store> Created { get; } = new();

    public IFeatureCacheStore Create(BindingContext context)
    {
        var store = new Store(_records);
        Created.Enqueue(store);
        return store;
    }

    internal sealed class Store(ConcurrentDictionary<string, byte[]> shared) : IFeatureCacheStore, ILoadable, IAsyncDisposable
    {
        private int _loads;
        private int _disposals;

        public string StoreType => Type;

        public int Loads => Volatile.Read(ref _loads);

        public int Disposals => Volatile.Read(ref _disposals);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask LoadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loads);
            return ValueTask.CompletedTask;
        }

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<EntityKey> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                if (shared.TryGetValue(Name(keyspace, keys.Span[i]), out var record))
                {
                    records.Span[i] = record;
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken)
        {
            foreach (var entry in entries.Span)
            {
                shared[Name(keyspace, entry.Key)] = entry.Record.ToArray();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private static string Name(CacheKeyspace keyspace, in EntityKey key) => keyspace.Prefix + (key.StringValue ?? key.Int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
