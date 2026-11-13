using System.Diagnostics.Metrics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>
/// The write side of the distributed tier (<see cref="DistributedCacheTier"/>): its loop, and its disposal, which waits
/// for a write in progress to really return, then disposes the store exactly once, whatever failed on the way.
/// </summary>
[Collection(MetricMeasurement.Name)]
public class DistributedCacheTierTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static readonly LocalTableSchema Schema = new(
        "item_store", "item", EntityKeyType.Int64, LocalTableMode.Cache, [new("clicks", ColumnType.Float64)]);

    /// <summary>Review 19: a callback the store registered on its token throws when the disposal cancels it.</summary>
    [Fact]
    public async Task ACancellationCallbackThatThrows_DoesNotDisposeTheStoreUnderAWrite_NorSkipItsDisposal()
    {
        var store = new GatedStore(holdCall: 1)
        {
            OnToken = token => token.Register(static () => throw new InvalidOperationException("cleanup failed")),
        };
        var tier = new DistributedCacheTier(store);
        Assert.True(tier.TryWrite(Cache(), Entries(1), 1));
        await store.Held.Task.WaitAsync(Wait);

        var disposing = tier.DisposeAsync().AsTask();
        await store.Cancelled.Task.WaitAsync(Wait);
        Assert.Equal(0, store.Disposals); // the write still runs on it
        store.Release();

        var error = await Assert.ThrowsAsync<AggregateException>(() => disposing.WaitAsync(Wait));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "cleanup failed"); // reported, for the host to log
        Assert.Equal((1, true), (store.Disposals, store.DisposedAfterTheWriteReturned));
        await tier.DisposeAsync();
        Assert.Equal(1, store.Disposals);
    }

    /// <summary>
    /// Review 19: a metric listener that throws when a written batch is counted neither stops the loop (the next batch is
    /// still written) nor skips the store's disposal.
    /// </summary>
    [Fact]
    public async Task AMetricListenerThatThrows_DoesNotStopTheWriter_NorSkipTheStoresDisposal()
    {
        using var throwing = new ThrowingListener(StrataRecMetrics.Names.CacheWrites);
        var store = new GatedStore(holdCall: 2);
        var tier = new DistributedCacheTier(store);
        var cache = Cache();
        Assert.True(tier.TryWrite(cache, Entries(1), 1)); // written, then its count throws
        Assert.True(tier.TryWrite(cache, Entries(2), 1));
        await store.Held.Task.WaitAsync(Wait); // the loop went on to the second

        var disposing = tier.DisposeAsync().AsTask();
        await store.Cancelled.Task.WaitAsync(Wait);
        Assert.Equal(0, store.Disposals);
        store.Release();

        var error = await Assert.ThrowsAsync<AggregateException>(() => disposing.WaitAsync(Wait));
        Assert.Contains(error.InnerExceptions, e => e.Message == "listener failed");
        Assert.Equal((2, 1, true), (store.Calls, store.Disposals, store.DisposedAfterTheWriteReturned));
        Assert.Equal(0, tier.QueuedBytes);
    }

    private static LocalTableCache Cache() =>
        new LocalFeatureTable(Schema, new LocalFeatureTableOptions { MaxBytes = 1 << 20, Cache = new CacheTableOptions { TtlMs = 60_000 } }).Cache!;

    private static CacheStoreEntry[] Entries(long key) => [new(EntityKey.FromInt64(key), new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(1))];

    /// <summary>
    /// A store whose call number <c>holdCall</c> waits for <see cref="Release"/> (other calls return at once), and which
    /// says when its token was cancelled and whether it was disposed only after its writes returned.
    /// </summary>
    private sealed class GatedStore(int holdCall) : IFeatureCacheStore, IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _running;
        private int _disposals;

        public string StoreType => TestCacheStoreFactory.Type;

        /// <summary>Runs on the token of each write (to register a callback).</summary>
        public Action<CancellationToken>? OnToken { get; init; }

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public int Disposals => Volatile.Read(ref _disposals);

        public bool DisposedAfterTheWriteReturned { get; private set; }

        public void Release() => _release.TrySetResult();

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<EntityKey> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _running);
            try
            {
                OnToken?.Invoke(cancellationToken);
                cancellationToken.Register(() => Cancelled.TrySetResult());
                if (Interlocked.Increment(ref _calls) == holdCall)
                {
                    Held.TrySetResult();
                    await _release.Task; // ignores the token, like a store whose write is already on the wire
                }
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public void Dispose()
        {
            DisposedAfterTheWriteReturned = Volatile.Read(ref _running) == 0;
            Interlocked.Increment(ref _disposals);
        }
    }

    /// <summary>A metric listener whose callback throws for one instrument.</summary>
    private sealed class ThrowingListener : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ThrowingListener(string instrument)
        {
            _listener.InstrumentPublished = (published, listener) =>
            {
                if (published.Meter.Name == StrataRecTelemetry.Name && published.Name == instrument)
                {
                    listener.EnableMeasurementEvents(published);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("listener failed"));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
