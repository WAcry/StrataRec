using StrataRec.Core.Threading;

namespace StrataRec.UnitTests.Core;

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
        var later = reads.Join(Table, 7, 0, out var third);
        Assert.False(third);
        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record"), await later.WaitAsync(bound, CallDeadline.After(60_000)));

        reads.End();
        Assert.NotSame(read, reads.Join(Table, 7, 0, out var outlived));
        Assert.True(outlived);
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
        reads.Join(Table, 7, 20, out var otherLimit);
        reads.Join(Table, 9, 0, out var otherPlan, new object());
        reads.Join(new object(), 9, 0, out var planElsewhere, plan); // the plan's results as another caller keeps them
        reads.Join(Table, 9, 0, out var noPlan);
        reads.Join(Table, 9, 0, out var samePlan, plan);

        Assert.True(otherKey && otherTable && otherHalf && otherLimit && otherPlan && planElsewhere && noPlan);
        Assert.False(samePlan);
    }

    [Fact]
    public async Task AWithdrawal_IsNotKept()
    {
        var reads = new RequestReads();
        var read = reads.Join(Table, 7, 0, out _);
        using var bound = Bound();
        var answer = reads.Join(Table, 7, 0, out _).WaitAsync(bound, CallDeadline.None);

        read.Withdraw();
        read.Withdraw();
        read.Complete("late");

        Assert.Equal(new SharedAnswer(SharedEnd.Withdrawn, null), await answer);
        var next = reads.Join(Table, 7, 0, out var reader);
        Assert.True(reader);
        Assert.NotSame(read, next);

        read.Withdraw();
        Assert.Same(next, reads.Join(Table, 7, 0, out var again));
        Assert.False(again);
    }

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

    [Fact]
    public async Task RequestsOfOneGroup_ShareARead_WhileTheRequestThatMadeItIsUnderWay()
    {
        var group = new RequestReads.Group();
        var first = new RequestReads(group);
        var second = new RequestReads(group);
        using var bound = Bound();

        var read = first.Join(Table, 7, 0, out var reader);
        first.Join(Table, 7, 0, out _);
        var joined = second.Join(Table, 7, 0, out var other);
        read.Complete("record");
        var third = new RequestReads(group);
        var kept = third.Join(Table, 7, 0, out var taken);

        Assert.True(reader);
        Assert.False(other || taken);
        Assert.Same(read, joined);
        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record"), await kept.WaitAsync(bound, CallDeadline.None));
        Assert.Equal(1, group.Count);

        first.End();
        first.End();
        Assert.Equal(0, group.Count);
        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record"), await joined.WaitAsync(bound, CallDeadline.None));

        var next = new RequestReads(group).Join(Table, 7, 0, out var after);
        Assert.True(after);
        Assert.NotSame(read, next);
        Assert.Same(next, second.Join(Table, 7, 0, out var again));
        Assert.False(again);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReadStillUnderWayWhenItsRequestEnds_GoesOnForThoseWaiting_AndNoLaterReadFindsIt(bool getsARecord)
    {
        var group = new RequestReads.Group();
        var first = new RequestReads(group);
        var second = new RequestReads(group);
        using var bound = Bound();
        var read = first.Join(Table, 7, 0, out _);
        var waiting = second.Join(Table, 7, 0, out _).WaitAsync(bound, CallDeadline.None).AsTask();

        first.End();

        Assert.Equal(0, group.Count);
        Assert.False(waiting.IsCompleted);
        var own = second.Join(Table, 7, 0, out var reader);
        Assert.True(reader);
        Assert.NotSame(read, own);

        if (getsARecord)
        {
            read.Complete("late");
        }
        else
        {
            read.Withdraw();
        }

        Assert.Equal(getsARecord ? new SharedAnswer(SharedEnd.Record, "late") : new SharedAnswer(SharedEnd.Withdrawn, null), await waiting);
        Assert.Same(own, second.Join(Table, 7, 0, out var again));
        Assert.False(again);
    }

    /// <summary>
    /// A request that ended holds none of what it read, of a host's group or on its own: a call it did not wait for
    /// may keep the request's reads long after, and would keep every record with them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARequestThatEnded_HoldsNoneOfWhatItRead(bool ofAGroup)
    {
        var group = new RequestReads.Group();
        var reads = ofAGroup ? new RequestReads(group) : new RequestReads();
        var record = ReadARecord(reads);

        reads.End();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(record.IsAlive);
        GC.KeepAlive(reads);
        GC.KeepAlive(group);
    }

    /// <summary>Not inlined, so no local here keeps it alive.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ReadARecord(RequestReads reads)
    {
        var record = new object();
        reads.Join(Table, 7, 0, out _).Complete(record);
        return new WeakReference(record);
    }

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
        new RequestReads(group).Join(Table, 8, 0, out var unseen);
        Assert.True(unseen);
    }

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
            await called.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set(); // before the registration is disposed, which waits for its callback
            blocking.Dispose();
        }
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("cancelled and own time")]
    [InlineData("bound")]
    [InlineData("bound and own time")]
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

    private static CallBound Under(CancellationToken caller) => new(CancellationSite.Call, caller, long.MaxValue);
}
