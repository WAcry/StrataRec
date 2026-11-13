using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// The time budget of one node attempt (design 5.6): its absolute deadline, the node token handed to the operator and
/// its clients (linked to the caller's token and cancelled at the deadline), and the caller's token itself.
/// </summary>
/// <remarks>
/// Reference-counted: the attempt holds one reference until it ends, and each external call one until its client has
/// really completed. The token source is disposed with the last, so a client that ignores cancellation and finishes
/// after the attempt ended still holds a live token, cancelled (or about to be) at the deadline.
/// </remarks>
internal sealed class AttemptBudget
{
    /// <summary>The longest a timer or a timed wait accepts (just under 49.7 days); longer waits are capped to it.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    /// <summary>The same, in milliseconds, for budgets computed as numbers.</summary>
    public const double MaxWaitMs = uint.MaxValue - 1.0;

    private readonly CancellationTokenSource _source;
    private int _references = 1;

    /// <param name="caller">The caller's token; the node token is linked to it.</param>
    /// <param name="deadline">
    /// The absolute deadline as a <see cref="Stopwatch"/> timestamp; <see cref="long.MaxValue"/> for an untimed attempt
    /// (a replay takes its timing decisions from the recording).
    /// </param>
    public AttemptBudget(CancellationToken caller, long deadline)
    {
        Caller = caller;
        Deadline = deadline;
        _source = CancellationTokenSource.CreateLinkedTokenSource(caller);
        if (deadline != long.MaxValue)
        {
            _source.CancelAfter(Until(deadline));
        }

        Token = _source.Token; // read once: the property throws once the source is disposed
    }

    /// <summary>The node token: cancelled at the deadline or with the caller's token.</summary>
    public CancellationToken Token { get; }

    public CancellationToken Caller { get; }

    public long Deadline { get; }

    /// <summary>Time left before the deadline; zero once it has passed, <see cref="TimeSpan.MaxValue"/> when untimed.</summary>
    public TimeSpan Remaining => Deadline == long.MaxValue ? TimeSpan.MaxValue : Until(Deadline);

    /// <summary>
    /// The late-result rule: the budget has run out and the caller has not cancelled. An operator that returns while this
    /// holds has timed out, whatever it returned and whether or not it ever looked at its token, so its outputs are not
    /// committed and the fallback runs. The deadline counts as passed when the clock says so or when the budget's timer
    /// has cancelled the node token (a timer may fire slightly before the clock reaches the deadline). Never true for an
    /// untimed attempt.
    /// </summary>
    public bool RanOut => !Caller.IsCancellationRequested && (Token.IsCancellationRequested || Stopwatch.GetTimestamp() >= Deadline);

    /// <summary>An external call is about to be sent; released when its client has really completed.</summary>
    public void AddReference() => Interlocked.Increment(ref _references);

    /// <summary>The attempt has ended, or a call's client has completed.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            _source.Dispose();
        }
    }

    /// <summary>The time from now until <paramref name="deadline"/>: never negative, and capped to what timers accept.</summary>
    public static TimeSpan Until(long deadline)
    {
        if (deadline == long.MaxValue)
        {
            return MaxWait;
        }

        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < MaxWait ? remaining : MaxWait;
    }
}

/// <summary>How one bounded external call ended, as the request saw it.</summary>
internal enum CallEnd : byte
{
    /// <summary>The client returned a value in time; <see cref="CallResult{T}.Outcome"/> says whether it is a success.</summary>
    Answered = 1,

    /// <summary>The client threw in time (anything but cancellation).</summary>
    Threw,

    /// <summary>The call's own timeout: the wait was cut off at it, or the client honoured it (or cancelled on its own).</summary>
    TimedOut,

    /// <summary>The node's budget ran out.</summary>
    BudgetRanOut,

    /// <summary>The caller cancelled the request.</summary>
    Cancelled,
}

/// <summary>The result of <see cref="BoundedCall.RunAsync{T, TCall}"/>.</summary>
/// <param name="Value">What the client returned; default unless <see cref="CallEnd.Answered"/>.</param>
/// <param name="Exception">
/// What the client threw (<see cref="CallEnd.Threw"/>); for <see cref="CallEnd.BudgetRanOut"/> and
/// <see cref="CallEnd.Cancelled"/>, the cancellation to throw to the operator: the client's own, or a new one when it was
/// abandoned.
/// </param>
/// <param name="Outcome">The call's outcome for metrics, health and recordings.</param>
/// <param name="Error">The error text: a failed answer's, a thrown exception's message, or the timeout's.</param>
/// <param name="Abandoned">The client was still running when the wait ended; it may still write into what it was handed.</param>
internal readonly record struct CallResult<T>(CallEnd End, T? Value, Exception? Exception, IoOutcome Outcome, string? Error, bool Abandoned);

/// <summary>What one kind of call site gives <see cref="BoundedCall.RunAsync{T, TCall}"/>: a struct, so nothing is allocated for it.</summary>
internal interface IBoundedCall<T>
{
    /// <summary>Sends the call; <paramref name="token"/> is the node token, or one its own timeout also cancels.</summary>
    ValueTask<T> Send(CancellationToken token);

    /// <summary>Whether an answer is a success; otherwise its error text.</summary>
    bool Succeeded(T value, out string? error);

    /// <summary>
    /// Called exactly once, when the call has ended and before its hold on the request memory is released: the place to
    /// close what the client writes into and to record the call. Only an <see cref="CallEnd.Answered"/> call's data may be
    /// read; an abandoned client may still be writing.
    /// </summary>
    void Ended(in CallResult<T> result, TimeSpan duration);
}

/// <summary>
/// The framework's I/O boundary for candidate sources and models (design 4.8, 5.6, 13.1): one external call, bounded on
/// its own whether or not the client honours cancellation. Both <see cref="OperatorContext.RetrieveAsync"/> and
/// <see cref="OperatorContext.InvokeModelAsync"/> go through it; they only translate the result.
/// </summary>
/// <remarks>
/// <para>Rules, each locked by tests: (1) the wait ends at the call's own timeout or the node's deadline, whichever is
/// first, or when the caller cancels, and never waits on a token the client holds (cancelling a token runs the client's
/// callbacks one after another, and one that blocks would hold the wait); (2) the call holds the request memory once,
/// from before it is sent until both the client has really completed and the call site has recorded it, so an abandoned
/// client writes only into live buffers and <c>stratarec.io.abandoned</c> counts it once; (3) the tokens the client holds
/// stay alive until it completes; (4) the outcome is <c>cancelled</c> when the caller cancelled (neither timed nor counted
/// against the dependency), <c>timeout</c> for a timeout, the budget or a client's <see cref="TimeoutException"/>, and
/// <c>error</c> otherwise; (5) the call is completed (metrics, health, span) and recorded exactly once, at the call site;
/// (6) a client that answers synchronously costs no lease, continuation, timer or wait.</para>
/// <para>Feature fetches have their own implementation of the same rules (FeatureFetcher), with one token source and
/// one deadline per fetch for all its chunks.</para>
/// </remarks>
internal static class BoundedCall
{
    /// <summary>
    /// Sends one call and waits for it within its bound. A client that answers synchronously is completed, recorded and
    /// released here, with nothing allocated (this is not an async method: Debug builds compile async state machines as
    /// classes); only a pending one goes on to <see cref="AwaitAsync{T, TCall}"/>.
    /// </summary>
    /// <param name="call">The call site: sends the call, judges an answer, records the end.</param>
    /// <param name="budget">The attempt's budget: the node token and deadline, and the caller's token.</param>
    /// <param name="memory">The request memory, held for the call (rule 2).</param>
    /// <param name="timeoutMs">The call's own timeout (its binding's <c>timeout_ms</c>); null for none.</param>
    public static ValueTask<CallResult<T>> RunAsync<T, TCall>(
        TCall call, AttemptBudget budget, RequestMemory memory, IoTarget target, int? timeoutMs, string requestId, RequestEventLog? events)
        where TCall : struct, IBoundedCall<T>
    {
        memory.Hold();
        budget.AddReference();
        var (own, token, deadline) = Bound(budget, timeoutMs);
        var io = target.Start();
        ValueTask<T> pending;
        try
        {
            pending = call.Send(token);
        }
        catch (Exception ex)
        {
            pending = ValueTask.FromException<T>(ex);
        }

        if (!pending.IsCompleted)
        {
            return AwaitAsync(call, pending.AsTask(), io, budget, memory, own, deadline, timeoutMs, requestId, events);
        }

        var result = Defect<T>();
        try
        {
            result = Completed(call, pending, budget, own, timeoutMs);
        }
        finally
        {
            try
            {
                End(call, io, result, requestId, events);
            }
            finally
            {
                own?.Dispose();
                budget.Release();
                memory.Release();
            }
        }

        return new ValueTask<CallResult<T>>(result);
    }

    /// <summary>The rest of a call whose client did not answer synchronously; the memory and the budget are held already.</summary>
    private static async ValueTask<CallResult<T>> AwaitAsync<T, TCall>(
        TCall call, Task<T> client, IoCall io, AttemptBudget budget, RequestMemory memory, CancellationTokenSource? own, long deadline, int? timeoutMs,
        string requestId, RequestEventLog? events)
        where TCall : struct, IBoundedCall<T>
    {
        var lease = Lease.Until(client, memory, budget, own);
        var result = Defect<T>();
        try
        {
            // Rule 1: the wait has its own bound and ends with the client still running if need be.
            await ((Task)client).WaitAsync(AttemptBudget.Until(deadline), budget.Caller).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            result = client.IsCompleted
                ? Completed(call, new ValueTask<T>(client), budget, own, timeoutMs)
                : Unanswered<T>(budget, own, timeoutMs, null, abandoned: true);
            return result;
        }
        finally
        {
            try
            {
                End(call, io, result, requestId, events);
            }
            finally
            {
                lease.CallSiteDone();
            }
        }
    }

    /// <summary>Rule 5: completes the call's metrics, health and span, and has the call site record it, once.</summary>
    private static void End<T, TCall>(TCall call, in IoCall io, in CallResult<T> result, string requestId, RequestEventLog? events)
        where TCall : struct, IBoundedCall<T>
    {
        io.Complete(result.Outcome, -1, requestId, result.Error, events);
        call.Ended(result, io.Elapsed);
    }

    /// <summary>The result recorded if the framework itself throws before the call is classified.</summary>
    private static CallResult<T> Defect<T>() => new(CallEnd.Threw, default, null, IoOutcome.Error, null, false);

    /// <summary>
    /// The token the client gets and when the wait ends. The call's own timeout gets a token source of its own only when it
    /// comes before the node's deadline; otherwise the node token already says everything.
    /// </summary>
    private static (CancellationTokenSource? Own, CancellationToken Token, long Deadline) Bound(AttemptBudget budget, int? timeoutMs)
    {
        if (timeoutMs is { } timeout)
        {
            var milliseconds = Math.Max(0, timeout);
            var deadline = Stopwatch.GetTimestamp() + ((long)milliseconds * Stopwatch.Frequency / 1000);
            if (deadline < budget.Deadline)
            {
                var own = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                own.CancelAfter(milliseconds);
                return (own, own.Token, deadline);
            }
        }

        return (null, budget.Token, budget.Deadline);
    }

    /// <summary>A client that completed in time: its answer, what it threw, or the cancellation it honoured.</summary>
    private static CallResult<T> Completed<T, TCall>(TCall call, ValueTask<T> pending, AttemptBudget budget, CancellationTokenSource? own, int? timeoutMs)
        where TCall : struct, IBoundedCall<T>
    {
        try
        {
            var value = pending.GetAwaiter().GetResult();
            return call.Succeeded(value, out var error)
                ? new CallResult<T>(CallEnd.Answered, value, null, IoOutcome.Ok, null, false)
                : new CallResult<T>(CallEnd.Answered, value, null, IoOutcome.Error, error, false);
        }
        catch (OperationCanceledException ex)
        {
            return Unanswered<T>(budget, own, timeoutMs, ex, abandoned: false);
        }
        catch (Exception ex)
        {
            // A client's own transport timeout is a timeout, as for feature sources.
            return new CallResult<T>(CallEnd.Threw, default, ex, ex is TimeoutException ? IoOutcome.Timeout : IoOutcome.Error, ex.Message, false);
        }
    }

    /// <summary>
    /// A call that ended without an answer, either cut off by the wait (<paramref name="abandoned"/>) or by the client
    /// honouring a token (<paramref name="honoured"/>): the caller first, then the node's budget, else the call's own timeout.
    /// </summary>
    private static CallResult<T> Unanswered<T>(AttemptBudget budget, CancellationTokenSource? own, int? timeoutMs, OperationCanceledException? honoured, bool abandoned)
    {
        if (budget.Caller.IsCancellationRequested)
        {
            return new CallResult<T>(CallEnd.Cancelled, default, honoured ?? new OperationCanceledException(budget.Caller), IoOutcome.Cancelled, null, abandoned);
        }

        // A wait without a timeout of its own ended at the node's deadline, even if the budget's timer has not fired yet.
        if (budget.Token.IsCancellationRequested || (abandoned && own is null))
        {
            var cancelled = honoured ?? new OperationCanceledException("The node's budget ran out before the call completed.", budget.Token);
            return new CallResult<T>(CallEnd.BudgetRanOut, default, cancelled, IoOutcome.Timeout, null, abandoned);
        }

        var error = timeoutMs is { } timeout ? $"timed out after {timeout} ms" : "timed out";
        return new CallResult<T>(CallEnd.TimedOut, default, null, IoOutcome.Timeout, error, abandoned);
    }

    /// <summary>
    /// What a call whose client did not answer synchronously holds: the hold on the request memory taken before it was
    /// sent, a reference to the attempt's budget, and its own token source. The memory is released when both the client
    /// has really completed and the call site has recorded the call, in either order (rule 2); the token sources when the
    /// client has completed (rule 3).
    /// </summary>
    private sealed class Lease(RequestMemory memory, AttemptBudget budget, CancellationTokenSource? own)
    {
        private int _parts = 2;

        public static Lease Until(Task client, RequestMemory memory, AttemptBudget budget, CancellationTokenSource? own)
        {
            var lease = new Lease(memory, budget, own);
            _ = client.ContinueWith(
                static (t, state) =>
                {
                    _ = t.Exception; // observe a late failure nobody awaits any more
                    ((Lease)state!).ClientDone();
                },
                lease,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return lease;
        }

        /// <summary>The call site has recorded the call.</summary>
        public void CallSiteDone() => Release();

        /// <summary>The client's task has completed: it writes no more and no longer needs its token.</summary>
        private void ClientDone()
        {
            own?.Dispose();
            budget.Release();
            Release();
        }

        private void Release()
        {
            if (Interlocked.Decrement(ref _parts) == 0)
            {
                memory.Release();
            }
        }
    }
}
