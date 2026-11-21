namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// What a call answered from a service's in-process cache allocates: per call, never per item. At 10 and 1,000 items a
/// keyed hit took the same 1.5 KB (with the rig's request state and response), about what one call sent to the service
/// takes before the service's own allocations; it completes synchronously, so this thread's count is the call's.
/// </summary>
public sealed class ServiceCacheAllocationTests
{
    [Fact]
    public async Task ACallAnsweredFromTheLocalTier_AllocatesNothingPerItem()
    {
        var few = await AllocatedPerHit(10);
        var many = await AllocatedPerHit(1_000);

        Assert.True(many - few < 128, $"a hit of 10 items allocated {few} B, of 1,000 items {many} B");
    }

    private static async Task<long> AllocatedPerHit(int items)
    {
        var rig = new ServiceCacheRig { ReleaseEachCall = true };
        var ids = Enumerable.Range(1, items).Select(i => (long)i).ToArray();
        var call = ServiceCacheRig.Items(ids, ids.Select(i => i / 10f).ToArray());
        await rig.CallAsync(call);
        rig.Apply();
        for (var i = 0; i < 50; i++)
        {
            await rig.CallAsync(call); // warm: the pool holds the buffers a call rents
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            await rig.CallAsync(call);
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        Assert.Single(rig.Service.Calls); // every call after the first was a hit
        return allocated;
    }
}
