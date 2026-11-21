using StrataRec.Core.Threading;
using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Pipeline.Operators;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// Review 85: the host's <see cref="Activity.CurrentChanged"/> handler runs whenever a call gives the flow its activity
/// back. One that threw when the call's span stopped being current went up through <see cref="IoCall.Sent"/>: the call
/// site had sent the call and neither completed it nor let go of the request's memory, and the caller got the handler's
/// exception for a call that had answered.
/// </summary>
public sealed class BoundedCallCurrentChangedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandlerThatThrowsWhenTheFlowGetsItsActivityBack_LosesNothingOfTheCall(bool pending)
    {
        var target = "changed-" + Guid.NewGuid().ToString("N");
        using var flow = new TracedFlow();
        var thrown = 0;
        void Changed(object? sender, ActivityChangedEventArgs args)
        {
            if (ReferenceEquals(args.Current, flow.Request) && args.Previous?.OperationName == "recall " + target)
            {
                Interlocked.Increment(ref thrown);
                throw new InvalidOperationException("a host's CurrentChanged handler failed");
            }
        }

        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, Stopwatch.GetTimestamp() + (600L * Stopwatch.Frequency));
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(pending ? answer.Task : Task.FromResult(42), new List<string>());
        Activity.CurrentChanged += Changed;
        CallResult<int> result;
        try
        {
            var running = BoundedCall.RunAsync<int, Client>(client, budget, memory, new IoTarget(IoRoles.Recall, target), CallDeadline.None);
            Assert.Same(flow.Request, Activity.Current);
            answer.TrySetResult(42);
            result = await running;
        }
        finally
        {
            Activity.CurrentChanged -= Changed;
        }

        Assert.Equal(pending ? 2 : 1, thrown); // a pending call's span is also stopped in the call site's context (review 87)
        Assert.Equal((IoOutcome.Ok, 42), (result.Outcome, result.Value));
        Assert.True(Assert.Single(flow.Named("recall " + target)).IsStopped);
        Assert.Equal(new[] { "sent", "ended" }, client.Observed);
        budget.Release();
        memory.Release();
        Assert.True(SpinWait.SpinUntil(() => memory.IsReturned, TimeSpan.FromSeconds(30))); // once the client's task has run its continuations
    }

    private readonly struct Client(Task<int> answer, List<string> observed) : IBoundedCall<int>
    {
        public List<string> Observed => observed;

        public ValueTask<int> Send(CancellationToken token)
        {
            observed.Add("sent");
            return new(answer);
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
            lock (observed)
            {
                observed.Add("ended");
            }
        }
    }
}
