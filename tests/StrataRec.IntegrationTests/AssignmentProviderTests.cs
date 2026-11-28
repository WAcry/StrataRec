using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;

namespace StrataRec.IntegrationTests;

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

        Assert.False(context.Layers is LayerInfo[]);
        Assert.True(((ICollection<LayerInfo>)context.Layers).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((IList<ArmInfo>)context.Arms)[0] = context.Arms[1]);

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
        Assert.Empty(fromNothing.Experiments.Assigned);
        Assert.Equal("external", fromNothing.Recording!.Request.Assignment.Source);
        Assert.Equal(forced, fromForce.Experiments.Assigned);
        Assert.Equal("forced", fromForce.Recording!.Request.Assignment.Source);
        Assert.Single(provider.Calls);
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
        // An answer the engine observes complete is judged when it is observed: the caller's cancellation comes first,
        // so no pipeline runs on the answer (a run would deliver its aborted bundle to OnRecording).
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
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new BlockingProvider(token => Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))));
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-late",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Deadline = TimeSpan.FromMilliseconds(500),
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("deadline exceeded during assignment", result.Error);
        Assert.Empty(result.Experiments.Assigned);
        Assert.Empty(result.Experiments.Ignored);
    }

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

    private static async Task AssertEnds(TokenOnlyProvider provider)
    {
        var ended = await Task.WhenAny(provider.Ended, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(ended, provider.Ended), "the abandoned provider's token was never cancelled");
    }

    [Fact]
    public async Task AProviderAbandonedAtTheDeadline_SeesItsTokenCancelled()
    {
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
        using var caller = new CancellationTokenSource();
        var provider = new TokenOnlyProvider();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);

        var request = engine.ExecuteAsync(Request("req-caller-abandons"), caller.Token).AsTask();
        Assert.False(request.IsCompleted);

        // From a thread without a synchronization context, as in a server, so Cancel may end the request inline before
        // it cancels the provider's token; xUnit's context would keep the request's continuation out of Cancel.
        await Task.Run(caller.Cancel, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await AssertEnds(provider);
    }

    /// <summary>
    /// Keeps its token, to tell when its source is disposed.
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
        Assert.False(DisposedTokens.Disposed(provider.Token));

        provider.End();
        Assert.True(DisposedTokens.WaitUntilDisposed(provider.Token, TimeSpan.FromSeconds(30)), "the abandoned provider's token source was not disposed after it ended");
    }

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
        Assert.False(request.IsCompleted);
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
        // throw).
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new CompletesOnSecondLook());
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-fast-path"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProviderArms.Order(), result.Experiments.Assigned.Order());
    }

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
