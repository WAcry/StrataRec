using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>Regression tests for findings of the logging/expressions/e2e review (cf06c43).</summary>
public sealed class Review4RegressionTests
{
    [Fact]
    public void ReloadWithAnUnknownFallbackPipelineKeepsTheCurrentPlan()
    {
        // Finding 5: the FALLBACK_PIPELINE_UNKNOWN error must block the primary's publication.
        var host = HomeFeedTestHost.LoadModified(_ => { });
        var engine = new RecEngine(host.Environment);
        ServiceCollectionExtensions.LoadWorkspace(engine, host.Root, "local", new[] { "home_feed" }).ThrowIfErrors("initial load");
        engine.TryGetPipeline("home_feed", out var before);

        var pipeline = Path.Combine(host.Root, "pipelines", "home_feed.jsonc");
        File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace("\"pipeline_id\": \"home_feed\",", "\"pipeline_id\": \"home_feed\", \"fallback_pipeline\": \"missing_backup\",", StringComparison.Ordinal));
        var diagnostics = ServiceCollectionExtensions.LoadWorkspace(engine, host.Root, "local", new[] { "home_feed" });

        Assert.Contains(diagnostics.Items, d => d.IsError && d.Code == "FALLBACK_PIPELINE_UNKNOWN");
        engine.TryGetPipeline("home_feed", out var after);
        Assert.Same(before, after);
        Assert.Null(after.FallbackPipeline);
    }

    private sealed class SlowProvider(TimeSpan delay, bool honorCancellation) : IAssignmentProvider
    {
        public async ValueTask<IReadOnlyList<string>> AssignAsync(string pipelineId, RequestInputs inputs, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, honorCancellation ? cancellationToken : CancellationToken.None);
            return Array.Empty<string>();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExternalAssignmentIsBoundedByTheRequestDeadline(bool honorCancellation)
    {
        // Finding 10: a slow provider (cooperative or not) must not hold the request past its deadline.
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new SlowProvider(TimeSpan.FromSeconds(5), honorCancellation));
        Assert.True(engine.Load(host.Request).Applied);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed", RequestId = "r", Inputs = HomeFeedTestHost.Inputs(1001, "home"), Deadline = TimeSpan.FromMilliseconds(100),
        });

        Assert.False(result.Succeeded);
        Assert.Contains("assignment", result.Error);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 2_000);
    }

    [Fact]
    public async Task CallerCancellationStillPublishesTheExperimentRecord()
    {
        // Finding 6: a cancelled request was assigned (and maybe exposed); its durable record must not vanish.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var bindings = Path.Combine(root, "bindings", "local.jsonc");
            File.WriteAllText(bindings, File.ReadAllText(bindings).Replace("\"client\": \"memory_list@1\",", "\"client\": \"memory_list@1\", \"latency_ms\": 5000,", StringComparison.Ordinal));
        });
        var plan = host.Plan();
        var sink = new InMemoryLogSink();
        var request = HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(new[] { "exp_quality_weight/strong" }));
        await using (var logs = new LogPipeline(sink))
        {
            var executor = new PipelineExecutor(logs: logs);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));


            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await executor.ExecuteAsync(plan, request, cancel.Token));
            await logs.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        var record = Assert.Single(sink.Snapshot(), b => b.TapId == "$experiments");
        Assert.Equal(request.RequestId, record.RequestId);
        Assert.Contains("exp_quality_weight/strong", record.AssignedArms);
    }

    private static PlanCompileRequest With(PlanCompileRequest r, params ExperimentPlacement[] extra) => new()
    {
        Pipeline = r.Pipeline, Catalog = r.Catalog, Bindings = r.Bindings, Tables = r.Tables, Policy = r.Policy,
        Experiments = r.Experiments.Concat(extra).ToList(), Assignment = r.Assignment, ManifestId = r.ManifestId,
    };

    [Fact]
    public async Task ColumnsFromLogsTheInputsOfTheModelVariantThatServed()
    {
        // Findings 1 and 7: a treatment that rewires a model input must log its own inputs, under its own schema id.
        var host = HomeFeedTestHost.Load();
        var swap = new ExperimentPlacement(ArtifactSerializer.Deserialize<ExperimentArtifact>("""
            { "experiment_id": "exp_inputs", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
              "arms": { "control": {}, "swap": { "nodes": { "ctr_ranker": { "config": { "inputs": { "log_followers": "col.item.smoothed_ctr" } } } } } } }
            """), "inputs");
        var compilation = PlanCompiler.Compile(With(host.Request, swap), host.Environment);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var plan = compilation.Plan!;
        var executor = new PipelineExecutor();

        async Task<LogBatch> RankerInputs(string arm)
        {
            var result = await executor.ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(new[] { arm }), captureLogs: true));
            Assert.True(result.Succeeded, result.Error);
            return Assert.Single(result.LogBatches, b => b.TapId == "ranker_inputs");
        }

        static ScalarValue[] Column(LogBatch batch, string name) => Assert.Single(batch.Columns, c => c.Name == name).Values;

        var control = await RankerInputs("exp_inputs/control");
        var treatment = await RankerInputs("exp_inputs/swap");

        Assert.NotEqual(Column(control, "smoothed_ctr"), Column(control, "log_followers"));
        Assert.Equal(Column(treatment, "smoothed_ctr"), Column(treatment, "log_followers"));
        Assert.NotEqual(control.SchemaId, treatment.SchemaId);
    }

    [Fact]
    public void TapCannotReadAVersionProducedAfterItsAttachPoint()
    {
        // Finding 9: col.score.final@fusion does not exist yet right after recall.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
            File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace("\"log_taps\": {",
                "\"log_taps\": { \"future\": { \"group\": \"all\", \"attach\": { \"after\": \"recall\" }, \"columns\": { \"f\": \"col.score.final@fusion\" } },",
                StringComparison.Ordinal));
        });

        var compilation = host.Compile();

        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, d => d.IsError && d.Code == "TAP_PINNED_NOT_PRODUCED" && d.Message.Contains("fusion"));
    }

    [Fact]
    public void FeaturesDerivedFromPersonalDataKeepTheLoggingRestriction()
    {
        // Finding 2: an alias of a personal feature must not escape purpose/durable rules (design 8.9).
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var catalog = Path.Combine(root, "catalog", "home_feed.jsonc");
            File.WriteAllText(catalog, File.ReadAllText(catalog)
                .Replace("\"field\": \"u_tenure_d\", \"default\": 0.0 }", "\"field\": \"u_tenure_d\", \"default\": 0.0, \"sensitivity\": \"personal\" }", StringComparison.Ordinal)
                .Replace("\"derived_features\": {", "\"derived_features\": { \"user.tenure_copy\": { \"type\": \"float32\", \"expr\": \"ctx.user.tenure_days\" },", StringComparison.Ordinal));
            var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
            File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace(
                "\"context\": { \"user_id\": \"ctx.request.user_id\",", "\"context\": { \"tenure\": \"ctx.user.tenure_copy\", \"user_id\": \"ctx.request.user_id\",", StringComparison.Ordinal));
        });

        var compilation = host.Compile();

        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, d => d.IsError && d.Code == "TAP_PERSONAL");
    }

    private static HomeFeedTestHost WithPipelineEdit(string from, string to) => HomeFeedTestHost.LoadModified(root =>
    {
        var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
        var text = File.ReadAllText(pipeline);
        Assert.Contains(from, text);
        File.WriteAllText(pipeline, text.Replace(from, to, StringComparison.Ordinal));
    });

    [Fact]
    public void FunnelKeysCannotWaitForFetchedFeatures()
    {
        // Finding 8: the sampling decision is made at request start; a fetched key would block every request on I/O.
        var host = WithPipelineEdit("\"deep_trace\": { \"key\": [", "\"deep_trace\": { \"key\": [\"ctx.user.tenure_days\", ");

        var compilation = host.Compile();

        Assert.Contains(compilation.Diagnostics, d => d.IsError && d.Code == "FUNNEL_KEY_FETCHED" && d.Pointer == "/funnel_groups/deep_trace/key/0");
    }

    [Fact]
    public async Task TapsArePassiveForContextFeatures()
    {
        // Finding 8: a tap may log a context feature the pipeline reads anyway, but must not add a fetch of its own.
        var logOnly = WithPipelineEdit("\"context\": { \"user_id\": \"ctx.request.user_id\",", "\"context\": { \"tenure\": \"ctx.user.tenure_days\", \"user_id\": \"ctx.request.user_id\",");
        Assert.Contains(logOnly.Compile().Diagnostics, d => d.IsError && d.Code == "TAP_FEATURE_NOT_FETCHED" && d.Message.Contains("user.tenure_days"));

        var alsoServed = HomeFeedTestHost.LoadModified(root =>
        {
            var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
            File.WriteAllText(pipeline, File.ReadAllText(pipeline)
                .Replace("\"context\": { \"user_id\": \"ctx.request.user_id\",", "\"context\": { \"tenure\": \"ctx.user.tenure_days\", \"user_id\": \"ctx.request.user_id\",", StringComparison.Ordinal)
                .Replace("\"expr\": \"col.item.smoothed_ctr\" }", "\"expr\": \"col.item.smoothed_ctr * (ctx.user.tenure_days > 1000.0 ? 1.0 : 1.0)\" }", StringComparison.Ordinal));
        });
        var plan = alsoServed.Plan();
        var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(captureLogs: true));
        Assert.True(result.Succeeded, result.Error);
        var served = Assert.Single(result.LogBatches, b => b.TapId == "served");
        Assert.Equal(420.0, served.Context.Single(c => c.Item1 == "tenure").Item2.AsFloat64());
    }

    [Fact]
    public void NodeCannotReadItsOwnPinnedVersion()
    {
        // Found while fixing finding 15: col.x@self resolves to the slot the node is about to write.
        var host = WithPipelineEdit("\"output_col\": \"score.coarse\", \"expr\": \"col.item.smoothed_ctr\"",
            "\"output_col\": \"score.coarse\", \"expr\": \"col.item.smoothed_ctr + (col.score.coarse@coarse_score ?? 0.0)\"");

        var compilation = host.Compile();

        Assert.Contains(compilation.Diagnostics, d => d.IsError && d.Code == "VERSION_REF_SELF" && d.Pointer.StartsWith("/nodes/coarse_score", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledCapturingRequestNeverWritesToTheProductionLogs()
    {
        // Review 5 finding 2: a cancelled preview (CaptureLogs) must not fall back to persistent submission.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var bindings = Path.Combine(root, "bindings", "local.jsonc");
            File.WriteAllText(bindings, File.ReadAllText(bindings).Replace("\"client\": \"memory_list@1\",", "\"client\": \"memory_list@1\", \"latency_ms\": 5000,", StringComparison.Ordinal));
        });
        var plan = host.Plan();
        var sink = new InMemoryLogSink();
        await using (var logs = new LogPipeline(sink))
        {
            var executor = new PipelineExecutor(logs: logs);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var request = HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(new[] { "exp_quality_weight/strong" }), captureLogs: true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await executor.ExecuteAsync(plan, request, cancel.Token));
            await logs.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public void FunnelKeysMustBeHashableScalars()
    {
        // Review 5 finding 3: a list key compiled, then threw from sampling at request time.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
            File.WriteAllText(pipeline, File.ReadAllText(pipeline)
                .Replace("\"session_id\": { \"type\": \"string?\" },", "\"session_id\": { \"type\": \"string?\" }, \"history\": { \"type\": \"list<int64>?\" },", StringComparison.Ordinal)
                .Replace("\"deep_trace\": { \"key\": [", "\"deep_trace\": { \"key\": [\"ctx.request.history\", ", StringComparison.Ordinal));
        });

        Assert.Contains(host.Compile().Diagnostics, d => d.IsError && d.Code == "FUNNEL_KEY_TYPE" && d.Pointer == "/funnel_groups/deep_trace/key/0");
    }

    [Fact]
    public void RowSourceOutputsCannotBePinned()
    {
        // Review 5 finding 4: row sources share one slot per column, so col.x@recall read an unwritten per-node slot.
        var host = WithPipelineEdit("\"primary_source\": \"col.recall.primary_source\"",
            "\"primary_source\": \"col.recall.primary_source\", \"pinned\": \"col.recall.best_score@recall ?? -100.0\"");

        Assert.Contains(host.Compile().Diagnostics, d => d.IsError && d.Code == "VERSION_REF_ROW_SOURCE");
    }
}
