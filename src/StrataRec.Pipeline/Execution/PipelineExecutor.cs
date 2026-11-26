using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Threading;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Runtime;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Pipeline.Execution;

/// <summary>One run of a plan as <c>RecEngine</c> asks for it, with the request's arms already decided.</summary>
internal sealed class PipelineRequest
{
    public required string RequestId { get; init; }

    public required RequestInputs Inputs { get; init; }

    private RequestArms _arms;

    /// <summary>The request's arms; the executor runs them as <see cref="ExecutionPlan.Yields"/> resolves them.</summary>
    public RequestArms Arms { get => _arms; init => _arms = value; }

    /// <summary>This request with other arms (a shallow copy: the rest is shared).</summary>
    internal PipelineRequest WithArms(in RequestArms arms)
    {
        var copy = (PipelineRequest)MemberwiseClone();
        copy._arms = arms;
        return copy;
    }

    public required TimeSpan Deadline { get; init; }

    /// <summary>Request seed; defaults to a hash of the request id (design 3.4).</summary>
    public long? Seed { get; init; }

    /// <summary>Request time in Unix milliseconds; defaults to now.</summary>
    public long? TimeMs { get; init; }

    /// <summary>
    /// Return log batches in <see cref="PipelineResult.LogBatches"/> to the caller. A captured batch is the caller's, so
    /// it is never also sent to the log pipeline, whatever <see cref="WriteLogs"/> says.
    /// </summary>
    public bool CaptureLogs { get; init; }

    /// <summary>
    /// Whether the run may send its log batches (taps and the <c>$experiments</c> record) to the log pipeline, the
    /// persistent logs: the engine sets it for traffic, and not for a run whose arms or configuration a debugger forced,
    /// whose logs are captured, or that replays (design 8.6 item 6, 14 item 6), since what they log is not what the
    /// served configuration did for the request and must not reach experiment analysis or training data. Separate from
    /// <see cref="CaptureLogs"/>: a forced run that captures nothing writes nothing either. False by default, so a run
    /// writes only when asked to; a replay never writes.
    /// </summary>
    public bool WriteLogs { get; init; }

    /// <summary>
    /// Record the run for a replay bundle (design 13.4): <see cref="PipelineResult.Recording"/> then holds every external
    /// call, node decision and the sampling. Requests that do not record pay nothing for the feature but a null check per
    /// external call and node.
    /// </summary>
    public bool Record { get; init; }

    /// <summary>
    /// Receives the run's recording on every exit (design 13.4): with the result when the run returns, and before the
    /// exception propagates when it is aborted (cancelled by the caller, or a defect), so the engine can still deliver the
    /// request's bundle (<c>DebugOptions.OnRecording</c>). Setting it records the run. Called once, on the request's
    /// thread; an exception it throws is reported as an event and does not change the run's outcome.
    /// </summary>
    /// <remarks>
    /// An aborted run's recording is a snapshot taken at the abort: calls still in flight are <c>outstanding</c>, and what
    /// they return afterwards is not in it. Nothing waits for them.
    /// </remarks>
    public Action<ReplayRunRecord>? OnRecording { get; init; }

    /// <summary>
    /// Replay a recorded run instead of calling sources and models (design 13.4): external calls are answered from the
    /// recording and timing decisions come from it, not from the clock. A replay never writes persistent logs and records
    /// no metrics, event logs or spans. Use <see cref="RunReplay.Deadline"/> as <see cref="Deadline"/>.
    /// </summary>
    public RunReplay? Replay { get; init; }

    /// <summary>
    /// Told once when the run and every external call it started have ended (its request memory is returned), which may be
    /// after the run returned: a call it abandoned at a timeout runs on inside its client. An engine passes the plan's
    /// binding lease, so the plan's clients are not disposed while anything of the run can still call them.
    /// </summary>
    /// <remarks>Set, it is called exactly once per <see cref="PipelineExecutor.ExecuteAsync"/>, whatever the outcome.</remarks>
    public IRequestEndObserver? OnEnd { get; init; }

    /// <summary>
    /// Candidates to explain (design 13.3): <see cref="PipelineResult.Explanation"/> then has, for each, its state after
    /// every node. At most <see cref="ExplainTargets.MaxCount"/>, or the run fails as with invalid inputs; repeated ids are
    /// explained once, and an id of the other key type is never found (a host checks ids first with
    /// <see cref="ExplainTargets.Check"/>). Null, the default, costs nothing but a null check per node.
    /// </summary>
    public IReadOnlyList<EntityKey>? ExplainIds { get; init; }

    /// <summary>
    /// Runs the pipelines the run's nodes call (<see cref="Operators.OperatorContext.CallPipelineAsync"/>):
    /// the engine's, which serves them. Null for a run nothing serves pipelines to: a call then fails as a result.
    /// </summary>
    public IPipelineCalls? Calls { get; init; }

    /// <summary>For a run a node of another pipeline called: who called, for its log batches (<see cref="Logging.LogBatch.CalledBy"/>).</summary>
    public string? CalledBy { get; init; }

    /// <summary>The seed of a request that does not set one: a hash of its id (design 3.4).</summary>
    public static long DefaultSeed(string requestId) => unchecked((long)StableHash.Hash(StableHash.SeedFromSalt(requestId)));
}

/// <summary>Result of one run (<see cref="PipelineExecutor"/>); <c>RecEngine</c> turns it into the request's result.</summary>
internal sealed class PipelineResult
{
    public required bool Succeeded { get; init; }

    public string? Error { get; init; }

    /// <summary>The returned items column by column (<see cref="ResultColumns"/>): the form to read many rows in.</summary>
    public required ResultColumns Columns { get; init; }

    /// <summary>The output's request-level values (<c>output.values</c>); empty for a failed run.</summary>
    public ResultValues Values { get; init; } = ResultValues.Empty;

    /// <summary>The returned items as rows, a dictionary of output columns each, made from <see cref="Columns"/> when first read.</summary>
    public IReadOnlyList<RankedItem> Items => Columns.Rows;

    public required IReadOnlyList<NodeOutcome> Outcomes { get; init; }

    /// <summary>Assigned arms as <c>experiment/arm</c>.</summary>
    public required IReadOnlyList<string> AssignedArms { get; init; }

    /// <summary>Experiments whose modifications were actually reached (design 12.6).</summary>
    public required IReadOnlyList<string> TriggeredExperiments { get; init; }

    /// <summary>Output column → node that produced it (lineage, design 8.7).</summary>
    public required IReadOnlyDictionary<string, string> ColumnProducers { get; init; }

    public double ElapsedMs { get; init; }

    /// <summary>Number of non-finite values replaced or masked at commit (metric <c>column_nonfinite_total</c>).</summary>
    public int NonFiniteValues { get; init; }

    /// <summary>
    /// Number of values that did not fit the int32 storage of an <c>int32</c> or <c>list&lt;int32&gt;</c> column or feature
    /// and were replaced by its default or marked missing (metric <c>stratarec.column.out_of_range</c>, design 4.3).
    /// Counted where the value is written, so it includes writes of nodes that later failed.
    /// </summary>
    public long OutOfRangeValues { get; init; }

    /// <summary>
    /// The request succeeded with a node that was degraded, served by its fallback, failed and was skipped, or was skipped
    /// for lack of time or input (metric <c>stratarec.request.degraded</c>).
    /// </summary>
    public bool Degraded { get; init; }

    /// <summary>Log batches of the request when <see cref="PipelineRequest.CaptureLogs"/> was set.</summary>
    public IReadOnlyList<Logging.LogBatch> LogBatches { get; set; } = Array.Empty<Logging.LogBatch>();

    /// <summary>The run's recording when <see cref="PipelineRequest.Record"/> was set (design 13.4).</summary>
    public ReplayRunRecord? Recording { get; set; }

    /// <summary>The explained candidates when <see cref="PipelineRequest.ExplainIds"/> was set (design 13.3); null otherwise.</summary>
    public RunExplanation? Explanation { get; set; }
}

/// <summary>Mutable per-request state shared by the executor and operator contexts.</summary>
internal sealed class RequestState
{
    public required ExecutionPlan Plan { get; init; }

    public required string RequestId { get; init; }

    /// <summary>
    /// The caller's token. Operators see a node token that the node budget also cancels; this one tells an external call
    /// cut off by the caller (not the dependency's fault) from one that ran out of time.
    /// </summary>
    public CancellationToken CallerCancellation { get; init; }

    public required RequestInputs Inputs { get; init; }

    public required long Seed { get; init; }

    public required RequestMemory Memory { get; init; }

    public required CandidateTable Table { get; init; }

    public required OutputBindings Bindings { get; init; }

    public required ScalarValue[] Context { get; init; }

    public required ScalarValue[] Params { get; init; }

    public required FeatureFetcher Features { get; init; }

    public required ExpressionCounters Counters { get; init; }

    public required long DeadlineTimestamp { get; init; }

    public required RequestArms Arms { get; init; }

    public required CompiledNode?[] Variants { get; init; }

    public bool[] Triggered { get; init; } = Array.Empty<bool>();

    public bool?[] WhenResults { get; init; } = Array.Empty<bool?>();

    /// <summary>Selected tap variant per tap slot; null when absent or its group did not sample the request.</summary>
    public CompiledTap?[] Taps { get; set; } = Array.Empty<CompiledTap?>();

    /// <summary>Batches captured so far; null when no tap is active for the request.</summary>
    public List<Logging.LogBatch>? Batches { get; set; }

    /// <summary>Request hash shared by all row priorities (design 8.4).</summary>
    public long RequestHash => Logging.CoordinatedSampling.RequestHash(RequestId);

    public List<NodeOutcome> Outcomes { get; } = new();

    public int NonFinite { get; set; }

    /// <summary>Set by any outcome other than success or a <c>when</c> skip.</summary>
    public bool Degraded { get; set; }

    /// <summary>Windowed failure summaries and source health; null when the executor has no logger.</summary>
    public RequestEventLog? Events { get; init; }

    /// <summary>The request span is recorded: create node spans (design 13.2).</summary>
    public bool TraceNodes { get; init; }

    /// <summary>Span of the position being run, when <see cref="TraceNodes"/>.</summary>
    public Activity? NodeSpan { get; set; }

    /// <summary>
    /// The position being run has started and not yet recorded its outcome. An abort (caller cancellation, defect) records
    /// it then, so slow attempts the caller gave up on stay in node latency and outcome metrics.
    /// </summary>
    public bool PositionOpen { get; set; }

    public long PositionStarted { get; set; }

    public int PositionRowsIn { get; set; }

    /// <summary>The fallback node run for the current position, for its span's variant and experiment attribution.</summary>
    public CompiledNode? PositionFallback { get; set; }

    /// <summary>Records the run for a replay bundle; null unless the request asked for it (design 13.4).</summary>
    public RunRecorder? Recorder { get; init; }

    /// <summary>Answers external calls and timing decisions from a recording; null unless the request is a replay.</summary>
    public RunReplay? Replay { get; init; }

    /// <summary>Follows the candidates the request explains; null unless it asked to (design 13.3).</summary>
    public CandidateExplainer? Explainer { get; init; }

    /// <summary>Runs the pipelines the run's nodes call (<see cref="PipelineRequest.Calls"/>); null when nothing does.</summary>
    public IPipelineCalls? Calls { get; init; }

    /// <summary>Who called the run (<see cref="PipelineRequest.CalledBy"/>); null for a run of the request's own pipeline.</summary>
    public string? CalledBy { get; init; }

    private Dictionary<string, int>? _pipelineCalls;

    /// <summary>
    /// The place of a node's next pipeline call among those it made in this run, from 0: counted over the run, not over an
    /// attempt, since a fallback node may run for several nodes, and each of its calls needs a place of its own (a seed, a
    /// record a replay finds). Calls made at once from several threads each get one, in no repeatable order.
    /// </summary>
    public int NextPipelineCall(string nodeId)
    {
        lock (this)
        {
            _pipelineCalls ??= new Dictionary<string, int>(StringComparer.Ordinal);
            _pipelineCalls.TryGetValue(nodeId, out var next);
            _pipelineCalls[nodeId] = next + 1;
            return next;
        }
    }

    public TimeSpan Remaining => Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), DeadlineTimestamp);

    public EvalContext Eval => new(Table, Bindings, Context, Params, Counters);

    /// <summary>
    /// The buffer a column reads in this request, as an expression reads it: its fixed slot (a feature, an <c>@node</c>
    /// version, <c>col.entity_id</c>) when it has one, otherwise the committed version through the bindings; null when no
    /// node has produced it.
    /// </summary>
    public ColumnBuffer? ReadColumn(int fixedSlot, int logicalId)
    {
        var slot = fixedSlot >= 0 ? fixedSlot : Bindings.Resolve(logicalId);
        return slot < 0 ? null : Table.TryGetSlot(slot);
    }

    /// <summary>
    /// The slot a node writes an output into now (design 4.6): its own, except that a row source writes the rows it
    /// appends into the column's current version once a node has committed one. Its rows then join the values readers
    /// already see, and nothing is committed for the column: a row source after a scorer can give the rows it injects a
    /// score in the same column. Before any version, and after a row source's, this is the row sources' shared version.
    /// </summary>
    /// <remarks>Stable for a node's attempt: nothing commits while it runs. A failed attempt's rows are released by the
    /// rollback, in every slot, so writing into another node's version leaves nothing behind.</remarks>
    public int OutputSlot(Operators.Effects effects, in Operators.OutputColumn output) =>
        (effects & Operators.Effects.RowSource) != 0 && Bindings.Resolve(output.LogicalId) is >= 0 and var current ? current : output.Slot;
}

/// <summary>
/// Executes an <see cref="ExecutionPlan"/> for one request (design 3.3): walk the timeline in order, pick each
/// position's variant, make its features available, run it with its time budget, and commit outputs only on success.
/// CPU work runs on the request's logical thread; concurrency comes from overlapping I/O (design 5.4).
/// </summary>
/// <remarks>
/// <para>Internal: <c>RecEngine</c> is the one host API (design 3.4). It owns what a run needs around it (the plan's
/// binding lease, assignment, the fallback pipeline, bundles, the request span and request-level metrics), which a
/// second, lower-level way to run plans would have had to repeat or leave out. Tests reach the executor through
/// <c>InternalsVisibleTo</c>.</para>
/// <para>Observability (design 13): node, source, model, column and memory metrics go to <see cref="StrataRecMetrics"/>.
/// Node and I/O spans are created only under a recorded request span (<see cref="StrataRecTracing.IsRecording"/>),
/// which <c>RecEngine</c> starts; request-level metrics are recorded there too.</para>
/// </remarks>
internal sealed class PipelineExecutor
{
    private readonly IBufferPool _pool;
    private readonly Logging.LogPipeline? _logs;
    private readonly RequestEventLog? _events;

    /// <param name="pool">Buffer pool for request memory.</param>
    /// <param name="logs">Where tap batches and trigger records go; null disables persistent logging.</param>
    /// <param name="loggerFactory">
    /// Receives windowed summaries of request-level failures and source health changes (design 13.2); null logs nothing.
    /// </param>
    public PipelineExecutor(IBufferPool? pool = null, Logging.LogPipeline? logs = null, ILoggerFactory? loggerFactory = null)
        : this(pool, logs, loggerFactory is null ? null : new RequestEventLog(loggerFactory.CreateLogger<PipelineExecutor>()))
    {
    }

    /// <summary>Where request-level failures are summarized; the engine's assignment provider reports there too.</summary>
    internal RequestEventLog? Events => _events;

    /// <summary>With a caller-owned event log, e.g. one a test flushes by hand.</summary>
    internal PipelineExecutor(IBufferPool? pool, Logging.LogPipeline? logs, RequestEventLog? events)
    {
        _pool = pool ?? SharedArrayBufferPool.Instance;
        _logs = logs;
        _events = events;
    }

    public async ValueTask<PipelineResult> ExecuteAsync(ExecutionPlan plan, PipelineRequest request, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();

        // Arms one of which yields never run together, whoever decided them (ArmYields): the engine has resolved them
        // after the assignment (and recorded what yielded); a caller that passes such arms gets what a request in them runs.
        if (!plan.Yields.IsEmpty)
        {
            var running = plan.Yields.Resolve(request.Arms);
            if (!running.SameAs(request.Arms))
            {
                request = request.WithArms(running);
            }
        }

        // A replay is not traffic: no metrics, event logs or spans (its timings are not real, and the recorded request
        // was already counted), and never a persistent log (design 8.6 item 6).
        var replay = request.Replay;
        var events = replay is null ? _events : null;
        var memory = new RequestMemory(_pool, request.OnEnd, events, plan.PipelineId, request.RequestId); // from here the run owns telling OnEnd
        FeatureFetcher? fetcher = null;
        RequestState? state = null;
        var position = 0;
        // A replay that records keeps its node decisions, parameters and result (its calls are answered, not recorded),
        // which is what a replay report compares with the original run.
        var recorder = request.Record || request.OnRecording is not null ? new RunRecorder() : null;
        try
        {
            var context = new ScalarValue[plan.Schema.ContextFields.Count];
            var inputError = (request.ExplainIds is { } explainIds ? ExplainTargets.TooMany(explainIds.Count) : null) ?? BindInputs(plan, request, context);
            if (inputError is not null)
            {
                var rejected = Failed(plan, request, inputError, start, Array.Empty<NodeOutcome>(), Array.Empty<bool>());
                if (recorder is not null)
                {
                    rejected.Recording = recorder.Build(plan, request, null, rejected, LogDestination(request, returned: true), ConsumesLogs(request));
                    Deliver(plan, request, rejected.Recording, events);
                }

                return rejected;
            }

            var parameters = plan.Params.Snapshot(request.Arms);
            var variants = new CompiledNode?[plan.Timeline.Length];
            var rowLimits = memory.Rent<int>(Math.Max(1, variants.Length));
            long capacity = 0;
            for (var i = 0; i < variants.Length; i++)
            {
                var node = plan.Timeline[i].Select(request.Arms);
                variants[i] = node;
                rowLimits[i] = 0;
                if (node is null)
                {
                    continue;
                }

                // Only one of a position's primary and fallback appends rows that stay: a failed attempt gives its rows back
                // (CandidateTable.Rollback), so the fallback reuses them (design 4.8, 5.6).
                var limit = RowLimit(node, parameters);
                if (node.OnFailure == FailurePolicy.Fallback && plan.SelectFallback(node, request.Arms) is { } fallback)
                {
                    limit = Math.Max(limit, RowLimit(fallback, parameters));
                }

                rowLimits[i] = (int)Math.Min(limit, int.MaxValue);
                capacity += limit;
            }

            var table = new CandidateTable(plan.TableSchema, (int)Math.Min(capacity, int.MaxValue), memory);
            var bindings = new OutputBindings(plan.Schema.Columns.Count, memory);
            var deadline = CallBound.DeadlineAfter(start, request.Deadline);
            // One set of counters per request: operator expressions and derived features count into the same totals.
            var counters = new ExpressionCounters();
            fetcher = new FeatureFetcher(plan.Features, table, context, bindings, parameters, memory, deadline, counters, request.RequestId, events,
                (IFeatureCallHook?)replay ?? recorder, context[PlanSchema.RequestSeedContext].AsInt64());
            state = new RequestState
            {
                Plan = plan,
                RequestId = request.RequestId,
                CallerCancellation = cancellationToken,
                Inputs = request.Inputs,
                Seed = context[PlanSchema.RequestSeedContext].AsInt64(),
                Memory = memory,
                Table = table,
                Bindings = bindings,
                Context = context,
                Params = parameters,
                Features = fetcher,
                Counters = counters,
                DeadlineTimestamp = deadline,
                Arms = request.Arms,
                Variants = variants,
                Triggered = new bool[plan.Experiments.Length],
                WhenResults = new bool?[plan.Timeline.Length],
                Events = events,
                TraceNodes = replay is null && StrataRecTracing.IsRecording,
                Recorder = recorder,
                Replay = replay,
                Explainer = request.ExplainIds is { } explain ? new CandidateExplainer(explain) : null,
                Calls = request.Calls,
                CalledBy = request.CalledBy,
            };

            fetcher.StartContextFetch(plan.ContextFeatures.AsSpan(), cancellationToken);
            if (ConsumesLogs(request))
            {
                // Without a consumer every captured batch would be discarded: skip sampling and capture entirely.
                await TapCapture.SelectAsync(state, cancellationToken).ConfigureAwait(false);
            }

            PipelineResult? result = null;
            for (; position < plan.Timeline.Length && result is null; position++)
            {
                // A caller that cancelled gets no result, whatever the remaining nodes are: nodes that compute without
                // I/O never look at the token, and went on to log rows as served that nobody received.
                cancellationToken.ThrowIfCancellationRequested();
                var appended = table.AppendedRows;
                var error = await RunPositionAsync(state, position, cancellationToken).ConfigureAwait(false);
                if (rowLimits[position] > 0)
                {
                    // The position can append no more: what it did not use of its limit is not reserved for later columns
                    // (a recall that found 500 of its 2,000 rows sizes every later column for 500).
                    table.ReleaseRowBudget(rowLimits[position] - (table.AppendedRows - appended));
                }

                EndNodeSpan(state, position, null);
                if (error is not null)
                {
                    result = Failed(plan, request, error, start, state.Outcomes, state.Triggered, state.NonFinite, state.Counters.OutOfRange);
                }
            }

            cancellationToken.ThrowIfCancellationRequested(); // as before each node: before the result is built and logged as returned
            result ??= await BuildResultAsync(state, request, start, cancellationToken).ConfigureAwait(false);
            if (state.Explainer is { } explainer)
            {
                result.Explanation = explainer.Build(plan.PipelineId, result.Succeeded, result.Items.Count);
            }

            if (recorder is not null)
            {
                result.Recording = recorder.Build(plan, request, parameters, result, LogDestination(request, returned: true), ConsumesLogs(request));
                Deliver(plan, request, result.Recording, events);
            }

            PublishLogs(state, request, result, returned: true);
            return result;
        }
        catch (Exception ex) when (state is not null)
        {
            // Cancelled by the caller (or a defect): nobody receives a result, but the request was assigned, may have
            // been exposed, and may have captured taps. Dropping those records would remove slow or failing requests
            // from experiment analysis, biased by arm (design 8.8, 12.6). Request memory is still held here.
            var reason = ex switch
            {
                OperationCanceledException => "cancelled by the caller",
                _ => "aborted: " + ex.Message,
            };
            if (state.PositionOpen && position < state.Variants.Length && state.Variants[position] is { } open)
            {
                Record(state, open, NodeOutcomeKind.Aborted, reason, null, state.PositionStarted, state.PositionRowsIn);
            }

            EndNodeSpan(state, position, reason);
            var aborted = Failed(plan, request, reason, start, state.Outcomes, state.Triggered, state.NonFinite, state.Counters.OutOfRange);
            if (recorder is not null)
            {
                try
                {
                    // A snapshot, taken while the request still holds its memory; calls in flight stay outstanding in it.
                    Deliver(plan, request, recorder.Build(plan, request, state.Params, aborted, LogDestination(request, returned: false), ConsumesLogs(request), aborted: true), events);
                }
                catch (Exception recordError)
                {
                    events?.Report(RequestEvent.RecordingFailed, plan.PipelineId, request.RequestId, recordError.Message);
                }
            }

            try
            {
                PublishLogs(state, request, aborted, returned: false);
            }
            catch (Exception publishError)
            {
                events?.Report(RequestEvent.LogPublishFailed, plan.PipelineId, request.RequestId, publishError.Message);
            }

            throw;
        }
        finally
        {
            // Prefetches no node read were still answered (stratarec.source.values); their staging is in the memory. A
            // chained context group whose key is stored from now on is not sent: nothing can read it.
            fetcher?.EndRequest();

            // In-flight connector calls hold the memory themselves; buffers return when the last one finishes. Those
            // still running now outlive the request (design 4.8).
            var rented = memory.RentedBytes;
            var abandoned = memory.Release();
            if (replay is null)
            {
                var tags = plan.Telemetry.Pipeline;
                StrataRecMetrics.RequestMemoryRented.Record(rented, tags);
                if (abandoned > 0)
                {
                    StrataRecMetrics.AbandonedIo.Add(abandoned, tags);
                    events?.WatchHeld(memory, plan.PipelineId, request.RequestId);
                }

                if (state?.Counters.OutOfRange is > 0 and var outOfRange)
                {
                    StrataRecMetrics.ColumnOutOfRange.Add(outOfRange, tags);
                }
            }
        }
    }

    /// <summary>The rows a row source may append for this request; 0 for other nodes.</summary>
    private static long RowLimit(CompiledNode node, ReadOnlySpan<ScalarValue> parameters) =>
        (node.Effects & Effects.RowSource) != 0 ? Math.Max(0, node.Operator.GetRowLimit(parameters)) : 0;

    /// <summary>Runs one timeline position. Returns an error message when the request must fail.</summary>
    private static async ValueTask<string?> RunPositionAsync(RequestState state, int position, CancellationToken cancellationToken)
    {
        var slot = state.Plan.Timeline[position];
        var node = state.Variants[position];
        await RecordSiteTriggersAsync(state, slot, fallback: false, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return null;
        }

        // Node spans exist only inside a recorded request span: an unrecorded request creates no node-level objects.
        // The caller ends the span after the position (EndNodeSpan), so this method keeps its single state machine.
        if (state.TraceNodes)
        {
            state.NodeSpan = StartNodeSpan(node);
        }

        var started = Stopwatch.GetTimestamp();
        var rowsIn = state.Table.Rows.Count;
        state.PositionOpen = true;
        state.PositionStarted = started;
        state.PositionRowsIn = rowsIn;
        state.PositionFallback = null;
        if (!await EvaluateWhenAsync(state, position, node, cancellationToken).ConfigureAwait(false))
        {
            Record(state, node, NodeOutcomeKind.SkippedWhen, "when is false", null, started, rowsIn);
            state.Explainer?.Skipped(node.NodeId, "when is false");
            return null;
        }

        if (node.MinRemainingMs is not null || state.Recorder is not null)
        {
            // A replay compares with the time that was left in the recording, not with its own clock (design 5.7 item 5).
            var remainingMs = state.Replay?.RemainingMs(node.NodeId) ?? state.Remaining.TotalMilliseconds;
            state.Recorder?.Remaining(remainingMs);
            if (node.MinRemainingMs is { } minRemaining && remainingMs < minRemaining)
            {
                Record(state, node, NodeOutcomeKind.SkippedDeadline, $"less than {minRemaining} ms remaining", null, started, rowsIn);
                state.Explainer?.Skipped(node.NodeId, $"less than {minRemaining} ms remaining");
                return null;
            }
        }

        int[]? entered = null;
        if (state.Batches is not null && TapCapture.NeedsEnteredRows(state, node.NodeId))
        {
            entered = state.Memory.Rent<int>(Math.Max(1, rowsIn));
            state.Table.Rows.Capture(entered);
        }

        // Unless the node says otherwise, it leaves its fallback the time the fallback may take (design 5.7).
        var reserve = node.OnFailure == FailurePolicy.Fallback
            ? node.FallbackReserveMs ?? state.Plan.SelectFallback(node, state.Arms)?.TimeoutMs ?? 0
            : 0;
        var attempt = await RunNodeAsync(state, node, reserve, cancellationToken).ConfigureAwait(false);
        if (attempt.Kind is AttemptKind.Succeeded or AttemptKind.Degraded)
        {
            var category = attempt.Kind == AttemptKind.Succeeded ? NodeOutcomeKind.Succeeded : NodeOutcomeKind.Degraded;
            Record(state, node, category, attempt.Detail, node.NodeId, started, rowsIn);
            if (state.Batches is not null)
            {
                await TapCapture.AfterNodeAsync(state, node.NodeId, entered, rowsIn, cancellationToken).ConfigureAwait(false);
            }

            await AfterNodeAsync(state, position, node, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (attempt.Kind == AttemptKind.Skipped)
        {
            Record(state, node, NodeOutcomeKind.SkippedInput, attempt.Detail, null, started, rowsIn);
            return null;
        }

        // Failed: apply on_failure (design 5.6).
        switch (node.OnFailure)
        {
            case FailurePolicy.Skip:
                Record(state, node, NodeOutcomeKind.Failed, attempt.Detail + " (skipped)", null, started, rowsIn);
                return null;
            case FailurePolicy.Fallback when state.Plan.SelectFallback(node, state.Arms) is { } fallback:
                // A fallback site triggers only when the fallback is invoked, which is independent of the arm (design 12.6).
                state.PositionFallback = fallback;
                if (state.Plan.Fallbacks.TryGetValue(fallback.NodeId, out var fallbackSlot))
                {
                    await RecordSiteTriggersAsync(state, fallbackSlot, fallback: true, cancellationToken).ConfigureAwait(false);
                }

                var fb = await RunNodeAsync(state, fallback, 0, cancellationToken).ConfigureAwait(false);
                if (fb.Kind is AttemptKind.Succeeded or AttemptKind.Degraded)
                {
                    Record(state, node, NodeOutcomeKind.Fallback, $"{attempt.Detail}; served by fallback '{fallback.NodeId}'", fallback.NodeId, started, rowsIn);
                    if (state.Batches is not null)
                    {
                        await TapCapture.AfterNodeAsync(state, node.NodeId, entered, rowsIn, cancellationToken).ConfigureAwait(false);
                    }

                    await AfterNodeAsync(state, position, fallback, cancellationToken).ConfigureAwait(false);
                    return null;
                }

                if (fallback.OnFailure == FailurePolicy.Skip || fb.Kind == AttemptKind.Skipped)
                {
                    Record(state, node, NodeOutcomeKind.Failed, $"{attempt.Detail}; fallback '{fallback.NodeId}': {fb.Detail} (skipped)", null, started, rowsIn);
                    return null;
                }

                Record(state, node, NodeOutcomeKind.Failed, $"{attempt.Detail}; fallback '{fallback.NodeId}': {fb.Detail}", null, started, rowsIn);
                return $"node '{node.NodeId}' failed ({attempt.Detail}) and its fallback '{fallback.NodeId}' failed ({fb.Detail})";
            default:
                Record(state, node, NodeOutcomeKind.Failed, attempt.Detail, null, started, rowsIn);
                return $"node '{node.NodeId}' failed: {attempt.Detail}";
        }
    }

    private readonly record struct Attempt(AttemptKind Kind, string? Detail);

    /// <summary>How one attempt of a node ended; the position's <see cref="NodeOutcomeKind"/> also says what was done about it.</summary>
    private enum AttemptKind : byte
    {
        Succeeded = 1,
        Degraded,
        Skipped,
        Failed,
    }

    /// <summary>Runs one attempt of a node; for a request that explains candidates, also records the attempt's step (design 13.3).</summary>
    private static ValueTask<Attempt> RunNodeAsync(RequestState state, CompiledNode node, int reserveMs, CancellationToken cancellationToken)
    {
        return state.Explainer is null ? AttemptAsync(state, node, reserveMs, cancellationToken) : Explained(state, node, reserveMs, cancellationToken);

        static async ValueTask<Attempt> Explained(RequestState state, CompiledNode node, int reserveMs, CancellationToken cancellationToken)
        {
            var attempt = await AttemptAsync(state, node, reserveMs, cancellationToken).ConfigureAwait(false);
            var explainer = state.Explainer!;
            switch (attempt.Kind)
            {
                case AttemptKind.Skipped:
                    explainer.Skipped(node.NodeId, attempt.Detail);
                    break;
                case AttemptKind.Failed:
                    explainer.RolledBack(node.NodeId, attempt.Detail);
                    break;
                default:
                    explainer.Ran(state, node);
                    break;
            }

            return attempt;
        }
    }

    private static async ValueTask<Attempt> AttemptAsync(RequestState state, CompiledNode node, int reserveMs, CancellationToken cancellationToken)
    {
        // Runtime protection (design 11.5 step 4): a required input with no producer skips the node.
        foreach (var logical in node.RequiredOutputs)
        {
            if (!state.Bindings.IsProduced(logical))
            {
                return new Attempt(AttemptKind.Skipped, $"input column '{state.Plan.Schema.Columns[logical].Name}' was not produced");
            }
        }

        if (MissingRequestColumn(state, node.RequestReads.AsSpan()) is { } unwritten)
        {
            return new Attempt(AttemptKind.Skipped, $"input request column '{unwritten}' was not written");
        }

        // What the node reads but waits for itself (a recall source's query, design 5.4) is not filled here.
        var fill = await state.Features.EnsureAsync(node.FilledFeatures.AsSpan(), cancellationToken).ConfigureAwait(false);
        if (fill.Failed)
        {
            return new Attempt(AttemptKind.Failed, "feature fetch failed: " + fill.Error);
        }

        var remainingMs = state.Remaining.TotalMilliseconds - reserveMs;
        var budgetMs = node.TimeoutMs is { } timeout ? Math.Min(timeout, remainingMs) : remainingMs;
        if (state.Replay is { } replay)
        {
            // Whether the attempt had time to start, and whether it ran out of time, comes from the recording: the replay
            // makes the same timeout, fallback and skip decisions without waiting (design 5.7 item 5, 13.4).
            var recorded = replay.NextAttempt(node.NodeId);
            if (recorded?.Timing == ReplayTiming.DeadlineExceeded)
            {
                state.Recorder?.Attempt(node.NodeId, ReplayTiming.DeadlineExceeded, recorded.BudgetMs ?? budgetMs);
                return new Attempt(AttemptKind.Failed, "deadline exceeded");
            }

            if (recorded?.Timing == ReplayTiming.TimedOut)
            {
                MarkParamTriggers(state, node);
                state.Recorder?.Attempt(node.NodeId, ReplayTiming.TimedOut, recorded.BudgetMs ?? budgetMs);
                return new Attempt(AttemptKind.Failed, $"timed out after {recorded.BudgetMs ?? budgetMs:0} ms");
            }
        }
        else if (budgetMs <= 0)
        {
            state.Recorder?.Attempt(node.NodeId, ReplayTiming.DeadlineExceeded, budgetMs);
            return new Attempt(AttemptKind.Failed, "deadline exceeded");
        }

        // A failed attempt leaves the table exactly as it was (design 4.6): the ranking, no rows and no slots of its own, so
        // nothing that runs or reads later (the fallback, a pinned col.x@node, the output) sees what it wrote, just as in a
        // replay, which skips a recorded timeout instead of running it.
        var checkpoint = state.Table.Checkpoint();

        try
        {
            // The attempt's absolute deadline. The context hands the operator a token cancelled at it and bounds every external
            // wait by it, whether or not the client honours the token. A replay takes timing from its recording instead.
            var deadline = state.Replay is null
                ? Stopwatch.GetTimestamp() + (long)(Math.Min(budgetMs, AttemptBudget.MaxWaitMs) * Stopwatch.Frequency / 1000)
                : long.MaxValue;
            var budget = new AttemptBudget(cancellationToken, deadline, state.Events, state.RequestId);
            var context = new OperatorContext(state, node.NodeId, node.Effects, budget, node.RequestOutputs);
            string? failure;
            var timedOut = false;
            try
            {
                // The operator is about to read its parameters: that is exposure, whether it then succeeds or fails
                // (design 12.6). Recording only successes would bias triggered analysis against arms that fail more.
                MarkParamTriggers(state, node);
                await node.Operator.ExecuteAsync(context).ConfigureAwait(false);
                if (budget.RanOut)
                {
                    // A late success is not a success (AttemptBudget.RanOut). The operator itself is awaited, never abandoned:
                    // it changes the table the fallback works on.
                    timedOut = true;
                    failure = $"timed out after {budgetMs:0} ms";
                }
                else if ((failure = context.FailureReason) is null)
                {
                    // An explained request sees the attempt as the operator left it, before its outputs are committed: the
                    // operator explains itself reading the versions it read (CandidateExplainer.Observe).
                    state.Explainer?.Observe(state, node);
                    failure = Commit(state, node, context);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                failure = $"timed out after {budgetMs:0} ms";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = $"{ExceptionName(ex)}: {ex.Message}";
            }
            finally
            {
                // The node token's source lives on while a client the context abandoned still holds its token.
                budget.Release();
            }

            state.Recorder?.Attempt(node.NodeId, timedOut ? ReplayTiming.TimedOut : ReplayTiming.Ran, budgetMs);

            if (failure is not null)
            {
                // Every way an attempt fails ends here: an exception, a failure it reported, a timeout, a late success, a commit.
                state.Table.Rollback(checkpoint);
                return new Attempt(AttemptKind.Failed, failure);
            }

            var degraded = context.DegradedReason ?? (fill.State == FeatureState.Degraded ? "degraded features: " + fill.Error : null);
            return new Attempt(degraded is null ? AttemptKind.Succeeded : AttemptKind.Degraded, degraded);
        }
        finally
        {
            state.Table.Discard(checkpoint); // settled, or the request is cancelled: nothing rolls back to it again
        }
    }

    /// <summary>The exception's type name for a failure detail.</summary>
    private static string ExceptionName(Exception ex) => ex.GetType().Name;

    /// <summary>
    /// Validates and publishes a successful node's outputs (design 4.3, 4.6): non-finite values take the column default
    /// or become missing; non-nullable columns must then have a value on every live row.
    /// </summary>
    /// <remarks>
    /// A row nobody wrote is missing (<see cref="CandidateTable"/>), so a row the node skipped, a column it never wrote,
    /// and a row an earlier row source appended without this column all fail the node here. The default does not fill
    /// them: it replaces only values that cannot be stored (non-finite, out of int32 range), which the writer and the
    /// sanitizing above already did, so a missing producer never turns into the default (design 4.6).
    /// </remarks>
    /// <remarks>
    /// A request column the node writes is published the same way, as one value (<see cref="CommitRequestColumns"/>): every
    /// check comes before anything is published, so a node that fails here leaves the table and the request columns as
    /// they were.
    /// </remarks>
    private static string? Commit(RequestState state, CompiledNode node, OperatorContext context)
    {
        var table = state.Table;
        var live = table.Rows.Order;
        for (var o = 0; o < node.Outputs.Length; o++)
        {
            var output = node.Outputs[o];
            var column = state.Plan.Schema.Columns[output.LogicalId];

            // A node that never asked for its buffer wrote nothing: the new slot has every row missing.
            var slot = state.OutputSlot(node.Effects, output);
            var buffer = table.GetOrCreateSlot(slot);
            if (ColumnValues.IsFloat(buffer)
                && ColumnValues.SanitizeNonFinite(buffer, live, column.HasDefault ? column.Default : ScalarValue.Missing) is > 0 and var replaced)
            {
                state.NonFinite += replaced;
                if (state.Replay is null)
                {
                    StrataRecMetrics.ColumnNonFinite.Add(replaced, state.Plan.Telemetry.Nodes[node.Index].Outputs[o]);
                }
            }

            if (!output.Type.Nullable && table.CountMissingLiveRows(slot) is > 0 and var missing)
            {
                return $"left non-nullable column '{output.Name}' without a value on {missing} of {live.Length} live rows";
            }
        }

        if (node.RequestOutputs.Length > 0 && CommitRequestColumns(state, node, context) is { } failure)
        {
            return failure;
        }

        foreach (var output in node.Outputs)
        {
            // A row source that wrote into another node's version leaves that version current (OutputSlot).
            if (state.OutputSlot(node.Effects, output) == output.Slot)
            {
                state.Bindings.Commit(output.LogicalId, output.Slot, node.Index);
            }
        }

        // Published last, with nothing left to fail: a later node, tap and the output read them from here.
        for (var p = 0; p < node.RequestOutputs.Length; p++)
        {
            state.Context[node.RequestOutputs[p].ContextId] = context.StagedRequestValue(p);
        }

        return null;
    }

    /// <summary>
    /// Settles what the attempt staged for each request column it writes, as a column's commit settles its rows (design
    /// 4.3): stored as the column's type holds it (<see cref="ColumnValues.Store"/>), NaN/±∞ and a value outside int32
    /// storage replaced by the column's default or missing, and counted; then a non-nullable column must have a value.
    /// A column the node wrote nothing to is missing, as a column whose slot it never asked for is: the node's write is
    /// the new value, so a nullable flow column written before is missing after it.
    /// </summary>
    private static string? CommitRequestColumns(RequestState state, CompiledNode node, OperatorContext context)
    {
        for (var p = 0; p < node.RequestOutputs.Length; p++)
        {
            var output = node.RequestOutputs[p];
            var field = state.Plan.Schema.ContextFields[output.ContextId];
            var value = context.StagedRequestValue(p);
            switch (ColumnValues.Store(ref value, output.Type))
            {
                case StorageFit.NonFinite:
                    state.NonFinite++;
                    if (state.Replay is null)
                    {
                        StrataRecMetrics.ColumnNonFinite.Add(1, state.Plan.Telemetry.Nodes[node.Index].RequestOutputs[p]);
                    }

                    value = field.HasDefault ? field.Default : ScalarValue.Missing;
                    break;
                case StorageFit.OutOfRange:
                    state.Counters.AddOutOfRange(1);
                    value = field.HasDefault ? field.Default : ScalarValue.Missing;
                    break;
            }

            if (value.IsMissing && !output.Type.Nullable)
            {
                return $"left non-nullable request column '{output.Name}' without a value";
            }

            context.RestageRequestValue(p, value);
        }

        return null;
    }

    /// <summary>
    /// The first non-nullable request column among <paramref name="reads"/> (context ids) that no node has written in this
    /// request, or null: what a node, condition or output value reading it cannot be evaluated without. The data-flow
    /// checks rule it out for every arm combination. A trigger site evaluates another arm's condition in a request of
    /// this arm, which they do not check, but there too the column has been written: when the arms differ in its
    /// producer (one adds, deletes or moves it), the producer's position is an earlier site of the experiment, which
    /// triggered it already, and a triggered experiment's conditions are not evaluated (design 12.6). Like the skip of a
    /// node whose input column was not produced (design 11.5 step 4), this is the runtime's own protection: a defect skips
    /// a node rather than aborting the request. A tap reading such a column logs it missing.
    /// </summary>
    internal static string? MissingRequestColumn(RequestState state, ReadOnlySpan<int> reads)
    {
        foreach (var id in reads)
        {
            var field = state.Plan.Schema.ContextFields[id];
            if (field.Kind == ContextFieldKind.RequestColumn && !field.Type.Nullable && state.Context[id].IsMissing)
            {
                return field.Name;
            }
        }

        return null;
    }

    /// <summary><see cref="MissingRequestColumn(RequestState, ReadOnlySpan{int})"/> over the symbols an expression reads.</summary>
    internal static string? MissingRequestColumn(RequestState state, IReadOnlyList<Symbol> reads)
    {
        for (var i = 0; i < reads.Count; i++) // an interface foreach would box the enumerator
        {
            var symbol = reads[i];
            if (symbol.Kind == SymbolKind.Context && !symbol.Type.Nullable && state.Context[symbol.Id].IsMissing
                && state.Plan.Schema.ContextFields[symbol.Id] is { Kind: ContextFieldKind.RequestColumn } field)
            {
                return field.Name;
            }
        }

        return null;
    }

    private static async ValueTask AfterNodeAsync(RequestState state, int position, CompiledNode node, CancellationToken cancellationToken)
    {
        if (!node.IsRowSetNode)
        {
            return;
        }

        if (node.Compact == CompactMode.Always || (node.Compact == CompactMode.Auto && state.Table.LiveRatio < 0.5))
        {
            state.Table.Compact();
            state.Explainer?.Compacted(); // rows moved: the explained ones are followed, not looked up again
        }

        if (node.FetchBarrier)
        {
            await PrefetchSegmentAsync(state, position, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After a fetch barrier, starts fetches for every node up to and including the next barrier, plus their fallbacks
    /// (design 5.4, 6.8). Nodes already known to be skipped by <c>when</c> are left out; a <c>when</c> that reads a request
    /// column is not known yet (the nodes before its own may still write it), so its node's features are prefetched.
    /// </summary>
    private static async ValueTask PrefetchSegmentAsync(RequestState state, int position, CancellationToken cancellationToken)
    {
        var features = new List<int>();
        for (var i = position + 1; i < state.Variants.Length; i++)
        {
            var next = state.Variants[i];
            if (next is null)
            {
                continue;
            }

            if (next.When is not null && !next.WhenReadsRequest && !await EvaluateWhenAsync(state, i, next, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            features.AddRange(next.ReadFeatures.AsSpan()); // a span: an immutable array as IEnumerable would be boxed
            if (state.Plan.SelectFallback(next, state.Arms) is { } fallback)
            {
                features.AddRange(fallback.ReadFeatures.AsSpan());
            }

            if (next.IsRowSetNode && next.FetchBarrier)
            {
                break;
            }
        }

        if (features.Count > 0)
        {
            state.Features.Prefetch(CollectionsMarshal.AsSpan(features), cancellationToken);
        }
    }

    private static async ValueTask<bool> EvaluateWhenAsync(RequestState state, int position, CompiledNode node, CancellationToken cancellationToken)
    {
        if (node.When is null)
        {
            return true;
        }

        if (state.WhenResults[position] is { } cached)
        {
            return cached;
        }

        await EnsureContextAsync(state, node.When, cancellationToken).ConfigureAwait(false);
        var result = Holds(state, node.When);
        state.WhenResults[position] = result;
        return result;
    }

    /// <summary>
    /// Whether a <c>when</c> holds: not when its value is missing, nor when it reads a non-nullable request column no node
    /// has written in this request (<see cref="MissingRequestColumn(RequestState, IReadOnlyList{Symbol})"/>), which it cannot be evaluated without.
    /// </summary>
    private static bool Holds(RequestState state, CompiledExpression when)
    {
        if (MissingRequestColumn(state, when.Reads) is not null)
        {
            return false;
        }

        var value = when.EvaluateScalar(state.Eval);
        return !value.IsMissing && value.AsBool();
    }

    /// <summary>`when` and site conditions read request-level values only; make sure the context features they read are present (design 5.5).</summary>
    private static ValueTask EnsureContextAsync(RequestState state, CompiledExpression expression, CancellationToken cancellationToken)
    {
        List<int>? contextFeatures = null;
        var reads = expression.Reads;
        for (var i = 0; i < reads.Count; i++) // an interface foreach would box the enumerator
        {
            var symbol = reads[i];
            if (symbol.Kind == SymbolKind.Context && state.Plan.Schema.ContextFields[symbol.Id].FeatureIndex is >= 0 and var f)
            {
                (contextFeatures ??= new List<int>()).Add(f);
            }
        }

        return contextFeatures is null ? ValueTask.CompletedTask : AwaitEnsure(state, contextFeatures, cancellationToken);

        static async ValueTask AwaitEnsure(RequestState state, List<int> features, CancellationToken cancellationToken) =>
            await state.Features.EnsureAsync(CollectionsMarshal.AsSpan(features), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the experiments reaching this position triggers (design 12.6): an assigned experiment is triggered when one
    /// of its arms, together with the request's arms in the other layers, has a node here whose <c>when</c> holds (or that
    /// has none, or is the fallback being invoked). Every arm is looked at, whichever the request has, so the decision is
    /// the same for all of them, control included; the other layers' arms are the request's own, since another layer may
    /// change the node's <c>when</c> or remove it.
    /// </summary>
    private static async ValueTask RecordSiteTriggersAsync(RequestState state, TimelineSlot slot, bool fallback, CancellationToken cancellationToken)
    {
        for (var i = 0; i < slot.SiteExperiments.Length; i++)
        {
            var experiment = slot.SiteExperiments[i];
            if (state.Triggered[experiment] || !IsAssigned(state, experiment))
            {
                continue;
            }

            var info = state.Plan.Experiments[experiment];
            string? untrue = null; // a `when` found false: another arm's node with the same one is not evaluated again
            for (var a = 0; a < info.ArmIndices.Length; a++)
            {
                if (slot.SelectWith(state.Arms, info.LayerIndex, info.ArmIndices[a]) is not { } node)
                {
                    continue; // this arm has no node here
                }

                if (fallback || node.When is not { } when)
                {
                    state.Triggered[experiment] = true;
                    break;
                }

                if (string.Equals(when.Source, untrue, StringComparison.Ordinal))
                {
                    continue;
                }

                await EnsureContextAsync(state, when, cancellationToken).ConfigureAwait(false);
                if (Holds(state, when))
                {
                    state.Triggered[experiment] = true;
                    break;
                }

                untrue = when.Source;
            }
        }
    }

    private static void MarkParamTriggers(RequestState state, CompiledNode node)
    {
        foreach (var experiment in node.ParamExperiments)
        {
            if (IsAssigned(state, experiment))
            {
                state.Triggered[experiment] = true;
            }
        }
    }

    private static bool IsAssigned(RequestState state, int experimentIndex)
    {
        var experiment = state.Plan.Experiments[experimentIndex];
        var arm = state.Arms.ArmIn(experiment.LayerIndex);
        return arm >= 0 && state.Plan.Arms[arm].ExperimentIndex == experimentIndex;
    }

    /// <summary>
    /// Records a position's outcome: in the result, as node metrics (design 13.1: duration and rows by node only, the
    /// category on a counter), in the windowed failure summaries (design 13.2), and on the node span.
    /// </summary>
    private static void Record(RequestState state, CompiledNode node, NodeOutcomeKind category, string? detail, string? producedBy, long started, int rowsIn)
    {
        state.PositionOpen = false;
        var elapsed = Stopwatch.GetElapsedTime(started);
        var rowsOut = state.Table.Rows.Count;
        var outcome = new NodeOutcome(node.NodeId, category, detail, producedBy, elapsed.TotalMilliseconds, rowsIn, rowsOut);
        state.Outcomes.Add(outcome);
        state.Degraded |= category is not (NodeOutcomeKind.Succeeded or NodeOutcomeKind.SkippedWhen);
        state.Recorder?.Position(outcome, NodeOutcomeNames.Name(category));
        if (state.Replay is not null)
        {
            return; // a replay records no metrics, event logs or spans
        }

        var telemetry = state.Plan.Telemetry.Nodes[node.Index];
        StrataRecMetrics.NodeOutcomes.Add(1, telemetry.Outcomes[(int)category]);
        if (!NodeOutcomeNames.IsSkipped(category))
        {
            StrataRecMetrics.NodeDuration.Record(elapsed.TotalSeconds, telemetry.Tags);
            StrataRecMetrics.NodeRowsIn.Record(rowsIn, telemetry.Tags);
            StrataRecMetrics.NodeRowsOut.Record(rowsOut, telemetry.Tags);
        }

        if (category == NodeOutcomeKind.Fallback)
        {
            state.Events?.Report(RequestEvent.NodeFallback, telemetry.Subject, state.RequestId, detail);
        }
        else if (category == NodeOutcomeKind.Failed)
        {
            state.Events?.Report(RequestEvent.NodeFailed, telemetry.Subject, state.RequestId, detail);
        }

        if (state.NodeSpan is { IsAllDataRequested: true } span)
        {
            span.SetTag(StrataRecTags.Outcome, NodeOutcomeNames.Name(category));
            span.SetTag(StrataRecTags.RowsIn, rowsIn);
            span.SetTag(StrataRecTags.RowsOut, rowsOut);
            if (producedBy is not null && producedBy != node.NodeId)
            {
                span.SetTag(StrataRecTags.Fallback, producedBy);
            }

            if (detail is not null)
            {
                span.SetTag(StrataRecTags.OutcomeDetail, detail);
            }

            if (category == NodeOutcomeKind.Failed)
            {
                span.SetStatus(ActivityStatusCode.Error, detail);
            }
        }
    }

    private static Activity? StartNodeSpan(CompiledNode node)
    {
        var span = StrataRecTelemetry.ActivitySource.StartActivity(node.NodeId, ActivityKind.Internal);
        if (span is { IsAllDataRequested: true })
        {
            span.SetTag(StrataRecTags.Node, node.NodeId);
            span.SetTag(StrataRecTags.Variant, node.DefinitionHash);
        }

        return span;
    }

    /// <summary>
    /// Ends the span of <paramref name="position"/>, if one is open, tagging the experiments triggered there (the
    /// position's trigger sites and the parameters its node reads).
    /// </summary>
    private static void EndNodeSpan(RequestState state, int position, string? abortReason)
    {
        if (state.NodeSpan is not { } span)
        {
            return;
        }

        state.NodeSpan = null;
        var slot = state.Plan.Timeline[position];
        var node = state.Variants[position]!;
        if (span.IsAllDataRequested)
        {
            if (abortReason is not null)
            {
                span.SetStatus(ActivityStatusCode.Error, abortReason);
            }

            List<string>? triggered = null;
            AddTriggered(state, slot.SiteExperiments, ref triggered);
            AddTriggered(state, node.ParamExperiments, ref triggered);

            // A fallback that served (or was tried) carries its own variant and experiments: an arm may change only the
            // fallback's definition, and its trigger must show on the position that ran it (design 12.6).
            if (state.PositionFallback is { } fallback)
            {
                span.SetTag(StrataRecTags.FallbackVariant, fallback.DefinitionHash);
                if (state.Plan.Fallbacks.TryGetValue(fallback.NodeId, out var fallbackSlot))
                {
                    AddTriggered(state, fallbackSlot.SiteExperiments, ref triggered);
                }

                AddTriggered(state, fallback.ParamExperiments, ref triggered);
            }

            if (triggered is not null)
            {
                span.SetTag(StrataRecTags.ExperimentsTriggered, triggered.ToArray());
            }
        }

        span.Dispose();

        static void AddTriggered(RequestState state, ImmutableArray<int> experiments, ref List<string>? triggered)
        {
            foreach (var experiment in experiments)
            {
                var id = state.Plan.Experiments[experiment].Id;
                if (state.Triggered[experiment] && (triggered is null || !triggered.Contains(id)))
                {
                    (triggered ??= new List<string>()).Add(id);
                }
            }
        }
    }

    /// <summary>The node whose version output column <paramref name="name"/> reads (<c>col.x@node</c>), or null.</summary>
    private static string? PinnedProducer(OutputPlan output, string name)
    {
        foreach (var (column, read) in output.VersionReads)
        {
            if (column == name)
            {
                return read.Node;
            }
        }

        return null;
    }

    private static async ValueTask<PipelineResult> BuildResultAsync(RequestState state, PipelineRequest request, long start, CancellationToken cancellationToken)
    {
        var plan = state.Plan;
        var output = plan.Output;
        var limitValue = state.Params[output.Limit.Id];
        var limit = limitValue.IsMissing ? 0 : (int)Math.Clamp(limitValue.AsInt64(), 0, int.MaxValue);
        foreach (var experiment in output.ParamExperiments)
        {
            if (IsAssigned(state, experiment))
            {
                state.Triggered[experiment] = true;
            }
        }

        var rows = FirstRows(state.Table, limit);
        var count = rows.Length;

        // Output expressions may read features (design 3.4); make sure they are present for the returned rows, and for
        // those only: no one reads the others, so nothing is fetched or computed for them, and a value of theirs that
        // cannot be served does not fail the request. A feature that failed on a returned row (a source failure, or a
        // non-nullable derived int32 that did not fit) cannot be served as typed: expressions compiled against it skip
        // validity checks. The request fails, as a node reading it would.
        if (count > 0)
        {
            var outputFeatures = new List<int>();
            foreach (var (_, expression) in output.Columns)
            {
                var reads = expression.Reads;
                for (var i = 0; i < reads.Count; i++) // an interface foreach would box the enumerator
                {
                    if (FeatureOf(plan, reads[i]) is >= 0 and var f)
                    {
                        outputFeatures.Add(f);
                    }
                }
            }

            if (outputFeatures.Count > 0)
            {
                var fill = await state.Features.EnsureAsync(CollectionsMarshal.AsSpan(outputFeatures), rows, cancellationToken).ConfigureAwait(false);
                if (fill.Failed)
                {
                    return Failed(plan, request, "output reads a feature that failed: " + fill.Error, start, state.Outcomes, state.Triggered,
                        state.NonFinite + state.Features.NonFiniteReplaced, state.Counters.OutOfRange);
                }
            }
        }

        // The result column by column (ResultColumns): each output column evaluated over the rows into one array; the
        // rows' dictionaries are made only for a caller that reads Items.
        var names = new string[output.Columns.Length];
        var types = new ColumnType[output.Columns.Length];
        var values = new ScalarValue[output.Columns.Length][];
        var producers = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var c = 0; c < output.Columns.Length; c++)
        {
            var (name, expression) = output.Columns[c];
            names[c] = name;
            types[c] = plan.Schema.DeclaredType(expression);
            values[c] = count == 0 ? [] : new ScalarValue[count];
            if (count == 0)
            {
                continue;
            }

            if (MissingRequestColumn(state, expression.Reads) is { } unwritten)
            {
                // The data-flow checks rule it out, as for the output's values (ValuesAsync).
                return Failed(plan, request, $"output column '{name}' reads request column '{unwritten}', which no node wrote", start, state.Outcomes,
                    state.Triggered, state.NonFinite + state.Features.NonFiniteReplaced, state.Counters.OutOfRange);
            }

            if (expression.ColumnReference is { } column && state.ReadColumn(column.FixedSlot, column.Id) is { } storage && storage.Type == types[c])
            {
                // A column is read from its own storage, as a tap reads it: nothing to evaluate, and no buffer of the
                // table's size for the few rows returned (2 MB for a 128-float embedding over 4,096 rows, audit of f47a255).
                for (var i = 0; i < count; i++)
                {
                    values[c][i] = ColumnValues.Get(storage, rows[i]);
                }
            }
            else
            {
                // Evaluated into storage of the declared type, so a float32 column's values print as the floats they are.
                var buffer = ColumnBuffer.Create(types[c], Math.Max(1, state.Table.StorageCapacity), state.Memory);
                expression.Evaluate(state.Eval, rows, buffer);
                for (var i = 0; i < count; i++)
                {
                    values[c][i] = ColumnValues.Get(buffer, rows[i]);
                }
            }

            if (expression.Reads.Count == 1 && expression.Reads[0] is { Kind: SymbolKind.Column, FixedSlot: < 0 } symbol)
            {
                var producer = state.Bindings.Producer(symbol.Id);
                if (producer >= 0)
                {
                    producers[name] = plan.Nodes[producer].NodeId;
                }
            }
            else if (expression.ColumnReference is { FixedSlot: >= 0 } && PinnedProducer(output, name) is { } pinned && TapCapture.Produced(state, pinned))
            {
                producers[name] = pinned; // col.x@node always reads that node's version, as a tap's lineage has it
            }
        }

        var results = ResultValues.Empty;
        if (output.Values.Length > 0)
        {
            var (computed, failure) = await ValuesAsync(state, output, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return Failed(plan, request, failure, start, state.Outcomes, state.Triggered, state.NonFinite + state.Features.NonFiniteReplaced,
                    state.Counters.OutOfRange);
            }

            results = computed!;
        }

        if (state.Batches is not null)
        {
            await TapCapture.OutputAsync(state, rows, cancellationToken).ConfigureAwait(false);
        }

        var ids = new EntityKey[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = state.Table.GetKey(rows[i]);
        }

        return new PipelineResult
        {
            Succeeded = true,
            Columns = new ResultColumns(ids, names, types, values),
            Values = results,
            Outcomes = state.Outcomes,
            AssignedArms = plan.ArmIds(state.Arms),
            TriggeredExperiments = TriggeredIds(plan, state.Triggered),
            ColumnProducers = producers,
            ElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            NonFiniteValues = state.NonFinite + state.Features.NonFiniteReplaced,
            OutOfRangeValues = state.Counters.OutOfRange,
            Degraded = state.Degraded,
        };
    }

    /// <summary>
    /// The output's request-level values, evaluated once after every node: with the context features they read, a failed
    /// one failing the result as a failed feature an output column reads does; a request column as the nodes left it.
    /// </summary>
    private static async ValueTask<(ResultValues? Values, string? Failure)> ValuesAsync(RequestState state, OutputPlan output, CancellationToken cancellationToken)
    {
        var plan = state.Plan;
        List<int>? features = null;
        foreach (var (_, expression) in output.Values)
        {
            var reads = expression.Reads;
            for (var i = 0; i < reads.Count; i++) // an interface foreach would box the enumerator
            {
                if (FeatureOf(plan, reads[i]) is >= 0 and var f)
                {
                    (features ??= new List<int>()).Add(f);
                }
            }
        }

        if (features is not null)
        {
            var fill = await state.Features.EnsureAsync(CollectionsMarshal.AsSpan(features), cancellationToken).ConfigureAwait(false);
            if (fill.Failed)
            {
                return (null, "output reads a feature that failed: " + fill.Error);
            }
        }

        var values = new ResultValue[output.Values.Length];
        for (var v = 0; v < values.Length; v++)
        {
            var (name, expression) = output.Values[v];
            if (MissingRequestColumn(state, expression.Reads) is { } unwritten)
            {
                // The data-flow checks rule it out (a non-nullable one read here has a producer that cannot be skipped).
                return (null, $"output value '{name}' reads request column '{unwritten}', which no node wrote");
            }

            values[v] = new ResultValue(name, plan.Schema.DeclaredType(expression), expression.EvaluateScalar(state.Eval));
        }

        return (ResultValues.OfOutput(values), null);
    }

    /// <summary>
    /// Completes every batch with the request's final record (assignment, triggers, node outcomes) and either returns
    /// them with the result (<see cref="Returns"/>) or hands them to the log pipeline (<see cref="Writes"/>), or neither.
    /// Experiment requests also emit a durable trigger record (design 8.8, 12.6).
    /// </summary>
    private void PublishLogs(RequestState state, PipelineRequest request, PipelineResult result, bool returned)
    {
        // Only a run the engine lets write reaches the persistent logs, also when it is aborted and nobody receives a
        // capture (design 8.6 item 6): forced arms and configurations must not contaminate experiment data.
        var capture = returned && Returns(request);
        if (!capture && !Writes(request))
        {
            return;
        }

        if (state.Batches is null && state.Plan.Experiments.Length == 0)
        {
            return;
        }

        var batches = state.Batches ?? new List<Logging.LogBatch>();
        if (state.Plan.Experiments.Length > 0)
        {
            batches.Add(new Logging.LogBatch
            {
                TapId = "$experiments",
                RequestId = state.RequestId,
                PipelineId = state.Plan.PipelineId,
                CalledBy = state.CalledBy,
                ManifestId = state.Plan.ManifestId,
                Schema = Logging.LogSchema.Experiments,
                TimeMs = state.Context[PlanSchema.RequestTimeContext].AsInt64(),
                Delivery = Logging.LogDelivery.Durable,
                RequestProbability = 1.0,
                Int64EntityIds = Array.Empty<long>(),
                Ranks = Array.Empty<int>(),
                RowProbability = 1.0,
                Columns = Array.Empty<Logging.LogColumn>(),
                Context = Array.Empty<(string, ScalarValue)>(),
            });
        }

        var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var outcome in result.Outcomes)
        {
            outcomes[outcome.NodeId] = NodeOutcomeNames.Name(outcome.Kind); // a fallback that served is "fallback" at the primary
        }

        foreach (var batch in batches)
        {
            batch.AssignedArms = result.AssignedArms;
            batch.TriggeredExperiments = result.TriggeredExperiments;
            batch.NodeOutcomes = outcomes;
        }

        if (capture)
        {
            result.LogBatches = batches;
            return;
        }

        foreach (var batch in batches)
        {
            _logs!.Submit(batch);
        }
    }

    /// <summary>Whether the run's log batches are returned to the caller: captured, or a replay's (compared, never written).</summary>
    private static bool Returns(PipelineRequest request) => request.CaptureLogs || request.Replay is not null;

    /// <summary>
    /// Whether the run's log batches go to the persistent logs: there are some, the engine allows it
    /// (<see cref="PipelineRequest.WriteLogs"/>), and they are not the caller's instead.
    /// </summary>
    private bool Writes(PipelineRequest request) => _logs is not null && request.WriteLogs && !Returns(request);

    /// <summary>Whether anything takes the run's log batches, so that it samples its taps and captures them.</summary>
    private bool ConsumesLogs(PipelineRequest request) => Writes(request) || Returns(request);

    /// <summary>
    /// Where the run's log batches went, as a bundle records it: <c>captured</c> only when they were returned, with the
    /// run's result. An aborted run returns nothing, and one that captures writes nothing either, so its batches went
    /// nowhere (<c>none</c>), though it sampled its taps; it recorded <c>captured</c> (review 79).
    /// </summary>
    /// <param name="returned">The run returns a result (<see cref="PublishLogs"/>'s <c>returned</c>).</param>
    private string LogDestination(PipelineRequest request, bool returned) =>
        returned && request.CaptureLogs ? "captured" : Writes(request) ? "pipeline" : "none";

    /// <summary>Hands a run's recording to <see cref="PipelineRequest.OnRecording"/>; a callback that throws never changes the run's outcome.</summary>
    private static void Deliver(ExecutionPlan plan, PipelineRequest request, ReplayRunRecord run, RequestEventLog? events)
    {
        if (request.OnRecording is not { } deliver)
        {
            return;
        }

        try
        {
            deliver(run);
        }
        catch (Exception ex)
        {
            events?.Report(RequestEvent.RecordingFailed, plan.PipelineId, request.RequestId, ex.Message);
        }
    }

    private static int[] FirstRows(CandidateTable table, int limit)
    {
        var order = table.Rows.Order;
        return order[..Math.Min(limit, order.Length)].ToArray();
    }

    /// <summary>The feature a column or context symbol reads, or -1.</summary>
    internal static int FeatureOf(ExecutionPlan plan, Symbol symbol) => symbol.Kind switch
    {
        SymbolKind.Column => plan.Schema.Columns[symbol.Id].FeatureIndex,
        SymbolKind.Context => plan.Schema.ContextFields[symbol.Id].FeatureIndex,
        _ => -1,
    };

    private static PipelineResult Failed(
        ExecutionPlan plan, PipelineRequest request, string error, long start, IReadOnlyList<NodeOutcome> outcomes, bool[] triggered, int nonFinite = 0,
        long outOfRange = 0) =>
        new()
        {
            Succeeded = false,
            Error = error,
            Columns = ResultColumns.Empty,
            Outcomes = outcomes,
            AssignedArms = plan.ArmIds(request.Arms),
            TriggeredExperiments = TriggeredIds(plan, triggered),
            ColumnProducers = new Dictionary<string, string>(),
            ElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            NonFiniteValues = nonFinite,
            OutOfRangeValues = outOfRange,
        };

    private static List<string> TriggeredIds(ExecutionPlan plan, bool[] triggered)
    {
        var ids = new List<string>();
        for (var i = 0; i < triggered.Length; i++)
        {
            if (triggered[i])
            {
                ids.Add(plan.Experiments[i].Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Validates inputs against <c>request_schema</c> (<see cref="RequestSchema"/>: the rules and words hosts and the engine
    /// use) and fills request-level context values.
    /// </summary>
    private static string? BindInputs(ExecutionPlan plan, PipelineRequest request, ScalarValue[] context)
    {
        if (plan.RequestSchema.Bind(request.Inputs, context) is { } error)
        {
            return RequestSchema.Describe(error);
        }

        context[PlanSchema.RequestIdContext] = ScalarValue.FromString(request.RequestId);
        context[PlanSchema.RequestSeedContext] = ScalarValue.FromInt64(request.Seed ?? PipelineRequest.DefaultSeed(request.RequestId));
        context[PlanSchema.RequestTimeContext] = ScalarValue.FromInt64(request.TimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return null;
    }
}
