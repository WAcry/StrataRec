using StrataRec.Core.Threading;
using System.Diagnostics;
using System.Threading.Tasks.Sources;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Sources;
using StrataRec.UnitTests.Features;
using StrataRec.UnitTests.Features.Cache;
using StrataRec.Testing;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// The rules of the framework's I/O boundary (<see cref="BoundedCall"/>: service calls, feature connectors, distributed
/// caches, assignment providers), checked on the helper itself so that no call site can break them unnoticed: the wait has its own bound, the memory and the
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

    private static readonly IoTarget Target = new(IoRoles.Model, "m");

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
        }), budget, memory, timeoutMs: null).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.BudgetRanOut, IoOutcome.Timeout, true, "timed out: the time budget ran out"), (result.End, result.Outcome, result.Abandoned, result.Error));
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        var ended = Assert.Single(site.Recorded);
        Assert.True(ended.MemoryLive); // recorded while the call held the memory
        Assert.Equal(budget.Token, client); // no timeout of its own: the client got the node token

        budget.Release(); // the attempt ends
        Assert.Equal(1, memory.Release()); // the request returns: the client still holds the memory, once
        Assert.True(pool.Outstanding > 0); // the ledger is still out
        Assert.True(SpinWait.SpinUntil(() => client.IsCancellationRequested, Patience)); // cancelled at the budget...
        _ = client.WaitHandle; // ...and still alive: the source is not disposed while the client runs

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
        DisposedTokens.AssertEventuallyDisposed(client); // disposed once the client completed
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
        }), budget, memory, timeoutMs: 50).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.TimedOut, IoOutcome.Timeout, true, "timed out after 50 ms"), (result.End, result.Outcome, result.Abandoned, result.Error));
        Assert.Null(result.Exception);
        Assert.NotEqual(budget.Token, client);
        Assert.True(SpinWait.SpinUntil(() => client.IsCancellationRequested, Patience));
        Assert.False(budget.Token.IsCancellationRequested);

        budget.Release();
        Assert.Equal(1, memory.Release());
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
        DisposedTokens.AssertEventuallyDisposed(client);
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
        await started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        caller.Cancel();
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

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
        Assert.Equal(which == "own" ? "timed out after 50 ms" : "timed out: the time budget ran out", result.Error);
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

        // Marked as the call is sent, after its timeout's timer was set: that timer runs its callback in the context it was
        // set in (ObservedCancellationSource), and it fires with the wait's timeout; marked before, it would be held.
        var running = Task.Run(async () => await Run(site.With(_ =>
        {
            gate.Mark();
            return new ValueTask<int>(client.Task);
        }), budget, memory, timeoutMs: 200));
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
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

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
        Assert.True(waiting.Wait(Patience, TestContext.Current.CancellationToken));
        // Cancelled from a thread of its own: the wait's continuation may run inside Cancel, and the gate holds it there.
        var cancelling = Task.Run(caller.Cancel, TestContext.Current.CancellationToken);
        Assert.True(gate.WaitHeld(), "the wait never resumed on a thread of its own");
        client.SetResult(31);
        gate.Release();
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await cancelling.WaitAsync(Patience, TestContext.Current.CancellationToken);

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

        var result = await Run(site.With(client.Send), budget, memory, timeoutMs: when == "after its own timeout" ? 10 : 300_000).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

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
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.Threw, IoOutcome.Timeout, "threw TimeoutException: transport", false), (result.End, result.Outcome, result.Error, result.Abandoned));
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
            "budget" => (CallEnd.BudgetRanOut, IoOutcome.Timeout, (string?)"timed out: the time budget ran out"),
            _ => (CallEnd.Cancelled, IoOutcome.Cancelled, null),
        };
        Assert.Equal((expected.Item1, expected.Item2, expected.Item3, 0, false), (result.End, result.Outcome, result.Error, result.Value, result.Abandoned));
        Assert.Equal(bound != "own", result.Exception is OperationCanceledException);
        Assert.Equal(bound == "own", !budget.Token.IsCancellationRequested); // an own timeout: the node's budget was left
        Assert.Equal(result.End, Assert.Single(site.Recorded).Result.End);
        budget.Release();
        Assert.Equal(0, memory.Release());
    }

    /// <summary>
    /// A synchronous client that returns only after both the node's budget and its own, later, timeout have passed was cut
    /// off by the earlier, the budget: the operator gets the budget's cancellation (review of IO step 4). It was classified
    /// by its own timeout, which was checked first, and the operator got a failed status instead.
    /// </summary>
    [Fact]
    public async Task ASynchronousAnswerAfterBothDeadlines_IsCutOffByTheEarlier()
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(50));
        var site = new Site(memory);

        var result = await Run(site.With(_ =>
        {
            Thread.Sleep(300);
            return ValueTask.FromResult(5);
        }), budget, memory, timeoutMs: 150);

        Assert.Equal((CallEnd.BudgetRanOut, IoOutcome.Timeout, "timed out: the time budget ran out"), (result.End, result.Outcome, result.Error));
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
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
        }), budget, memory, timeoutMs: 300_000); // its own, shorter than the node's: a token source of its own

        Assert.True(pending.IsCompletedSuccessfully);
        var result = await pending;
        Assert.Equal((CallEnd.Answered, value, outcome, error, false), (result.End, result.Value, result.Outcome, result.Error, result.Abandoned));
        Assert.True(Assert.Single(site.Recorded).MemoryLive);
        budget.Release();
        DisposedTokens.AssertEventuallyDisposed(client); // the call's own token source is gone already
        DisposedTokens.AssertEventuallyDisposed(budget.Token);
        Assert.Equal(0, memory.Release()); // nothing holds the memory but the request
    }

    /// <summary>
    /// A client that cancels its own call, with none of the framework's tokens cancelled and its deadline ahead, threw: a
    /// timeout of its own, said so without reading the exception's message (review of 43bdf97: the message, a getter an
    /// exception may override, was read on the request's thread, and a cancellation's message does not say whose it was).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClientsOwnCancellation_IsATimeoutItThrew_SaidWithoutItsMessage(bool synchronously)
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);
        var thrown = new MessageCountingCancellation();

        var result = await Run(site.With(_ => synchronously ? throw thrown : ValueTask.FromException<int>(thrown)), budget, memory, timeoutMs: null);

        Assert.Equal((CallEnd.Threw, IoOutcome.Timeout, "threw MessageCountingCancellation: it cancelled the call itself, before its deadline"),
            (result.End, result.Outcome, result.Error));
        Assert.Same(thrown, result.Exception);
        Assert.Equal(0, thrown.MessageReads);
        budget.Release();
        Assert.Equal(0, memory.Release());
    }

    /// <summary>
    /// A client that answers its token's cancellation with an exception of its protocol, not a cancellation (gRPC's
    /// <c>RpcException</c>, status <c>Cancelled</c>), before the wait ends: the call's own deadline cancelled the token, so
    /// it timed out, as when the wait ends first, and the protocol's echo of the cancellation is dropped (2026-11-22: it
    /// was recorded as the client's error). The wait's timer is put far off by moving the deadline's clock back once the
    /// call is sent, so the client always comes first.
    /// </summary>
    [Fact]
    public async Task AClientThatAnswersItsCancelledTokenWithAnotherException_TimedOut()
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(600_000));
        var site = new Site(memory);
        var clock = new ManualClock();
        clock.HoldTimestamps();
        var deadline = CallDeadline.At(clock, clock.GetTimestamp() + (clock.TimestampFrequency / 50), 20); // the token: 20 ms
        var client = default(CancellationToken);

        var result = await BoundedCall.RunAsync<int, Site>(site.With(token =>
        {
            client = token;
            var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => answer.TrySetException(new ProtocolCancellation()));
            clock.AdvanceTimestamps(TimeSpan.FromMinutes(-10)); // the wait: ten minutes
            return new ValueTask<int>(answer.Task);
        }), budget, memory, Target, deadline).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(client.IsCancellationRequested);
        Assert.Equal((CallEnd.TimedOut, IoOutcome.Timeout, "timed out after 20 ms", false), (result.End, result.Outcome, result.Error, result.Abandoned));
        Assert.Equal(result.End, Assert.Single(site.Recorded).Result.End);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience)); // the client completed: nothing holds it
    }

    /// <summary>
    /// A client's failure that came before any of the framework's tokens was cancelled stays its own, type and message,
    /// when the caller cancels or the budget runs out after it and before the call is classified (review of 763fcbd: the
    /// tokens were read when the call was classified, so the failure became the caller's cancellation, or the budget's).
    /// The client completes on a thread of its own with its continuations inline: the lease reads the tokens, then the
    /// wait ends and its continuation, which alone carries the gate's mark, is held there.
    /// </summary>
    [Theory]
    [InlineData("caller")]
    [InlineData("budget")]
    public async Task AFailureThatCameFirst_StaysTheClients_WhenATokenIsCancelledBeforeTheCallIsClassified(string late)
    {
        using var gate = new ResumeGate();
        using var caller = new CancellationTokenSource();
        using var waiting = new ManualResetEventSlim();
        var client = new TaskCompletionSource<int>();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(caller.Token, Deadline(late == "budget" ? 300 : 600_000));
        var site = new Site(memory).WhenPending(gate.Mark);
        var failure = new InvalidOperationException("the service failed");

        var running = Task.Run(async () =>
        {
            var pending = Run(site.With(_ => new ValueTask<int>(client.Task)), budget, memory, timeoutMs: null);
            waiting.Set();
            return await pending;
        });
        Assert.True(waiting.Wait(Patience, TestContext.Current.CancellationToken));
        var failing = Task.Run(() => client.SetException(failure), TestContext.Current.CancellationToken);
        Assert.True(gate.WaitHeld(), "the wait never resumed on the failing thread");
        if (late == "caller")
        {
            caller.Cancel();
        }
        else
        {
            Assert.True(budget.Token.WaitHandle.WaitOne(Patience));
        }

        gate.Release();
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await failing.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.Threw, IoOutcome.Error, "threw InvalidOperationException: the service failed", false), (result.End, result.Outcome, result.Error, result.Abandoned));
        Assert.Same(failure, result.Exception);
        Assert.Equal(result.End, Assert.Single(site.Recorded).Result.End);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    /// <summary>
    /// A client whose task ended cancelled on its own (its transport's token: an HTTP client's own timeout ends its async
    /// method cancelled, not faulted) cancelled the call itself, though the caller cancels after it and before the call is
    /// classified (review of 1158d00: the caller was read when the call was classified, and the client, done, was said to
    /// be abandoned). Set up as the failure that came first: its wait's continuation is held until the caller cancelled.
    /// </summary>
    [Fact]
    public async Task AClientsOwnCancellation_ThatCameFirst_StaysItsOwn_WhenTheCallerCancelsBeforeTheCallIsClassified()
    {
        using var gate = new ResumeGate();
        using var caller = new CancellationTokenSource();
        using var transport = new CancellationTokenSource();
        using var waiting = new ManualResetEventSlim();
        var client = new TaskCompletionSource<int>();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(caller.Token, Deadline(600_000));
        var site = new Site(memory).WhenPending(gate.Mark);
        transport.Cancel();

        var running = Task.Run(async () =>
        {
            var pending = Run(site.With(_ => new ValueTask<int>(client.Task)), budget, memory, timeoutMs: null);
            waiting.Set();
            return await pending;
        });
        Assert.True(waiting.Wait(Patience, TestContext.Current.CancellationToken));
        var cancelling = Task.Run(() => client.SetCanceled(transport.Token), TestContext.Current.CancellationToken);
        Assert.True(gate.WaitHeld(), "the wait never resumed on the cancelling thread");
        caller.Cancel();
        gate.Release();
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await cancelling.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.Threw, IoOutcome.Timeout, "threw TaskCanceledException: it cancelled the call itself, before its deadline", false),
            (result.End, result.Outcome, result.Error, result.Abandoned));
        Assert.Equal(transport.Token, Assert.IsType<TaskCanceledException>(result.Exception).CancellationToken);
        Assert.Equal(result.End, Assert.Single(site.Recorded).Result.End);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    /// <summary>
    /// A caller that ended the wait before the cancellation a client's task ended with reached it was first: the call is
    /// the caller's, though the client's task had ended cancelled, on its own, before the caller cancelled (review of
    /// 378b594: the lease had seen the client complete with no token cancelled, and the call was the client's own
    /// timeout, the operator given a failed status instead of the caller's cancellation). The client's completion is held
    /// after the lease's continuation and before the wait's, by one registered between them.
    /// </summary>
    [Fact]
    public async Task ACallerThatEndedTheWaitFirst_IsWhoEndedTheCall_ThoughTheClientsTaskEndedCancelledBefore()
    {
        using var caller = new CancellationTokenSource();
        using var transport = new CancellationTokenSource();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        transport.Cancel();
        var client = new TaskCompletionSource<int>();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(caller.Token, Deadline(600_000));
        var site = new Site(memory).WhenPending(() => _ = client.Task.ContinueWith(
            _ =>
            {
                held.Set();
                release.Wait(Patience);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));

        var running = Run(site.With(_ => new ValueTask<int>(client.Task)), budget, memory, timeoutMs: null).AsTask();
        var cancelling = Task.Run(() => client.SetCanceled(transport.Token), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(held.Wait(Patience, TestContext.Current.CancellationToken), "the client's completion was never held");
            Assert.True(client.Task.IsCanceled);
            caller.Cancel();
            var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

            Assert.Equal((CallEnd.Cancelled, IoOutcome.Cancelled, true), (result.End, result.Outcome, result.Abandoned));
            Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(result.Exception).CancellationToken);
            Assert.Equal(result.End, Assert.Single(site.Recorded).Result.End);
        }
        finally
        {
            release.Set();
            await cancelling.WaitAsync(Patience, TestContext.Current.CancellationToken);
        }

        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    /// <summary>
    /// A client that answered the call's own deadline (an exception of its protocol) timed out, though the node's budget
    /// ran out too before the call was classified: the call's own deadline came first (review of 763fcbd: it was the
    /// budget's, and the operator was thrown a cancellation instead of a failed status).
    /// </summary>
    [Fact]
    public async Task ACallsOwnDeadline_ThatCameFirst_IsItsTimeout_ThoughTheBudgetRunsOutBeforeTheCallIsClassified()
    {
        using var gate = new ResumeGate();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Deadline(400));
        var site = new Site(memory).WhenPending(gate.Mark);
        var clock = new ManualClock();
        clock.HoldTimestamps();
        var deadline = CallDeadline.At(clock, clock.GetTimestamp() + (clock.TimestampFrequency / 20), 50); // the token: 50 ms
        var client = new TaskCompletionSource<int>(); // continuations inline, on the thread the token's callbacks run on

        var running = Task.Run(async () => await BoundedCall.RunAsync<int, Site>(site.With(token =>
        {
            token.Register(() => client.TrySetException(new ProtocolCancellation()));
            clock.AdvanceTimestamps(TimeSpan.FromMinutes(-10)); // the wait: the budget's
            return new ValueTask<int>(client.Task);
        }), budget, memory, Target, deadline));
        Assert.True(gate.WaitHeld(), "the wait never resumed on the client's thread");
        Assert.False(budget.Token.IsCancellationRequested);
        Assert.True(budget.Token.WaitHandle.WaitOne(Patience));
        gate.Release();
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.TimedOut, IoOutcome.Timeout, "timed out after 50 ms", false), (result.End, result.Outcome, result.Error, result.Abandoned));
        Assert.Null(result.Exception);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    /// <summary>What a gRPC client throws when its token is cancelled, unless it is told to throw a cancellation.</summary>
    private sealed class ProtocolCancellation() : Exception("Status(StatusCode=\"Cancelled\", Detail=\"Call canceled by the client.\")");

    private sealed class MessageCountingCancellation() : OperationCanceledException(new CancellationToken(canceled: true))
    {
        public int MessageReads { get; private set; }

        public override string Message
        {
            get
            {
                MessageReads++;
                return "the client's own timeout";
            }
        }
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

        Assert.Equal((CallEnd.Threw, timeout ? IoOutcome.Timeout : IoOutcome.Error, $"threw {thrown.GetType().Name}: {thrown.Message}"), (result.End, result.Outcome, result.Error));
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
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

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

    /// <summary>
    /// Rule 8 (Gemini review 2026-11-14, finding 4): a call whose caller has already cancelled (an earlier source of the
    /// same recall did, say) or whose time is up is not sent; it ends cancelled or out of budget, recorded once, and gives
    /// back the memory and the budget.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACallWhoseBoundHasPassed_IsNotSent_AndEndsAsCutOff(bool callerCancelled)
    {
        using var caller = new CancellationTokenSource();
        if (callerCancelled)
        {
            caller.Cancel();
        }

        var memory = new RequestMemory();
        var budget = new AttemptBudget(caller.Token, callerCancelled ? Deadline(600_000) : Deadline(-1));
        var site = new Site(memory);
        var sent = 0;

        var result = await Run(site.With(_ =>
        {
            sent++;
            return ValueTask.FromResult(1);
        }), budget, memory, timeoutMs: null);

        Assert.Equal(0, sent);
        Assert.Equal(callerCancelled ? (CallEnd.Cancelled, IoOutcome.Cancelled, false) : (CallEnd.BudgetRanOut, IoOutcome.Timeout, false),
            (result.End, result.Outcome, result.Abandoned));
        Assert.True(Assert.Single(site.Recorded).MemoryLive);
        budget.Release();
        Assert.Equal(0, memory.Release()); // nothing holds it any more
    }

    private static long Deadline(int milliseconds) => Stopwatch.GetTimestamp() + ((long)milliseconds * Stopwatch.Frequency / 1000);

    private static ValueTask<CallResult<int>> Run(Site site, AttemptBudget budget, RequestMemory memory, int? timeoutMs) =>
        BoundedCall.RunAsync<int, Site>(site, budget, memory, Target, CallDeadline.After(timeoutMs));

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
    private readonly struct Site(
        RequestMemory memory, Func<CancellationToken, ValueTask<int>>? send = null, List<(CallResult<int> Result, bool MemoryLive)>? ended = null, Action? pending = null)
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

        public Site With(Func<CancellationToken, ValueTask<int>> client) => new(memory, client, _ended, pending);

        /// <summary>Runs <paramref name="action"/> when the call is pending, before its wait is set up.</summary>
        public Site WhenPending(Action action) => new(memory, send, _ended, action);

        public ValueTask<int> Send(CancellationToken token) => send!(token);

        public IoOutcome Judge(int value, out string? error)
        {
            error = value < 0 ? "negative" : null;
            return value >= 0 ? IoOutcome.Ok : IoOutcome.Error;
        }

        public void Pending(Task done) => pending?.Invoke();

        public int Keys => -1;

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
/// Review 80, rule 5: a host's metric listener that throws while a call completes loses its measurement. The call is still
/// recorded, with the outcome it had, the caller gets its result rather than the listener's exception, and the call lets
/// go of the memory: the call site completed the call before recording it, so the throw left a call that had answered
/// unrecorded (outstanding in a recording) and threw out of the node.
/// </summary>
[Collection(MetricMeasurement.Name)]
public sealed class BoundedCallListenerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AListenerThatThrowsWhenACallCompletes_LosesItsMeasurement_AndTheCallIsStillRecorded(bool synchronously)
    {
        var target = new IoTarget(IoRoles.Recall, "review80_listener");
        using var listener = MeasurementHook.Throwing(StrataRecMetrics.Names.IoDuration, (StrataRecTags.IoTarget, target.Name));
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (600L * Stopwatch.Frequency));
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new Recording(synchronously ? new ValueTask<int>(5) : new ValueTask<int>(answer.Task));

        var running = BoundedCall.RunAsync<int, Recording>(call, budget, memory, target, CallDeadline.None).AsTask();
        answer.TrySetResult(5);
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.Answered, 5, IoOutcome.Ok), (result.End, result.Value, result.Outcome));
        Assert.Equal(IoOutcome.Ok, Assert.Single(call.Results).Outcome);
        Assert.Equal(1, listener.Calls); // it ran, and threw
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    [Fact]
    public async Task AnActivityListenerThatThrowsWhenACallStarts_LeavesItWithoutASpan_AndTheCallLetsGoOfTheMemory()
    {
        // The call site holds the request's memory, then starts the call: a throw from the host's start callback kept the
        // memory held for good (found while fixing review 80). The listener samples only this test's trace.
        var root = "bounded-call-start-" + Guid.NewGuid().ToString("N");
        ActivityTraceId trace = default;
        var started = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name == root || (trace != default && options.Parent.TraceId == trace)
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.None,
            ActivityStarted = span =>
            {
                if (span.TraceId == trace && span.OperationName != root)
                {
                    Interlocked.Increment(ref started);
                    throw new InvalidOperationException("a host's activity listener failed");
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var request = StrataRecTelemetry.ActivitySource.StartActivity(root);
        trace = request!.TraceId;
        var target = new IoTarget(IoRoles.Recall, "start_listener");
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (600L * Stopwatch.Frequency));
        var call = new Recording(new ValueTask<int>(5));

        var result = await BoundedCall.RunAsync<int, Recording>(call, budget, memory, target, CallDeadline.None)
            .AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.Answered, 5, IoOutcome.Ok), (result.End, result.Value, result.Outcome));
        Assert.Equal(1, Volatile.Read(ref started)); // it ran, and threw
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, Patience));
    }

    /// <summary>
    /// A host's sampler that takes the time of both deadlines as the call starts leaves it not sent, cut off at the one
    /// that came first, the call's own: as a sent call would have been (review of IO step 4b: it was put down to the
    /// scope's bound, which the operator sees as its budget running out rather than the service timing out).
    /// </summary>
    [Fact]
    public async Task ACallWhoseTwoDeadlinesPassInTheHostsSampler_IsCutOffAtTheEarlier_AndNotSent()
    {
        var root = "bounded-call-two-deadlines-" + Guid.NewGuid().ToString("N");
        ActivityTraceId trace = default;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Name == root)
                {
                    return ActivitySamplingResult.AllDataAndRecorded;
                }

                if (trace != default && options.Parent.TraceId == trace)
                {
                    Thread.Sleep(400); // past both deadlines
                    return ActivitySamplingResult.AllDataAndRecorded;
                }

                return ActivitySamplingResult.None;
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var request = StrataRecTelemetry.ActivitySource.StartActivity(root);
        trace = request!.TraceId;
        var started = Stopwatch.GetTimestamp();
        var budget = new AttemptBudget(CancellationToken.None, started + (Stopwatch.Frequency / 5)); // 200 ms
        var sent = 0;
        var call = new Recording(new ValueTask<int>(5), () => sent++);

        var result = await BoundedCall.RunAsync<int, Recording>(call, budget, null, new IoTarget(IoRoles.Model, "two_deadlines"), CallDeadline.After(100))
            .AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.TimedOut, IoOutcome.Timeout, false, 0), (result.End, result.Outcome, result.Sent, sent));
        Assert.Equal(CallEnd.TimedOut, Assert.Single(call.Results).End);
        budget.Release();
    }

    /// <summary>
    /// A call whose host's sampler takes only the scope's time is the scope's, though its own deadline came first when it
    /// started: that deadline, on a clock of its own (held still here), has not passed (review of the step 4b fixes: the
    /// order told before the call started put it down to its own deadline).
    /// </summary>
    [Fact]
    public async Task ACallWhoseSamplerTakesOnlyTheScopesTime_IsTheScopes()
    {
        var root = "bounded-call-scope-only-" + Guid.NewGuid().ToString("N");
        ActivityTraceId trace = default;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Name == root)
                {
                    return ActivitySamplingResult.AllDataAndRecorded;
                }

                if (trace != default && options.Parent.TraceId == trace)
                {
                    Thread.Sleep(400); // past the scope's deadline
                    return ActivitySamplingResult.AllDataAndRecorded;
                }

                return ActivitySamplingResult.None;
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var request = StrataRecTelemetry.ActivitySource.StartActivity(root);
        trace = request!.TraceId;
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 5)); // 200 ms
        var held = new HeldClock();
        var own = CallDeadline.At(held, held.GetTimestamp() + (held.TimestampFrequency / 10), 100); // 100 ms, never passing
        var sent = 0;
        var call = new Recording(new ValueTask<int>(5), () => sent++);

        var result = await BoundedCall.RunAsync<int, Recording>(call, budget, null, new IoTarget(IoRoles.Model, "scope_only"), own)
            .AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((CallEnd.BudgetRanOut, IoOutcome.Timeout, false, 0), (result.End, result.Outcome, result.Sent, sent));
        budget.Release();
    }

    private sealed class HeldClock : TimeProvider
    {
        private readonly long _timestamp = Stopwatch.GetTimestamp();

        public override long GetTimestamp() => _timestamp;
    }

    /// <summary>Answers with <paramref name="answer"/> and keeps what each end recorded.</summary>
    private readonly struct Recording(ValueTask<int> answer, Action? onSend = null) : IBoundedCall<int>
    {
        public List<CallResult<int>> Results { get; } = new();

        public ValueTask<int> Send(CancellationToken token)
        {
            onSend?.Invoke();
            return answer;
        }

        public IoOutcome Judge(int value, out string? error)
        {
            error = null;
            return IoOutcome.Ok;
        }

        public void Pending(Task done)
        {
        }

        public int Keys => -1;

        public void Ended(in CallResult<int> result, TimeSpan duration)
        {
            lock (Results)
            {
                Results.Add(result);
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
        // 224 B on .NET 10: the source, its registration on the node token and the timer (160 B with CancelAfter, whose
        // timer cancels on the timer's thread; review 20 #1). Bounded, so that anything more per call shows up here.
        var bytes = BytesPerCall(timeoutMs: 300_000, calls: 2_000);

        Assert.InRange(bytes, 1, 256);
    }

    /// <summary>The average bytes one call allocates on this thread, after warming up.</summary>
    private static double BytesPerCall(int? timeoutMs, int calls)
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (600L * Stopwatch.Frequency));
        var target = new IoTarget(IoRoles.Recall, "s");
        for (var i = 0; i < 100; i++)
        {
            Consume(BoundedCall.RunAsync<int, Answering>(default, budget, memory, target, CallDeadline.After(timeoutMs)));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++)
        {
            Consume(BoundedCall.RunAsync<int, Answering>(default, budget, memory, target, CallDeadline.After(timeoutMs)));
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

        public IoOutcome Judge(int value, out string? error)
        {
            error = null;
            return IoOutcome.Ok;
        }

        public void Pending(Task done)
        {
        }

        public int Keys => -1;

        public void Ended(in CallResult<int> result, TimeSpan duration)
        {
        }
    }
}

/// <summary>A candidate buffer abandoned with its source still running reads as empty and takes no more candidates.</summary>
public sealed class CandidateBufferTests
{
    /// <summary>The items dropped past the limit stay at most int.MaxValue, however a recorded count adds to them (review of IO step 4).</summary>
    [Fact]
    public void TheDroppedCount_StaysAtMostIntMaxValue()
    {
        var memory = new RequestMemory();
        var buffer = new ServiceResponse(memory, 1);
        buffer.AddItem(1, 0.9);
        buffer.AddDropped(int.MaxValue);
        buffer.AddItem(2, 0.8); // over the limit
        buffer.AddDropped(5);

        Assert.Equal(int.MaxValue, buffer.Dropped);
        memory.Release();
    }

    [Fact]
    public void AnAbandonedBuffer_ReadsAsEmpty_AndIgnoresLateAdds()
    {
        var memory = new RequestMemory();
        var buffer = new ServiceResponse(memory, 2);
        buffer.AddItem(1, 0.9);
        buffer.AddItem(2, 0.8);
        buffer.AddItem(3, 0.7); // over the limit

        buffer.Abandon();
        buffer.AddItem(4, 0.6);

        Assert.Equal((0, 0, 0, 0), (buffer.Count, buffer.Keys.Length, buffer.Scores.Length, buffer.Dropped));
        memory.Release();
    }

    [Fact]
    public void AClosedBuffer_KeepsWhatItHolds_AndIgnoresLateAdds()
    {
        var memory = new RequestMemory();
        var buffer = new ServiceResponse(memory, 3);
        buffer.AddItem(1, 0.9);

        buffer.Close();
        buffer.AddItem(2, 0.8);

        Assert.Equal(1, buffer.Count);
        Assert.Equal(1, buffer.Keys[0].Int64Value);
        memory.Release();
    }
}
