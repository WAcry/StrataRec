using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Models;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A replayed model call that threw throws what the client threw (design 13.4), so operators that handle particular
/// client exceptions, and the failure details of nodes that do not, are the same on replay.
/// </summary>
public sealed class ReplayExceptionTests
{
    [Fact]
    public async Task AnOperatorHandlingAClientException_TakesTheSamePathOnReplay()
    {
        var call = await AssertHandledAlikeOnReplay(message => new TransientModelException(message));

        Assert.Equal((typeof(TransientModelException).FullName, "StrataRec.IntegrationTests", null), (call.ErrorType!.Name, call.ErrorType.Assembly, call.ErrorType.Args));
    }

    [Fact]
    public async Task AnOperatorHandlingAClosedGenericClientException_TakesTheSamePathOnReplay()
    {
        var call = await AssertHandledAlikeOnReplay(message => new BackendException<int>(message));

        Assert.Equal((typeof(BackendException<>).FullName, "StrataRec.IntegrationTests"), (call.ErrorType!.Name, call.ErrorType.Assembly));
        var argument = Assert.Single(call.ErrorType.Args!);
        Assert.Equal(("System.Int32", "System.Private.CoreLib"), (argument.Name, argument.Assembly));
    }

    /// <summary>
    /// Records a request whose operator catches <typeparamref name="TException"/> from its model call and scores without the
    /// model, replays it, and checks the replay took the same path. Returns the recorded call.
    /// </summary>
    private static async Task<ReplayModelCall> AssertHandledAlikeOnReplay<TException>(Func<string, TException> exception)
        where TException : Exception
    {
        // The baseline pipeline without experiments or taps (they read ctr_ranker's model inputs, which the operator lacks).
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.LogTaps.Clear();
        pipeline.Nodes["ctr_ranker"] = new NodeSpec
        {
            Order = 400, Op = "CatchingScorer@1", OnFailure = "fail_request",
            Config = new JsonObject { ["output_col"] = "score.ctr", ["expr"] = "col.item.smoothed_ctr" },
        };
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Operators = OperatorRegistry.CreateDefault().Add(new CatchingScorerFactory<TException>(exception)),
        });
        var load = engine.Load(new PlanCompileRequest
        {
            Pipeline = pipeline, Catalog = host.Request.Catalog, Bindings = host.Request.Bindings, Tables = host.Request.Tables, Policy = host.Request.Policy,
        });
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-catch", 1001, "home", arms: null, deadlineMs: 30_000));
        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(10, recorded.Items.Count);
        var call = Assert.Single(recorded.Recording!.Runs[0].ModelCalls);
        Assert.Equal(("error", true), (call.Status, call.Threw));

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording)));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal("succeeded", outcome.Report.Nodes.Single(n => n.Node == "ctr_ranker").Replayed);
        return call;
    }

    [Theory]
    [InlineData("timeout", "TimeoutException: backend timed out")]
    [InlineData("refused", "InvalidOperationException: backend refused")]
    public async Task ANodeFailedByAThrowingClient_FailsWithTheSameDetailOnReplay(string mode, string detail)
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"on_failure\": \"fallback:ctr_backup\", \"timeout_ms\": 50,", "\"on_failure\": \"fail_request\", \"timeout_ms\": 30000,"));
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Models = new ModelClientRegistry().Add(new ThrowingModels(mode == "timeout"
                ? () => new TimeoutException("backend timed out")
                : () => new InvalidOperationException("backend refused"))),
        });
        Assert.True(engine.Load(host.Request, host.Workspace.ArmRanges("home_feed", "local")).Applied);

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-throw-" + mode, 1001, "home", ReplayTests.ControlArms, deadlineMs: 30_000));
        Assert.Equal((false, $"node 'ctr_ranker' failed: {detail}"), (recorded.Succeeded, recorded.Error));

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!)));

        Assert.Equal(recorded.Error, outcome.Result.Error);
        var node = outcome.Report.Nodes.Single(n => n.Node == "ctr_ranker");
        Assert.Equal(("failed", detail), (node.Replayed, node.ReplayedDetail));
    }

    /// <summary>A client exception an operator knows how to handle.</summary>
    public sealed class TransientModelException(string message) : Exception(message);

    /// <summary>A generic client exception; an operator catches one closed type of it.</summary>
    public sealed class BackendException<TCode>(string message) : Exception(message);

    /// <summary>
    /// Calls a model through the framework, and scores with an expression when the call throws
    /// <typeparamref name="TException"/> (a typed catch of the closed type).
    /// </summary>
    private sealed class CatchingScorerFactory<TException>(Func<string, TException> exception) : IOperatorFactory
        where TException : Exception
    {
        public OperatorDescriptor Descriptor { get; } =
            new("CatchingScorer", 1, Effects.ColumnWriter, typeof(ExpressionScorerConfig), "Scores with a model, or an expression when the model is unavailable.");

        public Operator? Compile(OperatorCompileContext context) =>
            new ExpressionScorerFactory().Compile(context) is { } fallback ? new CatchingScorer(fallback, exception) : null;

        private sealed class CatchingScorer(Operator fallback, Func<string, TException> exception) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var rows = context.LiveRows.ToArray();
                try
                {
                    var call = new ModelCall { Model = "flaky", Inputs = Array.Empty<NamedInput>(), BatchSize = rows.Length };
                    await context.InvokeModelAsync(new FlakyClient(exception), new IoTarget(IoKind.Model, "flaky"), call, new DiscardingWriter(), rows,
                        timeoutMs: null);
                }
                catch (TException)
                {
                    await fallback.ExecuteAsync(context);
                }
            }
        }

        private sealed class FlakyClient(Func<string, TException> exception) : IModelClient
        {
            public string ClientType => "flaky";

            public ModelSignature GetSignature(string model, string? version) => new(Array.Empty<TensorSpec>(), Array.Empty<TensorSpec>());

            public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
                throw exception("the model is warming up");
        }

        private sealed class DiscardingWriter : IModelOutputWriter
        {
            public Memory<float> GetFloat32(string name, int elementCount) => new float[elementCount];

            public void SetText(string name, string text)
            {
            }
        }
    }

    private sealed class ThrowingModels(Func<Exception> exception) : IModelClientFactory
    {
        private readonly LinearModelClientFactory _inner = new();

        public string ClientType => _inner.ClientType;

        public IModelClient Create(string modelName, IReadOnlyDictionary<string, JsonElement> settings) => new Client(_inner.Create(modelName, settings), exception);

        private sealed class Client(IModelClient inner, Func<Exception> exception) : IModelClient
        {
            public string ClientType => inner.ClientType;

            public ModelSignature GetSignature(string model, string? version) => inner.GetSignature(model, version);

            public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) => throw exception();
        }
    }
}
