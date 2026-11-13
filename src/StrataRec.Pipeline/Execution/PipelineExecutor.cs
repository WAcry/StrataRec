using System.Diagnostics;
using StrataRec.Core.Columns;
using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

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

    /// <summary>Log batches of the request when <see cref="PipelineRequest.CaptureLogs"/> was set.</summary>
    public IReadOnlyList<Logging.LogBatch> LogBatches { get; set; } = Array.Empty<Logging.LogBatch>();
}

/// <summary>Mutable per-request state shared by the executor and operator contexts.</summary>
internal sealed class RequestState
{
    public required ExecutionPlan Plan { get; init; }

    public required string RequestId { get; init; }

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

    public TimeSpan Remaining => Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), DeadlineTimestamp);

    public EvalContext Eval => new(Table, Bindings, Context, Params, Counters);
}

/// <summary>
/// Executes an <see cref="ExecutionPlan"/> for one request (design 3.3): walk the timeline in order, pick each
/// position's variant, make its features available, run it with its time budget, and commit outputs only on success.
/// CPU work runs on the request's logical thread; concurrency comes from overlapping I/O (design 5.4).
/// </summary>
public sealed class PipelineExecutor
{
    private readonly IBufferPool _pool;
    private readonly Logging.LogPipeline? _logs;

    /// <param name="pool">Buffer pool for request memory.</param>
    /// <param name="logs">Where tap batches and trigger records go; null disables persistent logging.</param>
    public PipelineExecutor(IBufferPool? pool = null, Logging.LogPipeline? logs = null)
    {
        _pool = pool ?? SharedArrayBufferPool.Instance;
        _logs = logs;
    }

    public async ValueTask<PipelineResult> ExecuteAsync(ExecutionPlan plan, PipelineRequest request, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        var memory = new RequestMemory(_pool);
        FeatureFetcher? fetcher = null;
        RequestState? state = null;
        try
        {
            var context = new ScalarValue[plan.Schema.ContextFields.Count];
            var inputError = BindInputs(plan, request, context);
            if (inputError is not null)
            {
                return Failed(plan, request, inputError, start, Array.Empty<NodeOutcome>(), Array.Empty<bool>());
            }

            var parameters = plan.Params.Snapshot(request.Arms);
            var variants = new CompiledNode?[plan.Timeline.Length];
            var capacity = 0;
            for (var i = 0; i < variants.Length; i++)
            {
                var node = plan.Timeline[i].Select(request.Arms);
                variants[i] = node;
                if (node is not null && (node.Effects & Effects.RowSource) != 0)
                {
                    capacity += Math.Max(0, node.Operator.GetRowLimit(parameters));
                }
            }

            var table = new CandidateTable(plan.TableSchema, capacity, memory);
            var bindings = new OutputBindings(plan.Schema.Columns.Count, memory);
            var deadline = start + (long)(request.Deadline.TotalSeconds * Stopwatch.Frequency);
            fetcher = new FeatureFetcher(plan.Features, table, context, bindings, parameters, memory, deadline);
            state = new RequestState
            {
                Plan = plan,
                RequestId = request.RequestId,
                Inputs = request.Inputs,
                Seed = context[PlanSchema.RequestSeedContext].AsInt64(),
                Memory = memory,
                Table = table,
                Bindings = bindings,
                Context = context,
                Params = parameters,
                Features = fetcher,
                Counters = new ExpressionCounters(),
                DeadlineTimestamp = deadline,
                Arms = request.Arms,
                Variants = variants,
                Triggered = new bool[plan.Experiments.Length],
                WhenResults = new bool?[plan.Timeline.Length],
            };

            fetcher.StartContextFetch(plan.ContextFeatures, cancellationToken);
            if (_logs is not null || request.CaptureLogs)
            {
                // Without a consumer every captured batch would be discarded: skip sampling and capture entirely.
                await TapCapture.SelectAsync(state, cancellationToken).ConfigureAwait(false);
            }

            PipelineResult? result = null;
            for (var i = 0; i < plan.Timeline.Length && result is null; i++)
            {
                var error = await RunPositionAsync(state, i, cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    result = Failed(plan, request, error, start, state.Outcomes, state.Triggered, state.NonFinite);
                }
            }

            result ??= await BuildResultAsync(state, start, cancellationToken).ConfigureAwait(false);
            PublishLogs(state, request, result, returned: true);
            return result;
        }
        catch (Exception ex) when (state is not null)
        {
            // Cancelled by the caller (or a defect): nobody receives a result, but the request was assigned, may have
            // been exposed, and may have captured taps. Dropping those records would remove slow or failing requests
            // from experiment analysis, biased by arm (design 8.8, 12.6). Request memory is still held here.
            var reason = ex is OperationCanceledException ? "cancelled by the caller" : "aborted: " + ex.Message;
            try
            {
                PublishLogs(state, request, Failed(plan, request, reason, start, state.Outcomes, state.Triggered, state.NonFinite), returned: false);
            }
            catch (Exception publishError)
            {
                Debug.WriteLine($"StrataRec could not publish the logs of an aborted request: {publishError.Message}");
            }

            throw;
        }
        finally
        {
            // In-flight connector calls hold the memory themselves; buffers return when the last one finishes.
            memory.Release();
        }
    }

    /// <summary>Runs one timeline position. Returns an error message when the request must fail.</summary>
    private static async ValueTask<string?> RunPositionAsync(RequestState state, int position, CancellationToken cancellationToken)
    {
        var slot = state.Plan.Timeline[position];
        var node = state.Variants[position];
        await RecordSiteTriggersAsync(state, slot, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return null;
        }

        var started = Stopwatch.GetTimestamp();
        var rowsIn = state.Table.Rows.Count;
        if (!await EvaluateWhenAsync(state, position, node, cancellationToken).ConfigureAwait(false))
        {
            Record(state, node.NodeId, NodeOutcomeKind.Skipped, "when is false", null, started, rowsIn);
            return null;
        }

        if (node.MinRemainingMs is { } minRemaining && state.Remaining.TotalMilliseconds < minRemaining)
        {
            Record(state, node.NodeId, NodeOutcomeKind.Skipped, $"less than {minRemaining} ms remaining", null, started, rowsIn);
            return null;
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
            Record(state, node.NodeId, attempt.Kind, attempt.Detail, node.NodeId, started, rowsIn);
            if (state.Batches is not null)
            {
                await TapCapture.AfterNodeAsync(state, node.NodeId, entered, rowsIn, cancellationToken).ConfigureAwait(false);
            }

            await AfterNodeAsync(state, position, node, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (attempt.Kind == NodeOutcomeKind.Skipped)
        {
            Record(state, node.NodeId, NodeOutcomeKind.Skipped, attempt.Detail, null, started, rowsIn);
            return null;
        }

        // Failed: apply on_failure (design 5.6).
        switch (node.OnFailure)
        {
            case FailurePolicy.Skip:
                Record(state, node.NodeId, NodeOutcomeKind.Failed, attempt.Detail + " (skipped)", null, started, rowsIn);
                return null;
            case FailurePolicy.Fallback when state.Plan.SelectFallback(node, state.Arms) is { } fallback:
                // A fallback site triggers only when the fallback is invoked, which is independent of the arm (design 12.6).
                if (state.Plan.Fallbacks.TryGetValue(fallback.NodeId, out var fallbackSlot))
                {
                    await RecordSiteTriggersAsync(state, fallbackSlot, cancellationToken).ConfigureAwait(false);
                }

                var fb = await RunNodeAsync(state, fallback, 0, cancellationToken).ConfigureAwait(false);
                if (fb.Kind is NodeOutcomeKind.Succeeded or NodeOutcomeKind.Degraded)
                {
                    Record(state, node.NodeId, NodeOutcomeKind.Degraded, $"{attempt.Detail}; served by fallback '{fallback.NodeId}'", fallback.NodeId, started, rowsIn);
                    if (state.Batches is not null)
                    {
                        await TapCapture.AfterNodeAsync(state, node.NodeId, entered, rowsIn, cancellationToken).ConfigureAwait(false);
                    }

                    await AfterNodeAsync(state, position, fallback, cancellationToken).ConfigureAwait(false);
                    return null;
                }

                if (fallback.OnFailure == FailurePolicy.Skip || fb.Kind == NodeOutcomeKind.Skipped)
                {
                    Record(state, node.NodeId, NodeOutcomeKind.Failed, $"{attempt.Detail}; fallback '{fallback.NodeId}': {fb.Detail} (skipped)", null, started, rowsIn);
                    return null;
                }

                Record(state, node.NodeId, NodeOutcomeKind.Failed, $"{attempt.Detail}; fallback '{fallback.NodeId}': {fb.Detail}", null, started, rowsIn);
                return $"node '{node.NodeId}' failed ({attempt.Detail}) and its fallback '{fallback.NodeId}' failed ({fb.Detail})";
            default:
                Record(state, node.NodeId, NodeOutcomeKind.Failed, attempt.Detail, null, started, rowsIn);
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
        if (budgetMs <= 0)
        {
            return new Attempt(NodeOutcomeKind.Failed, "deadline exceeded");
        }

        int[]? captured = null;
        var capturedCount = 0;
        if (node.HasRowEffects)
        {
            captured = state.Memory.Rent<int>(Math.Max(1, state.Table.Rows.Count));
            capturedCount = state.Table.Rows.Capture(captured);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(budgetMs));
        var context = new OperatorContext(state, node.NodeId, node.Effects, cts.Token);
        string? failure;
        try
        {
            // The operator is about to read its parameters: that is exposure, whether it then succeeds or fails
            // (design 12.6). Recording only successes would bias triggered analysis against arms that fail more.
            MarkParamTriggers(state, node);
            await node.Operator.ExecuteAsync(context).ConfigureAwait(false);
            failure = context.FailureReason ?? Commit(state, node);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failure = $"timed out after {budgetMs:0} ms";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (failure is not null)
        {
            if (captured is not null)
            {
                state.Table.Rows.Restore(captured.AsSpan(0, capturedCount));
            }

            return new Attempt(NodeOutcomeKind.Failed, failure);
        }

        var degraded = context.DegradedReason ?? (fill.State == FeatureState.Degraded ? "degraded features: " + fill.Error : null);
        return new Attempt(degraded is null ? NodeOutcomeKind.Succeeded : NodeOutcomeKind.Degraded, degraded);
    }

    /// <summary>
    /// Validates and publishes a successful node's outputs (design 4.3, 4.6): non-finite values take the column default
    /// or become missing; non-nullable columns must have a value on every live row.
    /// </summary>
    private static string? Commit(RequestState state, CompiledNode node)
    {
        var live = state.Table.Rows.Order;
        foreach (var output in node.Outputs)
        {
            var column = state.Plan.Schema.Columns[output.LogicalId];
            var buffer = state.Table.TryGetSlot(output.Slot);
            if (buffer is null)
            {
                if (!output.Type.Nullable && live.Length > 0)
                {
                    return $"did not write non-nullable column '{output.Name}'";
                }

                buffer = state.Table.GetOrCreateSlot(output.Slot);
                foreach (var row in live)
                {
                    buffer.SetMissing(row);
                }
            }

            if (ColumnValues.IsFloat(buffer))
            {
                state.NonFinite += ColumnValues.SanitizeNonFinite(buffer, live, column.HasDefault ? column.Default : ScalarValue.Missing);
            }

            if (!output.Type.Nullable && buffer.Validity is not null)
            {
                foreach (var row in live)
                {
                    if (buffer.IsValid(row))
                    {
                        continue;
                    }

                    if (!column.HasDefault)
                    {
                        return $"wrote a missing value into non-nullable column '{output.Name}'";
                    }

                    ColumnValues.Set(buffer, row, column.Default);
                }
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
        foreach (var symbol in expression.Reads)
        {
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

    private static void Record(RequestState state, string nodeId, NodeOutcomeKind kind, string? detail, string? producedBy, long started, int rowsIn) =>
        state.Outcomes.Add(new NodeOutcome(nodeId, kind, detail, producedBy, Stopwatch.GetElapsedTime(started).TotalMilliseconds, rowsIn, state.Table.Rows.Count));

    private static async ValueTask<PipelineResult> BuildResultAsync(RequestState state, long start, CancellationToken cancellationToken)
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

        // Output expressions may read features (design 3.4); make sure they are present for the returned rows.
        var outputFeatures = new List<int>();
        foreach (var (_, expression) in output.Columns)
        {
            foreach (var symbol in expression.Reads)
            {
                if (symbol.Kind == SymbolKind.Column && plan.Schema.Columns[symbol.Id].FeatureIndex is >= 0 and var f)
                {
                    outputFeatures.Add(f);
                }
            }
        }

        if (outputFeatures.Count > 0)
        {
            await state.Features.EnsureAsync(outputFeatures, cancellationToken).ConfigureAwait(false);
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
        };
    }

    /// <summary>
    /// Completes every batch with the request's final record (assignment, triggers, node outcomes) and either returns
    /// them with the result or hands them to the log pipeline. Experiment requests also emit a durable trigger record
    /// (design 8.8, 12.6).
    /// </summary>
    private void PublishLogs(RequestState state, PipelineRequest request, PipelineResult result, bool returned)
    {
        var capture = returned && request.CaptureLogs;
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

    private static int[] FirstRows(CandidateTable table, int limit)
    {
        var order = table.Rows.Order;
        return order[..Math.Min(limit, order.Length)].ToArray();
    }

    private static PipelineResult Failed(
        ExecutionPlan plan, PipelineRequest request, string error, long start, IReadOnlyList<NodeOutcome> outcomes, bool[] triggered, int nonFinite = 0) =>
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
        foreach (var (name, field) in plan.RequestInputs)
        {
            request.Inputs.TryGetValue(name, out var value);
            if (value.IsMissing)
            {
                if (!field.Type.Nullable)
                {
                    return $"request input '{name}' ({field.Type}) is required";
                }

                context[field.Id] = ScalarValue.Missing;
                continue;
            }

            if (!value.TryConvert(field.Type, out var converted))
            {
                return $"request input '{name}' must be {field.Type}, got {value}";
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
        context[PlanSchema.RequestSeedContext] = ScalarValue.FromInt64(request.Seed ?? unchecked((long)StableHash.Hash(StableHash.SeedFromSalt(request.RequestId))));
        context[PlanSchema.RequestTimeContext] = ScalarValue.FromInt64(request.TimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return null;
    }
}
