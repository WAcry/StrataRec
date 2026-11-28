using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Threading;

namespace StrataRec.Logging;

/// <summary>Queue sizes and timing of a <see cref="LogPipeline"/>.</summary>
public sealed class LogPipelineOptions
{
    /// <summary>
    /// Memory the best-effort queue may hold (<see cref="LogBatch.ApproximateBytes"/>) before new batches are dropped
    /// (and counted).
    /// </summary>
    public long BestEffortMaxBytes { get; init; } = 64L << 20;

    /// <summary>
    /// Memory the durable queue may hold in memory. A batch that does not fit goes to <see cref="DurableOverflow"/>;
    /// without one it is lost, counted in <see cref="LogPipeline.DurableOverflow"/>, and leaves a sequence gap downstream.
    /// A sink that stops accepting writes fills this, and only this, so it bounds what a stuck sink costs the process.
    /// </summary>
    public long DurableMaxBytes { get; init; } = 256L << 20;

    /// <summary>
    /// Spill hook for durable batches the memory queue cannot take (the local disk buffer). Called
    /// on the request thread with the batch, which already carries its <c>seq</c>; return true when the batch was
    /// persisted and will be delivered later (e.g. appended to a local file replayed at startup). Must be fast.
    /// </summary>
    public Func<LogBatch, bool>? DurableOverflow { get; init; }

    /// <summary>
    /// Receives batches a sink rejected permanently (<see cref="LogBatchRejectedException"/>): they are never retried,
    /// so a malformed batch cannot block the durable stream behind it.
    /// </summary>
    public Action<LogBatch, Exception>? Rejected { get; init; }

    /// <summary>Batches handed to the sink per write; at least 1.</summary>
    public int MaxBatchesPerWrite { get; init; } = 256;

    /// <summary>
    /// How long <see cref="LogPipeline.DisposeAsync"/> keeps draining durable batches, at most
    /// <see cref="MaxDrainTimeout"/>. Disposal returns at most half a second later even if the sink ignores cancellation.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The longest wait between two attempts to write a durable batch the sink failed; the waits double from 10 ms (from
    /// this, when it is shorter) up to it. At least <see cref="MinRetryDelay"/>: without a wait a failing sink would be
    /// retried in a busy loop.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait a timer takes (2^32 − 2 ms, about 49.7 days).</summary>
    private static readonly TimeSpan LongestWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    /// <summary>
    /// The longest <see cref="DrainTimeout"/>: disposal waits for the consumers for the drain timeout and half a second
    /// more, in one timer's wait.
    /// </summary>
    public static readonly TimeSpan MaxDrainTimeout = LongestWait - LogPipeline.ShutdownGrace;

    /// <summary>
    /// The shortest <see cref="MaxRetryDelay"/>, 1 ms: a timer waits whole milliseconds, so a shorter delay is none.
    /// </summary>
    public static readonly TimeSpan MinRetryDelay = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Throws an <see cref="ArgumentOutOfRangeException"/> naming the first option out of its range. The pipeline
    /// checks its options before it starts its consumers, and a host checks them when it registers the pipeline: a
    /// value that cannot work fails at startup, not once logs are written.
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DurableMaxBytes, nameof(DurableMaxBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BestEffortMaxBytes, nameof(BestEffortMaxBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchesPerWrite, nameof(MaxBatchesPerWrite));
        if (DrainTimeout < TimeSpan.Zero || DrainTimeout > MaxDrainTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(DrainTimeout), DrainTimeout,
                $"DrainTimeout must be between 0 and {MaxDrainTimeout}: disposal waits {LogPipeline.ShutdownGrace.TotalMilliseconds:0} ms longer, " +
                $"and a timer waits at most {LongestWait}.");
        }

        if (MaxRetryDelay < MinRetryDelay || MaxRetryDelay > LongestWait)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), MaxRetryDelay,
                $"MaxRetryDelay must be between {MinRetryDelay} and {LongestWait}: a timer waits whole milliseconds, so a shorter delay " +
                "would retry a failing sink in a busy loop.");
        }
    }
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

/// <summary>Delivery counters of one tap (drops are counted so offline weights can be corrected).</summary>
public readonly record struct TapLogStats(string TapId, long Delivered, long Dropped);

/// <summary>
/// The asynchronous log pipeline: request threads only enqueue finished batches; background loops write
/// them to an <see cref="ILogSink"/>. Durable and best-effort batches use separate bounded queues so a burst of
/// diagnostics never delays training logs. Durable batches get consecutive per-process sequence numbers and are
/// retried until the sink accepts them (or rejects them permanently); best-effort batches are dropped when their queue
/// is full or a write fails.
/// </summary>
/// <remarks>
/// Observability: queue depth, per-tap delivery, durable overflow and rejections are metrics
/// (<see cref="StrataRecMetrics"/>). A failing sink is logged when it starts failing and when it recovers, not per write;
/// failures of the host's hooks are summarized per window.
/// </remarks>
public sealed partial class LogPipeline : IAsyncDisposable
{
    private static readonly KeyValuePair<string, object?>[] DurableTags = [new(StrataRecTags.LogDelivery, "durable")];
    private static readonly KeyValuePair<string, object?>[] BestEffortTags = [new(StrataRecTags.LogDelivery, "best_effort")];

    private readonly ILogSink _sink;
    private readonly LogPipelineOptions _options;
    private readonly ILogger _logger;
    private readonly RequestEventLog? _events;
    private readonly Channel<LogBatch> _durable;
    private readonly Channel<LogBatch> _bestEffort;
    private readonly Task _durableLoop;
    private readonly Task _bestEffortLoop;
    private readonly SinkStop _stop;
    private readonly ConcurrentDictionary<string, TapCounters> _stats = new(StringComparer.Ordinal);
    /// <summary>Consecutive failed writes per loop (0: durable, 1: best-effort); each loop touches only its own.</summary>
    private readonly long[] _failedWrites = new long[2];
    internal static readonly TimeSpan ShutdownGrace = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(10);

    private long _seq;
    private long _pending;
    private long _durableBytes;
    private long _bestEffortBytes;
    private int _disposed;

    /// <param name="loggerFactory">Receives sink failures and recoveries and hook failures; null logs nothing.</param>
    public LogPipeline(ILogSink sink, LogPipelineOptions? options = null, string? producerId = null, ILoggerFactory? loggerFactory = null)
    {
        _sink = sink;
        _options = options ?? new LogPipelineOptions();
        _logger = loggerFactory?.CreateLogger<LogPipeline>() ?? NullLogger<LogPipeline>.Instance;
        _events = loggerFactory is null ? null : new RequestEventLog(_logger);
        _stop = new SinkStop(_logger);
        ProducerId = producerId ?? $"{Environment.MachineName}/{Environment.ProcessId}/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        _options.Validate();

        // Unbounded channels, bounded by the bytes their batches hold (Reserve): a writer never waits either way.
        _durable = Channel.CreateUnbounded<LogBatch>(new UnboundedChannelOptions { SingleReader = true });
        _bestEffort = Channel.CreateUnbounded<LogBatch>(new UnboundedChannelOptions { SingleReader = true });
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
    /// spilled or dropped, so publishers and metrics can see durable loss as it happens.
    /// </summary>
    public LogAdmission Submit(LogBatch batch)
    {
        batch.ProducerId = ProducerId;
        var durable = batch.Delivery == LogDelivery.Durable;
        if (Volatile.Read(ref _disposed) != 0)
        {
            Count(batch.TapId, delivered: 0, dropped: 1);
            if (durable)
            {
                Overflow();
            }

            return LogAdmission.Closed;
        }

        if (durable)
        {
            batch.Seq = Interlocked.Increment(ref _seq);
        }

        if (TryQueue(batch, durable))
        {
            return LogAdmission.Queued;
        }

        if (durable)
        {
            if (Spill(batch))
            {
                return LogAdmission.Spilled;
            }

            Overflow();
        }

        Count(batch.TapId, 0, 1);
        return LogAdmission.Dropped;
    }

    /// <summary>
    /// Queues a batch within its queue's budget. The pending count, the bytes and the queue depth are taken before its
    /// consumer can see it, and given back on every way it is not queued.
    /// </summary>
    private bool TryQueue(LogBatch batch, bool durable)
    {
        var bytes = batch.ApproximateBytes;
        ref var budget = ref durable ? ref _durableBytes : ref _bestEffortBytes;
        Interlocked.Increment(ref _pending);
        var reserved = false;
        var queued = false;
        try
        {
            reserved = Reserve(ref budget, bytes, durable ? _options.DurableMaxBytes : _options.BestEffortMaxBytes);
            if (reserved)
            {
                QueueDepth(1, durable);
                queued = (durable ? _durable : _bestEffort).Writer.TryWrite(batch);
                if (!queued)
                {
                    QueueDepth(-1, durable); // completed by a concurrent dispose
                }
            }
        }
        finally
        {
            if (!queued)
            {
                if (reserved)
                {
                    Interlocked.Add(ref budget, -bytes);
                }

                Interlocked.Decrement(ref _pending);
            }
        }

        return queued;
    }

    /// <summary>Memory the durable queue holds now (<see cref="LogBatch.ApproximateBytes"/> of its batches).</summary>
    public long DurableQueuedBytes => Interlocked.Read(ref _durableBytes);

    /// <summary>Memory the best-effort queue holds now.</summary>
    public long BestEffortQueuedBytes => Interlocked.Read(ref _bestEffortBytes);

    /// <summary>
    /// A batch larger than the whole budget still goes into an empty queue, so one huge batch is delayed behind nothing
    /// rather than never delivered.
    /// </summary>
    private static bool Reserve(ref long queued, long bytes, long max)
    {
        while (true)
        {
            var current = Interlocked.Read(ref queued);
            if (current > 0 && current + bytes > max)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref queued, current + bytes, current) == current)
            {
                return true;
            }
        }
    }

    private void Overflow()
    {
        Interlocked.Increment(ref _durableOverflow);
        Measure(static () => StrataRecMetrics.LogDurableOverflow, 1, default);
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
            _events?.Report(RequestEvent.LogSpillFailed, batch.TapId, batch.RequestId, ExceptionText.Of(ex));
            return false;
        }
    }

    /// <summary>Delivery counters per tap, sorted by tap id.</summary>
    public List<TapLogStats> Stats()
    {
        var list = new List<TapLogStats>();
        foreach (var (tap, counters) in _stats)
        {
            list.Add(new TapLogStats(tap, Interlocked.Read(ref counters.Delivered), Interlocked.Read(ref counters.Dropped)));
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
        _stop.CancelAfterObserved(_options.DrainTimeout);
        var stuck = false;
        try
        {
            await Task.WhenAll(_durableLoop, _bestEffortLoop).WaitAsync(_options.DrainTimeout + ShutdownGrace).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Diagnose(static (logger, self) => LogDrainTimedOut(logger, self._options.DrainTimeout.TotalSeconds, Interlocked.Read(ref self._pending)));
        }
        catch (TimeoutException)
        {
            stuck = true; // the loops still use _stop; leave it to the finalizer
            Diagnose(static (logger, self) => LogSinkStuck(logger, self._options.DrainTimeout.TotalSeconds, Interlocked.Read(ref self._pending)));
        }
        finally
        {
            Diagnose(static (_, self) => self._events?.Dispose());
            if (!stuck)
            {
                _stop.Dispose();
            }
        }
    }

    /// <summary>A logger that throws loses the record and nothing else: the disposal's cleanup goes on.</summary>
    private void Diagnose(Action<ILogger, LogPipeline> write)
    {
        try
        {
            write(_logger, this);
        }
        catch (Exception)
        {
        }
    }

    /// <remarks>
    /// Every batch taken is settled exactly once, however the iteration ends: the write returns, or something on its
    /// way throws, and then the batches are counted as dropped (whether the sink has them is not known) and the loop
    /// goes on.
    /// </remarks>
    private async Task RunAsync(ChannelReader<LogBatch> reader, bool durable)
    {
        var buffer = new List<LogBatch>(_options.MaxBatchesPerWrite);
        var rejected = new HashSet<LogBatch>(ReferenceEqualityComparer.Instance);
        var token = _stop.Token;
        try
        {
            // The batches are taken and settled outside this method, so no local of its state machine keeps one alive
            // while it waits for the next.
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                Take(reader, buffer);
                var delivered = false;
                try
                {
                    (delivered, var rejection) = await WriteAsync(buffer, durable, rejected, token).ConfigureAwait(false);
                    if (rejection is not null)
                    {
                        // The outcome is settled before the host hears of the rejection: nothing its callback does, or
                        // what it throws, can count the batches the sink wrote as dropped.
                        ReportRejected(rejected, rejection);
                    }
                }
                catch (Exception ex)
                {
                    ConsumerFaulted(durable, buffer.Count, ex);
                }
                finally
                {
                    Settle(buffer, rejected, delivered, durable);
                }
            }
        }
        finally
        {
            // The drain timeout cut shutdown short: batches still queued will never be written. Only this loop reads
            // its queue, so nothing is settled twice.
            var abandoned = 0;
            long bytes = 0;
            while (reader.TryRead(out var batch))
            {
                Count(batch.TapId, 0, 1);
                abandoned++;
                bytes += batch.ApproximateBytes;
            }

            if (abandoned > 0)
            {
                Interlocked.Add(ref durable ? ref _durableBytes : ref _bestEffortBytes, -bytes);
                QueueDepth(-abandoned, durable);
                Interlocked.Add(ref _pending, -abandoned);
            }
        }
    }

    private void Take(ChannelReader<LogBatch> reader, List<LogBatch> buffer)
    {
        while (buffer.Count < _options.MaxBatchesPerWrite && reader.TryRead(out var batch))
        {
            buffer.Add(batch);
        }
    }

    /// <summary>
    /// Counts each batch as delivered or dropped, gives its bytes back and lets go of it, all before the pending count
    /// drops: a completed <see cref="FlushAsync"/> holds none. Never throws.
    /// </summary>
    private void Settle(List<LogBatch> buffer, HashSet<LogBatch> rejected, bool delivered, bool durable)
    {
        long written = 0;
        foreach (var batch in buffer)
        {
            var ok = delivered && !rejected.Contains(batch);
            Count(batch.TapId, ok ? 1 : 0, ok ? 0 : 1);
            written += batch.ApproximateBytes;
        }

        var count = buffer.Count;
        buffer.Clear();
        rejected.Clear();

        // The batches stay in the queue's budget until the sink has them: a stuck sink fills the budget, not memory.
        Interlocked.Add(ref durable ? ref _durableBytes : ref _bestEffortBytes, -written);
        QueueDepth(-count, durable);
        Interlocked.Add(ref _pending, -count);
    }

    /// <returns>Whether the sink has the batches (all but the rejected ones), and its rejection, if any.</returns>
    private async Task<(bool Delivered, LogBatchRejectedException? Rejection)> WriteAsync(
        List<LogBatch> batches, bool durable, HashSet<LogBatch> rejected, CancellationToken token)
    {
        var delay = RetryDelay(null, _options.MaxRetryDelay);
        while (true)
        {
            try
            {
                await _sink.WriteAsync(batches, token).ConfigureAwait(false);
                SinkSucceeded(durable);
                return (true, null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return (false, null);
            }
            catch (LogBatchRejectedException ex)
            {
                foreach (var batch in ex.Batches)
                {
                    if (rejected.Add(batch))
                    {
                        Interlocked.Increment(ref _rejected);
                        Measure(static () => StrataRecMetrics.LogRejected, 1, default);
                    }
                }

                SinkSucceeded(durable);
                return (true, ex);
            }
            catch (Exception ex)
            {
                SinkFailed(durable, ex);
                if (!durable)
                {
                    return (false, null);
                }
            }

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (false, null);
            }

            delay = RetryDelay(delay, _options.MaxRetryDelay);
        }
    }

    /// <summary>Reports each rejected batch once the write's outcome is settled, so a callback that throws cannot
    /// change it. Never throws.</summary>
    private void ReportRejected(HashSet<LogBatch> rejected, LogBatchRejectedException reason)
    {
        var text = ExceptionText.Of(reason);
        foreach (var batch in rejected)
        {
            _events?.Report(RequestEvent.LogBatchRejected, batch.TapId, batch.RequestId, text);
            NotifyRejected(batch, reason);
        }
    }

    internal static TimeSpan RetryDelay(TimeSpan? last, TimeSpan longest) =>
        TimeSpan.FromTicks(Math.Min(last is { } previous ? previous.Ticks * 2 : FirstRetryDelay.Ticks, longest.Ticks));

    private void NotifyRejected(LogBatch batch, Exception reason)
    {
        try
        {
            _options.Rejected?.Invoke(batch, reason);
        }
        catch (Exception ex)
        {
            _events?.Report(RequestEvent.LogRejectedHandlerFailed, batch.TapId, batch.RequestId, ExceptionText.Of(ex));
        }
    }

    /// <summary>Logs the first failure of a run of failed writes; later ones are only counted. A logger that throws
    /// changes nothing else.</summary>
    private void SinkFailed(bool durable, Exception ex)
    {
        if (_failedWrites[durable ? 0 : 1]++ == 0)
        {
            try
            {
                LogSinkFailing(_logger, durable ? "durable" : "best_effort", ex);
            }
            catch (Exception)
            {
            }
        }
    }

    private void SinkSucceeded(bool durable)
    {
        ref var failed = ref _failedWrites[durable ? 0 : 1];
        if (failed > 0)
        {
            var failures = failed;
            failed = 0;
            try
            {
                LogSinkRecovered(_logger, durable ? "durable" : "best_effort", failures);
            }
            catch (Exception)
            {
            }
        }
    }

    private void ConsumerFaulted(bool durable, int batches, Exception ex)
    {
        try
        {
            LogConsumerFaulted(_logger, durable ? "durable" : "best_effort", batches, ex);
        }
        catch (Exception)
        {
        }
    }

    private void Count(string tap, long delivered, long dropped)
    {
        var counters = _stats.GetOrAdd(tap, static id => new TapCounters(id));
        if (delivered > 0)
        {
            Interlocked.Add(ref counters.Delivered, delivered);
            Measure(static () => StrataRecMetrics.LogBatches, delivered, counters.DeliveredTags);
        }

        if (dropped > 0)
        {
            Interlocked.Add(ref counters.Dropped, dropped);
            Measure(static () => StrataRecMetrics.LogBatches, dropped, counters.DroppedTags);
        }
    }

    /// <summary>
    /// A batch is counted in before it is queued and counted out before the pending count drops: the series never goes
    /// below zero, and a flush returns after the last decrement.
    /// </summary>
    private static void QueueDepth(long delta, bool durable) =>
        Measure(static () => StrataRecMetrics.LogQueueDepth, delta, durable ? DurableTags : BestEffortTags);

    /// <summary>
    /// What a listener throws is lost with its measurement. The instrument is read inside the isolation too: its first
    /// read runs the initializer, which tells every listener.
    /// </summary>
    private static void Measure(Func<Counter<long>> counter, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        try
        {
            counter().Add(value, tags);
        }
        catch (Exception)
        {
        }
    }

    private static void Measure(Func<UpDownCounter<long>> counter, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        try
        {
            counter().Add(value, tags);
        }
        catch (Exception)
        {
        }
    }

    private sealed class SinkStop(ILogger logger) : ObservedCancellationSource(CancellationSite.LogDrain)
    {
        protected override void CallbacksFailed(AggregateException errors) => LogSinkCancellationCallbacksFailed(logger, errors);
    }

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning,
        Message = "A cancellation callback of the log sink's token threw when the drain timeout ran out; shutdown is not affected")]
    private static partial void LogSinkCancellationCallbacksFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error,
        Message = "Log sink failed writing {Delivery} batches; durable batches are retried, best-effort batches are dropped until it recovers")]
    private static partial void LogSinkFailing(ILogger logger, string delivery, Exception exception);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Log sink recovered writing {Delivery} batches after {FailedWrites} failed write(s)")]
    private static partial void LogSinkRecovered(ILogger logger, string delivery, long failedWrites);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "Log pipeline stopped draining after {DrainTimeoutSeconds} s with {Pending} batch(es) unwritten; durable ones leave a sequence gap")]
    private static partial void LogDrainTimedOut(ILogger logger, double drainTimeoutSeconds, long pending);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error,
        Message = "Writing {Batches} {Delivery} log batch(es) failed for a reason other than the sink; they are counted as dropped, and the pipeline goes on")]
    private static partial void LogConsumerFaulted(ILogger logger, string delivery, int batches, Exception exception);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning,
        Message = "Log sink did not stop within the {DrainTimeoutSeconds} s drain timeout; abandoning in-flight writes, with {Pending} batches not yet settled")]
    private static partial void LogSinkStuck(ILogger logger, double drainTimeoutSeconds, long pending);

    private sealed class TapCounters(string tap)
    {
        public long Delivered;
        public long Dropped;

        public KeyValuePair<string, object?>[] DeliveredTags { get; } = [new(StrataRecTags.Tap, tap), new(StrataRecTags.Outcome, "delivered")];

        public KeyValuePair<string, object?>[] DroppedTags { get; } = [new(StrataRecTags.Tap, tap), new(StrataRecTags.Outcome, "dropped")];
    }
}
