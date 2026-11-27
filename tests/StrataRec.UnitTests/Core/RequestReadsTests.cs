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
    }

    [Fact]
    public void AReadIsOfOneTablesOneKey_ToOneLimit()
    {
        var reads = new RequestReads();
        reads.Join(Table, 7, 0, out _);

        reads.Join(Table, 8, 0, out var otherKey);
        reads.Join(new object(), 7, 0, out var otherTable);
        reads.Join(Table, ((UInt128)1 << 64) + 7, 0, out var otherHalf);
        reads.Join(Table, 7, 20, out var otherLimit); // one record, asked for to another limit

        Assert.True(otherKey && otherTable && otherHalf && otherLimit);
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
        using var bound = CallBound.After(CancellationSite.Call, TestContext.Current.CancellationToken, 20);
        var waiting = reads.Join(Table, 7, 0, out _).WaitAsync(bound, CallDeadline.None);
        var blocking = bound.Token.Register(() => release.Wait(TimeSpan.FromSeconds(60))); // registered last: called first
        try
        {
            var answer = await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(SharedEnd.CutOff, answer.End);
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
    [InlineData("bound")]
    [InlineData("bound and own time")] // the bound's end is the node's: it is said first
    [InlineData("own time")]
    public async Task ARecordAlreadyThere_IsNotTakenByAReadWhoseTimeIsUp(string how)
    {
        var reads = new RequestReads();
        reads.Join(Table, 7, 0, out _).Complete("record");
        using var caller = new CancellationTokenSource();
        if (how == "cancelled")
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
