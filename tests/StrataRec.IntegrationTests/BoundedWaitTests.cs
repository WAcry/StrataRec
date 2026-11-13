using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;
using StrataRec.Pipeline.Sources;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The waits of the standard <c>ModelScorer@1</c> and <c>MultiSourceRecall@1</c> for their clients are bounded on their
/// own, by the call's timeout, the node's budget and the caller (design 5.6), whatever the client does with its token:
/// the request falls back or fails in time, the abandoned client keeps the request memory until it answers and nothing
/// it writes is read, the recording has the call as the request saw it, and a replay takes the same path at once. An
/// operator that returns only after its budget timed out all the same.
/// </summary>
/// <remarks>
/// The clients here ignore cancellation and answer on a dedicated thread once the test opens their gate, so whether a
/// request waited for one is not a question of timing: it returned while the gate was closed, or the test fails after
/// <see cref="Patience"/>. The tests of ctr_ranker's own 50 ms budget load the sample as written
/// (<see cref="SampleTiming.AsWritten"/>); the others give the untimed sample the budget they are about. They run alone
/// because the abandoned-I/O count is a process-wide metric.
/// </remarks>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class BoundedWaitTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AModelIgnoringCancellation_IsCutOffAtTheNodeBudget_AndTheFallbackServes_WhileTheModelHoldsTheMemory()
    {
        using var capture = new MetricCapture();
        var host = HomeFeedTestHost.Load(SampleTiming.AsWritten); // ctr_ranker: timeout_ms 50, fallback ctr_backup
        using var models = new GatedModels();
        var pool = new CountingPool();
        var engine = Engine(host, pool, models: models);

        var result = await engine.ExecuteAsync(Request("req-node-timeout")).AsTask().WaitAsync(Patience);

        // Returned with the gate closed: the request did not wait for the model.
        Assert.True(result.Succeeded, result.Error);
        Assert.False(models.Answered.Task.IsCompleted);
        var ranker = result.Trace!.Single(n => n.NodeId == "ctr_ranker");
        Assert.Equal(("ctr_backup", "timed out after 50 ms; served by fallback 'ctr_backup'"), (ranker.ProducedBy, ranker.Detail));
        Assert.Equal(0, pool.Returned); // the abandoned call still holds the request memory...
        Assert.Equal(1, Assert.Single(capture.Of(StrataRecMetrics.Names.AbandonedIo)).Value); // ...and is counted once
        Assert.Equal("timeout", Tag(Assert.Single(capture.Of(StrataRecMetrics.Names.ModelErrors)), StrataRecTags.Outcome));
        var run = result.Recording!.Runs[0];
        var call = Assert.Single(run.ModelCalls);
        Assert.Equal(("timeout", true), (call.Status, call.Threw)); // as the request saw it
        Assert.Empty(call.Outputs);
        Assert.Equal(ReplayTiming.TimedOut, Assert.Single(run.Nodes.Single(n => n.Node == "ctr_ranker").Attempts, a => a.Node == "ctr_ranker").Timing);

        // The model answers late, writing its outputs into buffers still rented, and only then lets go of the memory.
        models.Open();
        Assert.True((await models.Answered.Task.WaitAsync(Patience)).Succeeded);
        Assert.True(SpinWait.SpinUntil(() => pool.Returned > 0, Patience));
        Assert.Empty(run.ModelCalls[0].Outputs);

        await AssertReplaysAtOnce(engine, result.Recording, "ctr_ranker", "fallback", ranker.Detail);
        Assert.Equal(1, models.Calls); // the replay called nothing
    }

    [Fact]
    public async Task TheRequestDeadline_BoundsTheWaitForAModelIgnoringCancellation()
    {
        // The untimed node timeout is longer than the request: the node's budget is what is left of it (design 5.6).
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"on_failure\": \"fallback:ctr_backup\"", "\"on_failure\": \"fail_request\""));
        using var models = new GatedModels();
        var pool = new CountingPool();
        var engine = Engine(host, pool, models: models);

        var result = await engine.ExecuteAsync(Request("req-deadline", deadlineMs: 1_000)).AsTask().WaitAsync(Patience);

        Assert.False(result.Succeeded);
        Assert.StartsWith("node 'ctr_ranker' failed: timed out after ", result.Error);
        Assert.Equal(1, models.Calls);
        Assert.False(models.Answered.Task.IsCompleted);
        Assert.Equal(0, pool.Returned);

        models.Open();
        Assert.True((await models.Answered.Task.WaitAsync(Patience)).Succeeded);
        Assert.True(SpinWait.SpinUntil(() => pool.Returned > 0, Patience));
    }

    [Fact]
    public async Task CallerCancellation_EndsTheWaitForAModelIgnoringIt_WhichHoldsTheMemoryUntilItAnswers()
    {
        using var capture = new MetricCapture();
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"on_failure\": \"fallback:ctr_backup\"", "\"on_failure\": \"fail_request\""));
        using var models = new GatedModels();
        var pool = new CountingPool();
        var engine = Engine(host, pool, models: models);
        ReplayBundle? delivered = null;
        using var cancel = new CancellationTokenSource();

        var running = engine.ExecuteAsync(Request("req-caller", onRecording: bundle => delivered = bundle), cancel.Token).AsTask();
        await models.Started.Task.WaitAsync(Patience);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Patience));
        Assert.False(models.Answered.Task.IsCompleted);
        Assert.Equal("cancelled", Assert.Single(delivered!.Runs[0].ModelCalls).Status);
        Assert.Empty(capture.Of(StrataRecMetrics.Names.ModelErrors)); // the caller giving up is not the model's fault
        Assert.Empty(capture.Of(StrataRecMetrics.Names.ModelDuration));
        Assert.Equal(1, Assert.Single(capture.Of(StrataRecMetrics.Names.AbandonedIo)).Value);
        Assert.Equal(0, pool.Returned);

        models.Open();
        Assert.True((await models.Answered.Task.WaitAsync(Patience)).Succeeded);
        Assert.True(SpinWait.SpinUntil(() => pool.Returned > 0, Patience));
        Assert.Equal("cancelled", delivered.Runs[0].ModelCalls[0].Status);
    }

    [Fact]
    public async Task ASourceIgnoringCancellation_IsCutOffAtItsOwnTimeout_AndNothingItAddedIsRead()
    {
        // What recall appends when popular answers, for comparison: popular adds rows two_tower does not have.
        var baseline = await Engine(HomeFeedTestHost.Load(), new CountingPool()).ExecuteAsync(Request("req-source-baseline"));
        var twoTower = baseline.Recording!.Runs[0].CandidateCalls.Single(c => c.Source == "two_tower").Ids!.Value.EnumerateArray()
            .Select(id => id.GetInt64()).Distinct().Count();
        Assert.True(baseline.Trace!.Single(n => n.NodeId == "recall").RowsOut > twoTower);

        using var capture = new MetricCapture();
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "bindings/local.jsonc",
            "\"data_file\": \"fixtures/recall_popular.json\"", "\"data_file\": \"fixtures/recall_popular.json\", \"timeout_ms\": 50"));
        using var sources = new GatedSources("popular");
        var pool = new CountingPool();
        var engine = Engine(host, pool, sources: sources);

        var result = await engine.ExecuteAsync(Request("req-source-timeout")).AsTask().WaitAsync(Patience);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(sources.Answered.Task.IsCompleted);
        var recall = result.Trace!.Single(n => n.NodeId == "recall");
        Assert.Equal((NodeOutcomeKind.Degraded, "recall sources failed: popular: timed out after 50 ms"), (recall.Kind, recall.Detail));

        // popular had added every candidate before it hung; its abandoned buffer was not read.
        Assert.Equal(twoTower, recall.RowsOut);
        Assert.DoesNotContain(result.Items, item => item.Columns["primary_source"].AsString() == "popular");
        var popular = result.Recording!.Runs[0].CandidateCalls.Single(c => c.Source == "popular");
        Assert.Equal(("timeout", null), (popular.Status, popular.Ids));
        Assert.Equal(0, pool.Returned);
        Assert.Equal(1, Assert.Single(capture.Of(StrataRecMetrics.Names.AbandonedIo)).Value);
        var error = Assert.Single(capture.Of(StrataRecMetrics.Names.SourceErrors));
        Assert.Equal(("popular", "timeout"), (Tag(error, StrataRecTags.Source), Tag(error, StrataRecTags.Outcome)));

        // It answers late, adding its candidates again into the closed buffer, and then lets go of the memory.
        sources.Open();
        Assert.True((await sources.Answered.Task.WaitAsync(Patience)).Succeeded);
        Assert.True(SpinWait.SpinUntil(() => pool.Returned > 0, Patience));

        await AssertReplaysAtOnce(engine, result.Recording, "recall", "degraded", recall.Detail);
    }

    [Fact]
    public async Task AModelAnsweringOnlyAfterTheNodeBudget_IsATimeout_AndItsScoresAreNotServed()
    {
        // The client answers synchronously, but only once the node's budget has cancelled its token. The answer came after
        // the call's bound (the node's deadline): it is not heard, so the call is a timeout as the request saw it.
        var engine = Engine(HomeFeedTestHost.Load(SampleTiming.AsWritten), new CountingPool(), models: new LateModels());

        var result = await engine.ExecuteAsync(Request("req-late")).AsTask().WaitAsync(Patience);

        Assert.True(result.Succeeded, result.Error);
        var ranker = result.Trace!.Single(n => n.NodeId == "ctr_ranker");
        Assert.Equal(("ctr_backup", "timed out after 50 ms; served by fallback 'ctr_backup'"), (ranker.ProducedBy, ranker.Detail));
        var run = result.Recording!.Runs[0];
        var call = Assert.Single(run.ModelCalls);
        Assert.Equal(("timeout", true), (call.Status, call.Threw));
        Assert.Empty(call.Outputs);
        Assert.Equal(ReplayTiming.TimedOut, run.Nodes.Single(n => n.Node == "ctr_ranker").Attempts[0].Timing);

        // Served exactly as when the model fails outright.
        var failed = await Engine(HomeFeedTestHost.LoadModified(MetricsTests.FailModel), new CountingPool()).ExecuteAsync(Request("req-late"));
        LiveRunAssert.SameNodeOutcomes(failed, result, "ctr_ranker");
        Assert.Equal(Items(failed), Items(result));

        await AssertReplaysAtOnce(engine, result.Recording, "ctr_ranker", "fallback", ranker.Detail);
    }

    /// <summary>Replays a bundle on the same engine: it completes synchronously, with the same result and node decision.</summary>
    private static async Task AssertReplaysAtOnce(RecEngine engine, ReplayBundle recording, string node, string outcome, string? detail)
    {
        var pending = engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recording)));

        Assert.True(pending.IsCompletedSuccessfully, "the replay waited for a call or a timer");
        var replayed = await pending;
        Assert.True(replayed.Report.Diff.Identical, ReplayTests.Describe(replayed.Report));
        Assert.All(replayed.Report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
        var position = replayed.Report.Nodes.Single(n => n.Node == node);
        Assert.Equal((outcome, detail), (position.Replayed, position.ReplayedDetail));
    }

    private static RecRequest Request(string requestId, int deadlineMs = 600_000, Action<ReplayBundle>? onRecording = null) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Deadline = TimeSpan.FromMilliseconds(deadlineMs),
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Record = true, Trace = true, ForceArms = ReplayTests.ControlArms, OnRecording = onRecording },
    };

    private static RecEngine Engine(HomeFeedTestHost host, IBufferPool pool, IModelClientFactory? models = null, ICandidateSourceFactory? sources = null)
    {
        var engine = new RecEngine(
            new PlanEnvironment
            {
                BaseDirectory = host.Root,
                Models = models is null ? ModelClientRegistry.CreateDefault() : new ModelClientRegistry().Add(models),
                CandidateSources = sources is null ? CandidateSourceRegistry.CreateDefault() : new CandidateSourceRegistry().Add(sources),
            },
            executor: new PipelineExecutor(pool));
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static List<(string Id, double Score)> Items(RecResult result) =>
        result.Items.Select(i => (i.Id.ToString(), i.Columns["score"].AsFloat64())).ToList();

    private static string? Tag(Measured m, string key) => m.Tags.TryGetValue(key, out var value) ? value as string : null;

    /// <summary>Counts buffers given back to it: none until the request memory's last holder lets go.</summary>
    private sealed class CountingPool : IBufferPool
    {
        private int _returned;

        public int Returned => Volatile.Read(ref _returned);

        public T[] Rent<T>(int minimumLength) => new T[Math.Max(1, minimumLength)];

        public void Return<T>(T[] array, bool clearArray) => Interlocked.Increment(ref _returned);
    }

    /// <summary>Runs a client's work on a dedicated thread once the gate opens, ignoring cancellation.</summary>
    private abstract class Gated : IDisposable
    {
        private readonly ManualResetEventSlim _gate = new();
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The status the gated call returned once released, or what its late work threw.</summary>
        public TaskCompletionSource<(bool Succeeded, string? Error)> Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public void Open() => _gate.Set();

        public void Dispose() => _gate.Set(); // never leave a client thread blocked

        protected ValueTask<TStatus> Later<TStatus>(Func<TStatus> work, Func<TStatus, (bool, string?)> describe)
        {
            Interlocked.Increment(ref _calls);
            var done = new TaskCompletionSource<TStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                Started.TrySetResult();
                try
                {
                    if (!_gate.Wait(Patience * 2))
                    {
                        throw new TimeoutException("the test never opened the gate");
                    }

                    var status = work(); // late: writes into what the framework handed the call
                    done.SetResult(status);
                    Answered.TrySetResult(describe(status));
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                    Answered.TrySetResult((false, ex.Message));
                }
            })
            { IsBackground = true, Name = "gated client" };
            thread.Start();
            return new ValueTask<TStatus>(done.Task);
        }
    }

    /// <summary>linear@1 models that answer only once the gate opens, writing their outputs then.</summary>
    private sealed class GatedModels : Gated, IModelClientFactory
    {
        private readonly LinearModelClientFactory _inner = new();

        public string ClientType => _inner.ClientType;

        public IModelClient Create(BindingContext context) => new Client(_inner.Create(context), this);

        private sealed class Client(IModelClient inner, GatedModels owner) : IModelClient
        {
            public string ClientType => inner.ClientType;

            public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
                inner.GetSignatureAsync(model, version, cancellationToken);

            public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
                owner.Later(() => inner.InvokeAsync(call, outputs, CancellationToken.None).AsTask().GetAwaiter().GetResult(), s => (s.Succeeded, s.Error));
        }
    }

    /// <summary>
    /// memory_list@1 sources where <paramref name="gated"/> adds all its candidates at once and then answers only once the
    /// gate opens, adding them again then.
    /// </summary>
    private sealed class GatedSources(string gated) : Gated, ICandidateSourceFactory
    {
        private readonly MemoryListCandidateSourceFactory _inner = new();

        public string ClientType => _inner.ClientType;

        public ICandidateSource Create(BindingContext context)
        {
            var source = _inner.Create(context);
            return context.Name == gated ? new Source(source, this) : source;
        }

        private sealed class Source(ICandidateSource inner, GatedSources owner) : ICandidateSource
        {
            public string ClientType => inner.ClientType;

            public ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken)
            {
                _ = inner.RetrieveAsync(query, sink, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                return owner.Later(() => inner.RetrieveAsync(query, sink, CancellationToken.None).AsTask().GetAwaiter().GetResult(), s => (s.Succeeded, s.Error));
            }
        }
    }

    /// <summary>linear@1 models that answer synchronously, but only once their token has been cancelled.</summary>
    private sealed class LateModels : IModelClientFactory
    {
        private readonly LinearModelClientFactory _inner = new();

        public string ClientType => _inner.ClientType;

        public IModelClient Create(BindingContext context) => new Client(_inner.Create(context));

        private sealed class Client(IModelClient inner) : IModelClient
        {
            public string ClientType => inner.ClientType;

            public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
                inner.GetSignatureAsync(model, version, cancellationToken);

            public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
            {
                if (!cancellationToken.WaitHandle.WaitOne(Patience))
                {
                    throw new TimeoutException("the node's budget never ran out");
                }

                return inner.InvokeAsync(call, outputs, CancellationToken.None);
            }
        }
    }
}
