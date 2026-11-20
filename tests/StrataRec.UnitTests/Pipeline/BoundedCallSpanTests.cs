using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Pipeline.Operators;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// Review 83: two bounded calls in one traced flow, the first answering at once. A host's activity listener that threw
/// when the first call's span started or stopped left that span current: both calls answered, and the second call's
/// client was sent under the first call's span, not the request's.
/// </summary>
public sealed class BoundedCallSpanTests
{
    [Theory]
    [InlineData(ListenerStage.Start)]
    [InlineData(ListenerStage.Stop)]
    public async Task AListenerThatThrowsOnTheFirstCall_LeavesTheSecondUnderTheRequest(ListenerStage stage)
    {
        using var flow = new TracedFlow(stage, "recall first");
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (600L * Stopwatch.Frequency));
        var first = new Traced(new List<Activity?>());
        var second = new Traced(new List<Activity?>());

        var firstResult = await BoundedCall.RunAsync<int, Traced>(first, budget, memory, new IoTarget(IoKind.CandidateSource, "first"), null, "req", null);
        var secondResult = await BoundedCall.RunAsync<int, Traced>(second, budget, memory, new IoTarget(IoKind.CandidateSource, "second"), null, "req", null);

        Assert.Equal((IoOutcome.Ok, IoOutcome.Ok), (firstResult.Outcome, secondResult.Outcome));
        Assert.Equal(1, flow.Thrown);
        var secondSpan = Assert.Single(flow.Named("recall second"));
        Assert.Same(secondSpan, Assert.Single(second.Sent)); // its client ran under its span
        Assert.Same(flow.Request, secondSpan.Parent);
        Assert.Same(flow.Request, Activity.Current);
        budget.Release();
        memory.Release();
        Assert.True(memory.IsReturned);
    }

    /// <summary>Answers 42 at once and keeps the current activity each send saw.</summary>
    private readonly struct Traced(List<Activity?> sent) : IBoundedCall<int>
    {
        public List<Activity?> Sent { get; } = sent;

        public ValueTask<int> Send(CancellationToken token)
        {
            Sent.Add(Activity.Current);
            return new ValueTask<int>(42);
        }

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
