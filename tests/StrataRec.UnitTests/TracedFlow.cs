using System.Collections.Concurrent;
using System.Diagnostics;
using StrataRec.Core.Diagnostics;

namespace StrataRec.UnitTests;

/// <summary>Where a host's activity listener throws.</summary>
public enum ListenerStage
{
    None,

    /// <summary>The sampler, before the span is created.</summary>
    Sample,

    /// <summary>The start callback, after <c>Activity.Start</c> made the span current.</summary>
    Start,

    /// <summary>The stop callback, after the span stopped and before <c>Activity.Stop</c> restores the current activity.</summary>
    Stop,
}

/// <summary>
/// A traced request in the test's flow: a recorded StrataRec span made current (<see cref="Request"/>), and a host's
/// <see cref="ActivityListener"/> that keeps every span of its trace and throws at <see cref="ListenerStage"/> for the
/// first span named <c>failing</c>. It samples nothing but its own trace and throws nowhere else, so tests running
/// beside it neither see its spans nor its exceptions. Disposing it gives the flow back the activity it had.
/// </summary>
internal sealed class TracedFlow : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly Activity? _initial;
    private readonly ListenerStage _stage;
    private readonly string? _failing;
    private readonly string _root = "traced-flow-" + Guid.NewGuid().ToString("N");
    private ActivityTraceId _trace;
    private int _thrown;

    public TracedFlow(ListenerStage stage = ListenerStage.None, string? failing = null)
    {
        _stage = stage;
        _failing = failing;
        _initial = Activity.Current;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Name == _root)
                {
                    return ActivitySamplingResult.AllDataAndRecorded;
                }

                if (_trace == default || options.Parent.TraceId != _trace)
                {
                    return ActivitySamplingResult.None;
                }

                Fail(ListenerStage.Sample, options.Name);
                return ActivitySamplingResult.AllDataAndRecorded;
            },
            ActivityStarted = span =>
            {
                if (span.TraceId == _trace && span.OperationName != _root)
                {
                    Spans.Enqueue(span);
                    Fail(ListenerStage.Start, span.OperationName);
                }
            },
            ActivityStopped = span =>
            {
                if (span.TraceId == _trace)
                {
                    Fail(ListenerStage.Stop, span.OperationName);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
        Request = StrataRecTelemetry.ActivitySource.StartActivity(_root)!;
        _trace = Request.TraceId;
    }

    /// <summary>The request's span, current in the flow that created this.</summary>
    public Activity Request { get; }

    /// <summary>The spans started under <see cref="Request"/>, in the order they started (one whose start callback threw included).</summary>
    public ConcurrentQueue<Activity> Spans { get; } = new();

    /// <summary>How many times the listener threw: once at most.</summary>
    public int Thrown => Volatile.Read(ref _thrown);

    /// <summary>The spans named <paramref name="name"/>, in the order they started.</summary>
    public Activity[] Named(string name) => Spans.Where(s => s.OperationName == name).ToArray();

    public void Dispose()
    {
        Request.Dispose();
        _listener.Dispose();
        Activity.Current = _initial;
    }

    private void Fail(ListenerStage stage, string name)
    {
        if (stage == _stage && name == _failing && Interlocked.CompareExchange(ref _thrown, 1, 0) == 0)
        {
            throw new InvalidOperationException($"a host's activity listener failed ({stage})");
        }
    }
}
