using StrataRec.Core.Bindings;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Models;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The sample's <c>ctr_model</c> also fed an int64 column (<c>col.entity_id</c>) and a vector (<c>item.embedding</c> with a
/// default): <c>ModelScorer@1</c> sends them as int64 <c>[rows]</c> and float32 <c>[rows, 4]</c> (design 6.9), the
/// recording digests each row's slice of them, and a replay on the same configuration computes the same digests, so every
/// call is served from the recording without a gap.
/// </summary>
public sealed class ModelTensorReplayTests
{
    [Fact]
    public async Task AModelFedInt64AndVectorInputs_RecordsThenReplaysIdentically_WithoutGaps()
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "catalog/home_feed.jsonc",
                "\"item.embedding\":      { \"type\": \"vec<float32,4>\", \"source\": \"item_store\", \"field\": \"i_emb\" },",
                "\"item.embedding\":      { \"type\": \"vec<float32,4>\", \"source\": \"item_store\", \"field\": \"i_emb\" },"
                + "\n    \"item.emb_or_zero\": { \"type\": \"vec<float32,4>\", \"source\": \"item_store\", \"field\": \"i_emb\", \"default\": [0, 0, 0, 0] },");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"log_followers\": \"col.author.log_followers\" }",
                "\"log_followers\": \"col.author.log_followers\", \"item_id\": \"col.entity_id\", \"item_emb\": \"col.item.emb_or_zero\" }");
            SampleBindings.RebindModel(root, "ctr_model", $$"""{ "client": "{{TensorModels.Type}}" }""");
        });
        var recording = new TensorModels();
        var engine = Load(host, recording);

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-tensors", 1001, "home", ReplayTests.ControlArms));

        Assert.True(recorded.Succeeded, recorded.Error);
        var run = Assert.Single(recorded.Recording!.Runs);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "ctr_ranker").Outcome); // the model answered: no fallback
        var call = Assert.Single(run.ModelCalls);
        Assert.Equal(("ctr_model", "ok", 30), (call.Model, call.Status, call.Keys!.Value.GetArrayLength()));
        Assert.Equal(new[] { "item_emb float32 [30, 4]", "item_id int64 [30]", "log_followers float32 [30]", "smoothed_ctr float32 [30]" }, recording.Seen);

        // Through JSON, on a fresh engine whose model would answer differently: every output must come from the recording.
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));
        var replaying = new TensorModels(bias: 1.0f);
        var outcome = await Load(host, replaying).ReplayAsync(bundle);

        var report = outcome.Report;
        Assert.True(report.Diff.Identical, ReplayTests.Describe(report));
        Assert.Equal(recorded.Items.Select(i => (i.Id.Int64Value, i.Rank)), outcome.Result.Items.Select(i => (i.Id.Int64Value, i.Rank)));
        Assert.Empty(report.Gaps);
        Assert.All(report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
        Assert.Empty(replaying.Seen);
    }

    private static RecEngine Load(HomeFeedTestHost host, TensorModels models)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Models = ModelClientRegistry.CreateDefault().Add(models) });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    /// <summary>
    /// A model taking the sample's two float32 inputs, an int64 id and a 4-vector, scoring each row from all four (so a
    /// replay that served another row's output would differ), and noting the tensors it was sent.
    /// </summary>
    private sealed class TensorModels(float bias = 0f) : IModelClientFactory, IModelClient
    {
        public const string Type = "tensor_test@1";

        private static readonly int[] Rows = { -1 };

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
                    new TensorSpec("item_emb", TensorElementType.Float32, new[] { -1, 4 }), new TensorSpec("item_id", TensorElementType.Int64, Rows),
                    new TensorSpec("log_followers", TensorElementType.Float32, Rows), new TensorSpec("smoothed_ctr", TensorElementType.Float32, Rows),
                },
                new[] { new TensorSpec("p_click", TensorElementType.Float32, Rows) }));

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
        {
            var inputs = call.Inputs.Span;
            lock (_seen)
            {
                foreach (var input in inputs)
                {
                    _seen.Add($"{input.Name} {input.ElementType.ToString().ToLowerInvariant()} [{string.Join(", ", input.Shape)}]");
                }
            }

            var emb = Find(inputs, "item_emb").Float32.Span;
            var ids = Find(inputs, "item_id").Int64.Span;
            var ctr = Find(inputs, "smoothed_ctr").Float32.Span;
            var followers = Find(inputs, "log_followers").Float32.Span;
            var scores = outputs.GetFloat32("p_click", call.BatchSize).Span;
            for (var r = 0; r < call.BatchSize; r++)
            {
                var logit = bias - 1.5f + (12f * ctr[r]) + (0.05f * followers[r]) + (0.1f * (emb[4 * r] - emb[(4 * r) + 3])) + (0.01f * (ids[r] % 7));
                scores[r] = 1f / (1f + MathF.Exp(-logit));
            }

            return ValueTask.FromResult(ModelStatus.Ok);
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
    }
}
