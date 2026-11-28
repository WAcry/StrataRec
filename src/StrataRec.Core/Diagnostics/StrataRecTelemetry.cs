using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace StrataRec.Core.Diagnostics;

/// <summary>
/// The engine's one <see cref="System.Diagnostics.Metrics.Meter"/> and one
/// <see cref="System.Diagnostics.ActivitySource"/>, both named <see cref="Name"/>. Hosts subscribe
/// by name (OpenTelemetry: <c>AddMeter("StrataRec")</c>, <c>AddSource("StrataRec")</c>); the engine references no
/// exporter.
/// </summary>
public static class StrataRecTelemetry
{
    /// <summary>Name of the meter and of the activity source.</summary>
    public const string Name = "StrataRec";

    private static readonly string? Version = typeof(StrataRecTelemetry).Assembly.GetName().Version?.ToString();

    public static Meter Meter { get; } = new(Name, Version);

    public static ActivitySource ActivitySource { get; } = new(Name, Version);
}

/// <summary>
/// Tag (attribute) keys. Metric tags take only bounded values; span-only keys may carry ids.
/// </summary>
public static class StrataRecTags
{
    public const string Pipeline = "stratarec.pipeline";

    public const string Node = "stratarec.node";

    /// <summary>
    /// Feature source name from the bindings, on what the feature runtime measures of a source (values, tables, caches);
    /// a service's name on its cache's metrics (with <c>stratarec.entity</c> = <c>answers</c>).
    /// </summary>
    public const string Source = "stratarec.source";

    /// <summary>
    /// What a call of an external dependency is for (<see cref="IoRoles"/>): <c>fetch</c>, <c>cache</c>, <c>recall</c>,
    /// <c>model</c>, <c>assignment</c>, or an extension's own (the sample's filter calls in <c>filter</c>); with
    /// <see cref="IoTarget"/> on <c>stratarec.io.duration</c>, <c>errors</c> and <c>keys</c>, on what a generative
    /// service reported (<c>stratarec.io.output_tokens</c>, <c>truncated</c>), and on what the generative operators made
    /// of its answers (<c>stratarec.model.answer_ids</c>, <c>answers</c>).
    /// </summary>
    public const string IoRole = "stratarec.io.role";

    /// <summary>The binding name of the dependency a call went to, e.g. <c>item_store</c> or <c>ctr_model</c>.</summary>
    public const string IoTarget = "stratarec.io.target";

    /// <summary>
    /// Catalog name of a feature (with <see cref="Source"/>: what a source supplies, field by field). Bounded by the
    /// catalog, as node ids are by the pipelines.
    /// </summary>
    public const string Feature = "stratarec.feature";

    /// <summary>Entity of a local feature table (with <see cref="Source"/>: one table per source and entity).</summary>
    public const string Entity = "stratarec.entity";

    /// <summary>
    /// Tier of a cache: <c>memory</c> (the in-process table), <c>distributed</c>, or <c>request</c>: what another run of
    /// the same request, or a request its host sent with it as one group, got from the origin; a hit for a read it
    /// answers, expired or a mismatch for one it does not (it holds nothing of its own).
    /// </summary>
    public const string CacheTier = "stratarec.cache.tier";

    /// <summary>Outcome category; only on counters, never on durations.</summary>
    public const string Outcome = "stratarec.outcome";

    /// <summary>Experiment arm (<c>experiment/arm</c>); only on request-level metrics.</summary>
    public const string Arm = "stratarec.arm";

    public const string Column = "stratarec.column";

    public const string Tap = "stratarec.tap";

    /// <summary><c>durable</c> or <c>best_effort</c>.</summary>
    public const string LogDelivery = "stratarec.log.delivery";

    /// <summary>A token the framework cancels for code it does not own (<see cref="Threading.CancellationSite"/>).</summary>
    public const string Token = "stratarec.token";

    // Span-only keys.

    public const string RequestId = "stratarec.request.id";

    public const string ManifestId = "stratarec.manifest.id";

    /// <summary>Pipeline that served the result: the requested one or its fallback pipeline.</summary>
    public const string ServedBy = "stratarec.served_by";

    /// <summary>Content hash of the selected node variant's merged definition.</summary>
    public const string Variant = "stratarec.node.variant";

    /// <summary>On a node span: the fallback node that served the position.</summary>
    public const string Fallback = "stratarec.node.fallback";

    /// <summary>
    /// On a node span: why the node did not simply succeed, as the debug trace says it (<c>NodeOutcome.Detail</c>): what
    /// failed when it fell back, was degraded or failed, why it was skipped. A failure and an abort (the caller cancelled
    /// while it ran) also set the span's status to error; a fallback or a degradation, part of a request that succeeded,
    /// does not.
    /// </summary>
    public const string OutcomeDetail = "stratarec.outcome.detail";

    /// <summary>Content hash of the fallback node variant that served.</summary>
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
/// Span helpers. A request gets one span; node and external-I/O child spans exist only while that span is recorded
/// (<see cref="Activity.Recorded"/>).
/// </summary>
public static class StrataRecTracing
{
    /// <summary>Operation name of the per-request span.</summary>
    public const string RequestSpanName = "stratarec.request";

    /// <summary>
    /// True inside a recorded StrataRec span: within a request whose span (<see cref="RequestSpanName"/>, which the engine
    /// starts) the host's sampler kept.
    /// </summary>
    public static bool IsRecording =>
        Activity.Current is { Recorded: true } current && ReferenceEquals(current.Source, StrataRecTelemetry.ActivitySource);

    /// <summary>Starts a child span only when the current StrataRec span is recorded; otherwise returns null without allocating.</summary>
    public static Activity? StartChild(string name, ActivityKind kind = ActivityKind.Internal) =>
        IsRecording ? StrataRecTelemetry.ActivitySource.StartActivity(name, kind) : null;
}
