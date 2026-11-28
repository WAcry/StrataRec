using StrataRec.Core.Types;
using static StrataRec.UnitTests.Features.ChainedContextKeyTests;

namespace StrataRec.UnitTests.Features;

[Collection(AllocationMeasurement.Name)]
public sealed class ContextReadyAllocationTests
{
    [Fact]
    public async Task AskingForFeaturesStoredAlready_AllocatesNothing()
    {
        var rig = new FeatureTestRig().Source("identity_store", Answering(UserIdOf));
        var userKey = rig.RequestInput("user_key");
        var id = rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        rig.Build();
        var request = Request(rig, userKey, "k-7");
        try
        {
            request.Fetcher.StartContextFetch([id], CancellationToken.None);
            Assert.True(request.Fetcher.ContextStoredTask(id).IsCompletedSuccessfully);
            int[] features = [id];
            for (var i = 0; i < 100; i++)
            {
                Assert.Equal(-1, await request.Fetcher.StoredOrFailedAsync(features));
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                await request.Fetcher.StoredOrFailedAsync(features);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            request.Memory.Release();
        }
    }
}
