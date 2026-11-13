using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>The one method a host calls (design 3.4). Transport, authentication and threading belong to the host.</summary>
public interface IRecEngine
{
    ValueTask<RecResult> ExecuteAsync(RecRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A recommendation request.</summary>
public sealed class RecRequest
{
    public required string PipelineId { get; init; }

    public required string RequestId { get; init; }

    /// <summary>Inputs validated against the pipeline's <c>request_schema</c>.</summary>
    public required RequestInputs Inputs { get; init; }

    /// <summary>
    /// Arms from an external experimentation platform as <c>experiment/arm</c> ids (design 11.1). Unknown arms are
    /// ignored and counted. Null uses the configured <see cref="IAssignmentProvider"/> or the built-in assignment.
    /// </summary>
    public IReadOnlyList<string>? Assignment { get; init; }

    public required TimeSpan Deadline { get; init; }

    /// <summary>Debug options; the host decides who may use them.</summary>
    public DebugOptions? Debug { get; init; }

    /// <summary>Deterministic seed; defaults to a hash of the request id.</summary>
    public long? Seed { get; init; }

    /// <summary>Request time in Unix milliseconds; defaults to now. Set it to replay a request exactly.</summary>
    public long? TimeMs { get; init; }
}

/// <summary>Debugging controls (design 13.3), authorized by the host.</summary>
public sealed class DebugOptions
{
    /// <summary>Arms to force (<c>experiment/arm</c>), overriding assignment. Unknown arms fail the request.</summary>
    public IReadOnlyList<string>? ForceArms { get; init; }

    /// <summary>Include per-node outcomes in <see cref="RecResult.Trace"/>.</summary>
    public bool Trace { get; init; }

    /// <summary>Return log batches in <see cref="RecResult.LogBatches"/> instead of writing them (design 8.6 item 6).</summary>
    public bool CaptureLogs { get; init; }
}

/// <summary>Assignment and trigger record of a request (design 12.6).</summary>
/// <param name="Assigned">Assigned arms (<c>experiment/arm</c>) for intent-to-treat analysis.</param>
/// <param name="Triggered">Experiments whose site the request reached, for triggered analysis.</param>
/// <param name="UnknownArms">External arms that are not in the current plan (ignored).</param>
public sealed record ExperimentRecord(IReadOnlyList<string> Assigned, IReadOnlyList<string> Triggered, int UnknownArms);

/// <summary>The result of a request.</summary>
public sealed class RecResult
{
    public required bool Succeeded { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<RankedItem> Items { get; init; }

    public required ExperimentRecord Experiments { get; init; }

    /// <summary>Content id of the manifest the plan was built from (design 3.4).</summary>
    public required string ManifestId { get; init; }

    /// <summary>The pipeline that served the result: the requested one or its <c>fallback_pipeline</c> (design 5.6).</summary>
    public required string ServedBy { get; init; }

    /// <summary>Output column → producing node (lineage).</summary>
    public IReadOnlyDictionary<string, string>? ColumnProducers { get; init; }

    public IReadOnlyList<NodeOutcome>? Trace { get; init; }

    /// <summary>Captured log batches when <see cref="DebugOptions.CaptureLogs"/> was set.</summary>
    public IReadOnlyList<Logging.LogBatch>? LogBatches { get; init; }

    public double ElapsedMs { get; init; }
}

/// <summary>A compiled pipeline currently being served.</summary>
public sealed class LoadedPipeline
{
    public required ExecutionPlan Plan { get; init; }

    /// <summary>Built-in assignment, or null when the manifest has no assignment section.</summary>
    public TrafficAssigner? Assigner { get; init; }

    public string? FallbackPipeline { get; init; }

    public required PlanReport Report { get; init; }

    /// <summary>Release order of this plan: the manifest <c>seq</c>, or the engine's load ticket for local loads.</summary>
    public required long Sequence { get; init; }

    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Request-level metric tags of the plan (design 13.1), built once per loaded plan.</summary>
    internal RequestTelemetry Telemetry =>
        _telemetry ?? Interlocked.CompareExchange(ref _telemetry, RequestTelemetry.Create(Plan), null) ?? _telemetry!;

    private RequestTelemetry? _telemetry;
}

/// <summary>Outcome of a plan load (design 10.3 step 5: applied or rejected).</summary>
public sealed record PlanLoadResult(
    string PipelineId,
    bool Applied,
    string ManifestId,
    TimeSpan CompileTime,
    IReadOnlyList<ConfigDiagnostic> Diagnostics,
    PlanReport Report);

/// <summary>
/// The engine: holds one immutable plan per pipeline and swaps it atomically when a new one compiles (design 10.3).
/// In-flight requests keep the plan they started with; a plan that fails to compile never replaces the current one.
/// </summary>
/// <remarks>
/// Observability (design 13): each request gets one span (<see cref="StrataRecTracing.StartRequest"/>) and request-level
/// metrics per pipeline and per assigned arm; plan loads are metrics and event logs. Everything is optional: without
/// listeners or a logger factory the engine records nothing and allocates nothing for it.
/// </remarks>
public sealed class RecEngine : IRecEngine
{
    private readonly ConcurrentDictionary<string, LoadedPipeline> _pipelines = new(StringComparer.Ordinal);
    private readonly object _publishGate = new();
    private long _loadTicket;
    private readonly PipelineExecutor _executor;
    private readonly IAssignmentProvider? _assignmentProvider;
    private readonly ILogger _logger;

    /// <param name="executor">Runs the plans; defaults to one sharing <paramref name="loggerFactory"/>.</param>
    /// <param name="loggerFactory">Receives plan load events (design 13.2); null logs nothing.</param>
    public RecEngine(
        PlanEnvironment? environment = null,
        IAssignmentProvider? assignmentProvider = null,
        PipelineExecutor? executor = null,
        ILoggerFactory? loggerFactory = null)
    {
        Environment = environment ?? new PlanEnvironment();
        _assignmentProvider = assignmentProvider;
        _executor = executor ?? new PipelineExecutor(loggerFactory: loggerFactory);
        _logger = loggerFactory?.CreateLogger<RecEngine>() ?? NullLogger<RecEngine>.Instance;
    }

    /// <summary>Operators, connectors and clients this binary provides.</summary>
    public PlanEnvironment Environment { get; }

    /// <summary>Ids of the pipelines currently served, sorted.</summary>
    public List<string> PipelineIds()
    {
        var ids = new List<string>(_pipelines.Keys);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    public bool TryGetPipeline(string pipelineId, out LoadedPipeline pipeline) => _pipelines.TryGetValue(pipelineId, out pipeline!);

    /// <summary>
    /// Compiles a plan and, when it has no errors, makes it current for its pipeline. Thread-safe; call it from a
    /// background thread (design 10.3 step 2).
    /// </summary>
    /// <param name="armRanges">Bucket ranges per experiment and arm (from the manifest or allocation file).</param>
    /// <param name="sequence">
    /// Release order (the manifest's <c>seq</c>, design 10.2). A plan never replaces one with an equal or higher
    /// sequence, so a slow load of an older release cannot overwrite a newer one that finished first. When omitted,
    /// a ticket taken at the start of the call orders local loads; do not mix the two schemes for one pipeline.
    /// </param>
    public PlanLoadResult Load(PlanCompileRequest request, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>>? armRanges = null, long? sequence = null)
    {
        var order = sequence ?? Interlocked.Increment(ref _loadTicket);
        var compilation = PlanCompiler.Compile(request, Environment);
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(compilation.Diagnostics);
        var pipelineId = request.Pipeline.PipelineId;
        if (compilation.Plan is not { } plan)
        {
            PlanLoadTelemetry.CompileFailed(_logger, pipelineId, request.ManifestId, diagnostics);
            return new PlanLoadResult(pipelineId, false, request.ManifestId, TimeSpan.Zero, diagnostics.Items, compilation.Report);
        }

        TrafficAssigner? assigner = null;
        if (request.Assignment is { } assignment)
        {
            assigner = TrafficAssigner.Create(assignment, plan, armRanges ?? new Dictionary<string, IReadOnlyDictionary<string, int[]>>(), diagnostics);
            if (diagnostics.HasErrors)
            {
                PlanLoadTelemetry.Rejected(_logger, pipelineId, request.ManifestId, diagnostics);
                return new PlanLoadResult(pipelineId, false, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
            }
        }

        var loaded = new LoadedPipeline
        {
            Plan = plan,
            Assigner = assigner,
            FallbackPipeline = request.Pipeline.FallbackPipeline,
            Report = compilation.Report,
            Sequence = order,
        };
        _ = loaded.Telemetry; // request metric tags are built with the plan, not on its first request
        long? served = null;
        lock (_publishGate)
        {
            if (_pipelines.TryGetValue(pipelineId, out var current) && current.Sequence >= order)
            {
                served = current.Sequence;
                diagnostics.Info("PLAN_STALE", "engine", string.Empty,
                    $"Not applied: pipeline '{pipelineId}' already serves sequence {current.Sequence} (this load is {order}).");
            }
            else
            {
                _pipelines[pipelineId] = loaded;
                PlanLoadTelemetry.Applied(plan, order); // gauges stay ordered with the swaps
            }
        }

        // Logged outside the lock: a slow logger must not delay other loads.
        if (served is { } currentSequence)
        {
            PlanLoadTelemetry.Stale(_logger, pipelineId, request.ManifestId, order, currentSequence);
            return new PlanLoadResult(pipelineId, false, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
        }

        PlanLoadTelemetry.LogApplied(_logger, plan, order);
        return new PlanLoadResult(pipelineId, true, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
    }

    public async ValueTask<RecResult> ExecuteAsync(RecRequest request, CancellationToken cancellationToken = default)
    {
        // One end-to-end deadline: assignment, the pipeline and its fallback pipeline all spend the same budget.
        var started = Stopwatch.GetTimestamp();

        // One span per request (design 13.2); null when nobody listens or the host's sampler drops it.
        var activity = StrataRecTracing.StartRequest(request.PipelineId, request.RequestId);
        LoadedPipeline? pipeline = null;
        var arms = RequestArms.None;
        var degraded = false;
        RecResult? result = null; // stays null when the request is aborted (cancelled by the caller, or a defect)
        try
        {
            if (!_pipelines.TryGetValue(request.PipelineId, out pipeline))
            {
                return result = Rejected(request, $"Unknown pipeline '{request.PipelineId}'.");
            }

            var assignment = await AssignAsync(pipeline, request, started, cancellationToken).ConfigureAwait(false);
            if (assignment.Error is { } assignmentError)
            {
                return result = Rejected(request, assignmentError);
            }

            arms = assignment.Arms;
            var unknown = assignment.Unknown;
            var remaining = request.Deadline - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return result = Rejected(request, "deadline exceeded before execution (assignment took the whole budget)");
            }

            var executed = await Run(pipeline, request, arms, remaining, cancellationToken).ConfigureAwait(false);
            remaining = request.Deadline - Stopwatch.GetElapsedTime(started);
            if (executed.Succeeded || pipeline.FallbackPipeline is not { } fallbackId || fallbackId == request.PipelineId
                || !_pipelines.TryGetValue(fallbackId, out var fallback) || remaining <= TimeSpan.Zero)
            {
                degraded = executed.Degraded;
                return result = ToResult(request, pipeline, executed, unknown, request.PipelineId);
            }

            // The pipeline failed: serve its fallback pipeline, without experiments, within what is left (design 5.6).
            degraded = true;
            var served = await Run(fallback, request, RequestArms.None, remaining, cancellationToken).ConfigureAwait(false);
            var converted = ToResult(request, fallback, served, unknown, fallbackId);
            return result = new RecResult
            {
                Succeeded = converted.Succeeded,
                Error = $"pipeline '{request.PipelineId}' failed: {executed.Error}" + (served.Succeeded ? string.Empty : $"; fallback '{fallbackId}' failed: {served.Error}"),
                Items = converted.Items,
                Experiments = new ExperimentRecord(executed.AssignedArms, executed.TriggeredExperiments, unknown),
                ManifestId = converted.ManifestId,
                ServedBy = fallbackId,
                ColumnProducers = converted.ColumnProducers,
                Trace = converted.Trace,
                LogBatches = converted.LogBatches,
                ElapsedMs = executed.ElapsedMs + served.ElapsedMs,
            };
        }
        finally
        {
            // What the caller saw (design 13.1 "实验桶"): an aborted request counts as an error, so arms whose requests
            // are slow enough to be cancelled do not look healthier.
            var failed = result is not { Succeeded: true };
            var empty = !failed && result!.Items.Count == 0;
            (pipeline?.Telemetry ?? RequestTelemetry.Unknown).Record(Stopwatch.GetElapsedTime(started).TotalSeconds, arms, failed, empty, degraded && !failed);
            if (activity is not null)
            {
                RequestTelemetry.EndSpan(activity, result, degraded && !failed, pipeline?.Plan, arms);
            }
        }
    }

    /// <summary>
    /// Assigns the request's arms: forced arms, the caller's external assignment, the assignment provider, or the
    /// built-in assigner. Returns an error instead of arms when the request must be rejected.
    /// </summary>
    private async ValueTask<(RequestArms Arms, int Unknown, string? Error)> AssignAsync(
        LoadedPipeline pipeline, RecRequest request, long started, CancellationToken cancellationToken)
    {
        if (request.Debug?.ForceArms is { } forced)
        {
            try
            {
                return (pipeline.Plan.ArmsFromIds(forced), 0, null);
            }
            catch (ArgumentException ex)
            {
                return (RequestArms.None, 0, ex.Message);
            }
        }

        if (request.Assignment is { } external)
        {
            var arms = MapExternal(pipeline.Plan, external, out var unknown);
            return (arms, unknown, null);
        }

        if (_assignmentProvider is null)
        {
            return (pipeline.Assigner?.Assign(request.Inputs) ?? RequestArms.None, 0, null);
        }

        // The provider shares the end-to-end deadline: it is cancelled at the deadline, and the wait is bounded even if
        // it ignores cancellation.
        var budget = request.Deadline - Stopwatch.GetElapsedTime(started);
        if (budget <= TimeSpan.Zero)
        {
            return (RequestArms.None, 0, "deadline exceeded before assignment");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        IReadOnlyList<string> ids;
        try
        {
            ids = await _assignmentProvider.AssignAsync(request.PipelineId, request.Inputs, deadline.Token).AsTask()
                .WaitAsync(budget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (RequestArms.None, 0, "deadline exceeded during assignment");
        }

        var assigned = MapExternal(pipeline.Plan, ids, out var unknownIds);
        return (assigned, unknownIds, null);
    }

    private ValueTask<PipelineResult> Run(LoadedPipeline pipeline, RecRequest request, RequestArms arms, TimeSpan deadline, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(pipeline.Plan, new PipelineRequest
        {
            RequestId = request.RequestId,
            Inputs = request.Inputs,
            Arms = arms,
            Deadline = deadline,
            Seed = request.Seed,
            TimeMs = request.TimeMs,
            CaptureLogs = request.Debug?.CaptureLogs == true,
        }, cancellationToken);

    /// <summary>Maps external arm ids; unknown ids and second arms in one layer are ignored and counted (design 11.1).</summary>
    private static RequestArms MapExternal(ExecutionPlan plan, IReadOnlyList<string> ids, out int unknown)
    {
        unknown = 0;
        var byLayer = new int[plan.Layers.Length];
        Array.Fill(byLayer, -1);
        string? domain = null;
        foreach (var id in ids)
        {
            if (!plan.TryGetArm(id, out var arm) || byLayer[arm.LayerIndex] >= 0)
            {
                unknown++;
                continue;
            }

            // Arms from different traffic domains never co-occur (design 11.1); their combination was not validated.
            var armDomain = plan.Layers[arm.LayerIndex].Domain;
            if (armDomain is not null && domain is not null && armDomain != domain)
            {
                unknown++;
                continue;
            }

            domain ??= armDomain;
            byLayer[arm.LayerIndex] = arm.Index;
        }

        return new RequestArms(byLayer);
    }

    private static RecResult ToResult(RecRequest request, LoadedPipeline pipeline, PipelineResult result, int unknown, string servedBy) => new()
    {
        Succeeded = result.Succeeded,
        Error = result.Error,
        Items = result.Items,
        Experiments = new ExperimentRecord(result.AssignedArms, result.TriggeredExperiments, unknown),
        ManifestId = pipeline.Plan.ManifestId,
        ServedBy = servedBy,
        ColumnProducers = result.ColumnProducers,
        Trace = request.Debug?.Trace == true ? result.Outcomes : null,
        LogBatches = request.Debug?.CaptureLogs == true ? result.LogBatches : null,
        ElapsedMs = result.ElapsedMs,
    };

    private static RecResult Rejected(RecRequest request, string error) => new()
    {
        Succeeded = false,
        Error = error,
        Items = Array.Empty<RankedItem>(),
        Experiments = new ExperimentRecord(Array.Empty<string>(), Array.Empty<string>(), 0),
        ManifestId = string.Empty,
        ServedBy = request.PipelineId,
    };
}
