using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;
using StrataRec.Hosting;
using StrataRec.Logging;

namespace StrataRec.UnitTests.Logging;

/// <summary>
/// What the log pipeline's options allow and its accounting of each batch (pending count, queue bytes, the
/// queue-depth series, per-tap counters) when the host's diagnostics misbehave or a consumer is quick.
/// </summary>
/// <remarks>
/// The metric tests listen to the process-wide queue-depth series, and count only the measurements of their own pipeline:
/// the pipeline's consumers run in the execution context it was made in, so an <see cref="AsyncLocal{T}"/> set before it
/// is made tells its measurements from those of pipelines other tests run meanwhile.
/// </remarks>
public sealed class LogPipelineAccountingTests
{
    private static readonly AsyncLocal<object?> Owner = new();

    /// <summary>A timer's limit: the longest wait <c>Task.Delay</c> and <c>WaitAsync</c> take, 2^32 − 2 ms.</summary>
    private const double TimerLimitMs = uint.MaxValue - 1.0;

    /// <summary>
    /// Disposal waits for the consumers for the drain timeout and 500 ms more: a drain timeout up to the timer's limit
    /// passed the check, and disposal then threw <see cref="ArgumentOutOfRangeException"/> instead of draining. A retry
    /// delay of one tick passed though a timer waits whole milliseconds, so a sink that kept failing was retried in a busy
    /// loop (49,039 attempts in 79 ms). Both limits are accepted, a step past either is refused, where the pipeline is made
    /// and where a host registers it.
    /// </summary>
    [Theory]
    [InlineData(TimerLimitMs - 500, 1.0, null)]
    [InlineData(TimerLimitMs - 499, 1.0, "DrainTimeout")]
    [InlineData(TimerLimitMs, 1.0, "DrainTimeout")]
    [InlineData(10_000, 0.9999, "MaxRetryDelay")]
    [InlineData(10_000, 0.0001, "MaxRetryDelay")] // one tick
    public void TheTimings_AreRefusedPastWhatTheTimersCanWait(double drainMs, double retryMs, string? refused)
    {
        var options = new LogPipelineOptions { DrainTimeout = TimeSpan.FromMilliseconds(drainMs), MaxRetryDelay = TimeSpan.FromMilliseconds(retryMs) };

        if (refused is null)
        {
            options.Validate();
            Assert.Equal(options.DrainTimeout, LogPipelineOptions.MaxDrainTimeout);
            Assert.Equal(options.MaxRetryDelay, LogPipelineOptions.MinRetryDelay);
            return;
        }

        var made = Assert.Throws<ArgumentOutOfRangeException>(() => new LogPipeline(new InMemoryLogSink(), options));
        var registered = Assert.Throws<ArgumentOutOfRangeException>(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddStrataRec(o => (o.LogSink, o.LogPipeline) = (new InMemoryLogSink(), options)));
        Assert.Equal((refused, refused), (made.ParamName, registered.ParamName));
    }

    /// <summary>
    /// At the longest drain timeout, disposal with a write in flight waits for it and returns once it is written: its wait
    /// (the drain timeout and the grace) is within a timer's limit.
    /// </summary>
    [Fact]
    public async Task AtTheLongestDrainTimeout_DisposalWaitsForTheWriteInFlight()
    {
        var sink = new GateSink();
        var pipeline = new LogPipeline(sink, new LogPipelineOptions { DrainTimeout = LogPipelineOptions.MaxDrainTimeout });
        Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("served", LogDelivery.Durable)));
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var disposing = pipeline.DisposeAsync().AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(disposing.IsCompleted); // draining, not failed
        sink.Release.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains(pipeline.Stats(), s => s is { TapId: "served", Delivered: 1, Dropped: 0 });
    }

    /// <summary>
    /// The first wait before a failed durable write is retried was 10 ms whatever <see cref="LogPipelineOptions.MaxRetryDelay"/>
    /// said; it is bounded by it, as every later one is.
    /// </summary>
    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData(null, 5_000, 10)]
    [InlineData(10, 5_000, 20)]
    [InlineData(10, 15, 15)]
    [InlineData(4_000, 5_000, 5_000)]
    public void EveryRetryDelay_IsBoundedByTheLongest(int? lastMs, int longestMs, int expectedMs)
    {
        var last = lastMs is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), LogPipeline.RetryDelay(last, TimeSpan.FromMilliseconds(longestMs)));
    }

    /// <summary>
    /// The producer published the batch's +1 only after the batch was queued, so a consumer that wrote and settled it at
    /// once could publish its −1 first: the series went below zero (5 times in 60,000 submissions). Here the producer's +1
    /// waits up to a second for a −1 to come first, which it can only do while the batch is already queued.
    /// </summary>
    [Fact]
    public async Task TheQueueDepth_CountsABatchIn_BeforeAConsumerCanCountItOut()
    {
        using var depth = new DepthSeries(waitForOutOnIn: TimeSpan.FromSeconds(1), delayOut: TimeSpan.Zero);
        await using var pipeline = depth.Create(new InMemoryLogSink());

        Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("served", LogDelivery.Durable)));
        Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("debug", LogDelivery.BestEffort)));
        await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal((0L, 0L), (depth.Lowest, depth.Current));
    }

    /// <summary>
    /// A settled write dropped the pending count before it published the depth's −1, so a flush could return while the
    /// series still counted the batches. Here a −1 takes 200 ms to be recorded: a flush returns only after it is.
    /// </summary>
    [Fact]
    public async Task AFlush_ReturnsOnlyOnceTheQueueDepthIsBackDown()
    {
        using var depth = new DepthSeries(waitForOutOnIn: TimeSpan.Zero, delayOut: TimeSpan.FromMilliseconds(200));
        await using var pipeline = depth.Create(new InMemoryLogSink());

        Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("served", LogDelivery.Durable)));
        await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0L, depth.Current);
    }

    /// <summary>
    /// The host's logger threw when the consumer logged a failing sink (and when it logged the recovery): the exception
    /// left the consumer, which never settled the batches it had taken, so a flush never returned, their bytes stayed in
    /// the budget, and nothing more was written. The logger loses its records; the batches are settled as ever, durable
    /// ones retried until written, best-effort ones dropped, and the consumers go on with the next batches.
    /// </summary>
    [Theory]
    [InlineData(LogDelivery.Durable)]
    [InlineData(LogDelivery.BestEffort)]
    public async Task ALoggerThatThrows_LosesOnlyItsRecords(LogDelivery delivery)
    {
        var durable = delivery == LogDelivery.Durable;
        var sink = new FailingSink(failures: durable ? 1 : int.MaxValue); // durable: fails once, then recovers
        var logger = new ThrowingLoggerFactory();
        await using var pipeline = new LogPipeline(sink, loggerFactory: logger);

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("served", delivery)));
            await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.True(logger.Thrown > 0);
        Assert.Contains(pipeline.Stats(), s => s is { TapId: "served" } && (s.Delivered, s.Dropped) == (durable ? (2, 0) : (0, 2)));
        Assert.Equal((0L, 0L), (pipeline.DurableQueuedBytes, pipeline.BestEffortQueuedBytes));
        Assert.Equal(durable ? 2 : 0, sink.Written);
    }

    /// <summary>
    /// A metric listener threw while the consumer settled a write (counting a tap's batches): the settlement stopped half
    /// way, leaving the batches pending and their bytes in the budget, and the consumer ended. The listener loses its
    /// measurements; the settlement is whole, and the next batches are written.
    /// </summary>
    [Fact]
    public async Task AMetricListenerThatThrows_LosesOnlyItsMeasurements()
    {
        using var listener = new MeterListener();
        var marker = new object();
        var thrown = 0;
        listener.InstrumentPublished = (instrument, self) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name == StrataRecMetrics.Names.LogBatches)
            {
                self.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (ReferenceEquals(Owner.Value, marker))
            {
                Interlocked.Increment(ref thrown);
                throw new InvalidOperationException("the listener is broken");
            }
        });
        listener.Start();
        Owner.Value = marker;
        var sink = new InMemoryLogSink();
        await using var pipeline = new LogPipeline(sink);

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("served", LogDelivery.Durable)));
            await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, Volatile.Read(ref thrown));
        Assert.Contains(pipeline.Stats(), s => s is { TapId: "served", Delivered: 2, Dropped: 0 });
        Assert.Equal(0L, pipeline.DurableQueuedBytes);
        Assert.Equal(2, sink.Snapshot().Count);
    }

    /// <summary>
    /// The sink wrote one batch of a write and rejected the other, and the host's rejection callback threw an
    /// exception whose message threw as well. Describing it threw out of the write, which was then settled as a failure:
    /// both batches counted as dropped, the one the sink had written among them. The outcome is settled before the
    /// callback runs, and the exception is described by its type.
    /// </summary>
    [Fact]
    public async Task ARejectionCallbackThatThrows_CannotCountTheBatchesTheSinkWroteAsDropped()
    {
        var sink = new ScriptedSink();
        var logger = new RecordingLoggerFactory(throwOn: null);
        var called = 0;
        var pipeline = new LogPipeline(sink, new LogPipelineOptions
        {
            Rejected = (_, _) =>
            {
                Interlocked.Increment(ref called);
                throw new UnreadableMessageException();
            },
        }, loggerFactory: logger);

        // The first write is held until both batches are queued, so the second write takes them together.
        Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("first", LogDelivery.Durable)));
        await sink.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("written", LogDelivery.Durable)));
        Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("rejected", LogDelivery.Durable)));
        sink.Release.SetResult();
        await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await pipeline.DisposeAsync();

        Assert.Equal(1, Volatile.Read(ref called));
        Assert.Equal(["first", "written"], sink.Written);
        var stats = pipeline.Stats();
        Assert.Contains(stats, s => s is { TapId: "written", Delivered: 1, Dropped: 0 });
        Assert.Contains(stats, s => s is { TapId: "rejected", Delivered: 0, Dropped: 1 });
        Assert.Equal((1L, 0L), (pipeline.Rejected, pipeline.DurableQueuedBytes));
        Assert.Contains(logger.Records, r => r.EventId == 2001 && r.Message.Contains(
            $"LogRejectedHandlerFailed for 'rejected': 1 time(s)", StringComparison.Ordinal) && r.Message.Contains(
            $"{typeof(UnreadableMessageException).FullName} (reading its message threw)", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Records, r => r.EventId == 3006);
    }

    /// <summary>
    /// The logger threw when disposal logged the drain timeout (3003), a sink that did not stop (3004), or the
    /// last window's summaries (2001): the exception left <see cref="LogPipeline.DisposeAsync"/> and the rest of its
    /// cleanup was skipped, the summaries among it. The logger loses that record; disposal completes, and writes the
    /// summaries.
    /// </summary>
    [Theory]
    [InlineData(3003)]
    [InlineData(3004)]
    [InlineData(2001)]
    public async Task ALoggerThatThrowsWhileThePipelineIsDisposed_LosesOnlyThatRecord(int throwOn)
    {
        var sink = new ScriptedSink { Hold = throwOn == 3003 ? Holding.UntilCancelled : throwOn == 3004 ? Holding.IgnoringCancellation : Holding.None };
        var logger = new RecordingLoggerFactory(throwOn);
        var pipeline = new LogPipeline(sink, new LogPipelineOptions { DrainTimeout = TimeSpan.FromMilliseconds(50) }, loggerFactory: logger);

        // A rejection leaves a summary for the last window.
        Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("rejected", LogDelivery.BestEffort)));
        await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (sink.Hold != Holding.None)
        {
            Assert.Equal(LogAdmission.Queued, pipeline.Submit(LogPipelineTests.Batch("first", LogDelivery.Durable)));
            await sink.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        try
        {
            await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            sink.Release.TrySetResult();
        }

        Assert.Contains(logger.Attempts, id => id == throwOn);
        if (throwOn != 2001)
        {
            Assert.Contains(logger.Records, r => r.EventId == 2001 && r.Message.Contains("LogBatchRejected for 'rejected'", StringComparison.Ordinal));
        }
    }

    /// <summary>A sink that rejects the batches of the tap <c>rejected</c>, and can hold the write of the tap <c>first</c>.</summary>
    private sealed class ScriptedSink : ILogSink
    {
        private readonly List<string> _written = new();

        public Holding Hold { get; init; } = Holding.UntilReleased;

        public TaskCompletionSource HeldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Written
        {
            get
            {
                lock (_written)
                {
                    return new List<string>(_written);
                }
            }
        }

        public async ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken)
        {
            if (batches.Any(b => b.TapId == "first"))
            {
                HeldStarted.TrySetResult();
                switch (Hold)
                {
                    case Holding.UntilReleased:
                        await Release.Task;
                        break;
                    case Holding.UntilCancelled:
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        break;
                    case Holding.IgnoringCancellation:
                        await Release.Task;
                        break;
                }
            }

            var rejected = batches.Where(b => b.TapId == "rejected").ToList();
            lock (_written)
            {
                _written.AddRange(batches.Where(b => b.TapId != "rejected").Select(b => b.TapId));
            }

            if (rejected.Count > 0)
            {
                throw new LogBatchRejectedException(rejected);
            }
        }
    }

    private enum Holding
    {
        None,
        UntilReleased,
        UntilCancelled,
        IgnoringCancellation,
    }

    /// <summary>An exception of a host's whose message cannot be read.</summary>
    private sealed class UnreadableMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("the message cannot be read");
    }

    /// <summary>Loggers that record what they write, and throw on one event id.</summary>
    private sealed class RecordingLoggerFactory(int? throwOn) : ILoggerFactory, ILogger
    {
        private readonly List<(int EventId, string Message)> _records = new();
        private readonly List<int> _attempts = new();

        public List<(int EventId, string Message)> Records
        {
            get
            {
                lock (_records)
                {
                    return new List<(int, string)>(_records);
                }
            }
        }

        public List<int> Attempts
        {
            get
            {
                lock (_records)
                {
                    return new List<int>(_attempts);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_records)
            {
                _attempts.Add(eventId.Id);
                if (eventId.Id == throwOn)
                {
                    throw new InvalidOperationException("the logger is broken");
                }

                _records.Add((eventId.Id, formatter(state, exception)));
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The queue-depth measurements of the pipelines it creates. A +1 can wait for a −1 to be recorded first (which only a
    /// batch already visible to its consumer can give), and a −1 can take a while to be recorded.
    /// </summary>
    private sealed class DepthSeries : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly object _marker = new();
        private readonly TimeSpan _waitForOutOnIn;
        private readonly TimeSpan _delayOut;
        private long _current;
        private long _lowest;
        private int _outs;

        public DepthSeries(TimeSpan waitForOutOnIn, TimeSpan delayOut)
        {
            _waitForOutOnIn = waitForOutOnIn;
            _delayOut = delayOut;
            _listener.InstrumentPublished = (instrument, self) =>
            {
                if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name == StrataRecMetrics.Names.LogQueueDepth)
                {
                    self.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, delta, _, _) => Record(delta));
            _listener.Start();
        }

        public long Current => Interlocked.Read(ref _current);

        public long Lowest => Interlocked.Read(ref _lowest);

        /// <summary>
        /// A pipeline whose measurements this series records (see the class remarks): those of its consumers, and those of
        /// the submissions made from the caller's flow from now on.
        /// </summary>
        public LogPipeline Create(ILogSink sink)
        {
            Owner.Value = _marker;
            return new LogPipeline(sink);
        }

        public void Dispose() => _listener.Dispose();

        private void Record(long delta)
        {
            if (!ReferenceEquals(Owner.Value, _marker))
            {
                return;
            }

            if (delta > 0 && _waitForOutOnIn > TimeSpan.Zero)
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref _outs) > 0, _waitForOutOnIn);
            }

            if (delta < 0 && _delayOut > TimeSpan.Zero)
            {
                Thread.Sleep(_delayOut);
            }

            var now = Interlocked.Add(ref _current, delta);
            if (delta < 0)
            {
                Interlocked.Increment(ref _outs);
            }

            long lowest;
            while (now < (lowest = Interlocked.Read(ref _lowest)) && Interlocked.CompareExchange(ref _lowest, now, lowest) != lowest)
            {
            }
        }
    }

    private sealed class GateSink : ILogSink
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailingSink(int failures) : ILogSink
    {
        private int _attempts;
        private int _written;

        public int Written => Volatile.Read(ref _written);

        public ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) <= failures)
            {
                throw new IOException("sink unavailable");
            }

            Interlocked.Add(ref _written, batches.Count);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Loggers that are enabled and throw on every record.</summary>
    private sealed class ThrowingLoggerFactory : ILoggerFactory, ILogger
    {
        private int _thrown;

        public int Thrown => Volatile.Read(ref _thrown);

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _thrown);
            throw new InvalidOperationException("the logger is broken");
        }

        public void Dispose()
        {
        }
    }
}
