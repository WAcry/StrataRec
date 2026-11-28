using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features.Cache;

public sealed class DistributedTierClockTests
{
    [Fact]
    public async Task AClockMovedByTheHostsTracing_LeavesTheTierLessTime_AndItsLateRecordIsNotTaken()
    {
        const int sourceMs = 4000;
        var name = "tier_clock_" + Guid.NewGuid().ToString("N");
        var store = new LateStore();
        var connector = ScriptedConnector.Values((_, _) => 7.0);
        var rig = new FeatureTestRig().CacheSource(name, connector, distributed: store, distributedTimeoutMs: sourceMs, timeoutMs: sourceMs);
        var value = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, name);
        rig.Build();
        rig.Clock.HoldTimestamps();
        var cache = rig.Tables[name].Cache!;
        var writer = new CacheRecordWriter(cache.Layout, 64);
        writer.Begin(rig.Clock.NowMs, cache.Fingerprint, absent: false);
        writer.Float64(0, 111.0);
        var (offset, length) = writer.End();
        store.Record = writer.Bytes.AsMemory(offset, length);
        store.AdvanceTime = () => rig.Clock.AdvanceTimestamps(TimeSpan.FromMilliseconds(1000));

        using var flow = new TracedFlow();
        var trace = flow.Request.TraceId;
        var sampled = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Parent.TraceId == trace && options.Name == "cache " + name)
                {
                    sampled++;
                    rig.Clock.AdvanceTimestamps(TimeSpan.FromMilliseconds(sourceMs - 250));
                    return ActivitySamplingResult.AllDataAndRecorded;
                }

                return ActivitySamplingResult.None;
            },
        };
        ActivitySource.AddActivityListener(listener);
        var request = rig.NewRequest([1]);
        try
        {
            var result = await request.EnsureAsync(value);
            Assert.Equal(FeatureState.Ok, result.State);
            Assert.Equal(1, sampled);
            Assert.Equal(1, store.Calls);
            var received = request.Value(value, 0).AsFloat64();
            Assert.Equal(7.0, received);
        }
        finally
        {
            await request.Fetcher.WhenIoCompleteAsync();
            request.Memory.Release();
        }
    }

    private sealed class LateStore : ICacheStore
    {
        public string StoreType => TestCacheStoreFactory.TypeName;
        public ReadOnlyMemory<byte> Record { get; set; }
        public Action? AdvanceTime { get; set; }
        public int Calls { get; private set; }

        public async ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(850).ConfigureAwait(false);
            AdvanceTime?.Invoke();
            records.Span.Fill(Record);
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
