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
    /// Call it where the call starts so the span becomes the ambient parent of the client's own spans, and
    /// <see cref="IoCall.Sent"/> once the client has returned with the call still running. Never throws: a host <c>ActivityListener</c> whose sampler
    /// or start callback throws leaves the call without a span, as a listener that throws when the call completes loses
    /// only that measurement (<see cref="IoCall.Complete"/>). The call sites hold the request's memory before they start
    /// the call, so a throw here would keep it held.
    /// </summary>
    /// <remarks>
    /// The span is created, then started, so the call holds it when the start callback throws: <see cref="Activity.Start"/>
    /// has made it <see cref="Activity.Current"/> by then, and a span left current became the parent of every later call
    /// of the flow, the next chunk of a fetch say (reviews 82, 83). The flow gets back the activity it had, and the call
    /// has no span: the half-started one is not stopped, which would report it to the listeners as a finished span. A
    /// sampler that throws does so before anything is created or made current.
    /// </remarks>
    public IoCall Start()
    {
        var ambient = Activity.Current;
        Activity? span = null;
        ExecutionContext? context = null;
        try
        {
            if (StrataRecTracing.IsRecording)
            {
                span = StrataRecTelemetry.ActivitySource.CreateActivity(SpanName, ActivityKind.Client);
                if (span is not null)
                {
                    if (span.IsAllDataRequested)
                    {
                        foreach (var tag in Tags)
                        {
                            span.SetTag(tag.Key, tag.Value);
                        }
                    }

                    span.Start();
                    context = ExecutionContext.Capture(); // the call site's, with the span current: see IoCall.Complete
                }
            }
        }
        catch (Exception)
        {
            // The host's listener threw: the call goes on without a span (see the remarks).
            if (span is not null)
            {
                IoCall.Restore(span, ambient);
                span = null;
                context = null;
            }
        }

        return new IoCall(this, Stopwatch.GetTimestamp(), span, ambient, context);
    }

    /// <summary>
    /// Records the call's metrics. Each instrument has its own boundary: what a host's listener throws loses that one
    /// measurement, not the others of the call (a listener that threw on the duration skipped the keys and the errors,
    /// review 83).
    /// </summary>
    internal void Record(double seconds, IoOutcome outcome, int keys)
    {
        try
        {
            Duration.Record(seconds, Tags);
        }
        catch (Exception)
        {
            // A metric listener threw: see IoCall.Complete.
        }

        if (keys >= 0 && Kind is IoKind.FeatureSource or IoKind.FeatureCache)
        {
            try
            {
                StrataRecMetrics.SourceKeys.Record(keys, Tags);
            }
            catch (Exception)
            {
                // As above.
            }
        }

        if (outcome is IoOutcome.Timeout or IoOutcome.Error)
        {
            try
            {
                Errors.Add(1, outcome == IoOutcome.Timeout ? TimeoutTags : ErrorTags);
            }
            catch (Exception)
            {
                // As above.
            }
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

/// <summary>
/// One external call in progress: its start time and, for traced requests, its span and the activity that was current
/// when the call started, which the flow gets back once the span is no longer its parent.
/// </summary>
public readonly struct IoCall
{
    private readonly IoTarget? _target;
    private readonly long _started;
    private readonly Activity? _span;
    private readonly Activity? _ambient;
    private readonly ExecutionContext? _context;

    internal IoCall(IoTarget target, long started, Activity? span, Activity? ambient, ExecutionContext? context)
    {
        _target = target;
        _started = started;
        _span = span;
        _ambient = ambient;
        _context = context;
    }

    /// <summary>Time since the call started (zero for a call that was never started); what a replay bundle records.</summary>
    public TimeSpan Elapsed => _target is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_started);

    /// <summary>
    /// The client has returned from the call still running: the span stops being the current activity of the call site's
    /// flow. The client's own work took it as its parent when it was sent; what the call site does next is not part of
    /// this call. One that runs on is completed on the flow that awaits it, so the span stayed the parent of the call
    /// site's later calls: each chunk of a fetch from an asynchronous connector was a child of the chunk before it. A call
    /// that answered before its client returned is completed at once instead, which gives the flow its activity back after
    /// the outcome is taken (review 85: the host's <see cref="Activity.CurrentChanged"/> handlers ran here before the
    /// answer's time was read). Nothing to do without a span; never throws.
    /// </summary>
    public void Sent()
    {
        if (_span is { } span)
        {
            Restore(span, _ambient);
        }
    }

    /// <summary>
    /// Gives the flow back <paramref name="ambient"/> while <paramref name="span"/>, which a call made current, still is
    /// its current activity. A flow whose current activity is another one (another call's, or the client's own) is left
    /// as it is: this flow is not the one the call changed. Never throws (<see cref="SetCurrent"/>, the one place the
    /// engine sets the current activity of an external call's flow).
    /// </summary>
    internal static void Restore(Activity span, Activity? ambient)
    {
        if (ReferenceEquals(Activity.Current, span))
        {
            SetCurrent(ambient);
        }
    }

    /// <summary>
    /// Sets the flow's current activity. The setter calls the host's <see cref="Activity.CurrentChanged"/> handlers once
    /// the activity is set, so what one throws is ignored, as what a listener throws when a call starts or completes
    /// (<see cref="Complete"/>): it went up through <see cref="Sent"/>, and the call site neither completed the call it
    /// had sent nor let go of the request's memory (review 85).
    /// </summary>
    private static void SetCurrent(Activity? activity)
    {
        try
        {
            Activity.Current = activity;
        }
        catch (Exception)
        {
            // A CurrentChanged handler threw, after the activity was set.
        }
    }

    /// <summary>
    /// Records the call: health and windowed failure summaries in <paramref name="events"/> (design 13.2); duration, keys
    /// and errors as metrics; and ends the span. Never throws.
    /// </summary>
    /// <remarks>
    /// <para>The metrics and the span call the host's listeners (a <c>MeterListener</c>'s measurement callback, an
    /// <c>ActivityListener</c>'s stop callback) on this thread. What one throws loses that measurement or the span's end,
    /// as for plan loads and the log pipeline: every call site completes a call here and then records it and lets go of
    /// it, so a throw here left a call that had answered recorded as outstanding and failed its node or fill with the
    /// listener's exception (review 80). The health comes first: it is the engine's own and reaches the operator whatever
    /// the listeners do; each metric has its own boundary (<see cref="IoTarget.Record"/>).</para>
    /// <para>A stop callback that throws does so after the span is stopped and before <see cref="Activity.Stop"/> gives
    /// the flow back the activity it had: the flow gets it here, or the stopped span stayed the parent of the flow's later
    /// calls (review 83).</para>
    /// <para><see cref="Activity.Stop"/> makes the activity the span started over current, whatever the flow's current
    /// one. A span that is not the current activity here (a call still running when its client returned, completed on the
    /// flow that awaited it or on another) is stopped in the call site's context as it was when the call started
    /// (<see cref="ExecutionContext.Run"/>), which that change leaves as it was: the flow completing the call keeps its own
    /// activity. Stopped on that flow, one running another request's activity went on as a child of this call's request
    /// (review 85), and one whose activity had stopped could not be given it back (review 87). The host's listeners run in
    /// the call site's context, as the call's own. A call started where the execution context does not flow
    /// (<see cref="ExecutionContext.SuppressFlow"/>) has none to stop it in: it is stopped on the flow completing it, whose
    /// listeners and handlers see that flow, and the flow is given back its activity after the span stops, unless that
    /// activity has stopped (the setter refuses it, and the flow stays under this call's request).</para>
    /// </remarks>
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
            events?.RecordIo(target, outcome, requestId, error);
            target.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, outcome, keys);
        }

        if (_span is { } span)
        {
            try
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
            }
            catch (Exception)
            {
                // Tags call no listener; nothing here may throw out of Complete.
            }

            var current = Activity.Current;
            if (ReferenceEquals(current, span))
            {
                Stop(span);
                Restore(span, _ambient); // a stop callback threw before Stop gave the flow its activity back
            }
            else if (_context is { } context)
            {
                ExecutionContext.Run(context, static span => Stop((Activity)span!), span); // see the remarks
            }
            else
            {
                // No context to stop it in (the flow did not flow when the call started): this flow gets back the activity
                // it had, which the setter refuses only for one that has stopped (review 88: it was left in this call's request).
                Stop(span);
                if (!ReferenceEquals(Activity.Current, current))
                {
                    SetCurrent(current);
                }
            }
        }
    }

    private static void Stop(Activity span)
    {
        try
        {
            span.Dispose();
        }
        catch (Exception)
        {
            // An activity listener threw: see the remarks of Complete.
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
