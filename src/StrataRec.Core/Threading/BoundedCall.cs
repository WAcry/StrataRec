using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;

namespace StrataRec.Core.Threading;

/// <summary>
/// The bound the external calls of one scope share (design 4.8, 5.6): a node attempt's budget, a feature fetch's, a cache
/// read's. Its absolute deadline, the token handed to the calls (linked to the caller's token and cancelled at the
/// deadline), and the caller's token itself.
/// </summary>
/// <remarks>
/// <para>The deadline cancels the token as an <see cref="ObservedCancellationSource"/> does, so the clients' callbacks run
/// on the pool and what they throw is counted and reported, never thrown on the timer's thread. The bound is the source
/// itself rather than an object holding one: one object less per scope pays for the timer's wrapper.</para>
/// <para>Reference-counted: the scope holds one reference until it ends, and each external call one until its client
/// has really completed. The source is disposed with the last, so a client that ignores cancellation and finishes after
/// the scope ended still holds a live token, cancelled (or about to be) at the deadline.</para>
/// </remarks>
internal class CallBound : ObservedCancellationSource
{
    /// <summary>The longest a timer or a timed wait accepts (just under 49.7 days); longer waits are capped to it.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    /// <summary>The same, in milliseconds, for budgets computed as numbers.</summary>
    public const double MaxWaitMs = uint.MaxValue - 1.0;

    private int _references = 1;

    /// <param name="site">Whose token it is, for the summaries of its callbacks' failures.</param>
    /// <param name="caller">The caller's token; the bound's token is linked to it.</param>
    /// <param name="deadline">
    /// The absolute deadline as a <see cref="Stopwatch"/> timestamp; <see cref="long.MaxValue"/> for an untimed scope (a
    /// replay takes its timing decisions from the recording).
    /// </param>
    /// <param name="events">Where a failure of the token's callbacks is summarized, with <paramref name="requestId"/>.</param>
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
    /// The calls' token: cancelled at the deadline or with the caller's token. Read once, so it stays readable after the
    /// bound is disposed; the source's own property throws then.
    /// </summary>
    public new CancellationToken Token { get; }

    public CancellationToken Caller { get; }

    public long Deadline { get; }

    /// <summary>Time left before the deadline; zero once it has passed, <see cref="TimeSpan.MaxValue"/> when untimed.</summary>
    public TimeSpan Remaining => Deadline == long.MaxValue ? TimeSpan.MaxValue : Until(Deadline);

    /// <summary>
    /// The late-result rule: the bound has run out and the caller has not cancelled. The deadline counts as passed when
    /// the clock says so or when the bound's timer has cancelled the token (a timer may fire slightly before the clock
    /// reaches the deadline). Never true for an untimed scope.
    /// </summary>
    public bool RanOut => !Caller.IsCancellationRequested && Passed;

    /// <summary>The deadline has passed, by the clock or by the timer that enforces it (or the caller has cancelled the token).</summary>
    internal bool Passed => Token.IsCancellationRequested || Stopwatch.GetTimestamp() >= Deadline;

    /// <summary>
    /// The deadline <paramref name="budget"/> after <paramref name="start"/> (a <see cref="Stopwatch"/> timestamp); a
    /// budget longer than any timer accepts is none (<see cref="long.MaxValue"/>), so the sum cannot overflow into the
    /// past.
    /// </summary>
    public static long DeadlineAfter(long start, TimeSpan budget) =>
        budget >= MaxWait ? long.MaxValue : start + (long)(budget.TotalSeconds * Stopwatch.Frequency);

    /// <summary>A bound <paramref name="budgetMs"/> from now.</summary>
    public static CallBound After(CancellationSite site, CancellationToken caller, int budgetMs, RequestEventLog? events = null, string? requestId = null) =>
        new(site, caller, Stopwatch.GetTimestamp() + ((long)Math.Max(0, budgetMs) * Stopwatch.Frequency / 1000), events, requestId);

    /// <summary>An external call is about to be sent; released when its client has really completed.</summary>
    public void AddReference() => Interlocked.Increment(ref _references);

    /// <summary>The scope has ended, or a call's client has completed.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            Dispose();
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

/// <summary>
/// A call's own deadline within its <see cref="CallBound"/>: a service's <c>timeout_ms</c> from when it is sent, or a
/// distributed tier's time from when its read started (each chunk of the read within it, none with a fresh budget),
/// judged by the clock it was taken on (a cache's clock, which a test holds).
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

    /// <summary><paramref name="timeoutMs"/> from now, on the system clock; none when null.</summary>
    public static CallDeadline After(int? timeoutMs)
    {
        if (timeoutMs is not { } timeout)
        {
            return None;
        }

        var milliseconds = Math.Max(0, timeout);
        return new CallDeadline(TimeProvider.System, Stopwatch.GetTimestamp() + ((long)milliseconds * Stopwatch.Frequency / 1000), milliseconds);
    }

    /// <summary>The timestamp <paramref name="timestamp"/> of <paramref name="clock"/>, set as <paramref name="timeoutMs"/>.</summary>
    public static CallDeadline At(TimeProvider clock, long timestamp, int timeoutMs) => new(clock, timestamp, timeoutMs);

    /// <summary>Whether it has passed, by its clock. Never for none.</summary>
    public bool HasPassed => _clock is { } clock && clock.GetTimestamp() >= _timestamp;

    /// <summary>The time left by its clock: never negative, capped to what timers accept; the longest wait for none.</summary>
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

/// <summary>How one bounded external call ended, as the request saw it.</summary>
internal enum CallEnd : byte
{
    /// <summary>The client returned a value in time; <see cref="CallResult{T}.Outcome"/> says whether it is a success.</summary>
    Answered = 1,

    /// <summary>The client threw in time (anything but cancellation).</summary>
    Threw,

    /// <summary>
    /// The call's own deadline: the call was cut off at it (the wait ended, or the client completed only after it), or the
    /// client honoured it (or cancelled on its own).
    /// </summary>
    TimedOut,

    /// <summary>The bound ran out (the node's budget, the fetch's).</summary>
    BudgetRanOut,

    /// <summary>The caller cancelled the request.</summary>
    Cancelled,
}

/// <summary>The result of <see cref="BoundedCall.RunAsync{T, TCall}"/>.</summary>
/// <param name="Value">What the client returned; default unless <see cref="CallEnd.Answered"/>.</param>
/// <param name="Exception">
/// What the client threw (<see cref="CallEnd.Threw"/>); for <see cref="CallEnd.BudgetRanOut"/> and
/// <see cref="CallEnd.Cancelled"/>, the cancellation to throw to the operator: the client's own, or a new one when it was
/// abandoned or not sent.
/// </param>
/// <param name="Outcome">The call's outcome for metrics, health and recordings.</param>
/// <param name="Error">The error text: a failed answer's, a thrown exception's message, or the timeout's.</param>
/// <param name="Abandoned">The client was still running when the wait ended; it may still write into what it was handed.</param>
/// <param name="Sent">The client was called: false when the bound had passed, or the caller had cancelled, before it.</param>
internal readonly record struct CallResult<T>(CallEnd End, T? Value, Exception? Exception, IoOutcome Outcome, string? Error, bool Abandoned, bool Sent = true);

/// <summary>What one kind of call site gives <see cref="BoundedCall.RunAsync{T, TCall}"/>: a struct, so nothing is allocated for it.</summary>
internal interface IBoundedCall<T>
{
    /// <summary>Sends the call; <paramref name="token"/> is the bound's token, or one the call's own deadline also cancels.</summary>
    ValueTask<T> Send(CancellationToken token);

    /// <summary>What an answer is: a success, a partial answer (some keys failed) or a failure, with its error text.</summary>
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
    /// Called exactly once, when the call has ended, before its metrics are completed (so a host's listener there cannot
    /// change what the call site sees) and before its hold on the request memory is released: the place to close what
    /// the client writes into and to record the call. Only an <see cref="CallEnd.Answered"/> call's data may be read; an
    /// abandoned client may still be writing.
    /// </summary>
    void Ended(in CallResult<T> result, TimeSpan duration);
}

/// <summary>
/// The framework's one I/O boundary (design 4.8, 5.6, 13.1): an external call, bounded whether or not the client honours
/// cancellation. Service calls (<c>OperatorContext.CallAsync</c>), feature connector calls and distributed cache reads all
/// go through it; each call site only says how to send, judge and record its call.
/// </summary>
/// <remarks>
/// <para>Rules, each locked by tests: (1) the call's bound is the earlier of its own deadline and the scope's, or the
/// caller cancelling; the wait ends at it and never waits on a token the client holds (cancelling a token runs the
/// client's callbacks one after another, and one that blocks would hold the wait), and what the client returns, throws or
/// honours is heard only if the framework observes it completed within the bound (<see cref="Winner"/>): by a wait it set
/// up, or, when no wait was set up (the client answered before Send returned, or completed before the wait could be set
/// up), by the bound not having passed when it looks; never when the bound had won, even an instant before; (2) the call
/// holds the request memory once, from before it is sent until both the client has really completed and the call site has
/// recorded it, so an abandoned client writes only into live buffers and <c>stratarec.io.abandoned</c> counts it once;
/// (3) the tokens the client holds stay alive until it completes; (4) the outcome is <c>cancelled</c> when the caller
/// cancelled (neither timed nor counted against the dependency), <c>timeout</c> for a deadline or a client's
/// <see cref="TimeoutException"/>, <c>partial</c> or <c>error</c> as the call site judges an answer, and <c>error</c> for
/// anything else thrown; (5) the call is recorded and completed (metrics, health, span) exactly once, at the call site,
/// whatever the host's metric and activity listeners throw (they lose their measurement); (6) a client that answers
/// synchronously costs no lease, continuation or wait, and a call within its scope's bound allocates nothing at all; a
/// call with a deadline of its own before the scope's costs one linked token source and its timer (about 210 B), created
/// before it is sent so that the client's token carries the deadline; (7) a deadline cancels the client's token through
/// <see cref="ObservedCancellationSource"/>: the client's callbacks run on the pool, and what they throw is counted and
/// reported, never thrown on the timer's thread; (8) a call whose bound has passed before it is sent is not sent (an
/// earlier call that cancelled the request, a chunk sent after the fetch's time ran out): it ends cancelled or timed out,
/// is recorded as not sent, and is no I/O: no metric, health or span counts it, since the dependency never saw it. The
/// bound is looked at before the call starts and again after (the host's tracing runs as it starts); a call the host's
/// tracing cut off has a span, which ends cancelled.</para>
/// </remarks>
internal static class BoundedCall
{
    /// <summary>
    /// Sends one call and waits for it within its bound. A client that answers synchronously is recorded, completed and
    /// released here, with nothing allocated beyond the call's own token source if it has one (rule 6; this is not an
    /// async method: Debug builds compile async state machines as classes); only a pending one goes on to
    /// <see cref="AwaitAsync{T, TCall}"/>.
    /// </summary>
    /// <param name="call">The call site: sends the call, judges an answer, records the end.</param>
    /// <param name="bound">The scope's bound: its token and deadline, and the caller's token.</param>
    /// <param name="memory">
    /// The request memory, held for the call (rule 2); null for a call made before the request has any (the assignment
    /// provider's), which writes into nothing of the request.
    /// </param>
    /// <param name="deadline">The call's own deadline (a binding's <c>timeout_ms</c>, a tier's time); none for the bound's alone.</param>
    /// <remarks>The call reports to the bound's request (<see cref="ObservedCancellationSource.RequestId"/>, its events).</remarks>
    public static ValueTask<CallResult<T>> RunAsync<T, TCall>(TCall call, CallBound bound, RequestMemory? memory, IoTarget target, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        // Rule 8: a call whose bound has passed before it is sent (the caller cancelled, as an earlier call may have done,
        // or the time is up) is not sent: it ends as a call the bound or the caller won at once would, and is recorded.
        var unsent = Unsent(bound, deadline);
        if (unsent != Winner.Client)
        {
            var cut = CutOff<T>(unsent, null, ownSet: false, bound, deadline, abandoned: false) with { Sent = false };
            call.Ended(cut, TimeSpan.Zero);
            return new ValueTask<CallResult<T>>(cut);
        }

        // Starting the call runs the host's code (an ActivityListener's sampler and start callback), which may cancel the
        // request or take the time that was left: the bound is looked at again after it (review of IO step 4: a sampler
        // that cancelled the request still had the provider called). The span the host saw start ends cancelled, which
        // no metric or health counts. Which deadline comes first is decided before, while both are ahead: a call whose
        // two deadlines both passed in the host's code is the earlier one's, as a sent one is (review of IO step 4b).
        var ownFirst = deadline.IsSet && deadline.Remaining < bound.Remaining;
        var io = target.Start();
        unsent = Unsent(bound, deadline);
        if (unsent != Winner.Client)
        {
            var cut = CutOff<T>(unsent, null, ownFirst, bound, deadline, abandoned: false) with { Sent = false };
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
        var (own, token) = Own(bound, deadline, ownFirst);
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
            io.Sent(); // the caller's next call is not a child of this one
            var client = pending.AsTask();
            return AwaitAsync(call, client, io, bound, Lease.Until(client, memory, bound, own), own, token, deadline);
        }

        var result = Defect<T>();
        try
        {
            // Rule 1: the client completed before Send returned; the framework observes it now.
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

    /// <summary>Who has won before the call is sent (rule 8): the caller, the bound or the call's own deadline; <see cref="Winner.Client"/> while none has.</summary>
    private static Winner Unsent(CallBound bound, in CallDeadline deadline) =>
        bound.Caller.IsCancellationRequested ? Winner.Caller : bound.Passed || deadline.HasPassed ? Winner.Bound : Winner.Client;

    /// <summary>
    /// The end of a call whose bound has passed before it starts (rule 8), or null while it has not: for a call site that
    /// may answer without sending (a cache holding the answer), which must not answer when the call it stands for would
    /// not be sent. The cancellation is the one <see cref="RunAsync{T, TCall}"/> would throw to the operator.
    /// </summary>
    public static (OperationCanceledException Exception, IoOutcome Outcome)? CutOff(CallBound bound)
    {
        if (bound.Caller.IsCancellationRequested)
        {
            return ((OperationCanceledException)Cancelled<bool>(bound, null, abandoned: false).Exception!, IoOutcome.Cancelled);
        }

        return bound.Passed ? ((OperationCanceledException)RanOut<bool>(bound, null, abandoned: false).Exception!, IoOutcome.Timeout) : null;
    }

    /// <summary>
    /// The rest of a call whose client did not answer synchronously; <paramref name="lease"/> holds the memory and the
    /// bound already. Only what the decision after the wait needs lives across it: the state machine is allocated for
    /// every pending call, so the result, the memory and the client's token are not kept in it (IO step 4 measured
    /// about 170 B a call more when they were).
    /// </summary>
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

            // Rule 1: the wait has its own bound and ends with the client still running if need be.
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
            // its outcome says nothing about who came first. The client completed after Send returned pending and before
            // the wait (while its ValueTask became a task, say): the framework observes it now, as an answer that came
            // before Send returned. A wait that was set up and has ended already (with the client just after it) is
            // another task, whose outcome says who won (review of IO step 4: both being complete did not tell them apart).
            return EndObserved(call, client, io, bound, lease, own, token, deadline);
        }

        // A wait that was set up has an outcome saying who won; the client may complete between the wait's end and this
        // continuation, so the client's state cannot. (One that ended at once without the client found the bound passed
        // or the caller gone.)
        var ownSet = own is not null;
        await wait.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return EndWaited(call, client, wait, io, bound, lease, ownSet, deadline);
    }

    /// <summary>A pending client that completed before its wait was set up, observed now (rule 1), and the call ended.</summary>
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

    /// <summary>A pending client's call decided by its wait (rule 1), and ended.</summary>
    private static CallResult<T> EndWaited<T, TCall>(
        TCall call, Task<T> client, Task wait, in IoCall io, CallBound bound, Lease lease, bool ownSet, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        var result = Defect<T>();
        try
        {
            var winner = Waited(wait, client, bound.Caller);
            return result = winner == Winner.Client
                ? Completed(call, new ValueTask<T>(client), bound, deadline)
                : CutOff<T>(winner, null, ownSet, bound, deadline, abandoned: true);
        }
        finally
        {
            EndPending(call, io, result, bound, lease);
        }
    }

    /// <summary>Ends a pending call, then lets its lease give the memory back once the client has completed too (rule 2).</summary>
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
    /// Rule 5: has the call site record the call, then completes its metrics, health and span, once. Completing never
    /// throws (<see cref="IoCall.Complete"/>): a host's metric listener that threw there left a call that had answered
    /// unrecorded, outstanding in a recording, and threw out of the node in place of its result (review 80). The call site
    /// goes first, so a listener that takes time (or moves a cache's clock) cannot change what it sees (review 80).
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

    /// <summary>The result recorded if the framework itself throws before the call is classified.</summary>
    private static CallResult<T> Defect<T>() => new(CallEnd.Threw, default, null, IoOutcome.Error, null, false);

    /// <summary>
    /// The token the client gets. The call's own deadline gets a token source of its own only when it comes before the
    /// bound's (<paramref name="ownFirst"/>, decided before the call started); otherwise the bound's token already says
    /// everything.
    /// </summary>
    private static (CancellationTokenSource? Own, CancellationToken Token) Own(CallBound bound, in CallDeadline deadline, bool ownFirst)
    {
        if (ownFirst)
        {
            var own = new ObservedCancellationSource(CancellationSite.Call, bound.Token, bound.Events, bound.RequestId);
            own.CancelAfterObserved(deadline.Remaining);
            return (own, own.Token);
        }

        return (null, bound.Token);
    }

    /// <summary>How long the wait for a pending client may last: until the bound's deadline, or the call's own when it comes first.</summary>
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

    /// <summary>
    /// Who ended a call (rule 1): the client, by completing within the call's bound (with an answer, a failure or a
    /// cancellation of its own), or the bound or the caller before it. Decided once, from how the call ended, and never
    /// re-derived from the client's state afterwards: a client that completes an instant after it lost stays lost.
    /// </summary>
    private enum Winner : byte
    {
        Client,

        /// <summary>The call's deadline: its own, or the bound's when it has none earlier.</summary>
        Bound,

        Caller,
    }

    /// <summary>
    /// Rule 1 for a completed client that no wait bounded: one that answered before Send returned, or one that completed
    /// before its wait was set up. The framework observes it now, so it is heard only if its bound has not passed now: the
    /// caller has not cancelled, neither deadline is reached by its clock, and no timer that enforces one has cancelled the
    /// client's token (a timer may fire slightly before the clock reaches the deadline, as for
    /// <see cref="CallBound.RanOut"/>). A client that completed in time but is observed late, because the thread was
    /// preempted, has timed out all the same: the framework can only stand behind what it observed.
    /// </summary>
    /// <param name="completed">The client's outcome; must have completed.</param>
    /// <param name="token">The token the client got, read before it was sent (its source may be disposed by now).</param>
    private static CallResult<T> Observed<T, TCall>(
        TCall call, ValueTask<T> completed, CallBound bound, CancellationTokenSource? own, CancellationToken token, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        var winner = bound.Caller.IsCancellationRequested ? Winner.Caller
            : token.IsCancellationRequested || bound.Passed || deadline.HasPassed ? Winner.Bound
            : Winner.Client;
        return winner == Winner.Client
            ? Completed(call, completed, bound, deadline)
            : CutOff<T>(winner, Discard(completed), own is not null, bound, deadline, abandoned: false);
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
    private static CallResult<T> Completed<T, TCall>(TCall call, ValueTask<T> pending, CallBound bound, in CallDeadline deadline)
        where TCall : struct, IBoundedCall<T>
    {
        try
        {
            var value = pending.GetAwaiter().GetResult();
            var outcome = call.Judge(value, out var error);
            return new CallResult<T>(CallEnd.Answered, value, null, outcome, outcome == IoOutcome.Ok ? null : error, false);
        }
        catch (OperationCanceledException ex)
        {
            // It honoured a token (or cancelled on its own) in time: the tokens say whose, the caller's first.
            return bound.Caller.IsCancellationRequested ? Cancelled<T>(bound, ex, abandoned: false)
                : bound.Token.IsCancellationRequested ? RanOut<T>(bound, ex, abandoned: false)
                : TimedOut<T>(deadline, abandoned: false);
        }
        catch (Exception ex)
        {
            // A client's own transport timeout is a timeout. The error names the type: the operator gets a failed status,
            // not the exception, and the type is often what says what went wrong.
            return new CallResult<T>(CallEnd.Threw, default, ex, ex is TimeoutException ? IoOutcome.Timeout : IoOutcome.Error,
                $"threw {ex.GetType().Name}: {ExceptionText.Of(ex)}", false);
        }
    }

    /// <summary>
    /// A call the bound or the caller won (rule 1): nothing the client returned or threw is used. The deadline that came
    /// first ended it: the call's own when it was the earlier when the call was sent (it had a token source of its own),
    /// or when it has passed by its clock while the scope's bound has not (a cache's clock is not the bound's); otherwise
    /// the scope's bound ran out (even if its timer has not fired yet). A client that returns only after both have passed
    /// is cut off by the earlier, not by whichever is checked first (review of IO step 4).
    /// </summary>
    /// <param name="honoured">A cancellation the client threw after it lost, thrown on to the operator as its own.</param>
    /// <param name="ownSet">The call had a token source of its own for its deadline.</param>
    /// <param name="abandoned">The client was still running when the call ended.</param>
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
    /// What a call whose client did not answer synchronously holds: the hold on the request memory taken before it was
    /// sent, a reference to the bound, and its own token source. The memory is released when both the client has really
    /// completed and the call site has recorded the call, in either order (rule 2); the token sources when the client has
    /// completed (rule 3). Its task completes just after the memory is released and never faults; continuations run
    /// asynchronously (only a request's I/O drain awaits it, and none runs on a client's or a call site's thread).
    /// </summary>
    private sealed class Lease(RequestMemory? memory, CallBound bound, CancellationTokenSource? own) : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        private int _parts = 2;

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

        /// <summary>The call site has recorded the call.</summary>
        public void CallSiteDone() => Release();

        /// <summary>The client's task has completed: it writes no more and no longer needs its token.</summary>
        private void ClientDone()
        {
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
