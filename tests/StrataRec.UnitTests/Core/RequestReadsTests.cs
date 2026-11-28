using StrataRec.Core.Threading;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// What the reads of one request share of one record: the first to join asks the origin, the others wait for what it
/// gets within their own bound, and take its record or ask the origin themselves.
/// </summary>
public class RequestReadsTests
{
    private static readonly object Table = new();

    [Fact]
    public async Task TheFirstToJoinReads_AndTheOthersGetItsRecord_ThoseThatComeLaterToo()
    {
        var reads = new RequestReads();
        var read = reads.Join(Table, 7, 0, out var reader);
        var waiting = reads.Join(Table, 7, 0, out var second);
        using var bound = Bound();
        var answer = waiting.WaitAsync(bound, CallDeadline.None);

        Assert.True(reader);
        Assert.False(second);
        Assert.Same(read, waiting);
        Assert.False(answer.IsCompleted);

        read.Complete("record");

        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record"), await answer);
        var later = reads.Join(Table, 7, 0, out var third); // kept for the request
        Assert.False(third);
        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record"), await later.WaitAsync(bound, CallDeadline.After(60_000)));
        reads.End(); // of a request on its own: its reads go with it, there is nothing to let go of
    }

    [Fact]
    public void AReadIsOfOneTablesOneKey_ToOneLimit_ByOnePlanWhereAPlansRunIsItsOrigin()
    {
        var reads = new RequestReads();
        var plan = new object();
        reads.Join(Table, 7, 0, out _);
        reads.Join(Table, 9, 0, out _, plan);

        reads.Join(Table, 8, 0, out var otherKey);
        reads.Join(new object(), 7, 0, out var otherTable);
        reads.Join(Table, ((UInt128)1 << 64) + 7, 0, out var otherHalf);
        reads.Join(Table, 7, 20, out var otherLimit); // one record, asked for to another limit
        reads.Join(Table, 9, 0, out var otherPlan, new object()); // a plan published since
        reads.Join(new object(), 9, 0, out var planElsewhere, plan); // the plan's results as another caller keeps them
        reads.Join(Table, 9, 0, out var noPlan);
        reads.Join(Table, 9, 0, out var samePlan, plan);

        Assert.True(otherKey && otherTable && otherHalf && otherLimit && otherPlan && planElsewhere && noPlan);
        Assert.False(samePlan);
    }

    /// <summary>
    /// A reader that got no record withdraws: those waiting are told to ask the origin themselves, and the next read is
    /// a reader again. A reader that withdraws after it ended its read changes nothing, so it can on every way out.
    /// </summary>
    [Fact]
    public async Task AWithdrawal_IsNotKept()
    {
        var reads = new RequestReads();
        var read = reads.Join(Table, 7, 0, out _);
        using var bound = Bound();
        var answer = reads.Join(Table, 7, 0, out _).WaitAsync(bound, CallDeadline.None);

        read.Withdraw();
        read.Withdraw();
        read.Complete("late"); // ended once

        Assert.Equal(new SharedAnswer(SharedEnd.Withdrawn, null), await answer);
        var next = reads.Join(Table, 7, 0, out var reader);
        Assert.True(reader);
        Assert.NotSame(read, next);

        // The first read's late end does not take the next one's place.
        read.Withdraw();
        Assert.Same(next, reads.Join(Table, 7, 0, out var again));
        Assert.False(again);
    }

    /// <summary>A reader that withdraws after its record was given leaves the record where it is.</summary>
    [Fact]
    public async Task ARecord_StaysWhenItsReaderWithdrawsAfterwards()
    {
        var reads = new RequestReads();
        var read = reads.Join(Table, 7, 0, out _);

        read.Complete("record");
        read.Withdraw();

        using var bound = Bound();
        var later = reads.Join(Table, 7, 0, out var reader);
        Assert.False(reader);
        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record"), await later.WaitAsync(bound, CallDeadline.None));
    }

    /// <summary>
    /// The requests of one group share a read as the runs of one request do, while a request that joined it is under
    /// way: the read outlives the request that made it for as long as another holds it, and is gone with the last, so
    /// a request that comes after reads for itself and the group holds nothing of requests that ended.
    /// </summary>
    [Fact]
    public async Task RequestsOfOneGroup_ShareARead_WhileOneThatJoinedItIsUnderWay()
    {
        var group = new RequestReads.Group();
        var first = new RequestReads(group);
        var second = new RequestReads(group);
        using var bound = Bound();

        var read = first.Join(Table, 7, 0, out var reader);
        first.Join(Table, 7, 0, out _); // another run of the same request
        var joined = second.Join(Table, 7, 0, out var other);
        read.Complete("record");
        first.End();
        first.End(); // ended once: again lets go of nothing more

        Assert.True(reader);
        Assert.False(other);
        Assert.Same(read, joined);
        var third = new RequestReads(group); // begun while the second, which holds the read, is under way
        var kept = third.Join(Table, 7, 0, out var taken);
        Assert.False(taken); // before the wait: a read of its own would never end
        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record"), await kept.WaitAsync(bound, CallDeadline.None));

        second.End();
        Assert.Equal(1, group.Count); // the third's
        third.End();
        Assert.Equal(0, group.Count);
        new RequestReads(group).Join(Table, 7, 0, out var after);
        Assert.True(after);
    }

    /// <summary>
    /// What a request does with the group once it has ended changes nothing for the others: a read of it that is still
    /// under way reads for itself, and the end of a request whose read withdrew does not take the place of the read
    /// another request has made of the record since.
    /// </summary>
    [Fact]
    public void ARequestThatEnded_LeavesTheGroupAsItIs()
    {
        var group = new RequestReads.Group();
        var ended = new RequestReads(group);
        var withdrawn = ended.Join(Table, 7, 0, out _);
        withdrawn.Withdraw();
        var other = new RequestReads(group);
        var current = other.Join(Table, 7, 0, out var reader);

        ended.End();
        var late = ended.Join(Table, 8, 0, out var alone);
        late.Complete("record");
        late.Withdraw();

        Assert.True(reader && alone);
        Assert.Same(current, new RequestReads(group).Join(Table, 7, 0, out var joined));
        Assert.False(joined);
        new RequestReads(group).Join(Table, 8, 0, out var unseen); // the late read was the ended request's alone
        Assert.True(unseen);
    }

    /// <summary>
    /// A wait ends with its own bound, whatever the reader does: when its caller cancels, when its bound runs out, and at
    /// its own deadline; the read goes on for the others.
    /// </summary>
    [Theory]
    [InlineData("cancelled")]
    [InlineData("bound")]
    [InlineData("bound before own time")]
    [InlineData("own time")]
    public async Task AWaitEndsWithItsOwnBound(string how)
    {
        var reads = new RequestReads();
        var read = reads.Join(Table, 7, 0, out _);
        using var caller = new CancellationTokenSource();
        using var bound = how.StartsWith("bound", StringComparison.Ordinal) ? CallBound.After(CancellationSite.Call, caller.Token, 20) : Under(caller.Token);
        var deadline = how switch { "own time" => CallDeadline.After(20), "bound before own time" => CallDeadline.After(600_000), _ => CallDeadline.None };
        var waiting = reads.Join(Table, 7, 0, out _).WaitAsync(bound, deadline);
        if (how == "cancelled")
        {
            caller.Cancel();
        }

        var answer = await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(how == "own time" ? SharedEnd.TimedOut : SharedEnd.CutOff, answer.End);
        read.Complete("record");
        using var fresh = Bound();
        Assert.Equal(SharedEnd.Record, (await reads.Join(Table, 7, 0, out _).WaitAsync(fresh, CallDeadline.None)).End);
    }

    /// <summary>
    /// The wait has its own timer and listens to the caller, as a call's wait does: a callback on the bound's token that
    /// does not return (the token is one an operator can register on) does not keep it from ending.
    /// </summary>
    [Fact]
    public async Task AWaitEndsAtItsBound_WhateverTheBoundsTokenCalls()
    {
        var reads = new RequestReads();
        reads.Join(Table, 7, 0, out _);
        using var release = new ManualResetEventSlim();
        using var registered = new ManualResetEventSlim();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bound = CallBound.After(CancellationSite.Call, TestContext.Current.CancellationToken, 500);
        var waiting = reads.Join(Table, 7, 0, out _).WaitAsync(bound, CallDeadline.None);

        // Registered after the wait began and before the bound's time is up: the token calls it first, and it stays.
        // Were the time up already, the token would call it here, where it is registered: it then returns, and the
        // test fails below for want of the call it is about.
        var blocking = bound.Token.Register(() =>
        {
            if (registered.IsSet)
            {
                called.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(60));
            }
        });
        registered.Set();
        try
        {
            var answer = await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(SharedEnd.CutOff, answer.End);
            await called.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); // the token did call it, and it is held there
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set(); // before the registration is disposed, which waits for its callback
            blocking.Dispose();
        }
    }

    /// <summary>
    /// A read whose time is up takes nothing, a record that is already there no more than one it would wait for: the
    /// call it stands for would not be sent.
    /// </summary>
    [Theory]
    [InlineData("cancelled")]
    [InlineData("cancelled and own time")] // the read's own time comes first of the two times, and the caller's end is still the one said
    [InlineData("bound")]
    [InlineData("bound and own time")] // the bound's end is the node's: it is said first
    [InlineData("own time")]
    public async Task ARecordAlreadyThere_IsNotTakenByAReadWhoseTimeIsUp(string how)
    {
        var reads = new RequestReads();
        reads.Join(Table, 7, 0, out _).Complete("record");
        using var caller = new CancellationTokenSource();
        if (how.StartsWith("cancelled", StringComparison.Ordinal))
        {
            caller.Cancel();
        }

        using var bound = how.StartsWith("bound", StringComparison.Ordinal) ? CallBound.After(CancellationSite.Call, caller.Token, 0) : Under(caller.Token);

        var answer = await reads.Join(Table, 7, 0, out _).WaitAsync(bound, how.EndsWith("own time", StringComparison.Ordinal) ? CallDeadline.After(0) : CallDeadline.None);

        Assert.Equal(new SharedAnswer(how == "own time" ? SharedEnd.TimedOut : SharedEnd.CutOff, null), answer);
    }

    private static CallBound Bound() => Under(TestContext.Current.CancellationToken);

    /// <summary>An untimed bound under <paramref name="caller"/>.</summary>
    private static CallBound Under(CancellationToken caller) => new(CancellationSite.Call, caller, long.MaxValue);
}
