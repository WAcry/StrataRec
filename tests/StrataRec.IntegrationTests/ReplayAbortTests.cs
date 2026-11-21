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
/// Requests aborted while recorded (design 13.4): the bundle reaches the host through the recording callback while the
/// cancellation still reaches the caller, calls in flight are outstanding, and a replay reports the recording without
/// executing it: an aborted request has no result to reproduce.
/// </summary>
/// <remarks>
/// Nothing here depends on timing: the caller cancels once the model client says it has started, and the untimed sample's
/// budgets and deadlines (<see cref="HomeFeedTestHost.UntimedMs"/>) are far longer than any run.
/// </remarks>
public sealed class ReplayAbortTests
{
    [Fact]
    public async Task ARequestCancelledDuringAModelCall_DeliversItsBundle_WhichIsNotReplayed()
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"on_failure\": \"fallback\", \"fallback\": \"ctr_backup\"", "\"on_failure\": \"fail_request\""));
        var models = new BlockingModels();
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Bindings = PlanEnvironment.DefaultBindings().Add(models) });
        Assert.True(engine.Load(host.Request).Applied);
        ReplayBundle? delivered = null;
        using var cancel = new CancellationTokenSource();

        var running = engine.ExecuteAsync(WithCallback(ReplayTests.Request("req-cancelled", 1001, "home", ReplayTests.ControlArms),
            bundle => delivered = bundle), cancel.Token).AsTask();
        await models.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); // recording does not swallow the cancellation
        Assert.NotNull(delivered);
        Assert.Equal((false, true, "cancelled by the caller"), (delivered.Result.Succeeded, delivered.Result.Aborted, delivered.Result.Error));
        var run = Assert.Single(delivered.Runs);
        Assert.True(run.Result.Aborted);
        Assert.Equal(("ctr_ranker", "aborted"), (run.Nodes[^1].Node, run.Nodes[^1].Outcome));
        Assert.Contains(run.ServiceCalls, c => c.Role == "model");
        Assert.All(run.ServiceCalls.FindAll(c => c.Role == "model"), call => Assert.Equal("cancelled", call.Status)); // the client honoured the cancellation

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(delivered)), cancellationToken: TestContext.Current.CancellationToken);

        // The recorded reason, the node that was running and every recorded decision, for inspection.
        Assert.Equal(new RecordedAbort("cancelled by the caller", "ctr_ranker"), outcome.NotReplayed);
        Assert.Equal(run.Nodes.Select(n => (n.Node, (string?)n.Outcome, n.Detail)), outcome.Report.Nodes.Select(n => (n.Node, n.Recorded, n.RecordedDetail)));

        // Nothing executed: no node ran, no feature value was asked of the recording, no client was called.
        Assert.All(outcome.Report.Nodes, n => Assert.Null(n.Replayed));
        Assert.Empty(outcome.Report.FieldStats);
        Assert.Empty(outcome.Report.Params);
        Assert.Null(outcome.Result.Trace);
        Assert.Equal(run.ServiceCalls.FindAll(c => c.Role == "model").Count, models.Calls);

        // A placeholder, not a result: it reproduces nothing.
        Assert.Equal((false, 0), (outcome.Result.Succeeded, outcome.Result.Items.Count));
        Assert.Contains("not replayed", outcome.Result.Error);
        Assert.False(outcome.Report.Diff.Identical);
    }

    /// <summary>
    /// The position running at the abort had already triggered an experiment: through a parameter its node read (the arm
    /// changes the model version) or through its site (the arm patches only the node's timeout). The exposure happened, so
    /// the recording's experiment record keeps it (design 8.8, 12.6), and a replay does not execute the bundle.
    /// </summary>
    [Theory]
    [InlineData("param")]
    [InlineData("site")]
    public async Task AnAbortedPosition_KeepsTheExposureItCaused_InTheRecording(string trigger)
    {
        var (engine, bundle) = await RecordAbortedAtTheRanker(trigger);
        var ranker = bundle.Runs[0].Nodes[^1];
        Assert.Equal(("ctr_ranker", "aborted"), (ranker.Node, ranker.Outcome));
        Assert.Equal(new[] { "exp_quality_weight" }, bundle.Runs[0].Result.TriggeredExperiments);
        Assert.Equal(new[] { "exp_quality_weight" }, bundle.Result.TriggeredExperiments);

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(bundle)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new RecordedAbort("cancelled by the caller", "ctr_ranker"), outcome.NotReplayed);
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
            ReplayTests.Edit(root, pipeline, "\"on_failure\": \"fallback\", \"fallback\": \"ctr_backup\"", "\"on_failure\": \"fail_request\"");
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
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Bindings = PlanEnvironment.DefaultBindings().Add(models) });
        var load = engine.Load(host.Request);
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
        await stuck.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.False(stuck.Answered.Task.IsCompleted);
        var call = Assert.Single(delivered!.Runs[0].ServiceCalls, c => c.Role == "model");
        Assert.Equal(("cancelled", true), (call.Status, call.Threw));
        Assert.Equal(30, call.Keys!.Value.GetArrayLength()); // the coarse top 30, known when the call began
        Assert.Equal(30, call.InputDigests.Count);

        // The client answers after the abort; its answer is recorded nowhere.
        stuck.Release.SetResult();
        await stuck.Answered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", delivered.Runs[0].ServiceCalls.FindAll(c => c.Role == "model")[0].Status);
        Assert.Empty(delivered.Runs[0].ServiceCalls.FindAll(c => c.Role == "model")[0].Outputs);
    }

    [Fact]
    public async Task ACallStillInFlightWhenTheRunIsRecorded_IsOutstanding_AndWhatItReturnsLaterIsNotRecorded()
    {
        // An operator that does not wait for its call (it must; nothing forces it to) fails for want of its column, and the
        // run is recorded while the call is still in flight: the recording is a snapshot and never waits for a call.
        var stuck = new StuckClient();
        var engine = WithStuckScorer(stuck, awaitCall: false);

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-in-flight", 1001, "home", arms: null), TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal("node 'ctr_ranker' failed: left non-nullable column 'score.ctr' without a value on 30 of 30 live rows", recorded.Error);
        var call = Assert.Single(recorded.Recording!.Runs[0].ServiceCalls, c => c.Role == "model");
        Assert.Equal(("outstanding", false), (call.Status, call.Threw));
        Assert.Equal(30, call.InputDigests.Count);

        // The call completes after the snapshot; its answer is recorded nowhere.
        stuck.Release.SetResult();
        Assert.True((await stuck.Invocation!.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal("outstanding", recorded.Recording.Runs[0].ServiceCalls.FindAll(c => c.Role == "model")[0].Status);
        Assert.Empty(recorded.Recording.Runs[0].ServiceCalls.FindAll(c => c.Role == "model")[0].Outputs);
    }

    [Fact]
    public async Task ARecordingAbortedBetweenPositions_IsNotReplayed_AndNamesNoRunningNode()
    {
        var (engine, bundle, finished) = await RecordAbortedAfterThreePositions();

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(bundle.ToJsonString()), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new RecordedAbort("cancelled by the caller", null), outcome.NotReplayed);
        Assert.Equal(finished, outcome.Report.Nodes.Select(n => n.Node));
        Assert.All(outcome.Report.Nodes, n => Assert.Null(n.Replayed));
    }

    [Fact]
    public async Task ARecordingAbortedInItsFallbackPipeline_NamesTheRunningNodeWithThatPipeline()
    {
        // As if the primary pipeline had failed and the request had been aborted while its fallback pipeline ran ctr_ranker.
        var (engine, bundle, _) = await RecordAbortedAfterThreePositions();
        var fallback = bundle["runs"]![0]!.DeepClone();
        fallback["role"] = "fallback_pipeline";
        fallback["pipeline_id"] = "home_feed_backup";
        fallback["nodes"]!.AsArray().Add(new JsonObject { ["node"] = "ctr_ranker", ["outcome"] = "aborted", ["detail"] = "cancelled by the caller" });
        bundle["runs"]![0]!["result"]!["aborted"] = false;
        bundle["runs"]!.AsArray().Add(fallback);

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(bundle.ToJsonString()), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new RecordedAbort("cancelled by the caller", "home_feed_backup:ctr_ranker"), outcome.NotReplayed);
        Assert.Equal("aborted", outcome.Report.Nodes[^1].Recorded);
    }

    /// <summary>
    /// A home_feed request recorded with its control arms, edited as if it had been aborted right after its third position
    /// (e.g. while publishing that node), and the ids of the three nodes it finished.
    /// </summary>
    private static async Task<(RecEngine Engine, JsonNode Bundle, string[] Finished)> RecordAbortedAfterThreePositions()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-between", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);
        Assert.True(recorded.Succeeded, recorded.Error);

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

        return (engine, bundle, finished);
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
        }), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Same(result.Recording, delivered);
        Assert.False(delivered!.Result.Aborted);
    }

    [Fact]
    public void TheSchema_DescribesAbortedResultsAndOutstandingCalls()
    {
        var output = new StringWriter();
        Assert.Equal(0, StrataRec.CommandLine.Commands.SchemaCommand.Run(Tool.Environment, "replay_bundle", null, output, TextWriter.Null));

        using var schema = JsonDocument.Parse(output.ToString());
        var result = schema.RootElement.GetProperty("properties").GetProperty("result").GetProperty("properties");
        Assert.Contains("snapshot", result.GetProperty("aborted").GetProperty("description").GetString());
        Assert.Contains("outstanding", output.ToString());
        Assert.Contains("does not execute", result.GetProperty("aborted").GetProperty("description").GetString());
        var run = schema.RootElement.GetProperty("properties").GetProperty("runs").GetProperty("items").GetProperty("properties");
        Assert.Contains("aborted (the position running", run.GetProperty("nodes").GetProperty("items").GetProperty("properties").GetProperty("outcome")
            .GetProperty("description").GetString());
        Assert.False(run.GetProperty("nodes").GetProperty("items").GetProperty("properties").TryGetProperty("triggered", out _)); // nothing replays an aborted node
        var threw = run.GetProperty("service_calls").GetProperty("items").GetProperty("properties").GetProperty("threw");
        Assert.Contains("cut the call off", threw.GetProperty("description").GetString());
    }

    /// <summary>
    /// Review 79: a request that captures its logs, cancelled while it runs, returns no batches and writes none, yet its
    /// recording said they were captured. They went nowhere; the sampling decisions it made are kept.
    /// </summary>
    [Fact]
    public async Task ACapturingRequestCancelled_RecordsThatItsBatchesWentNowhere()
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"on_failure\": \"fallback\", \"fallback\": \"ctr_backup\"", "\"on_failure\": \"fail_request\""));
        var models = new BlockingModels();
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Bindings = PlanEnvironment.DefaultBindings().Add(models) });
        Assert.True(engine.Load(host.Request).Applied);
        ReplayBundle? delivered = null;
        using var cancel = new CancellationTokenSource();

        var running = engine.ExecuteAsync(WithCallback(ReplayTests.Request("req-captured", 1001, "home", ReplayTests.ControlArms),
            bundle => delivered = bundle, captureLogs: true), cancel.Token).AsTask();
        await models.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var sampling = Assert.Single(delivered!.Runs).Sampling;
        Assert.Equal("none", sampling.Logs);
        Assert.Contains(sampling.Groups, g => g is { Group: "all", EffectiveRate: 1.0, Sampled: true });
        Assert.Contains(sampling.Taps, t => t is { Tap: "served", Active: true });
    }

    private static RecRequest WithCallback(RecRequest request, Action<ReplayBundle> onRecording, bool captureLogs = false) => new()
    {
        PipelineId = request.PipelineId,
        RequestId = request.RequestId,
        Inputs = request.Inputs,
        Deadline = request.Deadline,
        TimeMs = request.TimeMs,
        Debug = new DebugOptions { ForceArms = request.Debug?.ForceArms, OnRecording = onRecording, CaptureLogs = captureLogs },
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
    private sealed class BlockingModels : IBindingFactory<IServiceClient>
    {
        private readonly LinearModelClientFactory _inner = new();
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public string Type => _inner.Type;

        public Type SettingsType => _inner.SettingsType;

        public IServiceClient Create(BindingContext context) => new Client(_inner.Create(context), this);

        private sealed class Client(IServiceClient inner, BlockingModels owner) : IServiceClient
        {
            public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
                inner.DescribeAsync(model, version, cancellationToken);

            public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse outputs, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._calls);
                owner.Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return ServiceStatus.Ok;
            }
        }
    }

    /// <summary>A model client that ignores cancellation and answers only when released.</summary>
    private sealed class StuckClient : IServiceClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set once the client has answered, after the test released it.</summary>
        public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The framework's call of the client, as the operator started it.</summary>
        public Task<ServiceStatus>? Invocation { get; set; }

        public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ServiceSignature(Array.Empty<TensorSpec>(), Array.Empty<TensorSpec>()));

        public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse outputs, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            outputs.GetValues<float>("score", call.BatchSize).Span.Fill(0.5f);
            Answered.TrySetResult();
            return ServiceStatus.Ok;
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
                var call = new ServiceCall { Service = "stuck", Inputs = Array.Empty<NamedInput>(), BatchSize = rows.Length, Items = context.KeysOf(rows) };
                client.Invocation = context.CallAsync(new BoundService("stuck", client, IoRoles.Model, null), call, new ServiceResponse(context.Memory));
                if (awaitCall)
                {
                    await client.Invocation;
                }
            }
        }
    }
}
