using System.Collections;
using System.Diagnostics;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// <see cref="OperatorContext.WhenEachReadyAsync"/> on reads with nothing to wait for, which never reach the request's
/// state: what it allocates, and how far it reads the operator's list once the bound passed.
/// </summary>
[Collection(AllocationMeasurement.Name)]
public sealed class ReadinessWaitTests
{
    private static readonly Action<int, FeatureFillResult> Ignore = static (_, _) => { };

    /// <summary>Review 93: no copy of the list is made for an operator that waits for nothing (f450c03 made one, 24 B and up).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ReadsWithNothingToWaitFor_AllocateNothing(int count)
    {
        var budget = new AttemptBudget(CancellationToken.None, long.MaxValue);
        var context = new OperatorContext(null!, "n", Effects.ColumnWriter, budget);
        var reads = new AwaitedReads[count];
        Array.Fill(reads, AwaitedReads.None);
        for (var i = 0; i < 1_000; i++)
        {
            await context.WhenEachReadyAsync(reads, Ignore);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            await context.WhenEachReadyAsync(reads, Ignore);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        budget.Release();
    }

    /// <summary>
    /// Review 93: once the bound passed while the operator's list was read (its indexer for the second read ran past the
    /// deadline or cancelled the caller), the rest of the list is not read: the third read's indexer, which throws, would
    /// have been the node's failure instead of the timeout or the cancellation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheBoundPassingWhileTheListIsRead_StopsReadingIt(bool callerCancels)
    {
        using var caller = new CancellationTokenSource();
        var budget = new AttemptBudget(caller.Token, callerCancels ? long.MaxValue : Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 50));
        var context = new OperatorContext(null!, "n", Effects.ColumnWriter, budget);
        var settled = new List<int>();
        var reads = new ListReads(index =>
        {
            switch (index)
            {
                case 1 when callerCancels:
                    caller.Cancel();
                    break;
                case 1:
                    Thread.Sleep(100); // past the 20 ms deadline
                    break;
                case 2:
                    throw new InvalidOperationException("the third read was asked for");
            }
        });

        var error = await Record.ExceptionAsync(async () => await context.WhenEachReadyAsync(reads, (i, _) => settled.Add(i)));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal([0, 1], reads.Asked);
        Assert.Equal([0], settled); // the second read was asked for, but the bound had passed by then
        budget.Release();
    }

    /// <summary>
    /// Review 95: entered with the bound already passed, the wait calls none of the operator's code, not even its list's
    /// Count, whose exception would otherwise stand for the timeout or the cancellation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnteredWithTheBoundPassed_TheListIsNotAskedForItsCount(bool callerCancels)
    {
        using var caller = new CancellationTokenSource();
        if (callerCancels)
        {
            caller.Cancel();
        }

        var budget = new AttemptBudget(caller.Token, callerCancels ? long.MaxValue : Stopwatch.GetTimestamp() - 1);
        var context = new OperatorContext(null!, "n", Effects.ColumnWriter, budget);
        var reads = new ListReads(static _ => { }, countThrows: true);

        var error = await Record.ExceptionAsync(async () => await context.WhenEachReadyAsync(reads, Ignore));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(reads.CountAsked);
        budget.Release();
    }

    /// <summary>A null in the list is the caller's error, said with its index.</summary>
    [Fact]
    public async Task ANullRead_IsRejectedWithItsIndex()
    {
        var budget = new AttemptBudget(CancellationToken.None, long.MaxValue);
        var context = new OperatorContext(null!, "n", Effects.ColumnWriter, budget);

        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await context.WhenEachReadyAsync([AwaitedReads.None, null!], Ignore));

        Assert.Equal("reads", error.ParamName);
        Assert.Contains("reads[1] is null", error.Message, StringComparison.Ordinal);
        budget.Release();
    }

    /// <summary>
    /// Three reads with nothing to wait for, running <paramref name="onAsked"/> each time one is asked for; its Count throws
    /// when <paramref name="countThrows"/>.
    /// </summary>
    private sealed class ListReads(Action<int> onAsked, bool countThrows = false) : IReadOnlyList<AwaitedReads>
    {
        public List<int> Asked { get; } = new();

        public bool CountAsked { get; private set; }

        public int Count
        {
            get
            {
                CountAsked = true;
                return countThrows ? throw new InvalidOperationException("Count was asked for") : 3;
            }
        }

        public AwaitedReads this[int index]
        {
            get
            {
                Asked.Add(index);
                onAsked(index);
                return AwaitedReads.None;
            }
        }

        public IEnumerator<AwaitedReads> GetEnumerator()
        {
            for (var i = 0; i < 3; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
