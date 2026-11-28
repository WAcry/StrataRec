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

internal sealed class PipelineRequest
{
    public required string RequestId { get; init; }

    public required RequestInputs Inputs { get; init; }

    private RequestArms _arms;

    public RequestArms Arms { get => _arms; init => _arms = value; }

    internal PipelineRequest WithArms(in RequestArms arms)
    {
        var copy = (PipelineRequest)MemberwiseClone();
        copy._arms = arms;
        return copy;
    }

    public required TimeSpan Deadline { get; init; }

    /// <summary>Request seed; defaults to a hash of the request id.</summary>
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
    /// persistent logs: the engine sets it for traffic, and not for a run whose arms or configuration a debugger
    /// forced, whose logs are captured, or that replays. Separate from <see cref="CaptureLogs"/>: a forced run that
    /// captures nothing writes nothing either. False by default.
    /// </summary>
    public bool WriteLogs { get; init; }

    /// <summary>
    /// Record the run for a replay bundle: <see cref="PipelineResult.Recording"/> then holds every external call, node
    /// decision and the sampling.
    /// </summary>
    public bool Record { get; init; }

    /// <summary>
    /// Receives the run's recording on every exit: with the result when the run returns, and before the exception
    /// propagates when it is aborted (cancelled by the caller, or a defect). Setting it records the run. Called once,
    /// on the request's thread; an exception it throws is reported as an event and does not change the run's outcome.
    /// </summary>
    public Action<ReplayRunRecord>? OnRecording { get; init; }

    /// <summary>
    /// Use <see cref="RunReplay.Deadline"/> as <see cref="Deadline"/>.
    /// </summary>
    public RunReplay? Replay { get; init; }

    /// <summary>
    /// Told once when the run and every external call it started have ended (its request memory is returned), which may
    /// be after the run returned: a call it abandoned at a timeout runs on inside its client.
    /// </summary>
    public IRequestEndObserver? OnEnd { get; init; }

    /// <summary>
    /// At most <see cref="ExplainTargets.MaxCount"/>, or the run fails as with invalid inputs; repeated ids are
    /// explained once, and an id of the other key type is never found.
    /// </summary>
    public IReadOnlyList<EntityKey>? ExplainIds { get; init; }

    /// <summary>
    /// Null for a run nothing serves pipelines to: a call then fails as a result.
    /// </summary>
    public IPipelineCalls? Calls { get; init; }

    public string? CalledBy { get; init; }

    /// <summary>
    /// What the cached reads of the request's runs share (<see cref="RequestReads"/>): the same for every run of one
    /// request.
    /// </summary>
    public RequestReads? Reads { get; init; }

    public static long DefaultSeed(string requestId) => unchecked((long)StableHash.Hash(StableHash.SeedFromSalt(requestId)));
}

internal sealed class PipelineResult
{
    public required bool Succeeded { get; init; }

    public string? Error { get; init; }

    public required ResultColumns Columns { get; init; }

    public ResultValues Values { get; init; } = ResultValues.Empty;

    public IReadOnlyList<RankedItem> Items => Columns.Rows;

    public required IReadOnlyList<NodeOutcome> Outcomes { get; init; }

    /// <summary>Assigned arms as <c>experiment/arm</c>.</summary>
    public required IReadOnlyList<string> AssignedArms { get; init; }

    public required IReadOnlyList<string> TriggeredExperiments { get; init; }

    public required IReadOnlyDictionary<string, string> ColumnProducers { get; init; }

    public double ElapsedMs { get; init; }

    /// <summary>Number of non-finite values replaced or masked.</summary>
    public int NonFiniteValues { get; init; }

    /// <summary>
    /// Counted where the value is written, so it includes writes of nodes that later failed.
    /// </summary>
    public long OutOfRangeValues { get; init; }

    /// <summary>
    /// The request succeeded with a node that was degraded, served by its fallback, failed and was skipped, or was skipped
    /// for lack of time or input, or with features its output reads that were answered in part or stale, or that a node's
    /// <c>when</c> reads and that were not answered whole (metric
    /// <c>stratarec.request.degraded</c>).
    /// </summary>
    public bool Degraded { get; init; }

    /// <summary>
    /// A call of a pipeline made in the run was not answered when its node ended, or was answered by a run of which
    /// this is true: the result lacks what that call was for, whatever its node did without it. Not a result for a
    /// cache to keep.
    /// </summary>
    public bool CallsUnanswered { get; init; }

    public IReadOnlyList<Logging.LogBatch> LogBatches { get; set; } = Array.Empty<Logging.LogBatch>();

    public ReplayRunRecord? Recording { get; set; }

    public RunExplanation? Explanation { get; set; }
}

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

    /// <summary>
    /// By position, the fill of the features a <c>when</c> reads when it was not whole: failed, so that the condition
    /// cannot be decided and its node fails, or degraded, so that a node skipped says what it was decided on.
    /// </summary>
    public FeatureFillResult[]? WhenFills { get; set; }

    /// <summary>Selected tap variant per tap slot; null when absent or its group did not sample the request.</summary>
    public CompiledTap?[] Taps { get; set; } = Array.Empty<CompiledTap?>();

    public List<Logging.LogBatch>? Batches { get; set; }

    public long RequestHash => Logging.CoordinatedSampling.RequestHash(RequestId);

    public List<NodeOutcome> Outcomes { get; } = new();

    public int NonFinite { get; set; }

    public bool Degraded { get; set; }

    public volatile bool CallsUnanswered;

    public RequestEventLog? Events { get; init; }

    public bool TraceNodes { get; init; }

    public Activity? NodeSpan { get; set; }

    /// <summary>
    /// The position being run has started and not yet recorded its outcome. An abort (caller cancellation, defect)
    /// records it then.
    /// </summary>
    public bool PositionOpen { get; set; }

    public long PositionStarted { get; set; }

    public int PositionRowsIn { get; set; }

    /// <summary>The fallback node run for the current position, for its span's variant and experiment attribution.</summary>
    public CompiledNode? PositionFallback { get; set; }

    public RunRecorder? Recorder { get; init; }

    public RunReplay? Replay { get; init; }

    public CandidateExplainer? Explainer { get; init; }

    public IPipelineCalls? Calls { get; init; }

    public string? CalledBy { get; init; }

    public RequestReads? Reads { get; init; }

    /// <summary>
    /// Where the batches of the output's taps start in <see cref="Batches"/>, once they fire: a run aborted after that
    /// (the caller cancelled while the result was put together) takes them back, and delivers what its nodes logged.
    /// </summary>
    public int? OutputBatchesFrom { get; set; }

    private Dictionary<string, int>? _pipelineCalls;

    /// <summary>
    /// The place of a node's next pipeline call among those it made in this run, from 0: counted over the run, not over an
    /// attempt, since a fallback node may run for several nodes, and each of its calls needs a place of its own (a seed, a
    /// record a replay finds). Calls made at once from several threads each get one, in no repeatable order.
    /// </summary>
    public int NextPipelineCall(string nodeId) => SkipPipelineCalls(nodeId, 1);

    /// <summary>
    /// Passes over <paramref name="count"/> of the node's places, returning the first: the calls an attempt made that a
    /// replay does not run (one recorded as timed out), so the calls after them find their own records.
    /// </summary>
    public int SkipPipelineCalls(string nodeId, int count)
    {
        lock (this)
        {
            _pipelineCalls ??= new Dictionary<string, int>(StringComparer.Ordinal);
            _pipelineCalls.TryGetValue(nodeId, out var next);
            _pipelineCalls[nodeId] = next + count;
            return next;
        }
    }

    public int PipelineCallsOf(string nodeId)
    {
        lock (this)
        {
            return _pipelineCalls is not null && _pipelineCalls.TryGetValue(nodeId, out var made) ? made : 0;
        }
    }

    public TimeSpan Remaining => Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), DeadlineTimestamp);

    public EvalContext Eval => new(Table, Bindings, Context, Params, Counters);

    public ColumnBuffer? ReadColumn(int fixedSlot, int logicalId)
    {
        var slot = fixedSlot >= 0 ? fixedSlot : Bindings.Resolve(logicalId);
        return slot < 0 ? null : Table.TryGetSlot(slot);
    }

    /// <remarks>A failed attempt's rows are released by the rollback, in every slot, so writing into another node's
    /// version leaves nothing behind.</remarks>
    public int OutputSlot(Operators.Effects effects, in Operators.OutputColumn output) =>
        (effects & Operators.Effects.RowSource) != 0 && Bindings.Resolve(output.LogicalId) is >= 0 and var current ? current : output.Slot;
}

internal sealed class PipelineExecutor
{
    private readonly IBufferPool _pool;
    private readonly Logging.LogPipeline? _logs;
    private readonly RequestEventLog? _events;

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

        if (!plan.Yields.IsEmpty)
        {
            var running = plan.Yields.Resolve(request.Arms);
            if (!running.SameAs(request.Arms))
            {
                request = request.WithArms(running);
            }
        }

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
                // (CandidateTable.Rollback), so the fallback reuses them.
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
            var counters = new ExpressionCounters();
            fetcher = new FeatureFetcher(plan.Features, table, context, bindings, parameters, memory, deadline, counters, request.RequestId, events,
                (IFeatureCallHook?)replay ?? recorder, context[PlanSchema.RequestSeedContext].AsInt64())
            {
                Reads = replay is null ? request.Reads : null,
            };
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
                Reads = replay is null ? request.Reads : null,
            };

            fetcher.StartContextFetch(plan.ContextFeatures.AsSpan(), cancellationToken);
            if (ConsumesLogs(request))
            {
                await TapCapture.SelectAsync(state, cancellationToken).ConfigureAwait(false);
            }

            PipelineResult? result = null;
            for (; position < plan.Timeline.Length && result is null; position++)
            {
                // Before every node: nodes without I/O never look at the token, and a cancelled request must not log
                // rows as served.
                cancellationToken.ThrowIfCancellationRequested();
                var appended = table.AppendedRows;
                var error = await RunPositionAsync(state, position, cancellationToken).ConfigureAwait(false);
                if (rowLimits[position] > 0)
                {
                    table.ReleaseRowBudget(rowLimits[position] - (table.AppendedRows - appended));
                }

                EndNodeSpan(state, position, null);
                if (error is not null)
                {
                    result = Failed(plan, request, error, start, state.Outcomes, state.Triggered, state.NonFinite, state.Counters.OutOfRange);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            result ??= await BuildResultAsync(state, request, start, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); // a result built while the caller cancelled (a fetch it cut short reads as failed) is nobody's
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
            // been exposed, and may have captured taps. Request memory is still held here.
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
            if (state.OutputBatchesFrom is { } from && state.Batches is { } batches && batches.Count > from)
            {
                batches.RemoveRange(from, batches.Count - from);
            }

            var aborted = Failed(plan, request, reason, start, state.Outcomes, state.Triggered, state.NonFinite, state.Counters.OutOfRange);
            if (recorder is not null)
            {
                try
                {
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
            // still running now outlive the request.
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

    private static long RowLimit(CompiledNode node, ReadOnlySpan<ScalarValue> parameters) =>
        (node.Effects & Effects.RowSource) != 0 ? Math.Max(0, node.Operator.GetRowLimit(parameters)) : 0;

    private static async ValueTask<string?> RunPositionAsync(RequestState state, int position, CancellationToken cancellationToken)
    {
        var slot = state.Plan.Timeline[position];
        var node = state.Variants[position];
        await RecordSiteTriggersAsync(state, slot, fallback: false, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return null;
        }

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
            var why = (state.WhenFills?[position] ?? default) switch
            {
                { State: FeatureState.Degraded } fill => "when is false, on degraded features: " + fill.Error,
                { State: FeatureState.Failed } fill => "when is false, on features that failed: " + fill.Error,
                _ => "when is false",
            };
            Record(state, node, NodeOutcomeKind.SkippedWhen, why, null, started, rowsIn);
            state.Explainer?.Skipped(node.NodeId, why);
            return null;
        }

        if (node.MinRemainingMs is not null || state.Recorder is not null)
        {
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

        switch (node.OnFailure)
        {
            case FailurePolicy.Skip:
                Record(state, node, NodeOutcomeKind.Failed, attempt.Detail + " (skipped)", null, started, rowsIn);
                return null;
            case FailurePolicy.Fallback when state.Plan.SelectFallback(node, state.Arms) is { } fallback:
                // A fallback site triggers only when the fallback is invoked, which is independent of the arm.
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

        // What the node reads but waits for itself (a recall source's query) is not filled here.
        var fill = await state.Features.EnsureAsync(node.FilledFeatures.AsSpan(), cancellationToken).ConfigureAwait(false);
        if (fill.Failed)
        {
            return new Attempt(AttemptKind.Failed, "feature fetch failed: " + fill.Error);
        }

        var remainingMs = state.Remaining.TotalMilliseconds - reserveMs;
        var budgetMs = node.TimeoutMs is { } timeout ? Math.Min(timeout, remainingMs) : remainingMs;
        if (state.Replay is { } replay)
        {
            var recorded = replay.NextAttempt(node.NodeId);
            if (recorded?.Timing == ReplayTiming.DeadlineExceeded)
            {
                state.Recorder?.Attempt(node.NodeId, ReplayTiming.DeadlineExceeded, recorded.BudgetMs ?? budgetMs);
                return new Attempt(AttemptKind.Failed, "deadline exceeded");
            }

            if (recorded?.Timing == ReplayTiming.TimedOut)
            {
                MarkParamTriggers(state, node);
                if (recorded.PipelineCalls > 0)
                {
                    state.SkipPipelineCalls(node.NodeId, recorded.PipelineCalls);
                }

                state.Recorder?.Attempt(node.NodeId, ReplayTiming.TimedOut, recorded.BudgetMs ?? budgetMs, recorded.PipelineCalls);
                return new Attempt(AttemptKind.Failed, $"timed out after {recorded.BudgetMs ?? budgetMs:0} ms");
            }
        }
        else if (budgetMs <= 0)
        {
            state.Recorder?.Attempt(node.NodeId, ReplayTiming.DeadlineExceeded, budgetMs);
            return new Attempt(AttemptKind.Failed, "deadline exceeded");
        }

        // A failed attempt leaves the table exactly as it was: the ranking, no rows and no slots of its own, so nothing
        // that runs or reads later (the fallback, a pinned col.x@node, the output) sees what it wrote.
        var checkpoint = state.Table.Checkpoint();

        try
        {
            // The attempt's absolute deadline. The context hands the operator a token cancelled at it and bounds every
            // external wait by it, whether or not the client honours the token.
            var deadline = state.Replay is null
                ? Stopwatch.GetTimestamp() + (long)(Math.Min(budgetMs, AttemptBudget.MaxWaitMs) * Stopwatch.Frequency / 1000)
                : long.MaxValue;
            var budget = new AttemptBudget(cancellationToken, deadline, state.Events, state.RequestId);
            var context = new OperatorContext(state, node.NodeId, node.Effects, budget, node.RequestOutputs);
            if ((node.Effects & Operators.Effects.RowSource) == 0)
            {
                // A node that already ran in this request (a fallback node shared by several nodes) has a version in its
                // slots: this attempt writes another, and the earlier one stays until it commits (OperatorContext.Output).
                // A row source's slots are shared, and it writes only the rows it appends, which a rollback releases.
                foreach (var output in node.Outputs)
                {
                    if (state.Table.TryGetSlot(output.Slot) is not null)
                    {
                        context.Rewrites(output.Slot);
                    }
                }
            }

            var callsBefore = node.Calls.IsEmpty ? 0 : state.PipelineCallsOf(node.NodeId);
            string? failure;
            var timedOut = false;
            try
            {
                // The operator is about to read its parameters: that is exposure, whether it then succeeds or fails.
                MarkParamTriggers(state, node);
                await node.Operator.ExecuteAsync(context).ConfigureAwait(false);
                if (budget.RanOut)
                {
                    // The operator itself is awaited, never abandoned: it changes the table the fallback works on.
                    timedOut = true;
                    failure = $"timed out after {budgetMs:0} ms";
                }
                else if ((failure = context.FailureReason) is null)
                {
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

            var calls = node.Calls.IsEmpty ? 0 : state.PipelineCallsOf(node.NodeId) - callsBefore;
            state.Recorder?.Attempt(node.NodeId, timedOut ? ReplayTiming.TimedOut : ReplayTiming.Ran, budgetMs, calls);
            if (calls > context.CallsAnswered)
            {
                // Judged here, when the node ends, whatever became of the call and whatever the operator did about it:
                // a failed result it went on without, an exception it caught, a call it did not wait for.
                state.CallsUnanswered = true;
            }

            if (failure is not null)
            {
                state.Table.Rollback(checkpoint);
                return new Attempt(AttemptKind.Failed, failure);
            }

            var degraded = context.DegradedReason ?? (fill.State == FeatureState.Degraded ? "degraded features: " + fill.Error : null);
            return new Attempt(degraded is null ? AttemptKind.Succeeded : AttemptKind.Degraded, degraded);
        }
        finally
        {
            state.Table.Discard(checkpoint);
        }
    }

    private static string ExceptionName(Exception ex) => ex.GetType().Name;

    private static string? Commit(RequestState state, CompiledNode node, OperatorContext context)
    {
        var table = state.Table;
        var live = table.Rows.Order;
        for (var o = 0; o < node.Outputs.Length; o++)
        {
            var output = node.Outputs[o];
            var column = state.Plan.Schema.Columns[output.LogicalId];

            // A node that never asked for its buffer wrote nothing: the new version has every row missing.
            var buffer = context.Output(output);
            if (ColumnValues.IsFloat(buffer)
                && ColumnValues.SanitizeNonFinite(buffer, live, column.HasDefault ? column.Default : ScalarValue.Missing) is > 0 and var replaced)
            {
                state.NonFinite += replaced;
                if (state.Replay is null)
                {
                    StrataRecMetrics.ColumnNonFinite.Add(replaced, state.Plan.Telemetry.Nodes[node.Index].Outputs[o]);
                }
            }

            if (!output.Type.Nullable && table.CountMissingLiveRows(buffer) is > 0 and var missing)
            {
                return $"left non-nullable column '{output.Name}' without a value on {missing} of {live.Length} live rows";
            }
        }

        if (node.RequestOutputs.Length > 0 && CommitRequestColumns(state, node, context) is { } failure)
        {
            return failure;
        }

        context.PublishRewrites();
        foreach (var output in node.Outputs)
        {
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
    /// The first non-nullable request column among <paramref name="reads"/> (context ids) that no node has written in
    /// this request, or null: what a node, condition or output value reading it cannot be evaluated without. The
    /// data-flow checks rule it out for every arm combination.
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
            state.Explainer?.Compacted();
        }

        if (node.FetchBarrier)
        {
            await PrefetchSegmentAsync(state, position, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Nodes already known to be skipped by <c>when</c> are left out; a <c>when</c> that reads a request column is not
    /// known yet (the nodes before its own may still write it), so its node's features are prefetched.
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

        var fill = await EnsureContextAsync(state, node.When, cancellationToken).ConfigureAwait(false);
        if (fill.State is FeatureState.Degraded or FeatureState.Failed)
        {
            // The run is degraded, as it is when a node or the output reads such a feature: a recall skipped because of
            // what the user's features could not say is not a result computed whole.
            (state.WhenFills ??= new FeatureFillResult[state.WhenResults.Length])[position] = fill;
            state.Degraded = true;
        }

        var result = Reaches(state, node.When, fill);
        state.WhenResults[position] = result;
        return result;
    }

    /// <summary>
    /// A feature whose source failed is missing where it can be, and the condition is decided on that, as it is on any
    /// missing value. One that cannot be missing has no value to decide on: the node is reached, as one whose condition
    /// holds, and fails by its own fill of that feature.
    /// </summary>
    private static bool Reaches(RequestState state, CompiledExpression when, in FeatureFillResult fill)
    {
        if (fill.Failed)
        {
            var reads = when.Reads;
            for (var i = 0; i < reads.Count; i++) // an interface foreach would box the enumerator
            {
                if (reads[i].Kind == SymbolKind.Context && state.Plan.Schema.ContextFields[reads[i].Id] is { FeatureIndex: >= 0 and var f, Type.Nullable: false }
                    && state.Features.GetState(f) == FeatureState.Failed)
                {
                    return true;
                }
            }
        }

        return Holds(state, when);
    }

    private static bool Holds(RequestState state, CompiledExpression when)
    {
        if (MissingRequestColumn(state, when.Reads) is not null)
        {
            return false;
        }

        var value = when.EvaluateScalar(state.Eval);
        return !value.IsMissing && value.AsBool();
    }

    /// <summary>
    /// `when` and site conditions read request-level values only; make sure the context features they read are present.
    /// </summary>
    private static ValueTask<FeatureFillResult> EnsureContextAsync(RequestState state, CompiledExpression expression, CancellationToken cancellationToken)
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

        return contextFeatures is null ? new ValueTask<FeatureFillResult>(new FeatureFillResult(FeatureState.Ok)) : AwaitEnsure(state, contextFeatures, cancellationToken);

        static ValueTask<FeatureFillResult> AwaitEnsure(RequestState state, List<int> features, CancellationToken cancellationToken) =>
            state.Features.EnsureAsync(CollectionsMarshal.AsSpan(features), cancellationToken);
    }

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
            string? untrue = null;
            for (var a = 0; a < info.ArmIndices.Length; a++)
            {
                if (slot.SelectWith(state.Arms, info.LayerIndex, info.ArmIndices[a]) is not { } node)
                {
                    continue;
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

                // Looking at an arm the request does not have says nothing of the run: only a node the run reaches
                // marks it degraded (EvaluateWhenAsync).
                var fill = await EnsureContextAsync(state, when, cancellationToken).ConfigureAwait(false);
                if (Reaches(state, when, fill))
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
            return;
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
            // fallback's definition, and its trigger must show on the position that ran it.
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

    private static Symbol? OnlyColumn(IReadOnlyList<Symbol> reads)
    {
        Symbol? only = null;
        for (var i = 0; i < reads.Count; i++)
        {
            if (reads[i].Kind == SymbolKind.Column)
            {
                if (only is not null)
                {
                    return null;
                }

                only = reads[i];
            }
        }

        return only;
    }

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

        // Output expressions may read features; make sure they are present for the returned rows, and for those only:
        // no one reads the others, so nothing is fetched or computed for them, and a value of theirs that cannot be
        // served does not fail the request. A feature that failed on a returned row cannot be served as typed:
        // expressions compiled against it skip validity checks.
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

                state.Degraded |= fill.State == FeatureState.Degraded;
            }
        }

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
                return Failed(plan, request, $"output column '{name}' reads request column '{unwritten}', which no node wrote", start, state.Outcomes,
                    state.Triggered, state.NonFinite + state.Features.NonFiniteReplaced, state.Counters.OutOfRange);
            }

            // The same storage whatever the nullability says: a node's version of a column (col.x@node) is nullable where
            // the column is not, and a row without a value reads as missing either way.
            if (expression.ColumnReference is { } column && state.ReadColumn(column.FixedSlot, column.Id) is { } storage
                && storage.Type.AsNullable() == types[c].AsNullable())
            {
                // A column is read from its own storage, as a tap reads it: nothing to evaluate, and no buffer of the
                // table's size for the few rows returned.
                for (var i = 0; i < count; i++)
                {
                    values[c][i] = ColumnValues.Get(storage, rows[i]);
                }
            }
            else
            {
                var buffer = ColumnBuffer.Create(types[c], Math.Max(1, state.Table.StorageCapacity), state.Memory);
                expression.Evaluate(state.Eval, rows, buffer);
                for (var i = 0; i < count; i++)
                {
                    values[c][i] = ColumnValues.Get(buffer, rows[i]);
                }
            }

            if (OnlyColumn(expression.Reads) is { } symbol)
            {
                if (symbol.FixedSlot < 0)
                {
                    var producer = state.Bindings.Producer(symbol.Id);
                    if (producer >= 0)
                    {
                        producers[name] = plan.Nodes[producer].NodeId;
                    }
                }
                else if (PinnedProducer(output, name) is { } pinned && TapCapture.Produced(state, pinned))
                {
                    producers[name] = pinned;
                }
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

        // A cancellation that lands later, while the taps fire or the result is put together, is seen where the result
        // is handed over, and the abort takes back what the output's taps captured: it delivers what the nodes logged,
        // never an exposure nobody received.
        cancellationToken.ThrowIfCancellationRequested();
        if (state.Batches is { } captured)
        {
            state.OutputBatchesFrom = captured.Count;
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
            CallsUnanswered = state.CallsUnanswered,
        };
    }

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

            state.Degraded |= fill.State == FeatureState.Degraded;
        }

        var values = new ResultValue[output.Values.Length];
        for (var v = 0; v < values.Length; v++)
        {
            var (name, expression) = output.Values[v];
            if (MissingRequestColumn(state, expression.Reads) is { } unwritten)
            {
                return (null, $"output value '{name}' reads request column '{unwritten}', which no node wrote");
            }

            values[v] = new ResultValue(name, plan.Schema.DeclaredType(expression), expression.EvaluateScalar(state.Eval));
        }

        return (ResultValues.OfOutput(values), null);
    }

    private void PublishLogs(RequestState state, PipelineRequest request, PipelineResult result, bool returned)
    {
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
            outcomes[outcome.NodeId] = NodeOutcomeNames.Name(outcome.Kind);
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

    /// <summary>
    /// The <c>$experiments</c> record of a run that did not happen because a cache answered the call of its pipeline
    /// (<see cref="Operators.PipelineCallCache"/>). It carries where the result comes from and no node outcomes:
    /// nothing ran. The run's other taps wrote under the request that computed it.
    /// </summary>
    /// <returns>The batch when captured; null when it was written, or goes nowhere.</returns>
    internal Logging.LogBatch? PublishCachedExposure(
        string requestId, long timeMs, string calledBy, Operators.CallExposure exposure, Operators.CachedFrom from, bool capture, bool write)
    {
        if (!capture && !(write && _logs is not null))
        {
            return null;
        }

        var batch = new Logging.LogBatch
        {
            TapId = "$experiments",
            RequestId = requestId,
            PipelineId = exposure.PipelineId,
            CalledBy = calledBy,
            ManifestId = exposure.ManifestId,
            CachedFromRequestId = from.RequestId,
            CachedFromTimeMs = from.TimeMs,
            CachedFromCalledBy = from.CalledBy,
            Schema = Logging.LogSchema.Experiments,
            TimeMs = timeMs,
            Delivery = Logging.LogDelivery.Durable,
            RequestProbability = 1.0,
            Int64EntityIds = Array.Empty<long>(),
            Ranks = Array.Empty<int>(),
            RowProbability = 1.0,
            Columns = Array.Empty<Logging.LogColumn>(),
            Context = Array.Empty<(string, ScalarValue)>(),
            AssignedArms = exposure.Arms,
            TriggeredExperiments = exposure.Triggered,
        };
        if (capture)
        {
            return batch;
        }

        _logs!.Submit(batch);
        return null;
    }

    private static bool Returns(PipelineRequest request) => request.CaptureLogs || request.Replay is not null;

    private bool Writes(PipelineRequest request) => _logs is not null && request.WriteLogs && !Returns(request);

    private bool ConsumesLogs(PipelineRequest request) => Writes(request) || Returns(request);

    /// <summary>
    /// Where the run's log batches went, as a bundle records it: <c>captured</c> only when they were returned, with the
    /// run's result. An aborted run returns nothing, and one that captures writes nothing either, so its batches went
    /// nowhere (<c>none</c>), though it sampled its taps.
    /// </summary>
    private string LogDestination(PipelineRequest request, bool returned) =>
        returned && request.CaptureLogs ? "captured" : Writes(request) ? "pipeline" : "none";

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
