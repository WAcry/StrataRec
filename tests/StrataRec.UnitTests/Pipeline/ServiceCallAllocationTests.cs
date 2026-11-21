using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// What a node's service call costs on the path itself, before the service's own allocations: a call answered at once
/// allocates only the task it returns, one task for the node and the feature source path alike (review of IO step 5: the
/// node's entry awaited the shared path, a second task per call).
/// </summary>
[Collection(AllocationMeasurement.Name)]
public sealed class ServiceCallAllocationTests
{
    private static readonly ServiceCall Call = new() { Service = "x" };

    [Fact]
    public void ACallAnsweredAtOnce_AllocatesOnlyItsTask()
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, long.MaxValue);
        try
        {
            var context = Context(memory, budget);
            var bound = new BoundService("x", new Answering(), IoRoles.Model, null);
            long bytes = 0;
            for (var i = -100; i < 500; i++)
            {
                var response = new ServiceResponse(memory);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var call = context.CallAsync(bound, Call, response);
                var after = GC.GetAllocatedBytesForCurrentThread();
                bytes += i >= 0 ? after - before : 0;
                Assert.True(call.IsCompletedSuccessfully);
            }

            Assert.True(bytes / 500 <= 100, $"{bytes / 500} B per call");
        }
        finally
        {
            budget.Release();
            memory.Release();
        }
    }

    /// <summary>A validation error is still a faulted task, not a throw.</summary>
    [Fact]
    public void AWrongCall_IsAFaultedTask()
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, long.MaxValue);
        try
        {
            var context = Context(memory, budget);
            var bound = new BoundService("x", new Answering(), IoRoles.Model, null);

            var call = context.CallAsync(bound, new ServiceCall { Service = "y" }, new ServiceResponse(memory));

            Assert.True(call.IsFaulted);
            Assert.IsType<ArgumentException>(call.Exception!.InnerException);
        }
        finally
        {
            budget.Release();
            memory.Release();
        }
    }

    /// <summary>
    /// A cancellation while the call is checked is a cancelled task that throws it, as an async method's (reviews of the
    /// step 5 fixes and of da5cfe6: the task was faulted, then cancelled with another exception).
    /// </summary>
    [Fact]
    public async Task ACancellationWhileTheCallIsChecked_IsACancelledTask_ThatThrowsIt()
    {
        var memory = new RequestMemory();
        var budget = new AttemptBudget(CancellationToken.None, long.MaxValue);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            var context = Context(memory, budget);
            var bound = new BoundService("x", new Answering(), IoRoles.Model, null);
            var outputs = new CancellingNames(cancelled.Token);

            var call = context.CallAsync(bound, new ServiceCall { Service = "x", Outputs = outputs.Memory }, new ServiceResponse(memory));

            Assert.True(call.IsCanceled);
            Assert.Same(outputs.Thrown, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call));
        }
        finally
        {
            budget.Release();
            memory.Release();
        }
    }

    /// <summary>Output names whose memory throws a cancellation when read (after the memory over it is made).</summary>
    private sealed class CancellingNames(CancellationToken token) : System.Buffers.MemoryManager<string>
    {
        private readonly string[] _names = ["p"];
        private bool _made;

        public override Span<string> GetSpan()
        {
            if (!_made)
            {
                _made = true; // Memory reads its length
                return _names;
            }

            throw Thrown = new OperationCanceledException("the names were cancelled", token);
        }

        public OperationCanceledException? Thrown { get; private set; }

        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private static OperatorContext Context(RequestMemory memory, AttemptBudget budget) => new(new RequestState
    {
        Plan = null!,
        RequestId = "r",
        Inputs = new RequestInputs(),
        Seed = 1,
        Memory = memory,
        Table = null!,
        Bindings = null!,
        Context = [],
        Params = [],
        Features = null!,
        Counters = null!,
        DeadlineTimestamp = long.MaxValue,
        Arms = default,
        Variants = [],
    }, "n", Effects.ColumnWriter, budget);

    private sealed class Answering : IServiceClient
    {
        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse writer, CancellationToken token) => ValueTask.FromResult(ServiceStatus.Ok);
    }
}
