using System.Diagnostics.Metrics;

namespace StrataRec.Core.Diagnostics;

/// <summary>
/// Every instrument the engine records, on the <see cref="StrataRecTelemetry.Meter"/> (design 13.1). Names, units and
/// tag sets are a stable public interface: dashboards and alerts are built on them, and how they are exported (OTLP
/// exponential histograms, VictoriaMetrics, fixed buckets) is configured by the host per name.
/// </summary>
/// <remarks>
/// <para>Label rules (design 13.1): tags take only bounded values — pipeline, node, data source, model, outcome
/// category, tap, and the experiment arm on request-level instruments only. Durations, row counts and sizes are tagged
/// by what was measured (node, source, model), never crossed with outcomes; outcome categories go on counters. User,
/// entity, candidate and request ids are never tags.</para>
/// <para>Tag arrays are built once per compiled plan (per node, arm, source, model, tap) and passed as-is, so a
/// measurement allocates nothing, with or without listeners.</para>
/// <para>Histograms advise bucket boundaries that grow by a fixed ratio (<see cref="InstrumentAdvice{T}"/>): exporters
/// with fixed buckets can adopt them; exponential-histogram exporters ignore them.</para>
/// <para>Custom operators and connectors may record into these instruments with the same tag keys
/// (<see cref="StrataRecTags"/>), so their calls appear under the same names.</para>
/// </remarks>
public static class StrataRecMetrics
{
    /// <summary>Instrument names, for host configuration (views, allow-lists) without touching the instruments.</summary>
    public static class Names
    {
        public const string RequestDuration = "stratarec.request.duration";
        public const string RequestErrors = "stratarec.request.errors";
        public const string RequestEmpty = "stratarec.request.empty";
        public const string RequestDegraded = "stratarec.request.degraded";
        public const string ArmRequestDuration = "stratarec.arm.request.duration";
        public const string ArmRequestErrors = "stratarec.arm.request.errors";
        public const string ArmRequestEmpty = "stratarec.arm.request.empty";
        public const string ArmRequestDegraded = "stratarec.arm.request.degraded";
        public const string RequestMemoryRented = "stratarec.request.memory.rented";
        public const string AbandonedIo = "stratarec.io.abandoned";
        public const string NodeDuration = "stratarec.node.duration";
        public const string NodeRowsIn = "stratarec.node.rows.in";
        public const string NodeRowsOut = "stratarec.node.rows.out";
        public const string NodeOutcomes = "stratarec.node.outcomes";
        public const string ColumnNonFinite = "stratarec.column.nonfinite";
        public const string ColumnOutOfRange = "stratarec.column.out_of_range";
        public const string SourceDuration = "stratarec.source.duration";
        public const string SourceErrors = "stratarec.source.errors";
        public const string SourceKeys = "stratarec.source.keys";
        public const string ModelDuration = "stratarec.model.duration";
        public const string ModelErrors = "stratarec.model.errors";
        public const string ModelFallbacks = "stratarec.model.fallbacks";
        public const string PlanCompileDuration = "stratarec.plan.compile.duration";
        public const string PlanVariants = "stratarec.plan.variants";
        public const string PlanSequence = "stratarec.plan.sequence";
        public const string PlanLoads = "stratarec.plan.loads";
        public const string LogQueueDepth = "stratarec.log.queue.depth";
        public const string LogBatches = "stratarec.log.batches";
        public const string LogDurableOverflow = "stratarec.log.durable.overflow";
        public const string LogRejected = "stratarec.log.rejected";
    }

    // Advised boundaries. Field order matters: static initializers run top to bottom, before the instruments below.

    /// <summary>0.1 ms to ~13 s, ratio √2 (each bucket ~41 % wide), 35 boundaries.</summary>
    private static readonly double[] SecondsBuckets = Geometric(0.0001, Math.Sqrt(2), 35);

    /// <summary>1 to 2^20, ratio 2, 21 boundaries: rows and keys.</summary>
    private static readonly long[] CountBuckets = PowersOfTwo(0, 20);

    /// <summary>1 KiB to 1 GiB, ratio 2, 21 boundaries.</summary>
    private static readonly long[] ByteBuckets = PowersOfTwo(10, 30);

    private static readonly Meter Meter = StrataRecTelemetry.Meter;

    // Requests: recorded once per IRecEngine request (assignment, the pipeline and its fallback pipeline).

    /// <summary>End-to-end request latency in seconds. Tags: <c>stratarec.pipeline</c> (absent for unknown pipelines).</summary>
    public static readonly Histogram<double> RequestDuration = Seconds(Names.RequestDuration, "End-to-end latency of engine requests.");

    /// <summary>Requests that returned no result (failed, rejected or aborted). Tags: <c>stratarec.pipeline</c>.</summary>
    public static readonly Counter<long> RequestErrors = Meter.CreateCounter<long>(Names.RequestErrors, "{request}", "Requests that failed, were rejected or were aborted.");

    /// <summary>Successful requests with zero items. Tags: <c>stratarec.pipeline</c>.</summary>
    public static readonly Counter<long> RequestEmpty = Meter.CreateCounter<long>(Names.RequestEmpty, "{request}", "Successful requests that returned no items.");

    /// <summary>
    /// Successful requests served degraded: by the fallback pipeline, or with a node that was degraded, served by its
    /// fallback, failed and skipped, or skipped for lack of time or input. Tags: <c>stratarec.pipeline</c>.
    /// </summary>
    public static readonly Counter<long> RequestDegraded = Meter.CreateCounter<long>(Names.RequestDegraded, "{request}", "Successful requests served degraded.");

    /// <summary>
    /// <see cref="RequestDuration"/> per assigned arm, for health gates and experiment guardrails. A request is recorded
    /// once per arm it is assigned (one per layer), so the series of one experiment sum to its traffic and the
    /// request-level totals are never inflated by layering. Tags: <c>stratarec.pipeline</c>, <c>stratarec.arm</c>.
    /// </summary>
    public static readonly Histogram<double> ArmRequestDuration = Seconds(Names.ArmRequestDuration, "End-to-end request latency per assigned experiment arm.");

    /// <summary><see cref="RequestErrors"/> per assigned arm. Tags: <c>stratarec.pipeline</c>, <c>stratarec.arm</c>.</summary>
    public static readonly Counter<long> ArmRequestErrors = Meter.CreateCounter<long>(Names.ArmRequestErrors, "{request}", "Failed requests per assigned experiment arm.");

    /// <summary><see cref="RequestEmpty"/> per assigned arm. Tags: <c>stratarec.pipeline</c>, <c>stratarec.arm</c>.</summary>
    public static readonly Counter<long> ArmRequestEmpty = Meter.CreateCounter<long>(Names.ArmRequestEmpty, "{request}", "Empty results per assigned experiment arm.");

    /// <summary><see cref="RequestDegraded"/> per assigned arm. Tags: <c>stratarec.pipeline</c>, <c>stratarec.arm</c>.</summary>
    public static readonly Counter<long> ArmRequestDegraded = Meter.CreateCounter<long>(Names.ArmRequestDegraded, "{request}", "Degraded results per assigned experiment arm.");

    // Memory: recorded once per pipeline execution.

    /// <summary>Bytes a pipeline execution rented from the buffer pool (design 4.8). Tags: <c>stratarec.pipeline</c>.</summary>
    public static readonly Histogram<long> RequestMemoryRented = Meter.CreateHistogram<long>(Names.RequestMemoryRented, "By",
        "Bytes rented from the buffer pool per pipeline execution.", null, new InstrumentAdvice<long> { HistogramBucketBoundaries = ByteBuckets });

    /// <summary>
    /// External operations (connector, candidate source and model calls, the context prefetch) still running when their
    /// pipeline execution returned; they keep its buffers rented until they finish (design 4.8). Tags:
    /// <c>stratarec.pipeline</c>.
    /// </summary>
    public static readonly Counter<long> AbandonedIo = Meter.CreateCounter<long>(Names.AbandonedIo, "{operation}", "External I/O still running after its request returned.");

    // Nodes: recorded once per timeline position that has a node, under the primary node id.

    /// <summary>Wall time of a node that ran, fallback included. Tags: <c>stratarec.pipeline</c>, <c>stratarec.node</c>.</summary>
    public static readonly Histogram<double> NodeDuration = Seconds(Names.NodeDuration, "Wall time of pipeline nodes that ran.");

    /// <summary>Live rows before a node that ran. Tags: <c>stratarec.pipeline</c>, <c>stratarec.node</c>.</summary>
    public static readonly Histogram<long> NodeRowsIn = Rows(Names.NodeRowsIn, "Live rows before a node.");

    /// <summary>Live rows after a node that ran. Tags: <c>stratarec.pipeline</c>, <c>stratarec.node</c>.</summary>
    public static readonly Histogram<long> NodeRowsOut = Rows(Names.NodeRowsOut, "Live rows after a node.");

    /// <summary>
    /// Node outcomes (design 5.6). Tags: <c>stratarec.pipeline</c>, <c>stratarec.node</c>, <c>stratarec.outcome</c> =
    /// <c>succeeded</c> | <c>degraded</c> | <c>fallback</c> | <c>skipped_when</c> | <c>skipped_deadline</c> |
    /// <c>skipped_input</c> | <c>failed</c>.
    /// </summary>
    public static readonly Counter<long> NodeOutcomes = Meter.CreateCounter<long>(Names.NodeOutcomes, "{node}", "Node outcomes by category.");

    /// <summary>
    /// NaN/±∞ values replaced by the default or marked missing (design 4.3; <c>column_nonfinite_total</c>). Tags for
    /// node outputs: <c>stratarec.pipeline</c>, <c>stratarec.node</c>, <c>stratarec.column</c>; for features, which no
    /// node produces: <c>stratarec.column</c>.
    /// </summary>
    public static readonly Counter<long> ColumnNonFinite = Meter.CreateCounter<long>(Names.ColumnNonFinite, "{value}", "Non-finite values replaced or masked.");

    /// <summary>
    /// Values outside the int32 storage of an <c>int32</c> or <c>list&lt;int32&gt;</c> column or feature, replaced by
    /// the default or marked missing instead of wrapping (design 4.3; <c>column_out_of_range_total</c>). Recorded once
    /// per pipeline execution: the expression backend counts them per request and does not know the column. Tags:
    /// <c>stratarec.pipeline</c>.
    /// </summary>
    public static readonly Counter<long> ColumnOutOfRange = Meter.CreateCounter<long>(Names.ColumnOutOfRange, "{value}", "Values outside the int32 storage range, replaced or masked.");

    // Data sources: feature connectors (per max_batch chunk) and candidate sources (per call).

    /// <summary>
    /// Time the request waited for one call: until it completed, or until the source timeout or deadline. Tags:
    /// <c>stratarec.source</c>, <c>stratarec.source.kind</c>.
    /// </summary>
    public static readonly Histogram<double> SourceDuration = Seconds(Names.SourceDuration, "Latency of feature and candidate source calls.");

    /// <summary>
    /// Failed source calls. Tags: <c>stratarec.source</c>, <c>stratarec.source.kind</c>, <c>stratarec.outcome</c> =
    /// <c>timeout</c> | <c>error</c>.
    /// </summary>
    public static readonly Counter<long> SourceErrors = Meter.CreateCounter<long>(Names.SourceErrors, "{call}", "Failed feature and candidate source calls.");

    /// <summary>Keys per feature connector call. Tags: <c>stratarec.source</c>, <c>stratarec.source.kind</c>.</summary>
    public static readonly Histogram<long> SourceKeys = Meter.CreateHistogram<long>(Names.SourceKeys, "{key}",
        "Keys per feature source call.", null, new InstrumentAdvice<long> { HistogramBucketBoundaries = CountBuckets });

    // Model calls: one per batch.

    /// <summary>Latency of one model call (batch). Tags: <c>stratarec.model</c>.</summary>
    public static readonly Histogram<double> ModelDuration = Seconds(Names.ModelDuration, "Latency of model calls.");

    /// <summary>Failed model calls. Tags: <c>stratarec.model</c>, <c>stratarec.outcome</c> = <c>timeout</c> | <c>error</c>.</summary>
    public static readonly Counter<long> ModelErrors = Meter.CreateCounter<long>(Names.ModelErrors, "{call}", "Failed model calls.");

    /// <summary>Model nodes served by their fallback node. Tags: <c>stratarec.model</c>.</summary>
    public static readonly Counter<long> ModelFallbacks = Meter.CreateCounter<long>(Names.ModelFallbacks, "{node}", "Model nodes served by their fallback.");

    // Plans.

    /// <summary>Time to compile a plan, failed compiles included. Tags: <c>stratarec.pipeline</c>.</summary>
    public static readonly Histogram<double> PlanCompileDuration = Seconds(Names.PlanCompileDuration, "Plan compile time.");

    /// <summary>Compiled node variants in the plan being served (design 12.5). Tags: <c>stratarec.pipeline</c>.</summary>
    public static readonly Gauge<long> PlanVariants = Meter.CreateGauge<long>(Names.PlanVariants, "{variant}", "Node variants of the served plan.");

    /// <summary>Release sequence (<c>seq</c>) of the plan being served (design 10.2). Tags: <c>stratarec.pipeline</c>.</summary>
    public static readonly Gauge<long> PlanSequence = Meter.CreateGauge<long>(Names.PlanSequence, null, "Release sequence of the served plan.");

    /// <summary>
    /// Plan loads (design 10.3). Tags: <c>stratarec.pipeline</c>, <c>stratarec.outcome</c> = <c>applied</c> |
    /// <c>compile_failed</c> | <c>rejected</c> | <c>stale</c>.
    /// </summary>
    public static readonly Counter<long> PlanLoads = Meter.CreateCounter<long>(Names.PlanLoads, "{load}", "Plan loads by outcome.");

    // Log pipeline (design 8.6).

    /// <summary>Batches queued or being written. Tags: <c>stratarec.log.delivery</c> = <c>durable</c> | <c>best_effort</c>.</summary>
    public static readonly UpDownCounter<long> LogQueueDepth = Meter.CreateUpDownCounter<long>(Names.LogQueueDepth, "{batch}", "Log batches queued or being written.");

    /// <summary>
    /// Batches per tap and fate; drops feed the offline weight correction (design 8.4). Tags: <c>stratarec.tap</c>,
    /// <c>stratarec.outcome</c> = <c>delivered</c> | <c>dropped</c>.
    /// </summary>
    public static readonly Counter<long> LogBatches = Meter.CreateCounter<long>(Names.LogBatches, "{batch}", "Log batches delivered or dropped per tap.");

    /// <summary>Durable batches neither queued nor spilled: a gap in <c>seq</c> downstream. No tags.</summary>
    public static readonly Counter<long> LogDurableOverflow = Meter.CreateCounter<long>(Names.LogDurableOverflow, "{batch}", "Durable log batches lost to a full queue.");

    /// <summary>Batches a sink rejected permanently. No tags.</summary>
    public static readonly Counter<long> LogRejected = Meter.CreateCounter<long>(Names.LogRejected, "{batch}", "Log batches rejected permanently by the sink.");

    private static Histogram<double> Seconds(string name, string description) =>
        Meter.CreateHistogram<double>(name, "s", description, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = SecondsBuckets });

    private static Histogram<long> Rows(string name, string description) =>
        Meter.CreateHistogram<long>(name, "{row}", description, null, new InstrumentAdvice<long> { HistogramBucketBoundaries = CountBuckets });

    /// <summary><paramref name="count"/> boundaries from <paramref name="first"/> growing by <paramref name="ratio"/>, rounded to three significant digits.</summary>
    private static double[] Geometric(double first, double ratio, int count)
    {
        var bounds = new double[count];
        for (var i = 0; i < count; i++)
        {
            var value = first * Math.Pow(ratio, i);
            bounds[i] = Math.Round(value, Math.Clamp(2 - (int)Math.Floor(Math.Log10(value)), 0, 15));
        }

        return bounds;
    }

    private static long[] PowersOfTwo(int fromExponent, int toExponent)
    {
        var bounds = new long[toExponent - fromExponent + 1];
        for (var i = 0; i < bounds.Length; i++)
        {
            bounds[i] = 1L << (fromExponent + i);
        }

        return bounds;
    }
}
