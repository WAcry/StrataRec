using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Logging;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Pipeline.Execution;

/// <summary>
/// Hot-path capture of log taps (design 8.6): pick the rows (scope, filter, coordinated sampling), evaluate the tap's
/// columns for those rows only and copy them into a batch the request no longer owns. Everything here runs only for
/// requests whose funnel group sampled them.
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

        if (any)
        {
            state.Taps = taps;
            state.Batches = new List<LogBatch>();
        }
    }

    private static async ValueTask<bool> SampleGroupAsync(RequestState state, CompiledFunnelGroup group, CancellationToken cancellationToken)
    {
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
        var probabilities = new double[count];
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

            probabilities[i] = rowProbability;
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
            var values = new ScalarValue[count];
            if (count > 0)
            {
                var buffer = ColumnBuffer.Create(expression.ResultType, capacity, memory);
                expression.Evaluate(eval, selectedRows, buffer);
                for (var i = 0; i < count; i++)
                {
                    values[i] = Sanitize(ColumnValues.Get(buffer, selectedRows[i]));
                }
            }

            columns[c] = new LogColumn(name, expression.ResultType, values);
            if (tap.PinnedProducers[c] is { } pinned)
            {
                producers[name] = pinned; // col.x@node always reads that node's version
            }
            else if (tap.ColumnSources[c] is >= 0 and var logical && state.Bindings.Producer(logical) is >= 0 and var producer)
            {
                producers[name] = state.Plan.Nodes[producer].NodeId;
            }
        }

        var context = new (string, ScalarValue)[tap.Context.Length];
        for (var i = 0; i < context.Length; i++)
        {
            context[i] = (tap.Context[i].Name, Sanitize(tap.Context[i].Expression.EvaluateScalar(eval)));
        }

        state.Batches!.Add(new LogBatch
        {
            TapId = tap.TapId,
            RequestId = state.RequestId,
            PipelineId = state.Plan.PipelineId,
            ManifestId = state.Plan.ManifestId,
            SchemaId = tap.SchemaId,
            TimeMs = state.Context[PlanSchema.RequestTimeContext].AsInt64(),
            Delivery = tap.Delivery,
            RequestProbability = group.Rate,
            Int64EntityIds = intIds,
            StringEntityIds = stringIds,
            Ranks = batchRanks,
            RanksBefore = ranksBefore,
            RowProbabilities = probabilities,
            Columns = columns,
            Context = context,
            ColumnProducers = producers,
        });
    }

    /// <summary>Non-finite values are logged as missing, never as NaN (design 4.3, 8.7).</summary>
    private static ScalarValue Sanitize(ScalarValue value) =>
        value.Kind is StorageKind.Float32 or StorageKind.Float64 && !double.IsFinite(value.AsFloat64()) ? ScalarValue.Missing : value;

    private static long EntityHash(Core.Tables.CandidateTable table, int row) =>
        table.KeyType == EntityKeyType.Int64 ? table.Int64Ids![row] : Core.Hashing.StableHash.SeedFromSalt(table.StringIds!.Get(row));

    private static ValueTask EnsureContextAsync(RequestState state, CompiledExpression expression, CancellationToken cancellationToken)
    {
        List<int>? features = null;
        foreach (var symbol in expression.Reads)
        {
            if (symbol.Kind == SymbolKind.Context && state.Plan.Schema.ContextFields[symbol.Id].FeatureIndex is >= 0 and var f)
            {
                (features ??= new List<int>()).Add(f);
            }
        }

        return features is null ? ValueTask.CompletedTask : EnsureFeaturesAsync(state, features.ToArray(), cancellationToken);
    }

    private static async ValueTask EnsureFeaturesAsync(RequestState state, int[] features, CancellationToken cancellationToken)
    {
        if (features.Length > 0)
        {
            await state.Features.EnsureAsync(features, cancellationToken).ConfigureAwait(false);
        }
    }
}
