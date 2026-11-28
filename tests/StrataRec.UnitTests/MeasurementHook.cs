using System.Diagnostics.Metrics;
using StrataRec.Core.Diagnostics;

namespace StrataRec.UnitTests;

/// <summary>
/// A host's <see cref="MeterListener"/> that runs an action for every measurement of one StrataRec instrument carrying
/// the given tags, on the thread that records it. Tests that use it belong to
/// <see cref="Features.Cache.MetricMeasurement"/>, so no other test's measurement reaches it.
/// </summary>
internal sealed class MeasurementHook : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly (string Key, object Value)[] _tags;
    private readonly Action _measured;
    private int _calls;

    public MeasurementHook(string instrument, Action measured, params (string Key, object Value)[] tags)
    {
        _tags = tags;
        _measured = measured;
        _listener.InstrumentPublished = (published, listener) =>
        {
            if (published.Meter.Name == StrataRecTelemetry.Name && published.Name == instrument)
            {
                listener.EnableMeasurementEvents(published);
            }
        };
        _listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Measured(tags));
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Measured(tags));
        _listener.Start();
    }

    public int Calls => Volatile.Read(ref _calls);

    public static MeasurementHook Throwing(string instrument, params (string Key, object Value)[] tags) =>
        new(instrument, static () => throw new InvalidOperationException("a host's metric listener failed"), tags);

    public void Dispose() => _listener.Dispose();

    private void Measured(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var (key, value) in _tags)
        {
            var found = false;
            foreach (var tag in tags)
            {
                if (tag.Key == key && Equals(tag.Value, value))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return;
            }
        }

        Interlocked.Increment(ref _calls);
        _measured();
    }
}
