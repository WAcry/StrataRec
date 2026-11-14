using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Where a request's arms come from (design 11.1): forced arms, then the platform's answer the caller passed, then the
/// assignment provider, otherwise none. A pending provider and the deadline are covered by <see cref="Review4RegressionTests"/>.
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

        var result = await engine.ExecuteAsync(Request("req-ctx"));

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
        await engine.ExecuteAsync(Request("req-ctx-2"));
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

        var fromProvider = await engine.ExecuteAsync(Request("req-provider"));
        var fromCaller = await engine.ExecuteAsync(Request("req-caller", assignment: platform));
        var fromNothing = await engine.ExecuteAsync(Request("req-empty", assignment: Array.Empty<string>()));
        var fromForce = await engine.ExecuteAsync(Request("req-forced", assignment: platform, force: forced));

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

        var result = await engine.ExecuteAsync(Request("req-none"));

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
        // Review 18 #2: an answer the engine observes complete is judged when it is observed, as BoundedCall judges a
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
        // Review 18 #2: the provider blocks the request's thread until the deadline has cancelled its token, then answers.
        // The engine cannot cut that short, but it does not take the late answer.
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new BlockingProvider(token => Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))));
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-late",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Deadline = TimeSpan.FromMilliseconds(50),
        });

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

    /// <summary>Timers that fire as soon as they are started: the wait for the provider ends before anything else can.</summary>
    private sealed class ImmediateTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new Stopped();
        }

        private sealed class Stopped : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }

    [Fact]
    public async Task AProviderAbandonedAtTheWaitsTimeout_SeesItsTokenCancelled()
    {
        // Review 18 #2, follow-up: the wait and the timer that cancels the provider's token both aim at the deadline, and
        // the wait can end first. Disposing the source then stopped that timer: the token was never cancelled. Here the
        // wait's timer fires at once and the deadline's is a minute away, so the order is fixed.
        var provider = new TokenOnlyProvider();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider) { AssignmentWaitTimers = new ImmediateTimers() };
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-wait-abandons"));

        Assert.False(result.Succeeded);
        Assert.Equal("deadline exceeded during assignment", result.Error);
        await AssertEnds(provider);
    }

    [Fact]
    public async Task AProviderAbandonedForTheCallersCancellation_SeesItsTokenCancelled()
    {
        // Review 18 #2, follow-up: when the caller cancels, the wait's own registration on the caller's token can end the
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
        await Task.Run(caller.Cancel);

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

    /// <summary>A logger whose event 1102 throws once it is recorded.</summary>
    private sealed class ThrowingOn1102 : ILoggerFactory, ILogger
    {
        public TaskCompletionSource<Exception?> Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1102)
            {
                Logged.TrySetResult(exception);
                throw new InvalidOperationException("the logger failed");
            }
        }
    }

    [Fact]
    public async Task AnAbandonedProviderWhoseCallbackAndLoggerThrow_KeepsItsTokenUntilItEnds_ThenItIsDisposed()
    {
        // Review 20 #3: the callback's failure is logged (1102), and the logger throws too. That used to end the cleanup
        // there: the provider's late failure was never observed and its token source never disposed.
        var provider = new FailingCleanupProvider();
        var logger = new ThrowingOn1102();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider, loggerFactory: logger) { AssignmentWaitTimers = new ImmediateTimers() };
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-cleanup-fails"));

        Assert.Equal("deadline exceeded during assignment", result.Error);
        var logged = await logger.Logged.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("the provider's cleanup failed", logged!.ToString());
        Assert.True(provider.Token.IsCancellationRequested);
        Assert.False(Disposed(provider.Token)); // the provider is still running: its token stays valid

        provider.End();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!Disposed(provider.Token) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(Disposed(provider.Token), "the abandoned provider's token source was not disposed after it ended");
    }

    private static bool Disposed(CancellationToken token)
    {
        try
        {
            _ = token.WaitHandle;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>Answers when told to: a provider whose answer comes at a moment the test picks.</summary>
    private sealed class GatedProvider : IAssignmentProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Answer() => _answer.SetResult(ProviderArms);

        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken) => new(_answer.Task);
    }

    /// <summary>
    /// The wait's timer, run by the test as the wait sets it up: <paramref name="setUp"/> gets the timer's callback, which
    /// ends the wait with its timeout. The real deadline is minutes away, so nothing else decides.
    /// </summary>
    private sealed class WaitTimers(Action<Action> setUp) : TimeProvider
    {
        public int Created { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Created++;
            setUp(() => callback(state));
            return new StoppedTimer();
        }

        private sealed class StoppedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }

    [Fact]
    public async Task AProviderAnsweringAfterItsWaitTimedOut_IsRejectedAsPastTheDeadline()
    {
        // Review 20 #2: the wait times out, then the provider answers, both before the engine looks. Both tasks are
        // complete then, but the wait was set up and decided: the answer lost, however close it came. It used to be taken
        // as if WaitAsync had found the provider complete, and judged by the clock, a minute from the deadline.
        var provider = new GatedProvider();
        var timers = new WaitTimers(timeOut =>
        {
            timeOut();
            provider.Answer();
        });
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider) { AssignmentWaitTimers = timers };
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-wait-first"));

        Assert.Equal(1, timers.Created);
        Assert.False(result.Succeeded);
        Assert.Equal("deadline exceeded during assignment", result.Error);
        Assert.Empty(result.Experiments.Assigned);
    }

    [Fact]
    public async Task AProviderAnsweringWhileItsWaitIsSetUp_IsTaken()
    {
        // The control: the provider answers once its wait is set up, and the wait's timeout never comes. The wait ends
        // with the answer.
        var provider = new GatedProvider();
        var timers = new WaitTimers(_ => provider.Answer());
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider) { AssignmentWaitTimers = timers };
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-provider-first"));

        Assert.Equal(1, timers.Created);
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
        // The control for the fast path: WaitAsync hands back the completed provider as it is and sets up no wait (no
        // timer), so nothing decided who came first; the answer is judged when it is observed, well before the deadline.
        var timers = new WaitTimers(_ => throw new InvalidOperationException("a wait was set up for a completed provider"));
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new CompletesOnSecondLook()) { AssignmentWaitTimers = timers };
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-fast-path"));

        Assert.Equal(0, timers.Created);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProviderArms.Order(), result.Experiments.Assigned.Order());
    }

    private static RecRequest Request(string requestId, IReadOnlyList<string>? assignment = null, IReadOnlyList<string>? force = null) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Assignment = assignment,
        Deadline = HomeFeedTestHost.Deadline,
        Debug = new DebugOptions { Record = true, ForceArms = force },
    };
}
