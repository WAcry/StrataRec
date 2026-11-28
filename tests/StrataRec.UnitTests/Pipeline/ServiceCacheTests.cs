using StrataRec.Core.Services;
using StrataRec.UnitTests.Features;
using static StrataRec.UnitTests.Pipeline.ServiceCacheRig;

namespace StrataRec.UnitTests.Pipeline;

public sealed class ServiceCacheTests
{
    [Fact]
    public async Task ACallAboutNoItems_IsAnsweredWhole_FromTheCache_ItemsTextsTensorsAndTruncation()
    {
        var rig = new ServiceCacheRig();
        rig.Service.Truncated = true;
        var (first, missed) = await rig.CallAsync(Query(1, limit: 3, "answer", "p"));
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(1, rig.Apply());

        rig.Service.Offset = 100;
        var (second, hit) = await rig.CallAsync(Query(1, limit: 3, "answer", "p"));

        Assert.Single(rig.Service.Calls);
        Assert.True(second.Succeeded, second.Error);
        Assert.True(second.Truncated);
        Assert.Equal(Ids(missed), Ids(hit));
        Assert.Equal(new long[] { 1, 2, 3 }, Ids(hit));
        Assert.Equal(missed.Scores.ToArray(), hit.Scores.ToArray());
        Assert.True(hit.TryGetText("answer", out var text));
        Assert.Equal("a0", text);
        Assert.Equal(new float[] { 0, 1 }, Floats(hit, "p"));
    }

    /// <summary>
    /// The clock moves between calls: a write replaces a record only if it is newer.
    /// </summary>
    [Fact]
    public async Task FewerItems_AreTheCachedFirst_MoreGoToTheService_UnlessItHadNoMore()
    {
        var rig = new ServiceCacheRig();
        await rig.CallAsync(Query(1, limit: 3));
        rig.Apply();
        rig.Clock.AdvanceMs(1);

        var (_, fewer) = await rig.CallAsync(Query(1, limit: 2));
        Assert.Equal(new long[] { 1, 2 }, Ids(fewer));
        Assert.Single(rig.Service.Calls);

        var (_, more) = await rig.CallAsync(Query(1, limit: 5));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Ids(more));
        Assert.Equal(2, rig.Service.Calls.Count);
        rig.Apply();
        rig.Clock.AdvanceMs(1);

        await rig.CallAsync(Query(1, limit: 10));
        Assert.Equal(3, rig.Service.Calls.Count);
        rig.Apply();
        var (_, all) = await rig.CallAsync(Query(1, limit: 20));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Ids(all));
        Assert.Equal(3, rig.Service.Calls.Count);
    }

    [Fact]
    public async Task TheInputsAndTheOutputsRead_AreTheKey()
    {
        var rig = new ServiceCacheRig();
        await rig.CallAsync(Query(1, limit: 3, "answer"));
        rig.Apply();

        await rig.CallAsync(Query(2, limit: 3, "answer"));
        await rig.CallAsync(Query(1, limit: 3, "answer", "p"));

        Assert.Equal(3, rig.Service.Calls.Count);
    }

    [Fact]
    public async Task ACallAboutItems_SendsTheServiceOnlyTheItemsTheCacheMisses_WithTheirRows()
    {
        var rig = new ServiceCacheRig();
        await rig.CallAsync(Items([1, 2, 3], [0.1f, 0.2f, 0.3f]));
        Assert.Equal(1, rig.Apply());

        rig.Service.Offset = 100;
        var (status, response) = await rig.CallAsync(Items([3, 4, 2], [0.3f, 0.4f, 0.2f]));

        Assert.True(status.Succeeded, status.Error);
        var sent = rig.Service.Calls[^1];
        Assert.Equal(1, sent.Batch);
        Assert.Equal(new long[] { 4 }, sent.Items);
        Assert.Equal(new[] { 0.4f }, sent.X);
        Assert.Equal(new[] { 3f, 104f, 2f }, Floats(response, "p"));

        rig.Apply();
        var (_, cached) = await rig.CallAsync(Items([4, 1], [0.4f, 0.1f]));
        Assert.Equal(2, rig.Service.Calls.Count);
        Assert.Equal(new[] { 104f, 1f }, Floats(cached, "p"));
    }

    [Fact]
    public async Task AnItemWithAnotherValueOfItsInput_IsAMiss()
    {
        var rig = new ServiceCacheRig();
        await rig.CallAsync(Items([1, 2], [0.1f, 0.2f]));
        rig.Apply();

        var (_, response) = await rig.CallAsync(Items([1, 2], [0.1f, 0.5f]));

        Assert.Equal(new long[] { 2 }, rig.Service.Calls[^1].Items);
        Assert.Equal(new[] { 0.5f }, rig.Service.Calls[^1].X);
        Assert.Equal(new[] { 1f, 5f }, Floats(response, "p"));
    }

    [Fact]
    public async Task OutputsOfSeveralValuesAnItem_AndOfOtherTypes_ArePutBackRowByRow()
    {
        var rig = new ServiceCacheRig();
        rig.Service.Width = 2;
        await rig.CallAsync(Items([1, 2], [0.1f, 0.2f], "p", "q"));
        rig.Apply();

        var (_, response) = await rig.CallAsync(Items([2, 3, 1], [0.2f, 0.3f, 0.1f], "p", "q"));

        Assert.Equal(new long[] { 3 }, rig.Service.Calls[^1].Items);
        Assert.Equal(new[] { 2f, 3f, 3f, 4f, 1f, 2f }, Floats(response, "p"));
        Assert.True(response.TryGetValues("q", out var q));
        Assert.Equal(new long[] { 2, 4, 3, 6, 1, 2 }, q.Int64.ToArray());
    }

    [Fact]
    public async Task ACallAboutItemsThatAnswersAText_IsNotCached()
    {
        var rig = new ServiceCacheRig();
        rig.Service.WriteText = true;
        var (_, response) = await rig.CallAsync(Items([1], [0.1f], "p", "note"));

        Assert.True(response.TryGetText("note", out _));
        Assert.Equal(0, rig.Apply());
    }

    [Fact]
    public async Task CachedAnswersOfAnotherWidth_ThanTheServicesNow_FailTheCall()
    {
        var rig = new ServiceCacheRig();
        await rig.CallAsync(Items([1], [0.1f]));
        rig.Apply();

        rig.Service.Width = 2;
        var (status, _) = await rig.CallAsync(Items([1, 2], [0.1f, 0.2f]));

        Assert.False(status.Succeeded);
        Assert.Contains("cached answers of output 'p' are float32 with 1 value(s) an item", status.Error, StringComparison.Ordinal);
        Assert.Contains("without a new version", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerPastItsTtl_IsAMiss()
    {
        var rig = new ServiceCacheRig(ttlMs: 1_000);
        await rig.CallAsync(Query(1, limit: 2));
        rig.Apply();

        rig.Clock.AdvanceMs(2_000);
        await rig.CallAsync(Query(1, limit: 2));

        Assert.Equal(2, rig.Service.Calls.Count);
    }

    [Fact]
    public async Task AStaleAnswer_ServesAFailedCall_WithinStaleIfError_AndDegradesTheNode()
    {
        var rig = new ServiceCacheRig(ttlMs: 1_000, staleIfErrorMs: 5_000);
        await rig.CallAsync(Items([1, 2], [0.1f, 0.2f]));
        rig.Apply();
        rig.Clock.AdvanceMs(2_000);
        rig.Service.Fail = true;

        var (stale, response) = await rig.CallAsync(Items([2, 1], [0.2f, 0.1f]));
        Assert.True(stale.Succeeded, stale.Error);
        Assert.Equal(new[] { 2f, 1f }, Floats(response, "p"));
        Assert.Contains("m is down", rig.Last!.DegradedReason, StringComparison.Ordinal);

        var (missing, _) = await rig.CallAsync(Items([1, 3], [0.1f, 0.3f]));
        Assert.Equal(ServiceStatus.Failed("m is down"), missing);

        rig.Clock.AdvanceMs(10_000);
        var (expired, _) = await rig.CallAsync(Items([1], [0.1f]));
        Assert.Equal(ServiceStatus.Failed("m is down"), expired);
    }

    [Fact]
    public async Task TheDistributedTier_AnswersWhatTheLocalOneMisses_AndFillsIt()
    {
        var store = new FakeCacheStore();
        var writer = new ServiceCacheRig(store: store);
        await writer.CallAsync(Items([1, 2], [0.1f, 0.2f]));
        await store.WhenWrittenAsync(2).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var reader = new ServiceCacheRig(store: store);
        reader.Service.Offset = 100;
        var (status, response) = await reader.CallAsync(Items([2, 3, 1], [0.2f, 0.3f, 0.1f]));

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new long[] { 3 }, Assert.Single(reader.Service.Calls).Items);
        Assert.Equal(new[] { 2f, 103f, 1f }, Floats(response, "p"));
        Assert.Equal(2, reader.Apply()); // the store's two, then the service's one
        var (_, local) = await reader.CallAsync(Items([1, 2, 3], [0.1f, 0.2f, 0.3f]));
        Assert.Single(reader.Service.Calls);
        Assert.Equal(new[] { 1f, 2f, 103f }, Floats(local, "p"));
    }

    [Fact]
    public async Task AFailedDistributedRead_LeavesItsKeysToTheService()
    {
        var store = new FakeCacheStore { BeforeGet = _ => throw new InvalidOperationException("store down") };
        var rig = new ServiceCacheRig(store: store);

        var (status, response) = await rig.CallAsync(Items([1], [0.1f]));

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 1f }, Floats(response, "p"));
        Assert.Single(rig.Service.Calls);
    }
}
