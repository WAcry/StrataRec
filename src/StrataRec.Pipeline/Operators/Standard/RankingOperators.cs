using System.Numerics.Tensors;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>TopK@1</c>.</summary>
public sealed class TopKConfig
{
    /// <summary>Numeric column to rank by.</summary>
    public required string ScoreCol { get; init; }

    /// <summary>Fixed number of rows to keep; use <see cref="KParam"/> to make it tunable.</summary>
    public int? K { get; init; }

    /// <summary>int64 parameter holding k.</summary>
    public string? KParam { get; init; }

    /// <summary>Rank lower scores first.</summary>
    public bool Ascending { get; init; }
}

/// <summary>
/// <c>TopK@1</c>: sorts by a score (missing last, ties by <c>entity_id</c>) and keeps the best k (RowOrder + RowFilter).
/// </summary>
public sealed class TopKFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("TopK", 1, Effects.RowOrder | Effects.RowFilter, typeof(TopKConfig),
        "Sorts rows by a score column (missing last, ties by entity_id) and keeps the first k.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<TopKConfig>();
        if (config is null || !context.TryReadColumn(config.ScoreCol, "/score_col", out var score))
        {
            return null;
        }

        if (!score.Type.IsNumeric)
        {
            context.Error("/score_col", "COLUMN_TYPE_MISMATCH", $"Score column '{config.ScoreCol}' must be numeric, not {score.Type}.",
                "sort by a numeric column, e.g. \"score_col\": \"score.final\"");
            return null;
        }

        if ((config.K is null) == (config.KParam is null))
        {
            context.Error(string.Empty, "TOPK_K", "Set exactly one of 'k' and 'k_param'.",
                "give \"k\": <count>, or \"k_param\": \"<an int64 parameter>\" for a count experiments can change");
            return null;
        }

        ParamRef? kParam = null;
        if (config.KParam is not null)
        {
            if (!context.TryParam(config.KParam, "/k_param", out var p, StorageKind.Int64))
            {
                return null;
            }

            kParam = p;
        }
        else if (config.K < 0)
        {
            context.Error("/k", "TOPK_K", "k must not be negative.");
            return null;
        }

        return new TopK(score, config.K ?? 0, kParam, config.Ascending);
    }

    private sealed class TopK(ColumnRef score, int fixedK, ParamRef? kParam, bool ascending) : Operator, IExplainableOperator
    {
        private int K(ScalarValue parameter) => kParam is null ? fixedK : parameter.IsMissing ? 0 : (int)Math.Clamp(parameter.AsInt64(), 0, int.MaxValue);

        /// <summary>
        /// For each row it ranked: k, the row's key and the last kept row's key (design 13.3), and whether the key, a tie on
        /// it (broken by <c>entity_id</c>) or a missing key put the row past the cut.
        /// </summary>
        public void Explain(ExplainContext context)
        {
            var k = K(kParam is { } p ? context.Param(p) : default);
            var keys = context.Read(score);
            var live = context.LiveRows;
            var last = live.Length > 0 && keys is not null ? live[^1] : -1;
            var lastKey = last >= 0 ? ColumnValues.Get(keys!, last) : ScalarValue.Missing;
            ulong lastSortKey = 0;
            var lastPresent = last >= 0 && RowRanking.TryGetSortKey(keys!, last, out lastSortKey);
            for (var t = 0; t < context.Count; t++)
            {
                if (!context.WasAlive(t))
                {
                    continue;
                }

                var row = context.Row(t);
                var key = keys is null ? ScalarValue.Missing : ColumnValues.Get(keys, row);
                context.Term(t, "k", ScalarValue.FromInt64(k));
                context.Term(t, "key", key);
                context.Term(t, "last_kept_key", lastKey);
                if (context.IsAlive(t))
                {
                    context.Reason(t, $"kept: {score.Name} {key} ranks {context.Rank(t)} of the {live.Length} kept (k = {k})");
                    continue;
                }

                ulong sortKey = 0;
                var present = keys is not null && RowRanking.TryGetSortKey(keys, row, out sortKey);
                context.Reason(t, (present, lastPresent) switch
                {
                    _ when k == 0 => "cut: k = 0 keeps no row",
                    (false, _) => $"cut: no {score.Name} (missing or NaN ranks last; k = {k})",
                    (true, true) when sortKey == lastSortKey =>
                        $"cut: {score.Name} {key} ties the last kept row's; ties go by entity_id ascending (k = {k})",
                    _ => $"cut: {score.Name} {key} ranks after the last kept {lastKey} (k = {k})",
                });
            }
        }

        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var k = K(kParam is { } p ? context.Param(p) : default);
            var live = context.LiveRows;
            var rows = context.Memory.Rent<int>(Math.Max(1, live.Length));
            live.CopyTo(rows);
            var span = rows.AsSpan(0, live.Length);
            RowRanking.SelectTop(span, context.Read(score), context.Table, k, ascending, context.Memory);
            context.SetOrder(span[..Math.Min(k, span.Length)]);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Config of <c>SlidingWindowQuota@1</c>.</summary>
public sealed class SlidingWindowQuotaConfig
{
    /// <summary>Grouping column (int64 or string), e.g. <c>item.creator_id</c>. Missing values are never limited.</summary>
    public required string GroupCol { get; init; }

    /// <summary>Window length in ranks.</summary>
    public required int Window { get; init; }

    /// <summary>Maximum rows of one group in any window.</summary>
    public required int MaxPerWindow { get; init; }
}

/// <summary>
/// <c>SlidingWindowQuota@1</c>: walks the ranking and allows at most <c>max_per_window</c> rows of a group in any
/// <c>window</c> consecutive ranks; rows over quota move down, rows that cannot be placed are dropped (design 5.1).
/// </summary>
public sealed class SlidingWindowQuotaFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("SlidingWindowQuota", 1, Effects.RowOrder | Effects.RowFilter, typeof(SlidingWindowQuotaConfig),
        "Limits rows per group within any sliding window of ranks; over-quota rows move down or drop.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<SlidingWindowQuotaConfig>();
        if (config is null || !context.TryReadColumn(config.GroupCol, "/group_col", out var group))
        {
            return null;
        }

        if (group.Type.Kind is not (StorageKind.Int64 or StorageKind.Int32 or StorageKind.String))
        {
            context.Error("/group_col", "COLUMN_TYPE_MISMATCH", $"Group column '{config.GroupCol}' must be int64, int32 or string, not {group.Type}.",
                "group by a column that names a group, such as an id or a category");
            return null;
        }

        if (config.Window < 1 || config.MaxPerWindow < 1)
        {
            var (at, name, value) = config.Window < 1 ? ("/window", "window", config.Window) : ("/max_per_window", "max_per_window", config.MaxPerWindow);
            context.Error(at, "QUOTA_BAD_CONFIG", $"{name} is {value}, but it must be at least 1.",
                "e.g. \"window\": 3, \"max_per_window\": 1 keeps at most one row of a group in any 3 consecutive ranks");
            return null;
        }

        return new SlidingWindowQuota(group, config.Window, config.MaxPerWindow);
    }

    private sealed class SlidingWindowQuota(ColumnRef group, int window, int maxPerWindow) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var live = context.LiveRows;
            var n = live.Length;
            if (n == 0 || maxPerWindow >= window)
            {
                return ValueTask.CompletedTask;
            }

            var memory = context.Memory;
            var groupOf = memory.Rent<int>(n);
            var groups = RowGroups.Assign(context.Read(group), live, groupOf.AsSpan(0, n), memory);
            var placed = memory.Rent<int>(n);
            var count = Place(groupOf.AsSpan(0, n), groups, window, maxPerWindow, placed, memory);
            var order = memory.Rent<int>(Math.Max(1, count));
            for (var i = 0; i < count; i++)
            {
                order[i] = live[placed[i]];
            }

            context.SetOrder(order.AsSpan(0, count));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The quota's placement: at each position, the earliest row (by index) whose group has fewer than
    /// <paramref name="maxPerWindow"/> rows among the last <paramref name="window"/> − 1 placed; a row without a group
    /// (−1) is never limited. Stops when no row can be placed; returns how many were, their indices in
    /// <paramref name="placed"/>.
    /// </summary>
    /// <remarks>
    /// Each group's pending rows form a queue in index order, the earliest is its head, and a group under quota is
    /// eligible: the earliest placeable row is the smallest head among eligible groups, kept in a heap. Placing a row
    /// counts it in the window and uncounts the row the window leaves, which may make that group eligible again. O(n log G)
    /// for n rows in G groups; the first version rescanned the pending rows and recounted the window at every position,
    /// O(n² · window), 9 ms for 5,000 rows in two groups (performance audit).
    /// </remarks>
    internal static int Place(ReadOnlySpan<int> groupOf, int groups, int window, int maxPerWindow, Span<int> placed, Core.Memory.RequestMemory memory)
    {
        var n = groupOf.Length;
        var free = groups; // the rows without a group: a group of their own that is never limited
        var total = groups + 1;
        var head = memory.Rent<int>(total);
        var tail = memory.Rent<int>(total);
        var next = memory.Rent<int>(Math.Max(1, n));
        head.AsSpan(0, total).Fill(-1);
        for (var i = 0; i < n; i++)
        {
            var g = groupOf[i] < 0 ? free : groupOf[i];
            next[i] = -1;
            if (head[g] < 0)
            {
                head[g] = i;
            }
            else
            {
                next[tail[g]] = i;
            }

            tail[g] = i;
        }

        var inWindow = memory.RentCleared<int>(total);
        var heap = new GroupHeap(memory.Rent<int>(total), head);
        for (var g = 0; g < total; g++)
        {
            if (head[g] >= 0)
            {
                heap.Push(g);
            }
        }

        var count = 0;
        while (heap.Count > 0)
        {
            var g = heap.Pop();
            var row = head[g];
            head[g] = next[row];
            placed[count++] = row;
            if (g != free)
            {
                inWindow[g]++;
            }

            // The window of the next position is the last window − 1 placed: the row placed window positions back leaves.
            if (count >= window && groupOf[placed[count - window]] is var left and >= 0)
            {
                inWindow[left]--;
                if (left != g && inWindow[left] == maxPerWindow - 1 && head[left] >= 0)
                {
                    heap.Push(left); // was at its quota, so not in the heap
                }
            }

            if (head[g] >= 0 && (g == free || inWindow[g] < maxPerWindow))
            {
                heap.Push(g);
            }
        }

        return count;
    }

    /// <summary>A binary min-heap of groups by the index of their head row, in a rented array.</summary>
    private struct GroupHeap(int[] items, int[] head)
    {
        public int Count { get; private set; }

        public void Push(int group)
        {
            var i = Count++;
            items[i] = group;
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (head[items[parent]] <= head[items[i]])
                {
                    break;
                }

                (items[parent], items[i]) = (items[i], items[parent]);
                i = parent;
            }
        }

        public int Pop()
        {
            var top = items[0];
            items[0] = items[--Count];
            var i = 0;
            while (true)
            {
                var smallest = i;
                var left = (2 * i) + 1;
                if (left < Count && head[items[left]] < head[items[smallest]])
                {
                    smallest = left;
                }

                if (left + 1 < Count && head[items[left + 1]] < head[items[smallest]])
                {
                    smallest = left + 1;
                }

                if (smallest == i)
                {
                    return top;
                }

                (items[smallest], items[i]) = (items[i], items[smallest]);
                i = smallest;
            }
        }
    }
}

/// <summary>Config of <c>MmrDiversifier@1</c>.</summary>
public sealed class MmrDiversifierConfig
{
    public required string ScoreCol { get; init; }

    /// <summary>Vector column; rows without a vector have zero similarity to everything.</summary>
    public required string EmbeddingCol { get; init; }

    /// <summary>Relevance weight λ in [0, 1]; use <see cref="LambdaParam"/> to make it tunable.</summary>
    public double? Lambda { get; init; }

    public string? LambdaParam { get; init; }

    /// <summary>Number of leading ranks to rerank; later rows keep their order. Default 50.</summary>
    public int Window { get; init; } = 50;
}

/// <summary>
/// <c>MmrDiversifier@1</c>: maximal marginal relevance rerank of the top <c>window</c> rows (RowOrder):
/// repeatedly picks <c>argmax λ·score − (1−λ)·max cosine(selected)</c>.
/// </summary>
/// <remarks>
/// The formula is real-valued, so scores are read as float64: int64 scores beyond 2^53 lose their last bits here, unlike
/// in <c>TopK@1</c>, which compares them exactly. Equal values keep their incoming rank order.
/// </remarks>
public sealed class MmrDiversifierFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("MmrDiversifier", 1, Effects.RowOrder, typeof(MmrDiversifierConfig),
        "Maximal marginal relevance rerank of the leading rows using an embedding column.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<MmrDiversifierConfig>();
        if (config is null
            || !context.TryReadColumn(config.ScoreCol, "/score_col", out var score)
            || !context.TryReadColumn(config.EmbeddingCol, "/embedding_col", out var embedding))
        {
            return null;
        }

        if (!score.Type.IsNumeric)
        {
            context.Error("/score_col", "COLUMN_TYPE_MISMATCH", $"Score column '{config.ScoreCol}' must be numeric, not {score.Type}.",
                "use the score the rows are ranked by, e.g. \"score_col\": \"score.final\"");
            return null;
        }

        if (embedding.Type.Kind != StorageKind.Vector)
        {
            context.Error("/embedding_col", "COLUMN_TYPE_MISMATCH", $"Embedding column '{config.EmbeddingCol}' must be a vector, not {embedding.Type}.",
                "use a vec<float32,N> column, such as an item embedding from the catalog");
            return null;
        }

        if ((config.Lambda is null) == (config.LambdaParam is null))
        {
            context.Error(string.Empty, "MMR_LAMBDA", "Set exactly one of 'lambda' and 'lambda_param'.",
                "give \"lambda\": <0 to 1>, or \"lambda_param\": \"<a float64 parameter>\" for a value experiments can change");
            return null;
        }

        ParamRef? lambdaParam = null;
        if (config.LambdaParam is not null)
        {
            if (!context.TryParam(config.LambdaParam, "/lambda_param", out var p, StorageKind.Float64))
            {
                return null;
            }

            lambdaParam = p;
        }

        if (config.Window < 1)
        {
            context.Error("/window", "MMR_WINDOW", "window must be at least 1.");
            return null;
        }

        return new MmrDiversifier(score, embedding, config.Lambda ?? 0, lambdaParam, config.Window);
    }

    private sealed class MmrDiversifier(ColumnRef score, ColumnRef embedding, double fixedLambda, ParamRef? lambdaParam, int window) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var lambda = lambdaParam is { } p ? context.Param(p).AsFloat64() : fixedLambda;
            var live = context.LiveRows;
            var n = Math.Min(window, live.Length);
            if (n <= 1)
            {
                return ValueTask.CompletedTask;
            }

            var scores = context.Read(score);
            var vectors = context.Read(embedding) as VectorColumn;
            var memory = context.Memory;
            var relevance = memory.Rent<double>(n);
            var maxSim = memory.RentCleared<double>(n);
            var used = memory.RentCleared<bool>(n);
            var norms = memory.Rent<float>(n);
            for (var i = 0; i < n; i++)
            {
                relevance[i] = scores is not null && RowRanking.TryGetScore(scores, live[i], out var s) ? s : double.NegativeInfinity;
                norms[i] = vectors is not null && vectors.IsValid(live[i]) ? TensorPrimitives.Norm(vectors.Row(live[i])) : 0f;
            }

            var order = memory.Rent<int>(live.Length);
            for (var step = 0; step < n; step++)
            {
                var best = -1;
                var bestValue = double.NegativeInfinity;
                for (var i = 0; i < n; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    var value = (lambda * relevance[i]) - ((1 - lambda) * maxSim[i]);
                    if (best < 0 || value > bestValue)
                    {
                        best = i;
                        bestValue = value;
                    }
                }

                used[best] = true;
                order[step] = live[best];
                if (vectors is null || norms[best] == 0f)
                {
                    continue;
                }

                var chosen = vectors.Row(live[best]);
                for (var i = 0; i < n; i++)
                {
                    if (!used[i] && norms[i] > 0f)
                    {
                        var sim = TensorPrimitives.Dot(chosen, vectors.Row(live[i])) / (norms[best] * norms[i]);
                        maxSim[i] = Math.Max(maxSim[i], sim);
                    }
                }
            }

            live[n..].CopyTo(order.AsSpan(n));
            context.SetOrder(order.AsSpan(0, live.Length));
            return ValueTask.CompletedTask;
        }
    }
}
