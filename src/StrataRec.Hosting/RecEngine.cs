using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Threading;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Bindings;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>
/// What a host calls (design 3.4): <see cref="ExecuteAsync"/>, and two read-only questions a transport needs before it
/// can build a request, so a host needs no concrete engine. Transport, authentication and threading belong to the host.
/// </summary>
public interface IRecEngine
{
    /// <summary>
    /// Runs a request on the plan its pipeline serves now. Failures are results, not exceptions:
    /// <see cref="RecResult.ErrorKind"/> says whose they are. Throws only when the caller cancels, when the engine is
    /// disposed, or on a defect.
    /// </summary>
    ValueTask<RecResult> ExecuteAsync(RecRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ids of the pipelines served now, sorted (ordinal); a load or a disposal changes them.</summary>
    IReadOnlyList<string> PipelineIds();

    /// <summary>
    /// The request schema of the plan a pipeline serves now, to read a request's inputs with
    /// (<see cref="RequestSchema.TryRead"/>); false for a pipeline not served. A plan loaded between this call and
    /// <see cref="ExecuteAsync"/> may declare other inputs: the engine checks the inputs again against the plan it runs, and
    /// its answer (<see cref="RecErrorKind.InvalidRequest"/>) is the one that counts.
    /// </summary>
    bool TryGetRequestSchema(string pipelineId, [NotNullWhen(true)] out RequestSchema? schema);
}

/// <summary>
/// Whose a failed request is, so a host can answer without parsing <see cref="RecResult.Error"/>: over HTTP, 404, 400,
/// 504 and 500.
/// </summary>
public enum RecErrorKind
{
    /// <summary>The request succeeded (perhaps served by its fallback pipeline: <see cref="RecResult.ServedBy"/>).</summary>
    None = 0,

    /// <summary>The engine serves no pipeline of that id. Nothing ran.</summary>
    UnknownPipeline,

    /// <summary>
    /// The caller's mistake: inputs the pipeline's <c>request_schema</c> rejects (<see cref="RecResult.ErrorPointer"/> says
    /// which), or forced arms that are unknown or conflict (<see cref="DebugOptions.ForceArms"/>). Checked before anything
    /// runs: no assignment is asked for and no fallback pipeline serves it.
    /// </summary>
    InvalidRequest,

    /// <summary>The deadline was spent before the pipeline could start (by the assignment provider). Nothing ran.</summary>
    DeadlineExceeded,

    /// <summary>
    /// The pipeline ran and failed, and so did its fallback pipeline or there was none (or no time left for it): the
    /// service's failure, not the caller's.
    /// </summary>
    Failed,
}

/// <summary>A recommendation request.</summary>
public sealed class RecRequest
{
    public required string PipelineId { get; init; }

    public required string RequestId { get; init; }

    /// <summary>
    /// Inputs by the pipeline's <c>request_schema</c>: read them from JSON with <see cref="RequestSchema.TryRead"/>
    /// (<see cref="IRecEngine.TryGetRequestSchema"/>). The engine checks them against the plan it runs before anything
    /// else; inputs it rejects fail the request with <see cref="RecErrorKind.InvalidRequest"/>.
    /// </summary>
    public required RequestInputs Inputs { get; init; }

    /// <summary>
    /// The arms the experimentation platform assigned to the request, as <c>experiment/arm</c> ids (design 11.1): the
    /// caller asked the platform itself. Arms the request cannot run with are ignored, counted and reported in
    /// <see cref="ExperimentRecord.Ignored"/> (<see cref="ExternalArms"/> has the rules). An empty list is the platform's
    /// answer "no arms". Null asks the configured <see cref="IAssignmentProvider"/>; without one the request runs the
    /// baseline.
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
    /// <summary>
    /// Arms to force (<c>experiment/arm</c>), overriding assignment. Strict, unlike the platform's arms: an unknown arm or
    /// two arms of one layer fail the request.
    /// </summary>
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

    /// <summary>
    /// Candidates to explain (design 13.3), as entity ids of the pipeline's key type: <see cref="RecResult.Explanation"/>
    /// then says, for each, where it ended (returned, past the limit, dropped by which node, never recalled) and its state
    /// after every node of the run that served: live or not, its rank, the values of the columns the node read and wrote,
    /// and the operator's reason where it gives one. At most <see cref="MaxExplainIds"/>; ids of the other key type or
    /// more ids than that fail the request with <see cref="RecErrorKind.InvalidRequest"/> before anything runs. Costs
    /// nothing when null; when set, work and memory grow with ids × nodes, not with the candidates.
    /// </summary>
    public IReadOnlyList<EntityKey>? ExplainIds { get; init; }

    /// <summary>The most ids <see cref="ExplainIds"/> may hold.</summary>
    public const int MaxExplainIds = ExplainTargets.MaxCount;
}

/// <summary>Assignment and trigger record of a request (design 12.6).</summary>
/// <remarks>
/// A request rejected before its arms are decided (unknown pipeline, inputs the request schema rejects, invalid forced
/// arms, a provider past the deadline) has an empty record. One rejected after (out of budget before execution) carries
/// its assigned and ignored arms, which the metrics already counted, and no triggered experiment.
/// </remarks>
/// <param name="Assigned">Assigned arms (<c>experiment/arm</c>) for intent-to-treat analysis.</param>
/// <param name="Triggered">Experiments whose site the request reached, for triggered analysis.</param>
/// <param name="Ignored">
/// Arm ids the platform sent that the request did not run with, and why (<see cref="ExternalArms"/>), sorted by id;
/// empty when every id applied. A request that ignores arms is still served: the platform's view of its exposure and
/// this record differ, which is what offline analysis needs to see.
/// </param>
public sealed record ExperimentRecord(IReadOnlyList<string> Assigned, IReadOnlyList<string> Triggered, IReadOnlyList<IgnoredArm> Ignored);

/// <summary>The result of a request. Write it as JSON with <see cref="RecResultJson"/>.</summary>
public sealed class RecResult
{
    /// <summary>The request's id (<see cref="RecRequest.RequestId"/>), so a result can be logged or written on its own.</summary>
    public required string RequestId { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary><see cref="RecErrorKind.None"/> exactly when <see cref="Succeeded"/>; otherwise whose the failure is.</summary>
    public RecErrorKind ErrorKind { get; init; }

    /// <summary>
    /// What went wrong, for people: the failure, or for a request its fallback pipeline served, why the requested pipeline
    /// failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// For <see cref="RecErrorKind.InvalidRequest"/> caused by an input, the input's JSON pointer relative to a request body
    /// of the shape <c>{ "inputs": { … } }</c>, e.g. <c>/inputs/user_id</c>; null otherwise.
    /// </summary>
    public string? ErrorPointer { get; init; }

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

    /// <summary>
    /// The explained candidates when <see cref="DebugOptions.ExplainIds"/> was set and a pipeline ran: those of the run that
    /// served the result (<see cref="RunExplanation.PipelineId"/>, the fallback pipeline when it served); null otherwise.
    /// </summary>
    public RunExplanation? Explanation { get; init; }

    public double ElapsedMs { get; init; }
}

/// <summary>A compiled pipeline currently being served.</summary>
public sealed class LoadedPipeline
{
    public required ExecutionPlan Plan { get; init; }

    public string? FallbackPipeline { get; init; }

    public required PlanReport Report { get; init; }

    /// <summary>Release order of this plan: the manifest <c>seq</c>, or the engine's load ticket for local loads.</summary>
    public required long Sequence { get; init; }

    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Content ids of the artifacts the plan was compiled from, when the loader knew them (replay bundles record them).</summary>
    public IReadOnlyDictionary<string, string> ArtifactIds { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// What the plan binds (design 6.4, 6.6): its local tables, held while the plan is served, and its clients and
    /// connectors, held until no request of the plan can still call them. Every run of the plan enters it.
    /// </summary>
    internal BindingLease Bindings { get; init; } = null!;

    /// <summary>The plan's experiment metadata as read-only views, built with the plan: every provider call passes these.</summary>
    internal PlanLayout Layout { get; init; } = null!;

    /// <summary>Request-level metric tags of the plan (design 13.1), built once per loaded plan.</summary>
    internal RequestTelemetry Telemetry =>
        _telemetry ?? Interlocked.CompareExchange(ref _telemetry, RequestTelemetry.Create(Plan), null) ?? _telemetry!;

    private RequestTelemetry? _telemetry;
}

/// <summary>
/// Read-only views of a plan's layers, experiments and arms (<see cref="AssignmentContext"/>), built once per plan so
/// that asking the assignment provider allocates no views.
/// </summary>
internal sealed class PlanLayout(ExecutionPlan plan)
{
    public IReadOnlyList<LayerInfo> Layers { get; } = Array.AsReadOnly(plan.Layers);

    public IReadOnlyList<ExperimentInfo> Experiments { get; } = Array.AsReadOnly(plan.Experiments);

    public IReadOnlyList<ArmInfo> Arms { get; } = Array.AsReadOnly(plan.Arms);
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
/// <para>What plans bind belongs to <see cref="Bindings"/> (design 6.4, 6.6): local feature tables, model clients,
/// candidate sources and connectors. A plan is served only once every table it reads has completed its first load and
/// every instance it uses has loaded; what a new plan shares by identity with the served one is taken over, not loaded
/// again. Disposing the engine stops the tables' update sources and disposes the instances once no request can call
/// them.</para>
/// <para><b>Lifecycle.</b> One lock orders loads, publishing and disposal. A load registers itself (with its binding
/// lease) under it, and publishes under it; a disposal marks the engine disposed under it and takes every registered
/// load and every served plan. So nothing is published after a disposal, a disposal cancels the loads still running,
/// and each lease the engine opened is retired exactly by its owner at that moment: the load until it publishes (in
/// its <c>finally</c> when it does not), the served plan afterwards (when it is replaced, or by the disposal).</para>
/// <para><b>Requests.</b> Every run enters its plan's lease and leaves it when the run and every call it started have
/// ended, so the instances of a replaced plan are disposed only after its last request, and the calls those requests
/// abandoned at a timeout, returned. A request that finds its plan's lease retired reads the current plan again; after a
/// disposal it throws <see cref="ObjectDisposedException"/>.</para>
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
    private readonly bool _ownsBindings;
    private bool _disposed;

    /// <param name="executor">Runs the plans; defaults to one sharing <paramref name="loggerFactory"/>.</param>
    /// <param name="loggerFactory">Receives plan load events (design 13.2); null logs nothing.</param>
    /// <param name="bindings">Hosts what the plans bind (tables, clients, connectors); null creates one the engine owns and
    /// disposes. A host passed in is the caller's to dispose (it may serve several engines), with its own options.</param>
    /// <param name="bindingOptions">Options of the host the engine creates: its secret store (design 6.4), load and
    /// shutdown bounds.</param>
    public RecEngine(
        PlanEnvironment? environment = null,
        IAssignmentProvider? assignmentProvider = null,
        PipelineExecutor? executor = null,
        ILoggerFactory? loggerFactory = null,
        BindingHost? bindings = null,
        BindingHostOptions? bindingOptions = null)
    {
        if (bindings is not null && bindingOptions is not null)
        {
            throw new ArgumentException("A binding host passed in has its own options; pass one or the other.", nameof(bindingOptions));
        }

        Environment = environment ?? new PlanEnvironment();
        _assignmentProvider = assignmentProvider;
        _executor = executor ?? new PipelineExecutor(loggerFactory: loggerFactory);
        _logger = loggerFactory?.CreateLogger<RecEngine>() ?? NullLogger<RecEngine>.Instance;
        _ownsBindings = bindings is null;
        Bindings = bindings ?? new BindingHost(bindingOptions, loggerFactory);
    }

    /// <summary>Operators, connectors and clients this binary provides.</summary>
    public PlanEnvironment Environment { get; }

    /// <summary>What the served plans bind: local feature tables (design 6.6), model clients, candidate sources and
    /// connectors (design 6.4).</summary>
    public BindingHost Bindings { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> PipelineIds()
    {
        var ids = new List<string>(_pipelines.Keys);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <inheritdoc/>
    public bool TryGetRequestSchema(string pipelineId, [NotNullWhen(true)] out RequestSchema? schema)
    {
        schema = _pipelines.TryGetValue(pipelineId, out var pipeline) ? pipeline.Plan.RequestSchema : null;
        return schema is not null;
    }

    public bool TryGetPipeline(string pipelineId, out LoadedPipeline pipeline) => _pipelines.TryGetValue(pipelineId, out pipeline!);

    /// <summary>
    /// Compiles a plan and, when it has no errors, makes it current for its pipeline once what it binds has loaded
    /// (<see cref="LoadAsync"/>). Blocks while new tables and instances load; call it from a background thread (design 10.3
    /// step 2).
    /// </summary>
    public PlanLoadResult Load(PlanCompileRequest request, long? sequence = null) =>
        LoadAsync(request, sequence).GetAwaiter().GetResult();

    /// <summary>
    /// Compiles a plan and, when it has no errors, makes it current for its pipeline. Thread-safe. A plan becomes current
    /// only after every local feature table it reads (design 6.6) has completed its first load and every client and
    /// connector it uses (design 6.4) has loaded; until then the current plan keeps serving. Model signatures that need a
    /// loaded client or I/O are looked up here, concurrently, and checked before the plan is served (design 6.9). A table
    /// that fails to load rejects the plan with <c>LOCAL_TABLE_LOAD_FAILED</c> naming the binding and the bad file, an
    /// instance with <c>BINDING_LOAD_FAILED</c> at its binding. What the current plan already holds with the same identity
    /// is taken over at once, so a configuration change that does not touch a binding never reloads it.
    /// </summary>
    /// <param name="sequence">
    /// Release order (the manifest's <c>seq</c>, design 10.2). A plan never replaces one with an equal or higher
    /// sequence, so a slow load of an older release cannot overwrite a newer one that finished first. When omitted,
    /// a ticket taken at the start of the call orders local loads; do not mix the two schemes for one pipeline.
    /// </param>
    /// <param name="cancellationToken">Cancels the load until the plan is applied: checked when the call starts, while it
    /// waits for signatures and loads (the bindings of other plans load on), and when it is about to publish.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the plan was
    /// applied; the current plan is unchanged.</exception>
    /// <exception cref="ObjectDisposedException">The engine is disposed, or was disposed before the plan was applied.</exception>
    public async Task<PlanLoadResult> LoadAsync(
        PlanCompileRequest request,
        long? sequence = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = sequence ?? Interlocked.Increment(ref _loadTicket);
        var load = BeginLoad(cancellationToken);
        try
        {
            return await LoadWithBindingsAsync(request, order, load, cancellationToken).ConfigureAwait(false);
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

            var load = new PendingLoad(Bindings.OpenLease(), new ObservedCancellationSource(CancellationSite.PlanLoad, cancellationToken));
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

    private async Task<PlanLoadResult> LoadWithBindingsAsync(
        PlanCompileRequest request, long order, PendingLoad load, CancellationToken cancellationToken)
    {
        var lease = load.Lease;

        // Model signatures that need a client loaded (or a model server asked) are awaited here, off the compile, and the
        // plan is compiled once more with the answers (design 6.9); plans whose clients are loaded compile once.
        var environment = Environment;
        var compilation = await lease.CompileAsync(bindings => PlanCompiler.Compile(request, environment, bindings), load.Cancellation.Token)
            .ConfigureAwait(false);
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(compilation.Diagnostics);
        var pipelineId = request.Pipeline.PipelineId;
        if (compilation.Plan is not { } plan)
        {
            PlanLoadTelemetry.CompileFailed(_logger, pipelineId, request.ManifestId, diagnostics);
            return new PlanLoadResult(pipelineId, false, request.ManifestId, TimeSpan.Zero, diagnostics.Items, compilation.Report);
        }

        // Readiness (design 6.6 local item 6): the plan is served only once its tables hold a complete load and its
        // instances have loaded. A disposal during the wait also stops them (their loads end as failures): the
        // cancellation decides, not those.
        var failures = await lease.WaitLoadedAsync(load.Cancellation.Token).ConfigureAwait(false);
        load.Cancellation.Token.ThrowIfCancellationRequested();
        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                diagnostics.Error(failure.Code, failure.Artifact, failure.Pointer, failure.Message, failure.Hint);
            }

            PlanLoadTelemetry.BindingsFailed(_logger, pipelineId, request.ManifestId, diagnostics);
            return new PlanLoadResult(pipelineId, false, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
        }

        var loaded = new LoadedPipeline
        {
            Plan = plan,
            FallbackPipeline = request.Pipeline.FallbackPipeline,
            Report = compilation.Report,
            Sequence = order,
            ArtifactIds = request.ArtifactIds,
            Bindings = lease,
            Layout = new PlanLayout(plan),
        };
        _ = loaded.Telemetry; // request metric tags are built with the plan, not on its first request
        Publishing?.Invoke();
        long? served = null;
        LoadedPipeline? replaced = null;
        bool disposed, cancelled;
        try
        {
            lock (_publishGate)
            {
                // Checked under the lock that disposal takes, right before the current plan changes: a disposed engine
                // never publishes again, and a cancelled load never replaces a plan.
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
        }
        finally
        {
            // Once swapped, nothing owns the replaced plan's lease but this load, whatever the telemetry above did (a
            // metric listener's callback may throw): its tables that the new plan does not share stop their sources, and
            // its instances go once its last request has ended. Requests still running on the old plan read the versions
            // they pinned.
            replaced?.Bindings.Dispose();
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

        PlanLoadTelemetry.LogApplied(_logger, plan, order);
        return new PlanLoadResult(pipelineId, true, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report);
    }

    /// <summary>
    /// Cancels the loads still running (they throw <see cref="ObjectDisposedException"/>) and retires the binding leases of
    /// those loads and of the served plans: the tables no other user holds stop their update sources, and the instances
    /// no other user holds are disposed once the requests still running (and the calls they abandoned) have ended. A
    /// binding host passed to the constructor keeps serving its other engines; one the engine created is disposed, which
    /// waits for those disposals within its shutdown timeout. Requests running meanwhile keep the versions they pinned
    /// and their clients; no request and no load is taken any more.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var leases = new List<BindingLease>();
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
                _ = load.Cancellation.CancelObservedAsync();
                leases.Add(load.Lease);
            }

            foreach (var (_, pipeline) in _pipelines)
            {
                if (pipeline.Bindings is { } served)
                {
                    leases.Add(served);
                }
            }
        }

        foreach (var lease in leases)
        {
            lease.Dispose(); // stopping a source or disposing an instance runs none of its code here (BindingHost)
        }

        if (_ownsBindings)
        {
            await Bindings.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A load between its start and its end (see the lifecycle remarks): its lease, and the cancellation a disposal uses.</summary>
    private sealed class PendingLoad(BindingLease lease, ObservedCancellationSource cancellation)
    {
        public BindingLease Lease { get; } = lease;

        /// <summary>Linked to the caller's token; cancelled on its own only by a disposal.</summary>
        public ObservedCancellationSource Cancellation { get; } = cancellation;

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
        IReadOnlyList<IgnoredArm> ignored = Array.Empty<IgnoredArm>();
        LoadedPipeline? fallback = null;
        try
        {
            // Entered (see Enter) until the finally below; each run joins the lease for as long as its calls run.
            pipeline = Enter(request.PipelineId, out var disposed);
            if (pipeline is null)
            {
                return disposed
                    ? throw new ObjectDisposedException(nameof(RecEngine), "The engine is disposed: it takes no request.")
                    : result = Rejected(request, RecErrorKind.UnknownPipeline, $"Unknown pipeline '{request.PipelineId}'.");
            }

            // The caller's mistakes first, against the plan this request runs (a host may have read the inputs with an
            // older one): nothing is asked of the assignment provider for a request that cannot run, and the fallback
            // pipeline, which stands in for a pipeline that failed, does not serve it.
            if (pipeline.Plan.RequestSchema.Check(request.Inputs) is { } invalid)
            {
                return result = Rejected(request, RecErrorKind.InvalidRequest, RequestSchema.Describe(invalid), pointer: invalid.Pointer);
            }

            // A debug option, like forced arms: the caller's mistake, without a pointer into the body.
            if (request.Debug?.ExplainIds is { } explainIds
                && ExplainTargets.Check(explainIds, pipeline.Plan.TableSchema.KeyType, request.PipelineId) is { } explainError)
            {
                return result = Rejected(request, RecErrorKind.InvalidRequest, explainError);
            }

            var assignment = await AssignAsync(pipeline, request, started, cancellationToken).ConfigureAwait(false);
            if (assignment.Error is { } assignmentError)
            {
                return result = Rejected(request, assignment.ErrorKind, assignmentError);
            }

            arms = assignment.Arms;
            ignored = assignment.Ignored;
            pipeline.Telemetry.RecordIgnored(assignment.Ignored);
            var remaining = request.Deadline - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                // The arms were decided and the ignored ids counted: the record says so, as the metric does (review 18 #3).
                return result = Rejected(request, RecErrorKind.DeadlineExceeded, "deadline exceeded before execution (assignment took the whole budget)",
                    new ExperimentRecord(pipeline.Plan.ArmIds(arms), Array.Empty<string>(), ignored), pipeline.Plan.ManifestId);
            }

            var executed = await Run(pipeline, request, request.Inputs, arms, remaining, seed, timeMs, runs, cancellationToken).ConfigureAwait(false);
            remaining = request.Deadline - Stopwatch.GetElapsedTime(started);

            // A disposal since the primary ran leaves the request with the primary's result, as when no fallback is served.
            if (executed.Succeeded || pipeline.FallbackPipeline is not { } fallbackId || fallbackId == request.PipelineId
                || remaining <= TimeSpan.Zero || (fallback = Enter(fallbackId, out _)) is null)
            {
                degraded = executed.Degraded;
                result = ToResult(request, pipeline, executed, ignored, request.PipelineId);
                return result = record ? Recorded(request, pipeline, seed!.Value, timeMs!.Value, result, executed, null) : result;
            }

            // The pipeline failed: serve its fallback pipeline, without experiments, within what is left (design 5.6), with
            // the inputs it declares (FallbackInputs).
            degraded = true;
            var served = await Run(fallback, request, FallbackInputs(request.Inputs, fallback.Plan.RequestSchema), RequestArms.None, remaining, seed, timeMs, runs,
                cancellationToken).ConfigureAwait(false);
            var converted = ToResult(request, fallback, served, ignored, fallbackId);
            result = Combine(request, executed, converted, served, ignored, fallbackId);
            return result = record ? Recorded(request, pipeline, seed!.Value, timeMs!.Value, result, executed, served) : result;
        }
        catch (Exception) when (runs is { Runs.Count: > 0 } && pipeline is not null)
        {
            // Aborted while (or after) a pipeline ran: the caller gets the exception, the recording callback the bundle of
            // what ran up to the abort. Nothing here waits for calls still in flight.
            Deliver(request, AbortedBundle(request, pipeline, seed!.Value, timeMs!.Value, runs.Runs, ignored));
            throw;
        }
        finally
        {
            pipeline?.Bindings.RequestEnded();
            fallback?.Bindings.RequestEnded();

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
    /// The request's arms, from the first of: forced arms (debug), the platform's answer the caller passed
    /// (<see cref="RecRequest.Assignment"/>), the assignment provider; otherwise none (the baseline). Returns an error
    /// instead of arms when the request must be rejected.
    /// </summary>
    private async ValueTask<(RequestArms Arms, IgnoredArm[] Ignored, string? Error, RecErrorKind ErrorKind)> AssignAsync(
        LoadedPipeline pipeline, RecRequest request, long started, CancellationToken cancellationToken)
    {
        if (request.Debug?.ForceArms is { } forced)
        {
            try
            {
                return (pipeline.Plan.ArmsFromIds(forced), Array.Empty<IgnoredArm>(), null, RecErrorKind.None);
            }
            catch (ArgumentException ex)
            {
                return (RequestArms.None, Array.Empty<IgnoredArm>(), ex.Message, RecErrorKind.InvalidRequest);
            }
        }

        if (request.Assignment is { } external)
        {
            var arms = ExternalArms.Resolve(pipeline.Plan.Layers, pipeline.Plan.Arms, external, out var ignored);
            return (arms, ignored, null, RecErrorKind.None);
        }

        if (_assignmentProvider is null)
        {
            return (RequestArms.None, Array.Empty<IgnoredArm>(), null, RecErrorKind.None);
        }

        // The provider shares the end-to-end deadline. It is called inline on the request's thread, as connectors and
        // models are (BoundedCall): what it does before it returns is not bounded. Its asynchronous part is: its token is
        // cancelled at the deadline, and the wait ends there even if it ignores the token (review 18 #2). The deadline
        // cancels it through ObservedCancellationSource, so the provider's callbacks never run on the timer's thread, where
        // one that throws would end the process (review 20 #1).
        var budget = request.Deadline - Stopwatch.GetElapsedTime(started);
        if (budget <= TimeSpan.Zero)
        {
            return (RequestArms.None, Array.Empty<IgnoredArm>(), "deadline exceeded before assignment", RecErrorKind.DeadlineExceeded);
        }

        const string Late = "deadline exceeded during assignment";
        var deadline = new ProviderDeadline(_logger, cancellationToken, request.PipelineId, request.RequestId);
        Task<IReadOnlyList<string>>? pending = null; // the provider, once it did not answer synchronously
        try
        {
            deadline.CancelAfterObserved(budget);
            var context = new AssignmentContext
            {
                PipelineId = request.PipelineId,
                RequestId = request.RequestId,
                Inputs = request.Inputs,
                Layers = pipeline.Layout.Layers,
                Experiments = pipeline.Layout.Experiments,
                Arms = pipeline.Layout.Arms,
            };
            ValueTask<IReadOnlyList<string>> answer;
            try
            {
                answer = _assignmentProvider.AssignAsync(context, deadline.Token);
            }
            catch (Exception ex)
            {
                answer = ValueTask.FromException<IReadOnlyList<string>>(ex);
            }

            IReadOnlyList<string> ids;
            try
            {
                if (!answer.IsCompleted)
                {
                    pending = answer.AsTask();
                    var left = request.Deadline - Stopwatch.GetElapsedTime(started);
                    var wait = pending.WaitAsync(left <= TimeSpan.Zero ? TimeSpan.Zero : left, AssignmentWaitTimers, cancellationToken);
                    if (!ReferenceEquals(wait, pending))
                    {
                        // A wait that was set up ends with whoever came first: the provider, the deadline or the caller.
                        // Its outcome decides, even when the provider has completed by now too: a wait that timed out an
                        // instant before the provider answered has lost the answer (review 20 #2).
                        ids = await wait.ConfigureAwait(false);
                        return Resolve(pipeline.Plan, ids);
                    }

                    // WaitAsync handed back the provider itself: it had completed before a wait could be set up (while its
                    // ValueTask became a task, say), whatever the time or the caller, so no wait decided. Judged below.
                    answer = new ValueTask<IReadOnlyList<string>>(pending);
                }

                // An answer the engine observes complete, with no wait to say who came first (a synchronous return, or one
                // before the wait was set up), is judged now, as BoundedCall judges a client: the caller's cancellation
                // first, then the deadline (passed by the clock, or by the timer that cancels the provider's token).
                if (cancellationToken.IsCancellationRequested || deadline.IsCancellationRequested || Stopwatch.GetElapsedTime(started) >= request.Deadline)
                {
                    Discard(answer);
                    cancellationToken.ThrowIfCancellationRequested();
                    return (RequestArms.None, Array.Empty<IgnoredArm>(), Late, RecErrorKind.DeadlineExceeded);
                }

                ids = answer.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return (RequestArms.None, Array.Empty<IgnoredArm>(), Late, RecErrorKind.DeadlineExceeded);
            }

            return Resolve(pipeline.Plan, ids);
        }
        finally
        {
            // A provider that has ended no longer needs its token; one still running keeps it until it ends.
            if (pending is { IsCompleted: false })
            {
                _ = AbandonProviderAsync(deadline, pending);
            }
            else
            {
                deadline.Dispose();
            }
        }

        static (RequestArms, IgnoredArm[], string?, RecErrorKind) Resolve(ExecutionPlan plan, IReadOnlyList<string> answered)
        {
            var assigned = ExternalArms.Resolve(plan.Layers, plan.Arms, answered, out var ignoredIds);
            return (assigned, ignoredIds, null, RecErrorKind.None);
        }

        // Consumes, once (as a source-backed ValueTask requires), an answer that came too late; what it holds is dropped.
        static void Discard(ValueTask<IReadOnlyList<string>> late)
        {
            try
            {
                _ = late.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Dropped with the answer: the request ended before it was observed.
            }
        }
    }

    /// <summary>
    /// The timers of the wait for a pending assignment provider; the system's. A test ends that wait before the timer that
    /// cancels the provider's token, as the wait can in production: both aim at the deadline, each rounded to whole
    /// milliseconds and started at a different moment.
    /// </summary>
    internal TimeProvider AssignmentWaitTimers { get; init; } = TimeProvider.System;

    /// <summary>
    /// The engine stopped waiting for a provider that is still running (the deadline, or the caller): its token is
    /// cancelled now and stays valid until the provider ends, as a client's token does in the shared waits (decisions,
    /// invariant 3). Disposing the source at once could leave the token never cancelled: the wait may end before the
    /// source's timer fires, and a caller's cancellation may end the request, inline in Cancel, before the registration
    /// that cancels the linked token has run, and disposing removes it. The callbacks run on the pool, so one that blocks
    /// does not hold the request; one that throws is counted and logged by the source (<see cref="ProviderDeadline"/>),
    /// which no logger can make throw. A provider that never ends keeps its source. Whatever the cancellation does, the
    /// source is disposed once the provider has ended, and not before (review 20 #3).
    /// </summary>
    private static async Task AbandonProviderAsync(ProviderDeadline deadline, Task provider)
    {
        try
        {
            await deadline.CancelObservedAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            await provider.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _ = provider.Exception; // a late failure nobody awaits any more
            deadline.Dispose();
        }
    }

    /// <summary>
    /// The assignment provider's token: cancelled at the request's deadline, with the caller's token, or when the engine
    /// stops waiting. In every case the provider's callbacks run on the pool, and what they throw is logged (1102).
    /// </summary>
    private sealed class ProviderDeadline(ILogger logger, CancellationToken caller, string pipelineId, string requestId)
        : ObservedCancellationSource(CancellationSite.Assignment, caller)
    {
        protected override void CallbacksFailed(AggregateException errors) =>
            LogProviderCancellationCallbacksFailed(logger, errors, pipelineId, requestId);
    }

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning,
        Message = "A cancellation callback of the assignment provider's token threw (pipeline '{PipelineId}', request '{RequestId}'); the request is not affected")]
    private static partial void LogProviderCancellationCallbacksFailed(ILogger logger, Exception exception, string pipelineId, string requestId);

    /// <summary>
    /// The plan a pipeline serves, its binding lease entered (see the lifecycle remarks): the caller ends the entry
    /// (<see cref="BindingLease.RequestEnded"/>) when it is done with the plan; the plan's runs join the lease meanwhile.
    /// Null when the pipeline is not served, or when the engine is disposed (<paramref name="disposed"/>). A plan replaced
    /// between the read and the entry is read again.
    /// </summary>
    private LoadedPipeline? Enter(string pipelineId, out bool disposed)
    {
        disposed = false;
        while (_pipelines.TryGetValue(pipelineId, out var pipeline))
        {
            if (pipeline.Bindings.TryEnter())
            {
                return pipeline;
            }

            // Retired: replaced since the read (the next read finds the new plan), or, while still served, by a disposal.
            if (_pipelines.TryGetValue(pipelineId, out var current) && ReferenceEquals(current, pipeline))
            {
                disposed = true;
                return null;
            }
        }

        disposed = Volatile.Read(ref _disposed); // a disposed engine takes no request, also for a pipeline it never served
        return null;
    }

    /// <param name="pipeline">Entered by the caller (<see cref="Enter"/>); the run joins its lease until its calls end.</param>
    /// <param name="runs">Collects the run's recording on every exit; null when the request does not record.</param>
    private ValueTask<PipelineResult> Run(
        LoadedPipeline pipeline, RecRequest request, RequestInputs inputs, RequestArms arms, TimeSpan deadline, long? seed, long? timeMs, RunCapture? runs,
        CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(pipeline.Plan, new PipelineRequest
        {
            RequestId = request.RequestId,
            Inputs = inputs,
            Arms = arms,
            Deadline = deadline,
            Seed = seed,
            TimeMs = timeMs,
            CaptureLogs = request.Debug?.CaptureLogs == true,
            ExplainIds = request.Debug?.ExplainIds,
            OnRecording = runs is null ? null : runs.Add,
            OnEnd = pipeline.Bindings.Join(), // last: the executor, called next, ends it on every outcome
        }, cancellationToken);

    /// <summary>
    /// The inputs of a request that falls back (design 5.6): those the fallback pipeline declares. The caller wrote the
    /// request for the pipeline it named, and its inputs passed that pipeline's schema; a fallback typically uses fewer
    /// signals (a popularity list needs no user), and need not declare inputs it does not read. What it declares it checks
    /// as any pipeline does: an input it requires that the request lacks, or of another type, fails the fallback, and the
    /// request with <see cref="RecErrorKind.Failed"/> (the configuration's mistake, not the caller's). Allocates only when
    /// the request has an input the fallback does not declare.
    /// </summary>
    internal static RequestInputs FallbackInputs(RequestInputs inputs, RequestSchema fallback)
    {
        var undeclared = 0;
        foreach (var (name, _) in inputs)
        {
            undeclared += fallback.Contains(name) ? 0 : 1;
        }

        if (undeclared == 0)
        {
            return inputs;
        }

        var declared = new RequestInputs(inputs.Count - undeclared);
        foreach (var (name, value) in inputs)
        {
            if (fallback.Contains(name))
            {
                declared.Set(name, value);
            }
        }

        return declared;
    }

    /// <summary>The result of a request its fallback pipeline served: the primary's assignment and error, the fallback's items.</summary>
    private static RecResult Combine(
        RecRequest request, PipelineResult executed, RecResult converted, PipelineResult served, IReadOnlyList<IgnoredArm> ignored, string fallbackId) => new()
    {
        RequestId = request.RequestId,
        Succeeded = converted.Succeeded,
        ErrorKind = converted.Succeeded ? RecErrorKind.None : RecErrorKind.Failed,
        Error = $"pipeline '{request.PipelineId}' failed: {executed.Error}" + (served.Succeeded ? string.Empty : $"; fallback '{fallbackId}' failed: {served.Error}"),
        Items = converted.Items,
        Experiments = new ExperimentRecord(executed.AssignedArms, executed.TriggeredExperiments, ignored),
        ManifestId = converted.ManifestId,
        ServedBy = fallbackId,
        ColumnProducers = converted.ColumnProducers,
        Trace = converted.Trace,
        LogBatches = converted.LogBatches,
        Explanation = converted.Explanation, // of the run that served
        ElapsedMs = executed.ElapsedMs + served.ElapsedMs,
    };

    private static RecResult ToResult(RecRequest request, LoadedPipeline pipeline, PipelineResult result, IReadOnlyList<IgnoredArm> ignored, string servedBy) => new()
    {
        RequestId = request.RequestId,
        Succeeded = result.Succeeded,
        ErrorKind = result.Succeeded ? RecErrorKind.None : RecErrorKind.Failed,
        Error = result.Error,
        Items = result.Items,
        Experiments = new ExperimentRecord(result.AssignedArms, result.TriggeredExperiments, ignored),
        ManifestId = pipeline.Plan.ManifestId,
        ServedBy = servedBy,
        ColumnProducers = result.ColumnProducers,
        Trace = request.Debug?.Trace == true ? result.Outcomes : null,
        LogBatches = request.Debug?.CaptureLogs == true ? result.LogBatches : null,
        Explanation = result.Explanation,
        ElapsedMs = result.ElapsedMs,
    };

    /// <summary>A request that did not run: before assignment nothing is known about it; after, its arms and release are.</summary>
    private static RecResult Rejected(
        RecRequest request, RecErrorKind kind, string error, ExperimentRecord? experiments = null, string? manifestId = null, string? pointer = null) => new()
    {
        RequestId = request.RequestId,
        Succeeded = false,
        ErrorKind = kind,
        Error = error,
        ErrorPointer = pointer,
        Items = Array.Empty<RankedItem>(),
        Experiments = experiments ?? new ExperimentRecord(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IgnoredArm>()),
        ManifestId = manifestId ?? string.Empty,
        ServedBy = request.PipelineId,
    };
}
