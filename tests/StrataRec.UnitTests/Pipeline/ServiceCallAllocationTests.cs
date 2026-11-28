using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.UnitTests.Pipeline;

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

    [Fact]
    public void ARebatchedReplay_IndexesTheRecordedRowsOnce()
    {
        var run = RunReplayTests.Run();
        for (var c = 0; c < 8; c++)
        {
            var keys = Enumerable.Range((c * 128) + 1, 128).Select(k => (long)k).ToArray();
            run.ServiceCalls.Add(RunReplayTests.ModelCallOf(c, Batch(keys), keys, keys.Select(k => (float)k).ToArray()));
        }

        var replay = RunReplay.Create(run, "/runs/0");
        var calls = Enumerable.Range(0, 32).Select(b =>
        {
            var keys = Enumerable.Range((b * 32) + 1, 32).Select(k => (long)k).ToArray();
            return (Call: Batch(keys), Keys: keys.Select(EntityKey.FromInt64).ToArray(), Writer: new RunReplayTests.ArrayOutputWriter());
        }).ToArray();
        replay.InvokeModel(calls[0].Call, calls[0].Writer, calls[0].Keys); // the first builds the index

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var b = 1; b < calls.Length; b++)
        {
            Assert.True(replay.InvokeModel(calls[b].Call, calls[b].Writer, calls[b].Keys).Succeeded);
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (calls.Length - 1);
        Assert.Empty(replay.Gaps);
        Assert.True(perCall < 48 * 1024, $"{perCall} B a call of 32 items");

        static ServiceCall Batch(long[] keys) => RunReplayTests.Call(keys.Length, NamedInput.FromFloat32("x", [keys.Length], keys.Select(k => (float)k).ToArray()));
    }

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
