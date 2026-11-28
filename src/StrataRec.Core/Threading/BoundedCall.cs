using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;

namespace StrataRec.Core.Threading;

/// <summary>
/// The bound the external calls of one scope share: a node attempt's budget, a feature fetch's, a cache read's.
/// </summary>
/// <remarks>
/// <para>Reference-counted: the scope holds one reference until it ends, and each external call one until its client
/// has really completed. The source is disposed with the last, so a client that ignores cancellation and finishes after
/// the scope ended still holds a live token, cancelled (or about to be) at the deadline.</para>
/// </remarks>
internal class CallBound : ObservedCancellationSource
{
    /// <summary>The longest a timer or a timed wait accepts (just under 49.7 days); longer waits are capped to it.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    public const double MaxWaitMs = uint.MaxValue - 1.0;

    private int _references = 1;

    /// <param name="deadline">The absolute deadline as a <see cref="Stopwatch"/> timestamp; <see cref="long.MaxValue"/>
    /// for an untimed scope.</param>
    public CallBound(CancellationSite site, CancellationToken caller, long deadline, RequestEventLog? events = null, string? requestId = null)
        : base(site, caller, events, requestId)
    {
        Caller = caller;
        Deadline = deadline;
        if (deadline != long.MaxValue)
        {
            CancelAfterObserved(Until(deadline));
        }

        Token = base.Token;
    }

    /// <summary>
    /// Read once, so it stays readable after the bound is disposed; the source's own property throws then.
    /// </summary>
    public new CancellationToken Token { get; }

    public CancellationToken Caller { get; }

    public long Deadline { get; }

    public TimeSpan Remaining => Deadline == long.MaxValue ? TimeSpan.MaxValue : Until(Deadline);

    /// <summary>
    /// The deadline counts as passed when the clock says so or when the bound's timer has cancelled the token (a timer
    /// may fire slightly before the clock reaches the deadline).
    /// </summary>
    public bool RanOut => !Caller.IsCancellationRequested && Passed;

    internal bool Passed => Token.IsCancellationRequested || Stopwatch.GetTimestamp() >= Deadline;

    /// <summary>
    /// The deadline <paramref name="budget"/> after <paramref name="start"/> (a <see cref="Stopwatch"/> timestamp); a
    /// budget longer than any timer accepts is none (<see cref="long.MaxValue"/>), so the sum cannot overflow into the
    /// past.
    /// </summary>
    public static long DeadlineAfter(long start, TimeSpan budget) =>
        budget >= MaxWait ? long.MaxValue : start + (long)(budget.TotalSeconds * Stopwatch.Frequency);

    public static CallBound After(CancellationSite site, CancellationToken caller, int budgetMs, RequestEventLog? events = null, string? requestId = null) =>
        new(site, caller, Stopwatch.GetTimestamp() + ((long)Math.Max(0, budgetMs) * Stopwatch.Frequency / 1000), events, requestId);

    public void AddReference() => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            Dispose();
        }
    }

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

/// <summary>
/// A call's own deadline within its <see cref="CallBound"/>: a service's <c>timeout_ms</c> from when it is sent, or a
/// distributed tier's time from when its read started (each chunk of the read within it, none with a fresh budget),
/// judged by the clock it was taken on (a cache's clock).
/// </summary>
internal readonly struct CallDeadline
{
    private readonly TimeProvider? _clock;
    private readonly long _timestamp;

    private CallDeadline(TimeProvider clock, long timestamp, int timeoutMs)
    {
        _clock = clock;
        _timestamp = timestamp;
        TimeoutMs = timeoutMs;
    }

    /// <summary>No deadline of its own: the bound's.</summary>
    public static CallDeadline None => default;

    public bool IsSet => _clock is not null;

    /// <summary>The time the deadline was set as, for the error text.</summary>
    public int TimeoutMs { get; }

    public static CallDeadline After(int? timeoutMs)
    {
        if (timeoutMs is not { } timeout)
        {
            return None;
        }

        var milliseconds = Math.Max(0, timeout);
        return new CallDeadline(TimeProvider.System, Stopwatch.GetTimestamp() + ((long)milliseconds * Stopwatch.Frequency / 1000), milliseconds);
    }

    public static CallDeadline At(TimeProvider clock, long timestamp, int timeoutMs) => new(clock, timestamp, timeoutMs);

    public bool HasPassed => _clock is { } clock && clock.GetTimestamp() >= _timestamp;

    public TimeSpan Remaining
    {
        get
        {
            if (_clock is not { } clock)
            {
                return CallBound.MaxWait;
            }

            var remaining = clock.GetElapsedTime(clock.GetTimestamp(), _timestamp);
            return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < CallBound.MaxWait ? remaining : CallBound.MaxWait;
        }
    }
}

internal enum CallEnd : byte
{
    /// <summary>The client returned a value in time; <see cref="CallResult{T}.Outcome"/> says whether it is a success.</summary>
    Answered = 1,

    Threw,

    /// <summary>
    /// The call's own deadline: the call was cut off at it (the wait ended, or the client completed only after it), or
    /// the client honoured it. A client that cancels on its own, with none of the framework's tokens cancelled, has
    /// <see cref="Threw"/>.
    /// </summary>
    TimedOut,

    BudgetRanOut,

    Cancelled,
}

/// <param name="Abandoned">The client was still running when the wait ended; it may still write into what it was handed.</param>
internal readonly record struct CallResult<T>(CallEnd End, T? Value, Exception? Exception, IoOutcome Outcome, string? Error, bool Abandoned, bool Sent = true);

internal interface IBoundedCall<T>
{
    /// <summary>Sends the call; <paramref name="token"/> is the bound's token, or one the call's own deadline also cancels.</summary>
    ValueTask<T> Send(CancellationToken token);

    IoOutcome Judge(T value, out string? error);

    /// <summary>The items or keys the call is about, for <c>stratarec.io.keys</c>; -1 for a call about none.</summary>
    int Keys { get; }

    /// <summary>
    /// The call did not complete when it was sent: <paramref name="done"/> completes once the client has really completed
    /// and the call site is done with it (the request memory released). A request that drains its I/O tracks it; it
    /// never faults.
    /// </summary>
    void Pending(Task done);

    /// <summary>
    /// Called exactly once, when the call has ended, before its metrics are completed and before its hold on the
    /// request memory is released. Only an <see cref="CallEnd.Answered"/> call's data may be read; an abandoned client
    /// may still be writing.
    /// </summary>
    void Ended(in CallResult<T> result, TimeSpan duration);
}

internal static class BoundedCall
{
    public static ValueTask<CallResult<T>> RunAsync<T, TCall>(TCall call, CallBound bound, RequestMemory? memory, IoTarget target, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        var unsent = Unsent(bound, deadline);
        if (unsent != Winner.Client)
        {
            var cut = CutOff<T>(unsent, null, ownSet: false, bound, deadline, abandoned: false) with { Sent = false };
            call.Ended(cut, TimeSpan.Zero);
            return new ValueTask<CallResult<T>>(cut);
        }

        // Starting the call runs the host's code (an ActivityListener's sampler and start callback), which may cancel
        // the request or take the time that was left.
        var ownFirst = deadline.IsSet && deadline.Remaining < bound.Remaining;
        var io = target.Start();
        unsent = Unsent(bound, deadline);
        if (unsent != Winner.Client)
        {
            var cut = CutOff<T>(unsent, null, ownSet: ownFirst && deadline.HasPassed, bound, deadline, abandoned: false) with { Sent = false };
            try
            {
                call.Ended(cut, TimeSpan.Zero);
            }
            finally
            {
                io.Complete(IoOutcome.Cancelled);
            }

            return new ValueTask<CallResult<T>>(cut);
        }

        memory?.Hold();
        bound.AddReference();
        var (own, token) = Own(bound, deadline);
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
            io.Sent();
            var client = pending.AsTask();
            return AwaitAsync(call, client, io, bound, Lease.Until(client, memory, bound, own), own, token, deadline);
        }

        var result = Defect<T>();
        try
        {
            result = Observed(call, pending, bound, own, token, deadline);
        }
        finally
        {
            try
            {
                End(call, io, result, bound);
            }
            finally
            {
                own?.Dispose();
                bound.Release();
                memory?.Release();
            }
        }

        return new ValueTask<CallResult<T>>(result);
    }

    private static Winner Unsent(CallBound bound, in CallDeadline deadline) =>
        bound.Caller.IsCancellationRequested ? Winner.Caller : bound.Passed || deadline.HasPassed ? Winner.Bound : Winner.Client;

    /// <summary>
    /// The end of a call whose bound has passed before it starts, or null while it has not: for a call site that may
    /// answer without sending (a cache holding the answer), which must not answer when the call it stands for would not
    /// be sent.
    /// </summary>
    public static (OperationCanceledException Exception, IoOutcome Outcome)? CutOff(CallBound bound)
    {
        if (bound.Caller.IsCancellationRequested)
        {
            return ((OperationCanceledException)Cancelled<bool>(bound, null, abandoned: false).Exception!, IoOutcome.Cancelled);
        }

        return bound.Passed ? ((OperationCanceledException)RanOut<bool>(bound, null, abandoned: false).Exception!, IoOutcome.Timeout) : null;
    }

    /// <param name="token">
    /// The client's token, read before the call was sent: the lease disposes <paramref name="own"/> as soon as the client
    /// has completed, which may be before the wait is even set up, and a disposed source's token can no longer be read.
    /// </param>
    private static async ValueTask<CallResult<T>> AwaitAsync<T, TCall>(
        TCall call, Task<T> client, IoCall io, CallBound bound, Lease lease, CancellationTokenSource? own, CancellationToken token, CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        Task wait;
        try
        {
            call.Pending(lease.Task);

            wait = ((Task)client).WaitAsync(Earlier(bound, own is not null, deadline), bound.Caller);
        }
        catch
        {
            EndPending(call, io, Defect<T>(), bound, lease);
            throw;
        }

        if (ReferenceEquals(wait, client) && client.IsCompleted)
        {
            // No wait was set up: WaitAsync returns a completed client itself, whatever the time left or the caller, so
            // its outcome says nothing about who came first.
            return EndObserved(call, client, io, bound, lease, own, token, deadline);
        }

        // A wait that was set up has an outcome saying who won; the client may complete between the wait's end and this
        // continuation, so the client's state cannot.
        var ownSet = own is not null;
        await wait.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return EndWaited(call, client, wait, io, bound, lease, ownSet, deadline);
    }

    private static CallResult<T> EndObserved<T, TCall>(
        TCall call, Task<T> client, in IoCall io, CallBound bound, Lease lease, CancellationTokenSource? own, CancellationToken token, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        var result = Defect<T>();
        try
        {
            return result = Observed(call, new ValueTask<T>(client), bound, own, token, deadline);
        }
        finally
        {
            EndPending(call, io, result, bound, lease);
        }
    }

    private static CallResult<T> EndWaited<T, TCall>(
        TCall call, Task<T> client, Task wait, in IoCall io, CallBound bound, Lease lease, bool ownSet, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        var result = Defect<T>();
        try
        {
            var winner = Waited(wait, client, bound.Caller);
            return result = winner == Winner.Client
                ? Completed(call, new ValueTask<T>(client), bound, lease.CancelledWhenDone(), deadline)
                : CutOff<T>(winner, null, ownSet, bound, deadline, abandoned: true);
        }
        finally
        {
            EndPending(call, io, result, bound, lease);
        }
    }

    private static void EndPending<T, TCall>(TCall call, in IoCall io, in CallResult<T> result, CallBound bound, Lease lease)
        where TCall : struct, IBoundedCall<T>
    {
        try
        {
            End(call, io, result, bound);
        }
        finally
        {
            lease.CallSiteDone();
        }
    }

    /// <summary>
    /// The call site goes first, so a listener that takes time (or moves a cache's clock) cannot change what it sees.
    /// </summary>
    private static void End<T, TCall>(TCall call, in IoCall io, in CallResult<T> result, CallBound bound)
        where TCall : struct, IBoundedCall<T>
    {
        try
        {
            call.Ended(result, io.Elapsed);
        }
        finally
        {
            io.Complete(result.Outcome, call.Keys, bound.RequestId, result.Error, bound.Events);
        }
    }

    private static CallResult<T> Defect<T>() => new(CallEnd.Threw, default, null, IoOutcome.Error, null, false);

    private static (CancellationTokenSource? Own, CancellationToken Token) Own(CallBound bound, in CallDeadline deadline)
    {
        if (deadline.IsSet)
        {
            var remaining = deadline.Remaining;
            if (remaining < bound.Remaining)
            {
                var own = new ObservedCancellationSource(CancellationSite.Call, bound.Token, bound.Events, bound.RequestId);
                own.CancelAfterObserved(remaining);
                return (own, own.Token);
            }
        }

        return (null, bound.Token);
    }

    private static TimeSpan Earlier(CallBound bound, bool ownSet, in CallDeadline deadline)
    {
        var bounded = CallBound.Until(bound.Deadline);
        if (ownSet)
        {
            var own = deadline.Remaining;
            return own < bounded ? own : bounded;
        }

        return bounded;
    }

    private enum CancelledBy : byte
    {
        None,

        Caller,

        Budget,

        Own,
    }

    private static CancelledBy CancelledNow(CallBound bound, CancellationTokenSource? own) =>
        bound.Caller.IsCancellationRequested ? CancelledBy.Caller
        : bound.Token.IsCancellationRequested ? CancelledBy.Budget
        : own is { IsCancellationRequested: true } ? CancelledBy.Own
        : CancelledBy.None;

    private enum Winner : byte
    {
        Client,

        /// <summary>The call's deadline: its own, or the bound's when it has none earlier.</summary>
        Bound,

        Caller,
    }

    /// <summary>
    /// A client that completed in time but is observed late, because the thread was preempted, has timed out all the
    /// same: the framework can only stand behind what it observed.
    /// </summary>
    private static CallResult<T> Observed<T, TCall>(
        TCall call, ValueTask<T> completed, CallBound bound, CancellationTokenSource? own, CancellationToken token, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        var winner = bound.Caller.IsCancellationRequested ? Winner.Caller
            : token.IsCancellationRequested || bound.Passed || deadline.HasPassed ? Winner.Bound
            : Winner.Client;
        return winner == Winner.Client
            ? Completed(call, completed, bound, CancelledBy.None, deadline)
            : CutOff<T>(winner, Discard(completed), own is not null, bound, deadline, abandoned: false);
    }

    /// <summary>
    /// The wait's timeout faults it with a <see cref="TimeoutException"/> of its own, while a client's failure is
    /// passed on as the very same exception, so a client that throws a <see cref="TimeoutException"/> itself is told
    /// apart by identity.
    /// </summary>
    private static Winner Waited(Task wait, Task client, CancellationToken caller) => wait.Status switch
    {
        TaskStatus.RanToCompletion => Winner.Client,
        TaskStatus.Faulted => IsClientFailure(wait.Exception!, client) ? Winner.Client : Winner.Bound,
        // Only a caller that has cancelled can have ended the wait: a client's cancellation with no token, or with any
        // token while the caller passed none, is not the caller's because the tokens are equal.
        _ => client.IsCanceled && !(caller.IsCancellationRequested && CancelledWith(wait, caller)) ? Winner.Client : Winner.Caller,
    };

    /// <summary>
    /// Whether a cancelled task was cancelled with <paramref name="token"/>; read from the cancellation it throws, which
    /// is thrown only for a wait and a client both cancelled, with the caller's token cancelled (a cancelled task has no
    /// other public way to say).
    /// </summary>
    private static bool CancelledWith(Task cancelled, CancellationToken token)
    {
        try
        {
            cancelled.GetAwaiter().GetResult();
            return false;
        }
        catch (OperationCanceledException ex)
        {
            return ex.CancellationToken == token;
        }
    }

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

    private static CallResult<T> Completed<T, TCall>(TCall call, ValueTask<T> pending, CallBound bound, CancelledBy cancelledBy, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        try
        {
            var value = pending.GetAwaiter().GetResult();
            var outcome = call.Judge(value, out var error);
            return new CallResult<T>(CallEnd.Answered, value, null, outcome, outcome == IoOutcome.Ok ? null : error, false);
        }
        catch (Exception ex)
        {
            // A cancellation past the deadline by its clock is the deadline's, as one honoured an instant before the timer fires.
            var cancellation = ex as OperationCanceledException;
            return cancelledBy == CancelledBy.Caller ? Cancelled<T>(bound, cancellation, abandoned: false)
                : cancelledBy == CancelledBy.Budget ? RanOut<T>(bound, cancellation, abandoned: false)
                : cancelledBy == CancelledBy.Own || (cancellation is not null && deadline.HasPassed) ? TimedOut<T>(deadline, abandoned: false)
                : cancellation is not null
                ? new CallResult<T>(CallEnd.Threw, default, ex, IoOutcome.Timeout, $"threw {ex.GetType().Name}: it cancelled the call itself, before its deadline", false)
                : new CallResult<T>(CallEnd.Threw, default, ex, ex is TimeoutException ? IoOutcome.Timeout : IoOutcome.Error,
                    $"threw {ex.GetType().Name}: {ExceptionText.Of(ex)}", false);
        }
    }

    /// <summary>
    /// A client that returns only after both have passed is cut off by the earlier, not by whichever is checked first.
    /// </summary>
    private static CallResult<T> CutOff<T>(
        Winner winner, OperationCanceledException? honoured, bool ownSet, CallBound bound, in CallDeadline deadline, bool abandoned)
    {
        if (winner == Winner.Caller)
        {
            return Cancelled<T>(bound, honoured, abandoned);
        }

        var own = deadline.IsSet && (ownSet || (deadline.HasPassed && !bound.Passed));
        return own ? TimedOut<T>(deadline, abandoned) : RanOut<T>(bound, honoured, abandoned);
    }

    /// <summary>
    /// Consumes, once (as a source-backed ValueTask requires), the outcome of a client that completed after it lost.
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

    private static CallResult<T> Cancelled<T>(CallBound bound, OperationCanceledException? honoured, bool abandoned) =>
        new(CallEnd.Cancelled, default, honoured ?? new OperationCanceledException(bound.Caller), IoOutcome.Cancelled, null, abandoned);

    private static CallResult<T> RanOut<T>(CallBound bound, OperationCanceledException? honoured, bool abandoned) =>
        new(CallEnd.BudgetRanOut, default, honoured ?? new OperationCanceledException("The budget ran out before the call completed.", bound.Token),
            IoOutcome.Timeout, BudgetRanOut, abandoned);

    /// <summary>The error of a call its scope's bound cut off: health and the request events name a reason for every timeout.</summary>
    private const string BudgetRanOut = "timed out: the time budget ran out";

    private static CallResult<T> TimedOut<T>(in CallDeadline deadline, bool abandoned) =>
        new(CallEnd.TimedOut, default, null, IoOutcome.Timeout, deadline.IsSet ? $"timed out after {deadline.TimeoutMs} ms" : "timed out", abandoned);

    /// <summary>
    /// The memory is released when both the client has really completed and the call site has recorded the call, in
    /// either order; the token sources when the client has completed. Its task completes just after the memory is
    /// released and never faults.
    /// </summary>
    private sealed class Lease(RequestMemory? memory, CallBound bound, CancellationTokenSource? own) : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        private int _parts = 2;
        private int _cancelledBy = -1; // CancelledBy when the client completed; -1 until it has

        public static Lease Until(Task client, RequestMemory? memory, CallBound bound, CancellationTokenSource? own)
        {
            var lease = new Lease(memory, bound, own);
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

        public void CallSiteDone() => Release();

        /// <summary>
        /// Which of the framework's tokens had been cancelled when the client completed, for a call its client ended: read
        /// as it completed, before its wait is told (the continuation is registered first, and runs synchronously when the
        /// client completes inline); one classified before that (a client whose continuations run apart) reads them now, an
        /// instant after the client completed.
        /// </summary>
        public CancelledBy CancelledWhenDone()
        {
            var read = Volatile.Read(ref _cancelledBy);
            return read >= 0 ? (CancelledBy)read : CancelledNow(bound, own);
        }

        private void ClientDone()
        {
            Volatile.Write(ref _cancelledBy, (int)CancelledNow(bound, own));
            own?.Dispose();
            bound.Release();
            Release();
        }

        private void Release()
        {
            if (Interlocked.Decrement(ref _parts) == 0)
            {
                try
                {
                    memory?.Release();
                }
                finally
                {
                    SetResult();
                }
            }
        }
    }
}
