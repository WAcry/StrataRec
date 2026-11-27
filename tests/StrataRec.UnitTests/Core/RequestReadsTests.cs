using StrataRec.Core.Threading;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// What the reads of one request share of one record: the first to join reads, the others wait for what it gets within
/// their own bound, and what they get says whether they are answered, fail as it did, or go on by themselves.
/// </summary>
public class RequestReadsTests
{
    private static readonly object Table = new();

    [Fact]
    public async Task TheFirstToJoinReads_AndTheOthersGetItsRecord_ThoseThatComeLaterToo()
    {
        var reads = new RequestReads();
        var read = reads.Join(Table, 7, out var reader);
        var waiting = reads.Join(Table, 7, out var second);
        using var bound = Bound();
        var answer = waiting.WaitAsync(bound, Timeout.InfiniteTimeSpan);

        Assert.True(reader);
        Assert.False(second);
        Assert.Same(read, waiting);
        Assert.False(answer.IsCompleted);

        read.Complete("record");

        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record", null), await answer);
        var later = reads.Join(Table, 7, out var third); // kept for the request
        Assert.False(third);
        Assert.Equal(new SharedAnswer(SharedEnd.Record, "record", null), await later.WaitAsync(bound, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void AReadIsOfOneTablesOneKey()
    {
        var reads = new RequestReads();
        reads.Join(Table, 7, out _);

        reads.Join(Table, 8, out var otherKey);
        reads.Join(new object(), 7, out var otherTable);
        reads.Join(Table, ((UInt128)1 << 64) + 7, out var otherHalf);

        Assert.True(otherKey && otherTable && otherHalf);
    }

    /// <summary>
    /// A failure and a withdrawal are given to those waiting and not kept: the next read is a reader again. A reader that
    /// withdraws after it ended its read changes nothing, so it can on every way out.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailureAndAWithdrawal_AreNotKept(bool failed)
    {
        var reads = new RequestReads();
        var read = reads.Join(Table, 7, out _);
        using var bound = Bound();
        var answer = reads.Join(Table, 7, out _).WaitAsync(bound, Timeout.InfiniteTimeSpan);

        if (failed)
        {
            read.Fail("the origin said no", "stale");
        }
        else
        {
            read.Withdraw();
        }

        read.Withdraw();
        read.Complete("late"); // ended once

        Assert.Equal(failed ? new SharedAnswer(SharedEnd.Failed, "stale", "the origin said no") : new SharedAnswer(SharedEnd.Withdrawn, null, null), await answer);
        var next = reads.Join(Table, 7, out var reader);
        Assert.True(reader);
        Assert.NotSame(read, next);

        // The first read's late end does not take the next one's place.
        read.Withdraw();
        Assert.Same(next, reads.Join(Table, 7, out var again));
        Assert.False(again);
    }

    /// <summary>
    /// A wait ends with its own bound, whatever the reader does: when its caller cancels, when its bound runs out, and at
    /// its own time; the read goes on for the others.
    /// </summary>
    [Theory]
    [InlineData("cancelled")]
    [InlineData("bound")]
    [InlineData("own time")]
    [InlineData("no time")]
    public async Task AWaitEndsWithItsOwnBound(string how)
    {
        var reads = new RequestReads();
        var read = reads.Join(Table, 7, out _);
        using var caller = new CancellationTokenSource();
        using var bound = how == "bound" ? CallBound.After(CancellationSite.Call, caller.Token, 20) : Under(caller.Token);
        var own = how switch
        {
            "own time" => TimeSpan.FromMilliseconds(20),
            "no time" => TimeSpan.Zero,
            _ => Timeout.InfiniteTimeSpan,
        };
        var waiting = reads.Join(Table, 7, out _).WaitAsync(bound, own);
        if (how == "cancelled")
        {
            caller.Cancel();
        }

        var answer = await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(how is "own time" or "no time" ? SharedEnd.TimedOut : SharedEnd.CutOff, answer.End);
        read.Complete("record");
        using var fresh = Bound();
        Assert.Equal(SharedEnd.Record, (await reads.Join(Table, 7, out _).WaitAsync(fresh, Timeout.InfiniteTimeSpan)).End);
    }

    /// <summary>A record that is already there is taken though the bound has passed: nothing is waited for.</summary>
    [Fact]
    public async Task ARecordAlreadyThere_IsTakenWithoutWaiting()
    {
        var reads = new RequestReads();
        reads.Join(Table, 7, out _).Complete("record");
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        using var bound = Under(caller.Token);

        var answer = await reads.Join(Table, 7, out _).WaitAsync(bound, TimeSpan.Zero);

        Assert.Equal(SharedEnd.Record, answer.End);
    }

    /// <summary>
    /// Reads join when they ask the same of a record: by the same times, to the same limit. One judged by other times is
    /// a hit where another misses, and a limit a service refuses is not a smaller one it answers.
    /// </summary>
    [Fact]
    public void ReadsThatAskOtherwiseOfARecord_AreNotOneRead()
    {
        UInt128 record = new(3, 7);
        var asked = RequestReads.Asked(record, ttlMs: 60_000, staleIfErrorMs: 0, limit: 20);

        Assert.Equal(asked, RequestReads.Asked(record, 60_000, 0, 20));
        Assert.Equal(6, new[]
        {
            asked,
            RequestReads.Asked(new UInt128(3, 8), 60_000, 0, 20),
            RequestReads.Asked(new UInt128(4, 7), 60_000, 0, 20),
            RequestReads.Asked(record, 1_000, 0, 20),
            RequestReads.Asked(record, 60_000, 600_000, 20),
            RequestReads.Asked(record, 60_000, 0, 1_000),
        }.Distinct().Count());
        Assert.Equal(RequestReads.Asked(record, 60_000, 0), RequestReads.Asked(record, 60_000, 0, limit: 0));
    }

    private static CallBound Bound() => Under(TestContext.Current.CancellationToken);

    /// <summary>An untimed bound under <paramref name="caller"/>.</summary>
    private static CallBound Under(CancellationToken caller) => new(CancellationSite.Call, caller, long.MaxValue);
}
