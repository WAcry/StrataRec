using System.Diagnostics;
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

/// <summary>A synchronously answering call through <see cref="BoundedCall"/> allocates nothing (rule 6).</summary>
[Collection(AllocationMeasurement.Name)]
public sealed class BoundedCallAllocationTests
{
    [Fact]
    public void ASynchronousAnswer_AllocatesNothing()
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (600L * Stopwatch.Frequency));
        var target = new IoTarget(IoKind.CandidateSource, "s");
        for (var i = 0; i < 10; i++)
        {
            Consume(BoundedCall.RunAsync<int, Answering>(default, budget, memory, target, null, "req", null));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            Consume(BoundedCall.RunAsync<int, Answering>(default, budget, memory, target, null, "req", null));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        budget.Release();
        memory.Release();
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
