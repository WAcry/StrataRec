using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Services;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A service's cache through the engine: the sample's <c>ctr_model</c> (<c>linear@1</c>) with
/// <c>cache</c>. Served results are the model's; later requests are answered from its in-process table; a recorded
/// request replays exactly without the cache. Write-backs run in the background, so the tests wait for a state (a request
/// read every key from the table), never for a count or a time.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class ServiceCacheEngineTests
{
    private const string Service = "ctr_model";

    private const string Linear = """
        "client": "linear@1",
        "outputs": { "p_click": { "bias": -1.5, "weights": { "smoothed_ctr": 12.0, "log_followers": 0.05 }, "activation": "sigmoid" } }
        """;

    [Fact]
    public async Task ACachedModel_ServesWhatTheModelServes_LaterRequestsFromItsCache_AndARecordingReplaysWithoutIt()
    {
        static RecRequest Recorded(string id) => ReplayTests.Request(id, 1001, "home", ReplayTests.ControlArms);
        await using var reference = MetricsTests.LoadEngine(HomeFeedTestHost.Load()).Engine;
        await using var engine = MetricsTests.LoadEngine(Host($$"""{ {{Linear}}, "cache": { "ttl_ms": 600000, "memory": { "max_bytes": "1MiB" } } }""")).Engine;
        using var metrics = new MetricCapture();

        var expected = await reference.ExecuteAsync(Recorded("req-model"), TestContext.Current.CancellationToken);
        var before = Keys(metrics);
        var first = await engine.ExecuteAsync(Recorded("req-first"), TestContext.Current.CancellationToken);
        var firstRead = Keys(metrics).Minus(before);
        RecResult hit;
        for (var attempt = 0; ; attempt++)
        {
            before = Keys(metrics);
            hit = await engine.ExecuteAsync(Recorded("req-hit-" + attempt), TestContext.Current.CancellationToken);
            var read = Keys(metrics).Minus(before);
            if (read.Miss == 0 && read.Hit > 0)
            {
                Assert.Equal(0, read.ModelKeys); // the model was not called
                break;
            }

            Assert.True(attempt < 10_000, "no request was answered from the cache: " + read);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(expected.Succeeded && first.Succeeded && hit.Succeeded, first.Error ?? hit.Error);
        Assert.True(firstRead.Miss >= 1 && firstRead.ModelKeys >= 1, firstRead.ToString()); // a cold cache
        Assert.Equal(CacheEngineTests.Items(expected), CacheEngineTests.Items(first));
        Assert.Equal(CacheEngineTests.Items(expected), CacheEngineTests.Items(hit));

        // The recording holds the answer the node got: a fresh engine, whose cache is empty, replays it identically and
        // reads no cache.
        await using var fresh = MetricsTests.LoadEngine(Host($$"""{ {{Linear}}, "cache": { "ttl_ms": 600000, "memory": { "max_bytes": "1MiB" } } }""")).Engine;
        var replayedFrom = metrics.Measurements.Count;
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(hit.Recording!));
        var outcome = await fresh.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Empty(outcome.Report.Gaps);
        Assert.DoesNotContain(metrics.Measurements.Skip(replayedFrom), m => m.Instrument == StrataRecMetrics.Names.CacheKeys && Equals(m.Tags[StrataRecTags.Source], Service));
    }

    /// <summary>Each wrong setting of a service's cache is an error at the setting, and the service is not served without it.</summary>
    [Theory]
    [InlineData("""{ "ttl_ms": 1000 }""", "CACHE_TIERS", "/services/ctr_model/cache")]
    [InlineData("""{ "ttl_ms": 1000, "memory": { } }""", "CACHE_MAX_BYTES", "/services/ctr_model/cache/memory")]
    [InlineData("""{ "ttl_ms": 1000, "memory": { "max_bytes": "0B" } }""", "CACHE_MAX_BYTES", "/services/ctr_model/cache/memory/max_bytes")]
    [InlineData("""{ "ttl_ms": 1000, "memory": { "max_bytes": "1B" } }""", "CACHE_MAX_BYTES", "/services/ctr_model/cache/memory/max_bytes")]
    [InlineData("""{ "memory": { "max_bytes": "1MiB" } }""", "CACHE_TTL", "/services/ctr_model/cache")]
    [InlineData("""{ "ttl_ms": 0, "memory": { "max_bytes": "1MiB" } }""", "CACHE_TTL", "/services/ctr_model/cache/ttl_ms")]
    [InlineData("""{ "ttl_ms": 1000, "version": "a b", "memory": { "max_bytes": "1MiB" } }""", "CACHE_VERSION", "/services/ctr_model/cache/version")]
    [InlineData("""{ "ttl_ms": 1000, "memory": { "max_bytes": "1MiB" }, "distributed": { "store": "shared_memory@1" } }""", "DISTRIBUTED_CACHE_CONFIG", "/services/ctr_model/cache/distributed")]
    public void AWrongCacheSetting_IsAnErrorAtTheSetting(string cache, string code, string pointer)
    {
        var host = Host($$"""{ {{Linear}}, "cache": {{cache}} }""");

        var compilation = PlanCompiler.Compile(host.Request, host.Environment);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.IsError && d.Code == code);
        Assert.Equal(("bindings:local", pointer), (error.Artifact, error.Pointer));
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    /// <summary>
    /// A hot reload that changes the model's settings (its bias) under the same name and version serves the new model's
    /// scores at once, though the distributed tier still holds the earlier binding's answers: they are keyed by what the
    /// binding answers with. The new plan read them, and served the old scores for their TTL.
    /// </summary>
    [Fact]
    public async Task AReloadThatChangesTheModelsSettings_DoesNotReadTheEarlierAnswersFromTheDistributedTier()
    {
        var token = TestContext.Current.CancellationToken;
        var stores = new SharedStores();
        var root = HomeFeedTestHost.LoadModified(path => Rebind(path, -1.5, cached: true)).Root;
        await using var engine = await CacheEngineTests.Load(root, stores);
        var before = await engine.ExecuteAsync(Recorded("req-before"), token);
        await CacheEngineTests.Eventually(stores.HoldsEveryKeyRead, () => "the write-back of the first model's answers");

        Rebind(root, 5.0, cached: true);
        Assert.True((await engine.LoadAsync(CacheEngineTests.Compile(root), cancellationToken: token)).Applied);
        var after = await engine.ExecuteAsync(Recorded("req-after"), token);
        Rebind(root, 5.0, cached: false);
        Assert.True((await engine.LoadAsync(CacheEngineTests.Compile(root), cancellationToken: token)).Applied);
        var uncached = await engine.ExecuteAsync(Recorded("req-uncached"), token);

        Assert.True(before.Succeeded && after.Succeeded && uncached.Succeeded, before.Error ?? after.Error ?? uncached.Error);
        Assert.NotEqual(Scores(before), Scores(uncached));
        Assert.Equal(Scores(uncached), Scores(after));

        static void Rebind(string root, double bias, bool cached) => SampleBindings.RebindService(root, Service, FormattableString.Invariant($$"""
            { "client": "linear@1", "outputs": { "p_click": { "bias": {{bias}}, "weights": { "smoothed_ctr": 12.0, "log_followers": 0.05 }, "activation": "sigmoid" } }{{(cached ? Distributed : string.Empty)}} }
            """));

        static string Scores(RecResult result) =>
            string.Join(';', result.Recording!.Runs[0].ServiceCalls.Where(c => c.Service == Service).Select(c => c.Outputs["p_click"].Values.GetRawText()));
    }

    /// <summary>
    /// A service's cache with a version keeps its answers when the client's settings change, and starts anew when the
    /// version does: the version is what the answers are keyed by, in place of a digest of the client and its settings.
    /// </summary>
    [Fact]
    public async Task AServicesCacheWithAVersion_KeepsItsAnswersWhenTheClientsSettingsChange_UntilTheVersionChanges()
    {
        var token = TestContext.Current.CancellationToken;
        var stores = new SharedStores();
        var root = HomeFeedTestHost.LoadModified(path => Rebind(path, -1.5, "1")).Root;
        await using var engine = await CacheEngineTests.Load(root, stores);
        var first = await engine.ExecuteAsync(Recorded("req-first"), token);
        await CacheEngineTests.Eventually(stores.HoldsEveryKeyRead, () => "the write-back of the first answers");

        Rebind(root, 5.0, "1");
        Assert.True((await engine.LoadAsync(CacheEngineTests.Compile(root), cancellationToken: token)).Applied);
        var kept = await engine.ExecuteAsync(Recorded("req-kept"), token);
        Rebind(root, 5.0, "2");
        Assert.True((await engine.LoadAsync(CacheEngineTests.Compile(root), cancellationToken: token)).Applied);
        var anew = await engine.ExecuteAsync(Recorded("req-anew"), token);

        Assert.True(first.Succeeded && kept.Succeeded && anew.Succeeded, first.Error ?? kept.Error ?? anew.Error);
        Assert.Equal(Scores(first), Scores(kept)); // the version says the other bias answers the same
        Assert.NotEqual(Scores(first), Scores(anew));

        static void Rebind(string root, double bias, string version) => SampleBindings.RebindService(root, Service, FormattableString.Invariant($$"""
            { "client": "linear@1", "outputs": { "p_click": { "bias": {{bias}}, "weights": { "smoothed_ctr": 12.0, "log_followers": 0.05 }, "activation": "sigmoid" } }{{Distributed.Replace("\"ttl_ms\": 600000", "\"ttl_ms\": 600000, \"version\": \"" + version + "\"", StringComparison.Ordinal)}} }
            """));

        static string Scores(RecResult result) =>
            string.Join(';', result.Recording!.Runs[0].ServiceCalls.Where(c => c.Service == Service).Select(c => c.Outputs["p_click"].Values.GetRawText()));
    }

    /// <summary>
    /// A node its service's cache answered with stale values after the service failed is Degraded, and replays Degraded:
    /// the recording has why. It replayed as succeeded, and the replay called a node changed that was not.
    /// </summary>
    [Fact]
    public async Task ANodeTheCacheAnsweredWithStaleValues_ReplaysDegraded()
    {
        var token = TestContext.Current.CancellationToken;
        var model = new FlakyModel();
        var clock = new MovableClock();
        await using var engine = FlakyEngine(model, clock, cached: true);
        await CacheEngineTests.Eventually(
            () =>
            {
                var calls = model.Calls;
                Assert.True(engine.ExecuteAsync(Recorded("req-warm"), token).AsTask().GetAwaiter().GetResult().Succeeded);
                return model.Calls == calls; // answered from the cache
            },
            () => "a request answered from the model's cache");

        clock.Advance(2_000); // past the TTL, within stale_if_error_ms
        model.Fail = true;
        var served = await engine.ExecuteAsync(Recorded("req-stale"), token);
        var replay = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(served.Recording!)), cancellationToken: token);

        Assert.True(served.Succeeded, served.Error);
        Assert.Equal("degraded", Assert.Single(served.Recording!.Runs[0].Nodes, n => n.Node == "ctr_ranker").Outcome);
        Assert.True(replay.Report.Diff.Identical, ReplayTests.Describe(replay.Report));
        var ranker = Assert.Single(replay.Report.Nodes, n => n.Node == "ctr_ranker");
        Assert.False(ranker.Changed, $"recorded {ranker.Recorded}, replayed {ranker.Replayed}");
    }

    /// <summary>
    /// A failed answer of a cached service is recorded as the service gave it, its generation details too, as without a
    /// cache. The cache recorded them only for an answer that succeeded.
    /// </summary>
    [Fact]
    public async Task AFailedAnswer_IsRecordedWithItsGenerationDetails_WithOrWithoutACache()
    {
        var token = TestContext.Current.CancellationToken;
        var model = new FlakyModel { Fail = true };
        await using var plain = FlakyEngine(model, TimeProvider.System, cached: false);
        await using var cached = FlakyEngine(model, TimeProvider.System, cached: true);

        var withoutCache = (await plain.ExecuteAsync(Recorded("req-plain"), token)).Recording!.Runs[0].ServiceCalls.Single(c => c.Service == Service);
        var withCache = (await cached.ExecuteAsync(Recorded("req-cached"), token)).Recording!.Runs[0].ServiceCalls.Single(c => c.Service == Service);

        Assert.Equal(("error", 11, (bool?)true), (withoutCache.Status, withoutCache.OutputTokens, withoutCache.Truncated));
        Assert.Equal(("error", 11, (bool?)true), (withCache.Status, withCache.OutputTokens, withCache.Truncated));
    }

    private const string Distributed = """, "cache": { "ttl_ms": 600000, "memory": { "max_bytes": "1MiB" }, "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } }""";

    private static RecRequest Recorded(string id) => ReplayTests.Request(id, 1001, "home", ReplayTests.ControlArms);

    private static RecEngine FlakyEngine(FlakyModel model, TimeProvider clock, bool cached)
    {
        var cache = cached ? """, "cache": { "ttl_ms": 1000, "memory": { "max_bytes": "1MiB" }, "stale_if_error_ms": 60000 }""" : string.Empty;
        var host = HomeFeedTestHost.LoadModified(root => SampleBindings.RebindService(root, Service, $$"""{ "client": "{{FlakyModel.TypeName}}"{{cache}} }"""));
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Bindings = PlanEnvironment.DefaultBindings().Add(model), Clock = clock });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static HomeFeedTestHost Host(string binding) => HomeFeedTestHost.LoadModified(root => SampleBindings.RebindService(root, Service, binding));

    /// <summary>
    /// <c>ctr_model</c> as a model that answers 0.25 for every item, or fails reporting 11 generated tokens and a truncated
    /// generation when <see cref="Fail"/>.
    /// </summary>
    private sealed class FlakyModel : IBindingFactory<IServiceClient>, IServiceClient
    {
        public const string TypeName = "flaky_model@1";

        private static readonly int[] Rows = [-1];

        private int _calls;

        public string Type => TypeName;

        public Type SettingsType => typeof(NoSettings);

        public bool Fail { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public IServiceClient Create(BindingContext context) => this;

        public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ServiceSignature(
                [new TensorSpec("log_followers", TensorElementType.Float32, Rows), new TensorSpec("smoothed_ctr", TensorElementType.Float32, Rows)],
                [new TensorSpec("p_click", TensorElementType.Float32, Rows)]));

        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Fail)
            {
                return ValueTask.FromResult(ServiceStatus.Failed("model outage") with { OutputTokens = 11, Truncated = true });
            }

            response.GetValues<float>("p_click", call.BatchSize).Span.Fill(0.25f);
            return ValueTask.FromResult(ServiceStatus.Ok);
        }

        private sealed class NoSettings;
    }

    /// <summary>A wall clock the test moves.</summary>
    private sealed class MovableClock : TimeProvider
    {
        private long _timeMs = 1_720_000_000_000L;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _timeMs));

        public void Advance(long milliseconds) => Interlocked.Add(ref _timeMs, milliseconds);
    }

    /// <summary>The model's cache keys by local outcome so far, and the keys of calls the model was sent.</summary>
    private static ModelKeysRead Keys(MetricCapture metrics)
    {
        long hit = 0, miss = 0;
        foreach (var m in metrics.Of(StrataRecMetrics.Names.CacheKeys))
        {
            if (Equals(m.Tags[StrataRecTags.Source], Service) && Equals(m.Tags[StrataRecTags.CacheTier], "memory"))
            {
                hit += Equals(m.Tags[StrataRecTags.Outcome], "hit") ? (long)m.Value : 0;
                miss += Equals(m.Tags[StrataRecTags.Outcome], "miss") || Equals(m.Tags[StrataRecTags.Outcome], "expired") ? (long)m.Value : 0;
            }
        }

        var sent = metrics.Of(StrataRecMetrics.Names.IoKeys).Where(m => Equals(m.Tags[StrataRecTags.IoTarget], Service)).Sum(m => (long)m.Value);
        return new ModelKeysRead(hit, miss, sent);
    }

    private sealed record ModelKeysRead(long Hit, long Miss, long ModelKeys)
    {
        public ModelKeysRead Minus(ModelKeysRead earlier) => new(Hit - earlier.Hit, Miss - earlier.Miss, ModelKeys - earlier.ModelKeys);
    }
}
