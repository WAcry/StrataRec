using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Threading;
using StrataRec.UnitTests.Features.Cache;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// Review 20 #1: every cancellation of a token the framework handed to code it does not own runs that code's callbacks
/// on the pool and observes what they throw (<see cref="ObservedCancellationSource"/>). The sites' own regressions, with
/// the system's timers and in a child process, are the integration tests' <c>TimerFaultTests</c>.
/// </summary>
[Collection(MetricMeasurement.Name)]
public sealed class ObservedCancellationSourceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ATimedCancellation_RunsAThrowingCallbackOffTheTimer_AndCountsReportsAndLogsIt()
    {
        using var counted = new Counted();
        var logger = new ListLogger();
        var events = new RequestEventLog(logger, autoFlush: false);
        using var source = new Logging(CancellationSite.Fetch, events, "req-1");
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Token.Register(() =>
        {
            ran.TrySetResult();
            throw new InvalidOperationException("cleanup failed");
        });

        source.CancelAfterObserved(TimeSpan.FromMilliseconds(10));

        await ran.Task.WaitAsync(Patience);
        var failure = await source.Failed.Task.WaitAsync(Patience);
        Assert.Equal("cleanup failed", Assert.IsType<InvalidOperationException>(Assert.Single(failure.Flatten().InnerExceptions)).Message);
        Assert.True(SpinWait.SpinUntil(() => counted.Of("fetch") == 1, Patience));
        events.Flush();
        var summary = Assert.Single(logger.Entries, e => e.EventId.Id == 2001);
        Assert.Equal(nameof(RequestEvent.CancellationCallbacksFailed), summary.State["Event"]!.ToString());
        Assert.Equal(("fetch", "req-1", "cleanup failed"), ((string)summary.State["Subject"]!, (string?)summary.State["SampleRequestId"], (string?)summary.State["SampleError"]));
    }

    [Fact]
    public void ACallersCancellation_ReachesTheToken_AtOnce_ButItsCallbacksRunOnThePool_AndNothingIsThrownIntoTheCaller()
    {
        // The caller's own token may be cancelled by its timer (a request timeout made with CancelAfter): a linked source
        // that ran an extension's callbacks in that cancellation would run them on the timer's thread all the same.
        using var counted = new Counted();
        using var caller = new CancellationTokenSource();
        using var source = new ObservedCancellationSource(CancellationSite.Node, caller.Token);
        using var release = new ManualResetEventSlim();
        source.Token.Register(() =>
        {
            release.Wait(Patience); // blocks the canceller, if it is the canceller's thread that runs it
            throw new InvalidOperationException("cleanup failed");
        });

        var watch = Stopwatch.StartNew();
        caller.Cancel(); // throws the callback's exception, after the patience, when the link runs callbacks inline
        var cancelling = watch.Elapsed;
        Assert.True(source.IsCancellationRequested);
        release.Set();

        Assert.True(cancelling < Patience / 2, $"the caller's Cancel took {cancelling}: the callback ran in it");
        Assert.True(SpinWait.SpinUntil(() => counted.Of("node") == 1, Patience));
    }

    [Fact]
    public void ASourceNestedInAnother_IsCancelledByItsTimer_WithTheCallbacksOfBothObserved()
    {
        // The node token and a call's own token linked to it: the node's deadline passes the cancellation on.
        using var counted = new Counted();
        using var node = new ObservedCancellationSource(CancellationSite.Node);
        using var call = new ObservedCancellationSource(CancellationSite.Call, node.Token);
        var both = new CountdownEvent(2);
        node.Token.Register(() =>
        {
            both.Signal();
            throw new InvalidOperationException("the operator's cleanup failed");
        });
        call.Token.Register(() =>
        {
            both.Signal();
            throw new InvalidOperationException("the client's cleanup failed");
        });

        node.CancelAfterObserved(TimeSpan.FromMilliseconds(10));

        Assert.True(both.Wait(Patience));
        Assert.True(SpinWait.SpinUntil(() => counted.Of("node") == 1 && counted.Of("call") == 1, Patience));
        Assert.True(call.IsCancellationRequested);
    }

    [Fact]
    public void ADisposedSource_StopsItsTimer_AndIgnoresALateCancellation()
    {
        using var counted = new Counted();
        var source = new ObservedCancellationSource(CancellationSite.Call);
        source.Token.Register(() => throw new InvalidOperationException("must not run"));
        source.CancelAfterObserved(TimeSpan.FromMilliseconds(20));
        source.Dispose();

        Thread.Sleep(200);
        Assert.False(source.IsCancellationRequested);
        Assert.True(source.CancelObservedAsync().IsCompletedSuccessfully); // disposed: left as it is
        Assert.Equal(0, counted.Of("call"));
    }

    [Fact]
    public async Task AnAbandonment_ReturnsTheCallbacksTask_FaultedWithWhatTheyThrew_AndCountsItOnce()
    {
        using var counted = new Counted();
        using var source = new ObservedCancellationSource(CancellationSite.Assignment);
        source.Token.Register(() => throw new InvalidOperationException("cleanup failed"));

        var callbacks = source.CancelObservedAsync();
        Assert.True(source.IsCancellationRequested); // the token observes it before the callbacks have run

        // As CancellationTokenSource.Cancel throws it: the callbacks' exceptions in one AggregateException.
        var thrown = await Assert.ThrowsAsync<AggregateException>(() => callbacks);
        Assert.Equal("cleanup failed", Assert.IsType<InvalidOperationException>(Assert.Single(thrown.InnerExceptions)).Message);
        Assert.True(source.CancelObservedAsync().IsCompletedSuccessfully); // cancelled already: nothing runs again
        Assert.True(SpinWait.SpinUntil(() => counted.Of("assignment") == 1, Patience));
        Thread.Sleep(50);
        Assert.Equal(1, counted.Of("assignment"));
    }

    [Fact]
    public async Task AReportThatThrows_IsDropped_AndTheFailureStillCounted()
    {
        using var counted = new Counted();
        var events = new RequestEventLog(new ListLogger(), autoFlush: false);
        using var source = new Logging(CancellationSite.Call, events, "req-1") { Throws = true };
        source.Token.Register(() => throw new InvalidOperationException("cleanup failed"));

        await Assert.ThrowsAsync<AggregateException>(() => source.CancelObservedAsync());

        await source.Failed.Task.WaitAsync(Patience);
        Assert.True(SpinWait.SpinUntil(() => counted.Of("call") == 1, Patience));
    }

    /// <summary>A source with a logger of its own: records what it was told, and throws if asked to.</summary>
    private sealed class Logging(CancellationSite site, RequestEventLog events, string requestId) : ObservedCancellationSource(site, default, events, requestId)
    {
        public TaskCompletionSource<AggregateException> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Throws { get; init; }

        protected override void CallbacksFailed(AggregateException errors)
        {
            Failed.TrySetResult(errors);
            if (Throws)
            {
                throw new InvalidOperationException("the logger failed");
            }
        }
    }

    /// <summary>Counts <c>stratarec.cancellation.callback_errors</c> per site while alive.</summary>
    private sealed class Counted : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

        public Counted()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == StrataRecMetrics.Names.CancellationCallbackErrors)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == StrataRecTags.Token)
                    {
                        _counts.AddOrUpdate((string)tag.Value!, value, (_, count) => count + value);
                    }
                }
            });
            _listener.Start();
        }

        public long Of(string site) => _counts.TryGetValue(site, out var count) ? count : 0;

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Entry(EventId EventId, IReadOnlyDictionary<string, object?> State);

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    values[key] = value;
                }
            }

            Entries.Enqueue(new Entry(eventId, values));
        }
    }
}
