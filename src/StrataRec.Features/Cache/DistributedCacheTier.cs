using System.Threading.Channels;
using StrataRec.Core.Bindings;

namespace StrataRec.Features.Cache;

/// <summary>
/// The distributed tier of a <c>cache</c> source as the engine holds it (design 6.6 <c>cache</c>): the binding's
/// <see cref="IFeatureCacheStore"/> and the engine's write-back queue in front of it. This, not the store, is the binding
/// instance: shared by identity, loaded before a plan serves (the store's own load), disposed once nothing can call it.
/// </summary>
/// <remarks>
/// <para><b>Writes never wait.</b> A read hands what the connector returned to <see cref="TryWrite"/>, which queues it and
/// returns; one background loop per instance writes the queued batches one after another, one store round trip each.
/// Past <see cref="QueueBytes"/> waiting record bytes a batch is dropped and counted (<c>stratarec.cache.writes</c>,
/// outcome <c>dropped</c>): the keys are fetched again on their next miss, which is what the tier exists to avoid, so
/// the metric says when the store cannot keep up. A failed write is counted (<c>failed</c>) and forgotten.</para>
/// <para><b>The queue belongs to the instance, not to a table.</b> Plans that share the instance share its queue, and
/// the store is disposed only after the loop stopped, so no write runs on a disposed store; what is still queued then
/// is dropped.</para>
/// </remarks>
public sealed class DistributedCacheTier : ILoadable, IAsyncDisposable
{
    /// <summary>The most record bytes waiting to be written: 16 MiB, a few seconds of misses at a high miss rate.</summary>
    public const long DefaultQueueBytes = 16L << 20;

    private readonly Channel<PendingWrite> _queue = Channel.CreateUnbounded<PendingWrite>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private Task? _writer;
    private long _queuedBytes;
    private bool _disposed;

    public DistributedCacheTier(IFeatureCacheStore store, long queueBytes = DefaultQueueBytes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueBytes);
        Store = store;
        QueueBytes = queueBytes;
    }

    public IFeatureCacheStore Store { get; }

    /// <summary>The most record bytes waiting to be written; a write past it is dropped.</summary>
    public long QueueBytes { get; }

    /// <summary>Record bytes queued and not written yet.</summary>
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    /// <summary>Loads the store (connects, resolves its credentials) when it needs it.</summary>
    public ValueTask LoadAsync(CancellationToken cancellationToken) =>
        Store is ILoadable loadable ? loadable.LoadAsync(cancellationToken) : ValueTask.CompletedTask;

    /// <summary>
    /// Queues <paramref name="count"/> records of <paramref name="cache"/>'s keyspace for the store without waiting; false
    /// (counted as dropped) when the queue is full or the tier is being disposed. The arrays become the tier's.
    /// </summary>
    internal bool TryWrite(LocalTableCache cache, CacheStoreEntry[] entries, int count)
    {
        if (count == 0)
        {
            return true;
        }

        long bytes = 0;
        for (var i = 0; i < count; i++)
        {
            bytes += entries[i].Record.Length;
        }

        if (Interlocked.Add(ref _queuedBytes, bytes) > QueueBytes || !_queue.Writer.TryWrite(new PendingWrite(cache, entries, count, bytes)))
        {
            Interlocked.Add(ref _queuedBytes, -bytes);
            cache.CountWrites(CacheTier.Distributed, CacheWriteOutcome.Dropped, count);
            return false;
        }

        if (Volatile.Read(ref _writer) is null)
        {
            StartWriter();
        }

        return true;
    }

    /// <summary>The loop starts with the first write, so an instance that never writes (a plan compiled to validate, a
    /// replay) never runs one.</summary>
    private void StartWriter()
    {
        lock (_gate)
        {
            if (_writer is null && !_disposed)
            {
                Volatile.Write(ref _writer, Task.Run(WriteAsync));
            }
        }
    }

    private async Task WriteAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var write))
                {
                    Interlocked.Add(ref _queuedBytes, -write.Bytes);
                    CacheWriteOutcome outcome;
                    try
                    {
                        await Store.SetAsync(write.Cache.Keyspace, write.Entries.AsMemory(0, write.Count), _stopping.Token).ConfigureAwait(false);
                        outcome = CacheWriteOutcome.Written;
                    }
                    catch (Exception)
                    {
                        // Only an optimisation failed: the keys are fetched again on their next miss. The store's errors
                        // reach the operator through the metric and the store's reads, which fail the same way. A write
                        // the disposal cancelled is a dropped one.
                        outcome = _stopping.IsCancellationRequested ? CacheWriteOutcome.Dropped : CacheWriteOutcome.Failed;
                    }

                    write.Cache.CountWrites(CacheTier.Distributed, outcome, write.Count);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Disposed: what is still queued is dropped below.
        }
    }

    /// <summary>Stops the write loop (cancelling a write in progress), drops what is still queued, then disposes the store.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? writer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            writer = _writer;
        }

        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (writer is not null)
        {
            await writer.ConfigureAwait(false); // never faults: the loop catches what the store throws
        }

        while (_queue.Reader.TryRead(out var write))
        {
            Interlocked.Add(ref _queuedBytes, -write.Bytes);
            write.Cache.CountWrites(CacheTier.Distributed, CacheWriteOutcome.Dropped, write.Count);
        }

        _stopping.Dispose();
        switch (Store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private readonly record struct PendingWrite(LocalTableCache Cache, CacheStoreEntry[] Entries, int Count, long Bytes);
}
