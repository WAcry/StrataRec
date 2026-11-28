using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

public sealed class FetchSpanTests
{
    [Theory]
    [InlineData(true, ListenerStage.None)]
    [InlineData(true, ListenerStage.Start)]
    [InlineData(true, ListenerStage.Stop)]
    [InlineData(false, ListenerStage.None)]
    [InlineData(false, ListenerStage.Start)]
    [InlineData(false, ListenerStage.Stop)]
    public async Task EachChunkOfAFetch_IsAChildOfTheFetchesSpan(bool synchronously, ListenerStage stage)
    {
        var connector = synchronously
            ? ScriptedConnector.Values((key, _) => key * 1.5)
            : new ScriptedConnector(async (keys, fields, writer, _) =>
            {
                await Task.Yield();
                ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 1.5);
                return FetchStatus.Ok;
            });
        var rig = new FeatureTestRig().Source("chunked_store", connector, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "chunked_store");
        rig.Build();
        using var flow = new TracedFlow(stage, "fetch chunked_store");
        var request = rig.NewRequest([1, 2, 3]);

        var result = await request.EnsureAsync(score);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(4.5, request.Value(score, 2).AsFloat64());
        var chunks = flow.Named("fetch chunked_store");
        Assert.Equal(3, chunks.Length);
        Assert.All(chunks, chunk => Assert.Same(flow.Request, chunk.Parent));
        Assert.Equal(stage == ListenerStage.None ? 0 : 1, flow.Thrown);
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release());
    }

    [Fact]
    public async Task EachChunkOfADistributedCacheRead_IsAChildOfTheFetchesSpan()
    {
        var store = new FakeCacheStore { BeforeGet = static async _ => await Task.Yield() };
        var connector = ScriptedConnector.Values((key, _) => key * 1.5);
        var rig = new FeatureTestRig().CacheSource("cached_store", connector, distributed: store, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "cached_store");
        rig.Build();
        using var flow = new TracedFlow();
        var request = rig.NewRequest([1, 2, 3]);

        var result = await request.EnsureAsync(score);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(3, store.Gets.Count);
        var reads = flow.Named("cache cached_store");
        Assert.Equal(3, reads.Length);
        Assert.All(reads, read => Assert.Same(flow.Request, read.Parent));
        Assert.All(flow.Named("fetch cached_store"), fetch => Assert.Same(flow.Request, fetch.Parent));
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release());
    }
}
