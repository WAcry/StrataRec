using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace StrataRec.Core.Diagnostics;

/// <summary>The kinds of external dependency the engine calls.</summary>
public enum IoKind : byte
{
    FeatureSource = 1,
    CandidateSource,
    Model,

    /// <summary>The distributed cache of a feature source bound with mode <c>cache</c> (design 6.6), read before its connector.</summary>
    FeatureCache,
}

/// <summary>How one external call ended, as seen by the request.</summary>
public enum IoOutcome : byte
{
    Ok = 1,

    /// <summary>Answered, but some keys failed; readers become Degraded. Counts as healthy.</summary>
    Partial,

    /// <summary>The request stopped waiting: the call's timeout, the node's budget or the request deadline.</summary>
    Timeout,

    Error,

    /// <summary>The request's caller cancelled; not the dependency's fault, so neither timed nor counted.</summary>
    Cancelled,
}

/// <summary>
/// One external dependency (a feature source, candidate source, model or a feature source's distributed cache) with its
/// metric tags and span name built once, when the plan or operator is compiled, so recording a call allocates nothing
/// (design 13.1 rule 5).
/// </summary>
public sealed class IoTarget
{
    public IoTarget(IoKind kind, string name)
    {
        Kind = kind;
        Name = name;
        Tags = kind switch
        {
            IoKind.Model => [new(StrataRecTags.Model, name)],
            IoKind.FeatureSource => [new(StrataRecTags.Source, name), new(StrataRecTags.SourceKind, "feature")],
            IoKind.CandidateSource => [new(StrataRecTags.Source, name), new(StrataRecTags.SourceKind, "candidate")],
            IoKind.FeatureCache => [new(StrataRecTags.Source, name), new(StrataRecTags.SourceKind, "feature_cache")],
        };
        TimeoutTags = WithOutcome(Tags, "timeout");
        ErrorTags = WithOutcome(Tags, "error");
        SpanName = kind switch
        {
            IoKind.FeatureSource => "fetch ",
            IoKind.CandidateSource => "recall ",
            IoKind.Model => "model ",
            IoKind.FeatureCache => "cache ",
        } + name;
        (TimeoutEvent, FailedEvent) = kind switch
        {
            IoKind.FeatureSource => (RequestEvent.FeatureSourceTimeout, RequestEvent.FeatureSourceFailed),
            IoKind.CandidateSource => (RequestEvent.CandidateSourceTimeout, RequestEvent.CandidateSourceFailed),
            IoKind.Model => (RequestEvent.ModelTimeout, RequestEvent.ModelFailed),
            IoKind.FeatureCache => (RequestEvent.FeatureCacheTimeout, RequestEvent.FeatureCacheFailed),
        };
    }

    public IoKind Kind { get; }

    public string Name { get; }

    /// <summary><c>stratarec.model</c>, or <c>stratarec.source</c> and <c>stratarec.source.kind</c>.</summary>
    public KeyValuePair<string, object?>[] Tags { get; }

    internal KeyValuePair<string, object?>[] TimeoutTags { get; }

    internal KeyValuePair<string, object?>[] ErrorTags { get; }

    internal string SpanName { get; }

    internal RequestEvent TimeoutEvent { get; }

    internal RequestEvent FailedEvent { get; }

    private Histogram<double> Duration => Kind == IoKind.Model ? StrataRecMetrics.ModelDuration : StrataRecMetrics.SourceDuration;

    private Counter<long> Errors => Kind == IoKind.Model ? StrataRecMetrics.ModelErrors : StrataRecMetrics.SourceErrors;

    /// <summary>
    /// Starts timing one call, and a child span when the request is traced (<see cref="StrataRecTracing.IsRecording"/>).
    /// Call it where the call starts so the span becomes the ambient parent of the client's own spans.
    /// </summary>
    public IoCall Start()
    {
        var span = StrataRecTracing.StartChild(SpanName, ActivityKind.Client);
        if (span is { IsAllDataRequested: true })
        {
            foreach (var tag in Tags)
            {
                span.SetTag(tag.Key, tag.Value);
            }
        }

        return new IoCall(this, Stopwatch.GetTimestamp(), span);
    }

    internal void Record(double seconds, IoOutcome outcome, int keys)
    {
        Duration.Record(seconds, Tags);
        if (keys >= 0 && Kind is IoKind.FeatureSource or IoKind.FeatureCache)
        {
            StrataRecMetrics.SourceKeys.Record(keys, Tags);
        }

        if (outcome is IoOutcome.Timeout or IoOutcome.Error)
        {
            Errors.Add(1, outcome == IoOutcome.Timeout ? TimeoutTags : ErrorTags);
        }
    }

    private static KeyValuePair<string, object?>[] WithOutcome(KeyValuePair<string, object?>[] tags, string outcome)
    {
        var result = new KeyValuePair<string, object?>[tags.Length + 1];
        tags.CopyTo(result, 0);
        result[^1] = new(StrataRecTags.Outcome, outcome);
        return result;
    }
}

/// <summary>One external call in progress: its start time and, for traced requests, its span.</summary>
public readonly struct IoCall
{
    private readonly IoTarget? _target;
    private readonly long _started;
    private readonly Activity? _span;

    internal IoCall(IoTarget target, long started, Activity? span)
    {
        _target = target;
        _started = started;
        _span = span;
    }

    /// <summary>Time since the call started (zero for a call that was never started); what a replay bundle records.</summary>
    public TimeSpan Elapsed => _target is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_started);

    /// <summary>
    /// Records the call: duration, keys and errors as metrics; health and windowed failure summaries in
    /// <paramref name="events"/> (design 13.2); and ends the span.
    /// </summary>
    /// <param name="keys">Keys sent (feature sources), or -1.</param>
    /// <param name="requestId">Sample request id for failure summaries.</param>
    /// <param name="error">Error text of a failed call.</param>
    /// <param name="events">The engine's event log; null when it has no logger.</param>
    public void Complete(IoOutcome outcome, int keys = -1, string? requestId = null, string? error = null, RequestEventLog? events = null)
    {
        if (_target is not { } target)
        {
            return;
        }

        if (outcome != IoOutcome.Cancelled)
        {
            target.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, outcome, keys);
            events?.RecordIo(target, outcome, requestId, error);
        }

        if (_span is { } span)
        {
            if (span.IsAllDataRequested)
            {
                if (keys >= 0)
                {
                    span.SetTag(StrataRecTags.Keys, keys);
                }

                span.SetTag(StrataRecTags.Outcome, OutcomeName(outcome));
                if (outcome is IoOutcome.Timeout or IoOutcome.Error)
                {
                    span.SetStatus(ActivityStatusCode.Error, error);
                }
            }

            span.Dispose();
        }
    }

    /// <summary>The <c>stratarec.outcome</c> spelling of an outcome, also used by replay bundles.</summary>
    public static string OutcomeName(IoOutcome outcome) => outcome switch
    {
        IoOutcome.Ok => "ok",
        IoOutcome.Partial => "partial",
        IoOutcome.Timeout => "timeout",
        IoOutcome.Error => "error",
        _ => "cancelled",
    };
}
