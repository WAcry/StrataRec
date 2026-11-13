using System.Threading.Tasks.Sources;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// A connector's result counts only if the fetch observes it within its bound (design 4.8): a call that completes
/// without an installed wait to decide it (synchronously, or before the wait is installed) is judged when the fetch sees
/// it. Every connector here is gated on the fetch's own token, which its budget timer cancels, so nothing sleeps.
/// </summary>
public class FetchObservationTests
{
    private const int TimeoutMs = 20;

    [Fact]
    public async Task ASynchronousConnectorThatReturnsAfterTheDeadline_TimesOut_AndItsValuesAreNotBackfilled()
    {
        var connector = new ScriptedConnector((keys, fields, writer, token) =>
        {
            token.WaitHandle.WaitOne(); // blocks the calling thread until the fetch's budget timer fires
            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 2.0);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var (request, score, hook) = Request(connector);

        var result = await request.EnsureAsync(score);

        Assert.True(result.Failed);
        Assert.Contains($"timed out after {TimeoutMs} ms", result.Error);
        Assert.False(request.IsFilled(score, 0));
        var call = Assert.Single(hook.Recorded);
        Assert.Equal((IoOutcome.Timeout, FetchOutcome.Failed), (call.Outcome, call.Status.Outcome));
        Assert.Null(call.Values); // the staging of a late call is never read, not even to record it
    }

    [Fact]
    public async Task ASynchronousConnectorThatReturnsAfterTheCallerCancelled_IsCancelled()
    {
        using var caller = new CancellationTokenSource();
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            caller.Cancel(); // the caller gives up while the connector is still working
            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 2.0);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var (request, score, hook) = Request(connector);

        var result = await request.Fetcher.EnsureAsync(new[] { score }, caller.Token);

        Assert.True(result.Failed);
        Assert.Contains("was cancelled", result.Error);
        Assert.False(request.IsFilled(score, 0));
        Assert.Equal(IoOutcome.Cancelled, Assert.Single(hook.Recorded).Outcome);
    }

    [Fact]
    public async Task AnAsyncConnectorCompletingAfterTheDeadline_BeforeTheWaitIsInstalled_TimesOut()
    {
        // Pending when the fetch checks it; it completes while the fetch turns it into a task, after the deadline, so
        // WaitAsync returns the completed task at once without comparing it with the bound.
        var source = new CompletesWhenRegistered(waitForTheBound: true);
        var (request, score, hook) = Request(source.Connector());

        var result = await request.EnsureAsync(score);

        Assert.True(source.RegisteredWhilePending);
        Assert.True(result.Failed);
        Assert.Contains($"timed out after {TimeoutMs} ms", result.Error);
        Assert.False(request.IsFilled(score, 0));
        var call = Assert.Single(hook.Recorded);
        Assert.Equal(IoOutcome.Timeout, call.Outcome);
        Assert.Null(call.Values);
    }

    [Fact]
    public async Task AnAsyncConnectorCompletingInTime_BeforeTheWaitIsInstalled_Counts()
    {
        var source = new CompletesWhenRegistered(waitForTheBound: false);
        var (request, score, hook) = Request(source.Connector(), timeoutMs: 600_000);

        var result = await request.EnsureAsync(score);

        Assert.True(source.RegisteredWhilePending);
        Assert.False(result.Failed, result.Error);
        Assert.Equal(2.0, request.Value(score, 0).AsFloat64());
        Assert.Equal(4.0, request.Value(score, 1).AsFloat64());
        var call = Assert.Single(hook.Recorded);
        Assert.Equal(IoOutcome.Ok, call.Outcome);
        Assert.Equal(new double?[] { 2.0, 4.0 }, call.Values);
    }

    private static (TestRequest Request, int Score, FeatureCallHookTests.ListHook Hook) Request(IFeatureSourceConnector connector, int timeoutMs = TimeoutMs)
    {
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: timeoutMs);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new FeatureCallHookTests.ListHook();
        return (rig.NewRequest(new long[] { 1, 2 }, deadlineMs: 600_000, hook: hook), score, hook);
    }

    /// <summary>
    /// A well-behaved <see cref="IValueTaskSource{TResult}"/> that is pending until a continuation is registered, which
    /// happens while the fetch turns the call into a task; it then completes and runs the continuation inline, so the task
    /// is complete before the fetch installs its wait. With <c>waitForTheBound</c> it first waits for the connector's
    /// token, which the fetch's budget timer cancels.
    /// </summary>
    private sealed class CompletesWhenRegistered(bool waitForTheBound) : IValueTaskSource<FetchStatus>
    {
        private bool _completed;
        private CancellationToken _token;

        public bool RegisteredWhilePending { get; private set; }

        public IFeatureSourceConnector Connector() => new ScriptedConnector((keys, fields, writer, token) =>
        {
            _token = token;
            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 2.0);
            return new ValueTask<FetchStatus>(this, 0);
        });

        public ValueTaskSourceStatus GetStatus(short token) => _completed ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;

        public FetchStatus GetResult(short token) => _completed ? FetchStatus.Ok : throw new InvalidOperationException("not completed");

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            RegisteredWhilePending = !_completed;
            if (waitForTheBound)
            {
                _token.WaitHandle.WaitOne();
            }

            _completed = true;
            continuation(state);
        }
    }
}
