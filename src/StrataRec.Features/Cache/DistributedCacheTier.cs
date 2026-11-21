using System.Runtime.CompilerServices;
using System.Threading.Channels;
using StrataRec.Core.Bindings;
using StrataRec.Core.Threading;
using StrataRec.Features.Local;

namespace StrataRec.Features.Cache;

/// <summary>
/// The distributed tier of a <c>cache</c> source as the engine holds it (design 6.6 <c>cache</c>): the binding's
/// <see cref="ICacheStore"/> and the engine's write-back queue in front of it. This, not the store, is the binding
/// instance: shared by identity, loaded before a plan serves (the store's own load), disposed once nothing can call it.
/// </summary>
/// <remarks>
/// <para><b>Writes never wait.</b> A read hands what the connector returned to <see cref="TryWrite"/>, which queues it and
/// returns; one background loop per instance writes the queued batches one after another, one store round trip each.
/// Past <see cref="QueueBytes"/> waiting bytes (records, keys and entries) a batch is dropped and counted (<c>stratarec.cache.writes</c>,
/// outcome <c>dropped</c>): the keys are fetched again on their next miss, which is what the tier exists to avoid, so
/// the metric says when the store cannot keep up. An empty queue takes even a batch larger than the whole limit
/// (<see cref="WriteQueueBudget"/>). A failed write is counted (<c>failed</c>) and forgotten.</para>
/// <para><b>The queue belongs to the instance, not to a table.</b> Plans that share the instance share its queue, and
/// the store is disposed only after the loop stopped, so no write runs on a disposed store; what is still queued then
/// is dropped.</para>
/// <para><b>The loop never faults.</b> It catches what the store throws, and what an observer throws: a metric listener
/// that throws when a batch is counted. Such a failure is kept and reported by <see cref="DisposeAsync"/> (the host logs
/// it); the loop goes on writing.</para>
/// </remarks>
public sealed class DistributedCacheTier : ILoadable, IAsyncDisposable
{
    /// <summary>
    /// The most bytes waiting to be written: 16 MiB, a few seconds of misses at a high miss rate. A batch is charged what it
    /// holds, its records' bytes and its entries with their key texts: a long key text with a short record holds more in
    /// its key (pre-release assessment).
    /// </summary>
    public const long DefaultQueueBytes = 16L << 20;

    private readonly Channel<PendingWrite> _queue = Channel.CreateUnbounded<PendingWrite>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ObservedCancellationSource _stopping = new(CancellationSite.CacheWriter);
    private readonly object _gate = new();
    private Task? _writer;
    private long _queuedBytes;
    private bool _disposed;
    private Exception? _observerFailure;

    public DistributedCacheTier(ICacheStore store, long queueBytes = DefaultQueueBytes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueBytes);
        Store = store;
        QueueBytes = queueBytes;
    }

    public ICacheStore Store { get; }

    /// <summary>
    /// One read of the store (<see cref="ICacheStore.GetAsync"/>), for a bounded call's send: true once it answered, its
    /// failure thrown. The store's task is consumed once, also when it completed at once: a store may pool what backs it
    /// and take it back only then (review of the IO redesign: a service's cache read a completed one's status only).
    /// </summary>
    internal ValueTask<bool> ReadAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
    {
        var get = Store.GetAsync(keyspace, keys, records, cancellationToken);
        if (get.IsCompleted)
        {
            get.GetAwaiter().GetResult(); // a failure is the call's: BoundedCall catches what Send throws
            return new ValueTask<bool>(true);
        }

        return Await(get);

        static async ValueTask<bool> Await(ValueTask get)
        {
            await get.ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// The most bytes waiting to be written (records, entries and key texts); a write past it is dropped, but for one
    /// larger than the whole limit handed to an empty queue, which takes it.
    /// </summary>
    public long QueueBytes { get; }

    /// <summary>Bytes queued and not written yet, as charged (records, entries and key texts).</summary>
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    /// <summary>Loads the store (connects, resolves its credentials) when it needs it.</summary>
    public ValueTask LoadAsync(CancellationToken cancellationToken) =>
        Store is ILoadable loadable ? loadable.LoadAsync(cancellationToken) : ValueTask.CompletedTask;

    /// <summary>
    /// Queues <paramref name="count"/> records of <paramref name="cache"/>'s keyspace for the store without waiting; false
    /// (counted as dropped, on the caller's thread: <see cref="LocalTableCache.CountDropped"/>) when they do not fit a queue
    /// that holds some (<see cref="WriteQueueBudget"/>) or the tier is being disposed. The arrays become the tier's.
    /// </summary>
    internal bool TryWrite(LocalTableCache cache, CacheStoreEntry[] entries, int count)
    {
        if (count == 0)
        {
            return true;
        }

        var bytes = (long)entries.Length * Unsafe.SizeOf<CacheStoreEntry>();
        for (var i = 0; i < count; i++)
        {
            bytes += entries[i].Record.Length + TableVersion.StringObjectBytes(entries[i].Key.Length);
        }

        if (!WriteQueueBudget.TryTake(ref _queuedBytes, bytes, QueueBytes))
        {
            cache.CountDropped(CacheTier.Distributed, count);
            return false;
        }

        if (!_queue.Writer.TryWrite(new PendingWrite(cache, entries, count, bytes)))
        {
            Interlocked.Add(ref _queuedBytes, -bytes);
            cache.CountDropped(CacheTier.Distributed, count);
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
            // Once the disposal began, nothing more is sent: what is still queued is dropped by DisposeAsync, counted.
            while (!_stopping.IsCancellationRequested && await reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (!_stopping.IsCancellationRequested && reader.TryRead(out var write))
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

                    Count(write, outcome);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Disposed: what is still queued is dropped by DisposeAsync.
        }
        catch (Exception ex)
        {
            Observed(ex); // not expected; the loop must not fault all the same (see remarks)
        }
    }

    /// <summary>Counts a batch's outcome; a metric listener that throws is reported at disposal, not by the caller.</summary>
    private void Count(in PendingWrite write, CacheWriteOutcome outcome)
    {
        try
        {
            write.Cache.CountWrites(CacheTier.Distributed, outcome, write.Count);
        }
        catch (Exception ex)
        {
            Observed(ex);
        }
    }

    private void Observed(Exception exception) => Interlocked.CompareExchange(ref _observerFailure, exception, null);

    /// <summary>
    /// Stops the write loop (cancelling a write in progress, and sending no further batch), waits until it really
    /// exited, drops what is still queued (counted), then disposes the store, exactly once whatever failed before. Throws,
    /// after all that, what went wrong on the way (a callback the store registered on the cancelled token, a metric
    /// listener, the store's disposal), so that the host logs it.
    /// </summary>
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

        List<Exception>? failures = null;
        try
        {
            _queue.Writer.TryComplete();
            try
            {
                await _stopping.CancelObservedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex); // a callback on the token threw; the token is cancelled all the same
            }

            if (writer is not null)
            {
                await writer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // never faults: see remarks
            }
        }
        finally
        {
            try
            {
                while (_queue.Reader.TryRead(out var write))
                {
                    Interlocked.Add(ref _queuedBytes, -write.Bytes);
                    Count(write, CacheWriteOutcome.Dropped);
                }
            }
            finally
            {
                _stopping.Dispose();
                try
                {
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
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }
        }

        if (Volatile.Read(ref _observerFailure) is { } observed)
        {
            (failures ??= []).Add(observed);
        }

        if (failures is not null)
        {
            throw new AggregateException("The distributed cache tier was disposed, its store too, but not cleanly.", failures);
        }
    }

    private readonly record struct PendingWrite(LocalTableCache Cache, CacheStoreEntry[] Entries, int Count, long Bytes);
}
