using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;
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
