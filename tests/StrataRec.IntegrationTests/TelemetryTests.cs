using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Logging;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;
using StrataRec.Core.Services;

namespace StrataRec.IntegrationTests;

[Collection(ProcessWideListenersCollection.Name)]
public sealed class MetricsTests
{
    internal static readonly string[] Arms = { "exp_quality_weight/strong", "exp_mmr_diversity/mmr" };

    private static readonly HashSet<string> BoundedTagKeys = new(StringComparer.Ordinal)
    {
        StrataRecTags.Pipeline, StrataRecTags.Node, StrataRecTags.Source, StrataRecTags.IoRole, StrataRecTags.IoTarget,
        StrataRecTags.Outcome, StrataRecTags.Arm, StrataRecTags.Column, StrataRecTags.Tap, StrataRecTags.LogDelivery, StrataRecTags.Entity,
        StrataRecTags.CacheTier, StrataRecTags.Token, StrataRecTags.Feature,
    };

    [Fact]
    public async Task HomeFeedRequest_RecordsTheInstrumentsWithBoundedTagsAndPlausibleValues()
    {
        using var capture = new MetricCapture();
        var (engine, plan) = LoadEngine(HomeFeedTestHost.Load());

        var watch = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(Request("req-metrics"), TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.True(result.Succeeded, result.Error);
        AssertTagRules(capture.Measurements);
        var pipeline = new Dictionary<string, object?> { [StrataRecTags.Pipeline] = "home_feed" };

        Assert.Contains(capture.Of(StrataRecMetrics.Names.PlanCompileDuration), m => m.Value > 0 && TagsEqual(m, pipeline));
        Assert.Single(capture.Of(StrataRecMetrics.Names.PlanLoads), m => Tag(m, StrataRecTags.Outcome) == "applied");
        Assert.Equal(plan.Nodes.Length, Assert.Single(capture.Of(StrataRecMetrics.Names.PlanVariants)).Value);
        Assert.Equal(1, Assert.Single(capture.Of(StrataRecMetrics.Names.PlanSequence)).Value);

        var request = Assert.Single(capture.Of(StrataRecMetrics.Names.RequestDuration));
        Assert.True(TagsEqual(request, pipeline));
        // In seconds, and measured around the execution: no fixed bound a slow machine could pass.
        Assert.InRange(request.Value, result.ElapsedMs / 1000, watch.Elapsed.TotalSeconds);
        var perArm = capture.Of(StrataRecMetrics.Names.ArmRequestDuration);
        Assert.Equal(Arms.Order(), perArm.Select(m => Tag(m, StrataRecTags.Arm)).Order());
        Assert.All(perArm, m => Assert.Equal(request.Value, m.Value));
        Assert.Empty(capture.Of(StrataRecMetrics.Names.RequestErrors));
        Assert.Empty(capture.Of(StrataRecMetrics.Names.ArmRequestErrors));
        Assert.Empty(capture.Of(StrataRecMetrics.Names.RequestArmsIgnored));

        var trace = result.Trace!;
        var outcomes = capture.Of(StrataRecMetrics.Names.NodeOutcomes);
        Assert.Equal(trace.Select(t => t.NodeId).Order(), outcomes.Select(m => Tag(m, StrataRecTags.Node)).Order());
        Assert.All(outcomes, m => Assert.Equal("succeeded", Tag(m, StrataRecTags.Outcome)));
        foreach (var node in trace)
        {
            var tags = new Dictionary<string, object?> { [StrataRecTags.Pipeline] = "home_feed", [StrataRecTags.Node] = node.NodeId };
            var seconds = Assert.Single(capture.Of(StrataRecMetrics.Names.NodeDuration), m => TagsEqual(m, tags)).Value;
            Assert.InRange(seconds, (node.DurationMs / 1000) - 1e-9, (node.DurationMs / 1000) + 1e-9);
            Assert.Equal(node.RowsIn, Assert.Single(capture.Of(StrataRecMetrics.Names.NodeRowsIn), m => TagsEqual(m, tags)).Value);
            Assert.Equal(node.RowsOut, Assert.Single(capture.Of(StrataRecMetrics.Names.NodeRowsOut), m => TagsEqual(m, tags)).Value);
        }

        // The local author_store makes no source calls.
        var sources = capture.Of(StrataRecMetrics.Names.IoDuration)
            .Select(m => (Tag(m, StrataRecTags.IoRole), Tag(m, StrataRecTags.IoTarget))).ToHashSet();
        Assert.Superset(new HashSet<(string?, string?)> { ("recall", "two_tower"), ("recall", "popular"), ("fetch", "item_store") }, sources);
        Assert.DoesNotContain(("fetch", "author_store"), sources);
        var itemKeys = capture.Of(StrataRecMetrics.Names.IoKeys).Where(m => Tag(m, StrataRecTags.IoTarget) == "item_store").ToList();
        Assert.NotEmpty(itemKeys);
        Assert.All(itemKeys, m => Assert.InRange(m.Value, 1, 25));
        // Keyed calls only: the fetches, and the model's calls about the candidates they score; a recall is about no items.
        Assert.Equal(new HashSet<string?> { "fetch", "model" }, capture.Of(StrataRecMetrics.Names.IoKeys).Select(m => Tag(m, StrataRecTags.IoRole)).ToHashSet());
        Assert.Equal(30, capture.Of(StrataRecMetrics.Names.IoKeys).Where(m => Tag(m, StrataRecTags.IoRole) == "model").Sum(m => m.Value));
        Assert.Empty(capture.Of(StrataRecMetrics.Names.IoErrors));

        // Some recalled items have no i_safe in the fixture, and one recalled item's author (105) has no record in the
        // author snapshot.
        var values = capture.Of(StrataRecMetrics.Names.SourceValues);
        Assert.All(values, m =>
        {
            Assert.Contains(Tag(m, StrataRecTags.Outcome), new[] { "present", "missing" });
            Assert.True(m.Value > 0);
        });
        double Values(string source, string feature, string outcome) => values
            .Where(m => Tag(m, StrataRecTags.Source) == source && Tag(m, StrataRecTags.Feature) == feature && Tag(m, StrataRecTags.Outcome) == outcome)
            .Sum(m => m.Value);
        Assert.True(Values("item_store", "item.clicks_7d", "present") > 0);
        Assert.Equal(0, Values("item_store", "item.clicks_7d", "missing"));
        Assert.True(Values("item_store", "item.is_policy_safe", "present") > 0);
        Assert.True(Values("item_store", "item.is_policy_safe", "missing") > 0);
        Assert.True(Values("author_store", "author.followers", "present") > 0);
        Assert.True(Values("author_store", "author.followers", "missing") > 0);

        var author = new Dictionary<string, object?> { [StrataRecTags.Source] = "author_store", [StrataRecTags.Entity] = "author" };
        Assert.Contains(capture.Of(StrataRecMetrics.Names.LocalRecords), m => TagsEqual(m, author) && m.Value == 11);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.LocalBytes), m => TagsEqual(m, author) && m.Value > 0);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.LocalLoadDuration), m => TagsEqual(m, author) && m.Value > 0);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.LocalLoadLastSuccess), m => TagsEqual(m, author) && m.Value > 1.7e9);
        var authorKeys = capture.Of(StrataRecMetrics.Names.LocalKeys).Where(m => Tag(m, StrataRecTags.Source) == "author_store").ToList();
        Assert.Contains(authorKeys, m => Tag(m, StrataRecTags.Outcome) == "hit" && m.Value > 0);
        Assert.All(authorKeys, m => Assert.Contains(Tag(m, StrataRecTags.Outcome), new[] { "hit", "miss" }));

        // Model calls: the 30 coarse survivors fit one batch.
        Assert.Single(capture.Of(StrataRecMetrics.Names.IoDuration), m => Tag(m, StrataRecTags.IoRole) == "model" && Tag(m, StrataRecTags.IoTarget) == "ctr_model");

        Assert.True(Assert.Single(capture.Of(StrataRecMetrics.Names.RequestMemoryRented)).Value > 0);
    }

    [Fact]
    public async Task ThePlatformsArmsARequestIgnored_AreCountedByReason_NeverByTheirIds()
    {
        using var capture = new MetricCapture();
        var (engine, _) = LoadEngine(HomeFeedTestHost.Load());

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-ignored",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Assignment = new[] { "exp_ramped_early/t", "exp_mmr_diversity/mmr", "exp_mmr_diversity/control", "exp_quality_weight/strong" },
            Deadline = HomeFeedTestHost.Deadline,
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        AssertTagRules(capture.Measurements);
        var ignored = capture.Of(StrataRecMetrics.Names.RequestArmsIgnored);
        Assert.All(ignored, m => Assert.Equal("home_feed", Tag(m, StrataRecTags.Pipeline)));
        Assert.Equal(1, ignored.Where(m => Tag(m, StrataRecTags.Outcome) == "unknown").Sum(m => m.Value));
        Assert.Equal(2, ignored.Where(m => Tag(m, StrataRecTags.Outcome) == "layer_conflict").Sum(m => m.Value));
        Assert.Equal(new[] { "exp_quality_weight/strong" }, capture.Of(StrataRecMetrics.Names.ArmRequestDuration).Select(m => Tag(m, StrataRecTags.Arm)));
    }

    [Fact]
    public async Task ARequestAssignedThenOutOfBudget_ReportsTheArmsAndIgnoredIdsItsMetricsCounted()
    {
        // A zero deadline is spent by the time assignment returns, deterministically.
        using var capture = new MetricCapture();
        var (engine, plan) = LoadEngine(HomeFeedTestHost.Load());

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-assigned-out-of-budget",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Assignment = new[] { "exp_ramped_early/t", "exp_mmr_diversity/mmr", "exp_mmr_diversity/control", "exp_quality_weight/strong" },
            Deadline = TimeSpan.Zero,
            Debug = new DebugOptions { Record = true },
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("assignment took the whole budget", result.Error);
        Assert.Equal(new[] { "exp_quality_weight/strong" }, result.Experiments.Assigned);
        Assert.Empty(result.Experiments.Triggered);
        Assert.Equal(
            new[]
            {
                new IgnoredArm("exp_mmr_diversity/control", IgnoredArmReason.LayerConflict),
                new IgnoredArm("exp_mmr_diversity/mmr", IgnoredArmReason.LayerConflict),
                new IgnoredArm("exp_ramped_early/t", IgnoredArmReason.Unknown),
            },
            result.Experiments.Ignored);
        Assert.Equal(plan.ManifestId, result.ManifestId);
        Assert.Null(result.Recording);

        AssertTagRules(capture.Measurements);
        var ignored = capture.Of(StrataRecMetrics.Names.RequestArmsIgnored);
        Assert.Equal(1, ignored.Where(m => Tag(m, StrataRecTags.Outcome) == "unknown").Sum(m => m.Value));
        Assert.Equal(2, ignored.Where(m => Tag(m, StrataRecTags.Outcome) == "layer_conflict").Sum(m => m.Value));
        Assert.Single(capture.Of(StrataRecMetrics.Names.RequestErrors));
        Assert.Equal(result.Experiments.Assigned, capture.Of(StrataRecMetrics.Names.ArmRequestErrors).Select(m => Tag(m, StrataRecTags.Arm)));
    }

    [Fact]
    public async Task ARequestRejectedBeforeAssignment_HasAnEmptyRecord()
    {
        var (engine, _) = LoadEngine(HomeFeedTestHost.Load());

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-bad-force",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Assignment = new[] { "exp_quality_weight/strong" },
            Deadline = HomeFeedTestHost.Deadline,
            Debug = new DebugOptions { ForceArms = new[] { "exp_nope/t" } },
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Experiments.Assigned);
        Assert.Empty(result.Experiments.Ignored);
        Assert.Equal(string.Empty, result.ManifestId);
    }

    [Fact]
    public async Task FailingFeatureSource_CountsTheErrorTheFallbackAndADegradedRequest()
    {
        using var capture = new MetricCapture();
        var (engine, _) = LoadEngine(HomeFeedTestHost.LoadModified(FailAuthorStore));

        var result = await engine.ExecuteAsync(Request("req-degraded"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        AssertTagRules(capture.Measurements);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.IoErrors),
            m => Tag(m, StrataRecTags.IoRole) == "fetch" && Tag(m, StrataRecTags.IoTarget) == "author_store" && Tag(m, StrataRecTags.Outcome) == "error");
        Assert.Single(capture.Of(StrataRecMetrics.Names.NodeOutcomes),
            m => Tag(m, StrataRecTags.Node) == "ctr_ranker" && Tag(m, StrataRecTags.Outcome) == "fallback");
        Assert.DoesNotContain(capture.Of(StrataRecMetrics.Names.IoDuration), m => Tag(m, StrataRecTags.IoRole) == "model");
        Assert.Single(capture.Of(StrataRecMetrics.Names.RequestDegraded));
        Assert.Equal(Arms.Order(), capture.Of(StrataRecMetrics.Names.ArmRequestDegraded).Select(m => Tag(m, StrataRecTags.Arm)).Order());
        Assert.Empty(capture.Of(StrataRecMetrics.Names.RequestErrors));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("recall")]
    public async Task CallerCancellation_IsNotADependencyTimeout_ButARealTimeoutIs(string slow)
    {
        using (var capture = new MetricCapture())
        {
            var (engine, _) = LoadEngine(HomeFeedTestHost.LoadModified(root => SlowDown(root, slow, tightTimeout: false)));
            var node = slow == "model" ? "ctr_ranker" : "recall";
            using var cancel = new CancelOnceCallStarts(slow == "model" ? "model ctr_model" : "recall popular", TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ExecuteAsync(Request("req-cancelled"), cancel.Token));

            Assert.Empty(capture.Of(StrataRecMetrics.Names.IoErrors));
            Assert.DoesNotContain(capture.Of(StrataRecMetrics.Names.IoDuration), m => Tag(m, StrataRecTags.IoRole) == "model");
            if (slow == "recall")
            {
                Assert.DoesNotContain(capture.Of(StrataRecMetrics.Names.IoDuration), m => Tag(m, StrataRecTags.IoRole) == "recall");
            }

            Assert.Single(capture.Of(StrataRecMetrics.Names.RequestErrors));

            Assert.Single(capture.Of(StrataRecMetrics.Names.NodeOutcomes), m => Tag(m, StrataRecTags.Node) == node && Tag(m, StrataRecTags.Outcome) == "aborted");
            // Cut off 300 ms after its slow call started, in seconds; the upper bound is a generous watchdog.
            Assert.InRange(Assert.Single(capture.Of(StrataRecMetrics.Names.NodeDuration), m => Tag(m, StrataRecTags.Node) == node).Value, 0.2, 30);
            Assert.Single(capture.Of(StrataRecMetrics.Names.NodeRowsIn), m => Tag(m, StrataRecTags.Node) == node);
        }

        using (var capture = new MetricCapture())
        {
            var (engine, _) = LoadEngine(HomeFeedTestHost.LoadModified(root => SlowDown(root, slow, tightTimeout: true)));

            await engine.ExecuteAsync(Request("req-timeout"), TestContext.Current.CancellationToken);

            var errors = capture.Of(StrataRecMetrics.Names.IoErrors).Where(m => Tag(m, StrataRecTags.IoRole) == (slow == "model" ? "model" : "recall")).ToList();
            Assert.NotEmpty(errors);
            Assert.All(errors, m => Assert.Equal("timeout", Tag(m, StrataRecTags.Outcome)));
        }
    }

    [Theory]
    [InlineData("model")]
    [InlineData("recall")]
    public async Task ClientTimeoutExceptions_AreTimeouts(string client)
    {
        using var capture = new MetricCapture();
        var host = HomeFeedTestHost.Load();
        var environment = new PlanEnvironment
        {
            BaseDirectory = host.Environment.BaseDirectory,
            Bindings = client switch
            {
                "model" => TestBindings.With(new TimingOutModels(new LinearModelClientFactory())),
                "recall" => TestBindings.With(
                    new TimingOutSources(new MemoryListServiceFactory()), new TimingOutSources(new MemoryVectorsServiceFactory())),
                _ => PlanEnvironment.DefaultBindings(),
            },
        };
        var engine = new RecEngine(environment);
        Assert.True(engine.Load(host.Request).Applied);

        await engine.ExecuteAsync(Request("req-client-timeout"), TestContext.Current.CancellationToken);

        var errors = capture.Of(StrataRecMetrics.Names.IoErrors).Where(m => Tag(m, StrataRecTags.IoRole) == (client == "model" ? "model" : "recall")).ToList();
        Assert.Equal(client == "model" ? 1 : 2, errors.Count);
        Assert.All(errors, m => Assert.Equal("timeout", Tag(m, StrataRecTags.Outcome)));
    }

    private sealed class TimingOutModels(IBindingFactory<IServiceClient> inner) : IBindingFactory<IServiceClient>
    {
        public string Type => inner.Type;

        public Type SettingsType => inner.SettingsType;

        public IServiceClient Create(BindingContext context) => context.Name == "ctr_model" ? new Client(inner.Create(context)) : inner.Create(context);

        private sealed class Client(IServiceClient inner) : IServiceClient
        {
            public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
                inner.DescribeAsync(model, version, cancellationToken);

            public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) =>
                throw new TimeoutException("backend timed out");
        }
    }

    private sealed class TimingOutSources(IBindingFactory<IServiceClient> inner) : IBindingFactory<IServiceClient>
    {
        public string Type => inner.Type;

        public Type SettingsType => inner.SettingsType;

        public IServiceClient Create(BindingContext context)
        {
            _ = inner.Create(context); // its settings, checked as the source's
            return new Source();
        }

        private sealed class Source : IServiceClient
        {
            public ValueTask<ServiceStatus> CallAsync(ServiceCall query, IServiceResponse sink, CancellationToken cancellationToken) =>
                throw new TimeoutException("backend timed out");
        }
    }

    internal static void SlowDown(string root, string what, bool tightTimeout)
    {
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        var text = File.ReadAllText(bindings);
        string[] clients = what == "model" ? [CtrModelClient] : ["\"client\": \"memory_list@1\",", "\"client\": \"memory_vectors@1\","];
        var edited = text;
        foreach (var client in clients)
        {
            edited = edited.Replace(client, client + " \"latency_ms\": 60000," + (tightTimeout ? " \"timeout_ms\": 50," : string.Empty), StringComparison.Ordinal);
        }

        Assert.NotEqual(text, edited);
        File.WriteAllText(bindings, edited);
    }

    [Fact]
    public async Task LogPipeline_CountsBatchesPerTapAndDrainsItsQueueDepth()
    {
        using var capture = new MetricCapture();
        var plan = HomeFeedTestHost.Load().Plan();
        var sink = new InMemoryLogSink();
        await using (var logs = new LogPipeline(sink, producerId: "test/1"))
        {
            await new PipelineExecutor(logs: logs).ExecuteAsync(plan, HomeFeedTestHost.NewRequest(), TestContext.Current.CancellationToken);
            await logs.FlushAsync(TestContext.Current.CancellationToken);
        }

        AssertTagRules(capture.Measurements);
        var delivered = capture.Of(StrataRecMetrics.Names.LogBatches).Where(m => Tag(m, StrataRecTags.Outcome) == "delivered")
            .GroupBy(m => Tag(m, StrataRecTags.Tap)).ToDictionary(g => g.Key!, g => g.Sum(m => m.Value));
        Assert.Equal(sink.Snapshot().GroupBy(b => b.TapId).ToDictionary(g => g.Key, g => (double)g.Count()), delivered);
        foreach (var depth in capture.Of(StrataRecMetrics.Names.LogQueueDepth).GroupBy(m => Tag(m, StrataRecTags.LogDelivery)))
        {
            Assert.Equal(0, depth.Sum(m => m.Value));
        }

        Assert.Empty(capture.Of(StrataRecMetrics.Names.LogDurableOverflow));
    }

    [Fact]
    public void Histograms_AdviseBucketsGrowingByAFixedRatio()
    {
        var seconds = StrataRecMetrics.NodeDuration.Advice!.HistogramBucketBoundaries!;
        Assert.Equal(0.0001, seconds[0]);
        for (var i = 1; i < seconds.Count; i++)
        {
            Assert.InRange(seconds[i] / seconds[i - 1], 1.40, 1.43); // √2, rounded to three significant digits
        }

        Assert.True(seconds[^1] >= 10);
        var rows = StrataRecMetrics.NodeRowsIn.Advice!.HistogramBucketBoundaries!;
        Assert.All(Enumerable.Range(1, rows.Count - 1), i => Assert.Equal(2 * rows[i - 1], rows[i]));
    }

    internal static (RecEngine Engine, ExecutionPlan Plan) LoadEngine(HomeFeedTestHost host, PipelineExecutor? executor = null)
    {
        var engine = executor is null ? new RecEngine(host.Environment) : new RecEngine(host.Environment, executor);
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        Assert.True(engine.TryGetPipeline("home_feed", out var loaded));
        return (engine, loaded.Plan);
    }

    internal static RecRequest Request(string requestId) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Assignment = Arms,
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Trace = true },
    };

    internal const string CtrModelClient = "\"ctr_model\": {\n      \"client\": \"linear@1\",";

    internal static void FailModel(string root)
    {
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        var text = File.ReadAllText(bindings);
        // The CTR model only: the sample's other linear@1 service (engagement_model) is not what these tests are about.
        var edited = text.Replace(CtrModelClient, CtrModelClient + " \"fail\": true,", StringComparison.Ordinal);
        Assert.NotEqual(text, edited);
        File.WriteAllText(bindings, edited);
    }

    internal static void FailAuthorStore(string root) =>
        SampleBindings.Rebind(root, "author_store", $$"""{ "connector": "memory@1", "data": {}, "fail": true, "timeout_ms": {{HomeFeedTestHost.UntimedMs}} }""");

    private static void AssertTagRules(IEnumerable<Measured> measurements)
    {
        foreach (var m in measurements)
        {
            Assert.All(m.Tags.Keys, key => Assert.Contains(key, BoundedTagKeys));
            Assert.All(m.Tags.Values, value => Assert.IsType<string>(value));
            Assert.DoesNotContain(m.Tags.Values, value => value is string s && (s.StartsWith("req-", StringComparison.Ordinal) || s == "1001" || s.StartsWith("s-", StringComparison.Ordinal)));
            if (m.Instrument.EndsWith(".duration", StringComparison.Ordinal) || m.Instrument.Contains(".rows.", StringComparison.Ordinal))
            {
                Assert.False(m.Tags.ContainsKey(StrataRecTags.Outcome), $"{m.Instrument} crosses a distribution with an outcome");
            }

            if (m.Tags.ContainsKey(StrataRecTags.Arm))
            {
                Assert.StartsWith("stratarec.arm.request.", m.Instrument);
            }
        }
    }

    private static string? Tag(Measured m, string key) => m.Tags.TryGetValue(key, out var value) ? value as string : null;

    private static bool TagsEqual(Measured m, IReadOnlyDictionary<string, object?> expected) =>
        m.Tags.Count == expected.Count && expected.All(e => m.Tags.TryGetValue(e.Key, out var v) && Equals(v, e.Value));
}

[Collection(ProcessWideListenersCollection.Name)]
public sealed class TracingTests
{
    [Fact]
    public async Task RecordedRequest_HasNodeAndIoChildSpans_SampledOutRequestHasNone()
    {
        var record = true;
        var started = new ConcurrentQueue<Activity>();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name != StrataRecTracing.RequestSpanName || record ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData,
            ActivityStarted = started.Enqueue,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var (engine, plan) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var result = await engine.ExecuteAsync(MetricsTests.Request("req-traced"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var spans = stopped.ToList();
        var request = Assert.Single(spans, s => s.OperationName == StrataRecTracing.RequestSpanName);
        Assert.True(request.Recorded);
        Assert.Equal("req-traced", request.GetTagItem(StrataRecTags.RequestId));
        Assert.Equal(MetricsTests.Arms.Order(), ((string[])request.GetTagItem(StrataRecTags.ArmsAssigned)!).Order());

        var arms = plan.ArmsFromIds(MetricsTests.Arms);
        var nodes = spans.Where(s => s.GetTagItem(StrataRecTags.Node) is not null).ToList();
        Assert.Equal(result.Trace!.Select(t => t.NodeId), nodes.Select(s => s.OperationName));
        foreach (var span in nodes)
        {
            Assert.Equal(request.SpanId, span.ParentSpanId);
            var slot = plan.Timeline.Single(s => s.NodeId == span.OperationName);
            Assert.Equal(slot.Select(arms)!.DefinitionHash, span.GetTagItem(StrataRecTags.Variant));
            Assert.Equal("succeeded", span.GetTagItem(StrataRecTags.Outcome));
            var outcome = result.Trace!.Single(t => t.NodeId == span.OperationName);
            Assert.Equal(outcome.RowsIn, span.GetTagItem(StrataRecTags.RowsIn));
            Assert.Equal(outcome.RowsOut, span.GetTagItem(StrataRecTags.RowsOut));
        }

        // Triggered experiments: fusion reads quality_weight, the MMR node exists only in its arm.
        Assert.Contains("exp_quality_weight", Triggered(nodes.Single(s => s.OperationName == "fusion")));
        Assert.Contains("exp_mmr_diversity", Triggered(nodes.Single(s => s.OperationName == "mmr_diversifier")));
        Assert.Empty(Triggered(nodes.Single(s => s.OperationName == "safety_filter")));

        var recall = nodes.Single(s => s.OperationName == "recall");
        var underRecall = spans.Where(s => s.ParentSpanId == recall.SpanId).Select(s => s.OperationName).ToList();
        Assert.Equal(new[] { "recall popular", "recall two_tower" }, underRecall.Where(n => n.StartsWith("recall ", StringComparison.Ordinal)).Order());
        Assert.Contains("fetch item_store", underRecall);
        var ranker = nodes.Single(s => s.OperationName == "ctr_ranker");
        Assert.Contains(spans, s => s.OperationName == "model ctr_model" && s.ParentSpanId == ranker.SpanId && (string?)s.GetTagItem(StrataRecTags.Outcome) == "ok");
        Assert.Contains(spans, s => s.OperationName == "fetch item_store" && s.Kind == ActivityKind.Client && s.GetTagItem(StrataRecTags.Keys) is int);

        record = false;
        started.Clear();
        var dropped = await engine.ExecuteAsync(MetricsTests.Request("req-sampled-out"), TestContext.Current.CancellationToken);

        Assert.True(dropped.Succeeded, dropped.Error);
        var only = Assert.Single(started);
        Assert.Equal(StrataRecTracing.RequestSpanName, only.OperationName);
        Assert.False(only.Recorded);
    }

    [Fact]
    public async Task FallbackOnlyArm_IsAttributedOnThePositionSpan_WithTheFallbackVariant()
    {
        using var spans = new SpanCapture();
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            MetricsTests.FailModel(root);
            var experiment = Path.Combine(root, "experiments", "exp_quality_weight.jsonc");
            var text = File.ReadAllText(experiment);
            var edited = text.Replace("\"strong\": { \"params\": { \"quality_weight\": 0.8, \"final_top_k\": 8 } }",
                "\"strong\": { \"nodes\": { \"ctr_backup\": { \"config\": { \"expr\": \"col.item.smoothed_ctr * 0.5\" } } } }", StringComparison.Ordinal);
            Assert.NotEqual(text, edited);
            File.WriteAllText(experiment, edited);
        });
        var (engine, plan) = MetricsTests.LoadEngine(host);

        var result = await engine.ExecuteAsync(MetricsTests.Request("req-fallback-arm"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("exp_quality_weight", result.Experiments.Triggered);
        var ranker = Assert.Single(spans.Stopped, s => s.OperationName == "ctr_ranker");
        Assert.Equal("fallback", ranker.GetTagItem(StrataRecTags.Outcome));
        Assert.Equal("ctr_backup", ranker.GetTagItem(StrataRecTags.Fallback));
        Assert.Contains("ctr_model", Assert.IsType<string>(ranker.GetTagItem(StrataRecTags.OutcomeDetail)));
        Assert.Equal(ActivityStatusCode.Unset, ranker.Status); // the request succeeded: a fallback is no error
        Assert.Contains("exp_quality_weight", Triggered(ranker));
        var backup = plan.SelectFallback(plan.Timeline.Single(t => t.NodeId == "ctr_ranker").Select(plan.ArmsFromIds(MetricsTests.Arms))!, plan.ArmsFromIds(MetricsTests.Arms))!;
        Assert.Equal(backup.DefinitionHash, ranker.GetTagItem(StrataRecTags.FallbackVariant));
        Assert.NotEqual(plan.SelectFallback(plan.Timeline.Single(t => t.NodeId == "ctr_ranker").Select(RequestArms.None)!, RequestArms.None)!.DefinitionHash, backup.DefinitionHash);
    }

    [Fact]
    public async Task AbortedRequest_RootSpanKeepsTheManifestAndAssignedArms()
    {
        using var spans = new SpanCapture();
        var (engine, plan) = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root => MetricsTests.SlowDown(root, "model", tightTimeout: false)));
        using var cancel = new CancelOnceCallStarts("model ctr_model", TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ExecuteAsync(MetricsTests.Request("req-aborted"), cancel.Token));

        var root = Assert.Single(spans.Stopped, s => s.OperationName == StrataRecTracing.RequestSpanName);
        Assert.Equal(ActivityStatusCode.Error, root.Status);
        Assert.Equal(plan.ManifestId, root.GetTagItem(StrataRecTags.ManifestId));
        Assert.Equal(MetricsTests.Arms.Order(), ((string[])root.GetTagItem(StrataRecTags.ArmsAssigned)!).Order());
        var ranker = Assert.Single(spans.Stopped, s => s.OperationName == "ctr_ranker");
        Assert.Equal("aborted", ranker.GetTagItem(StrataRecTags.Outcome));
    }

    private static string[] Triggered(Activity span) => span.GetTagItem(StrataRecTags.ExperimentsTriggered) as string[] ?? Array.Empty<string>();

    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public void Dispose() => _listener.Dispose();
    }
}

[Collection(ProcessWideListenersCollection.Name)]
public sealed class EventLogTests
{
    [Fact]
    public async Task ManyFetchFailures_ProduceOneSummaryPerWindow_AndOneHealthChange()
    {
        var logging = new ListLoggerFactory();
        using var events = new RequestEventLog(logging.CreateLogger("test"), autoFlush: false);
        var plan = HomeFeedTestHost.LoadModified(MetricsTests.FailAuthorStore).Plan();
        var executor = new PipelineExecutor(null, null, events);

        for (var i = 0; i < 50; i++)
        {
            var result = await executor.ExecuteAsync(plan, Request(i), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Error);
        }

        Assert.Empty(logging.Entries);
        events.Flush();
        var first = logging.Entries.ToList();

        var fetch = Assert.Single(first, e => Event(e) == nameof(RequestEvent.IoFailed));
        Assert.Equal("fetch author_store", fetch.State["Subject"]);
        Assert.Equal(50L, fetch.State["Count"]);
        Assert.StartsWith("req-", (string)fetch.State["SampleRequestId"]!);
        Assert.Contains("configured to fail", (string)fetch.State["SampleError"]!);
        var fallback = Assert.Single(first, e => Event(e) == nameof(RequestEvent.NodeFallback));
        Assert.Equal("home_feed/ctr_ranker", fallback.State["Subject"]);
        Assert.Equal(50L, fallback.State["Count"]);
        var failing = Assert.Single(first, e => e.EventId.Id == 2002);
        Assert.Equal(LogLevel.Error, failing.Level);
        Assert.Equal(("fetch", "author_store"), (failing.State["Role"], failing.State["Name"]));
        Assert.Equal(3, first.Count);

        for (var i = 50; i < 80; i++)
        {
            await executor.ExecuteAsync(plan, Request(i), TestContext.Current.CancellationToken);
        }

        logging.Entries.Clear();
        events.Flush();
        var second = logging.Entries.ToList();
        Assert.Equal(2, second.Count);
        Assert.All(second, e => Assert.Equal(30L, e.State["Count"]));

        events.Flush();
        Assert.Equal(2, logging.Entries.Count);
    }

    [Fact]
    public void Engine_LogsPlanAcceptedAndCompileFailures()
    {
        var logging = new ListLoggerFactory();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, loggerFactory: logging);

        Assert.True(engine.Load(host.Request).Applied);
        Assert.False(engine.Load(host.Request, sequence: 0).Applied);
        var broken = HomeFeedTestHost.LoadModified(root =>
        {
            var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
            File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace("col.item.smoothed_ctr\" }", "col.item.no_such_feature\" }", StringComparison.Ordinal));
        });
        Assert.False(engine.Load(broken.Request).Applied);

        // The sample's local tables (author_store, item_country_store) are loaded once each, before the first plan is
        // applied; the second load takes them over.
        var entries = logging.Entries.ToList();
        Assert.Equal(new[] { 1201, 1201, 1001, 1004, 1002 }, entries.Select(e => e.EventId.Id));
        Assert.Equal(LogLevel.Error, entries[4].Level);
        Assert.Contains("no_such_feature", (string)entries[4].State["FirstError"]!);
    }

    private static string? Event(LogEntry entry) => entry.State.TryGetValue("Event", out var value) ? value?.ToString() : null;

    private static PipelineRequest Request(int i) => new()
    {
        RequestId = "req-" + i,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
    };
}
