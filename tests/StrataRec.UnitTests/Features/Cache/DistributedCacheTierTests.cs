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

    /// <summary>a callback the store registered on its token throws when the disposal cancels it.</summary>
    [Fact]
    public async Task ACancellationCallbackThatThrows_DoesNotDisposeTheStoreUnderAWrite_NorSkipItsDisposal()
    {
        var store = new GatedStore(holdCall: 1)
        {
            OnToken = token => token.Register(static () => throw new InvalidOperationException("cleanup failed")),
        };
        var tier = new DistributedCacheTier(store);
        Assert.True(tier.TryWrite(Cache(), Entries(1), 1));
        await store.Held.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        var disposing = tier.DisposeAsync().AsTask();
        await store.Cancelled.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(0, store.Disposals); // the write still runs on it
        store.Release();

        var error = await Assert.ThrowsAsync<AggregateException>(() => disposing.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "cleanup failed"); // reported, for the host to log
        Assert.Equal((1, true), (store.Disposals, store.DisposedAfterTheWriteReturned));
        await tier.DisposeAsync();
        Assert.Equal(1, store.Disposals);
    }

    /// <summary>
    /// A metric listener that throws when a written batch is counted neither stops the loop (the next batch is
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
        await store.Held.Task.WaitAsync(Wait, TestContext.Current.CancellationToken); // the loop went on to the second

        var disposing = tier.DisposeAsync().AsTask();
        await store.Cancelled.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(0, store.Disposals);
        store.Release();

        var error = await Assert.ThrowsAsync<AggregateException>(() => disposing.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Contains(error.InnerExceptions, e => e.Message == "listener failed");
        Assert.Equal((2, 1, true), (store.Calls, store.Disposals, store.DisposedAfterTheWriteReturned));
        Assert.Equal(0, tier.QueuedBytes);
    }

    /// <summary>
    /// Once the disposal began, the loop sends no further batch. The write in flight finishes; the two queued
    /// behind it are dropped and counted, not sent on a cancelled token; the store is disposed last.
    /// </summary>
    [Fact]
    public async Task TheDisposal_LetsTheWriteInFlightFinish_AndDropsTheQueuedBatches_WithoutSendingThem()
    {
        using var written = new CountedWrites("written");
        using var dropped = new CountedWrites("dropped");
        var store = new GatedStore(holdCall: 1);
        var tier = new DistributedCacheTier(store);
        var cache = Cache();
        Assert.True(tier.TryWrite(cache, Entries(1), 1));
        await store.Held.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.True(tier.TryWrite(cache, Entries(2), 1));
        Assert.True(tier.TryWrite(cache, Entries(3), 1));

        var disposing = tier.DisposeAsync().AsTask();
        await store.Cancelled.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        store.Release();
        await disposing.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(1, store.Calls);
        Assert.Equal((1L, 2L), (written.Total, dropped.Total));
        Assert.Equal(0, tier.QueuedBytes);
        Assert.Equal((1, true), (store.Disposals, store.DisposedAfterTheWriteReturned));
    }

    /// <summary>
    /// As the table's writer, the tier's queue takes a batch larger than its whole limit when it is empty (the
    /// loop takes a batch off the queue as it sends it), and nothing over its limit while it holds some: it holds at most
    /// that one batch over its limit.
    /// </summary>
    [Fact]
    public async Task AWriteLargerThanTheQueue_GoesIntoAnEmptyQueue_AndNotIntoOneThatHoldsSome()
    {
        using var dropped = new CountedWrites("dropped");
        using var written = new CountedWrites("written");
        var store = new GatedStore(holdCall: 1);
        var tier = new DistributedCacheTier(store, queueBytes: 4096);
        var cache = Cache();
        CacheStoreEntry[] large = [new(new string('k', 10_000), new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(1))];
        Assert.True(tier.TryWrite(cache, Entries(1), 1));
        await store.Held.Task.WaitAsync(Wait, TestContext.Current.CancellationToken); // being written: the queue is empty

        Assert.True(tier.TryWrite(cache, large, 1));
        Assert.InRange(tier.QueuedBytes, 20_000, 20_200);
        Assert.False(tier.TryWrite(cache, Entries(2), 1)); // past its limit
        Assert.False(tier.TryWrite(cache, [new(new string('k', 10_000), new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(1))], 1));
        Assert.Equal(2, dropped.Total);

        store.Release();
        await Task.Run(() => Assert.True(SpinWait.SpinUntil(() => store.Calls == 2, Wait)), TestContext.Current.CancellationToken);
        await tier.DisposeAsync();
        Assert.Equal((2, 2L, 2L), (store.Calls, written.Total, dropped.Total)); // both taken ones written
        Assert.Equal(0, tier.QueuedBytes);
    }

    /// <summary>
    /// A write the tier's queue drops is counted on the thread that hands it over, a request's, while the read
    /// resolves: a listener that threw there failed the read. It returns false, counted, and the listener's exception is
    /// lost; what the loop counts is still kept for the disposal to report (the test above).
    /// </summary>
    [Fact]
    public async Task AWriteTheQueueDrops_WhoseListenerThrows_IsRefusedWithoutThrowing()
    {
        using var listener = MeasurementHook.Throwing(
            StrataRecMetrics.Names.CacheWrites, (StrataRecTags.CacheTier, "distributed"), (StrataRecTags.Outcome, "dropped"));
        var store = new GatedStore(holdCall: 1);
        var tier = new DistributedCacheTier(store, queueBytes: 4096);
        var cache = Cache();
        Assert.True(tier.TryWrite(cache, Entries(1), 1));
        await store.Held.Task.WaitAsync(Wait, TestContext.Current.CancellationToken); // being written: the queue is empty
        Assert.True(tier.TryWrite(cache, [new(new string('k', 10_000), new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(1))], 1));

        Assert.False(tier.TryWrite(cache, Entries(2), 1)); // past its limit

        Assert.Equal(1, listener.Calls);
        store.Release();
        await Task.Run(() => Assert.True(SpinWait.SpinUntil(() => store.Calls == 2, Wait)), TestContext.Current.CancellationToken);
        await tier.DisposeAsync();
    }

    private static LocalTableCache Cache() =>
        new LocalFeatureTable(Schema, new LocalFeatureTableOptions { MaxBytes = 1 << 20, Cache = new CacheTableOptions { TtlMs = 60_000 } }).Cache!;

    private static CacheStoreEntry[] Entries(long key) => [new(key.ToString(System.Globalization.CultureInfo.InvariantCulture), new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(1))];

    /// <summary>
    /// A store whose call number <c>holdCall</c> waits for <see cref="Release"/> (other calls return at once), and which
    /// says when its token was cancelled and whether it was disposed only after its writes returned.
    /// </summary>
    private sealed class GatedStore(int holdCall) : ICacheStore, IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _running;
        private int _disposals;

        public string StoreType => TestCacheStoreFactory.TypeName;

        /// <summary>Runs on the token of each write (to register a callback).</summary>
        public Action<CancellationToken>? OnToken { get; init; }

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public int Disposals => Volatile.Read(ref _disposals);

        public bool DisposedAfterTheWriteReturned { get; private set; }

        public void Release() => _release.TrySetResult();

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken) =>
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

    /// <summary>Records counted by <c>stratarec.cache.writes</c> with one outcome, of the distributed tier.</summary>
    private sealed class CountedWrites : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public CountedWrites(string outcome)
        {
            _listener.InstrumentPublished = (published, listener) =>
            {
                if (published.Meter.Name == StrataRecTelemetry.Name && published.Name == StrataRecMetrics.Names.CacheWrites)
                {
                    listener.EnableMeasurementEvents(published);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                bool tier = false, matches = false;
                foreach (var tag in tags)
                {
                    tier |= tag.Key == StrataRecTags.CacheTier && Equals(tag.Value, "distributed");
                    matches |= tag.Key == StrataRecTags.Outcome && Equals(tag.Value, outcome);
                }

                if (tier && matches)
                {
                    Interlocked.Add(ref _total, value);
                }
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
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
