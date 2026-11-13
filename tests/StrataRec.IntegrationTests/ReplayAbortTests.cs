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

namespace StrataRec.IntegrationTests;

/// <summary>
/// Requests aborted while recorded (design 13.4): the bundle reaches the host through the recording callback while the
/// cancellation still reaches the caller, calls in flight are outstanding, and a replay stops where the recording did.
/// </summary>
/// <remarks>
/// Nothing here depends on timing: the caller cancels once the model client says it has started, and the untimed sample's
/// budgets and deadlines (<see cref="HomeFeedTestHost.UntimedMs"/>) are far longer than any run.
/// </remarks>
public sealed class ReplayAbortTests
{
    [Fact]
    public async Task ARequestCancelledDuringAModelCall_DeliversItsBundle_AndReplaysItsDecisionsUpToTheAbort()
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"on_failure\": \"fallback:ctr_backup\"", "\"on_failure\": \"fail_request\""));
        var models = new BlockingModels();
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Models = new ModelClientRegistry().Add(models) });
        Assert.True(engine.Load(host.Request, host.Workspace.ArmRanges("home_feed", "local")).Applied);
        ReplayBundle? delivered = null;
        using var cancel = new CancellationTokenSource();

        var running = engine.ExecuteAsync(WithCallback(ReplayTests.Request("req-cancelled", 1001, "home", ReplayTests.ControlArms),
            bundle => delivered = bundle), cancel.Token).AsTask();
        await models.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); // recording does not swallow the cancellation
        Assert.NotNull(delivered);
        Assert.Equal((false, true, "cancelled by the caller"), (delivered.Result.Succeeded, delivered.Result.Aborted, delivered.Result.Error));
        var run = Assert.Single(delivered.Runs);
        Assert.True(run.Result.Aborted);
        Assert.Equal(("ctr_ranker", "aborted"), (run.Nodes[^1].Node, run.Nodes[^1].Outcome));
        Assert.NotEmpty(run.ModelCalls);
        Assert.All(run.ModelCalls, call => Assert.Equal("cancelled", call.Status)); // the client honoured the cancellation

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(delivered)));

        Assert.Equal((true, "cancelled by the caller"), (outcome.Aborted, outcome.AbortReason));
        Assert.Empty(outcome.Result.Items);
        Assert.True(outcome.Report.Diff.Identical);
        Assert.True(outcome.Report.Diff.RightAborted);
        Assert.Equal(run.Nodes.Select(n => (n.Node, (string?)n.Outcome, n.Detail)), outcome.Report.Nodes.Select(n => (n.Node, n.Replayed, n.ReplayedDetail)));
        Assert.Equal(0, models.Calls - run.ModelCalls.Count); // the replay never called the client
    }

    /// <summary>
    /// The position running at the abort had already triggered an experiment: through a parameter its node read (the arm
    /// changes the model version) or through its site (the arm patches only the node's timeout). The recording keeps that
    /// exposure on the aborted node, and a replay, which stops before running the position, restores it.
    /// </summary>
    [Theory]
    [InlineData("param")]
    [InlineData("site")]
    public async Task AnAbortedPosition_KeepsTheExposureItCaused_AndTheReplayRestoresIt(string trigger)
    {
        var (engine, bundle) = await RecordAbortedAtTheRanker(trigger);
        var ranker = bundle.Runs[0].Nodes[^1];
        Assert.Equal(("ctr_ranker", "aborted"), (ranker.Node, ranker.Outcome));
        Assert.Equal(new[] { "exp_quality_weight" }, ranker.Triggered);
        Assert.Equal(new[] { "exp_quality_weight" }, bundle.Result.TriggeredExperiments);

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(bundle)));

        Assert.True(outcome.Aborted);
        Assert.Equal(bundle.Result.TriggeredExperiments, outcome.Result.Experiments.Triggered);
        Assert.All(bundle.Runs[0].Nodes.SkipLast(1), n => Assert.Null(n.Triggered)); // only the aborted position carries it
    }

    [Fact]
    public async Task APositionAbortedBeforeItTriggeredAnything_IsReplayedWithoutExposure()
    {
        // The parameter case, as if the abort had come while ctr_ranker still fetched its features, before it read its
        // parameters. (A caller cancelling during a fetch fails the fetch instead of aborting the request, so the bundle is
        // edited.) Not every aborted node read its parameters: the replay restores what was recorded, nothing more.
        var (engine, recorded) = await RecordAbortedAtTheRanker("param");
        var bundle = JsonNode.Parse(ReplayBundleFile.Serialize(recorded))!;
        var nodes = bundle["runs"]![0]!["nodes"]!.AsArray();
        nodes[nodes.Count - 1]!["triggered"] = new JsonArray();
        bundle["runs"]![0]!["result"]!["triggered_experiments"] = new JsonArray();
        bundle["result"]!["triggered_experiments"] = new JsonArray();

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(bundle.ToJsonString()));

        Assert.True(outcome.Aborted);
        Assert.Empty(outcome.Result.Experiments.Triggered);
    }

    /// <summary>Records home_feed under exp_quality_weight/strong, cancelled while ctr_ranker waits for its model.</summary>
    /// <param name="trigger"><c>param</c>: ctr_ranker reads a model version the arm overrides; <c>site</c>: the arm patches ctr_ranker's timeout
    /// (to another untimed value).</param>
    private static async Task<(RecEngine Engine, ReplayBundle Bundle)> RecordAbortedAtTheRanker(string trigger)
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            const string pipeline = "pipelines/home_feed.jsonc";
            const string experiment = "experiments/exp_quality_weight.jsonc";
            const string strong = "\"strong\": { \"params\": { \"quality_weight\": 0.8, \"final_top_k\": 8 } }";
            ReplayTests.Edit(root, pipeline, "\"on_failure\": \"fallback:ctr_backup\"", "\"on_failure\": \"fail_request\"");
            if (trigger == "param")
            {
                ReplayTests.Edit(root, pipeline, "\"quality_weight\": { \"type\": \"float64\", \"min\": 0, \"max\": 1 }",
                    "\"quality_weight\": { \"type\": \"float64\", \"min\": 0, \"max\": 1 }, \"model_version\": { \"type\": \"string\", \"enum\": [\"v1\", \"v2\"] }");
                ReplayTests.Edit(root, pipeline, "\"quality_weight\": 0.3", "\"quality_weight\": 0.3, \"model_version\": \"v1\"");
                ReplayTests.Edit(root, pipeline, "\"model\": \"ctr_model\",", "\"model\": \"ctr_model\", \"version_param\": \"model_version\",");
                ReplayTests.Edit(root, experiment, strong, "\"strong\": { \"params\": { \"quality_weight\": 0.8, \"final_top_k\": 8, \"model_version\": \"v2\" } }");
            }
            else
            {
                ReplayTests.Edit(root, experiment, strong,
                    $"\"strong\": {{ \"params\": {{ \"quality_weight\": 0.8, \"final_top_k\": 8 }}, \"nodes\": {{ \"ctr_ranker\": {{ \"timeout_ms\": {HomeFeedTestHost.UntimedMs + 1} }} }} }}");
            }
        });
        var models = new BlockingModels();
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Models = new ModelClientRegistry().Add(models) });
        var load = engine.Load(host.Request, host.Workspace.ArmRanges("home_feed", "local"));
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        ReplayBundle? delivered = null;
        using var cancel = new CancellationTokenSource();

        var running = engine.ExecuteAsync(WithCallback(ReplayTests.Request("req-exposed-" + trigger, 1001, "home", new[] { "exp_quality_weight/strong" }),
            bundle => delivered = bundle), cancel.Token).AsTask();
        await models.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        return (engine, delivered!);
    }

    [Fact]
    public async Task ACallTheCallerCutOff_IsRecordedAsTheRequestSawIt_AndWhatTheClientReturnsLaterIsNot()
    {
        // A custom operator awaits the framework's call of a client that ignores cancellation. The call itself stops
        // waiting when the caller cancels, so the request is aborted at once, with the call recorded as cancelled.
        var stuck = new StuckClient();
        var engine = WithStuckScorer(stuck, awaitCall: true);
        ReplayBundle? delivered = null;
        using var cancel = new CancellationTokenSource();

        var running = engine.ExecuteAsync(WithCallback(ReplayTests.Request("req-stuck", 1001, "home", arms: null),
            bundle => delivered = bundle), cancel.Token).AsTask();
        await stuck.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.False(stuck.Answered.Task.IsCompleted);
        var call = Assert.Single(delivered!.Runs[0].ModelCalls);
        Assert.Equal(("cancelled", true), (call.Status, call.Threw));
        Assert.Equal(30, call.Keys!.Value.GetArrayLength()); // the coarse top 30, known when the call began
        Assert.Equal(30, call.InputDigests.Count);

        // The client answers after the abort; its answer is recorded nowhere.
        stuck.Release.SetResult();
        await stuck.Answered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("cancelled", delivered.Runs[0].ModelCalls[0].Status);
        Assert.Empty(delivered.Runs[0].ModelCalls[0].Outputs);

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(delivered)));

        Assert.True(outcome.Aborted);
        Assert.True(outcome.Report.Diff.Identical);
        Assert.DoesNotContain(outcome.Report.Nodes, n => n.Changed);
        Assert.Equal("aborted", outcome.Report.Nodes.Single(n => n.Node == "ctr_ranker").Replayed);
    }

    [Fact]
    public async Task ACallStillInFlightWhenTheRunIsRecorded_IsOutstanding_AndWhatItReturnsLaterIsNotRecorded()
    {
        // An operator that does not wait for its call (it must; nothing forces it to) fails for want of its column, and the
        // run is recorded while the call is still in flight: the recording is a snapshot and never waits for a call.
        var stuck = new StuckClient();
        var engine = WithStuckScorer(stuck, awaitCall: false);

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-in-flight", 1001, "home", arms: null))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("node 'ctr_ranker' failed: left non-nullable column 'score.ctr' without a value on 30 of 30 live rows", recorded.Error);
        var call = Assert.Single(recorded.Recording!.Runs[0].ModelCalls);
        Assert.Equal(("outstanding", false), (call.Status, call.Threw));
        Assert.Equal(30, call.InputDigests.Count);

        // The call completes after the snapshot; its answer is recorded nowhere.
        stuck.Release.SetResult();
        Assert.True((await stuck.Invocation!.WaitAsync(TimeSpan.FromSeconds(60))).Succeeded);
        Assert.Equal("outstanding", recorded.Recording.Runs[0].ModelCalls[0].Status);
        Assert.Empty(recorded.Recording.Runs[0].ModelCalls[0].Outputs);
    }

    [Fact]
    public async Task ARunAbortedBetweenPositions_ReplaysThePositionsItFinished_AndStopsAtTheNextOne()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-between", 1001, "home", ReplayTests.ControlArms));
        Assert.True(recorded.Succeeded, recorded.Error);

        // The same request as if it had been aborted right after its third position (e.g. while publishing that node).
        var bundle = JsonNode.Parse(ReplayBundleFile.Serialize(recorded.Recording!))!;
        var nodes = bundle["runs"]![0]!["nodes"]!.AsArray();
        var finished = nodes.Take(3).Select(n => n!["node"]!.GetValue<string>()).ToArray();
        while (nodes.Count > 3)
        {
            nodes.RemoveAt(3);
        }

        foreach (var result in new[] { bundle["runs"]![0]!["result"]!, bundle["result"]! })
        {
            result["succeeded"] = false;
            result["aborted"] = true;
            result["error"] = "cancelled by the caller";
            result["items"] = new JsonArray();
        }

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(bundle.ToJsonString()));

        Assert.True(outcome.Aborted);
        Assert.True(outcome.Report.Diff.Identical);
        Assert.Equal(finished, outcome.Report.Nodes.Select(n => n.Node)); // nothing past the abort ran, not even as aborted
        Assert.DoesNotContain(outcome.Report.Nodes, n => n.Changed);
    }

    [Fact]
    public async Task TheCallbackGetsTheBundleOfAReturnedRequestToo_AndOneThatThrowsChangesNothing()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        ReplayBundle? delivered = null;

        var result = await engine.ExecuteAsync(WithCallback(ReplayTests.Request("req-returned", 1001, "home", ReplayTests.ControlArms), bundle =>
        {
            delivered = bundle;
            throw new InvalidOperationException("the host's sink is down");
        }));

        Assert.True(result.Succeeded, result.Error);
        Assert.Same(result.Recording, delivered);
        Assert.False(delivered!.Result.Aborted);
    }

    [Fact]
    public void TheSchema_DescribesAbortedResultsAndOutstandingCalls()
    {
        var output = new StringWriter();
        Assert.Equal(0, StrataRec.Cli.Commands.SchemaCommand.Run("replay_bundle", null, output, TextWriter.Null));

        using var schema = JsonDocument.Parse(output.ToString());
        var result = schema.RootElement.GetProperty("properties").GetProperty("result").GetProperty("properties");
        Assert.Contains("snapshot", result.GetProperty("aborted").GetProperty("description").GetString());
        Assert.Contains("outstanding", output.ToString());
        var run = schema.RootElement.GetProperty("properties").GetProperty("runs").GetProperty("items").GetProperty("properties");
        Assert.Contains("aborted position", run.GetProperty("nodes").GetProperty("items").GetProperty("properties").GetProperty("triggered")
            .GetProperty("description").GetString());
        var errorType = run.GetProperty("model_calls").GetProperty("items").GetProperty("properties").GetProperty("error_type");
        Assert.Contains("never loaded", errorType.GetProperty("description").GetString());
        Assert.Contains("\"assembly\"", errorType.GetRawText());
    }

    private static RecRequest WithCallback(RecRequest request, Action<ReplayBundle> onRecording) => new()
    {
        PipelineId = request.PipelineId,
        RequestId = request.RequestId,
        Inputs = request.Inputs,
        Deadline = request.Deadline,
        TimeMs = request.TimeMs,
        Debug = new DebugOptions { ForceArms = request.Debug?.ForceArms, OnRecording = onRecording },
    };

    /// <summary>The baseline pipeline without experiments or taps, with ctr_ranker replaced by an operator that calls a stuck client.</summary>
    /// <param name="awaitCall">Whether the operator awaits the framework's call, as it should, or returns without it.</param>
    private static RecEngine WithStuckScorer(StuckClient client, bool awaitCall)
    {
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.LogTaps.Clear();
        pipeline.Nodes["ctr_ranker"] = new NodeSpec
        {
            Order = 400, Op = "StuckScorer@1", OnFailure = "fail_request",
            Config = new JsonObject { ["output_col"] = "score.ctr", ["expr"] = "col.item.smoothed_ctr" },
        };
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Operators = OperatorRegistry.CreateDefault().Add(new StuckScorerFactory(client, awaitCall)),
        });
        var load = engine.Load(new PlanCompileRequest
        {
            Pipeline = pipeline, Catalog = host.Request.Catalog, Bindings = host.Request.Bindings, Tables = host.Request.Tables, Policy = host.Request.Policy,
        });
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    /// <summary>Linear model clients that block until cancelled, honouring the cancellation.</summary>
    private sealed class BlockingModels : IModelClientFactory
    {
        private readonly LinearModelClientFactory _inner = new();
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public string ClientType => _inner.ClientType;

        public IModelClient Create(BindingContext context) => new Client(_inner.Create(context), this);

        private sealed class Client(IModelClient inner, BlockingModels owner) : IModelClient
        {
            public string ClientType => inner.ClientType;

            public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
                inner.GetSignatureAsync(model, version, cancellationToken);

            public async ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._calls);
                owner.Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return ModelStatus.Ok;
            }
        }
    }

    /// <summary>A model client that ignores cancellation and answers only when released.</summary>
    private sealed class StuckClient : IModelClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set once the client has answered, after the test released it.</summary>
        public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The framework's call of the client, as the operator started it.</summary>
        public Task<ModelStatus>? Invocation { get; set; }

        public string ClientType => "stuck";

        public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ModelSignature(Array.Empty<TensorSpec>(), Array.Empty<TensorSpec>()));

        public async ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            outputs.GetFloat32("score", call.BatchSize).Span.Fill(0.5f);
            Answered.TrySetResult();
            return ModelStatus.Ok;
        }
    }

    /// <summary>Calls the stuck client through the framework, which bounds the wait for it.</summary>
    private sealed class StuckScorerFactory(StuckClient client, bool awaitCall) : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } =
            new("StuckScorer", 1, Effects.ColumnWriter, typeof(ExpressionScorerConfig), "Scores with a model that never answers in time.");

        // Compiled as an expression scorer for its output declaration; the operator never writes it.
        public Operator? Compile(OperatorCompileContext context) =>
            new ExpressionScorerFactory().Compile(context) is null ? null : new StuckScorer(client, awaitCall);

        private sealed class StuckScorer(StuckClient client, bool awaitCall) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var rows = context.LiveRows.ToArray();
                var call = new ModelCall { Model = "stuck", Inputs = Array.Empty<NamedInput>(), BatchSize = rows.Length };
                client.Invocation = context.InvokeModelAsync(client, new IoTarget(IoKind.Model, "stuck"), call, new ArrayWriter(), rows, timeoutMs: null);
                if (awaitCall)
                {
                    await client.Invocation;
                }
            }
        }

        private sealed class ArrayWriter : IModelOutputWriter
        {
            public Memory<float> GetFloat32(string name, int elementCount) => new float[elementCount];

            public void SetText(string name, string text)
            {
            }
        }
    }
}
