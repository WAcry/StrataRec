using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace StrataRec.Core.Diagnostics;

/// <summary>
/// The engine's one <see cref="System.Diagnostics.Metrics.Meter"/> and one
/// <see cref="System.Diagnostics.ActivitySource"/>, both named <see cref="Name"/> (design 13.1, 13.2). Hosts subscribe
/// by name (OpenTelemetry: <c>AddMeter("StrataRec")</c>, <c>AddSource("StrataRec")</c>); the engine references no
/// exporter.
/// </summary>
/// <remarks>
/// One static meter and source per process is the standard .NET pattern: instruments and the source are no-ops until a
/// listener enables them, so an engine nobody observes pays a null check per measurement and allocates nothing.
/// </remarks>
public static class StrataRecTelemetry
{
    /// <summary>Name of the meter and of the activity source.</summary>
    public const string Name = "StrataRec";

    private static readonly string? Version = typeof(StrataRecTelemetry).Assembly.GetName().Version?.ToString();

    public static Meter Meter { get; } = new(Name, Version);

    public static ActivitySource ActivitySource { get; } = new(Name, Version);
}

/// <summary>
/// Tag (attribute) keys. Metric tags take only bounded values (design 13.1 rule 1); span-only keys may carry ids.
/// </summary>
/// <remarks>
/// Keys are namespaced with <c>stratarec.</c> so they cannot collide with labels the collection pipeline adds itself
/// (Kubernetes, for one, adds <c>node</c>).
/// </remarks>
public static class StrataRecTags
{
    public const string Pipeline = "stratarec.pipeline";

    public const string Node = "stratarec.node";

    /// <summary>Feature source or candidate source name from the bindings.</summary>
    public const string Source = "stratarec.source";

    /// <summary><c>feature</c> or <c>candidate</c>: the two source namespaces of the bindings; <c>feature_cache</c> for the
    /// distributed cache of a feature source bound with mode <c>cache</c>.</summary>
    public const string SourceKind = "stratarec.source.kind";

    /// <summary>Entity of a local feature table (with <see cref="Source"/>: one table per source and entity).</summary>
    public const string Entity = "stratarec.entity";

    /// <summary>Tier of a <c>cache</c> source (design 6.6): <c>local</c> (the in-process table) or <c>distributed</c>.</summary>
    public const string CacheTier = "stratarec.cache.tier";

    public const string Model = "stratarec.model";

    /// <summary>Outcome category; only on counters, never on durations (design 13.1 rule 2).</summary>
    public const string Outcome = "stratarec.outcome";

    /// <summary>Experiment arm (<c>experiment/arm</c>); only on request-level metrics (design 13.1 rule 4).</summary>
    public const string Arm = "stratarec.arm";

    public const string Column = "stratarec.column";

    public const string Tap = "stratarec.tap";

    /// <summary><c>durable</c> or <c>best_effort</c>.</summary>
    public const string LogDelivery = "stratarec.log.delivery";

    // Span-only keys.

    public const string RequestId = "stratarec.request.id";

    public const string ManifestId = "stratarec.manifest.id";

    /// <summary>Pipeline that served the result: the requested one or its fallback pipeline.</summary>
    public const string ServedBy = "stratarec.served_by";

    /// <summary>Content hash of the selected node variant's merged definition (design 12.3).</summary>
    public const string Variant = "stratarec.node.variant";

    public const string Fallback = "stratarec.node.fallback";

    /// <summary>Content hash of the fallback node variant that served (design 12.3).</summary>
    public const string FallbackVariant = "stratarec.node.fallback.variant";

    public const string ArmsAssigned = "stratarec.arms.assigned";

    public const string ExperimentsTriggered = "stratarec.experiments.triggered";

    public const string RowsIn = "stratarec.rows.in";

    public const string RowsOut = "stratarec.rows.out";

    public const string Items = "stratarec.items";

    public const string Keys = "stratarec.keys";

    public const string Degraded = "stratarec.degraded";
}

/// <summary>
/// Span helpers (design 13.2). A request gets one span; node and external-I/O child spans exist only while that span is
/// recorded (<see cref="Activity.Recorded"/>), so a request the host's sampler drops creates no node-level objects.
/// </summary>
public static class StrataRecTracing
{
    /// <summary>Operation name of the per-request span.</summary>
    public const string RequestSpanName = "stratarec.request";

    /// <summary>
    /// True inside a recorded StrataRec span. Hosts that call the pipeline executor directly can start
    /// <see cref="StartRequest"/> around the call to get node spans.
    /// </summary>
    public static bool IsRecording =>
        Activity.Current is { Recorded: true } current && ReferenceEquals(current.Source, StrataRecTelemetry.ActivitySource);

    /// <summary>
    /// Starts the request span; null when nobody listens or the sampler drops it. Starting it costs a listener check
    /// otherwise, so call it once per request, not per node.
    /// </summary>
    public static Activity? StartRequest(string pipelineId, string requestId)
    {
        var activity = StrataRecTelemetry.ActivitySource.StartActivity(RequestSpanName, ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(StrataRecTags.Pipeline, pipelineId);
            activity.SetTag(StrataRecTags.RequestId, requestId);
        }

        return activity;
    }

    /// <summary>Starts a child span only when the current StrataRec span is recorded; otherwise returns null without allocating.</summary>
    public static Activity? StartChild(string name, ActivityKind kind = ActivityKind.Internal) =>
        IsRecording ? StrataRecTelemetry.ActivitySource.StartActivity(name, kind) : null;
}
