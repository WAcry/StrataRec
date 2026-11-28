using StrataRec.CommandLine.Commands;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

public sealed class ExperimentRegressionTests
{
    private static ExperimentArtifact Experiment(string json) => ArtifactSerializer.Deserialize<ExperimentArtifact>(json);

    private static PlanCompileRequest With(PlanCompileRequest r, IEnumerable<ExperimentPlacement> extra, int? maxCombinations = null) => new()
    {
        Pipeline = r.Pipeline,
        Catalog = r.Catalog,
        Bindings = r.Bindings,
        Tables = r.Tables,
        Policy = r.Policy,
        Experiments = r.Experiments.Concat(extra).ToList(),
        ManifestId = r.ManifestId,
        MaxCombinationsChecked = maxCombinations ?? r.MaxCombinationsChecked,
    };

    private static ExperimentPlacement FusionMultiplier(string id, string layer) => new(Experiment($$"""
        { "experiment_id": "{{id}}", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
          "arms": { "control": {}, "t": { "nodes": { "fusion": { "config": { "multipliers": { "{{id}}": "1.1" } } } } } } }
        """), layer);

    [Fact]
    public void CombinationSpaceOverTheLimitIsAnErrorNotAWarning()
    {
        // score.final is written or read by nodes touched in four layers: 16 combinations > limit 4.
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request, new[] { FusionMultiplier("exp_a", "layer_a"), FusionMultiplier("exp_b", "layer_b") }, maxCombinations: 4);

        var compilation = PlanCompiler.Compile(request, host.Environment);

        Assert.False(compilation.Succeeded);
        Assert.False(compilation.Report.CombinationCheckExhaustive);
        Assert.Contains(compilation.Diagnostics, d => d.IsError && d.Code == "COMBINATION_CHECK_LIMIT" && d.Message.Contains("score.final"));
    }

    [Fact]
    public void ArmAdditionsCannotRepairABrokenBaseline()
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
            File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace(
                "\"output_col\": \"score.coarse\", \"expr\": \"col.item.smoothed_ctr\"",
                "\"output_col\": \"score.coarse\", \"expr\": \"col.item.smoothed_ctr + col.item.bookmarks\"", StringComparison.Ordinal));
        });
        var addition = """{ "item.bookmarks": { "type": "float32", "source": "item_store", "field": "bmk", "default": 0.0 } }""";
        var request = With(host.Request, new[]
        {
            new ExperimentPlacement(Experiment($$"""
                { "experiment_id": "exp_bmk", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": { "features": {{addition}} }, "t": { "features": {{addition}} } } }
                """), "layer_x"),
        });

        var compilation = PlanCompiler.Compile(request, host.Environment);

        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, d => d.IsError && d.Artifact == "pipeline:home_feed" && d.Pointer == "/nodes/coarse_score/config/expr");
    }

    [Fact]
    public async Task ParameterExposureTriggersEvenWhenTheNodeFails()
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var bindings = Path.Combine(root, "bindings", "local.jsonc");
            File.WriteAllText(bindings, File.ReadAllText(bindings)
                .Replace("\"client\": \"memory_list@1\",", "\"client\": \"memory_list@1\", \"fail\": true,", StringComparison.Ordinal)
                .Replace("\"client\": \"memory_vectors@1\",", "\"client\": \"memory_vectors@1\", \"fail\": true,", StringComparison.Ordinal));
        });
        var request = With(host.Request, new[]
        {
            new ExperimentPlacement(Experiment("""
                { "experiment_id": "exp_quota", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {}, "more": { "params": { "recall_quotas": { "two_tower": 80 } } } } }
                """), "quota"),
        });
        var plan = host.Compile(request).GetPlanOrThrow();

        foreach (var arm in new[] { "exp_quota/control", "exp_quota/more" })
        {
            var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(new[] { arm })), TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Contains("exp_quota", result.TriggeredExperiments);
        }
    }

    [Fact]
    public async Task ATriggerFollowsTheNodeEachArmHas_WithTheRequestsArmsInTheOtherLayers()
    {
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request, new[]
        {
            new ExperimentPlacement(Experiment("""
                { "experiment_id": "exp_cap_limit", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {}, "t": { "nodes": { "author_cap": { "config": { "predicate": "(col.group.author_rank ?? 1) <= 3" } } } } } }
                """), "cap_limit"),
            new ExperimentPlacement(Experiment("""
                { "experiment_id": "exp_cap_search", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {}, "t": { "nodes": { "author_cap": { "when": "ctx.request.surface == 'search'" } } } } }
                """), "cap_search"),
        });
        var plan = host.Compile(request).GetPlanOrThrow();

        foreach (var search in new[] { "control", "t" })
        {
            foreach (var limit in new[] { "control", "t" })
            {
                foreach (var surface in new[] { "home", "search", "other" })
                {
                    var arms = plan.ArmsFromIds(new[] { "exp_cap_limit/" + limit, "exp_cap_search/" + search });
                    var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(surface: surface, arms: arms), TestContext.Current.CancellationToken);
                    var ran = result.Outcomes.Single(o => o.NodeId == "author_cap").Kind != NodeOutcomeKind.SkippedWhen;
                    var label = $"cap_search {search}, cap_limit {limit}, {surface}";

                    Assert.True(result.Succeeded, result.Error);
                    Assert.Equal(surface == (search == "t" ? "search" : "home"), ran);
                    Assert.True(ran == result.TriggeredExperiments.Contains("exp_cap_limit"), label);
                    Assert.True((surface != "other") == result.TriggeredExperiments.Contains("exp_cap_search"), label);
                }
            }
        }
    }

    [Fact]
    public async Task ExplainPlan_PrintsASitesConditionAsTheOrOfWholeConditions()
    {
        const string Ternary = "ctx.request.surface == 'home' ? ctx.request.surface == 'search' : ctx.request.surface == 'other'";
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
            File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace(
                "\"order\": 515, \"op\": \"ExpressionFilter@1\", \"when\": \"ctx.request.surface == 'home'\"",
                $"\"order\": 515, \"op\": \"ExpressionFilter@1\", \"when\": \"{Ternary}\"", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "experiments", "exp_cap_when.jsonc"), """
                { "experiment_id": "exp_cap_when", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {}, "t": { "nodes": { "author_cap": { "when": "ctx.request.surface == 'home'" } } } } }
                """);
            var release = Path.Combine(root, "releases", "home_feed.local.jsonc");
            File.WriteAllText(release, File.ReadAllText(release).Replace(
                "\"exp_quality_weight\":        { \"layer\": \"params\" }",
                "\"exp_quality_weight\":        { \"layer\": \"params\" }, \"exp_cap_when\": { \"layer\": \"cap\" }", StringComparison.Ordinal));
        });
        var output = new StringWriter();
        var errors = new StringWriter();

        var exit = await ExplainPlanCommand.RunAsync(Tool.Environment, host.Root, "local", "home_feed", "text", new[] { "exp_cap_when/control" }, output,
            TestContext.Current.CancellationToken, errors: errors);

        Assert.True(exit == 0, errors.ToString() + output);

        var site = output.ToString().Split('\n').Single(l => l.Contains("author_cap", StringComparison.Ordinal) && l.Contains("site:", StringComparison.Ordinal));
        Assert.Contains("exp_cap_when if (", site, StringComparison.Ordinal);
        Assert.Contains(") || (", site, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderLoadNeverReplacesANewerPlan()
    {
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment);

        Assert.True(engine.Load(host.Request, sequence: 5).Applied);
        var stale = engine.Load(host.Request, sequence: 3);

        Assert.False(stale.Applied);
        Assert.Contains(stale.Diagnostics, d => d.Code == "PLAN_STALE");
        engine.TryGetPipeline("home_feed", out var current);
        Assert.Equal(5, current.Sequence);
    }

    [Fact]
    public void ArmsOfDifferentLayersAreValidatedTogetherAndArmsOfOneLayerNever()
    {
        // Without traffic domains: rerank deletes diversity_cap and another experiment edits one of its fields.
        ExperimentPlacement Edit(string layer) => new(Experiment("""
            { "experiment_id": "exp_window", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
              "arms": { "control": {}, "w": { "nodes": { "diversity_cap": { "config": { "window": 4 } } } } } }
            """), layer);
        var host = HomeFeedTestHost.Load();

        var ownLayer = PlanCompiler.Compile(With(host.Request, new[] { Edit("window") }), host.Environment);
        Assert.Contains(ownLayer.Diagnostics, d => d.Code == "PATCH_CONFLICT");

        var sharedLayer = PlanCompiler.Compile(With(host.Request, new[] { Edit("rerank") }), host.Environment);

        Assert.True(sharedLayer.Succeeded, string.Join("\n", sharedLayer.Diagnostics));
        var plan = sharedLayer.Plan!;
        Assert.Throws<ArgumentException>(() => plan.ArmsFromIds(new[] { "exp_mmr_diversity/mmr", "exp_window/w" }));
        Assert.NotNull(plan.Timeline.Single(s => s.NodeId == "diversity_cap").Select(plan.ArmsFromIds(new[] { "exp_window/w" })));
    }

    [Fact]
    public async Task FallbackPipelineIsLoadedAndServesWhenThePrimaryFails()
    {
        var root = FallbackWorkspace(brokenLatencyMs: 0);
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
        var diagnostics = ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", new[] { "primary" });
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "primary", RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(2),
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("backup", result.ServedBy);
        Assert.Equal(5, result.Items.Count);
        Assert.Contains("pipeline 'primary' failed", result.Error);
    }

    [Fact]
    public async Task FallbackPipelineOnlyGetsTheRemainingBudget()
    {
        // The primary hangs until the 400 ms deadline; the backup needs 200 ms. With the remaining budget (~0 ms) the
        // backup cannot finish; with a fresh 400 ms budget it would have succeeded at ~600 ms.
        var root = FallbackWorkspace(brokenLatencyMs: 60_000, backupLatencyMs: 200);
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
        ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", new[] { "primary" }).ThrowIfErrors("load");

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "primary", RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromMilliseconds(400),
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.InRange(result.ElapsedMs, 0, 30_000); // the deadline cancelled the one-minute primary (a generous watchdog)
    }

    internal static string FallbackWorkspace(int brokenLatencyMs, int backupLatencyMs = 0)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-fallback-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines", "fixtures" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.Copy(Path.Combine(HomeFeedTestHost.SampleRoot(), "fixtures", "recall_popular.json"), Path.Combine(root, "fixtures", "recall_popular.json"));
        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), $$"""
            { "environment": "local",
              "services": {
                "broken":  { "client": "memory_list@1", "items": [1, 2, 3], "fail": true, "latency_ms": {{brokenLatencyMs}} },
                "popular": { "client": "memory_list@1", "data_file": "fixtures/recall_popular.json", "latency_ms": {{backupLatencyMs}} } } }
            """);
        foreach (var (id, source, fallback) in new[] { ("primary", "broken", "\"fallback_pipeline\": \"backup\","), ("backup", "popular", string.Empty) })
        {
            File.WriteAllText(Path.Combine(root, "pipelines", id + ".jsonc"), $$"""
                { "pipeline_id": "{{id}}", {{fallback}}
                  "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
                  "params": { "recall_quotas": { "{{source}}": 10 }, "k": 5 },
                  "nodes": { "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } } },
                  "output": { "limit_param": "k", "columns": { "source": "col.recall.primary_source" } } }
                """);
        }

        return root;
    }
}
