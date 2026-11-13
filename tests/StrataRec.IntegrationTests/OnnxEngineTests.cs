using StrataRec.Artifacts.Model;
using StrataRec.Cli.Commands;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Workspace;
using StrataRec.Models.Onnx;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>local_onnx@1</c> end to end (design 6.9): the sample's <c>onnx</c> environment serves its ctr model from
/// <c>models/ctr_model.onnx</c> through <c>ModelScorer@1</c> and the engine, and ranks like the <c>local</c> environment,
/// whose linear@1 binding has the same weights.
/// </summary>
public sealed class OnnxEngineTests
{
    /// <summary>Set to <c>1</c> to rewrite the sample's model file from <see cref="OnnxModels.CtrModel"/>.</summary>
    private const string Regenerate = "STRATAREC_REGENERATE_SAMPLES";

    [Fact]
    public void TheSamplesModelFile_IsWhatTheEncoderWrites()
    {
        if (System.Environment.GetEnvironmentVariable(Regenerate) == "1")
        {
            OnnxModels.WriteSample(RepositoryRoot());
        }

        var committed = File.ReadAllBytes(Path.Combine(RepositoryRoot(), OnnxModels.SamplePath));

        Assert.True(OnnxModels.CtrModel().AsSpan().SequenceEqual(committed),
            $"{OnnxModels.SamplePath} differs from tests/Shared/OnnxModels.cs: run the tests with {Regenerate}=1 to rewrite it, and commit it");
    }

    [Fact]
    public async Task TheOnnxEnvironment_ScoresWithTheModelFile_AndRanksLikeTheLinearWeights()
    {
        var root = HomeFeedTestHost.SampleRoot();
        await using var onnx = await Engine(root, "onnx");
        await using var linear = await Engine(root, "local");

        var served = await onnx.ExecuteAsync(Request());
        var expected = await linear.ExecuteAsync(Request());

        Assert.True(served.Succeeded, served.Error);
        Assert.Contains(served.Trace!, o => o is { NodeId: "ctr_ranker", Kind: NodeOutcomeKind.Succeeded, ProducedBy: "ctr_ranker" });
        Assert.Equal(Ids(expected), Ids(served));
        for (var i = 0; i < served.Items.Count; i++)
        {
            Assert.Equal(Score(expected.Items[i]), Score(served.Items[i]), 4);
        }
    }

    [Fact]
    public async Task TheCommandLineRunsTheOnnxEnvironment()
    {
        var root = HomeFeedTestHost.SampleRoot();
        var output = new StringWriter();

        Assert.Equal(0, ValidateCommand.Run(root, "onnx", null, "text", warningsAsErrors: true, output));
        Assert.Equal(0, await RunCommand.RunAsync(root, "onnx", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), true, output,
            CancellationToken.None));
    }

    [Fact]
    public async Task AModelFileThatIsNotThere_RejectsThePlanAtItsUri_AndValidateDoesNotNeedIt()
    {
        var host = HomeFeedTestHost.LoadModified(root => File.Delete(Path.Combine(root, "models", "ctr_model.onnx")));
        await using var engine = new RecEngine(CommonOptions.PlanEnvironment(host.Root));

        var load = await engine.LoadAsync(CompileRequest(host.Root, "onnx"));

        Assert.False(load.Applied);
        var error = Assert.Single(load.Diagnostics, d => d.IsError);
        Assert.Equal(("BINDING_LOAD_FAILED", "bindings:onnx", "/models/ctr_model/uri"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, ValidateCommand.Run(host.Root, "onnx", null, "text", warningsAsErrors: true, TextWriter.Null));
    }

    private static async Task<RecEngine> Engine(string root, string environment)
    {
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = root,
            Models = ModelClientRegistry.CreateDefault().Add(new OnnxModelClientFactory()),
        });
        var load = await engine.LoadAsync(CompileRequest(root, environment));
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static PlanCompileRequest CompileRequest(string root, string environment)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("home_feed", environment, diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return request!;
    }

    private static RecRequest Request() => new()
    {
        PipelineId = "home_feed",
        RequestId = "req-onnx",
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Assignment = Array.Empty<string>(),
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Trace = true },
    };

    private static List<string> Ids(RecResult result) => result.Items.Select(i => i.Id.ToString()).ToList();

    private static double Score(RankedItem item) => item.Columns["score"].AsFloat64();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StrataRec.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("StrataRec.sln not found above " + AppContext.BaseDirectory);
    }
}
