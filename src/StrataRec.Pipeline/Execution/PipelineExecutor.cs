using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Runtime;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Pipeline.Execution;

/// <summary>One pipeline execution request, already validated and assigned by the host layer.</summary>
public sealed class PipelineRequest
{
    public required string RequestId { get; init; }

    public required RequestInputs Inputs { get; init; }

    public RequestArms Arms { get; init; }

    public required TimeSpan Deadline { get; init; }

    /// <summary>Request seed; defaults to a hash of the request id (design 3.4).</summary>
    public long? Seed { get; init; }

    /// <summary>Request time in Unix milliseconds; defaults to now.</summary>
    public long? TimeMs { get; init; }

    /// <summary>
    /// Return log batches in <see cref="PipelineResult.LogBatches"/> instead of sending them to the log pipeline: draft
    /// previews, replays and local runs never write persistent logs (design 8.6 item 6).
    /// </summary>
    public bool CaptureLogs { get; init; }

    /// <summary>
    /// Record the run for a replay bundle (design 13.4): <see cref="PipelineResult.Recording"/> then holds every external
    /// call, node decision and the sampling. Requests that do not record pay nothing for the feature but a null check per
    /// external call and node.
    /// </summary>
    public bool Record { get; init; }

    /// <summary>
    /// Receives the run's recording on every exit (design 13.4): with the result when the run returns, and before the
    /// exception propagates when it is aborted (cancelled by the caller, or a defect), so an aborted run can be replayed
    /// too. Setting it records the run. Called once, on the request's thread; an exception it throws is reported as an
    /// event and does not change the run's outcome.
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

    /// <summary>The seed of a request that does not set one: a hash of its id (design 3.4).</summary>
    public static long DefaultSeed(string requestId) => unchecked((long)StableHash.Hash(StableHash.SeedFromSalt(requestId)));
}

public enum NodeOutcomeKind : byte
{
    Succeeded = 1,
    Degraded,
    Skipped,
    Failed,
}

/// <summary>What happened at one timeline position (design 5.6).</summary>
/// <param name="NodeId">Primary node id at the position.</param>
/// <param name="Kind">Final outcome of the position.</param>
/// <param name="Detail">Reason for skip/failure/degradation.</param>
/// <param name="ProducedBy">Node that produced the outputs (the fallback when it ran), or null.</param>
/// <param name="DurationMs">Wall time spent at the position.</param>
/// <param name="RowsIn">Live rows before the node.</param>
/// <param name="RowsOut">Live rows after the node.</param>
public readonly record struct NodeOutcome(string NodeId, NodeOutcomeKind Kind, string? Detail, string? ProducedBy, double DurationMs, int RowsIn, int RowsOut);

/// <summary>A returned result row.</summary>
public sealed record RankedItem(EntityKey Id, int Rank, IReadOnlyDictionary<string, ScalarValue> Columns);

/// <summary>Result of one pipeline execution.</summary>
public sealed class PipelineResult
{
    public required bool Succeeded { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<RankedItem> Items { get; init; }

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

    /// <summary>
    /// <see cref="Triggered"/> as the current position started; null unless the request records. An aborted position
    /// records the difference, the exposure it caused before the abort.
    /// </summary>
    public bool[]? TriggeredAtPosition { get; init; }

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

    public TimeSpan Remaining => Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), DeadlineTimestamp);

    public EvalContext Eval => new(Table, Bindings, Context, Params, Counters);
}

/// <summary>
/// Executes an <see cref="ExecutionPlan"/> for one request (design 3.3): walk the timeline in order, pick each
/// position's variant, make its features available, run it with its time budget, and commit outputs only on success.
/// CPU work runs on the request's logical thread; concurrency comes from overlapping I/O (design 5.4).
/// </summary>
/// <remarks>
/// Observability (design 13): node, source, model, column and memory metrics go to <see cref="StrataRecMetrics"/>.
/// Node and I/O spans are created only under a recorded request span (<see cref="StrataRecTracing.IsRecording"/>),
/// which <c>RecEngine</c> starts; request-level metrics are recorded there too.
/// </remarks>
public sealed class PipelineExecutor
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
        var memory = new RequestMemory(_pool);
        FeatureFetcher? fetcher = null;
        RequestState? state = null;
        var position = 0;

        // A replay is not traffic: no metrics, event logs or spans (its timings are not real, and the recorded request
        // was already counted), and never a persistent log (design 8.6 item 6).
        var replay = request.Replay;
        // A replay that records keeps its node decisions, parameters and result (its calls are answered, not recorded),
        // which is what a replay report compares with the original run.
        var recorder = request.Record || request.OnRecording is not null ? new RunRecorder() : null;
        var events = replay is null ? _events : null;
        try
        {
            var context = new ScalarValue[plan.Schema.ContextFields.Count];
            var inputError = BindInputs(plan, request, context);
            if (inputError is not null)
            {
                var rejected = Failed(plan, request, inputError, start, Array.Empty<NodeOutcome>(), Array.Empty<bool>());
                if (recorder is not null)
                {
                    rejected.Recording = recorder.Build(plan, request, null, rejected, LogDestination(request));
                    Deliver(plan, request, rejected.Recording, events);
                }

                return rejected;
            }

            var parameters = plan.Params.Snapshot(request.Arms);
            var variants = new CompiledNode?[plan.Timeline.Length];
            long capacity = 0;
            for (var i = 0; i < variants.Length; i++)
            {
                var node = plan.Timeline[i].Select(request.Arms);
                variants[i] = node;
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

                capacity += limit;
            }

            var table = new CandidateTable(plan.TableSchema, (int)Math.Min(capacity, int.MaxValue), memory);
            var bindings = new OutputBindings(plan.Schema.Columns.Count, memory);
            var deadline = start + (long)(request.Deadline.TotalSeconds * Stopwatch.Frequency);
            // One set of counters per request: operator expressions and derived features count into the same totals.
            var counters = new ExpressionCounters();
            fetcher = new FeatureFetcher(plan.Features, table, context, bindings, parameters, memory, deadline, counters, request.RequestId, events,
                (IFeatureCallHook?)replay ?? recorder);
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
                TriggeredAtPosition = recorder is null ? null : new bool[plan.Experiments.Length],
                WhenResults = new bool?[plan.Timeline.Length],
                Events = events,
                TraceNodes = replay is null && StrataRecTracing.IsRecording,
                Recorder = recorder,
                Replay = replay,
            };

            fetcher.StartContextFetch(plan.ContextFeatures, cancellationToken);
            if (_logs is not null || request.CaptureLogs || replay is not null)
            {
                // Without a consumer every captured batch would be discarded: skip sampling and capture entirely.
                await TapCapture.SelectAsync(state, cancellationToken).ConfigureAwait(false);
            }

            PipelineResult? result = null;
            for (; position < plan.Timeline.Length && result is null; position++)
            {
                var error = await RunPositionAsync(state, position, cancellationToken).ConfigureAwait(false);
                EndNodeSpan(state, position, null);
                if (error is not null)
                {
                    result = Failed(plan, request, error, start, state.Outcomes, state.Triggered, state.NonFinite, state.Counters.OutOfRange);
                }
            }

            if (result is null && replay?.AbortReason is { } recordedAbort)
            {
                // The recording was aborted after its last position: it has no result to reproduce.
                throw new ReplayAbortedException(recordedAbort);
            }

            result ??= await BuildResultAsync(state, request, start, cancellationToken).ConfigureAwait(false);
            if (recorder is not null)
            {
                result.Recording = recorder.Build(plan, request, parameters, result, LogDestination(request));
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
                ReplayAbortedException => ex.Message, // the recorded reason
                OperationCanceledException => "cancelled by the caller",
                _ => "aborted: " + ex.Message,
            };
            if (state.PositionOpen && position < state.Variants.Length && state.Variants[position] is { } open)
            {
                if (state.TriggeredAtPosition is { } before)
                {
                    // The exposure this position caused before the abort: a replay cannot run it again (design 12.6, 13.4).
                    recorder?.AbortedTriggers(NewlyTriggered(plan, before, state.Triggered));
                }

                Record(state, open, NodeOutcomeCategory.Aborted, reason, null, state.PositionStarted, state.PositionRowsIn);
            }

            EndNodeSpan(state, position, reason);
            var aborted = Failed(plan, request, reason, start, state.Outcomes, state.Triggered, state.NonFinite, state.Counters.OutOfRange);
            if (recorder is not null)
            {
                try
                {
                    // A snapshot, taken while the request still holds its memory; calls in flight stay outstanding in it.
                    Deliver(plan, request, recorder.Build(plan, request, state.Params, aborted, LogDestination(request), aborted: true), events);
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
        if (state.TriggeredAtPosition is { } before)
        {
            // Only a recorded request: what this position triggers is recorded if the request is aborted while it runs.
            Array.Copy(state.Triggered, before, before.Length);
        }

        if (node is not null && state.Replay is { AbortReason: { } recordedAbort } replay && replay.AbortsAt(node.NodeId, out var running))
        {
            // The recording was aborted before this position finished: it holds no decision to reproduce here or later.
            // A node that was running then is recorded as aborted again, with the exposure it had caused by then: a partly
            // run position cannot be replayed, so the recorded triggers are the facts. One never reached is not recorded.
            if (running)
            {
                state.PositionOpen = true;
                state.PositionStarted = Stopwatch.GetTimestamp();
                state.PositionRowsIn = state.Table.Rows.Count;
                MarkRecordedTriggers(state, replay.AbortedTriggers);
            }

            throw new ReplayAbortedException(recordedAbort);
        }

        await RecordSiteTriggersAsync(state, slot, cancellationToken).ConfigureAwait(false);
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
            Record(state, node, NodeOutcomeCategory.SkippedWhen, "when is false", null, started, rowsIn);
            return null;
        }

        if (node.MinRemainingMs is not null || state.Recorder is not null)
        {
            // A replay compares with the time that was left in the recording, not with its own clock (design 5.7 item 5).
            var remainingMs = state.Replay?.RemainingMs(node.NodeId) ?? state.Remaining.TotalMilliseconds;
            state.Recorder?.Remaining(remainingMs);
            if (node.MinRemainingMs is { } minRemaining && remainingMs < minRemaining)
            {
                Record(state, node, NodeOutcomeCategory.SkippedDeadline, $"less than {minRemaining} ms remaining", null, started, rowsIn);
                return null;
            }
        }

        int[]? entered = null;
        if (state.Batches is not null && TapCapture.NeedsEnteredRows(state, node.NodeId))
        {
            entered = state.Memory.Rent<int>(Math.Max(1, rowsIn));
            state.Table.Rows.Capture(entered);
        }

        var reserve = node.OnFailure == FailurePolicy.Fallback ? node.FallbackReserveMs : 0;
        var attempt = await RunNodeAsync(state, node, reserve, cancellationToken).ConfigureAwait(false);
        if (attempt.Kind is NodeOutcomeKind.Succeeded or NodeOutcomeKind.Degraded)
        {
            var category = attempt.Kind == NodeOutcomeKind.Succeeded ? NodeOutcomeCategory.Succeeded : NodeOutcomeCategory.Degraded;
            Record(state, node, category, attempt.Detail, node.NodeId, started, rowsIn);
            if (state.Batches is not null)
            {
                await TapCapture.AfterNodeAsync(state, node.NodeId, entered, rowsIn, cancellationToken).ConfigureAwait(false);
            }

            await AfterNodeAsync(state, position, node, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (attempt.Kind == NodeOutcomeKind.Skipped)
        {
            Record(state, node, NodeOutcomeCategory.SkippedInput, attempt.Detail, null, started, rowsIn);
            return null;
        }

        // Failed: apply on_failure (design 5.6).
        switch (node.OnFailure)
        {
            case FailurePolicy.Skip:
                Record(state, node, NodeOutcomeCategory.Failed, attempt.Detail + " (skipped)", null, started, rowsIn);
                return null;
            case FailurePolicy.Fallback when state.Plan.SelectFallback(node, state.Arms) is { } fallback:
                // A fallback site triggers only when the fallback is invoked, which is independent of the arm (design 12.6).
                state.PositionFallback = fallback;
                if (state.Plan.Fallbacks.TryGetValue(fallback.NodeId, out var fallbackSlot))
                {
                    await RecordSiteTriggersAsync(state, fallbackSlot, cancellationToken).ConfigureAwait(false);
                }

                var fb = await RunNodeAsync(state, fallback, 0, cancellationToken).ConfigureAwait(false);
                if (fb.Kind is NodeOutcomeKind.Succeeded or NodeOutcomeKind.Degraded)
                {
                    Record(state, node, NodeOutcomeCategory.Fallback, $"{attempt.Detail}; served by fallback '{fallback.NodeId}'", fallback.NodeId, started, rowsIn);
                    if (state.Batches is not null)
                    {
                        await TapCapture.AfterNodeAsync(state, node.NodeId, entered, rowsIn, cancellationToken).ConfigureAwait(false);
                    }

                    await AfterNodeAsync(state, position, fallback, cancellationToken).ConfigureAwait(false);
                    return null;
                }

                if (fallback.OnFailure == FailurePolicy.Skip || fb.Kind == NodeOutcomeKind.Skipped)
                {
                    Record(state, node, NodeOutcomeCategory.Failed, $"{attempt.Detail}; fallback '{fallback.NodeId}': {fb.Detail} (skipped)", null, started, rowsIn);
                    return null;
                }

                Record(state, node, NodeOutcomeCategory.Failed, $"{attempt.Detail}; fallback '{fallback.NodeId}': {fb.Detail}", null, started, rowsIn);
                return $"node '{node.NodeId}' failed ({attempt.Detail}) and its fallback '{fallback.NodeId}' failed ({fb.Detail})";
            default:
                Record(state, node, NodeOutcomeCategory.Failed, attempt.Detail, null, started, rowsIn);
                return $"node '{node.NodeId}' failed: {attempt.Detail}";
        }
    }

    private readonly record struct Attempt(NodeOutcomeKind Kind, string? Detail);

    private static async ValueTask<Attempt> RunNodeAsync(RequestState state, CompiledNode node, int reserveMs, CancellationToken cancellationToken)
    {
        // Runtime protection (design 11.5 step 4): a required input with no producer skips the node.
        foreach (var logical in node.RequiredOutputs)
        {
            if (!state.Bindings.IsProduced(logical))
            {
                return new Attempt(NodeOutcomeKind.Skipped, $"input column '{state.Plan.Schema.Columns[logical].Name}' was not produced");
            }
        }

        var fill = await state.Features.EnsureAsync(node.ReadFeatures, cancellationToken).ConfigureAwait(false);
        if (fill.Failed)
        {
            return new Attempt(NodeOutcomeKind.Failed, "feature fetch failed: " + fill.Error);
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
                return new Attempt(NodeOutcomeKind.Failed, "deadline exceeded");
            }

            if (recorded?.Timing == ReplayTiming.TimedOut)
            {
                MarkParamTriggers(state, node);
                state.Recorder?.Attempt(node.NodeId, ReplayTiming.TimedOut, recorded.BudgetMs ?? budgetMs);
                return new Attempt(NodeOutcomeKind.Failed, $"timed out after {recorded.BudgetMs ?? budgetMs:0} ms");
            }
        }
        else if (budgetMs <= 0)
        {
            state.Recorder?.Attempt(node.NodeId, ReplayTiming.DeadlineExceeded, budgetMs);
            return new Attempt(NodeOutcomeKind.Failed, "deadline exceeded");
        }

        // A failed attempt leaves the table exactly as it was (design 4.6): the ranking, no rows and no slots of its own, so
        // nothing that runs or reads later (the fallback, a pinned col.x@node, the output) sees what it wrote, just as in a
        // replay, which skips a recorded timeout instead of running it.
        var checkpoint = state.Table.Checkpoint();

        // The attempt's absolute deadline. The context hands the operator a token cancelled at it and bounds every external
        // wait by it, whether or not the client honours the token. A replay takes timing from its recording instead.
        var deadline = state.Replay is null
            ? Stopwatch.GetTimestamp() + (long)(Math.Min(budgetMs, AttemptBudget.MaxWaitMs) * Stopwatch.Frequency / 1000)
            : long.MaxValue;
        var budget = new AttemptBudget(cancellationToken, deadline);
        var context = new OperatorContext(state, node.NodeId, node.Effects, budget);
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
            else
            {
                failure = context.FailureReason ?? Commit(state, node);
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
            return new Attempt(NodeOutcomeKind.Failed, failure);
        }

        var degraded = context.DegradedReason ?? (fill.State == FeatureState.Degraded ? "degraded features: " + fill.Error : null);
        return new Attempt(degraded is null ? NodeOutcomeKind.Succeeded : NodeOutcomeKind.Degraded, degraded);
    }

    /// <summary>The exception's type name for a failure detail; a replayed exception that could not be recreated keeps the recorded name.</summary>
    private static string ExceptionName(Exception ex)
    {
        if (ex is ReplayedCallException { OriginalType: { } original })
        {
            // What Type.Name would be: the last segment of the name, without generic arguments.
            var arguments = original.IndexOf('[');
            var name = arguments < 0 ? original : original[..arguments];
            return name[(name.LastIndexOfAny(TypeNameSeparators) + 1)..];
        }

        return ex.GetType().Name;
    }

    private static readonly char[] TypeNameSeparators = { '.', '+' };

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
    private static string? Commit(RequestState state, CompiledNode node)
    {
        var table = state.Table;
        var live = table.Rows.Order;
        for (var o = 0; o < node.Outputs.Length; o++)
        {
            var output = node.Outputs[o];
            var column = state.Plan.Schema.Columns[output.LogicalId];

            // A node that never asked for its buffer wrote nothing: the new slot has every row missing.
            var buffer = table.GetOrCreateSlot(output.Slot);
            if (ColumnValues.IsFloat(buffer)
                && ColumnValues.SanitizeNonFinite(buffer, live, column.HasDefault ? column.Default : ScalarValue.Missing) is > 0 and var replaced)
            {
                state.NonFinite += replaced;
                if (state.Replay is null)
                {
                    StrataRecMetrics.ColumnNonFinite.Add(replaced, state.Plan.Telemetry.Nodes[node.Index].Outputs[o]);
                }
            }

            if (!output.Type.Nullable && table.CountMissingLiveRows(output.Slot) is > 0 and var missing)
            {
                return $"left non-nullable column '{output.Name}' without a value on {missing} of {live.Length} live rows";
            }
        }

        foreach (var output in node.Outputs)
        {
            state.Bindings.Commit(output.LogicalId, output.Slot, node.Index);
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
        }

        if (node.FetchBarrier)
        {
            await PrefetchSegmentAsync(state, position, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After a fetch barrier, starts fetches for every node up to and including the next barrier, plus their fallbacks
    /// (design 5.4, 6.8). Nodes already known to be skipped by <c>when</c> are left out.
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

            if (next.When is not null && !await EvaluateWhenAsync(state, i, next, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            features.AddRange(next.ReadFeatures);
            if (state.Plan.SelectFallback(next, state.Arms) is { } fallback)
            {
                features.AddRange(fallback.ReadFeatures);
            }

            if (next.IsRowSetNode && next.FetchBarrier)
            {
                break;
            }
        }

        if (features.Count > 0)
        {
            state.Features.Prefetch(features, cancellationToken);
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
        var value = node.When.EvaluateScalar(state.Eval);
        var result = !value.IsMissing && value.AsBool();
        state.WhenResults[position] = result;
        return result;
    }

    /// <summary>`when` and site conditions read only ctx.*; make sure the context features they read are present (design 5.5).</summary>
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
            await state.Features.EnsureAsync(features, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records experiments triggered by reaching this position (design 12.6): the site condition is the OR of every
    /// arm's effective `when` here, so the decision is identical across an experiment's arms, control included.
    /// </summary>
    private static async ValueTask RecordSiteTriggersAsync(RequestState state, TimelineSlot slot, CancellationToken cancellationToken)
    {
        for (var i = 0; i < slot.SiteExperiments.Length; i++)
        {
            var experiment = slot.SiteExperiments[i];
            if (state.Triggered[experiment] || !IsAssigned(state, experiment))
            {
                continue;
            }

            var condition = slot.SiteConditions[i];
            if (condition is null)
            {
                state.Triggered[experiment] = true;
                continue;
            }

            await EnsureContextAsync(state, condition, cancellationToken).ConfigureAwait(false);
            var value = condition.EvaluateScalar(state.Eval);
            state.Triggered[experiment] = !value.IsMissing && value.AsBool();
        }
    }

    /// <summary>
    /// Marks experiments a recording says were triggered, by id: only those the plan has and the request is assigned to,
    /// so a replay on another configuration or other arms never reports exposure to an experiment it does not run.
    /// </summary>
    private static void MarkRecordedTriggers(RequestState state, IReadOnlyList<string> experiments)
    {
        foreach (var id in experiments)
        {
            for (var i = 0; i < state.Plan.Experiments.Length; i++)
            {
                if (state.Plan.Experiments[i].Id == id && IsAssigned(state, i))
                {
                    state.Triggered[i] = true;
                }
            }
        }
    }

    /// <summary>Ids of experiments triggered now that were not in <paramref name="before"/>.</summary>
    private static List<string> NewlyTriggered(ExecutionPlan plan, bool[] before, bool[] now)
    {
        var ids = new List<string>();
        for (var i = 0; i < now.Length; i++)
        {
            if (now[i] && !before[i])
            {
                ids.Add(plan.Experiments[i].Id);
            }
        }

        return ids;
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
    private static void Record(RequestState state, CompiledNode node, NodeOutcomeCategory category, string? detail, string? producedBy, long started, int rowsIn)
    {
        state.PositionOpen = false;
        var elapsed = Stopwatch.GetElapsedTime(started);
        var rowsOut = state.Table.Rows.Count;
        var kind = category switch
        {
            NodeOutcomeCategory.Succeeded => NodeOutcomeKind.Succeeded,
            NodeOutcomeCategory.Degraded or NodeOutcomeCategory.Fallback => NodeOutcomeKind.Degraded,
            NodeOutcomeCategory.Failed or NodeOutcomeCategory.Aborted => NodeOutcomeKind.Failed,
            _ => NodeOutcomeKind.Skipped,
        };
        var outcome = new NodeOutcome(node.NodeId, kind, detail, producedBy, elapsed.TotalMilliseconds, rowsIn, rowsOut);
        state.Outcomes.Add(outcome);
        state.Degraded |= category is not (NodeOutcomeCategory.Succeeded or NodeOutcomeCategory.SkippedWhen);
        state.Recorder?.Position(outcome, PlanTelemetry.CategoryName(category));
        if (state.Replay is not null)
        {
            return; // a replay records no metrics, event logs or spans
        }

        var telemetry = state.Plan.Telemetry.Nodes[node.Index];
        StrataRecMetrics.NodeOutcomes.Add(1, telemetry.Outcomes[(int)category]);
        if (kind != NodeOutcomeKind.Skipped)
        {
            StrataRecMetrics.NodeDuration.Record(elapsed.TotalSeconds, telemetry.Tags);
            StrataRecMetrics.NodeRowsIn.Record(rowsIn, telemetry.Tags);
            StrataRecMetrics.NodeRowsOut.Record(rowsOut, telemetry.Tags);
        }

        if (category == NodeOutcomeCategory.Fallback)
        {
            state.Events?.Report(RequestEvent.NodeFallback, telemetry.Subject, state.RequestId, detail);
            if (telemetry.Model is { } model)
            {
                StrataRecMetrics.ModelFallbacks.Add(1, model);
            }
        }
        else if (category == NodeOutcomeCategory.Failed)
        {
            state.Events?.Report(RequestEvent.NodeFailed, telemetry.Subject, state.RequestId, detail);
        }

        if (state.NodeSpan is { IsAllDataRequested: true } span)
        {
            span.SetTag(StrataRecTags.Outcome, PlanTelemetry.CategoryName(category));
            span.SetTag(StrataRecTags.RowsIn, rowsIn);
            span.SetTag(StrataRecTags.RowsOut, rowsOut);
            if (producedBy is not null && producedBy != node.NodeId)
            {
                span.SetTag(StrataRecTags.Fallback, producedBy);
            }

            if (category == NodeOutcomeCategory.Failed)
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

        static void AddTriggered(RequestState state, int[] experiments, ref List<string>? triggered)
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

        // Output expressions may read features (design 3.4); make sure they are present for the returned rows. A feature
        // that failed (a source failure, or a non-nullable derived int32 that did not fit) cannot be served as typed:
        // expressions compiled against it skip validity checks. The request fails, as a node reading it would.
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
            var fill = await state.Features.EnsureAsync(outputFeatures, cancellationToken).ConfigureAwait(false);
            if (fill.Failed)
            {
                return Failed(plan, request, "output reads a feature that failed: " + fill.Error, start, state.Outcomes, state.Triggered,
                    state.NonFinite + state.Features.NonFiniteReplaced, state.Counters.OutOfRange);
            }
        }

        var values = new ScalarValue[output.Columns.Length][];
        var producers = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var c = 0; c < output.Columns.Length; c++)
        {
            var (name, expression) = output.Columns[c];
            values[c] = new ScalarValue[count];
            if (count == 0)
            {
                continue;
            }

            var buffer = ColumnBuffer.Create(expression.ResultType, Math.Max(1, state.Table.StorageCapacity), state.Memory);
            expression.Evaluate(state.Eval, rows, buffer);
            for (var i = 0; i < count; i++)
            {
                values[c][i] = ColumnValues.Get(buffer, rows[i]);
            }

            if (expression.Reads.Count == 1 && expression.Reads[0] is { Kind: SymbolKind.Column, FixedSlot: < 0 } symbol)
            {
                var producer = state.Bindings.Producer(symbol.Id);
                if (producer >= 0)
                {
                    producers[name] = plan.Nodes[producer].NodeId;
                }
            }
        }

        if (state.Batches is not null)
        {
            await TapCapture.OutputAsync(state, rows, cancellationToken).ConfigureAwait(false);
        }

        var items = new RankedItem[count];
        for (var i = 0; i < count; i++)
        {
            var columns = new Dictionary<string, ScalarValue>(output.Columns.Length, StringComparer.Ordinal);
            for (var c = 0; c < output.Columns.Length; c++)
            {
                columns[output.Columns[c].Name] = values[c][i];
            }

            items[i] = new RankedItem(state.Table.GetKey(rows[i]), i + 1, columns);
        }

        return new PipelineResult
        {
            Succeeded = true,
            Items = items,
            Outcomes = state.Outcomes,
            AssignedArms = AssignedArmIds(plan, state.Arms),
            TriggeredExperiments = TriggeredIds(plan, state.Triggered),
            ColumnProducers = producers,
            ElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            NonFiniteValues = state.NonFinite + state.Features.NonFiniteReplaced,
            OutOfRangeValues = state.Counters.OutOfRange,
            Degraded = state.Degraded,
        };
    }

    /// <summary>
    /// Completes every batch with the request's final record (assignment, triggers, node outcomes) and either returns
    /// them with the result or hands them to the log pipeline. Experiment requests also emit a durable trigger record
    /// (design 8.8, 12.6).
    /// </summary>
    private void PublishLogs(RequestState state, PipelineRequest request, PipelineResult result, bool returned)
    {
        // A capturing request (draft preview, debug run) never writes to the persistent logs, even when it is aborted
        // and nobody receives its captures (design 8.6 item 6): forced arms must not contaminate experiment data.
        if ((request.CaptureLogs || request.Replay is not null) && !returned)
        {
            return;
        }

        var capture = request.CaptureLogs || request.Replay is not null;
        if (state.Batches is null && (state.Plan.Experiments.Length == 0 || (_logs is null && !capture)))
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
                ManifestId = state.Plan.ManifestId,
                SchemaId = "$experiments",
                TimeMs = state.Context[PlanSchema.RequestTimeContext].AsInt64(),
                Delivery = Logging.LogDelivery.Durable,
                RequestProbability = 1.0,
                Int64EntityIds = Array.Empty<long>(),
                Ranks = Array.Empty<int>(),
                RowProbabilities = Array.Empty<double>(),
                Columns = Array.Empty<Logging.LogColumn>(),
                Context = Array.Empty<(string, ScalarValue)>(),
            });
        }

        var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var outcome in result.Outcomes)
        {
            if (outcome.ProducedBy is { } producer && producer != outcome.NodeId)
            {
                // Served by the fallback: the primary failed (design 8.7 envelope example).
                outcomes[outcome.NodeId] = nameof(NodeOutcomeKind.Failed);
                outcomes[producer] = nameof(NodeOutcomeKind.Succeeded);
                continue;
            }

            outcomes[outcome.NodeId] = outcome.Kind.ToString();
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

        if (_logs is not null)
        {
            foreach (var batch in batches)
            {
                _logs.Submit(batch);
            }
        }
    }

    /// <summary>Where the run's log batches go, as a bundle records it.</summary>
    private string LogDestination(PipelineRequest request) =>
        request.CaptureLogs ? "captured" : _logs is not null ? "pipeline" : "none";

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
            Items = Array.Empty<RankedItem>(),
            Outcomes = outcomes,
            AssignedArms = AssignedArmIds(plan, request.Arms),
            TriggeredExperiments = TriggeredIds(plan, triggered),
            ColumnProducers = new Dictionary<string, string>(),
            ElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            NonFiniteValues = nonFinite,
            OutOfRangeValues = outOfRange,
        };

    private static List<string> AssignedArmIds(ExecutionPlan plan, in RequestArms arms)
    {
        var ids = new List<string>();
        for (var layer = 0; layer < arms.LayerCount; layer++)
        {
            var arm = arms.ArmIn(layer);
            if (arm >= 0)
            {
                ids.Add(plan.Arms[arm].Id);
            }
        }

        return ids;
    }

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

    /// <summary>Validates inputs against <c>request_schema</c> and fills request-level context values.</summary>
    private static string? BindInputs(ExecutionPlan plan, PipelineRequest request, ScalarValue[] context)
    {
        // The plan builds a Dictionary: enumerate it as one, not through the interface (a boxed enumerator per request).
        var declared = plan.RequestInputs as Dictionary<string, ContextField> ?? new Dictionary<string, ContextField>(plan.RequestInputs);
        foreach (var (name, field) in declared)
        {
            request.Inputs.TryGetValue(name, out var value);
            if (value.IsMissing)
            {
                if (!field.Type.Nullable)
                {
                    return $"request input '{name}' ({field.InputType}) is required";
                }

                context[field.Id] = ScalarValue.Missing;
                continue;
            }

            // Validated against the declared type (an int32 must fit), held in its promoted form.
            if (!value.TryConvert(field.InputType, out var converted))
            {
                return $"request input '{name}' must be {field.InputType}, got {value}";
            }

            context[field.Id] = converted;
        }

        foreach (var (name, _) in request.Inputs)
        {
            if (!plan.RequestInputs.ContainsKey(name))
            {
                return $"request input '{name}' is not declared in request_schema";
            }
        }

        context[PlanSchema.RequestIdContext] = ScalarValue.FromString(request.RequestId);
        context[PlanSchema.RequestSeedContext] = ScalarValue.FromInt64(request.Seed ?? PipelineRequest.DefaultSeed(request.RequestId));
        context[PlanSchema.RequestTimeContext] = ScalarValue.FromInt64(request.TimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return null;
    }
}
