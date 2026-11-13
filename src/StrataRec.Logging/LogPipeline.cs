using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace StrataRec.Logging;

/// <summary>Queue sizes and timing of a <see cref="LogPipeline"/>.</summary>
public sealed class LogPipelineOptions
{
    /// <summary>Best-effort batches buffered before new ones are dropped (and counted).</summary>
    public int BestEffortCapacity { get; init; } = 4096;

    /// <summary>
    /// Durable batches buffered in memory. A batch that does not fit goes to <see cref="DurableOverflow"/>; without
    /// one it is lost, counted in <see cref="LogPipeline.DurableOverflow"/>, and leaves a sequence gap downstream.
    /// </summary>
    public int DurableCapacity { get; init; } = 65536;

    /// <summary>
    /// Spill hook for durable batches the memory queue cannot take (design 8.6 item 4: the local disk buffer). Called
    /// on the request thread with the batch, which already carries its <c>seq</c>; return true when the batch was
    /// persisted and will be delivered later (e.g. appended to a local file replayed at startup). Must be fast.
    /// </summary>
    public Func<LogBatch, bool>? DurableOverflow { get; init; }

    /// <summary>
    /// Receives batches a sink rejected permanently (<see cref="LogBatchRejectedException"/>): they are never retried,
    /// so a malformed batch cannot block the durable stream behind it.
    /// </summary>
    public Action<LogBatch, Exception>? Rejected { get; init; }

    /// <summary>Batches handed to the sink per write.</summary>
    public int MaxBatchesPerWrite { get; init; } = 256;

    /// <summary>How long <see cref="LogPipeline.DisposeAsync"/> keeps draining durable batches (design 8.6 item 4).</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>What happened to a submitted batch.</summary>
public enum LogAdmission : byte
{
    /// <summary>Queued for the sink.</summary>
    Queued = 1,

    /// <summary>A durable batch the memory queue could not take, persisted by <see cref="LogPipelineOptions.DurableOverflow"/>.</summary>
    Spilled,

    /// <summary>Not queued: a full best-effort queue, or a full durable queue without (or refused by) the spill hook.</summary>
    Dropped,

    /// <summary>The pipeline is shutting down.</summary>
    Closed,
}

/// <summary>
/// Thrown by an <see cref="ILogSink"/> for batches that can never be written (malformed, too large…). The sink
/// promises that every other batch of the call was written; the pipeline drops the rejected ones instead of retrying.
/// </summary>
public sealed class LogBatchRejectedException : Exception
{
    public LogBatchRejectedException(IReadOnlyList<LogBatch> rejected, Exception? inner = null)
        : base($"{rejected.Count} log batch(es) rejected permanently" + (inner is null ? string.Empty : ": " + inner.Message), inner)
    {
        Batches = rejected;
    }

    public IReadOnlyList<LogBatch> Batches { get; }
}

/// <summary>Delivery counters of one tap (design 8.4: drops are counted so offline weights can be corrected).</summary>
public readonly record struct TapLogStats(string TapId, long Delivered, long Dropped);

/// <summary>
/// The asynchronous log pipeline (design 8.6): request threads only enqueue finished batches; background loops write
/// them to an <see cref="ILogSink"/>. Durable and best-effort batches use separate bounded queues so a burst of
/// diagnostics never delays training logs. Durable batches get consecutive per-process sequence numbers and are
/// retried until the sink accepts them (or rejects them permanently); best-effort batches are dropped when their queue
/// is full or a write fails.
/// </summary>
public sealed class LogPipeline : IAsyncDisposable
{
    private readonly ILogSink _sink;
    private readonly LogPipelineOptions _options;
    private readonly Channel<LogBatch> _durable;
    private readonly Channel<LogBatch> _bestEffort;
    private readonly Task _durableLoop;
    private readonly Task _bestEffortLoop;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, long[]> _stats = new(StringComparer.Ordinal);
    private long _seq;
    private long _pending;
    private int _disposed;

    public LogPipeline(ILogSink sink, LogPipelineOptions? options = null, string? producerId = null)
    {
        _sink = sink;
        _options = options ?? new LogPipelineOptions();
        ProducerId = producerId ?? $"{Environment.MachineName}/{Environment.ProcessId}/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        _durable = Channel.CreateBounded<LogBatch>(new BoundedChannelOptions(_options.DurableCapacity) { SingleReader = true });
        _bestEffort = Channel.CreateBounded<LogBatch>(new BoundedChannelOptions(_options.BestEffortCapacity) { SingleReader = true });
        _durableLoop = Task.Run(() => RunAsync(_durable.Reader, durable: true));
        _bestEffortLoop = Task.Run(() => RunAsync(_bestEffort.Reader, durable: false));
    }

    /// <summary>Identifies this process in batch envelopes; with <c>seq</c> it is the dedup key downstream.</summary>
    public string ProducerId { get; }

    /// <summary>Durable batches that were neither queued nor spilled (they leave a gap in <c>seq</c>).</summary>
    public long DurableOverflow => Interlocked.Read(ref _durableOverflow);

    /// <summary>Batches a sink rejected permanently (<see cref="LogBatchRejectedException"/>).</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    private long _durableOverflow;
    private long _rejected;

    /// <summary>
    /// Enqueues a finished batch. Never blocks the request thread; the result says whether the batch was queued,
    /// spilled or dropped, so publishers and metrics can see durable loss as it happens (design 8.6).
    /// </summary>
    public LogAdmission Submit(LogBatch batch)
    {
        batch.ProducerId = ProducerId;
        if (Volatile.Read(ref _disposed) != 0)
        {
            Count(batch.TapId, delivered: 0, dropped: 1);
            if (batch.Delivery == LogDelivery.Durable)
            {
                Interlocked.Increment(ref _durableOverflow);
            }

            return LogAdmission.Closed;
        }

        Interlocked.Increment(ref _pending);
        if (batch.Delivery == LogDelivery.Durable)
        {
            batch.Seq = Interlocked.Increment(ref _seq);
            if (_durable.Writer.TryWrite(batch))
            {
                return LogAdmission.Queued;
            }

            Interlocked.Decrement(ref _pending);
            if (Spill(batch))
            {
                return LogAdmission.Spilled;
            }

            Interlocked.Increment(ref _durableOverflow);
            Count(batch.TapId, 0, 1);
            return LogAdmission.Dropped;
        }

        if (!_bestEffort.Writer.TryWrite(batch))
        {
            Count(batch.TapId, 0, 1);
            Interlocked.Decrement(ref _pending);
            return LogAdmission.Dropped;
        }

        return LogAdmission.Queued;
    }

    private bool Spill(LogBatch batch)
    {
        if (_options.DurableOverflow is not { } spill)
        {
            return false;
        }

        try
        {
            return spill(batch);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StrataRec durable spill failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Delivery counters per tap, sorted by tap id.</summary>
    public List<TapLogStats> Stats()
    {
        var list = new List<TapLogStats>();
        foreach (var (tap, counters) in _stats)
        {
            list.Add(new TapLogStats(tap, Interlocked.Read(ref counters[0]), Interlocked.Read(ref counters[1])));
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.TapId, b.TapId));
        return list;
    }

    /// <summary>Waits until every queued batch was written or dropped (tests, CLI, graceful shutdown).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Interlocked.Read(ref _pending) > 0)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops accepting batches and drains the queues for up to <see cref="LogPipelineOptions.DrainTimeout"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _durable.Writer.TryComplete();
        _bestEffort.Writer.TryComplete();
        _stop.CancelAfter(_options.DrainTimeout);
        try
        {
            await Task.WhenAll(_durableLoop, _bestEffortLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Drain timed out; remaining durable batches show up as a sequence gap.
        }

        _stop.Dispose();
    }

    private async Task RunAsync(ChannelReader<LogBatch> reader, bool durable)
    {
        var buffer = new List<LogBatch>(_options.MaxBatchesPerWrite);
        var token = _stop.Token;
        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            buffer.Clear();
            while (buffer.Count < _options.MaxBatchesPerWrite && reader.TryRead(out var batch))
            {
                buffer.Add(batch);
            }

            var rejected = new HashSet<LogBatch>(ReferenceEqualityComparer.Instance);
            var delivered = await WriteAsync(buffer, durable, rejected, token).ConfigureAwait(false);
            foreach (var batch in buffer)
            {
                var ok = delivered && !rejected.Contains(batch);
                Count(batch.TapId, ok ? 1 : 0, ok ? 0 : 1);
            }

            Interlocked.Add(ref _pending, -buffer.Count);
        }
    }

    /// <summary>
    /// Writes <paramref name="batches"/>; durable writes are retried until the sink accepts them. Batches the sink
    /// rejects permanently are removed from the retry set and reported, so they cannot block the batches behind them.
    /// </summary>
    private async Task<bool> WriteAsync(List<LogBatch> batches, bool durable, HashSet<LogBatch> rejected, CancellationToken token)
    {
        var delay = TimeSpan.FromMilliseconds(10);
        while (true)
        {
            try
            {
                await _sink.WriteAsync(batches, token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (LogBatchRejectedException ex)
            {
                foreach (var batch in ex.Batches)
                {
                    if (rejected.Add(batch))
                    {
                        Interlocked.Increment(ref _rejected);
                        NotifyRejected(batch, ex);
                    }
                }

                // The sink wrote everything else in this call.
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StrataRec log sink failed: {ex.Message}");
                if (!durable)
                {
                    return false;
                }
            }

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
    }

    private void NotifyRejected(LogBatch batch, Exception reason)
    {
        try
        {
            _options.Rejected?.Invoke(batch, reason);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StrataRec rejected-batch handler failed: {ex.Message}");
        }
    }

    private void Count(string tap, long delivered, long dropped)
    {
        var counters = _stats.GetOrAdd(tap, static _ => new long[2]);
        Interlocked.Add(ref counters[0], delivered);
        Interlocked.Add(ref counters[1], dropped);
    }
}
