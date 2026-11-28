namespace StrataRec.IntegrationTests;

public sealed class InteractionWarningTests
{
    internal static void Unreviewed(string root) => ReplayTests.Edit(root, "experiments/exp_mmr_diversity.jsonc",
        "\"reviewed_interactions\": { \"exp_freshness_calibration\": [\"score.final\"] },", "");

    [Fact]
    public void AnAddedNodeReadingWhatAnotherExperimentsAddedNodeWrites_Warns_UntilTheReviewIsRecorded()
    {
        var compilation = HomeFeedTestHost.LoadModified(Unreviewed).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var warning = Assert.Single(compilation.Diagnostics);
        Assert.Equal("INTERACTION_READ", warning.Code);
        Assert.Equal("experiment:exp_mmr_diversity", warning.Artifact);
        Assert.Equal("/arms/mmr/nodes/mmr_diversifier", warning.Pointer);
        Assert.Contains("'exp_freshness_calibration/calibrated' writes in the node it adds, 'freshness_calibration'", warning.Message);
        Assert.Contains("\"reviewed_interactions\": { \"exp_freshness_calibration\": [\"score.final\"] }", warning.Hint);
        Assert.Contains("'exp_mmr_diversity'", warning.Hint);
    }

    [Fact]
    public void TheReview_MayBeRecordedByEitherExperiment()
    {
        var compilation = HomeFeedTestHost.LoadModified(root =>
        {
            Unreviewed(root);
            ReplayTests.Edit(root, "experiments/exp_freshness_calibration.jsonc", "\"expires_at\": \"2099-12-31\",",
                "\"expires_at\": \"2099-12-31\", \"reviewed_interactions\": { \"exp_mmr_diversity\": [\"score.final\"] },");
        }).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void ExperimentsTuningAnExistingNode_DoNotInteract()
    {
        var compilation = HomeFeedTestHost.LoadModified(root =>
        {
            Experiment(root, "exp_tune_a", "tune_a", """{ "nodes": { "fusion": { "config": { "multipliers": { "a": "1.1" } } } } }""");
            Experiment(root, "exp_tune_b", "tune_b", """{ "nodes": { "fusion": { "config": { "multipliers": { "b": "0.9" } } } } }""");
        }).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void TwoAddedNodesWritingOneFlowColumn_Warn()
    {
        var compilation = HomeFeedTestHost.LoadModified(root => Experiment(root, "exp_boost", "boost",
            """{ "nodes": { "boost": { "order": 480, "op": "ExpressionScorer@1", "config": { "output_col": "score.final", "expr": "col.score.final * 1.1" } } } }""")).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var warning = Assert.Single(compilation.Diagnostics, d => d.Code == "INTERACTION_FLOW_COLUMN");
        Assert.Contains("'exp_boost/boost' ('boost')", warning.Message);
        Assert.Contains("exp_freshness_calibration/calibrated", warning.Message);
        // MMR's added node reads score.final, which boost now writes too.
        Assert.Contains(compilation.Diagnostics, d => d.Code == "INTERACTION_READ" && d.Message.Contains("'exp_boost/boost'", StringComparison.Ordinal));
    }

    [Fact]
    public void AReviewNoInteractionNeeds_IsAWarning_AndOneOfAnExperimentNotReleasedIsNot()
    {
        var compilation = HomeFeedTestHost.LoadModified(root =>
        {
            // In one layer, the two experiments' arms exclude each other: the review has nothing left to cover.
            ReplayTests.Edit(root, "releases/home_feed.local.jsonc", "\"exp_mmr_diversity\":         { \"layer\": \"rerank\" }",
                "\"exp_mmr_diversity\":         { \"layer\": \"fusion\" }");
            ReplayTests.Edit(root, "experiments/exp_mmr_diversity.jsonc", "[\"score.final\"] },", "[\"score.final\"], \"exp_elsewhere\": [\"score.final\"] },");
        }).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var warning = Assert.Single(compilation.Diagnostics);
        Assert.Equal("INTERACTION_REVIEW_UNUSED", warning.Code);
        Assert.Equal("experiment:exp_mmr_diversity", warning.Artifact);
        Assert.Equal("/reviewed_interactions/exp_freshness_calibration/0", warning.Pointer);
    }

    [Fact]
    public void AChangedNodeThatNowWritesAColumn_InteractsAsAnAddedOneDoes()
    {
        var compilation = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"rank_sort\": {",
                "\"swap\": { \"op\": \"ExpressionFilter@1\", \"order\": 505, \"config\": { \"predicate\": \"true\" } },\n    \"rank_sort\": {");
            Experiment(root, "exp_swap", "t",
                """{ "nodes": { "swap": { "op": "ExpressionScorer@1", "config": { "predicate": null, "output_col": "score.final", "expr": "0.0" } } } }""");
            Experiment(root, "exp_reader", "t",
                """{ "nodes": { "new_filter": { "op": "ExpressionFilter@1", "order": 510, "config": { "predicate": "col.score.final > 0.0" } } } }""");
        }).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.Contains(compilation.Diagnostics, d => d.Code == "INTERACTION_READ" && d.Message.Contains("'exp_swap/t' writes in the node it adds, 'swap'", StringComparison.Ordinal));
    }

    [Fact]
    public void AReadChangedFromPinnedToCurrent_Interacts_APinnedReadDoesNot()
    {
        StrataRec.Experiments.Compilation.PlanCompilation Compile(string readerArm) => HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"rank_sort\": {",
                "\"reader\": { \"op\": \"ExpressionFilter@1\", \"order\": 490, \"config\": { \"predicate\": \"(col.score.final@fusion ?? 0.0) > 0.0\" } },\n    \"rank_sort\": {");
            Experiment(root, "exp_writer", "on",
                """{ "nodes": { "zero": { "op": "ExpressionScorer@1", "order": 480, "config": { "output_col": "score.final", "expr": "0.0" } } } }""");
            Experiment(root, "exp_reader", "on", readerArm);
        }).Compile();

        var current = Compile("""{ "nodes": { "reader": { "config": { "predicate": "col.score.final > 0.0" } } } }""");
        var pinned = Compile("""{ "nodes": { "reader": { "config": { "predicate": "(col.score.final@fusion ?? 1.0) > 0.5" } } } }""");

        Assert.True(current.Succeeded, string.Join("\n", current.Diagnostics));
        Assert.Contains(current.Diagnostics, d => d.Code == "INTERACTION_READ" && d.Message.Contains("'reader'", StringComparison.Ordinal));
        Assert.True(pinned.Succeeded, string.Join("\n", pinned.Diagnostics));
        Assert.DoesNotContain(pinned.Diagnostics, d => d.Code == "INTERACTION_READ" && d.Message.Contains("'reader'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("skip", "", """{ "nodes": { "late": { "on_failure": "fallback", "fallback": "late_backup" } } }""", true)]
    [InlineData("fallback", "old_backup", """{ "nodes": { "late": { "fallback": "late_backup" } } }""", true)]
    [InlineData("fallback", "late_backup", """{ "nodes": { "late": { "config": { "expr": "2.0" } } } }""", false)]
    public void AFallbackAnArmTurnsOnOrSwitchesTo_AddsWhatItReads(string onFailure, string fallback, string armPatch, bool warns)
    {
        var compilation = HomeFeedTestHost.LoadModified(root =>
        {
            var policy = fallback.Length == 0 ? $"\"on_failure\": \"{onFailure}\"" : $"\"on_failure\": \"{onFailure}\", \"fallback\": \"{fallback}\"";
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"score.final\":  {",
                "\"score.extra\":  { \"type\": \"float32\", \"default\": 0.0 },\n    \"score.final\":  {");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"rank_sort\": {", $$"""
                "late": { "order": 505, "op": "ExpressionScorer@1", {{policy}}, "config": { "output_col": "score.extra", "expr": "1.0" } },
                "late_backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.extra", "expr": "col.score.final" } },
                "old_backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.extra", "expr": "1.0" } },
                "rank_sort": {
                """);
            Experiment(root, "exp_writer", "on",
                """{ "nodes": { "zero": { "op": "ExpressionScorer@1", "order": 480, "config": { "output_col": "score.final", "expr": "0.0" } } } }""");
            Experiment(root, "exp_activate", "on", armPatch);
        }).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var warned = compilation.Diagnostics.Where(d => d.Code == "INTERACTION_READ" && d.Message.Contains("'exp_activate/on'", StringComparison.Ordinal)
            && d.Message.Contains("'exp_writer/on'", StringComparison.Ordinal)).ToList();
        if (warns)
        {
            var warning = Assert.Single(warned);
            Assert.StartsWith("As 'exp_activate/on' changes node 'late', it fails over to 'late_backup', reading 'score.final', which 'exp_writer/on' writes", warning.Message);
            Assert.EndsWith("/arms/on/nodes/late", warning.Pointer);
        }
        else
        {
            Assert.Empty(warned);
        }
    }

    [Fact]
    public void WithAnError_NoReviewIsCalledUnused()
    {
        var compilation = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "experiments/exp_mmr_diversity.jsonc",
            "\"lambda_param\": \"mmr_lambda\",", "\"lambda_param\": \"mmr_lambda\", \"unknown_key\": true,")).Compile();

        Assert.Contains(compilation.Diagnostics, d => d.Code == "NODE_BAD_CONFIG");
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Code == "INTERACTION_REVIEW_UNUSED");
    }

    internal static void Experiment(string root, string id, string arm, string patch)
    {
        File.WriteAllText(Path.Combine(root, "experiments", id + ".jsonc"), $$"""
            { "experiment_id": "{{id}}", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
              "arms": { "control": {}, "{{arm}}": {{patch}} } }
            """);
        ReplayTests.Edit(root, "releases/home_feed.local.jsonc", "\"exp_quality_weight\":",
            $"\"{id}\": {{ \"layer\": \"{id}\" }},\n    \"exp_quality_weight\":");
    }
}
