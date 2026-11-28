using System.Diagnostics;

namespace StrataRec.Core.Diagnostics;

/// <summary>
/// The roles of the external calls the framework makes: what a call is for, which metrics, spans, summaries and health
/// name together with the binding name of the dependency called (<c>fetch item_store</c>). A role is a short bounded
/// label, never configuration; an extension that calls a dependency of its own through the framework's call path gives
/// it a role of its own.
/// </summary>
public static class IoRoles
{
    /// <summary>Features from a feature source's connector, per <c>max_batch</c> chunk.</summary>
    public const string Fetch = "fetch";

    /// <summary>
    /// A read of a distributed cache, before what it caches is asked: of a feature source bound with mode
    /// <c>cache</c>, before its connector, or of a service bound with a <c>cache</c>, before its client; the target is the
    /// source's or the service's name.
    /// </summary>
    public const string Cache = "cache";

    /// <summary>Candidates from a service, per recall call.</summary>
    public const string Recall = "recall";

    /// <summary>A model's answer, per batch.</summary>
    public const string Model = "model";

    /// <summary>
    /// The experiment assignment provider's answer, per request the caller passed no arms for; its target
    /// is <c>provider</c>, the engine's one.
    /// </summary>
    public const string Assignment = "assignment";
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
/// One external dependency, by role (<see cref="IoRoles"/>) and binding name. Every role is measured by the same
/// instruments (<c>stratarec.io.*</c>), told apart by <see cref="StrataRecTags.IoRole"/>.
/// </summary>
public sealed class IoTarget
{
    /// <param name="role">What the call is for: one of <see cref="IoRoles"/>, or an extension's own bounded label of lowercase
    /// letters, digits and <c>_</c>, starting with a letter.</param>
    /// <param name="name">The dependency's binding name, e.g. <c>item_store</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="role"/> is not such a label: a space in it would make two
    /// targets one subject (<c>fetch geo</c> + <c>shard</c> and <c>fetch</c> + <c>geo shard</c>).</exception>
    public IoTarget(string role, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(name);
        if (!IsLabel(role))
        {
            throw new ArgumentException($"Role '{role}' is not a label of lowercase letters, digits and '_' starting with a letter.", nameof(role));
        }

        Role = role;
        Name = name;
        Tags = [new(StrataRecTags.IoRole, role), new(StrataRecTags.IoTarget, name)];
        TimeoutTags = WithOutcome(Tags, "timeout");
        ErrorTags = WithOutcome(Tags, "error");
        SpanName = role + " " + name;
    }

    public string Role { get; }

    public string Name { get; }

    /// <summary><c>stratarec.io.role</c> and <c>stratarec.io.target</c>.</summary>
    public KeyValuePair<string, object?>[] Tags { get; }

    internal KeyValuePair<string, object?>[] TimeoutTags { get; }

    internal KeyValuePair<string, object?>[] ErrorTags { get; }

    internal string SpanName { get; }

    /// <summary>
    /// Starts timing one call, and a child span when the request is traced
    /// (<see cref="StrataRecTracing.IsRecording"/>). Call it where the call starts so the span becomes the ambient
    /// parent of the client's own spans, and <see cref="IoCall.Sent"/> once the client has returned with the call still
    /// running. Never throws: a host <c>ActivityListener</c> whose sampler or start callback throws leaves the call
    /// without a span, as a listener that throws when the call completes loses only that measurement
    /// (<see cref="IoCall.Complete"/>).
    /// </summary>
    /// <remarks>
    /// The flow gets back the activity it had, and the call has no span: the half-started one is not stopped, which
    /// would report it to the listeners as a finished span.
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
                    context = ExecutionContext.Capture();
                }
            }
        }
        catch (Exception)
        {
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
    /// Each instrument has its own boundary: what a host's listener throws loses that one measurement, not the others
    /// of the call.
    /// </summary>
    internal void Record(double seconds, IoOutcome outcome, int keys)
    {
        try
        {
            StrataRecMetrics.IoDuration.Record(seconds, Tags);
        }
        catch (Exception)
        {
        }

        if (keys >= 0)
        {
            try
            {
                StrataRecMetrics.IoKeys.Record(keys, Tags);
            }
            catch (Exception)
            {
            }
        }

        if (outcome is IoOutcome.Timeout or IoOutcome.Error)
        {
            try
            {
                StrataRecMetrics.IoErrors.Add(1, outcome == IoOutcome.Timeout ? TimeoutTags : ErrorTags);
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool IsLabel(string role)
    {
        if (role[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var c in role)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
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
    /// The client has returned from the call still running: the span stops being the current activity of the call
    /// site's flow. The client's own work took it as its parent when it was sent; what the call site does next is not
    /// part of this call. A call that answered before its client returned is completed at once instead, which gives the
    /// flow its activity back after the outcome is taken. Nothing to do without a span; never throws.
    /// </summary>
    public void Sent()
    {
        if (_span is { } span)
        {
            Restore(span, _ambient);
        }
    }

    /// <summary>
    /// A flow whose current activity is another one (another call's, or the client's own) is left as it is: this flow
    /// is not the one the call changed. Never throws.
    /// </summary>
    internal static void Restore(Activity span, Activity? ambient)
    {
        if (ReferenceEquals(Activity.Current, span))
        {
            SetCurrent(ambient);
        }
    }

    /// <summary>What a host's <see cref="Activity.CurrentChanged"/> handler throws is ignored: the call must still
    /// complete and let go of the request's memory.</summary>
    private static void SetCurrent(Activity? activity)
    {
        try
        {
            Activity.Current = activity;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Records the call: health and windowed failure summaries in <paramref name="events"/>; duration, keys and errors
    /// as metrics; and ends the span. Never throws: what a host's listener throws loses that measurement or the span's
    /// end.
    /// </summary>
    /// <param name="keys">Keys the call sent (a keyed call: a feature source's), or -1.</param>
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
            }

            var current = Activity.Current;
            if (ReferenceEquals(current, span))
            {
                Stop(span);
                Restore(span, _ambient); // a stop callback threw before Stop gave the flow its activity back
            }
            else if (_context is { } context)
            {
                ExecutionContext.Run(context, static span => Stop((Activity)span!), span);
            }
            else
            {
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
