using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using static StrataRec.IntegrationTests.CombinationLimitTests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Request columns (<c>req.*</c>) in the home feed: the recall's statistics (<c>recall_stats</c>) a later node's
/// <c>when</c> reads and the result returns (<c>output.values</c>), recorded and replayed like the items.
/// </summary>
public sealed class RequestColumnEndToEndTests
{
    /// <summary>
    /// The values are part of the recorded result: a replay of the same configuration is identical, and one after a
    /// value's definition changed reports that value, by name, with the items unchanged.
    /// </summary>
    [Fact]
    public async Task TheValues_AreRecorded_AndAReplayAfterOneChanges_ReportsIt()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-values", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);
        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(["best_score", "popular", "recalled"], recorded.Values.All.ToArray().Select(v => v.Name));
        Assert.True(recorded.Values.TryGetEntry("popular", out var popular));
        Assert.True(recorded.Values["recalled"].AsInt64() > popular.Value.AsInt64());

        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        Assert.Equal("int64", bundle.Result.ValueTypes["popular"]);
        var same = await engine.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(same.Report.Diff.Identical, ReplayTests.Describe(same.Report));

        var modified = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "(col.recall.primary_source ?? '') == 'popular'", "(col.recall.primary_source ?? '') == 'two_tower'"));
        var (candidate, _) = MetricsTests.LoadEngine(modified);
        var outcome = await candidate.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);

        var diff = outcome.Report.Diff;
        Assert.False(diff.Identical);
        Assert.Empty(diff.Changes);
        var change = Assert.Single(diff.ValueChanges);
        Assert.Equal("popular", change.Name);
        Assert.Equal(popular, change.Recorded);
        Assert.True(outcome.Result.Values.TryGetEntry("popular", out var replayed));
        Assert.Equal(replayed, change.Replayed);
        Assert.NotEqual(popular.Value.AsInt64(), change.Replayed!.Value.Value.AsInt64());
    }

    /// <summary>
    /// A request its fallback pipeline serves returns the fallback's values, as it returns its items, live and replayed:
    /// the result of the run that served, the backup counting the 10 candidates it recalled.
    /// </summary>
    [Fact]
    public async Task AFallbackPipelinesValues_AreTheResults_LiveAndReplayed()
    {
        var root = ReviewRegressionTests.FallbackWorkspace(brokenLatencyMs: 0);
        ReplayTests.Edit(root, "pipelines/backup.jsonc", "\"params\":", "\"request_columns\": { \"n\": { \"type\": \"int64\" } }, \"params\":");
        ReplayTests.Edit(root, "pipelines/backup.jsonc", "\"config\": { \"quotas_param\": \"recall_quotas\" } } },",
            "\"config\": { \"quotas_param\": \"recall_quotas\" } }, \"count\": { \"order\": 200, \"op\": \"RequestAggregate@1\", \"config\": { \"values\": { \"n\": { \"fn\": \"count\" } } } } },");
        ReplayTests.Edit(root, "pipelines/backup.jsonc", "\"columns\": { \"source\": \"col.recall.primary_source\" } }",
            "\"columns\": { \"source\": \"col.recall.primary_source\" }, \"values\": { \"count\": \"req.n\" } }");
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
        ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", ["primary"]).ThrowIfErrors("load");

        var recorded = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "primary", RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(2),
            Debug = new DebugOptions { Record = true },
        }, TestContext.Current.CancellationToken);
        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(("backup", 10L), (recorded.ServedBy, recorded.Values["count"].AsInt64()));
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal(10, outcome.Result.Values["count"].AsInt64());
    }

    /// <summary>
    /// A trigger site's condition may read a request column (design 12.6): evaluated in a request of any arm, it decides
    /// the same, because an experiment not yet triggered has changed nothing before its site, so the column holds what it
    /// would in every arm. t changes author_cap's <c>when</c> to read the recall's count (47 on explore); the control's
    /// author_cap does not run on explore.
    /// </summary>
    [Theory]
    [InlineData(10, true)]
    [InlineData(1000, false)]
    public async Task ASiteConditionReadingARequestColumn_TriggersBothArmsAlike(int threshold, bool triggered)
    {
        var host = WithExperiment($$"""{ "author_cap": { "when": "req.candidates.recalled >= {{threshold}}" } }""");
        var plan = host.Plan();

        foreach (var arm in new[] { "exp_req_when/control", "exp_req_when/t" })
        {
            var result = await new PipelineExecutor().ExecuteAsync(plan,
                HomeFeedTestHost.NewRequest(surface: "explore", arms: plan.ArmsFromIds([arm])), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(triggered, result.TriggeredExperiments.Contains("exp_req_when"));
            var ran = arm.EndsWith("/t", StringComparison.Ordinal) && triggered;
            Assert.Equal(ran ? NodeOutcomeKind.Succeeded : NodeOutcomeKind.SkippedWhen, result.Outcomes.Single(o => o.NodeId == "author_cap").Kind);
        }
    }

    /// <summary>
    /// A condition may read a request column only its own arm writes: the node writing it is a site of the experiment
    /// too, and an earlier one, so a request of the other arm is triggered there, before the condition, and both arms are
    /// triggered alike.
    /// </summary>
    [Fact]
    public async Task ASiteConditionReadingARequestColumnOnlyItsArmWrites_IsPrecededByTheWritersSite()
    {
        var host = WithExperiment("""
            { "count_all": { "order": 130, "op": "RequestAggregate@1", "config": { "values": { "cand.n": { "fn": "count" } } } },
              "author_cap": { "when": "req.cand.n >= 1000" } }
            """);
        var plan = host.Plan();

        foreach (var arm in new[] { "exp_req_when/control", "exp_req_when/t" })
        {
            var result = await new PipelineExecutor().ExecuteAsync(plan,
                HomeFeedTestHost.NewRequest(surface: "explore", arms: plan.ArmsFromIds([arm])), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("exp_req_when", result.TriggeredExperiments);
        }
    }

    /// <summary>
    /// An arm may declare a request column of its own, as it declares a column (design 11.4: add-only), and its nodes
    /// write and read it: in a request of the arm the count gates author_cap on explore; the control has neither.
    /// </summary>
    [Fact]
    public async Task AnArm_DeclaresARequestColumn_ItsNodesWriteAndRead()
    {
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request,
        [
            Treatment("a", """
                { "request_columns": { "cand.n": { "type": "int64", "description": "the candidates after the safety filter" } },
                  "nodes": { "count_all": { "order": 210, "op": "RequestAggregate@1", "config": { "values": { "cand.n": { "fn": "count" } } } },
                             "author_cap": { "when": "req.cand.n >= 10" } } }
                """),
        ]);
        var compilation = host.Compile(request);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var plan = compilation.Plan!;

        var treated = await new PipelineExecutor().ExecuteAsync(plan,
            HomeFeedTestHost.NewRequest(surface: "explore", arms: plan.ArmsFromIds(["a/t"])), TestContext.Current.CancellationToken);
        var control = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(surface: "explore"), TestContext.Current.CancellationToken);

        Assert.True(treated.Succeeded, treated.Error);
        Assert.Equal(NodeOutcomeKind.Succeeded, treated.Outcomes.Single(o => o.NodeId == "count_all").Kind);
        Assert.Equal(NodeOutcomeKind.Succeeded, treated.Outcomes.Single(o => o.NodeId == "author_cap").Kind);
        Assert.True(control.Succeeded, control.Error);
        Assert.DoesNotContain(control.Outcomes, o => o.NodeId == "count_all");
        Assert.Equal(NodeOutcomeKind.SkippedWhen, control.Outcomes.Single(o => o.NodeId == "author_cap").Kind);
    }

    /// <summary>
    /// A request column an arm declares exists only where the arm is assigned: a node of another layer's arm reading it is
    /// refused for the combination without the declaring arm, as that combination's own compile refuses it (design 11.5
    /// check 3).
    /// </summary>
    [Fact]
    public void AReaderOfARequestColumnAnotherLayersArmDeclares_IsRejected_WhereThatArmIsNotAssigned()
    {
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request,
        [
            Treatment("a", """
                { "request_columns": { "cand.n": { "type": "int64?" } },
                  "nodes": { "count_all": { "order": 210, "op": "RequestAggregate@1", "config": { "values": { "cand.n": { "fn": "count" } } } } } }
                """),
            Treatment("b", """{ "nodes": { "author_cap": { "when": "(req.cand.n ?? 0) >= 10" } } }"""),
        ]);
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment).Succeeded);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["b/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "experiment:b", "/arms/t/nodes/author_cap"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("'req.cand.n'", error.Message, StringComparison.Ordinal);
        Assert.Contains("only 'a/t' declares it, and it is not assigned with it here", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A request column's producers are checked together only where a rule needs them together (review of 1102725): a
    /// nullable flow column only nodes write, one optional writer in each of 13 layers (8,192 combinations, past the
    /// default limit of 4,096), needs no combination of them.
    /// </summary>
    [Fact]
    public void ANullableFlowRequestColumnsOptionalWriters_AreNotCheckedTogether()
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.RequestColumns["review.flow"] = new ColumnSpec { Type = "int64?", Flow = true };
        var placements = new List<ExperimentPlacement>();
        for (var i = 0; i < 13; i++)
        {
            placements.Add(Treatment("w" + i, $$"""
                { "nodes": { "count_{{i}}": { "order": {{600 + i}}, "op": "RequestAggregate@1", "config": { "values": { "review.flow": { "fn": "count" } } } } } }
                """));
        }

        var unified = PlanCompiler.Compile(With(host.Request, placements), host.Environment);

        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
        Assert.DoesNotContain(unified.Diagnostics, d => d.Code == "COMBINATION_CHECK_LIMIT");
    }

    /// <summary>
    /// Request columns interact between experiments as columns do (design 11.5; review of 1102725): an added node reading
    /// what another layer's added node writes, two added nodes writing one flow column. The review names it req.&lt;name&gt;.
    /// </summary>
    [Fact]
    public void AddedWritersAndReadersOfARequestColumn_Interact_UntilTheReviewIsRecorded()
    {
        static void Experiments(string root, bool reviewed)
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"request_columns\": {", "\"request_columns\": { \"cand.flow\": { \"type\": \"int64?\", \"flow\": true },");
            InteractionWarningTests.Experiment(root, "exp_req_a", "t",
                """{ "nodes": { "count_a": { "order": 130, "op": "RequestAggregate@1", "config": { "values": { "cand.flow": { "fn": "count" } } } } } }""");
            InteractionWarningTests.Experiment(root, "exp_req_b", "t",
                """{ "nodes": { "count_b": { "order": 140, "op": "RequestAggregate@1", "config": { "values": { "cand.flow": { "fn": "count", "where": "col.entity_id > 0" } } } } } }""");
            InteractionWarningTests.Experiment(root, "exp_req_c", "t",
                """{ "nodes": { "read_c": { "order": 600, "op": "ExpressionFilter@1", "config": { "predicate": "(req.cand.flow ?? 0) >= 0" } } } }""");
            if (reviewed)
            {
                ReplayTests.Edit(root, "experiments/exp_req_c.jsonc", "\"expires_at\": \"2099-01-01\",",
                    "\"expires_at\": \"2099-01-01\", \"reviewed_interactions\": { \"exp_req_a\": [\"req.cand.flow\"], \"exp_req_b\": [\"req.cand.flow\"] },");
                ReplayTests.Edit(root, "experiments/exp_req_a.jsonc", "\"expires_at\": \"2099-01-01\",",
                    "\"expires_at\": \"2099-01-01\", \"reviewed_interactions\": { \"exp_req_b\": [\"req.cand.flow\"] },");
            }
        }

        var warned = HomeFeedTestHost.LoadModified(root => Experiments(root, reviewed: false)).Compile();
        var reviewed = HomeFeedTestHost.LoadModified(root => Experiments(root, reviewed: true)).Compile();

        Assert.True(warned.Succeeded, string.Join("\n", warned.Diagnostics));
        Assert.Equal(
            [
                ("INTERACTION_FLOW_COLUMN", "experiment:exp_req_a", "/arms/t/nodes/count_a"),
                ("INTERACTION_READ", "experiment:exp_req_c", "/arms/t/nodes/read_c"),
                ("INTERACTION_READ", "experiment:exp_req_c", "/arms/t/nodes/read_c"),
            ],
            warned.Diagnostics.Where(d => d.Code.StartsWith("INTERACTION_", StringComparison.Ordinal)).Select(d => (d.Code, d.Artifact, d.Pointer)).Order());
        Assert.All(warned.Diagnostics.Where(d => d.Code.StartsWith("INTERACTION_", StringComparison.Ordinal)), d => Assert.Contains("'req.cand.flow'", d.Message));
        Assert.True(reviewed.Succeeded, string.Join("\n", reviewed.Diagnostics));
        Assert.Empty(reviewed.Diagnostics);
    }

    /// <summary>
    /// A tap binding a request column only another layer's arm declares, in a branch the optimizer drops, is refused
    /// where that arm is not assigned, also when the combinations are checked by scope (past the limit): the name decides
    /// which combinations the tap is checked in (second review of 1102725). gate's named input reads the column only when
    /// a and b both switch it on, and only the tap c adds compiles it (columns_from); d declares it.
    /// </summary>
    [Fact]
    public void ATapBindingARequestColumnAnotherLayersArmDeclares_IsRejected_WhenCheckedByScope()
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["gate"] = new NodeSpec
        {
            Order = 850, Op = "GateInputs@1", Config = new System.Text.Json.Nodes.JsonObject { ["left"] = false, ["right"] = false },
        };
        var placements = new List<ExperimentPlacement>
        {
            Treatment("a", """{ "nodes": { "gate": { "config": { "left": true } } } }"""),
            Treatment("b", """{ "nodes": { "gate": { "config": { "right": true } } } }"""),
            Treatment("c", """{ "log_taps": { "probe": { "group": "all", "attach": { "output": true }, "columns_from": "gate.inputs" } } }"""),
            Treatment("d", """{ "request_columns": { "maybe": { "type": "int64?" } } }"""),
        };
        placements.AddRange(Filters(10)); // 16,384 combinations, past the limit of 4,096; the tap's scope has three layers
        var request = With(host.Request, placements);
        var environment = new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new GateInputsFactory()) };
        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t", "b/t", "c/t"], environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t", "b/t", "c/t", "d/t"], environment).Succeeded);

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Report.CombinationCheckExhaustive);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "/arms/t/log_taps/probe"), (error.Code, error.Pointer));
        Assert.Contains("'req.maybe'", error.Message, StringComparison.Ordinal);
    }

    public sealed class GateInputsConfig
    {
        public bool Left { get; init; }

        public bool Right { get; init; }
    }

    /// <summary><c>GateInputs@1</c>: names an input for taps (columns_from) that reads <c>req.maybe</c> only when both options are on.</summary>
    private sealed class GateInputsFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("GateInputs", 1, Effects.None, typeof(GateInputsConfig), "Names an input two options decide.");

        public Operator? Compile(OperatorCompileContext context) => context.BindConfig<GateInputsConfig>() is { } config
            ? new Gate(config.Left && config.Right ? "true ? 1 : (req.maybe ?? 0)" : "1")
            : null;

        private sealed class Gate(string source) : Operator
        {
            public override IReadOnlyDictionary<string, string> NamedInputs { get; } = new Dictionary<string, string> { ["feature"] = source };

            public override ValueTask ExecuteAsync(OperatorContext context) => ValueTask.CompletedTask;
        }
    }

    /// <summary>A request column the baseline declares is the same in every arm: an arm may not change or delete it.</summary>
    [Theory]
    [InlineData("""{ "type": "int64?" }""")]
    [InlineData("null")]
    public void AnArm_MayNotChangeABaselineRequestColumn(string change)
    {
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request, [Treatment("a", $$"""{ "request_columns": { "candidates.recalled": {{change}} } }""")]);

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("PATCH_ADD_ONLY", "experiment:a"), (error.Code, error.Artifact));
        Assert.StartsWith("/arms/t/request_columns/candidates.recalled", error.Pointer, StringComparison.Ordinal);
    }

    /// <summary>The home feed with <c>cand.n</c> declared and exp_req_when in its own layer: control, and t changing <paramref name="nodes"/>.</summary>
    private static HomeFeedTestHost WithExperiment(string nodes) => HomeFeedTestHost.LoadModified(root =>
    {
        ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"request_columns\": {", "\"request_columns\": { \"cand.n\": { \"type\": \"int64\" },");
        File.WriteAllText(Path.Combine(root, "experiments", "exp_req_when.jsonc"), $$"""
            { "experiment_id": "exp_req_when", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
              "arms": { "control": {}, "t": { "nodes": {{nodes}} } } }
            """);
        ReplayTests.Edit(root, "releases/home_feed.local.jsonc",
            "\"exp_quality_weight\":        { \"layer\": \"params\" }",
            "\"exp_quality_weight\":        { \"layer\": \"params\" }, \"exp_req_when\": { \"layer\": \"cap\" }");
    });
}
