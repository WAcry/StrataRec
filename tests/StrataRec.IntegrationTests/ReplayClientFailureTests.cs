using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Models;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Pipeline.Planning;
using StrataRec.Core.Services;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A model client that throws gives the operator a failed status naming the exception, live and on replay:
/// the recording has the status, so a replay takes the same path without recreating the client's exception type.
/// </summary>
public sealed class ReplayClientFailureTests
{
    [Fact]
    public async Task AnOperatorHandlingAFailedCall_TakesTheSamePathOnReplay()
    {
        // The baseline pipeline without experiments or taps (they read ctr_ranker's model inputs, which the operator lacks).
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.LogTaps.Clear();
        pipeline.Nodes["ctr_ranker"] = new NodeSpec
        {
            Order = 400, Op = "CheckingScorer@1", OnFailure = "fail_request",
            Config = new JsonObject { ["output_col"] = "score.ctr", ["expr"] = "col.item.smoothed_ctr" },
        };
        var errors = new List<string?>();
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Operators = OperatorRegistry.CreateDefault().Add(new CheckingScorerFactory(errors)),
        });
        var load = engine.Load(new PlanCompileRequest
        {
            Pipeline = pipeline, Catalog = host.Request.Catalog, Bindings = host.Request.Bindings, Tables = host.Request.Tables, Policy = host.Request.Policy,
        });
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-catch", 1001, "home", arms: null), TestContext.Current.CancellationToken);
        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(10, recorded.Items.Count);
        var call = Assert.Single(recorded.Recording!.Runs[0].ServiceCalls, c => c.Role == "model");
        Assert.Equal(("error", false, "threw TransientModelException: the model is warming up"), (call.Status, call.Threw, call.Error));

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal("succeeded", outcome.Report.Nodes.Single(n => n.Node == "ctr_ranker").Replayed);
        Assert.Equal(new[] { call.Error, call.Error }, errors); // what the operator saw, live and replayed
    }

    [Theory]
    [InlineData("timeout", "model 'ctr_model' failed: threw TimeoutException: backend timed out")]
    [InlineData("refused", "model 'ctr_model' failed: threw InvalidOperationException: backend refused")]
    public async Task ANodeFailedByAThrowingClient_FailsWithTheSameDetailOnReplay(string mode, string detail)
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"on_failure\": \"fallback\", \"fallback\": \"ctr_backup\"", "\"on_failure\": \"fail_request\""));
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Bindings = PlanEnvironment.DefaultBindings().Add(new ThrowingModels(mode == "timeout"
                ? () => new TimeoutException("backend timed out")
                : () => new InvalidOperationException("backend refused"))),
        });
        Assert.True(engine.Load(host.Request).Applied);

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-throw-" + mode, 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);
        Assert.Equal((false, $"node 'ctr_ranker' failed: {detail}"), (recorded.Succeeded, recorded.Error));

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(recorded.Error, outcome.Result.Error);
        var node = outcome.Report.Nodes.Single(n => n.Node == "ctr_ranker");
        Assert.Equal(("failed", detail), (node.Replayed, node.ReplayedDetail));
    }

    /// <summary>A client exception.</summary>
    public sealed class TransientModelException(string message) : Exception(message);

    /// <summary>Calls a model through the framework, and scores with an expression when the call failed.</summary>
    private sealed class CheckingScorerFactory(List<string?> errors) : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } =
            new("CheckingScorer", 1, Effects.ColumnWriter, typeof(ExpressionScorerConfig), "Scores with a model, or an expression when the model is unavailable.");

        public Operator? Compile(OperatorCompileContext context) =>
            new ExpressionScorerFactory().Compile(context) is { } fallback ? new CheckingScorer(fallback, errors) : null;

        private sealed class CheckingScorer(Operator fallback, List<string?> errors) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var rows = context.LiveRows.ToArray();
                var call = new ServiceCall { Service = "flaky", Inputs = Array.Empty<NamedInput>(), BatchSize = rows.Length, Items = context.KeysOf(rows) };
                var status = await context.CallAsync(new BoundService("flaky", new FlakyClient(), IoRoles.Model, null), call, new ServiceResponse(context.Memory));
                lock (errors)
                {
                    errors.Add(status.Error);
                }

                if (!status.Succeeded)
                {
                    await fallback.ExecuteAsync(context);
                }
            }
        }

        private sealed class FlakyClient : IServiceClient
        {
            public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ServiceSignature(Array.Empty<TensorSpec>(), Array.Empty<TensorSpec>()));

            public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) =>
                throw new TransientModelException("the model is warming up");
        }
    }

    private sealed class ThrowingModels(Func<Exception> exception) : IBindingFactory<IServiceClient>
    {
        private readonly LinearModelClientFactory _inner = new();

        public string Type => _inner.Type;

        public Type SettingsType => _inner.SettingsType;

        public IServiceClient Create(BindingContext context) => new Client(_inner.Create(context), exception);

        private sealed class Client(IServiceClient inner, Func<Exception> exception) : IServiceClient
        {
            public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
                inner.DescribeAsync(model, version, cancellationToken);

            public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) => throw exception();
        }
    }
}
