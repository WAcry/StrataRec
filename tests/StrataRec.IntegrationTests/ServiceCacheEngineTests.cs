using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A service's cache through the engine (design 6.9, IO step 3): the sample's <c>ctr_model</c> (<c>linear@1</c>) with
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
        await using var engine = MetricsTests.LoadEngine(Host($$"""{ {{Linear}}, "cache": { "ttl_ms": 600000, "max_bytes": "1MiB" } }""")).Engine;
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
        await using var fresh = MetricsTests.LoadEngine(Host($$"""{ {{Linear}}, "cache": { "ttl_ms": 600000, "max_bytes": "1MiB" } }""")).Engine;
        var replayedFrom = metrics.Measurements.Count;
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(hit.Recording!));
        var outcome = await fresh.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Empty(outcome.Report.Gaps);
        Assert.DoesNotContain(metrics.Measurements.Skip(replayedFrom), m => m.Instrument == StrataRecMetrics.Names.CacheKeys && Equals(m.Tags[StrataRecTags.Source], Service));
    }

    /// <summary>Each wrong setting of a service's cache is an error at the setting, and the service is not served without it.</summary>
    [Theory]
    [InlineData("""{ "ttl_ms": 1000 }""", "CACHE_MAX_BYTES", "/services/ctr_model/cache")]
    [InlineData("""{ "ttl_ms": 1000, "max_bytes": "0B" }""", "CACHE_MAX_BYTES", "/services/ctr_model/cache/max_bytes")]
    [InlineData("""{ "max_bytes": "1MiB" }""", "CACHE_TTL", "/services/ctr_model/cache")]
    [InlineData("""{ "ttl_ms": 0, "max_bytes": "1MiB" }""", "CACHE_TTL", "/services/ctr_model/cache/ttl_ms")]
    [InlineData("""{ "ttl_ms": 1000, "max_bytes": "1MiB", "distributed": { "store": "shared_memory@1" } }""", "DISTRIBUTED_CACHE_CONFIG", "/services/ctr_model/cache/distributed")]
    public void AWrongCacheSetting_IsAnErrorAtTheSetting(string cache, string code, string pointer)
    {
        var host = Host($$"""{ {{Linear}}, "cache": {{cache}} }""");

        var compilation = PlanCompiler.Compile(host.Request, host.Environment);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.IsError && d.Code == code);
        Assert.Equal(("bindings:local", pointer), (error.Artifact, error.Pointer));
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    private static HomeFeedTestHost Host(string binding) => HomeFeedTestHost.LoadModified(root => SampleBindings.RebindService(root, Service, binding));

    /// <summary>The model's cache keys by local outcome so far, and the keys of calls the model was sent.</summary>
    private static ModelKeysRead Keys(MetricCapture metrics)
    {
        long hit = 0, miss = 0;
        foreach (var m in metrics.Of(StrataRecMetrics.Names.CacheKeys))
        {
            if (Equals(m.Tags[StrataRecTags.Source], Service) && Equals(m.Tags[StrataRecTags.CacheTier], "local"))
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
