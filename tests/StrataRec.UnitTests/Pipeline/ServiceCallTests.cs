using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>What the call primitive and its types reject or guarantee before and around a client's call (review of IO step 2).</summary>
public sealed class ServiceCallTests
{
    [Fact]
    public async Task ACallNamingAnotherServiceThanItsBinding_OrAboutItemsAskingForItems_IsRejected()
    {
        var context = new OperatorContext(null!, "n", Effects.ColumnWriter, new AttemptBudget(CancellationToken.None, long.MaxValue));
        var service = new BoundService("s", new Unused(), IoRoles.Recall, null);
        var memory = new RequestMemory();

        // Live it called the binding's client and recorded the binding's name; a replay looked for the other one.
        var renamed = await Assert.ThrowsAsync<ArgumentException>(() => context.CallAsync(service, new ServiceCall { Service = "alias" }, new ServiceResponse(memory)));
        Assert.StartsWith("The call names service 'alias', but it is sent to the binding 's'", renamed.Message, StringComparison.Ordinal);

        // Items added to a call about items belong to the whole call: a replay that synthesized it dropped them unseen.
        var both = new ServiceCall { Service = "s", Items = new[] { EntityKey.FromInt64(1) }, BatchSize = 1, Limit = 2 };
        var mixed = await Assert.ThrowsAsync<ArgumentException>(() => context.CallAsync(service, both, new ServiceResponse(memory, 2)));
        Assert.StartsWith("A call about items adds none", mixed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbandoningACall_DoesNotWaitForAWriteInProgress()
    {
        // The client is inside GetValues, renting from a host's pool that blocks: the timeout that abandons the call must not
        // wait for it (Abandon took the lock the write holds).
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
        Assert.False(response.TryGetValues("p", out _)); // the write completed into a buffer nobody reads
    }

    [Fact]
    public void ATensorWithANegativeDimension_OrFewerValuesThanItsShape_IsNotBuilt_ALongerBufferIs()
    {
        Assert.Throws<ArgumentException>(() => NamedInput.FromFloat32("x", [3], new float[2]));
        Assert.Throws<ArgumentException>(() => NamedInput.FromFloat32("x", [-1], new float[2]));
        Assert.Throws<ArgumentException>(() => NamedInput.FromInt64("x", [2, 2], new long[3]));
        Assert.Throws<ArgumentException>(() => NamedInput.FromString("x", [], ReadOnlyMemory<string>.Empty)); // a scalar is one value

        Assert.Equal([2], NamedInput.FromFloat32("x", [2], new float[5]).Shape); // a rented buffer: the shape says how many
        Assert.Equal([-1], NamedInput.FromMissing("x", TensorElementType.Float32, [-1]).Shape); // a missing list of any length
    }

    private sealed class Unused : IServiceClient
    {
        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not called");
    }

    /// <summary>A host's pool whose rent blocks until released.</summary>
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
