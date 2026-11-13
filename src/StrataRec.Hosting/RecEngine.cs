using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.LocalTables;
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

    /// <summary>
    /// Record the request as a replay bundle in <see cref="RecResult.Recording"/> (design 13.4): inputs, assignment, every
    /// external response with its status and duration, every node decision, the sampling and the result. Costs nothing
    /// when off; when on, the recording allocates in proportion to the data the request fetched.
    /// </summary>
    public bool Record { get; init; }

    /// <summary>
    /// Receives the replay bundle of the request on every exit once a pipeline ran (design 13.4): with the result, and
    /// before the exception propagates when the request is aborted (cancelled by the caller, or a defect), which
    /// <see cref="RecResult.Recording"/> cannot carry. Setting it records the request. An exception it throws is logged
    /// and does not change the request's outcome.
    /// </summary>
    /// <remarks>
    /// An aborted request's bundle is a snapshot taken at the abort: calls still in flight are <c>outstanding</c>, and what
    /// they return afterwards is not in it. Nothing waits for them, so the cancellation reaches the caller as promptly as
    /// without recording. A request rejected or aborted before any pipeline ran has nothing to replay and no bundle.
    /// </remarks>
    public Action<ReplayBundle>? OnRecording { get; init; }
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

    /// <summary>
    /// The replay bundle when the request recorded and was executed (a request rejected before execution has nothing to
    /// replay). Save it with <see cref="Replay.ReplayBundleFile"/>. An aborted request returns no result; its bundle goes
    /// to <see cref="DebugOptions.OnRecording"/>.
    /// </summary>
    public ReplayBundle? Recording { get; init; }

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

    /// <summary>Content ids of the artifacts the plan was compiled from, when the loader knew them (replay bundles record them).</summary>
    public IReadOnlyDictionary<string, string> ArtifactIds { get; init; } = new Dictionary<string, string>();

    /// <summary>The plan's local tables (design 6.6), held while the plan is served.</summary>
    internal LocalTableLease? LocalTables { get; init; }

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
/// <para>Local feature tables (design 6.6) belong to <see cref="LocalTables"/>: a plan is served only once every table it
/// reads has completed its first load, and tables whose identity a new plan shares are taken over, not loaded again.
/// Disposing the engine stops their update sources.</para>
/// <para><b>Lifecycle.</b> One lock orders loads, publishing and disposal. A load registers itself (with its table
/// lease) under it, and publishes under it; a disposal marks the engine disposed under it and takes every registered
/// load and every served plan. So nothing is published after a disposal, a disposal cancels the loads still running,
/// and each lease the engine opened is returned exactly by its owner at that moment: the load until it publishes (in
/// its <c>finally</c> when it does not), the served plan afterwards (when it is replaced, or by the disposal).</para>
/// <para>Observability (design 13): each request gets one span (<see cref="StrataRecTracing.StartRequest"/>) and
/// request-level metrics per pipeline and per assigned arm; plan loads are metrics and event logs. Everything is
/// optional: without listeners or a logger factory the engine records nothing and allocates nothing for it.</para>
/// </remarks>
public sealed partial class RecEngine : IRecEngine, IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, LoadedPipeline> _pipelines = new(StringComparer.Ordinal);
    private readonly object _publishGate = new(); // the lifecycle lock: _loads, _disposed and every change of _pipelines
    private readonly HashSet<PendingLoad> _loads = new();
    private long _loadTicket;
    private readonly PipelineExecutor _executor;
    private readonly IAssignmentProvider? _assignmentProvider;
    private readonly ILogger _logger;
    private readonly bool _ownsLocalTables;
    private bool _disposed;

    /// <param name="executor">Runs the plans; defaults to one sharing <paramref name="loggerFactory"/>.</param>
    /// <param name="loggerFactory">Receives plan load events (design 13.2); null logs nothing.</param>
    /// <param name="localTables">Hosts the plans' local feature tables; null creates one the engine owns and disposes. A
    /// host passed in is the caller's to dispose (it may serve several engines).</param>
    public RecEngine(
        PlanEnvironment? environment = null,
        IAssignmentProvider? assignmentProvider = null,
        PipelineExecutor? executor = null,
        ILoggerFactory? loggerFactory = null,
        LocalTableHost? localTables = null)
    {
        Environment = environment ?? new PlanEnvironment();
        _assignmentProvider = assignmentProvider;
        _executor = executor ?? new PipelineExecutor(loggerFactory: loggerFactory);
        _logger = loggerFactory?.CreateLogger<RecEngine>() ?? NullLogger<RecEngine>.Instance;
        _ownsLocalTables = localTables is null;
        LocalTables = localTables ?? new LocalTableHost(loggerFactory: loggerFactory);
    }

    /// <summary>Operators, connectors and clients this binary provides.</summary>
    public PlanEnvironment Environment { get; }

    /// <summary>The local feature tables of the served plans (design 6.6).</summary>
    public LocalTableHost LocalTables { get; }

    /// <summary>Ids of the pipelines currently served, sorted.</summary>
    public List<string> PipelineIds()
    {
        var ids = new List<string>(_pipelines.Keys);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    public bool TryGetPipeline(string pipelineId, out LoadedPipeline pipeline) => _pipelines.TryGetValue(pipelineId, out pipeline!);

    /// <summary>
    /// Compiles a plan and, when it has no errors, makes it current for its pipeline once its local tables are loaded
    /// (<see cref="LoadAsync"/>). Blocks while new tables load; call it from a background thread (design 10.3 step 2).
    /// </summary>
    public PlanLoadResult Load(PlanCompileRequest request, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>>? armRanges = null, long? sequence = null) =>
        LoadAsync(request, armRanges, sequence).GetAwaiter().GetResult();

    /// <summary>
    /// Compiles a plan and, when it has no errors, makes it current for its pipeline. Thread-safe. A plan that reads local
    /// feature tables (design 6.6) becomes current only after every one of them has completed its first load; until then
    /// the current plan keeps serving. A table that fails to load rejects the plan with a <c>LOCAL_TABLE_LOAD_FAILED</c>
    /// error naming the binding and the bad file. Tables the current plan already holds with the same identity are taken
    /// over at once, so a configuration change that does not touch a table never reloads it.
    /// </summary>
    /// <param name="armRanges">Bucket ranges per experiment and arm (from the manifest or allocation file).</param>
    /// <param name="sequence">
    /// Release order (the manifest's <c>seq</c>, design 10.2). A plan never replaces one with an equal or higher
    /// sequence, so a slow load of an older release cannot overwrite a newer one that finished first. When omitted,
    /// a ticket taken at the start of the call orders local loads; do not mix the two schemes for one pipeline.
    /// </param>
    /// <param name="cancellationToken">Cancels the load until the plan is applied: checked when the call starts, while it
    /// waits for tables (the tables of other plans load on), and when it is about to publish.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the plan was
    /// applied; the current plan is unchanged.</exception>
    /// <exception cref="ObjectDisposedException">The engine is disposed, or was disposed before the plan was applied.</exception>
    public async Task<PlanLoadResult> LoadAsync(
        PlanCompileRequest request,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>>? armRanges = null,
        long? sequence = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = sequence ?? Interlocked.Increment(ref _loadTicket);
        var load = BeginLoad(cancellationToken);
        try
        {
            return await LoadWithTablesAsync(request, armRanges, order, load, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (load.Cancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Only the engine's disposal cancels a load other than through the caller's token (see DisposeAsync); the
            // load stopped wherever it was: waiting for tables, or compiling against its released lease.
            throw Disposed(ex);
        }
        finally
        {
            EndLoad(load);
        }
    }

    /// <summary>Registers a load and opens its lease; a disposed engine takes no load.</summary>
    private PendingLoad BeginLoad(CancellationToken cancellationToken)
    {
        lock (_publishGate)
        {
            if (_disposed)
            {
                throw Disposed(null);
            }

            var load = new PendingLoad(LocalTables.OpenLease(), CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            _loads.Add(load);
            return load;
        }
    }

    /// <summary>Every load ends here: a plan that was not published returns its lease (its new tables stop, shared ones
    /// lose a reference); a published one's lease belongs to the served plan.</summary>
    private void EndLoad(PendingLoad load)
    {
        bool published;
        lock (_publishGate)
        {
            _loads.Remove(load);
            published = load.Published;
        }

        load.Cancellation.Dispose(); // no disposal can cancel it any more: it is no longer registered
        if (!published)
        {
            load.Lease.Dispose(); // also when a disposal released it already: a lease is returned once
        }
    }

    private static ObjectDisposedException Disposed(Exception? cause) =>
        new("The engine was disposed before the plan was applied (or before the load started).", cause);

    /// <summary>Called by a load right before it takes the publish lock, its tables loaded: tests land a cancellation or a
    /// disposal in that window.</summary>
    internal Action? Publishing { get; set; }

    private async Task<PlanLoadResult> LoadWithTablesAsync(
        PlanCompileRequest request, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>>? armRanges, long order, PendingLoad load,
        CancellationToken cancellationToken)
    {
        var lease = load.Lease;
        var compilation = PlanCompiler.Compile(request, Environment, lease);
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

        // Readiness (design 6.6 local item 6): the plan is served only once its tables hold a complete load. A disposal
        // during the wait also stops the tables (their loads end as failures): the cancellation decides, not those.
        var failures = await lease.WaitLoadedAsync(load.Cancellation.Token).ConfigureAwait(false);
        load.Cancellation.Token.ThrowIfCancellationRequested();
        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                diagnostics.Error("LOCAL_TABLE_LOAD_FAILED", failure.Artifact, failure.Pointer, failure.Message, failure.Hint);
            }

            PlanLoadTelemetry.TablesFailed(_logger, pipelineId, request.ManifestId, diagnostics);
            return new PlanLoadResult(pipelineId, false, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
        }

        var loaded = new LoadedPipeline
        {
            Plan = plan,
            Assigner = assigner,
            FallbackPipeline = request.Pipeline.FallbackPipeline,
            Report = compilation.Report,
            Sequence = order,
            ArtifactIds = request.ArtifactIds,
            LocalTables = lease,
        };
        _ = loaded.Telemetry; // request metric tags are built with the plan, not on its first request
        Publishing?.Invoke();
        long? served = null;
        LoadedPipeline? replaced = null;
        bool disposed, cancelled;
        lock (_publishGate)
        {
            // Checked under the lock that disposal takes, right before the current plan changes: a disposed engine never
            // publishes again, and a cancelled load never replaces a plan.
            disposed = _disposed;
            cancelled = cancellationToken.IsCancellationRequested;
            if (!disposed && !cancelled)
            {
                if (_pipelines.TryGetValue(pipelineId, out var current) && current.Sequence >= order)
                {
                    served = current.Sequence;
                    diagnostics.Info("PLAN_STALE", "engine", string.Empty,
                        $"Not applied: pipeline '{pipelineId}' already serves sequence {current.Sequence} (this load is {order}).");
                }
                else
                {
                    replaced = current;
                    _pipelines[pipelineId] = loaded;
                    load.Published = true; // the lease is the served plan's now
                    PlanLoadTelemetry.Applied(plan, order); // gauges stay ordered with the swaps
                }
            }
        }

        if (disposed || cancelled)
        {
            // The disposal cancelled this load under the same lock: LoadAsync reports which of the two it was.
            throw new OperationCanceledException(disposed ? load.Cancellation.Token : cancellationToken);
        }

        // Logged outside the lock: a slow logger must not delay other loads.
        if (served is { } currentSequence)
        {
            PlanLoadTelemetry.Stale(_logger, pipelineId, request.ManifestId, order, currentSequence);
            return new PlanLoadResult(pipelineId, false, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
        }

        // The replaced plan's tables that the new plan does not share are no longer used: their sources stop. Requests
        // still running on the old plan read the versions they pinned.
        replaced?.LocalTables?.Dispose();
        PlanLoadTelemetry.LogApplied(_logger, plan, order);
        return new PlanLoadResult(pipelineId, true, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
    }

    /// <summary>
    /// Cancels the loads still running (they throw <see cref="ObjectDisposedException"/>) and returns the table leases of
    /// those loads and of the served plans, so the tables no other user holds stop their update sources. A table host
    /// passed to the constructor keeps serving its other engines; one the engine created is disposed. Requests running
    /// meanwhile keep the versions they pinned; the served plans stay readable, and no load is taken any more.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var leases = new List<LocalTableLease>();
        lock (_publishGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var load in _loads)
            {
                // The load's waits complete on the thread pool, never on this thread under the lock; it ends with
                // ObjectDisposedException (see LoadAsync) and its EndLoad waits for this lock.
                _ = load.Cancellation.CancelAsync();
                leases.Add(load.Lease);
            }

            foreach (var (_, pipeline) in _pipelines)
            {
                if (pipeline.LocalTables is { } served)
                {
                    leases.Add(served);
                }
            }
        }

        foreach (var lease in leases)
        {
            lease.Dispose(); // stopping a source runs none of its code here (LocalTableHost)
        }

        if (_ownsLocalTables)
        {
            await LocalTables.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A load between its start and its end (see the lifecycle remarks): its lease, and the cancellation a disposal uses.</summary>
    private sealed class PendingLoad(LocalTableLease lease, CancellationTokenSource cancellation)
    {
        public LocalTableLease Lease { get; } = lease;

        /// <summary>Linked to the caller's token; cancelled on its own only by a disposal.</summary>
        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>Set under the lifecycle lock when the plan is published: the served plan owns the lease from then on.</summary>
        public bool Published { get; set; }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

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

        // A recorded request resolves its seed and time once, so both pipelines see the same values and the bundle holds
        // exactly what ran (design 5.7).
        var record = request.Debug is { } debug && (debug.Record || debug.OnRecording is not null);
        var seed = record ? request.Seed ?? PipelineRequest.DefaultSeed(request.RequestId) : request.Seed;
        var timeMs = record ? request.TimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : request.TimeMs;
        var runs = record ? new RunCapture() : null;
        var unknown = 0;
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
            unknown = assignment.Unknown;
            var remaining = request.Deadline - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return result = Rejected(request, "deadline exceeded before execution (assignment took the whole budget)");
            }

            var executed = await Run(pipeline, request, arms, remaining, seed, timeMs, runs, cancellationToken).ConfigureAwait(false);
            remaining = request.Deadline - Stopwatch.GetElapsedTime(started);
            if (executed.Succeeded || pipeline.FallbackPipeline is not { } fallbackId || fallbackId == request.PipelineId
                || !_pipelines.TryGetValue(fallbackId, out var fallback) || remaining <= TimeSpan.Zero)
            {
                degraded = executed.Degraded;
                result = ToResult(request, pipeline, executed, unknown, request.PipelineId);
                return result = record ? Recorded(request, pipeline, seed!.Value, timeMs!.Value, result, executed, null) : result;
            }

            // The pipeline failed: serve its fallback pipeline, without experiments, within what is left (design 5.6).
            degraded = true;
            var served = await Run(fallback, request, RequestArms.None, remaining, seed, timeMs, runs, cancellationToken).ConfigureAwait(false);
            var converted = ToResult(request, fallback, served, unknown, fallbackId);
            result = Combine(request, executed, converted, served, unknown, fallbackId);
            return result = record ? Recorded(request, pipeline, seed!.Value, timeMs!.Value, result, executed, served) : result;
        }
        catch (Exception) when (runs is { Runs.Count: > 0 } && pipeline is not null)
        {
            // Aborted while (or after) a pipeline ran: the caller gets the exception, the recording callback the bundle of
            // what ran up to the abort. Nothing here waits for calls still in flight.
            Deliver(request, AbortedBundle(request, pipeline, seed!.Value, timeMs!.Value, runs.Runs, unknown));
            throw;
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

    /// <param name="runs">Collects the run's recording on every exit; null when the request does not record.</param>
    private ValueTask<PipelineResult> Run(
        LoadedPipeline pipeline, RecRequest request, RequestArms arms, TimeSpan deadline, long? seed, long? timeMs, RunCapture? runs,
        CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(pipeline.Plan, new PipelineRequest
        {
            RequestId = request.RequestId,
            Inputs = request.Inputs,
            Arms = arms,
            Deadline = deadline,
            Seed = seed,
            TimeMs = timeMs,
            CaptureLogs = request.Debug?.CaptureLogs == true,
            OnRecording = runs is null ? null : runs.Add,
        }, cancellationToken);

    /// <summary>The result of a request its fallback pipeline served: the primary's assignment and error, the fallback's items.</summary>
    private static RecResult Combine(RecRequest request, PipelineResult executed, RecResult converted, PipelineResult served, int unknown, string fallbackId) => new()
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
