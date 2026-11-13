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

    /// <summary>
    /// The call's own timeout: the call was cut off at it (the wait ended, or the client completed only after it), or the
    /// client honoured it (or cancelled on its own).
    /// </summary>
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
/// <para>Rules, each locked by tests: (1) the call's bound is the earlier of its own timeout and the node's deadline, or
/// the caller cancelling; the wait ends at it and never waits on a token the client holds (cancelling a token runs the
/// client's callbacks one after another, and one that blocks would hold the wait), and what the client returns, throws or
/// honours is heard only if the framework observes it completed within the bound (<see cref="Winner"/>): by a wait it
/// set up, or, when no wait was set up (the client answered before Send returned, or completed before the wait could be
/// set up), by the bound not having passed when it looks; never when the bound had won, even an instant before; (2) the
/// call holds the request memory once, from before it is sent until both
/// the client has really completed and the call site has recorded it, so an abandoned client writes only into live
/// buffers and <c>stratarec.io.abandoned</c> counts it once; (3) the tokens the client holds stay alive until it
/// completes; (4) the outcome is <c>cancelled</c> when the caller cancelled (neither timed nor counted against the
/// dependency), <c>timeout</c> for a timeout, the budget or a client's <see cref="TimeoutException"/>, and <c>error</c>
/// otherwise; (5) the call is completed (metrics, health, span) and recorded exactly once, at the call site; (6) a client
/// that answers synchronously costs no lease, continuation or wait, and a call that shares the node's budget allocates
/// nothing at all; a call with an own timeout shorter than the budget costs one linked token source and its timer (about
/// 160 B), created before it is sent so that the client's token carries the timeout.</para>
/// <para>Feature fetches have their own implementation of the same rules (FeatureFetcher), with one token source and
/// one deadline per fetch for all its chunks.</para>
/// </remarks>
internal static class BoundedCall
{
    /// <summary>
    /// Sends one call and waits for it within its bound. A client that answers synchronously is completed, recorded and
    /// released here, with nothing allocated beyond the call's own token source if it has one (rule 6; this is not an
    /// async method: Debug builds compile async state machines as classes); only a pending one goes on to
    /// <see cref="AwaitAsync{T, TCall}"/>.
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
            return AwaitAsync(call, pending.AsTask(), io, budget, memory, own, token, deadline, timeoutMs, requestId, events);
        }

        var result = Defect<T>();
        try
        {
            // Rule 1: the client completed before Send returned; the framework observes it now.
            result = Observed(call, pending, budget, own, token, deadline, timeoutMs);
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
    /// <param name="token">
    /// The client's token, read before the call was sent: the lease disposes <paramref name="own"/> as soon as the client
    /// has completed, which may be before the wait is even set up, and a disposed source's token can no longer be read.
    /// </param>
    private static async ValueTask<CallResult<T>> AwaitAsync<T, TCall>(
        TCall call, Task<T> client, IoCall io, AttemptBudget budget, RequestMemory memory, CancellationTokenSource? own, CancellationToken token,
        long deadline, int? timeoutMs, string requestId, RequestEventLog? events)
        where TCall : struct, IBoundedCall<T>
    {
        var lease = Lease.Until(client, memory, budget, own);
        var result = Defect<T>();
        try
        {
            // Rule 1: the wait has its own bound and ends with the client still running if need be.
            var wait = ((Task)client).WaitAsync(AttemptBudget.Until(deadline), budget.Caller);
            if (wait.IsCompleted && client.IsCompleted)
            {
                // No wait was set up: WaitAsync returns a completed client as it is, whatever the time left or the caller,
                // so its outcome says nothing about who came first. The client completed after Send returned pending and
                // before the wait (while its ValueTask became a task, say): the framework observes it now, as an answer
                // that came before Send returned.
                result = Observed(call, new ValueTask<T>(client), budget, own, token, deadline, timeoutMs);
                return result;
            }

            // A wait that was set up has an outcome saying who won; the client may complete between the wait's end and
            // this continuation, so the client's state cannot. (One that ended at once without the client found the bound
            // passed or the caller gone.)
            await wait.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var winner = Waited(wait, client, budget.Caller);
            result = winner == Winner.Client
                ? Completed(call, new ValueTask<T>(client), budget, timeoutMs)
                : CutOff<T>(winner, null, own, budget, timeoutMs, abandoned: true);
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

    /// <summary>
    /// Who ended a call (rule 1): the client, by completing within the call's bound (with an answer, a failure or a
    /// cancellation of its own), or the bound or the caller before it. Decided once, from how the call ended, and never
    /// re-derived from the client's state afterwards: a client that completes an instant after it lost stays lost.
    /// </summary>
    private enum Winner : byte
    {
        Client,

        /// <summary>The call's deadline: its own timeout, or the node's when it has none shorter.</summary>
        Bound,

        Caller,
    }

    /// <summary>
    /// Rule 1 for a completed client that no wait bounded: one that answered before Send returned, or one that completed
    /// before its wait was set up. The framework observes it now, so it is heard only if its bound has not passed now: the
    /// caller has not cancelled, the deadline is not reached, and the timer that enforces it has not cancelled the
    /// client's token (a timer may fire slightly before the clock reaches the deadline, as for
    /// <see cref="AttemptBudget.RanOut"/>). A client that completed in time but is observed late, because the thread was
    /// preempted, has timed out all the same: the framework can only stand behind what it observed.
    /// </summary>
    /// <param name="completed">The client's outcome; must have completed.</param>
    /// <param name="token">The token the client got, read before it was sent (its source may be disposed by now).</param>
    private static CallResult<T> Observed<T, TCall>(
        TCall call, ValueTask<T> completed, AttemptBudget budget, CancellationTokenSource? own, CancellationToken token, long deadline, int? timeoutMs)
        where TCall : struct, IBoundedCall<T>
    {
        var winner = budget.Caller.IsCancellationRequested ? Winner.Caller
            : token.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadline ? Winner.Bound
            : Winner.Client;
        return winner == Winner.Client
            ? Completed(call, completed, budget, timeoutMs)
            : CutOff<T>(winner, Discard(completed), own, budget, timeoutMs, abandoned: false);
    }

    /// <summary>
    /// The winner of the wait for a pending client, read from the wait's own outcome. The wait's timeout faults it with a
    /// <see cref="TimeoutException"/> of its own, while a client's failure is passed on as the very same exception, so a
    /// client that throws a <see cref="TimeoutException"/> itself is told apart by identity. The caller's token cancels
    /// the wait; so does a client's own cancellation, which counts as the client's only while the caller has not cancelled.
    /// </summary>
    private static Winner Waited(Task wait, Task client, CancellationToken caller) => wait.Status switch
    {
        TaskStatus.RanToCompletion => Winner.Client,
        TaskStatus.Faulted => IsClientFailure(wait.Exception!, client) ? Winner.Client : Winner.Bound,
        _ => caller.IsCancellationRequested ? Winner.Caller : Winner.Client,
    };

    private static bool IsClientFailure(AggregateException waited, Task client)
    {
        var thrown = waited.InnerExceptions[0];
        if (client.Exception is { } failure)
        {
            var exceptions = failure.InnerExceptions;
            for (var i = 0; i < exceptions.Count; i++)
            {
                if (ReferenceEquals(exceptions[i], thrown))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A client that completed in time: its answer, what it threw, or the cancellation it honoured.</summary>
    private static CallResult<T> Completed<T, TCall>(TCall call, ValueTask<T> pending, AttemptBudget budget, int? timeoutMs)
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
            // It honoured a token (or cancelled on its own) in time: the tokens say whose, the caller's first.
            return budget.Caller.IsCancellationRequested ? Cancelled<T>(budget, ex, abandoned: false)
                : budget.Token.IsCancellationRequested ? RanOut<T>(budget, ex, abandoned: false)
                : TimedOut<T>(timeoutMs, abandoned: false);
        }
        catch (Exception ex)
        {
            // A client's own transport timeout is a timeout, as for feature sources.
            return new CallResult<T>(CallEnd.Threw, default, ex, ex is TimeoutException ? IoOutcome.Timeout : IoOutcome.Error, ex.Message, false);
        }
    }

    /// <summary>
    /// A call the bound or the caller won (rule 1): nothing the client returned or threw is used. A bound without a
    /// timeout of its own is the node's deadline, so the budget ran out (even if the budget's timer has not fired yet);
    /// otherwise the call's own timeout expired.
    /// </summary>
    /// <param name="honoured">A cancellation the client threw after it lost, thrown on to the operator as its own.</param>
    /// <param name="abandoned">The client was still running when the call ended.</param>
    private static CallResult<T> CutOff<T>(
        Winner winner, OperationCanceledException? honoured, CancellationTokenSource? own, AttemptBudget budget, int? timeoutMs, bool abandoned) =>
        winner == Winner.Caller ? Cancelled<T>(budget, honoured, abandoned)
        : own is null ? RanOut<T>(budget, honoured, abandoned)
        : TimedOut<T>(timeoutMs, abandoned);

    /// <summary>
    /// Consumes, once (as a source-backed ValueTask requires), the outcome of a client that completed after it lost.
    /// Returns the cancellation it threw, if any; whatever else it returned or threw is dropped.
    /// </summary>
    private static OperationCanceledException? Discard<T>(ValueTask<T> pending)
    {
        try
        {
            _ = pending.GetAwaiter().GetResult();
            return null;
        }
        catch (OperationCanceledException ex)
        {
            return ex;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CallResult<T> Cancelled<T>(AttemptBudget budget, OperationCanceledException? honoured, bool abandoned) =>
        new(CallEnd.Cancelled, default, honoured ?? new OperationCanceledException(budget.Caller), IoOutcome.Cancelled, null, abandoned);

    private static CallResult<T> RanOut<T>(AttemptBudget budget, OperationCanceledException? honoured, bool abandoned) =>
        new(CallEnd.BudgetRanOut, default, honoured ?? new OperationCanceledException("The node's budget ran out before the call completed.", budget.Token),
            IoOutcome.Timeout, null, abandoned);

    private static CallResult<T> TimedOut<T>(int? timeoutMs, bool abandoned) =>
        new(CallEnd.TimedOut, default, null, IoOutcome.Timeout, timeoutMs is { } timeout ? $"timed out after {timeout} ms" : "timed out", abandoned);

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
