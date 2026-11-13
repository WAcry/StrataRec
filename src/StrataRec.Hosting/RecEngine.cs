using System.Collections.Concurrent;
using StrataRec.Artifacts.Model;
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

    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
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
public sealed class RecEngine : IRecEngine
{
    private readonly ConcurrentDictionary<string, LoadedPipeline> _pipelines = new(StringComparer.Ordinal);
    private readonly PipelineExecutor _executor;
    private readonly IAssignmentProvider? _assignmentProvider;

    public RecEngine(PlanEnvironment? environment = null, IAssignmentProvider? assignmentProvider = null, PipelineExecutor? executor = null)
    {
        Environment = environment ?? new PlanEnvironment();
        _assignmentProvider = assignmentProvider;
        _executor = executor ?? new PipelineExecutor();
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
    public PlanLoadResult Load(PlanCompileRequest request, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>>? armRanges = null)
    {
        var compilation = PlanCompiler.Compile(request, Environment);
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(compilation.Diagnostics);
        var pipelineId = request.Pipeline.PipelineId;
        if (compilation.Plan is not { } plan)
        {
            return new PlanLoadResult(pipelineId, false, request.ManifestId, TimeSpan.Zero, diagnostics.Items, compilation.Report);
        }

        TrafficAssigner? assigner = null;
        if (request.Assignment is { } assignment)
        {
            assigner = TrafficAssigner.Create(assignment, plan, armRanges ?? new Dictionary<string, IReadOnlyDictionary<string, int[]>>(), diagnostics);
            if (diagnostics.HasErrors)
            {
                return new PlanLoadResult(pipelineId, false, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
            }
        }

        _pipelines[pipelineId] = new LoadedPipeline
        {
            Plan = plan,
            Assigner = assigner,
            FallbackPipeline = request.Pipeline.FallbackPipeline,
            Report = compilation.Report,
        };
        return new PlanLoadResult(pipelineId, true, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
    }

    public async ValueTask<RecResult> ExecuteAsync(RecRequest request, CancellationToken cancellationToken = default)
    {
        if (!_pipelines.TryGetValue(request.PipelineId, out var pipeline))
        {
            return Rejected(request, $"Unknown pipeline '{request.PipelineId}'.");
        }

        RequestArms arms;
        var unknown = 0;
        if (request.Debug?.ForceArms is { } forced)
        {
            try
            {
                arms = pipeline.Plan.ArmsFromIds(forced);
            }
            catch (ArgumentException ex)
            {
                return Rejected(request, ex.Message);
            }
        }
        else if (request.Assignment is { } external)
        {
            arms = MapExternal(pipeline.Plan, external, out unknown);
        }
        else if (_assignmentProvider is not null)
        {
            var ids = await _assignmentProvider.AssignAsync(request.PipelineId, request.Inputs, cancellationToken).ConfigureAwait(false);
            arms = MapExternal(pipeline.Plan, ids, out unknown);
        }
        else
        {
            arms = pipeline.Assigner?.Assign(request.Inputs) ?? RequestArms.None;
        }

        var result = await Run(pipeline, request, arms, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded || pipeline.FallbackPipeline is not { } fallbackId || fallbackId == request.PipelineId
            || !_pipelines.TryGetValue(fallbackId, out var fallback))
        {
            return ToResult(request, pipeline, result, unknown, request.PipelineId);
        }

        // The pipeline failed: serve its fallback pipeline, without experiments (design 5.6).
        var served = await Run(fallback, request, RequestArms.None, cancellationToken).ConfigureAwait(false);
        var converted = ToResult(request, fallback, served, unknown, fallbackId);
        return new RecResult
        {
            Succeeded = converted.Succeeded,
            Error = $"pipeline '{request.PipelineId}' failed: {result.Error}" + (served.Succeeded ? string.Empty : $"; fallback '{fallbackId}' failed: {served.Error}"),
            Items = converted.Items,
            Experiments = new ExperimentRecord(result.AssignedArms, result.TriggeredExperiments, unknown),
            ManifestId = converted.ManifestId,
            ServedBy = fallbackId,
            ColumnProducers = converted.ColumnProducers,
            Trace = converted.Trace,
            LogBatches = converted.LogBatches,
            ElapsedMs = result.ElapsedMs + served.ElapsedMs,
        };
    }

    private ValueTask<PipelineResult> Run(LoadedPipeline pipeline, RecRequest request, RequestArms arms, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(pipeline.Plan, new PipelineRequest
        {
            RequestId = request.RequestId,
            Inputs = request.Inputs,
            Arms = arms,
            Deadline = request.Deadline,
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
        foreach (var id in ids)
        {
            if (!plan.TryGetArm(id, out var arm) || byLayer[arm.LayerIndex] >= 0)
            {
                unknown++;
                continue;
            }

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
