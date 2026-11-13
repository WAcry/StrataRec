using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace StrataRec.Core.Diagnostics;

/// <summary>Request-level failures that are summarized per time window instead of logged one by one (design 13.2).</summary>
public enum RequestEvent : byte
{
    FeatureSourceTimeout = 1,
    FeatureSourceFailed,
    CandidateSourceTimeout,
    CandidateSourceFailed,
    ModelTimeout,
    ModelFailed,

    /// <summary>A node failed and its fallback node served.</summary>
    NodeFallback,

    /// <summary>A node failed and was skipped, or failed the request.</summary>
    NodeFailed,

    /// <summary>An aborted request's logs could not be published.</summary>
    LogPublishFailed,

    /// <summary>The host's <c>DurableOverflow</c> hook threw.</summary>
    LogSpillFailed,

    /// <summary>The host's <c>Rejected</c> handler threw.</summary>
    LogRejectedHandlerFailed,

    /// <summary>The log sink rejected a batch permanently.</summary>
    LogBatchRejected,
}

/// <summary>
/// Turns request-level failures into event logs whose volume does not depend on traffic (design 13.2). Failures are
/// counted per (event, subject); every window (10 s by default) writes at most one summary line per pair, with the count
/// and one sample request. External dependencies are also judged per window: a dependency is failing when at least half
/// of its calls in a window failed, and a line is written only when that verdict flips.
/// </summary>
/// <remarks>
/// <para>The request path only increments counters in a slot found by a dictionary lookup on keys that already exist;
/// it allocates only the first time a pair is seen. Samples are references to strings the request already has.</para>
/// <para>A timer flushes the windows. It holds the log only weakly, so an engine that is dropped stops flushing and its
/// timer is finalized; <see cref="Dispose"/> flushes the last partial window.</para>
/// </remarks>
public sealed partial class RequestEventLog : IDisposable
{
    /// <summary>Default summary window.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(RequestEvent Event, string Subject), EventWindow> _events = new();
    private readonly ConcurrentDictionary<(IoKind Kind, string Name), HealthWindow> _health = new();
    private readonly object _flushGate = new();
    private readonly Timer? _timer;
    private long _windowStart;

    /// <param name="logger">Where summaries and health changes go.</param>
    /// <param name="window">Summary window; <see cref="DefaultWindow"/> when null.</param>
    /// <param name="autoFlush">False leaves flushing to the caller (tests).</param>
    public RequestEventLog(ILogger logger, TimeSpan? window = null, bool autoFlush = true)
    {
        _logger = logger;
        Window = window ?? DefaultWindow;
        _windowStart = Stopwatch.GetTimestamp();
        if (autoFlush)
        {
            // The timer must not capture the creator's execution context (it would pin its Activity.Current forever).
            var suppress = !ExecutionContext.IsFlowSuppressed();
            var flow = suppress ? ExecutionContext.SuppressFlow() : default;
            try
            {
                _timer = new Timer(static state => Tick(state), new WeakReference<RequestEventLog>(this), Window, Window);
            }
            finally
            {
                if (suppress)
                {
                    flow.Undo();
                }
            }
        }
    }

    public TimeSpan Window { get; }

    /// <summary>Counts one occurrence; the first one of a window becomes the sample.</summary>
    /// <param name="subject">A bounded name: source, model, <c>pipeline/node</c>, pipeline.</param>
    /// <param name="requestId">Sample request id.</param>
    /// <param name="detail">Sample error text.</param>
    public void Report(RequestEvent @event, string subject, string? requestId, string? detail)
    {
        _events.GetOrAdd((@event, subject), static _ => new EventWindow()).Add(requestId, detail);
    }

    /// <summary>Counts one call of an external dependency for its health, and its failure for the summaries.</summary>
    public void RecordIo(IoTarget target, IoOutcome outcome, string? requestId, string? error)
    {
        var failed = outcome is IoOutcome.Timeout or IoOutcome.Error;
        _health.GetOrAdd((target.Kind, target.Name), static _ => new HealthWindow()).Add(failed, error);
        if (failed)
        {
            Report(outcome == IoOutcome.Timeout ? target.TimeoutEvent : target.FailedEvent, target.Name, requestId, error);
        }
    }

    /// <summary>Closes the current window: writes one summary per (event, subject) seen and every health change.</summary>
    public void Flush()
    {
        lock (_flushGate)
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Round(Stopwatch.GetElapsedTime(_windowStart, now).TotalSeconds, 1);
            _windowStart = now;
            foreach (var (key, window) in _events)
            {
                var count = window.Take(out var request, out var detail);
                if (count > 0)
                {
                    LogSummary(_logger, key.Event, key.Subject, count, seconds, request, detail);
                }
            }

            foreach (var (key, health) in _health)
            {
                var calls = health.Take(out var failures, out var error);
                if (calls == 0)
                {
                    continue; // no evidence either way
                }

                var failing = failures * 2 >= calls;
                if (failing == health.Failing)
                {
                    continue;
                }

                health.Failing = failing;
                if (failing)
                {
                    LogDependencyFailing(_logger, key.Kind, key.Name, failures, calls, seconds, error);
                }
                else
                {
                    LogDependencyRecovered(_logger, key.Kind, key.Name, failures, calls, seconds);
                }
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Flush();
    }

    private static void Tick(object? state)
    {
        if (((WeakReference<RequestEventLog>)state!).TryGetTarget(out var log))
        {
            try
            {
                log.Flush();
            }
            catch (Exception)
            {
                // A throwing logger provider must not take the timer thread down; the next window tries again.
            }
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "{Event} for '{Subject}': {Count} time(s) in the last {WindowSeconds} s; sample request '{SampleRequestId}': {SampleError}")]
    private static partial void LogSummary(ILogger logger, RequestEvent @event, string subject, long count, double windowSeconds, string? sampleRequestId, string? sampleError);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error,
        Message = "{Kind} '{Name}' is failing: {Failures} of {Calls} call(s) failed in the last {WindowSeconds} s; sample error: {SampleError}")]
    private static partial void LogDependencyFailing(ILogger logger, IoKind kind, string name, long failures, long calls, double windowSeconds, string? sampleError);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information,
        Message = "{Kind} '{Name}' recovered: {Failures} of {Calls} call(s) failed in the last {WindowSeconds} s")]
    private static partial void LogDependencyRecovered(ILogger logger, IoKind kind, string name, long failures, long calls, double windowSeconds);

    /// <summary>Occurrences of one (event, subject) in the current window, plus the window's sample.</summary>
    private sealed class EventWindow
    {
        private long _count;
        private int _sampled;
        private string? _request;
        private string? _detail;

        public void Add(string? request, string? detail)
        {
            Interlocked.Increment(ref _count);
            if (Volatile.Read(ref _sampled) != 0)
            {
                return;
            }

            lock (this)
            {
                if (_sampled == 0)
                {
                    _request = request;
                    _detail = detail;
                    _sampled = 1;
                }
            }
        }

        public long Take(out string? request, out string? detail)
        {
            lock (this)
            {
                request = _request;
                detail = _detail;
                _request = null;
                _detail = null;
                _sampled = 0;
                return Interlocked.Exchange(ref _count, 0);
            }
        }
    }

    /// <summary>Calls and failures of one dependency in the current window, and the verdict of the last window with calls.</summary>
    /// <remarks>
    /// A call and its failure are added and taken under one lock: counted separately, a flush between the two increments
    /// moves the failure into the next window without its call, and the verdicts of both windows go wrong (review 6).
    /// The lock is per dependency and held for a few instructions.
    /// </remarks>
    private sealed class HealthWindow
    {
        private long _calls;
        private long _failures;
        private string? _error;

        /// <summary>Read and written only under the flush lock.</summary>
        public bool Failing { get; set; }

        public void Add(bool failed, string? error)
        {
            lock (this)
            {
                _calls++;
                if (failed)
                {
                    _failures++;
                    _error ??= error;
                }
            }
        }

        public long Take(out long failures, out string? error)
        {
            lock (this)
            {
                var calls = _calls;
                failures = _failures;
                error = _error;
                _calls = 0;
                _failures = 0;
                _error = null;
                return calls;
            }
        }
    }
}
