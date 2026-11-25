using System.Collections.Immutable;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Runtime;
using StrataRec.Logging;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Pipeline.Execution;

/// <summary>
/// Hot-path capture of log taps (design 8.6): pick the rows (scope, filter, coordinated sampling), copy the tap's columns
/// for those rows only into a batch the request no longer owns: a column logged as a reference to a candidate column
/// straight from that column's storage, at its declared width; any other expression evaluated for those rows. Everything
/// here runs only for requests whose funnel group sampled them.
/// </summary>
internal static class TapCapture
{
    /// <summary>Selects tap variants and makes each group's request-level sampling decision once (design 8.2).</summary>
    public static async ValueTask SelectAsync(RequestState state, CancellationToken cancellationToken)
    {
        var log = state.Plan.Log;
        if (log.Taps.Length == 0)
        {
            return;
        }

        var sampled = new bool?[log.Groups.Length];
        var taps = new CompiledTap?[log.Taps.Length];
        var any = false;
        for (var i = 0; i < taps.Length; i++)
        {
            var tap = log.Taps[i].Select(state.Arms);
            if (tap is null)
            {
                continue;
            }

            if (sampled[tap.Group] is not { } decision)
            {
                decision = await SampleGroupAsync(state, log.Groups[tap.Group], cancellationToken).ConfigureAwait(false);
                sampled[tap.Group] = decision;
            }

            if (decision)
            {
                taps[i] = tap;
                any = true;
            }
        }

        state.Recorder?.Sampling(sampled, taps);
        if (any)
        {
            state.Taps = taps;
            state.Batches = new List<LogBatch>();
        }
    }

    private static async ValueTask<bool> SampleGroupAsync(RequestState state, CompiledFunnelGroup group, CancellationToken cancellationToken)
    {
        if (group.Rate <= 0)
        {
            return false; // a disabled group samples nothing; do not even evaluate its keys
        }

        for (var k = 0; k < group.Keys.Length; k++)
        {
            await EnsureContextAsync(state, group.Keys[k], cancellationToken).ConfigureAwait(false);
            var key = group.Keys[k].EvaluateScalar(state.Eval);
            if (!key.IsMissing)
            {
                return CoordinatedSampling.SampleRequest(group.Seed, k, key, group.Rate);
            }
        }

        return false;
    }

    /// <summary>True when a tap after <paramref name="nodeId"/> needs the rows that entered the node.</summary>
    public static bool NeedsEnteredRows(RequestState state, string nodeId)
    {
        foreach (var tap in state.Taps)
        {
            if (tap is not null && tap.AttachAfter == nodeId && tap.Scope != TapScope.Survived)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fires the taps attached after <paramref name="nodeId"/>, whether its primary or its fallback produced the output
    /// (design 8.3). Must run before compaction, which renumbers physical rows.
    /// </summary>
    public static async ValueTask AfterNodeAsync(RequestState state, string nodeId, int[]? entered, int enteredCount, CancellationToken cancellationToken)
    {
        foreach (var tap in state.Taps)
        {
            if (tap is null || tap.AttachAfter != nodeId)
            {
                continue;
            }

            await EnsureFeaturesAsync(state, tap.ReadContextFeatures, cancellationToken).ConfigureAwait(false);
            switch (tap.Scope)
            {
                case TapScope.Survived:
                    CaptureInOrder(state, tap, state.Table.Rows.Order);
                    break;
                case TapScope.Entered when entered is not null:
                    CaptureInOrder(state, tap, entered.AsSpan(0, enteredCount));
                    break;
                case TapScope.Dropped when entered is not null:
                    CaptureDropped(state, tap, entered.AsSpan(0, enteredCount));
                    break;
            }
        }
    }

    /// <summary>Fires output taps over the returned rows; ranks are the served positions (design 8.3).</summary>
    public static async ValueTask OutputAsync(RequestState state, int[] rows, CancellationToken cancellationToken)
    {
        foreach (var tap in state.Taps)
        {
            if (tap is { AttachOutput: true })
            {
                await EnsureFeaturesAsync(state, tap.ReadContextFeatures, cancellationToken).ConfigureAwait(false);
                CaptureInOrder(state, tap, rows);
            }
        }
    }

    private static void CaptureDropped(RequestState state, CompiledTap tap, ReadOnlySpan<int> entered)
    {
        // Dropped rows keep their pre-node rank: entered position + 1.
        var rows = state.Memory.Rent<int>(Math.Max(1, entered.Length));
        var ranks = state.Memory.Rent<int>(Math.Max(1, entered.Length));
        var count = 0;
        for (var i = 0; i < entered.Length; i++)
        {
            if (!state.Table.Rows.IsAlive(entered[i]))
            {
                rows[count] = entered[i];
                ranks[count] = i + 1;
                count++;
            }
        }

        Capture(state, tap, rows.AsSpan(0, count), ranks.AsSpan(0, count), dropped: true);
    }

    /// <summary>Captures rows that are in rank order: rank = position + 1.</summary>
    private static void CaptureInOrder(RequestState state, CompiledTap tap, ReadOnlySpan<int> rows)
    {
        var ranks = state.Memory.Rent<int>(Math.Max(1, rows.Length));
        for (var i = 0; i < rows.Length; i++)
        {
            ranks[i] = i + 1;
        }

        Capture(state, tap, rows, ranks.AsSpan(0, rows.Length), dropped: false);
    }

    private static void Capture(RequestState state, CompiledTap tap, ReadOnlySpan<int> rows, ReadOnlySpan<int> ranks, bool dropped)
    {
        var table = state.Table;
        var memory = state.Memory;
        var capacity = Math.Max(1, table.StorageCapacity);
        var eval = state.Eval;
        var group = state.Plan.Log.Groups[tap.Group];

        // Positions into rows/ranks that pass the filter.
        var positions = memory.Rent<int>(Math.Max(1, rows.Length));
        var count = 0;
        if (tap.Filter is { } filter && rows.Length > 0)
        {
            // A filter reading a failed feature is missing for every row, which a filter treats as false.
            if (!LacksData(state, filter))
            {
                var keep = ColumnBuffer.Create(filter.ResultType, capacity, memory);
                filter.Evaluate(eval, rows, keep);
                for (var i = 0; i < rows.Length; i++)
                {
                    var v = ColumnValues.Get(keep, rows[i]);
                    if (!v.IsMissing && v.AsBool())
                    {
                        positions[count++] = i;
                    }
                }
            }
        }
        else
        {
            for (var i = 0; i < rows.Length; i++)
            {
                positions[count++] = i;
            }
        }

        // Coordinated row sampling: the same priority for an entity in every tap of the group (design 8.4).
        var rowProbability = 1.0;
        if (tap.Rate < 1.0 || (tap.MaxRows is { } max && count > max))
        {
            var priorities = memory.Rent<double>(Math.Max(1, count));
            for (var i = 0; i < count; i++)
            {
                priorities[i] = CoordinatedSampling.RowPriority(group.Seed, state.RequestHash, EntityHash(table, rows[positions[i]]));
            }

            count = CoordinatedSampling.Select(positions.AsSpan(0, count), priorities.AsSpan(0, count), tap.Rate, tap.MaxRows, out rowProbability);
        }

        var selected = memory.Rent<int>(Math.Max(1, count));
        var batchRanks = new int[count];
        int[]? ranksBefore = dropped ? new int[count] : null;
        long[]? intIds = table.KeyType == EntityKeyType.Int64 ? new long[count] : null;
        string[]? stringIds = intIds is null ? new string[count] : null;
        for (var i = 0; i < count; i++)
        {
            var row = rows[positions[i]];
            selected[i] = row;
            batchRanks[i] = dropped ? 0 : ranks[positions[i]];
            if (ranksBefore is not null)
            {
                ranksBefore[i] = ranks[positions[i]];
            }

            if (intIds is not null)
            {
                intIds[i] = table.Int64Ids![row];
            }
            else
            {
                stringIds![i] = table.StringIds!.Get(row);
            }
        }

        var selectedRows = selected.AsSpan(0, count);
        var columns = new LogColumn[tap.Columns.Length];
        var producers = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var c = 0; c < tap.Columns.Length; c++)
        {
            var (name, expression) = tap.Columns[c];
            var type = tap.Schema.Columns[c].Type; // the declared type for a reference, else the promoted result type
            if (count == 0 || LacksData(state, expression))
            {
                columns[c] = LogColumn.AllMissing(name, type, count);
            }
            else if (expression.ColumnReference is { } column)
            {
                // A column is copied from its own storage, at its declared width: nothing to evaluate, nothing widened (a
                // float32 embedding stays float32). A column no node produced in this request has no value to log.
                columns[c] = state.ReadColumn(column.FixedSlot, column.Id) is { } storage
                    ? LogColumn.Capture(name, type, storage, selectedRows)
                    : LogColumn.AllMissing(name, type, count);
            }
            else
            {
                // A request-level reference broadcast to the rows is narrowed to its declared type, which holds it.
                var buffer = ColumnBuffer.Create(type, capacity, memory);
                expression.Evaluate(eval, selectedRows, buffer);
                MissWhereInputsAreInvalid(state, expression, selectedRows, buffer);
                columns[c] = LogColumn.Capture(name, type, buffer, selectedRows); // non-finite values are missing in logs
            }

            if (tap.PinnedProducers[c] is { } pinned)
            {
                if (Produced(state, pinned))
                {
                    producers[name] = pinned; // col.x@node always reads that node's version
                }
            }
            else if (tap.ColumnSources[c] is >= 0 and var logical && state.Bindings.Producer(logical) is >= 0 and var producer)
            {
                producers[name] = state.Plan.Nodes[producer].NodeId;
            }
        }

        var context = new (string, ScalarValue)[tap.Context.Length];
        for (var i = 0; i < context.Length; i++)
        {
            var expression = tap.Context[i].Expression;
            context[i] = (tap.Context[i].Name, LacksData(state, expression) ? ScalarValue.Missing : Sanitize(expression.EvaluateScalar(eval)));
        }

        state.Batches!.Add(new LogBatch
        {
            TapId = tap.TapId,
            RequestId = state.RequestId,
            PipelineId = state.Plan.PipelineId,
            CalledBy = state.CalledBy,
            ManifestId = state.Plan.ManifestId,
            Schema = tap.Schema,
            TimeMs = state.Context[PlanSchema.RequestTimeContext].AsInt64(),
            Delivery = tap.Delivery,
            RequestProbability = group.Rate,
            Int64EntityIds = intIds,
            StringEntityIds = stringIds,
            Ranks = batchRanks,
            RanksBefore = ranksBefore,
            RowProbability = rowProbability,
            Columns = columns,
            Context = context,
            ColumnProducers = producers,
        });
    }

    /// <summary>True when <paramref name="nodeId"/> ran and committed its outputs in this request (lineage, design 8.7).</summary>
    private static bool Produced(RequestState state, string nodeId)
    {
        foreach (var outcome in state.Outcomes)
        {
            if (outcome.ProducedBy == nodeId && outcome.Kind is NodeOutcomeKind.Succeeded or NodeOutcomeKind.Degraded or NodeOutcomeKind.Fallback)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Non-finite values are logged as missing, never as NaN (design 4.3, 8.7). A float list with any non-finite element
    /// is missing as a whole: a partial embedding has no meaning to a training job.
    /// </summary>
    private static ScalarValue Sanitize(ScalarValue value) => !value.IsMissing && ColumnValues.IsNonFinite(value) ? ScalarValue.Missing : value;

    private static long EntityHash(Core.Tables.CandidateTable table, int row) =>
        table.KeyType == EntityKeyType.Int64 ? table.Int64Ids![row] : Core.Hashing.StableHash.SeedFromSalt(table.StringIds!.Get(row));

    /// <summary>
    /// True when the expression reads what this request has no value of: a feature whose fetch failed (its storage may hold
    /// rows invalid under a non-nullable type, which the expression would read without checking validity, review 6), or a
    /// non-nullable column or request column no node produced (evaluated, the read would throw; a nullable one reads as
    /// missing, which an expression may handle). A tap logs such a column or context field missing; it never fails the
    /// request.
    /// </summary>
    private static bool LacksData(RequestState state, CompiledExpression expression)
    {
        var reads = expression.Reads;
        for (var i = 0; i < reads.Count; i++)
        {
            var symbol = reads[i];
            if ((PipelineExecutor.FeatureOf(state.Plan, symbol) is >= 0 and var f && state.Features.GetState(f) == FeatureState.Failed)
                || (symbol.Kind == SymbolKind.Column && !symbol.Type.Nullable && state.ReadColumn(symbol.FixedSlot, symbol.Id) is null))
            {
                return true;
            }
        }

        // A request column no node wrote before the tap's point (its producer was skipped) has no value to log.
        return PipelineExecutor.MissingRequestColumn(state, reads) is not null;
    }

    /// <summary>
    /// Marks missing the rows where a non-nullable column the expression reads has no valid value. An expression reads such
    /// a column without its validity (every live row has a value), but a tap of dropped rows sees rows a node dropped
    /// unwritten, or marked missing: their slots hold whatever was there, which a plain reference never logs either.
    /// </summary>
    private static void MissWhereInputsAreInvalid(RequestState state, CompiledExpression expression, ReadOnlySpan<int> rows, ColumnBuffer buffer)
    {
        var reads = expression.Reads;
        for (var i = 0; i < reads.Count; i++)
        {
            var symbol = reads[i];
            if (symbol.Kind != SymbolKind.Column || symbol.Type.Nullable || state.ReadColumn(symbol.FixedSlot, symbol.Id) is not { } storage)
            {
                continue;
            }

            foreach (var row in rows)
            {
                if (!storage.IsValid(row))
                {
                    buffer.SetMissing(row);
                }
            }
        }
    }

    private static ValueTask EnsureContextAsync(RequestState state, CompiledExpression expression, CancellationToken cancellationToken)
    {
        List<int>? features = null;
        var reads = expression.Reads;
        for (var i = 0; i < reads.Count; i++) // an interface foreach would box the enumerator
        {
            var symbol = reads[i];
            if (symbol.Kind == SymbolKind.Context && state.Plan.Schema.ContextFields[symbol.Id].FeatureIndex is >= 0 and var f)
            {
                (features ??= new List<int>()).Add(f);
            }
        }

        return features is null ? ValueTask.CompletedTask : EnsureFeaturesAsync(state, [.. features], cancellationToken);
    }

    private static async ValueTask EnsureFeaturesAsync(RequestState state, ImmutableArray<int> features, CancellationToken cancellationToken)
    {
        if (features.Length > 0)
        {
            await state.Features.EnsureAsync(features.AsSpan(), cancellationToken).ConfigureAwait(false);
        }
    }
}
