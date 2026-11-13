using System.Diagnostics;
using System.Threading.Tasks.Sources;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Sources;
using StrataRec.UnitTests.Features;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// The rules of the framework's I/O boundary for candidate sources and models (<see cref="BoundedCall"/>), checked on the
/// helper itself so that neither call site can break them unnoticed: the wait has its own bound, the memory and the
/// client's tokens outlive an abandoned client, the outcome is classified one way, and each call ends exactly once.
/// </summary>
/// <remarks>
/// Clients that ignore cancellation answer on dedicated threads once the test opens their gate. Whether the helper waited
/// for one is therefore not a question of timing: it returned while the gate was closed, or the test fails after
/// <see cref="Patience"/> (which is what an unbounded wait does here).
/// </remarks>
public sealed class BoundedCallTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static readonly IoTarget Target = new(IoKind.Model, "m");

    [Fact]
    public async Task AClientIgnoringItsToken_IsCutOffAtTheNodeDeadline_AndHoldsTheMemoryAndItsTokenUntilItCompletes()
    {
        using var gate = new ManualResetEventSlim();
        var pool = new PoisoningPool();
        var memory = new RequestMemory(pool);
        var budget = new AttemptBudget(CancellationToken.None, Deadline(50));
        var site = new Site(memory);
        var client = default(CancellationToken);

        var result = await Run(site.With(token =>
        {
            client = token;
            return OnThread(gate, () => 1);
        }), budget, memory, timeoutMs: null).AsTask().WaitAsync(Patience);

        Assert.Equal((CallEnd.BudgetRanOut, IoOutcome.Timeout, true, null), (result.End, result.Outcome, result.Abandoned, result.Error));
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        var ended = Assert.Single(site.Recorded);
        Assert.True(ended.MemoryLive); // recorded while the call held the memory
        Assert.Equal(budget.Token, client); // no timeout of its own: the client got the node token

        budget.Release(); // the attempt ends
        Assert.Equal(1, memory.Release()); // the request returns: the client still holds the memory, once
        Assert.Equal(0, pool.Returned);
        Assert.True(SpinWait.SpinUntil(() => client.IsCancellationRequested, Patience)); // cancelled at the budget...
        _ = client.WaitHandle; // ...and still alive: the source is not disposed while the client runs

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
        Assert.Throws<ObjectDisposedException>(() => client.WaitHandle); // disposed once the client completed
        Assert.Single(site.Recorded); // nothing is recorded when the abandoned client answers
    }

    [Fact]
    public async Task TheCallsOwnTimeout_EndsTheWaitBeforeTheNodeDeadline_WithATokenOfItsOwn()
    {
        using var gate = new ManualResetEventSlim();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);
        var client = default(CancellationToken);

        var result = await Run(site.With(token =>
        {
            client = token;
            return OnThread(gate, () => 1);
        }), budget, memory, timeoutMs: 50).AsTask().WaitAsync(Patience);

        Assert.Equal((CallEnd.TimedOut, IoOutcome.Timeout, true, "timed out after 50 ms"), (result.End, result.Outcome, result.Abandoned, result.Error));
        Assert.Null(result.Exception);
        Assert.NotEqual(budget.Token, client);
        Assert.True(SpinWait.SpinUntil(() => client.IsCancellationRequested, Patience));
        Assert.False(budget.Token.IsCancellationRequested);

        budget.Release();
        Assert.Equal(1, memory.Release());
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
        Assert.Throws<ObjectDisposedException>(() => client.WaitHandle);
    }

    [Fact]
    public async Task ATimeoutAfterTheNodeDeadline_GetsNoTokenOfItsOwn()
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);
        var client = default(CancellationToken);

        await Run(site.With(token =>
        {
            client = token;
            return ValueTask.FromResult(1);
        }), budget, memory, timeoutMs: 1_200_000);

        Assert.Equal(budget.Token, client);
    }

    [Fact]
    public async Task CallerCancellation_EndsTheWait_AsCancelled_NotAsATimeout()
    {
        using var gate = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new RequestMemory();
        var budget = new AttemptBudget(caller.Token, Deadline(600_000));
        var site = new Site(memory);

        var running = Run(site.With(_ =>
        {
            started.SetResult();
            return OnThread(gate, () => 1);
        }), budget, memory, timeoutMs: 300_000).AsTask();
        await started.Task.WaitAsync(Patience);
        caller.Cancel();
        var result = await running.WaitAsync(Patience);

        Assert.Equal((CallEnd.Cancelled, IoOutcome.Cancelled, true), (result.End, result.Outcome, result.Abandoned));
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(result.Exception).CancellationToken);

        budget.Release();
        Assert.Equal(1, memory.Release());
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    [Theory]
    [InlineData("own")]
    [InlineData("budget")]
    public async Task AClientHonouringItsToken_EndsTheWayTheTokenSays(string which)
    {
        // The client blocks until its token is cancelled and then throws, synchronously: the call completed in time,
        // and the tokens alone tell why.
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(which == "own" ? 600_000 : 50));
        var site = new Site(memory);

        var result = await Run(site.With(token =>
        {
            Assert.True(token.WaitHandle.WaitOne(Patience));
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(1);
        }), budget, memory, timeoutMs: which == "own" ? 50 : null);

        Assert.Equal((which == "own" ? CallEnd.TimedOut : CallEnd.BudgetRanOut, IoOutcome.Timeout, false), (result.End, result.Outcome, result.Abandoned));
        Assert.Equal(which == "own" ? "timed out after 50 ms" : null, result.Error);
        Assert.Equal(which == "own", result.Exception is null); // the budget's cancellation is thrown on to the operator
    }

    [Theory]
    [InlineData("answers")]
    [InlineData("throws a timeout of its own")]
    public async Task AClientCompletingAfterItsTimeoutWon_ButBeforeTheCallIsClassified_HasTimedOut(string completion)
    {
        // The wait ends at the call's timeout; its continuation is held until the client has completed. The call has
        // timed out all the same: the winner is the wait's outcome, not the client's state when the call is classified.
        using var gate = new ResumeGate();
        var client = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);

        var running = Task.Run(async () =>
        {
            gate.Mark();
            return await Run(site.With(_ => new ValueTask<int>(client.Task)), budget, memory, timeoutMs: 200);
        });
        Assert.True(gate.WaitHeld(), "the wait never resumed on a thread of its own");
        Assert.False(running.IsCompleted);
        if (completion == "answers")
        {
            client.SetResult(31);
        }
        else
        {
            client.SetException(new TimeoutException("transport")); // not to be mistaken for the wait's own timeout
        }

        gate.Release();
        var result = await running.WaitAsync(Patience);

        Assert.Equal((CallEnd.TimedOut, IoOutcome.Timeout, 0, "timed out after 200 ms", true), (result.End, result.Outcome, result.Value, result.Error, result.Abandoned));
        Assert.Null(result.Exception);
        Assert.Equal(CallEnd.TimedOut, Assert.Single(site.Recorded).Result.End);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    [Fact]
    public async Task AClientCompletingAfterTheCallerCancelled_ButBeforeTheCallIsClassified_IsCancelled()
    {
        using var gate = new ResumeGate();
        using var caller = new CancellationTokenSource();
        using var waiting = new ManualResetEventSlim();
        var client = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new RequestMemory();
        var budget = new AttemptBudget(caller.Token, Deadline(600_000));
        var site = new Site(memory);

        var running = Task.Run(async () =>
        {
            gate.Mark();
            var pending = Run(site.With(_ => new ValueTask<int>(client.Task)), budget, memory, timeoutMs: null);
            waiting.Set(); // the pending call has started its wait
            return await pending;
        });
        Assert.True(waiting.Wait(Patience));
        // Cancelled from a thread of its own: the wait's continuation may run inside Cancel, and the gate holds it there.
        var cancelling = Task.Run(caller.Cancel);
        Assert.True(gate.WaitHeld(), "the wait never resumed on a thread of its own");
        client.SetResult(31);
        gate.Release();
        var result = await running.WaitAsync(Patience);
        await cancelling.WaitAsync(Patience);

        Assert.Equal((CallEnd.Cancelled, IoOutcome.Cancelled, 0, true), (result.End, result.Outcome, result.Value, result.Abandoned));
        Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(result.Exception).CancellationToken);
        Assert.Equal(CallEnd.Cancelled, Assert.Single(site.Recorded).Result.End);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    [Theory]
    [InlineData("in time")]
    [InlineData("after its own timeout")]
    [InlineData("after the caller cancelled")]
    public async Task AClientCompletingWhileItsValueTaskBecomesATask_IsHeardOnlyIfItsBoundHasNotPassed(string when)
    {
        // Send returns pending; the client completes while the framework turns its ValueTask into a task, before any wait
        // is set up. WaitAsync then returns the completed client as it is, whatever the time left or the caller, so its
        // outcome proves nothing: the framework observes the completion itself, as an answer before Send returned.
        using var caller = new CancellationTokenSource();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(caller.Token, Deadline(600_000));
        var site = new Site(memory);
        var client = new CompletingWhileAdapted(token =>
        {
            if (when == "after its own timeout")
            {
                Assert.True(token.WaitHandle.WaitOne(Patience));
            }
            else if (when == "after the caller cancelled")
            {
                caller.Cancel();
            }
        });

        var result = await Run(site.With(client.Send), budget, memory, timeoutMs: when == "after its own timeout" ? 10 : 300_000).AsTask().WaitAsync(Patience);

        Assert.Equal((1, 1), (client.OnCompletedCalls, client.GetResultCalls)); // pending when sent; its result taken once
        var expected = when switch
        {
            "in time" => (CallEnd.Answered, IoOutcome.Ok, 81, (string?)null),
            "after its own timeout" => (CallEnd.TimedOut, IoOutcome.Timeout, 0, "timed out after 10 ms"),
            _ => (CallEnd.Cancelled, IoOutcome.Cancelled, 0, null),
        };
        Assert.Equal((expected.Item1, expected.Item2, expected.Item3, expected.Item4, false), (result.End, result.Outcome, result.Value, result.Error, result.Abandoned));
        Assert.Equal(when == "after the caller cancelled", result.Exception is OperationCanceledException { } cancelled && cancelled.CancellationToken == caller.Token);
        Assert.Equal(result.End, Assert.Single(site.Recorded).Result.End);
        budget.Release();
        Assert.Equal(0, memory.Release()); // the completed client and the call site let go before the call returned
    }

    [Fact]
    public async Task AClientsOwnTimeoutException_ThrownInTime_IsTheClientsFailure()
    {
        // The wait passes the client's exception on: the very object it threw tells it apart from the wait's own timeout.
        var client = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);
        var thrown = new TimeoutException("transport");

        var running = Run(site.With(_ => new ValueTask<int>(client.Task)), budget, memory, timeoutMs: 300_000).AsTask();
        client.SetException(thrown);
        var result = await running.WaitAsync(Patience);

        Assert.Equal((CallEnd.Threw, IoOutcome.Timeout, "transport", false), (result.End, result.Outcome, result.Error, result.Abandoned));
        Assert.Same(thrown, result.Exception);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    [Theory]
    [InlineData("own")]
    [InlineData("budget")]
    [InlineData("caller")]
    public async Task ASynchronousAnswerAfterTheCallsBound_IsNotUsed(string bound)
    {
        // The client waits until its token is cancelled and then answers anyway, synchronously. With an own timeout shorter
        // than the budget, the node's budget is far from spent: only the call's own bound can tell the answer is late.
        using var caller = new CancellationTokenSource();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(caller.Token, Deadline(bound == "budget" ? 10 : 600_000));
        var site = new Site(memory);

        var pending = Run(site.With(token =>
        {
            if (bound == "caller")
            {
                caller.Cancel(); // the caller gives up while the client is still working
            }

            Assert.True(token.WaitHandle.WaitOne(Patience));
            return ValueTask.FromResult(17);
        }), budget, memory, timeoutMs: bound == "own" ? 10 : null);

        Assert.True(pending.IsCompleted);
        var result = await pending;
        var expected = bound switch
        {
            "own" => (CallEnd.TimedOut, IoOutcome.Timeout, "timed out after 10 ms"),
            "budget" => (CallEnd.BudgetRanOut, IoOutcome.Timeout, (string?)null),
            _ => (CallEnd.Cancelled, IoOutcome.Cancelled, null),
        };
        Assert.Equal((expected.Item1, expected.Item2, expected.Item3, 0, false), (result.End, result.Outcome, result.Error, result.Value, result.Abandoned));
        Assert.Equal(bound != "own", result.Exception is OperationCanceledException);
        Assert.Equal(bound == "own", !budget.Token.IsCancellationRequested); // an own timeout: the node's budget was left
        Assert.Equal(result.End, Assert.Single(site.Recorded).Result.End);
        budget.Release();
        Assert.Equal(0, memory.Release());
    }

    [Theory]
    [InlineData(7, IoOutcome.Ok, null)]
    [InlineData(-1, IoOutcome.Error, "negative")]
    public async Task ASynchronousAnswer_TakesNoLease_AndLetsGoOfEverythingAtOnce(int value, IoOutcome outcome, string? error)
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);
        var client = default(CancellationToken);

        var pending = Run(site.With(token =>
        {
            client = token;
            return ValueTask.FromResult(value);
        }), budget, memory, timeoutMs: 50);

        Assert.True(pending.IsCompletedSuccessfully);
        var result = await pending;
        Assert.Equal((CallEnd.Answered, value, outcome, error, false), (result.End, result.Value, result.Outcome, result.Error, result.Abandoned));
        Assert.True(Assert.Single(site.Recorded).MemoryLive);
        budget.Release();
        Assert.Throws<ObjectDisposedException>(() => client.WaitHandle); // the call's own token source is gone already
        Assert.Throws<ObjectDisposedException>(() => budget.Token.WaitHandle);
        Assert.Equal(0, memory.Release()); // nothing holds the memory but the request
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WhatAClientThrows_IsAnErrorUnlessItIsATimeout(bool timeout, bool synchronously)
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);
        Exception thrown = timeout ? new TimeoutException("transport") : new InvalidOperationException("refused");

        var result = await Run(site.With(_ => synchronously ? throw thrown : ValueTask.FromException<int>(thrown)), budget, memory, timeoutMs: null);

        Assert.Equal((CallEnd.Threw, timeout ? IoOutcome.Timeout : IoOutcome.Error, thrown.Message), (result.End, result.Outcome, result.Error));
        Assert.Same(thrown, result.Exception);
        Assert.Single(site.Recorded);
        budget.Release();
        Assert.Equal(0, memory.Release());
    }

    [Fact]
    public async Task AnAsynchronousAnswer_IsRecordedWhileTheCallHoldsTheMemory_EvenAfterTheRequestLetGo()
    {
        using var gate = new ManualResetEventSlim();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);

        var running = Run(site.With(_ => OnThread(gate, () => 3)), budget, memory, timeoutMs: null).AsTask();
        Assert.Equal(1, memory.Release()); // the request has gone; the call is still waiting
        gate.Set();
        var result = await running.WaitAsync(Patience);

        Assert.Equal((CallEnd.Answered, 3), (result.End, result.Value));
        Assert.True(Assert.Single(site.Recorded).MemoryLive);
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
        budget.Release();
    }

    [Theory]
    [InlineData(-1_000, false, true)] // the deadline has passed
    [InlineData(600_000, false, false)] // time is left
    [InlineData(-1_000, true, false)] // the caller cancelled: not the budget's doing
    public void RanOut_IsTheBudgetRunningOut_NotTheCaller(int deadlineMs, bool callerCancelled, bool ranOut)
    {
        using var caller = new CancellationTokenSource();
        if (callerCancelled)
        {
            caller.Cancel();
        }

        var budget = new AttemptBudget(caller.Token, Deadline(deadlineMs));

        Assert.Equal(ranOut, budget.RanOut);
        budget.Release();
    }

    [Fact]
    public void RanOut_CountsTheBudgetsTimer_AndNeverHoldsForAnUntimedAttempt()
    {
        var timed = new AttemptBudget(CancellationToken.None, Deadline(20));
        Assert.True(SpinWait.SpinUntil(() => timed.Token.IsCancellationRequested, Patience));
        Assert.True(timed.RanOut);
        timed.Release();

        var untimed = new AttemptBudget(CancellationToken.None, long.MaxValue);
        Assert.False(untimed.RanOut);
        Assert.Equal(TimeSpan.MaxValue, untimed.Remaining);
        untimed.Release();
    }

    private static long Deadline(int milliseconds) => Stopwatch.GetTimestamp() + ((long)milliseconds * Stopwatch.Frequency / 1000);

    private static ValueTask<CallResult<int>> Run(Site site, AttemptBudget budget, RequestMemory memory, int? timeoutMs) =>
        BoundedCall.RunAsync<int, Site>(site, budget, memory, Target, timeoutMs, "req", null);

    /// <summary>Completes the call on a dedicated thread once <paramref name="gate"/> is set; the client ignores its token.</summary>
    private static ValueTask<int> OnThread(ManualResetEventSlim gate, Func<int> work)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!gate.Wait(Patience * 3))
                {
                    throw new TimeoutException("the test never opened the gate");
                }

                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "test client" };
        thread.Start();
        return new ValueTask<int>(done.Task);
    }

    /// <summary>
    /// Holds back the first continuation that resumes the marked flow on another thread, until released. The wait for a
    /// pending client resumes that way when it ends (an async method restores its execution context when it resumes, and
    /// the AsyncLocal is told), so a test can complete the client after the wait has ended and before the call is
    /// classified: a window no timing makes reliable.
    /// </summary>
    private sealed class ResumeGate : IDisposable
    {
        private readonly AsyncLocal<ResumeGate?> _flow;
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _released = new();
        private int _caught;

        public ResumeGate() => _flow = new AsyncLocal<ResumeGate?>(OnChanged);

        /// <summary>Marks the current flow: what it awaits from now on carries the mark.</summary>
        public void Mark() => _flow.Value = this;

        /// <summary>Waits until a continuation is held.</summary>
        public bool WaitHeld() => _held.Wait(Patience);

        public void Release() => _released.Set();

        public void Dispose() => _released.Set(); // never leave a thread held when a test fails

        private void OnChanged(AsyncLocalValueChangedArgs<ResumeGate?> change)
        {
            if (change.ThreadContextChanged && ReferenceEquals(change.CurrentValue, this) && Interlocked.Exchange(ref _caught, 1) == 0)
            {
                _held.Set();
                _released.Wait(Patience);
            }
        }
    }

    /// <summary>
    /// A client whose ValueTask is still pending when Send returns and completes with 81 while the framework turns it into
    /// a task: AsTask registers its continuation through OnCompleted, which first runs <c>meanwhile</c> with the token the
    /// client got, then completes. No timing is involved: by the time a wait could be set up, the client has completed.
    /// </summary>
    private sealed class CompletingWhileAdapted(Action<CancellationToken> meanwhile) : IValueTaskSource<int>
    {
        private CancellationToken _token;
        private int _completed;

        public int OnCompletedCalls { get; private set; }

        public int GetResultCalls { get; private set; }

        public ValueTask<int> Send(CancellationToken token)
        {
            _token = token;
            return new ValueTask<int>(this, 0);
        }

        public ValueTaskSourceStatus GetStatus(short token) =>
            Volatile.Read(ref _completed) == 1 ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;

        public int GetResult(short token)
        {
            GetResultCalls++;
            return Volatile.Read(ref _completed) == 1 ? 81 : throw new InvalidOperationException("the result was taken before the client completed");
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            OnCompletedCalls++;
            meanwhile(_token);
            Volatile.Write(ref _completed, 1);
            continuation(state);
        }
    }

    /// <summary>A call site: a negative answer is a failure; every end is recorded with whether the memory was still live.</summary>
    private readonly struct Site(RequestMemory memory, Func<CancellationToken, ValueTask<int>>? send = null, List<(CallResult<int> Result, bool MemoryLive)>? ended = null)
        : IBoundedCall<int>
    {
        private readonly List<(CallResult<int> Result, bool MemoryLive)> _ended = ended ?? new();

        public IReadOnlyList<(CallResult<int> Result, bool MemoryLive)> Recorded
        {
            get
            {
                lock (_ended)
                {
                    return _ended.ToList();
                }
            }
        }

        public Site With(Func<CancellationToken, ValueTask<int>> client) => new(memory, client, _ended);

        public ValueTask<int> Send(CancellationToken token) => send!(token);

        public bool Succeeded(int value, out string? error)
        {
            error = value < 0 ? "negative" : null;
            return value >= 0;
        }

        public void Ended(in CallResult<int> result, TimeSpan duration)
        {
            bool live;
            try
            {
                memory.Rent<int>(1);
                live = true;
            }
            catch (ObjectDisposedException)
            {
                live = false;
            }

            lock (_ended)
            {
                _ended.Add((result, live));
            }
        }
    }
}

/// <summary>
/// What a synchronously answering call through <see cref="BoundedCall"/> allocates (rule 6): nothing when it shares the
/// node's budget, one linked token source and its timer when it has an own, shorter timeout.
/// </summary>
[Collection(AllocationMeasurement.Name)]
public sealed class BoundedCallAllocationTests
{
    [Fact]
    public void ASynchronousAnswer_SharingTheNodesBudget_AllocatesNothing()
    {
        Assert.Equal(0, BytesPerCall(timeoutMs: null, calls: 100));
    }

    [Fact]
    public void ASynchronousAnswer_WithAShorterOwnTimeout_CostsOneTokenSourceAndTimer()
    {
        // The token source is created before the call is sent, so that the client's token carries the timeout. About
        // 160 B on .NET 8: the linked source, its registration on the node token and the timer. Bounded, so that anything
        // more per call shows up here.
        var bytes = BytesPerCall(timeoutMs: 1_000, calls: 2_000);

        Assert.InRange(bytes, 1, 256);
    }

    /// <summary>The average bytes one call allocates on this thread, after warming up.</summary>
    private static double BytesPerCall(int? timeoutMs, int calls)
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (600L * Stopwatch.Frequency));
        var target = new IoTarget(IoKind.CandidateSource, "s");
        for (var i = 0; i < 100; i++)
        {
            Consume(BoundedCall.RunAsync<int, Answering>(default, budget, memory, target, timeoutMs, "req", null));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++)
        {
            Consume(BoundedCall.RunAsync<int, Answering>(default, budget, memory, target, timeoutMs, "req", null));
        }

        var bytes = (double)(GC.GetAllocatedBytesForCurrentThread() - before) / calls;
        budget.Release();
        memory.Release();
        return bytes;
    }

    /// <summary>Takes the result of a call that must have completed synchronously.</summary>
    private static void Consume(ValueTask<CallResult<int>> pending)
    {
        if (!pending.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("the call did not complete synchronously");
        }

        _ = pending.GetAwaiter().GetResult();
    }

    private readonly struct Answering : IBoundedCall<int>
    {
        public ValueTask<int> Send(CancellationToken token) => new(1);

        public bool Succeeded(int value, out string? error)
        {
            error = null;
            return true;
        }

        public void Ended(in CallResult<int> result, TimeSpan duration)
        {
        }
    }
}

/// <summary>A candidate buffer abandoned with its source still running reads as empty and takes no more candidates.</summary>
public sealed class CandidateBufferTests
{
    [Fact]
    public void AnAbandonedBuffer_ReadsAsEmpty_AndIgnoresLateAdds()
    {
        var memory = new RequestMemory();
        var buffer = new CandidateBuffer(2, memory);
        buffer.Add(1, 0.9);
        buffer.Add(2, 0.8);
        buffer.Add(3, 0.7); // over the limit

        buffer.Abandon();
        buffer.Add(4, 0.6);

        Assert.Equal((0, 0, 0, 0), (buffer.Count, buffer.Keys.Length, buffer.Scores.Length, buffer.Truncated));
        memory.Release();
    }

    [Fact]
    public void AClosedBuffer_KeepsWhatItHolds_AndIgnoresLateAdds()
    {
        var memory = new RequestMemory();
        var buffer = new CandidateBuffer(3, memory);
        buffer.Add(1, 0.9);

        buffer.Close();
        buffer.Add(2, 0.8);

        Assert.Equal(1, buffer.Count);
        Assert.Equal(1, buffer.Keys[0].Int64Value);
        memory.Release();
    }
}
