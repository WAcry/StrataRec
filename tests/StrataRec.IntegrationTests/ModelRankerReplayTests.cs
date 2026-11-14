using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Models;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The sample with a <c>PairwiseRanker@1</c> and a <c>ListwiseReranker@1</c> after the final sort, each bound to a test
/// model: their calls carry no per-row keys, so a recording keeps each call by the digest of its whole input and a replay
/// serves it back only for the same input (design 13.4). Runs alone: it counts <c>stratarec.model.pairs</c>.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class ModelRankerReplayTests
{
    private const string Pipeline = "pipelines/home_feed.jsonc";

    [Fact]
    public async Task PairwiseAndListwiseNodes_RecordThenReplayIdentically_WithoutGaps_AndAReplayCountsNoPairs()
    {
        var host = HomeFeedTestHost.LoadModified(AddRankers);
        var (pairs, lists) = (new PairModels(), new ListModels());
        var engine = Load(host, pairs, lists);
        using var metrics = new MetricCapture();

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-rankers", 1001, "home", ReplayTests.ControlArms));

        Assert.True(recorded.Succeeded, recorded.Error);
        var run = Assert.Single(recorded.Recording!.Runs);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "pairwise_rerank").Outcome);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "listwise_rerank").Outcome);
        var pairCalls = run.ModelCalls.Where(c => c.Model == "pair_model").ToList();
        Assert.Equal(new[] { 3, 4, 4, 4 }, pairCalls.Select(c => c.BatchSize).Order()); // window 6: 15 pairs in batches of 4
        Assert.All(pairCalls, c => Assert.Equal(("ok", false, 1), (c.Status, c.Keys.HasValue, c.InputDigests.Count))); // one digest per call
        var listCall = Assert.Single(run.ModelCalls, c => c.Model == "list_model");
        Assert.Equal(("ok", 1, false), (listCall.Status, listCall.BatchSize, listCall.Keys.HasValue));
        Assert.Equal(new[] { "final float32 [1, 8]", "seed int64 [1]", "smoothed_ctr float32 [1, 8]" }, lists.Seen);
        Assert.Equal(15.0, PairsCounted(metrics));
        var scored = recorded.Items.Where(i => !i.Columns["pairwise"].IsMissing).ToList();
        Assert.InRange(scored.Count, 1, 6); // the window's rows, of those served
        Assert.All(scored, i => Assert.InRange(i.Columns["pairwise"].AsFloat64(), 0, 5));

        // Through JSON, on a fresh engine whose models would answer differently: every answer must come from the recording.
        metrics.Measurements.Clear();
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));
        var (replayPairs, replayLists) = (new PairModels(bias: 1f), new ListModels(bias: 1f));
        var outcome = await Load(host, replayPairs, replayLists).ReplayAsync(bundle);

        var report = outcome.Report;
        Assert.True(report.Diff.Identical, ReplayTests.Describe(report));
        Assert.Equal(recorded.Items.Select(i => (i.Id.Int64Value, i.Rank)), outcome.Result.Items.Select(i => (i.Id.Int64Value, i.Rank)));
        Assert.Empty(report.Gaps);
        Assert.All(report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
        Assert.Empty(replayPairs.Seen);
        Assert.Empty(replayLists.Seen);
        Assert.Equal(0.0, PairsCounted(metrics)); // a replay is not traffic
    }

    [Fact]
    public async Task AfterAnInputChanges_TheReplayReportsTheCallsAsNotRecorded_AndTheNodesFollowTheirOnFailure()
    {
        var host = HomeFeedTestHost.LoadModified(AddRankers);
        var recorded = await Load(host, new PairModels(), new ListModels()).ExecuteAsync(ReplayTests.Request("req-rankers-changed", 1001, "home", ReplayTests.ControlArms));
        Assert.True(recorded.Succeeded, recorded.Error);

        // Each ranker reads the model's score instead of the one it was recorded with.
        var modified = HomeFeedTestHost.LoadModified(root =>
        {
            AddRankers(root);
            ReplayTests.Edit(root, Pipeline, "\"final\": \"col.score.final\", \"item_id\"", "\"final\": \"col.score.ctr\", \"item_id\"");
            ReplayTests.Edit(root, Pipeline, "\"smoothed_ctr\": \"col.item.smoothed_ctr\", \"seed\"", "\"smoothed_ctr\": \"col.score.ctr\", \"seed\"");
        });
        var (pairs, lists) = (new PairModels(), new ListModels());
        var outcome = await Load(modified, pairs, lists).ReplayAsync(recorded.Recording!);

        var report = outcome.Report;
        Assert.True(outcome.Result.Succeeded, outcome.Result.Error); // on_failure: skip
        Assert.Contains(report.Gaps, g => g is { Kind: "model", Source: "pair_model", Reason: "not_recorded" });
        Assert.Contains(report.Gaps, g => g is { Kind: "model", Source: "list_model", Reason: "not_recorded" });
        Assert.DoesNotContain(report.Gaps, g => g.Source == "ctr_model"); // its inputs did not change
        Assert.Contains(report.Nodes, n => n is { Node: "pairwise_rerank", Recorded: "succeeded", Replayed: "failed" });
        Assert.Contains(report.Nodes, n => n is { Node: "listwise_rerank", Recorded: "succeeded", Replayed: "failed" });
        Assert.All(outcome.Result.Items, i => Assert.True(i.Columns["pairwise"].IsMissing && i.Columns["listwise"].IsMissing)); // nothing committed
        Assert.Empty(pairs.Seen);
        Assert.Empty(lists.Seen);
    }

    /// <summary>
    /// After <c>rank_sort</c>: a pairwise rerank of the top 6 in batches of 4 pairs, then a listwise rerank of the top 8,
    /// both skipped when they fail, with their scores in the output.
    /// </summary>
    private static void AddRankers(string root)
    {
        ReplayTests.Edit(root, Pipeline, "    \"diversity_cap\": {",
            """
                "pairwise_rerank": {
                  "order": 505, "op": "PairwiseRanker@1", "on_failure": "skip",
                  "config": {
                    "model": "pair_model", "output": "p_first", "window": 6, "batch_size": 4, "score_col": "score.pairwise",
                    "inputs": { "final": "col.score.final", "item_id": "col.entity_id", "quality": "param.quality_weight" }
                  }
                },
                "listwise_rerank": {
                  "order": 510, "op": "ListwiseReranker@1", "on_failure": "skip",
                  "config": {
                    "model": "list_model", "output": "scores", "window": 8, "score_col": "score.listwise",
                    "inputs": { "final": "col.score.final", "smoothed_ctr": "col.item.smoothed_ctr", "seed": "ctx.request.seed" }
                  }
                },
                "diversity_cap": {
            """);
        ReplayTests.Edit(root, Pipeline, "\"primary_source\": \"col.recall.primary_source\"",
            "\"primary_source\": \"col.recall.primary_source\", \"pairwise\": \"col.score.pairwise\", \"listwise\": \"col.score.listwise\"");
        ReplayTests.Edit(root, "bindings/local.jsonc", "\"models\": {",
            $$"""
            "models": {
                "pair_model": { "client": "{{PairModels.Type}}" },
                "list_model": { "client": "{{ListModels.Type}}" },
            """);
    }

    private static double PairsCounted(MetricCapture metrics) =>
        metrics.Of(StrataRecMetrics.Names.ModelPairs).Where(m => Equals(m.Tags[StrataRecTags.Model], "pair_model")).Sum(m => m.Value);

    private static RecEngine Load(HomeFeedTestHost host, PairModels pairs, ListModels lists)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Models = ModelClientRegistry.CreateDefault().Add(pairs).Add(lists) });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static NamedInput Find(ReadOnlySpan<NamedInput> inputs, string name)
    {
        foreach (var input in inputs)
        {
            if (input.Name == name)
            {
                return input;
            }
        }

        throw new InvalidOperationException($"no input '{name}'");
    }

    private static string Describe(in NamedInput input) => $"{input.Name} {input.ElementType.ToString().ToLowerInvariant()} [{string.Join(", ", input.Shape)}]";

    /// <summary>
    /// A pairwise model over the final score, the ids and the quality weight: the first row is preferred by the logistic
    /// of their difference (so a replay that served another pair's answer would differ); <c>bias</c> shifts every answer.
    /// </summary>
    private sealed class PairModels(float bias = 0f) : IModelClientFactory, IModelClient
    {
        public const string Type = "pair_test@1";

        private readonly List<string> _seen = new();

        public string ClientType => Type;

        public List<string> Seen
        {
            get
            {
                lock (_seen)
                {
                    return _seen.ToList();
                }
            }
        }

        public IModelClient Create(BindingContext context) => this;

        public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ModelSignature(
                new[]
                {
                    new TensorSpec("final", TensorElementType.Float32, new[] { -1, 2 }), new TensorSpec("item_id", TensorElementType.Int64, new[] { -1, -1 }),
                    new TensorSpec("quality", TensorElementType.Float32, new[] { -1 }),
                },
                new[] { new TensorSpec("p_first", TensorElementType.Float32, new[] { -1 }) }));

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
        {
            var inputs = call.Inputs.Span;
            lock (_seen)
            {
                foreach (var input in inputs)
                {
                    _seen.Add(ModelRankerReplayTests.Describe(input));
                }
            }

            var final = Find(inputs, "final").Float32.Span;
            var ids = Find(inputs, "item_id").Int64.Span;
            var quality = Find(inputs, "quality").Float32.Span;
            var preferences = outputs.GetFloat32("p_first", call.BatchSize).Span;
            for (var p = 0; p < call.BatchSize; p++)
            {
                var logit = bias + (20f * (final[2 * p] - final[(2 * p) + 1])) + (0.01f * ((ids[2 * p] % 5) - (ids[(2 * p) + 1] % 5))) + (0.1f * quality[p]);
                preferences[p] = 1f / (1f + MathF.Exp(-logit));
            }

            return ValueTask.FromResult(ModelStatus.Ok);
        }
    }

    /// <summary>A listwise model: each row's final score relative to the list's mean, plus a little of its CTR and the seed.</summary>
    private sealed class ListModels(float bias = 0f) : IModelClientFactory, IModelClient
    {
        public const string Type = "list_test@1";

        private readonly List<string> _seen = new();

        public string ClientType => Type;

        /// <summary>Every input sent, as <c>name type [shape]</c>, in the order sent.</summary>
        public List<string> Seen
        {
            get
            {
                lock (_seen)
                {
                    return _seen.ToList();
                }
            }
        }

        public IModelClient Create(BindingContext context) => this;

        public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ModelSignature(
                new[]
                {
                    new TensorSpec("final", TensorElementType.Float32, new[] { 1, -1 }), new TensorSpec("smoothed_ctr", TensorElementType.Float32, new[] { -1, -1 }),
                    new TensorSpec("seed", TensorElementType.Int64, new[] { 1 }),
                },
                new[] { new TensorSpec("scores", TensorElementType.Float32, new[] { 1, -1 }) }));

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
        {
            var inputs = call.Inputs.Span;
            lock (_seen)
            {
                foreach (var input in inputs)
                {
                    _seen.Add(ModelRankerReplayTests.Describe(input));
                }
            }

            var final = Find(inputs, "final").Float32.Span;
            var ctr = Find(inputs, "smoothed_ctr").Float32.Span;
            var seed = Find(inputs, "seed").Int64.Span[0];
            var n = final.Length;
            var mean = 0f;
            for (var i = 0; i < n; i++)
            {
                mean += final[i] / n;
            }

            var scores = outputs.GetFloat32("scores", n).Span;
            for (var i = 0; i < n; i++)
            {
                scores[i] = bias + final[i] - mean + (0.5f * ctr[i]) + (0.001f * (seed % 3));
            }

            return ValueTask.FromResult(ModelStatus.Ok);
        }
    }
}
