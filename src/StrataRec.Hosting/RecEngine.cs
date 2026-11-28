using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
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
using StrataRec.Hosting.Plans;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>
/// What a host calls: <see cref="ExecuteAsync"/>, and two read-only questions a transport needs before it can build a
/// request. Transport, authentication and threading belong to the host.
/// </summary>
public interface IRecEngine
{
    /// <summary>
    /// Runs a request on the plan its pipeline serves now. Failures are results, not exceptions:
    /// <see cref="RecResult.ErrorKind"/> says whose they are. Throws only when the caller cancels (an
    /// <see cref="OperationCanceledException"/> carrying <paramref name="cancellationToken"/>), when the engine is
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
    /// The arms the experimentation platform assigned to the request, as <c>experiment/arm</c> ids: the
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

    /// <summary>
    /// The requests this one is sent with for one thing (<see cref="RequestGroup"/>): their cached reads are shared as
    /// those of one request's runs are. Null for a request on its own.
    /// </summary>
    public RequestGroup? Group { get; init; }
}

/// <summary>
/// Requests a host sends at the same time for one thing: the feed and the promoted row of one page visit, each a
/// request of its own pipeline. Make one for the visit and give it to each of its requests
/// (<see cref="RecRequest.Group"/>).
/// </summary>
/// <remarks>
/// <para>What is shared, and how, is what the runs of one request share: a read through a declared cache that no tier
/// answered joins the group's read of the same record, the first asks the origin, and the others take what it gets
/// when it is fresh by their own times. A failure is not shared. A result of a pipeline call that another request of
/// the group computed says so (<see cref="CalledRun.Cached"/> names that request).</para>
/// <para>A record is the group's while the request that read it is under way, and no longer, however many others took
/// it: requests sent one after another share nothing through the group, and nothing needs to be released, whatever
/// becomes of the object. Each request keeps its own deadline, inputs, arms and result.</para>
/// <para>A request that is not served as any other is (<see cref="DebugOptions.ForceArms"/>, a
/// <see cref="DebugOptions.ConfigOverride"/>, <see cref="DebugOptions.CaptureLogs"/>) writes no logs: it runs on its
/// own, as if it had no group.</para>
/// </remarks>
public sealed class RequestGroup
{
    internal RequestReads.Group Reads { get; } = new();
}

/// <summary>Debugging controls, authorized by the host.</summary>
/// <remarks>
/// <para>Which requests write the persistent logs (the log sink's taps and the <c>$experiments</c> record): a request
/// that changes what is served, with <see cref="ForceArms"/> (an empty list, the baseline, included) or a
/// <see cref="ConfigOverride"/>, writes none, also when its fallback pipeline serves it. Nor does a request that
/// <see cref="CaptureLogs"/>: its batches are returned instead. Options that only look at a request,
/// <see cref="Trace"/>, <see cref="ExplainIds"/>, <see cref="Record"/> and <see cref="OnRecording"/>, leave its logging
/// as it is.</para>
/// </remarks>
public sealed class DebugOptions
{
    /// <summary>
    /// Arms to force (<c>experiment/arm</c>), overriding assignment; an empty list forces the baseline. Strict, unlike
    /// the platform's arms: an unknown arm or two arms of one layer fail the request. An arm whose experiment yields to
    /// the experiment of another forced arm (a release's <c>yields_to</c>) does not run and is recorded as yielded, as
    /// for a request the platform assigned both. The request writes no persistent logs (see remarks).
    /// </summary>
    public IReadOnlyList<string>? ForceArms { get; init; }

    /// <summary>
    /// A configuration to try on this request alone: an RFC 7396 merge patch of the pipeline, in the shape an
    /// experiment arm patches (<c>params</c>, <c>nodes</c>, <c>log_taps</c>, <c>columns</c>, <c>param_schema</c>,
    /// <c>funnel_groups</c>, <c>features</c>, <c>derived_features</c>), applied after the request's arms, so it wins
    /// over them. The plan is compiled for the request and counts against the deadline; one that does not compile, or
    /// whose new bindings fail to load, rejects the request as invalid, naming the first error. Nothing of it outlives
    /// the request.
    /// </summary>
    /// <remarks>
    /// The request writes no persistent logs (its batches are returned when <see cref="CaptureLogs"/> is set). Its
    /// record lists the arms applied and no triggered experiment. A recording carries the override, and replaying it
    /// applies the override again. The fallback pipeline, when it serves, runs as configured. The engine reads the
    /// object once, when it checks it: a CLR value wrapped in it
    /// (<see cref="JsonValue.Create{T}(T, JsonNodeOptions?)"/>) is written then, and the JSON it wrote is what is
    /// compiled and recorded; what the host does with the object afterwards changes neither.
    /// </remarks>
    public JsonObject? ConfigOverride { get; init; }

    /// <summary>Include per-node outcomes in <see cref="RecResult.Trace"/>.</summary>
    public bool Trace { get; init; }

    /// <summary>
    /// Return log batches in <see cref="RecResult.LogBatches"/> instead of writing them; a request that
    /// forces arms or a configuration writes none either way, and returns them only with this set.
    /// </summary>
    public bool CaptureLogs { get; init; }

    /// <summary>
    /// Record the request as a replay bundle in <see cref="RecResult.Recording"/>: inputs, assignment, every
    /// external response with its status and duration, every node decision, the sampling and the result. Costs nothing
    /// when off; when on, the recording allocates in proportion to the data the request fetched.
    /// </summary>
    public bool Record { get; init; }

    /// <summary>
    /// Receives the replay bundle of the request on every exit once a pipeline ran: with the result, and
    /// before the exception propagates when the request is aborted (cancelled by the caller, or a defect), which
    /// <see cref="RecResult.Recording"/> cannot carry. Setting it records the request. An exception it throws is logged
    /// and does not change the request's outcome.
    /// </summary>
    /// <remarks>
    /// An aborted request's bundle is a snapshot taken at the abort: calls still in flight are <c>outstanding</c>, and
    /// what they return afterwards is not in it. Nothing waits for them, so the cancellation reaches the caller as
    /// promptly as without recording. It is for inspection: it is not replayed
    /// (<see cref="Replay.ReplayOutcome.NotReplayed"/>). A request rejected or aborted before any pipeline ran has no
    /// bundle.
    /// </remarks>
    public Action<ReplayBundle>? OnRecording { get; init; }

    /// <summary>
    /// Candidates to explain, as entity ids of the pipeline's key type: <see cref="RecResult.Explanation"/> then says,
    /// for each, where it ended (returned, past the limit, dropped by which node, never recalled) and its state after
    /// every node of the run that served: live or not, its rank, the values of the columns the node read and wrote, and
    /// the operator's reason where it gives one. At most <see cref="MaxExplainIds"/>; ids of the other key type or more
    /// ids than that fail the request with <see cref="RecErrorKind.InvalidRequest"/> before anything runs. Costs
    /// nothing when null. When set, the recording grows with ids × nodes.
    /// </summary>
    public IReadOnlyList<EntityKey>? ExplainIds { get; init; }

    public const int MaxExplainIds = ExplainTargets.MaxCount;
}

/// <summary>One run of a pipeline a node of another called, as a request's trace shows it (<see cref="RecResult.Calls"/>).</summary>
/// <param name="CalledBy">
/// The calls from the request's pipeline down to this run, as its logs say them (<c>LogBatch.CalledBy</c>):
/// <c>home/blend#0</c>, or <c>home/blend#1 &gt; inner/blend#0</c> for a run a called run called.
/// </param>
/// <param name="Caller">The pipeline whose node called.</param>
/// <param name="Node">The node that called.</param>
/// <param name="Sequence">The call's place among the calls the node made in its run, from 0.</param>
/// <param name="PipelineId">The pipeline called.</param>
/// <param name="Error">Why the call failed: the pipeline is not served, does not fit the call, rejected its inputs, failed, or had no time.</param>
/// <param name="Arms">The arms the called run ran; empty when it did not run.</param>
/// <param name="Trace">The called run's node outcomes; empty when it did not run.</param>
public sealed record CalledRun(
    string CalledBy, string Caller, string Node, int Sequence, string PipelineId, bool Succeeded, string? Error, double ElapsedMs, IReadOnlyList<string> Arms,
    IReadOnlyList<NodeOutcome> Trace)
{
    /// <summary>
    /// For a call the node's cache answered: the request whose run computed the result. The pipeline did not run for this
    /// call, so <see cref="Trace"/> is empty and that request has its nodes; a stale one was served after the run here
    /// failed, and <see cref="Trace"/> is that run's.
    /// </summary>
    public Pipeline.Operators.CachedFrom? Cached { get; init; }
}

/// <summary>Assignment and trigger record of a request.</summary>
/// <remarks>
/// A request rejected before its arms are decided (unknown pipeline, inputs the request schema rejects, invalid forced
/// arms, a provider past the deadline) has an empty record. One rejected after (out of budget before execution) carries
/// its assigned and ignored arms, which the metrics already counted, and no triggered experiment.
/// </remarks>
/// <param name="Assigned">Assigned arms (<c>experiment/arm</c>) for intent-to-treat analysis.</param>
/// <param name="Triggered">Experiments whose site the request reached, for triggered analysis.</param>
/// <param name="Ignored">Arm ids the platform sent that the request did not run with, and why
/// (<see cref="ExternalArms"/>), sorted by id; empty when every id applied. A request that ignores arms is still
/// served: the platform's view of its exposure and this record differ.</param>
public sealed record ExperimentRecord(IReadOnlyList<string> Assigned, IReadOnlyList<string> Triggered, IReadOnlyList<IgnoredArm> Ignored);

/// <summary>Write it as JSON with <see cref="RecResultJson"/>.</summary>
public sealed class RecResult
{
    /// <summary>The request's id (<see cref="RecRequest.RequestId"/>).</summary>
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
    /// of the shape <c>{ "inputs": { … } }</c>, e.g. <c>/inputs/user_id</c>; for a mistake in the request's configuration
    /// override, its pointer in a request file, e.g. <c>/config_override/params/k</c>: one in the override itself, or one
    /// the compile finds at a value the override sets or within an object it patches (one elsewhere in the configuration
    /// the override produced is in <see cref="Error"/>, at the pipeline's or catalog's pointer); null otherwise.
    /// </summary>
    public string? ErrorPointer { get; init; }

    /// <summary>
    /// The returned items column by column: the form to read when a request returns many rows to process further.
    /// </summary>
    public required ResultColumns Columns { get; init; }

    /// <summary>The returned items as rows, each with its output columns by name; made from <see cref="Columns"/> when first read.</summary>
    public IReadOnlyList<RankedItem> Items => Columns.Rows;

    /// <summary>
    /// The output's request-level values (<c>output.values</c>), in the output's order, each with its declared type: what
    /// the request's nodes computed for the caller once (a count of the candidates, a decision a node made), beside the
    /// items. Empty when the pipeline returns none, and for a failed request.
    /// </summary>
    public ResultValues Values { get; init; } = ResultValues.Empty;

    public required ExperimentRecord Experiments { get; init; }

    /// <summary>Content id of the manifest the plan was built from.</summary>
    public required string ManifestId { get; init; }

    /// <summary>The pipeline that served the result: the requested one or its <c>fallback_pipeline</c>.</summary>
    public required string ServedBy { get; init; }

    /// <summary>Output column → producing node (lineage).</summary>
    public IReadOnlyDictionary<string, string>? ColumnProducers { get; init; }

    public IReadOnlyList<NodeOutcome>? Trace { get; init; }

    /// <summary>
    /// With <see cref="DebugOptions.Trace"/>, the runs of the pipelines the request's nodes called
    /// (<c>OperatorContext.CallPipelineAsync</c>), theirs included, each with its own trace: by calling pipeline, node and
    /// the call's place among the node's calls. Null without a trace or without a call.
    /// </summary>
    public IReadOnlyList<CalledRun>? Calls { get; init; }

    /// <summary>
    /// Captured log batches when <see cref="DebugOptions.CaptureLogs"/> was set: of every run of the request in run order,
    /// the primary pipeline's and then, when it served, the fallback pipeline's, each naming its pipeline and manifest.
    /// </summary>
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

    internal BindingLease Bindings { get; init; } = null!;

    internal PlanLayout Layout { get; init; } = null!;

    internal PlanSource Source { get; init; } = null!;

    internal CallIdentity CallIdentity { get; init; } = null!;

    /// <summary>
    /// The configuration override the plan of a request was compiled with, as the compile read it
    /// (<see cref="PlanCompilation.ConfigOverride"/>), which the request's recording holds; null for a plan the engine serves.
    /// </summary>
    internal JsonObject? ConfigOverride { get; init; }

    internal RequestTelemetry Telemetry =>
        _telemetry ?? Interlocked.CompareExchange(ref _telemetry, RequestTelemetry.Create(Plan), null) ?? _telemetry!;

    private RequestTelemetry? _telemetry;
}

/// <summary>
/// A plan's layers, experiments and arms as the read-only lists of <see cref="AssignmentContext"/>, boxed once per plan
/// so that asking the assignment provider allocates nothing.
/// </summary>
internal sealed class PlanLayout(ExecutionPlan plan)
{
    public IReadOnlyList<LayerInfo> Layers { get; } = plan.Layers;

    public IReadOnlyList<ExperimentInfo> Experiments { get; } = plan.Experiments;

    public IReadOnlyList<ArmInfo> Arms { get; } = plan.Arms;
}

public sealed record PlanLoadResult(
    string PipelineId,
    bool Applied,
    string ManifestId,
    TimeSpan CompileTime,
    IReadOnlyList<ConfigDiagnostic> Diagnostics,
    PlanReport Report)
{
    /// <summary>
    /// The plan compiled and loaded, and was not applied because its pipeline serves an equal or higher sequence by
    /// now: not a failure, and nothing to retry.
    /// </summary>
    public bool Stale { get; init; }
}

/// <summary>
/// In-flight requests keep the plan they started with; a plan that fails to compile never replaces the current one.
/// </summary>
/// <remarks>
/// <para>What plans bind belongs to <see cref="Bindings"/>: local feature tables, service clients and connectors. A
/// plan is served only once every table it reads has completed its first load and every instance it uses has loaded;
/// what a new plan shares by identity with the served one is taken over, not loaded again.</para>
/// <para>The instances of a replaced plan are disposed once its last request, and every call those requests started,
/// has ended. After disposal a request throws <see cref="ObjectDisposedException"/>.</para>
/// <para>Each request gets one span (<see cref="StrataRecTracing.RequestSpanName"/>) and request metrics per pipeline
/// and arm; without listeners or a logger factory the engine records nothing and allocates nothing for it.</para>
/// </remarks>
public sealed partial class RecEngine : IRecEngine, IAsyncDisposable, IDisposable
{
    private const string CallerCancelled = "The request was cancelled by its caller.";

    private readonly ConcurrentDictionary<string, LoadedPipeline> _pipelines = new(StringComparer.Ordinal);
    private readonly object _publishGate = new(); // the lifecycle lock: _loads, _disposed and every change of _pipelines
    private readonly HashSet<PendingLoad> _loads = new();
    private long _loadTicket;
    private readonly PipelineExecutor _executor;
    private readonly IAssignmentProvider? _assignmentProvider;
    private readonly ILogger _logger;
    private readonly bool _ownsBindings;
    private bool _disposed;

    /// <param name="logs">
    /// Where served requests' tap batches and trigger records go; null writes no persistent logs. Requests that
    /// capture their logs, try a configuration override or replay a bundle never write to it. The caller owns it and
    /// disposes it after the engine, draining its durable batches;
    /// <see cref="ServiceCollectionExtensions.AddStrataRec"/> leaves that to the container.
    /// </param>
    /// <param name="loggerFactory">Receives plan load events, and windowed summaries of request-level failures and of
    /// dependency health changes; null logs nothing.</param>
    /// <param name="bindings">Hosts what the plans bind (tables, clients, connectors); null creates one the engine owns and
    /// disposes. A host passed in is the caller's to dispose (it may serve several engines), with its own options.</param>
    /// <param name="bindingOptions">Options of the host the engine creates: its secret store, load and
    /// shutdown bounds.</param>
    public RecEngine(
        PlanEnvironment? environment = null,
        IAssignmentProvider? assignmentProvider = null,
        Logging.LogPipeline? logs = null,
        ILoggerFactory? loggerFactory = null,
        BindingHost? bindings = null,
        BindingHostOptions? bindingOptions = null)
        : this(environment, new PipelineExecutor(logs: logs, loggerFactory: loggerFactory), assignmentProvider, loggerFactory, bindings, bindingOptions)
    {
    }

    internal RecEngine(
        PlanEnvironment? environment,
        PipelineExecutor executor,
        IAssignmentProvider? assignmentProvider = null,
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
        _executor = executor;
        _logger = loggerFactory?.CreateLogger<RecEngine>() ?? NullLogger<RecEngine>.Instance;
        _ownsBindings = bindings is null;
        Bindings = bindings ?? new BindingHost(bindingOptions, loggerFactory);
    }

    /// <summary>Operators, connectors and clients this binary provides.</summary>
    public PlanEnvironment Environment { get; }

    /// <summary>What the served plans bind: local feature tables, service clients and connectors.</summary>
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
    /// (<see cref="LoadAsync"/>). Blocks while new tables and instances load; call it from a background thread.
    /// </summary>
    public PlanLoadResult Load(PlanCompileRequest request, long? sequence = null) =>
        LoadAsync(request, sequence).GetAwaiter().GetResult();

    /// <summary>
    /// A table that fails to load rejects the plan with <c>LOCAL_TABLE_LOAD_FAILED</c> naming the binding and the bad
    /// file, an instance with <c>BINDING_LOAD_FAILED</c> at its binding.
    /// </summary>
    /// <param name="sequence">A plan never replaces one with an equal or higher sequence
    /// (<see cref="PlanLoadResult.Stale"/>). When omitted, a ticket taken at the start of the call orders local loads;
    /// do not mix the two schemes for one pipeline.</param>
    /// <param name="cancellationToken">Cancels the load until the plan is applied.</param>
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
        catch (Exception ex) when (DisposedDuring(load, cancellationToken))
        {
            throw Disposed(ex);
        }
        finally
        {
            EndLoad(load);
        }
    }

    private static bool DisposedDuring(PendingLoad load, CancellationToken cancellationToken) =>
        load.Cancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

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

    /// <summary>Every load ends here: a plan that was not published returns its lease; a published one's lease belongs
    /// to the served plan.</summary>
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

        // A disposal during the wait also stops them (their loads end as failures): the cancellation decides, not
        // those.
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
            ArtifactIds = request.ArtifactIds.ToFrozenDictionary(StringComparer.Ordinal),
            Bindings = lease,
            Layout = new PlanLayout(plan),
            Source = PlanSource.Capture(request),
            CallIdentity = CallIdentity.Of(request, plan),
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
                        load.Published = true;
                        lease.Serve();
                        PlanLoadTelemetry.Applied(plan, order); // gauges stay ordered with the swaps
                    }
                }
            }
        }
        finally
        {
            // Once swapped, nothing owns the replaced plan's lease but this load, whatever happened after the swap (the
            // telemetry does not throw, but the lease must not depend on it)
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
            return new PlanLoadResult(pipelineId, false, request.ManifestId, plan.CompileTime, diagnostics.Items, compilation.Report) { Stale = true };
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
            lease.Dispose();
        }

        if (_ownsBindings)
        {
            await Bindings.DisposeAsync().ConfigureAwait(false);
        }
    }

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

        var activity = RequestTelemetry.StartRequest(request.PipelineId, request.RequestId);
        LoadedPipeline? pipeline = null;
        var arms = RequestArms.None;
        var degraded = false;
        RecResult? result = null; // stays null when the request is aborted (cancelled by the caller, or a defect)

        // A request's time is taken once, when it arrives: the pipeline, its fallback pipeline and the pipelines its
        // nodes call see the same, and so does a recorded request, whose bundle holds exactly what ran.
        var record = request.Debug is { } debug && (debug.Record || debug.OnRecording is not null);
        var seed = record ? request.Seed ?? PipelineRequest.DefaultSeed(request.RequestId) : request.Seed;
        long? timeMs = request.TimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var runs = record ? new RunCapture() : null;
        IReadOnlyList<IgnoredArm> ignored = Array.Empty<IgnoredArm>();
        LoadedPipeline? fallback = null;
        RequestCalls? calls = null;

        var reads = request.Group is { } group && WritesLogs(request) ? new RequestReads(group.Reads) : new RequestReads();
        LoadedPipeline? overridden = null; // the plan compiled for a configuration override, entered until the finally below
        IReadOnlyList<string>? merged = null; // the arms such a plan has merged in: it runs with none of its own
        try
        {
            pipeline = Enter(request.PipelineId, out var disposed);
            if (pipeline is null)
            {
                return disposed
                    ? throw new ObjectDisposedException(nameof(RecEngine), "The engine is disposed: it takes no request.")
                    : result = Rejected(request, RecErrorKind.UnknownPipeline, $"Unknown pipeline '{request.PipelineId}'.");
            }

            // A caller that cancelled already is not served: counted as an aborted request of its pipeline, as one
            // cancelled while it runs is.
            cancellationToken.ThrowIfCancellationRequested();

            if (pipeline.Plan.RequestSchema.Check(request.Inputs) is { } invalid)
            {
                return result = Rejected(request, RecErrorKind.InvalidRequest, RequestSchema.Describe(invalid), pointer: invalid.Pointer);
            }

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

            arms = ExternalArms.Yield(pipeline.Plan, assignment.Arms, assignment.Ignored, out var ignoredOrYielded);
            ignored = ignoredOrYielded;
            pipeline.Telemetry.RecordIgnored(ignoredOrYielded);
            var remaining = request.Deadline - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return result = Rejected(request, RecErrorKind.DeadlineExceeded, "deadline exceeded before execution (assignment took the whole budget)",
                    new ExperimentRecord(pipeline.Plan.ArmIds(arms), Array.Empty<string>(), ignored), pipeline.Plan.ManifestId);
            }

            var running = pipeline;
            var runArms = arms;
            if (request.Debug?.ConfigOverride is { } configOverride)
            {
                merged = pipeline.Plan.ArmIds(arms);
                var compiled = await CompileOverrideAsync(pipeline, configOverride, merged, remaining, cancellationToken).ConfigureAwait(false);
                if (compiled.Pipeline is null)
                {
                    return result = Rejected(request, compiled.ErrorKind, compiled.Error!, new ExperimentRecord(merged, Array.Empty<string>(), ignored),
                        pipeline.Plan.ManifestId, compiled.Pointer);
                }

                running = overridden = compiled.Pipeline;
                runArms = RequestArms.None;
                remaining = request.Deadline - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return result = Rejected(request, RecErrorKind.DeadlineExceeded, "deadline exceeded before execution (compiling the configuration override took the whole budget)",
                        new ExperimentRecord(merged, Array.Empty<string>(), ignored), pipeline.Plan.ManifestId);
                }
            }

            var executed = await Run(running, request, request.Inputs, runArms, remaining, seed, timeMs, runs, reads, ref calls, cancellationToken).ConfigureAwait(false);
            remaining = request.Deadline - Stopwatch.GetElapsedTime(started);

            // A disposal since the primary ran leaves the request with the primary's result, as when no fallback is served.
            if (executed.Succeeded || running.FallbackPipeline is not { } fallbackId || fallbackId == request.PipelineId
                || remaining <= TimeSpan.Zero || (fallback = Enter(fallbackId, out _)) is null)
            {
                degraded = executed.Degraded;
                result = ToResult(request, running, executed, ignored, request.PipelineId, merged);
                result = record ? Recorded(request, pipeline, seed!.Value, timeMs!.Value, result, executed, null, overridden?.ConfigOverride) : result;
                return result = calls?.Merge(result) ?? result; // after the bundle, which holds the request's own pipeline's
            }

            degraded = true;
            var served = await Run(fallback, request, FallbackInputs(request.Inputs, fallback.Plan.RequestSchema), RequestArms.None, remaining, seed, timeMs, runs,
                reads, ref calls, cancellationToken).ConfigureAwait(false);
            var converted = ToResult(request, fallback, served, ignored, fallbackId);
            result = Combine(request, executed, converted, served, ignored, fallbackId, merged);
            result = record ? Recorded(request, pipeline, seed!.Value, timeMs!.Value, result, executed, served, overridden?.ConfigOverride) : result;
            return result = calls?.Merge(result) ?? result;
        }
        catch (Exception ex)
        {
            if (runs is { Runs.Count: > 0 } && pipeline is not null)
            {
                Deliver(request, AbortedBundle(request, pipeline, seed!.Value, timeMs!.Value, runs.Runs, ignored, merged, overridden?.ConfigOverride));
            }

            // The caller's cancellation is thrown as the caller's. Its message is not read: an extension's exception
            // may throw from it.
            if (ex is OperationCanceledException cancelled && cancellationToken.IsCancellationRequested && cancelled.CancellationToken != cancellationToken)
            {
                throw new OperationCanceledException(CallerCancelled, cancelled, cancellationToken);
            }

            throw;
        }
        finally
        {
            reads.End();
            pipeline?.Bindings.RequestEnded();
            fallback?.Bindings.RequestEnded();
            EndOverride(overridden);

            // What the caller saw: an aborted request counts as an error, so arms whose requests
            // are slow enough to be cancelled do not look healthier.
            var failed = result is not { Succeeded: true };
            var empty = !failed && result!.Columns.Count == 0;
            (pipeline?.Telemetry ?? RequestTelemetry.Unknown).Record(Stopwatch.GetElapsedTime(started).TotalSeconds, arms, failed, empty, degraded && !failed);
            if (activity is not null)
            {
                RequestTelemetry.EndSpan(activity, result, degraded && !failed, pipeline?.Plan, arms);
            }
        }
    }

    private async ValueTask<(RequestArms Arms, IgnoredArm[] Ignored, string? Error, RecErrorKind ErrorKind)> AssignAsync(
        LoadedPipeline pipeline, RecRequest request, long started, CancellationToken cancellationToken)
    {
        if (request.Debug?.ForceArms is { } forced)
        {
            try
            {
                CheckForcedOfCalled(pipeline.Plan, forced);
                return (pipeline.Plan.ArmsFromIds(OwnForced(pipeline.Plan, forced)), Array.Empty<IgnoredArm>(), null, RecErrorKind.None);
            }
            catch (ArgumentException ex)
            {
                return (RequestArms.None, Array.Empty<IgnoredArm>(), ex.Message, RecErrorKind.InvalidRequest);
            }
        }

        if (request.Assignment is { } external)
        {
            var arms = ExternalArms.Resolve(pipeline.Plan.Layers.AsSpan(), pipeline.Plan.Arms.AsSpan(), external, out var ignored);
            return (arms, WithoutCalled(pipeline.Plan, ignored), null, RecErrorKind.None);
        }

        var asked = await AskProviderAsync(pipeline, request, CallBound.DeadlineAfter(started, request.Deadline), cancellationToken).ConfigureAwait(false);
        return (asked.Arms, WithoutCalled(pipeline.Plan, asked.Ignored), asked.Error, asked.ErrorKind);
    }

    /// <summary>
    /// The forced arms that are <paramref name="plan"/>'s to run: all but those it does not have and a pipeline it calls
    /// has, which that pipeline's run takes (<see cref="AssignCallAsync"/>). An arm no pipeline has stays, and is refused
    /// as unknown.
    /// </summary>
    private IReadOnlyList<string> OwnForced(ExecutionPlan plan, IReadOnlyList<string> forced)
    {
        if (!Calls(plan, out _))
        {
            return forced;
        }

        List<string>? own = null;
        for (var i = 0; i < forced.Count; i++)
        {
            if (!plan.TryGetArm(forced[i], out _) && KnownToCalled(plan, forced[i]))
            {
                if (own is null)
                {
                    own = new List<string>(forced.Count);
                    for (var j = 0; j < i; j++)
                    {
                        own.Add(forced[j]);
                    }
                }
            }
            else
            {
                own?.Add(forced[i]);
            }
        }

        return own ?? forced;
    }

    /// <param name="called">The pipeline is one a node calls: what the provider throws fails the call, as a result, and is
    /// not thrown through the calling operator (the request's own provider failure is the host's to see).</param>
    private async ValueTask<(RequestArms Arms, IgnoredArm[] Ignored, string? Error, RecErrorKind ErrorKind)> AskProviderAsync(
        LoadedPipeline pipeline, RecRequest request, long deadline, CancellationToken cancellationToken, bool called = false)
    {
        if (_assignmentProvider is null)
        {
            return (RequestArms.None, Array.Empty<IgnoredArm>(), null, RecErrorKind.None);
        }

        var bound = new CallBound(CancellationSite.Assignment, cancellationToken, deadline, _executor.Events, request.RequestId);
        CallResult<IReadOnlyList<string>> asked;
        try
        {
            var context = new AssignmentContext
            {
                PipelineId = pipeline.Plan.PipelineId,
                RequestId = request.RequestId,
                Inputs = request.Inputs,
                Layers = pipeline.Layout.Layers,
                Experiments = pipeline.Layout.Experiments,
                Arms = pipeline.Layout.Arms,
            };
            asked = await BoundedCall.RunAsync<IReadOnlyList<string>, ProviderCall>(
                new ProviderCall(_assignmentProvider, context), bound, memory: null, AssignmentTarget, CallDeadline.None).ConfigureAwait(false);
        }
        finally
        {
            bound.Release();
        }

        switch (asked.End)
        {
            case CallEnd.Answered:
                return Resolve(pipeline.Plan, asked.Value);
            case CallEnd.Threw when called && asked.Outcome != IoOutcome.Timeout:
                return (RequestArms.None, Array.Empty<IgnoredArm>(), $"its assignment failed: {asked.Error ?? "the provider threw"}", RecErrorKind.Failed);
            case CallEnd.Cancelled:
            case CallEnd.Threw when asked.Outcome != IoOutcome.Timeout:
                ExceptionDispatchInfo.Throw(asked.Exception!);
                break;
        }

        return (RequestArms.None, Array.Empty<IgnoredArm>(),
            asked.Sent ? "deadline exceeded during assignment" : "deadline exceeded before assignment", RecErrorKind.DeadlineExceeded);

        static (RequestArms, IgnoredArm[], string?, RecErrorKind) Resolve(ExecutionPlan plan, IReadOnlyList<string>? answered)
        {
            var assigned = ExternalArms.Resolve(plan.Layers.AsSpan(), plan.Arms.AsSpan(), answered, out var ignoredIds);
            return (assigned, ignoredIds, null, RecErrorKind.None);
        }
    }

    private static readonly IoTarget AssignmentTarget = new(IoRoles.Assignment, "provider");

    private readonly struct ProviderCall(IAssignmentProvider provider, AssignmentContext context) : IBoundedCall<IReadOnlyList<string>>
    {
        public int Keys => -1;

        public ValueTask<IReadOnlyList<string>> Send(CancellationToken token) => provider.AssignAsync(context, token);

        public IoOutcome Judge(IReadOnlyList<string> value, out string? error)
        {
            error = null;
            return IoOutcome.Ok;
        }

        /// <summary>Nothing waits for an abandoned provider: it holds nothing of the request, and its token lives until it ends.</summary>
        public void Pending(Task done)
        {
        }

        public void Ended(in CallResult<IReadOnlyList<string>> result, TimeSpan duration)
        {
        }
    }

    /// <summary>
    /// The plan a pipeline serves, its binding lease entered (a retired lease reads the current plan again): the caller
    /// ends the entry (<see cref="BindingLease.RequestEnded"/>) when it is done with the plan; the plan's runs join the
    /// lease meanwhile.
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
    private ValueTask<PipelineResult> Run(
        LoadedPipeline pipeline, RecRequest request, RequestInputs inputs, RequestArms arms, TimeSpan deadline, long? seed, long? timeMs, RunCapture? runs,
        RequestReads reads, ref RequestCalls? calls, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(pipeline.Plan, new PipelineRequest
        {
            RequestId = request.RequestId,
            Inputs = inputs,
            Arms = arms,
            Deadline = deadline,
            Seed = seed,
            TimeMs = timeMs,
            CaptureLogs = request.Debug?.CaptureLogs == true,
            WriteLogs = WritesLogs(request),
            ExplainIds = request.Debug?.ExplainIds,
            OnRecording = runs is null ? null : runs.Add,
            Calls = pipeline.Plan.Calls.IsEmpty ? null : new RunCalls(calls ??= new RequestCalls(this, request, reads), pipeline.Plan.PipelineId, 0),
            Reads = reads,
            OnEnd = pipeline.Bindings.Join(), // last: the executor, called next, ends it on every outcome
        }, cancellationToken);

    private static bool WritesLogs(RecRequest request) =>
        request.Debug is not { } debug || (debug.ForceArms is null && debug.ConfigOverride is null && !debug.CaptureLogs);

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

    private static RecResult Combine(
        RecRequest request, PipelineResult executed, RecResult converted, PipelineResult served, IReadOnlyList<IgnoredArm> ignored, string fallbackId,
        IReadOnlyList<string>? merged = null) => new()
    {
        RequestId = request.RequestId,
        Succeeded = converted.Succeeded,
        ErrorKind = converted.Succeeded ? RecErrorKind.None : RecErrorKind.Failed,
        Error = $"pipeline '{request.PipelineId}' failed: {executed.Error}" + (served.Succeeded ? string.Empty : $"; fallback '{fallbackId}' failed: {served.Error}"),
        Columns = converted.Columns,
        Values = converted.Values,
        Experiments = new ExperimentRecord(merged ?? executed.AssignedArms, executed.TriggeredExperiments, ignored),
        ManifestId = converted.ManifestId,
        ServedBy = fallbackId,
        ColumnProducers = converted.ColumnProducers,
        Trace = converted.Trace,
        LogBatches = converted.LogBatches is { } fallbackBatches ? BothRuns(executed.LogBatches, fallbackBatches) : null,
        Explanation = converted.Explanation,
        ElapsedMs = executed.ElapsedMs + served.ElapsedMs,
    };

    private static IReadOnlyList<Logging.LogBatch> BothRuns(IReadOnlyList<Logging.LogBatch> primary, IReadOnlyList<Logging.LogBatch> fallback)
    {
        if (primary.Count == 0)
        {
            return fallback;
        }

        var batches = new List<Logging.LogBatch>(primary.Count + fallback.Count);
        foreach (var batch in primary)
        {
            batches.Add(batch);
        }

        foreach (var batch in fallback)
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <param name="merged">The arms a configuration override's plan merged in: the run had none of its own, and they are
    /// the request's assignment; null for every other run.</param>
    private static RecResult ToResult(
        RecRequest request, LoadedPipeline pipeline, PipelineResult result, IReadOnlyList<IgnoredArm> ignored, string servedBy, IReadOnlyList<string>? merged = null) => new()
    {
        RequestId = request.RequestId,
        Succeeded = result.Succeeded,
        ErrorKind = result.Succeeded ? RecErrorKind.None : RecErrorKind.Failed,
        Error = result.Error,
        Columns = result.Columns,
        Values = result.Values,
        Experiments = new ExperimentRecord(merged ?? result.AssignedArms, result.TriggeredExperiments, ignored),
        ManifestId = pipeline.Plan.ManifestId,
        ServedBy = servedBy,
        ColumnProducers = result.ColumnProducers,
        Trace = request.Debug?.Trace == true ? result.Outcomes : null,
        LogBatches = request.Debug?.CaptureLogs == true ? result.LogBatches : null,
        Explanation = result.Explanation,
        ElapsedMs = result.ElapsedMs,
    };

    private static RecResult Rejected(
        RecRequest request, RecErrorKind kind, string error, ExperimentRecord? experiments = null, string? manifestId = null, string? pointer = null) => new()
    {
        RequestId = request.RequestId,
        Succeeded = false,
        ErrorKind = kind,
        Error = error,
        ErrorPointer = pointer,
        Columns = ResultColumns.Empty,
        Experiments = experiments ?? new ExperimentRecord(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IgnoredArm>()),
        ManifestId = manifestId ?? string.Empty,
        ServedBy = request.PipelineId,
    };
}
