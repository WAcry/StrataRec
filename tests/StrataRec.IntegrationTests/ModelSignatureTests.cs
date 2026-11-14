using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Experiments.Compilation;
using StrataRec.Models;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>ModelScorer@1</c> rejects at load time every model version it could not call (design 6.9), at the node's config
/// key, rather than loading cleanly and failing or falling back on every request.
/// </summary>
public sealed class ModelSignatureTests
{
    private static readonly int[] Rows = { -1 };

    [Fact]
    public void TheSampleModel_Loads()
    {
        Assert.True(Compile(HomeFeedTestHost.Load(), version => Linear()).Succeeded);
    }

    [Theory]
    [InlineData("int64 input", "/nodes/ctr_ranker/config/inputs/smoothed_ctr", "MODEL_TENSOR_MISMATCH")]
    [InlineData("matrix input", "/nodes/ctr_ranker/config/inputs/smoothed_ctr", "MODEL_TENSOR_MISMATCH")]
    [InlineData("extra input", "/nodes/ctr_ranker/config/model", "MODEL_INPUT_MISSING")]
    [InlineData("matrix output", "/nodes/ctr_ranker/config/outputs/p_click", "MODEL_TENSOR_UNSUPPORTED")]
    public void AVersionTheScorerCannotCall_IsALoadError_AtItsConfigKey(string mismatch, string pointer, string code)
    {
        var signature = mismatch switch
        {
            "int64 input" => Linear(smoothedCtr: new TensorSpec("smoothed_ctr", TensorElementType.Int64, Rows)),
            "matrix input" => Linear(smoothedCtr: new TensorSpec("smoothed_ctr", TensorElementType.Float32, new[] { -1, 2 })),
            "extra input" => Linear(extra: new TensorSpec("tenure", TensorElementType.Float32, Rows)),
            _ => Linear(pClick: new TensorSpec("p_click", TensorElementType.Float32, new[] { -1, 2 })),
        };

        var compilation = Compile(HomeFeedTestHost.Load(), _ => signature);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, IsModelError);
        Assert.Equal(("pipeline:home_feed", pointer, code), (error.Artifact, error.Pointer, error.Code));
        Assert.Contains("version 'default'", error.Message);
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    [Fact]
    public void EveryVersionTheNodeMayUse_IsChecked()
    {
        // ctr_ranker picks its version from a parameter; v2 takes an int64 input. v1 alone would load.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            const string pipeline = "pipelines/home_feed.jsonc";
            ReplayTests.Edit(root, pipeline, "\"quality_weight\": { \"type\": \"float64\", \"min\": 0, \"max\": 1 }",
                "\"quality_weight\": { \"type\": \"float64\", \"min\": 0, \"max\": 1 }, \"model_version\": { \"type\": \"string\", \"enum\": [\"v1\", \"v2\"] }");
            ReplayTests.Edit(root, pipeline, "\"quality_weight\": 0.3", "\"quality_weight\": 0.3, \"model_version\": \"v1\"");
            ReplayTests.Edit(root, pipeline, "\"model\": \"ctr_model\",", "\"model\": \"ctr_model\", \"version_param\": \"model_version\",");
        });

        var compilation = Compile(host, version => version == "v2"
            ? Linear(logFollowers: new TensorSpec("log_followers", TensorElementType.Int64, Rows))
            : Linear());

        var error = Assert.Single(compilation.Diagnostics, IsModelError);
        Assert.Equal(("/nodes/ctr_ranker/config/inputs/log_followers", "MODEL_TENSOR_MISMATCH"), (error.Pointer, error.Code));
        Assert.Equal("Model 'ctr_model' version 'v2' declares input 'log_followers' as int64 [-1], but it maps col.author.log_followers (float32), "
            + "which ModelScorer@1 sends as float32 [-1].", error.Message);
    }

    /// <summary>The scorer's own errors; the log tap that reads ctr_ranker's inputs fails with it and reports its own.</summary>
    private static bool IsModelError(ConfigDiagnostic d) => d.IsError && d.Code.StartsWith("MODEL_", StringComparison.Ordinal);

    private static PlanCompilation Compile(HomeFeedTestHost host, Func<string?, ModelSignature> signature) =>
        PlanCompiler.Compile(host.Request, new PlanEnvironment { BaseDirectory = host.Root, Models = new ModelClientRegistry().Add(new SignatureModels(signature)) });

    /// <summary>The sample model's signature (what <c>linear@1</c> declares for it), with one tensor replaced or added.</summary>
    private static ModelSignature Linear(TensorSpec? smoothedCtr = null, TensorSpec? logFollowers = null, TensorSpec? extra = null, TensorSpec? pClick = null)
    {
        var inputs = new List<TensorSpec>
        {
            logFollowers ?? new TensorSpec("log_followers", TensorElementType.Float32, Rows),
            smoothedCtr ?? new TensorSpec("smoothed_ctr", TensorElementType.Float32, Rows),
        };
        if (extra is not null)
        {
            inputs.Add(extra);
        }

        return new ModelSignature(inputs, new[] { pClick ?? new TensorSpec("p_click", TensorElementType.Float32, Rows) });
    }

    /// <summary>Stands in for <c>linear@1</c> with a signature per version; never called.</summary>
    private sealed class SignatureModels(Func<string?, ModelSignature> signature) : IModelClientFactory
    {
        public string ClientType => LinearModelClientFactory.Type;

        public IModelClient Create(BindingContext context) => new Client(signature);

        private sealed class Client(Func<string?, ModelSignature> signature) : IModelClient
        {
            public string ClientType => LinearModelClientFactory.Type;

            public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
                ValueTask.FromResult(signature(version));

            public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("not called at load time");
        }
    }
}
