using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Tests that attach process-wide listeners (MeterListener, ActivityListener) or measure allocations on the request
/// thread. They run alone: another test's requests would show up in a listener, and a listener's callback runs on the
/// measuring thread.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideListenersCollection
{
    public const string Name = "Process-wide listeners";
}

/// <summary>One logged entry, with the structured state the [LoggerMessage] generator produced.</summary>
internal sealed record LogEntry(LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> State, Exception? Exception = null);

/// <summary>An <see cref="ILoggerFactory"/> that keeps every entry in memory.</summary>
internal sealed class ListLoggerFactory : ILoggerFactory
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ListLogger(Entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class ListLogger(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    values[key] = value;
                }
            }

            entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), values, exception));
        }
    }
}

/// <summary>One measurement of a StrataRec instrument.</summary>
internal sealed record Measured(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>Captures every measurement of the StrataRec meter while alive.</summary>
internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();

    public MetricCapture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    public ConcurrentQueue<Measured> Measurements { get; } = new();

    public List<Measured> Of(string instrument) => Measurements.Where(m => m.Instrument == instrument).ToList();

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, tagValue) in tags)
        {
            copy[key] = tagValue;
        }

        Measurements.Enqueue(new Measured(instrument.Name, value, copy));
    }
}

/// <summary>
/// Cancels <see cref="Token"/> a fixed time after an external call starts, for a test about a request the caller cuts off
/// while that call is in flight. The call's span (<c>model ctr_model</c>, <c>recall popular</c>) is started by the
/// framework right before it invokes the client, so this is the moment a gated client would report that it was entered,
/// with the clients under test left as they are. A timer started with the request fires in an earlier node whenever the
/// nodes before it are slow, as on a busy machine; this one cannot fire before the call is made.
/// </summary>
internal sealed class CancelOnceCallStarts : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly ActivityListener _listener;

    public CancelOnceCallStarts(string call, TimeSpan after)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded, // call spans exist
            ActivityStarted = span =>
            {
                if (span.OperationName == call)
                {
                    _cancel.CancelAfter(after);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public CancellationToken Token => _cancel.Token;

    public void Dispose()
    {
        _listener.Dispose();
        _cancel.Dispose();
    }
}
