using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>Regression tests for findings of the experiments/hosting review (c84900e).</summary>
public sealed class ReviewRegressionTests
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
        Assignment = r.Assignment,
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
        // score.final is written or read by nodes touched in four layers: 16 combinations > limit 4 (finding 1).
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
        // The baseline reads a feature only an experiment adds (finding 2): removing the experiment would break it.
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
        // Finding 3: recall reads the overridden quota, then fails; the experiment was still exposed.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var bindings = Path.Combine(root, "bindings", "local.jsonc");
            File.WriteAllText(bindings, File.ReadAllText(bindings).Replace("\"client\": \"memory_list@1\",", "\"client\": \"memory_list@1\", \"fail\": true,", StringComparison.Ordinal));
        });
        var request = With(host.Request, new[]
        {
            new ExperimentPlacement(Experiment("""
                { "experiment_id": "exp_quota", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {}, "more": { "params": { "recall_quotas": { "two_tower": 80 } } } } }
                """), "quota"),
        });
        var plan = PlanCompiler.Compile(request, host.Environment).GetPlanOrThrow();

        foreach (var arm in new[] { "exp_quota/control", "exp_quota/more" })
        {
            var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(new[] { arm })));
            Assert.False(result.Succeeded);
            Assert.Contains("exp_quota", result.TriggeredExperiments);
        }
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
    public void ImpossibleCrossDomainCombinationsAreNotCompiled()
    {
        // Finding 6: rerank deletes diversity_cap, a layer in another domain edits one of its fields.
        var edit = new ExperimentPlacement(Experiment("""
            { "experiment_id": "exp_window", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
              "arms": { "control": {}, "w": { "nodes": { "diversity_cap": { "config": { "window": 4 } } } } } }
            """), "window");
        var host = HomeFeedTestHost.Load();

        var sameDomain = PlanCompiler.Compile(With(host.Request, new[] { edit }), host.Environment);
        Assert.Contains(sameDomain.Diagnostics, d => d.Code == "PATCH_CONFLICT");

        var assignment = new AssignmentSpec
        {
            Buckets = 100,
            DomainSplit = new DomainSplitSpec { Salt = "d", Unit = new() { "ctx.request.session_id" } },
            Domains = new() { ["a"] = new DomainSpec { Buckets = new[] { 0, 49 } }, ["b"] = new DomainSpec { Buckets = new[] { 50, 99 } } },
            Layers = new()
            {
                ["rerank"] = new LayerSpec { Domain = "a", Salt = "r", Unit = new() { "ctx.request.session_id" } },
                ["fusion"] = new LayerSpec { Domain = "a", Salt = "f", Unit = new() { "ctx.request.session_id" } },
                ["params"] = new LayerSpec { Domain = "a", Salt = "p", Unit = new() { "ctx.request.session_id" } },
                ["window"] = new LayerSpec { Domain = "b", Salt = "w", Unit = new() { "ctx.request.session_id" } },
            },
        };
        var split = With(host.Request, new[] { edit });
        split = new PlanCompileRequest
        {
            Pipeline = split.Pipeline, Catalog = split.Catalog, Bindings = split.Bindings, Tables = split.Tables,
            Experiments = split.Experiments, Assignment = assignment,
        };

        var compilation = PlanCompiler.Compile(split, host.Environment);

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var plan = compilation.Plan!;
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
        });

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("backup", result.ServedBy);
        Assert.Equal(5, result.Items.Count);
        Assert.Contains("pipeline 'primary' failed", result.Error);
    }

    [Fact]
    public async Task FallbackPipelineOnlyGetsTheRemainingBudget()
    {
        // The primary hangs until the 400 ms deadline; the backup needs 200 ms. With the remaining budget (~0 ms) the
        // backup cannot finish; with a fresh 400 ms budget (finding 12) it would have succeeded at ~600 ms.
        var root = FallbackWorkspace(brokenLatencyMs: 5_000, backupLatencyMs: 200);
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
        ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", new[] { "primary" }).ThrowIfErrors("load");

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "primary", RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromMilliseconds(400),
        });

        Assert.False(result.Succeeded);
        Assert.InRange(result.ElapsedMs, 0, 3_000); // the deadline cancelled the 5 s primary
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
              "candidate_sources": {
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
