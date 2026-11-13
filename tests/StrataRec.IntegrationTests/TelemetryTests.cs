using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Hosting;
using StrataRec.Logging;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;

namespace StrataRec.IntegrationTests;

/// <summary>Metrics (design 13.1) recorded by a home_feed request, read back through a <c>MeterListener</c>.</summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class MetricsTests
{
    internal static readonly string[] Arms = { "exp_quality_weight/strong", "exp_mmr_diversity/mmr" };

    /// <summary>Every tag key a metric may carry (design 13.1 rule 1); ids are never among them.</summary>
    private static readonly HashSet<string> BoundedTagKeys = new(StringComparer.Ordinal)
    {
        StrataRecTags.Pipeline, StrataRecTags.Node, StrataRecTags.Source, StrataRecTags.SourceKind, StrataRecTags.Model,
        StrataRecTags.Outcome, StrataRecTags.Arm, StrataRecTags.Column, StrataRecTags.Tap, StrataRecTags.LogDelivery, StrataRecTags.Entity,
    };

    [Fact]
    public async Task HomeFeedRequest_RecordsTheInstrumentsWithBoundedTagsAndPlausibleValues()
    {
        using var capture = new MetricCapture();
        var (engine, plan) = LoadEngine(HomeFeedTestHost.Load());

        var watch = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(Request("req-metrics"));
        watch.Stop();

        Assert.True(result.Succeeded, result.Error);
        AssertTagRules(capture.Measurements);
        var pipeline = new Dictionary<string, object?> { [StrataRecTags.Pipeline] = "home_feed" };

        // Plan lifecycle.
        Assert.Contains(capture.Of(StrataRecMetrics.Names.PlanCompileDuration), m => m.Value > 0 && TagsEqual(m, pipeline));
        Assert.Single(capture.Of(StrataRecMetrics.Names.PlanLoads), m => Tag(m, StrataRecTags.Outcome) == "applied");
        Assert.Equal(plan.Nodes.Length, Assert.Single(capture.Of(StrataRecMetrics.Names.PlanVariants)).Value);
        Assert.Equal(1, Assert.Single(capture.Of(StrataRecMetrics.Names.PlanSequence)).Value);

        // Request level: once for the pipeline, once per assigned arm (and nowhere else).
        var request = Assert.Single(capture.Of(StrataRecMetrics.Names.RequestDuration));
        Assert.True(TagsEqual(request, pipeline));
        // In seconds, and measured around the execution: no fixed bound a slow machine could pass.
        Assert.InRange(request.Value, result.ElapsedMs / 1000, watch.Elapsed.TotalSeconds);
        var perArm = capture.Of(StrataRecMetrics.Names.ArmRequestDuration);
        Assert.Equal(Arms.Order(), perArm.Select(m => Tag(m, StrataRecTags.Arm)).Order());
        Assert.All(perArm, m => Assert.Equal(request.Value, m.Value));
        Assert.Empty(capture.Of(StrataRecMetrics.Names.RequestErrors));
        Assert.Empty(capture.Of(StrataRecMetrics.Names.ArmRequestErrors));

        // Nodes: one outcome per position with a node, duration and rows for each that ran, matching the trace.
        var trace = result.Trace!;
        var outcomes = capture.Of(StrataRecMetrics.Names.NodeOutcomes);
        Assert.Equal(trace.Select(t => t.NodeId).Order(), outcomes.Select(m => Tag(m, StrataRecTags.Node)).Order());
        Assert.All(outcomes, m => Assert.Equal("succeeded", Tag(m, StrataRecTags.Outcome)));
        foreach (var node in trace)
        {
            var tags = new Dictionary<string, object?> { [StrataRecTags.Pipeline] = "home_feed", [StrataRecTags.Node] = node.NodeId };
            var seconds = Assert.Single(capture.Of(StrataRecMetrics.Names.NodeDuration), m => TagsEqual(m, tags)).Value;
            Assert.InRange(seconds, (node.DurationMs / 1000) - 1e-9, (node.DurationMs / 1000) + 1e-9); // the trace's time, in seconds
            Assert.Equal(node.RowsIn, Assert.Single(capture.Of(StrataRecMetrics.Names.NodeRowsIn), m => TagsEqual(m, tags)).Value);
            Assert.Equal(node.RowsOut, Assert.Single(capture.Of(StrataRecMetrics.Names.NodeRowsOut), m => TagsEqual(m, tags)).Value);
        }

        // Data sources: both candidate sources and the fetched feature sources the request reads; key counts respect
        // max_batch. The local author_store makes no source calls.
        var sources = capture.Of(StrataRecMetrics.Names.SourceDuration)
            .Select(m => (Tag(m, StrataRecTags.SourceKind), Tag(m, StrataRecTags.Source))).ToHashSet();
        Assert.Superset(new HashSet<(string?, string?)> { ("candidate", "two_tower"), ("candidate", "popular"), ("feature", "item_store") }, sources);
        Assert.DoesNotContain(("feature", "author_store"), sources);
        var itemKeys = capture.Of(StrataRecMetrics.Names.SourceKeys).Where(m => Tag(m, StrataRecTags.Source) == "item_store").ToList();
        Assert.NotEmpty(itemKeys);
        Assert.All(itemKeys, m => Assert.InRange(m.Value, 1, 25));
        Assert.Empty(capture.Of(StrataRecMetrics.Names.SourceErrors));

        // Local table: the load the engine waited for (11 authors in the snapshot) and the keys the request looked up.
        var author = new Dictionary<string, object?> { [StrataRecTags.Source] = "author_store", [StrataRecTags.Entity] = "author" };
        Assert.Contains(capture.Of(StrataRecMetrics.Names.LocalRecords), m => TagsEqual(m, author) && m.Value == 11);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.LocalBytes), m => TagsEqual(m, author) && m.Value > 0);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.LocalLoadDuration), m => TagsEqual(m, author) && m.Value > 0);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.LocalLoadLastSuccess), m => TagsEqual(m, author) && m.Value > 1.7e9);
        var authorKeys = capture.Of(StrataRecMetrics.Names.LocalKeys).Where(m => Tag(m, StrataRecTags.Source) == "author_store").ToList();
        Assert.Contains(authorKeys, m => Tag(m, StrataRecTags.Outcome) == "hit" && m.Value > 0);
        Assert.All(authorKeys, m => Assert.Contains(Tag(m, StrataRecTags.Outcome), new[] { "hit", "miss" }));

        // Model calls: the 30 coarse survivors fit one batch.
        Assert.Single(capture.Of(StrataRecMetrics.Names.ModelDuration), m => Tag(m, StrataRecTags.Model) == "ctr_model");

        // Memory.
        Assert.True(Assert.Single(capture.Of(StrataRecMetrics.Names.RequestMemoryRented)).Value > 0);
    }

    [Fact]
    public async Task FailingFeatureSource_CountsTheErrorTheFallbackAndADegradedRequest()
    {
        using var capture = new MetricCapture();
        var (engine, _) = LoadEngine(HomeFeedTestHost.LoadModified(FailAuthorStore));

        var result = await engine.ExecuteAsync(Request("req-degraded"));

        Assert.True(result.Succeeded, result.Error);
        AssertTagRules(capture.Measurements);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.SourceErrors),
            m => Tag(m, StrataRecTags.Source) == "author_store" && Tag(m, StrataRecTags.Outcome) == "error");
        Assert.Single(capture.Of(StrataRecMetrics.Names.NodeOutcomes),
            m => Tag(m, StrataRecTags.Node) == "ctr_ranker" && Tag(m, StrataRecTags.Outcome) == "fallback");
        Assert.Single(capture.Of(StrataRecMetrics.Names.ModelFallbacks), m => Tag(m, StrataRecTags.Model) == "ctr_model");
        Assert.Empty(capture.Of(StrataRecMetrics.Names.ModelDuration)); // the model was never called
        Assert.Single(capture.Of(StrataRecMetrics.Names.RequestDegraded));
        Assert.Equal(Arms.Order(), capture.Of(StrataRecMetrics.Names.ArmRequestDegraded).Select(m => Tag(m, StrataRecTags.Arm)).Order());
        Assert.Empty(capture.Of(StrataRecMetrics.Names.RequestErrors));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("recall")]
    public async Task CallerCancellation_IsNotADependencyTimeout_ButARealTimeoutIs(string slow)
    {
        // The caller giving up is not the dependency's fault: no error, no latency sample, no health evidence.
        using (var capture = new MetricCapture())
        {
            var (engine, _) = LoadEngine(HomeFeedTestHost.LoadModified(root => SlowDown(root, slow, tightTimeout: false)));
            var node = slow == "model" ? "ctr_ranker" : "recall";
            using var cancel = new CancelOnceCallStarts(slow == "model" ? "model ctr_model" : "recall popular", TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ExecuteAsync(Request("req-cancelled"), cancel.Token));

            Assert.Empty(capture.Of(StrataRecMetrics.Names.ModelErrors));
            Assert.Empty(capture.Of(StrataRecMetrics.Names.ModelDuration));
            Assert.Empty(capture.Of(StrataRecMetrics.Names.SourceErrors));
            if (slow == "recall")
            {
                Assert.DoesNotContain(capture.Of(StrataRecMetrics.Names.SourceDuration), m => Tag(m, StrataRecTags.SourceKind) == "candidate");
            }

            Assert.Single(capture.Of(StrataRecMetrics.Names.RequestErrors)); // the request itself failed

            // Review 6 #5: the node the caller cut off still has its outcome, duration and rows.
            Assert.Single(capture.Of(StrataRecMetrics.Names.NodeOutcomes), m => Tag(m, StrataRecTags.Node) == node && Tag(m, StrataRecTags.Outcome) == "aborted");
            // Cut off 300 ms after its slow call started, in seconds; the upper bound is a generous watchdog.
            Assert.InRange(Assert.Single(capture.Of(StrataRecMetrics.Names.NodeDuration), m => Tag(m, StrataRecTags.Node) == node).Value, 0.2, 30);
            Assert.Single(capture.Of(StrataRecMetrics.Names.NodeRowsIn), m => Tag(m, StrataRecTags.Node) == node);
        }

        // The same dependency cut off by its own timeout (model binding / node budget, source binding) is a timeout.
        using (var capture = new MetricCapture())
        {
            var (engine, _) = LoadEngine(HomeFeedTestHost.LoadModified(root => SlowDown(root, slow, tightTimeout: true)));

            await engine.ExecuteAsync(Request("req-timeout"));

            var errors = slow == "model"
                ? capture.Of(StrataRecMetrics.Names.ModelErrors)
                : capture.Of(StrataRecMetrics.Names.SourceErrors).Where(m => Tag(m, StrataRecTags.SourceKind) == "candidate").ToList();
            Assert.NotEmpty(errors);
            Assert.All(errors, m => Assert.Equal("timeout", Tag(m, StrataRecTags.Outcome)));
        }
    }

    [Theory]
    [InlineData("model")]
    [InlineData("recall")]
    public async Task ClientTimeoutExceptions_AreTimeouts(string client)
    {
        // Review 6 #6: a client's own transport timeout is a timeout, as it already was for feature sources.
        using var capture = new MetricCapture();
        var host = HomeFeedTestHost.Load();
        var environment = new PlanEnvironment
        {
            BaseDirectory = host.Environment.BaseDirectory,
            Models = client == "model" ? new ModelClientRegistry().Add(new TimingOutModels(new LinearModelClientFactory())) : ModelClientRegistry.CreateDefault(),
            CandidateSources = client == "recall"
                ? new CandidateSourceRegistry().Add(new TimingOutSources(new MemoryListCandidateSourceFactory()))
                : CandidateSourceRegistry.CreateDefault(),
        };
        var engine = new RecEngine(environment);
        Assert.True(engine.Load(host.Request).Applied);

        await engine.ExecuteAsync(Request("req-client-timeout"));

        var errors = client == "model"
            ? capture.Of(StrataRecMetrics.Names.ModelErrors)
            : capture.Of(StrataRecMetrics.Names.SourceErrors).Where(m => Tag(m, StrataRecTags.SourceKind) == "candidate").ToList();
        Assert.Equal(client == "model" ? 1 : 2, errors.Count);
        Assert.All(errors, m => Assert.Equal("timeout", Tag(m, StrataRecTags.Outcome)));
    }

    private sealed class TimingOutModels(IModelClientFactory inner) : IModelClientFactory
    {
        public string ClientType => inner.ClientType;

        public IModelClient Create(BindingContext context) => new Client(inner.Create(context));

        private sealed class Client(IModelClient inner) : IModelClient
        {
            public string ClientType => inner.ClientType;

            public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
                inner.GetSignatureAsync(model, version, cancellationToken);

            public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
                throw new TimeoutException("backend timed out");
        }
    }

    private sealed class TimingOutSources(ICandidateSourceFactory inner) : ICandidateSourceFactory
    {
        public string ClientType => inner.ClientType;

        public ICandidateSource Create(BindingContext context) =>
            new Source(inner.Create(context));

        private sealed class Source(ICandidateSource inner) : ICandidateSource
        {
            public string ClientType => inner.ClientType;

            public ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken) =>
                throw new TimeoutException("backend timed out");
        }
    }

    /// <summary>
    /// Makes the model or both candidate sources take a minute, longer than any test waits for them; with a 50 ms timeout
    /// of their own (in their bindings), or with none shorter than the request (the untimed sample's).
    /// </summary>
    internal static void SlowDown(string root, string what, bool tightTimeout)
    {
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        var text = File.ReadAllText(bindings);
        var client = what == "model" ? "\"client\": \"linear@1\"," : "\"client\": \"memory_list@1\",";
        var edited = text.Replace(client, client + " \"latency_ms\": 60000," + (tightTimeout ? " \"timeout_ms\": 50," : string.Empty), StringComparison.Ordinal);
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
            await new PipelineExecutor(logs: logs).ExecuteAsync(plan, HomeFeedTestHost.NewRequest());
            await logs.FlushAsync();
        }

        AssertTagRules(capture.Measurements);
        var delivered = capture.Of(StrataRecMetrics.Names.LogBatches).Where(m => Tag(m, StrataRecTags.Outcome) == "delivered")
            .GroupBy(m => Tag(m, StrataRecTags.Tap)).ToDictionary(g => g.Key!, g => g.Sum(m => m.Value));
        Assert.Equal(sink.Snapshot().GroupBy(b => b.TapId).ToDictionary(g => g.Key, g => (double)g.Count()), delivered);
        foreach (var depth in capture.Of(StrataRecMetrics.Names.LogQueueDepth).GroupBy(m => Tag(m, StrataRecTags.LogDelivery)))
        {
            Assert.Equal(0, depth.Sum(m => m.Value)); // every queued batch was written
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
        var engine = new RecEngine(host.Environment, executor: executor);
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

    internal static void FailModel(string root)
    {
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        var text = File.ReadAllText(bindings);
        var edited = text.Replace("\"client\": \"linear@1\",", "\"client\": \"linear@1\", \"fail\": true,", StringComparison.Ordinal);
        Assert.NotEqual(text, edited);
        File.WriteAllText(bindings, edited);
    }

    /// <summary>Binds <c>author_store</c> (local in the sample) to a fetched source that fails every call, never by timing out.</summary>
    internal static void FailAuthorStore(string root) =>
        SampleBindings.Rebind(root, "author_store", $$"""{ "connector": "memory@1", "data": {}, "fail": true, "timeout_ms": {{HomeFeedTestHost.UntimedMs}} }""");

    /// <summary>Design 13.1 label rules: bounded string tags only; durations never carry outcomes; arms only on request-level instruments.</summary>
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

/// <summary>Spans (design 13.2): node and I/O children exist only for recorded requests.</summary>
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

        var result = await engine.ExecuteAsync(MetricsTests.Request("req-traced"));

        Assert.True(result.Succeeded, result.Error);
        var spans = stopped.ToList();
        var request = Assert.Single(spans, s => s.OperationName == StrataRecTracing.RequestSpanName);
        Assert.True(request.Recorded);
        Assert.Equal("req-traced", request.GetTagItem(StrataRecTags.RequestId));
        Assert.Equal(MetricsTests.Arms.Order(), ((string[])request.GetTagItem(StrataRecTags.ArmsAssigned)!).Order());

        // One child span per position with a node, tagged with its id, selected variant, outcome and rows.
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

        // External I/O under the node that started it: both recall sources, the prefetch after recall's fetch barrier,
        // and the model call.
        var recall = nodes.Single(s => s.OperationName == "recall");
        var underRecall = spans.Where(s => s.ParentSpanId == recall.SpanId).Select(s => s.OperationName).ToList();
        Assert.Equal(new[] { "recall popular", "recall two_tower" }, underRecall.Where(n => n.StartsWith("recall ", StringComparison.Ordinal)).Order());
        Assert.Contains("fetch item_store", underRecall);
        var ranker = nodes.Single(s => s.OperationName == "ctr_ranker");
        Assert.Contains(spans, s => s.OperationName == "model ctr_model" && s.ParentSpanId == ranker.SpanId && (string?)s.GetTagItem(StrataRecTags.Outcome) == "ok");
        Assert.Contains(spans, s => s.OperationName == "fetch item_store" && s.Kind == ActivityKind.Client && s.GetTagItem(StrataRecTags.Keys) is int);

        // A request the sampler drops gets its (unrecorded) request span and nothing else.
        record = false;
        started.Clear();
        var dropped = await engine.ExecuteAsync(MetricsTests.Request("req-sampled-out"));

        Assert.True(dropped.Succeeded, dropped.Error);
        var only = Assert.Single(started);
        Assert.Equal(StrataRecTracing.RequestSpanName, only.OperationName);
        Assert.False(only.Recorded);
    }

    [Fact]
    public async Task FallbackOnlyArm_IsAttributedOnThePositionSpan_WithTheFallbackVariant()
    {
        // Review 6 #7: an arm that changes only the fallback triggers when the fallback runs; the span must say so.
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

        var result = await engine.ExecuteAsync(MetricsTests.Request("req-fallback-arm"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("exp_quality_weight", result.Experiments.Triggered);
        var ranker = Assert.Single(spans.Stopped, s => s.OperationName == "ctr_ranker");
        Assert.Equal("fallback", ranker.GetTagItem(StrataRecTags.Outcome));
        Assert.Equal("ctr_backup", ranker.GetTagItem(StrataRecTags.Fallback));
        Assert.Contains("exp_quality_weight", Triggered(ranker));
        var backup = plan.SelectFallback(plan.Timeline.Single(t => t.NodeId == "ctr_ranker").Select(plan.ArmsFromIds(MetricsTests.Arms))!, plan.ArmsFromIds(MetricsTests.Arms))!;
        Assert.Equal(backup.DefinitionHash, ranker.GetTagItem(StrataRecTags.FallbackVariant));
        Assert.NotEqual(plan.SelectFallback(plan.Timeline.Single(t => t.NodeId == "ctr_ranker").Select(RequestArms.None)!, RequestArms.None)!.DefinitionHash, backup.DefinitionHash);
    }

    [Fact]
    public async Task AbortedRequest_RootSpanKeepsTheManifestAndAssignedArms()
    {
        // Review 6 #8: the release and the arms are known before execution; an abort must not erase them.
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

    /// <summary>Records every StrataRec span while alive.</summary>
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

/// <summary>Event logs (design 13.2): request-level failures become one summary per (event, subject) and window.</summary>
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
            var result = await executor.ExecuteAsync(plan, Request(i));
            Assert.True(result.Succeeded, result.Error); // served by ctr_backup
        }

        Assert.Empty(logging.Entries); // nothing is logged per request
        events.Flush();
        var first = logging.Entries.ToList();

        var fetch = Assert.Single(first, e => Event(e) == nameof(RequestEvent.FeatureSourceFailed));
        Assert.Equal("author_store", fetch.State["Subject"]);
        Assert.Equal(50L, fetch.State["Count"]);
        Assert.StartsWith("req-", (string)fetch.State["SampleRequestId"]!);
        Assert.Contains("configured to fail", (string)fetch.State["SampleError"]!);
        var fallback = Assert.Single(first, e => Event(e) == nameof(RequestEvent.NodeFallback));
        Assert.Equal("home_feed/ctr_ranker", fallback.State["Subject"]);
        Assert.Equal(50L, fallback.State["Count"]);
        var failing = Assert.Single(first, e => e.EventId.Id == 2002);
        Assert.Equal(LogLevel.Error, failing.Level);
        Assert.Equal("author_store", failing.State["Name"]);
        Assert.Equal(3, first.Count);

        // The next window: one more summary per pair, and no new health line while the source keeps failing.
        for (var i = 50; i < 80; i++)
        {
            await executor.ExecuteAsync(plan, Request(i));
        }

        logging.Entries.Clear();
        events.Flush();
        var second = logging.Entries.ToList();
        Assert.Equal(2, second.Count);
        Assert.All(second, e => Assert.Equal(30L, e.State["Count"]));

        events.Flush();
        Assert.Equal(2, logging.Entries.Count); // an empty window writes nothing
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

        // The sample's local author table is loaded once, before the first plan is applied; the second load takes it over.
        var entries = logging.Entries.ToList();
        Assert.Equal(new[] { 1201, 1001, 1004, 1002 }, entries.Select(e => e.EventId.Id));
        Assert.Equal(LogLevel.Error, entries[3].Level);
        Assert.Contains("no_such_feature", (string)entries[3].State["FirstError"]!);
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
