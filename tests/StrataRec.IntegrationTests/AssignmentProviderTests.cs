using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Where a request's arms come from: forced arms, then the platform's answer the caller passed, then the
/// assignment provider, otherwise none. A pending provider and the deadline are covered by <see cref="Review4RegressionTests"/>;
/// the provider is waited for by the framework's one bounded wait, whose rules (who came first, the tokens' lifetime) are
/// locked in <c>BoundedCallTests</c>.
/// </summary>
public sealed class AssignmentProviderTests
{
    private static readonly string[] ProviderArms = { "exp_quality_weight/strong", "exp_mmr_diversity/mmr" };

    private sealed class RecordingProvider(IReadOnlyList<string> arms) : IAssignmentProvider
    {
        public List<AssignmentContext> Calls { get; } = new();

        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            Calls.Add(context);
            return ValueTask.FromResult(arms);
        }
    }

    [Fact]
    public async Task TheProviderIsToldTheRequestAndTheServedPlansExperimentsReadOnly()
    {
        var host = HomeFeedTestHost.Load();
        var provider = new RecordingProvider(ProviderArms);
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);
        engine.TryGetPipeline("home_feed", out var served);

        var result = await engine.ExecuteAsync(Request("req-ctx"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProviderArms.Order(), result.Experiments.Assigned.Order());
        var context = Assert.Single(provider.Calls);
        Assert.Equal("home_feed", context.PipelineId);
        Assert.Equal("req-ctx", context.RequestId);
        Assert.Equal(1001L, context.Inputs["user_id"].AsInt64());
        Assert.Equal(served.Plan.Layers.Select(l => l.Name), context.Layers.Select(l => l.Name));
        Assert.Equal(served.Plan.Experiments.Select(e => e.Id), context.Experiments.Select(e => e.Id));
        Assert.Equal(served.Plan.Arms.Select(a => a.Id), context.Arms.Select(a => a.Id));

        // Views of the plan's arrays, not the arrays: a provider cannot change what the plan serves.
        Assert.False(context.Layers is LayerInfo[]);
        Assert.True(((ICollection<LayerInfo>)context.Layers).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((IList<ArmInfo>)context.Arms)[0] = context.Arms[1]);

        // Built once per plan: every request of the plan passes the same views.
        await engine.ExecuteAsync(Request("req-ctx-2"), TestContext.Current.CancellationToken);
        Assert.Same(provider.Calls[0].Arms, provider.Calls[1].Arms);
    }

    [Fact]
    public async Task ForcedArmsWinOverTheCallersAssignmentWhichWinsOverTheProvider()
    {
        var host = HomeFeedTestHost.Load();
        var provider = new RecordingProvider(ProviderArms);
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);
        string[] platform = { "exp_freshness_calibration/calibrated" };
        string[] forced = { "exp_quality_weight/control" };

        var fromProvider = await engine.ExecuteAsync(Request("req-provider"), TestContext.Current.CancellationToken);
        var fromCaller = await engine.ExecuteAsync(Request("req-caller", assignment: platform), TestContext.Current.CancellationToken);
        var fromNothing = await engine.ExecuteAsync(Request("req-empty", assignment: Array.Empty<string>()), TestContext.Current.CancellationToken);
        var fromForce = await engine.ExecuteAsync(Request("req-forced", assignment: platform, force: forced), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderArms.Order(), fromProvider.Experiments.Assigned.Order());
        Assert.Equal("provider", fromProvider.Recording!.Request.Assignment.Source);
        Assert.Equal(platform, fromCaller.Experiments.Assigned);
        Assert.Equal("external", fromCaller.Recording!.Request.Assignment.Source);
        Assert.Empty(fromNothing.Experiments.Assigned); // the platform answered "no arms": not a reason to ask again
        Assert.Equal("external", fromNothing.Recording!.Request.Assignment.Source);
        Assert.Equal(forced, fromForce.Experiments.Assigned);
        Assert.Equal("forced", fromForce.Recording!.Request.Assignment.Source);
        Assert.Single(provider.Calls); // asked only when nothing else decided
    }

    [Fact]
    public async Task WithoutAnAnswerOrAProviderTheRequestRunsTheBaseline()
    {
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment);
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-none"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Experiments.Assigned);
        Assert.Empty(result.Experiments.Triggered);
        Assert.Equal("none", result.Recording!.Request.Assignment.Source);
    }

    /// <summary>Does its work before returning, then answers synchronously: the part of a provider the engine cannot bound.</summary>
    private sealed class BlockingProvider(Action<CancellationToken> work) : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            work(cancellationToken);
            return ValueTask.FromResult<IReadOnlyList<string>>(ProviderArms);
        }
    }

    [Fact]
    public async Task AProviderAnsweringSynchronouslyAfterTheCallerCancelled_CancelsTheRequest()
    {
        // An answer the engine observes complete is judged when it is observed, as BoundedCall judges a
        // client's: the caller's cancellation comes first, so the request ends there and no pipeline runs on the answer
        // (a run would deliver its aborted bundle to OnRecording).
        using var caller = new CancellationTokenSource();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new BlockingProvider(_ => caller.Cancel()));
        Assert.True(engine.Load(host.Request).Applied);
        var bundles = 0;
        var request = new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-cancelled",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Deadline = HomeFeedTestHost.Deadline,
            Debug = new DebugOptions { OnRecording = _ => Interlocked.Increment(ref bundles) },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ExecuteAsync(request, caller.Token));
        Assert.Equal(0, bundles);
    }

    [Fact]
    public async Task AProviderAnsweringSynchronouslyPastTheDeadline_IsRejectedAsPastIt()
    {
        // The provider blocks the request's thread until the deadline has cancelled its token, then answers.
        // The engine cannot cut that short, but it does not take the late answer.
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new BlockingProvider(token => Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))));
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-late",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Deadline = TimeSpan.FromMilliseconds(500), // room to reach the provider on a loaded machine: 50 ms ran out before it
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("deadline exceeded during assignment", result.Error);
        Assert.Empty(result.Experiments.Assigned); // rejected before the arms were decided
        Assert.Empty(result.Experiments.Ignored);
    }

    /// <summary>
    /// Ends only when its token is cancelled: the provider that token is for. If the engine let go of the token without
    /// cancelling it, this provider would never end, holding whatever it holds.
    /// </summary>
    private sealed class TokenOnlyProvider : IAssignmentProvider
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ended => _ended.Task;

        public async ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return ProviderArms;
            }
            finally
            {
                _ended.TrySetResult();
            }
        }
    }

    /// <summary>Waits for a provider the engine abandoned to end, with a watchdog: one whose token is never cancelled never does.</summary>
    private static async Task AssertEnds(TokenOnlyProvider provider)
    {
        var ended = await Task.WhenAny(provider.Ended, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(ended, provider.Ended), "the abandoned provider's token was never cancelled");
    }

    [Fact]
    public async Task AProviderAbandonedAtTheDeadline_SeesItsTokenCancelled()
    {
        // The wait and the timer that cancels the provider's token both aim at the deadline, and
        // the wait can end first. Disposing the source then stopped that timer: the token was never cancelled. The token
        // lives until the provider ends (BoundedCall, rule 3; the order where the wait ends first is fixed in BoundedCallTests).
        var provider = new TokenOnlyProvider();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-wait-abandons", deadlineMs: 200), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("deadline exceeded during assignment", result.Error);
        await AssertEnds(provider);
    }

    [Fact]
    public async Task AProviderAbandonedForTheCallersCancellation_SeesItsTokenCancelled()
    {
        // When the caller cancels, the wait's own registration on the caller's token can end the
        // request (inline, in Cancel) before the registration that cancels the provider's linked token runs; disposing the
        // source then removed that registration, and the provider's token was never cancelled.
        using var caller = new CancellationTokenSource();
        var provider = new TokenOnlyProvider();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);

        var request = engine.ExecuteAsync(Request("req-caller-abandons"), caller.Token).AsTask();
        Assert.False(request.IsCompleted); // waiting for the provider

        // From a thread without a synchronization context, as in a server: xUnit's would keep the request's continuation
        // out of Cancel, hiding the order above.
        await Task.Run(caller.Cancel, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await AssertEnds(provider);
    }

    /// <summary>
    /// Registers a cancellation callback that throws, and ends when told to (with a failure): a provider whose cleanup
    /// fails, and which outlives the request. Keeps its token, to tell when its source is disposed.
    /// </summary>
    private sealed class FailingCleanupProvider : IAssignmentProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        public void End() => _end.SetException(new InvalidOperationException("the provider failed late"));

        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            _ = cancellationToken.WaitHandle; // the handle a disposed source closes
            cancellationToken.Register(static () => throw new InvalidOperationException("the provider's cleanup failed"));
            return new(_end.Task);
        }
    }

    [Fact]
    public async Task AnAbandonedProviderWhoseCallbackThrows_IsSummarized_AndKeepsItsTokenUntilItEnds_ThenItIsDisposed()
    {
        // The callback's failure is counted and summarized as every token's is (the request events, subject
        // 'assignment'), and the provider's token stays valid while it runs, then is disposed once it has ended. A logger
        // that threw on the failure used to end the cleanup there: the token source was never disposed.
        var provider = new FailingCleanupProvider();
        var loggers = new ListLoggerFactory();
        using var events = new RequestEventLog(loggers.CreateLogger("events"), autoFlush: false);
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new PipelineExecutor(null, null, events), provider, loggers);
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-cleanup-fails", deadlineMs: 200), TestContext.Current.CancellationToken);

        Assert.Equal("deadline exceeded during assignment", result.Error);
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                events.Flush();
                return loggers.Entries.Any(e => e.Message.Contains("CancellationCallbacksFailed for 'assignment'", StringComparison.Ordinal)
                    && e.Message.Contains("the provider's cleanup failed", StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(30)), "the callback's failure was not summarized");
        Assert.True(provider.Token.IsCancellationRequested);
        Assert.False(DisposedTokens.Disposed(provider.Token)); // the provider is still running: its token stays valid

        provider.End();
        Assert.True(DisposedTokens.WaitUntilDisposed(provider.Token, TimeSpan.FromSeconds(30)), "the abandoned provider's token source was not disposed after it ended");
    }

    /// <summary>Answers when told to: a provider whose answer comes at a moment the test picks.</summary>
    private sealed class GatedProvider : IAssignmentProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Answer() => _answer.SetResult(ProviderArms);

        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken) => new(_answer.Task);
    }

    [Fact]
    public async Task APendingProviderAnsweringInTime_IsTaken()
    {
        var provider = new GatedProvider();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);

        var request = engine.ExecuteAsync(Request("req-provider-first"), TestContext.Current.CancellationToken).AsTask();
        Assert.False(request.IsCompleted); // waiting for the provider
        provider.Answer();
        var result = await request;

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProviderArms.Order(), result.Experiments.Assigned.Order());
    }

    /// <summary>
    /// Pending when the engine first asks, complete from then on: the provider completes between the engine's look at its
    /// ValueTask and the wait (while the ValueTask becomes a task).
    /// </summary>
    private sealed class CompletesOnSecondLook : IAssignmentProvider, IValueTaskSource<IReadOnlyList<string>>
    {
        private int _looks;

        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken) => new(this, 0);

        public ValueTaskSourceStatus GetStatus(short token) =>
            Interlocked.Increment(ref _looks) == 1 ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Succeeded;

        public IReadOnlyList<string> GetResult(short token) => ProviderArms;

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            throw new InvalidOperationException("the engine waited on a provider that had completed");
    }

    [Fact]
    public async Task AProviderCompletingBeforeItsWaitIsSetUp_IsObservedThenAndTaken()
    {
        // The fast path: the provider's task is complete once it is a task, so no wait is set up (OnCompleted would
        // throw); nothing decided who came first, and the answer is judged when it is observed, well before the deadline.
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new CompletesOnSecondLook());
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-fast-path"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProviderArms.Order(), result.Experiments.Assigned.Order());
    }

    /// <summary>
    /// The provider is an external call like a connector or a model (IO step 4): its duration and its timeouts are
    /// measured under <c>assignment provider</c>, and its health is tracked. It had no telemetry of its own.
    /// </summary>
    [Fact]
    public async Task TheProvidersCall_IsMeasuredAsAnExternalCall()
    {
        using var capture = new MetricCapture();
        var host = HomeFeedTestHost.Load();
        var answering = new RecEngine(host.Environment, new RecordingProvider(ProviderArms));
        var hanging = new RecEngine(host.Environment, new TokenOnlyProvider());
        Assert.True(answering.Load(host.Request).Applied && hanging.Load(host.Request).Applied);

        Assert.True((await answering.ExecuteAsync(Request("req-measured"), TestContext.Current.CancellationToken)).Succeeded);
        Assert.False((await hanging.ExecuteAsync(Request("req-measured-late", deadlineMs: 100), TestContext.Current.CancellationToken)).Succeeded);

        static bool Provider(Measured m) => (string?)m.Tags[StrataRecTags.IoRole] == IoRoles.Assignment && (string?)m.Tags[StrataRecTags.IoTarget] == "provider";
        Assert.True(capture.Of(StrataRecMetrics.Names.IoDuration).Count(Provider) >= 2);
        Assert.Contains(capture.Of(StrataRecMetrics.Names.IoErrors), m => Provider(m) && (string?)m.Tags[StrataRecTags.Outcome] == "timeout");
    }

    private sealed class NullProvider : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken) => new((IReadOnlyList<string>)null!);
    }

    /// <summary>A provider that answers null assigns no arms: the request runs the baseline, as with an empty answer (ExternalArms).</summary>
    [Fact]
    public async Task AProviderAnsweringNull_AssignsNoArms()
    {
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new NullProvider());
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-null"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Experiments.Assigned);
        Assert.Empty(result.Experiments.Ignored);
    }

    /// <summary>
    /// The longest deadline a request can have is no deadline, not one in the past: the provider is waited for and the
    /// pipeline runs. The sum of the start and the deadline overflowed into the past, and every node timed out at once.
    /// </summary>
    [Fact]
    public async Task ARequestWithTheLongestDeadline_IsNotCutOff()
    {
        var provider = new GatedProvider();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);

        var longest = new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-longest",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Deadline = TimeSpan.MaxValue,
            Debug = new DebugOptions { Record = true },
        };
        var request = engine.ExecuteAsync(longest, TestContext.Current.CancellationToken).AsTask();
        provider.Answer();
        var result = await request;
        var usual = await engine.ExecuteAsync(Request("req-usual"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProviderArms.Order(), result.Experiments.Assigned.Order());
        Assert.Equal(Outcomes(usual), Outcomes(result));

        static string Outcomes(RecResult result) => string.Join(", ", result.Recording!.Runs[0].Nodes.Select(n => n.Node + "=" + n.Outcome));
    }

    private static RecRequest Request(string requestId, IReadOnlyList<string>? assignment = null, IReadOnlyList<string>? force = null, int? deadlineMs = null) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Assignment = assignment,
        Deadline = deadlineMs is { } ms ? TimeSpan.FromMilliseconds(ms) : HomeFeedTestHost.Deadline,
        Debug = new DebugOptions { Record = true, ForceArms = force },
    };
}
