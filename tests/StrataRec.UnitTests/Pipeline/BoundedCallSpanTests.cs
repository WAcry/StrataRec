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

        var firstResult = await BoundedCall.RunAsync<int, Traced>(first, budget, memory, new IoTarget(IoRoles.Recall, "first"), null, "req", null);
        var secondResult = await BoundedCall.RunAsync<int, Traced>(second, budget, memory, new IoTarget(IoRoles.Recall, "second"), null, "req", null);

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

    /// <summary>
    /// Two bounded calls in one traced flow whose clients answer later. A pending call is completed on the flow that awaits
    /// it, so its span stayed current on the caller's flow and the second call was a child of the first.
    /// </summary>
    [Fact]
    public async Task TwoCallsWhoseClientsAnswerLater_AreBothUnderTheRequest()
    {
        using var flow = new TracedFlow();
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (600L * Stopwatch.Frequency));
        var first = new Traced(new List<Activity?>(), later: true);
        var second = new Traced(new List<Activity?>(), later: true);

        var firstResult = await BoundedCall.RunAsync<int, Traced>(first, budget, memory, new IoTarget(IoRoles.Recall, "first"), null, "req", null);
        var secondResult = await BoundedCall.RunAsync<int, Traced>(second, budget, memory, new IoTarget(IoRoles.Recall, "second"), null, "req", null);

        Assert.Equal((42, 42), (firstResult.Value, secondResult.Value));
        var firstSpan = Assert.Single(flow.Named("recall first"));
        var secondSpan = Assert.Single(flow.Named("recall second"));
        Assert.Same(firstSpan, Assert.Single(first.Sent)); // each client ran under its own span
        Assert.Same(secondSpan, Assert.Single(second.Sent));
        Assert.Same(flow.Request, secondSpan.Parent);
        Assert.Same(flow.Request, Activity.Current);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, TimeSpan.FromSeconds(20)));
    }

    /// <summary>Answers 42, at once or after a yield, and keeps the current activity each send saw.</summary>
    private readonly struct Traced(List<Activity?> sent, bool later = false) : IBoundedCall<int>
    {
        public List<Activity?> Sent { get; } = sent;

        public ValueTask<int> Send(CancellationToken token)
        {
            Sent.Add(Activity.Current);
            return later ? new ValueTask<int>(AnswerLaterAsync()) : new ValueTask<int>(42);
        }

        private static async Task<int> AnswerLaterAsync()
        {
            await Task.Yield();
            return 42;
        }

        public bool Succeeded(int value, out string? error)
        {
            error = null;
            return true;
        }

        public int Keys => -1;

        public void Ended(in CallResult<int> result, TimeSpan duration)
        {
        }
    }
}
