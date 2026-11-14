using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A <c>ListwiseReranker@1</c> and a <c>PairwiseRanker@1</c> in the sample, their models bound to <c>random@1</c> through
/// the binding's settings alone (design 6.4 item 1): the stand-in declares the listwise and pairwise signatures, so the
/// plan loads as against the real models, the rankers order the window by the seeded stand-in scores, and a recording
/// replays identically.
/// </summary>
public sealed class RandomRankerModelTests
{
    private const string Pipeline = "pipelines/home_feed.jsonc";

    /// <summary>As <c>PairwiseRanker@1</c> sends them: columns as <c>[pairs, 2]</c>, a context value as <c>[pairs]</c>.</summary>
    private const string PairBinding = """
        { "client": "random@1",
          "inputs": { "final": { "type": "float32", "shape": [-1, 2] }, "item_id": { "type": "int64", "shape": [-1, 2] }, "quality": { "type": "float32", "shape": [-1] } },
          "outputs": { "p_first": { "shape": [-1] } } }
        """;

    /// <summary>As <c>ListwiseReranker@1</c> sends them: columns as <c>[1, n]</c>, a context value as <c>[1]</c>.</summary>
    private const string ListBinding = """
        { "client": "random@1",
          "inputs": { "final": { "type": "float32", "shape": [1, -1] }, "smoothed_ctr": { "type": "float32", "shape": [1, -1] }, "seed": { "type": "int64", "shape": [1] } },
          "outputs": { "scores": { "shape": [1, -1] } } }
        """;

    [Fact]
    public async Task BoundToRandom_TheRankersLoad_OrderTheWindowDeterministically_ByTheRequestSeed_AndReplayIdentically()
    {
        var host = HomeFeedTestHost.LoadModified(root => AddRankers(root, PairBinding, ListBinding));
        var (engine, _) = MetricsTests.LoadEngine(host); // the signature checks pass against the stand-in

        var recorded = await engine.ExecuteAsync(Request("req-random-rankers"));

        Assert.True(recorded.Succeeded, recorded.Error);
        var run = Assert.Single(recorded.Recording!.Runs);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "listwise_rerank").Outcome); // not skipped: the stand-in answered
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "pairwise_rerank").Outcome);
        var pairCalls = run.ModelCalls.Where(c => c.Model == "pair_ranker").ToList();
        Assert.Equal(new[] { 3, 4, 4, 4 }, pairCalls.Select(c => c.BatchSize).Order()); // window 6: 15 pairs in batches of 4
        Assert.All(pairCalls, c => Assert.Equal("ok", c.Status));
        var listCall = Assert.Single(run.ModelCalls, c => c.Model == "list_ranker");
        Assert.Equal(("ok", 1), (listCall.Status, listCall.BatchSize));

        // The top 6 in the pairwise order (expected wins: each pair gives its two rows 1 in all), then the rest of the listwise window.
        var top = Ranked(recorded);
        Assert.True(top.Count >= 8, $"{top.Count} items served");
        var wins = top.Take(6).Select(i => i.Pairwise!.Value).ToArray();
        Assert.Equal(15.0, wins.Sum(), 4);
        Assert.Equal(wins.OrderDescending(), wins);
        Assert.All(top.Skip(6), i => Assert.Null(i.Pairwise));
        Assert.True(top[6].Listwise >= top[7].Listwise);
        Assert.True(top.Take(6).Min(i => i.Listwise) >= top[6].Listwise);

        // The same request ranks the same; another request seed reranks the same window.
        Assert.Equal(top, Ranked(await engine.ExecuteAsync(Request("req-random-rankers"))));
        var reseeded = await engine.ExecuteAsync(Request("req-random-rankers-2"));
        Assert.NotEqual(recorded.Recording.Request.Seed, reseeded.Recording!.Request.Seed);
        var other = Ranked(reseeded);
        Assert.Equal(top.Take(8).Select(i => i.Id).Order(), other.Take(8).Select(i => i.Id).Order());
        Assert.NotEqual(top.Take(8).Select(i => i.Id), other.Take(8).Select(i => i.Id));

        // Through JSON, on a fresh engine.
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));
        var (fresh, _) = MetricsTests.LoadEngine(host);
        var outcome = await fresh.ReplayAsync(bundle);

        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal(recorded.Items.Select(i => (i.Id.Int64Value, i.Rank)), outcome.Result.Items.Select(i => (i.Id.Int64Value, i.Rank)));
        Assert.Empty(outcome.Report.Gaps);
        Assert.All(outcome.Report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
    }

    /// <summary>The load-time check runs against what the stand-in declares: a shape the operator does not send is an error at the node.</summary>
    [Theory]
    [InlineData("""{ "client": "random@1", "inputs": { "final": { "type": "float32", "shape": [-1] }, "item_id": { "type": "int64", "shape": [-1, 2] }, "quality": { "type": "float32", "shape": [-1] } }, "outputs": { "p_first": { "shape": [-1] } } }""",
        ListBinding, "MODEL_TENSOR_MISMATCH", "/nodes/pairwise_rerank/config/inputs/final")]
    [InlineData(PairBinding,
        """{ "client": "random@1", "inputs": { "final": { "type": "float32", "shape": [-1] }, "smoothed_ctr": { "type": "float32", "shape": [-1] }, "seed": { "type": "int64", "shape": [-1] } }, "outputs": ["scores"] }""",
        "MODEL_TENSOR_UNSUPPORTED", "/nodes/listwise_rerank/config/output")]
    public void ABindingWhoseShapesDoNotFitTheOperator_IsAnErrorAtTheNode(string pairBinding, string listBinding, string code, string pointer)
    {
        var host = HomeFeedTestHost.LoadModified(root => AddRankers(root, pairBinding, listBinding));

        var compilation = PlanCompiler.Compile(host.Request, host.Environment);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.IsError && d.Code == code);
        Assert.Equal(("pipeline:home_feed", pointer), (error.Artifact, error.Pointer));
    }

    /// <summary>
    /// After <c>rank_sort</c>: a listwise rerank of the top 8, then a pairwise rerank of its top 6 in batches of 4 pairs,
    /// so both shape the served order; each fails the request rather than being skipped, with its scores in the output.
    /// </summary>
    private static void AddRankers(string root, string pairBinding, string listBinding)
    {
        ReplayTests.Edit(root, Pipeline, "    \"diversity_cap\": {",
            """
                "listwise_rerank": {
                  "order": 505, "op": "ListwiseReranker@1",
                  "config": {
                    "model": "list_ranker", "output": "scores", "window": 8, "score_col": "score.listwise",
                    "inputs": { "final": "col.score.final", "smoothed_ctr": "col.item.smoothed_ctr", "seed": "ctx.request.seed" }
                  }
                },
                "pairwise_rerank": {
                  "order": 510, "op": "PairwiseRanker@1",
                  "config": {
                    "model": "pair_ranker", "output": "p_first", "window": 6, "batch_size": 4, "score_col": "score.pairwise",
                    "inputs": { "final": "col.score.final", "item_id": "col.entity_id", "quality": "param.quality_weight" }
                  }
                },
                "diversity_cap": {
            """);
        ReplayTests.Edit(root, Pipeline, "\"primary_source\": \"col.recall.primary_source\"",
            "\"primary_source\": \"col.recall.primary_source\", \"pairwise\": \"col.score.pairwise\", \"listwise\": \"col.score.listwise\"");
        ReplayTests.Edit(root, "bindings/local.jsonc", "\"models\": {",
            $$"""
            "models": {
                "pair_ranker": {{pairBinding}},
                "list_ranker": {{listBinding}},
            """);
    }

    /// <summary>Surface <c>explore</c>: the diversity cap (home only) does not reorder what the rankers ordered.</summary>
    private static RecRequest Request(string requestId) => ReplayTests.Request(requestId, 1001, "explore", ReplayTests.ControlArms);

    private static List<(long Id, double? Pairwise, double Listwise)> Ranked(RecResult result)
    {
        Assert.True(result.Succeeded, result.Error);
        return result.Items.Select(i => (i.Id.Int64Value, i.Columns["pairwise"].IsMissing ? (double?)null : i.Columns["pairwise"].AsFloat64(),
            i.Columns["listwise"].IsMissing ? double.NegativeInfinity : i.Columns["listwise"].AsFloat64())).ToList();
    }
}
