namespace StrataRec.IntegrationTests;

/// <summary>
/// Interaction warnings between experiments of different layers: only nodes an arm adds interact, and a
/// reviewed interaction recorded in an experiment stops warning, while a review no interaction needs warns.
/// </summary>
public sealed class InteractionWarningTests
{
    /// <summary>Removes the sample's review of calibration × MMR, so its INTERACTION_READ warning is reported.</summary>
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
        // The hint is the entry that records the review.
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

    /// <summary>
    /// Changing an existing node's settings is a parameter change, inline or not: two experiments tuning different
    /// multipliers of fusion, which writes the flow column score.final, compose by the pipeline's structure. Counting the
    /// nodes an arm touches warned for this pair (and for every pair of such experiments), but never when the same change
    /// went through a parameter.
    /// </summary>
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

    /// <summary>
    /// Review 69: an arm that changes an existing node into a writer of a flow column changes the structure, as adding
    /// one does. A filter becomes a writer of score.final, and another experiment's added node reads score.final: the
    /// reader warns, naming the changed node. Settings of a node that keeps its columns still do not.
    /// </summary>
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

    /// <summary>
    /// Review 70: changing a read of a pinned version (col.score.final@fusion) to the current one is a new dependency: a
    /// writer another experiment adds before the node now changes what it reads (the two arms together filtered every
    /// item), so it warns. A pinned read does not see the writer, so it does not.
    /// </summary>
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

    /// <summary>
    /// Review 71: a node's fallback reads when the node fails, so an arm that turns a fallback on, or points the node at
    /// another one, adds what that fallback reads. Turned on here, the fallback read the zero another experiment writes
    /// into score.final, and nothing warned. A fallback the baseline's node already had adds nothing.
    /// </summary>
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

    /// <summary>
    /// Review 69: while the configuration has errors, interactions are not known, so no review is called unused: a node's
    /// config error made the sample's valid review "unused", with a hint to remove it.
    /// </summary>
    [Fact]
    public void WithAnError_NoReviewIsCalledUnused()
    {
        var compilation = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "experiments/exp_mmr_diversity.jsonc",
            "\"lambda_param\": \"mmr_lambda\",", "\"lambda_param\": \"mmr_lambda\", \"unknown_key\": true,")).Compile();

        Assert.Contains(compilation.Diagnostics, d => d.Code == "NODE_BAD_CONFIG");
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Code == "INTERACTION_REVIEW_UNUSED");
    }

    /// <summary>Adds an experiment of one arm (and a control) in a layer of its own.</summary>
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
