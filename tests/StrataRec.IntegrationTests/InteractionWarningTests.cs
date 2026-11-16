namespace StrataRec.IntegrationTests;

/// <summary>
/// Interaction warnings between experiments of different layers (design 11.5): only nodes an arm adds interact, and a
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

    /// <summary>Adds an experiment of one arm (and a control) in a layer of its own.</summary>
    private static void Experiment(string root, string id, string arm, string patch)
    {
        File.WriteAllText(Path.Combine(root, "experiments", id + ".jsonc"), $$"""
            { "experiment_id": "{{id}}", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
              "arms": { "control": {}, "{{arm}}": {{patch}} } }
            """);
        ReplayTests.Edit(root, "releases/home_feed.local.jsonc", "\"exp_quality_weight\":",
            $"\"{id}\": {{ \"layer\": \"{id}\" }},\n    \"exp_quality_weight\":");
    }
}
