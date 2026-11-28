using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

public sealed class ServiceCallTests
{
    [Fact]
    public async Task ACallNamingAnotherServiceThanItsBinding_OrAboutItemsAskingForItems_IsRejected()
    {
        var context = new OperatorContext(null!, "n", Effects.ColumnWriter, new AttemptBudget(CancellationToken.None, long.MaxValue));
        var service = new BoundService("s", new Unused(), IoRoles.Recall, null);
        var memory = new RequestMemory();

        var renamed = await Assert.ThrowsAsync<ArgumentException>(() => context.CallAsync(service, new ServiceCall { Service = "alias" }, new ServiceResponse(memory)));
        Assert.StartsWith("The call names service 'alias', but it is sent to the binding 's'", renamed.Message, StringComparison.Ordinal);

        var both = new ServiceCall { Service = "s", Items = new[] { EntityKey.FromInt64(1) }, BatchSize = 1, Limit = 2 };
        var mixed = await Assert.ThrowsAsync<ArgumentException>(() => context.CallAsync(service, both, new ServiceResponse(memory, 2)));
        Assert.StartsWith("A call about items adds none", mixed.Message, StringComparison.Ordinal);

        var twice = new ServiceCall { Service = "s", Outputs = new[] { "a", "b", "a" } };
        var duplicated = await Assert.ThrowsAsync<ArgumentException>(() => context.CallAsync(service, twice, new ServiceResponse(memory)));
        Assert.StartsWith("The call names output 'a' twice", duplicated.Message, StringComparison.Ordinal);

        foreach (var count in new[] { 2, 300 })
        {
            var inputs = Enumerable.Range(0, count).Select(i => NamedInput.FromFloat32(i == count - 1 ? "f0" : "f" + i, [], new[] { (float)i })).ToArray();
            var named = new ServiceCall { Service = "s", Inputs = inputs };
            var again = await Assert.ThrowsAsync<ArgumentException>(() => context.CallAsync(service, named, new ServiceResponse(memory)));
            Assert.StartsWith("The call names input 'f0' twice", again.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AbandoningACall_DoesNotWaitForAWriteInProgress()
    {
        // The client is inside GetValues, renting from a host's pool that blocks: the timeout that abandons the call
        // must not wait for it.
        var cancellation = TestContext.Current.CancellationToken;
        using var renting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var response = new ServiceResponse(new RequestMemory(new BlockingPool(renting, release)));
        var writer = Task.Run(() => response.GetValues<float>("p", 4), cancellation);
        Assert.True(renting.Wait(TimeSpan.FromSeconds(30), cancellation));

        var abandon = Task.Run(response.Abandon, cancellation);
        var abandoned = await Task.WhenAny(abandon, Task.Delay(TimeSpan.FromSeconds(2), cancellation)) == abandon;

        release.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        Assert.True(abandoned, "abandoning the call waited for the client's write");
        Assert.False(response.TryGetValues("p", out _));
    }

    [Fact]
    public void ATensorWithANegativeDimension_OrFewerValuesThanItsShape_IsNotBuilt_ALongerBufferIs()
    {
        Assert.Throws<ArgumentException>(() => NamedInput.FromFloat32("x", [3], new float[2]));
        Assert.Throws<ArgumentException>(() => NamedInput.FromFloat32("x", [-1], new float[2]));
        Assert.Throws<ArgumentException>(() => NamedInput.FromInt64("x", [2, 2], new long[3]));
        Assert.Throws<ArgumentException>(() => NamedInput.FromString("x", [], ReadOnlyMemory<string>.Empty));

        Assert.Equal([2], NamedInput.FromFloat32("x", [2], new float[5]).Shape); // a rented buffer: the shape says how many
        Assert.Equal(2, NamedInput.FromFloat32("x", [2], new float[5]).Float32.Length);
        Assert.Throws<ArgumentException>(() => NamedInput.FromFloat32("x", [int.MaxValue, int.MaxValue, 3], ReadOnlyMemory<float>.Empty));
        Assert.Equal(0, NamedInput.FromFloat32("x", [0, int.MaxValue], ReadOnlyMemory<float>.Empty).Float32.Length);
        Assert.Equal([-1], NamedInput.FromMissing("x", TensorElementType.Float32, [-1]).Shape);
    }

    private sealed class Unused : IServiceClient
    {
        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not called");
    }

    private sealed class BlockingPool(ManualResetEventSlim renting, ManualResetEventSlim release) : IBufferPool
    {
        public T[] Rent<T>(int minimumLength)
        {
            renting.Set();
            release.Wait();
            return new T[minimumLength];
        }

        public void Return<T>(T[] array, bool clearArray)
        {
        }
    }
}
