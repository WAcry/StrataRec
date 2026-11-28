using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.UnitTests.Features.Cache;

namespace StrataRec.UnitTests.Features;

[Collection(MetricMeasurement.Name)]
public sealed class CallSiteListenerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFetchWhoseListenerThrows_IsRecorded_AndFillsItsRows(bool synchronously)
    {
        var connector = synchronously
            ? ScriptedConnector.Values((key, _) => key * 1.5)
            : new ScriptedConnector(async (keys, fields, writer, _) =>
            {
                await Task.Yield();
                ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 1.5);
                return FetchStatus.Ok;
            });
        var rig = new FeatureTestRig().Source("listener_store", connector);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "listener_store");
        rig.Build();
        using var listener = MeasurementHook.Throwing(StrataRecMetrics.Names.IoDuration, (StrataRecTags.IoTarget, "listener_store"));
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest([1, 2], hook: hook);

        var result = await request.EnsureAsync(score);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(3.0, request.Value(score, 1).AsFloat64());
        var call = Assert.Single(hook.Recorded);
        Assert.Equal(IoOutcome.Ok, call.Outcome);
        Assert.Equal(new double?[] { 1.5, 3.0 }, call.Values);
        Assert.Equal(1, listener.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReadServedFromATable_IsRecordedBeforeItsValuesAreCounted(bool cache)
    {
        const string source = "listener_table";
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = cache
            ? new FeatureTestRig().CacheSource(source, connector)
            : new FeatureTestRig().LocalSource(source, writer =>
            {
                var record = writer.Begin(1L, 1);
                Assert.True(writer.Schema.TryGetFieldIndex("clicks", out var field));
                record.Set(field, ScalarValue.FromFloat64(1.0));
                Assert.True(record.Commit());
            });
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, source);
        rig.Build();
        if (cache)
        {
            var warm = rig.NewRequest([1]);
            Assert.Equal(FeatureState.Ok, (await warm.EnsureAsync(clicks)).State);
            Assert.Equal(1, rig.WriteBack(source));
            warm.Memory.Release();
        }

        using var listener = MeasurementHook.Throwing(StrataRecMetrics.Names.SourceValues, (StrataRecTags.Source, source));
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest([1], hook: hook);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => request.EnsureAsync(clicks).AsTask());

        Assert.Equal("a host's metric listener failed", thrown.Message);
        Assert.Equal(1, listener.Calls);
        var read = Assert.Single(hook.Recorded);
        Assert.Equal(new double?[] { 1.0 }, read.Values);
        Assert.Equal(cache ? 1 : 0, connector.Calls.Count);
        request.Memory.Release();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AContextReadServedFromATable_IsRecordedBeforeItsValuesAreCounted(bool cache)
    {
        const string source = "listener_context_table";
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = cache
            ? new FeatureTestRig().CacheSource(source, connector)
            : new FeatureTestRig().LocalSource(source, writer =>
            {
                var record = writer.Begin(1001L, 1);
                Assert.True(writer.Schema.TryGetFieldIndex("ctr", out var field));
                record.Set(field, ScalarValue.FromFloat64(1001.0));
                Assert.True(record.Commit());
            });
        var ctr = rig.Physical("user.ctr", FeatureTestRig.User, ColumnType.Float64, source);
        rig.Build();
        if (cache)
        {
            var warm = rig.NewRequest([1], userId: 1001);
            warm.Fetcher.StartContextFetch([ctr], CancellationToken.None);
            Assert.Equal(FeatureState.Ok, (await warm.EnsureAsync(ctr)).State);
            await warm.Fetcher.WhenIoCompleteAsync();
            Assert.Equal(1, rig.WriteBack(source));
            warm.Memory.Release();
        }

        using var listener = MeasurementHook.Throwing(StrataRecMetrics.Names.SourceValues, (StrataRecTags.Source, source));
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest([1], userId: 1001, hook: hook);

        request.Fetcher.StartContextFetch([ctr], CancellationToken.None);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => request.EnsureAsync(ctr).AsTask());

        Assert.Equal("a host's metric listener failed", thrown.Message);
        Assert.Equal(1, listener.Calls);
        var read = Assert.Single(hook.Recorded);
        Assert.Equal(new double?[] { 1001.0 }, read.Values);
        Assert.Equal(cache ? 1 : 0, connector.Calls.Count);
        request.Memory.Release();
    }

    /// <summary>
    /// A read hands what the connector answered to the table's write-back queue while it resolves, before it is
    /// recorded; a full queue drops it and counts the drop (<c>stratarec.cache.writes</c>, outcome <c>dropped</c>) on
    /// the request's thread.
    /// </summary>
    [Fact]
    public async Task AWriteBackTheQueueDrops_WhoseListenerThrows_LeavesTheFillAndTheRecording()
    {
        const string source = "listener_write_back";
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource(source, connector, maxBytes: "16MiB"); // a write-back queue of 1 MiB
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, source);
        rig.Build();
        var table = rig.Tables[source].Cache!;
        Assert.True(table.TryWriteBack(new CacheWriteBatch([EntityKey.FromInt64(99)], [new byte[2 << 20]], 1)));
        using var listener = MeasurementHook.Throwing(StrataRecMetrics.Names.CacheWrites, (StrataRecTags.Outcome, "dropped"));
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest([1], hook: hook);

        var result = await request.EnsureAsync(clicks);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(1.0, request.Value(clicks, 0).AsFloat64());
        Assert.Equal(1, listener.Calls);
        Assert.Equal(IoOutcome.Ok, Assert.Single(hook.Recorded).Outcome);
        Assert.Single(connector.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release());
    }
}
