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
            context.Error("/score_col", "COLUMN_TYPE_MISMATCH", $"Score column '{config.ScoreCol}' must be numeric, not {score.Type}.");
            return null;
        }

        if ((config.K is null) == (config.KParam is null))
        {
            context.Error(string.Empty, "TOPK_K", "Set exactly one of 'k' and 'k_param'.");
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

    private sealed class TopK(ColumnRef score, int fixedK, ParamRef? kParam, bool ascending) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var k = fixedK;
            if (kParam is { } p)
            {
                var value = context.Param(p);
                k = value.IsMissing ? 0 : (int)Math.Clamp(value.AsInt64(), 0, int.MaxValue);
            }

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
            context.Error("/group_col", "COLUMN_TYPE_MISMATCH", $"Group column must be int64, int32 or string, not {group.Type}.");
            return null;
        }

        if (config.Window < 1 || config.MaxPerWindow < 1)
        {
            context.Error(string.Empty, "QUOTA_BAD_CONFIG", "window and max_per_window must be at least 1.");
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

            var column = context.Read(group);
            var memory = context.Memory;
            var keys = memory.Rent<long>(n);
            var hasKey = memory.Rent<bool>(n);
            for (var i = 0; i < n; i++)
            {
                var row = live[i];
                hasKey[i] = column is not null && column.IsValid(row);
                keys[i] = !hasKey[i] ? 0 : column switch
                {
                    ScalarColumn<long> l => l[row],
                    ScalarColumn<int> x => x[row],
                    DictStringColumn s => s.Codes[row],
                    _ => 0,
                };
            }

            // Greedy: at each output position take the first pending row whose group is under quota in the
            // last (window - 1) placed rows.
            var placed = memory.Rent<int>(n);       // indices into live
            var used = memory.RentCleared<bool>(n);
            var placedCount = 0;
            var firstPending = 0;
            while (placedCount < n)
            {
                var chosen = -1;
                for (var i = firstPending; i < n; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    if (!hasKey[i] || CountInWindow(keys, hasKey, placed, placedCount, keys[i]) < maxPerWindow)
                    {
                        chosen = i;
                        break;
                    }
                }

                if (chosen < 0)
                {
                    break; // remaining rows cannot be placed without violating the quota
                }

                used[chosen] = true;
                placed[placedCount++] = chosen;
                while (firstPending < n && used[firstPending])
                {
                    firstPending++;
                }
            }

            var order = memory.Rent<int>(Math.Max(1, placedCount));
            for (var i = 0; i < placedCount; i++)
            {
                order[i] = live[placed[i]];
            }

            context.SetOrder(order.AsSpan(0, placedCount));
            return ValueTask.CompletedTask;
        }

        private int CountInWindow(long[] keys, bool[] hasKey, int[] placed, int placedCount, long key)
        {
            var count = 0;
            for (var j = Math.Max(0, placedCount - (window - 1)); j < placedCount; j++)
            {
                var p = placed[j];
                if (hasKey[p] && keys[p] == key)
                {
                    count++;
                }
            }

            return count;
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

        if (embedding.Type.Kind != StorageKind.Vector || !score.Type.IsNumeric)
        {
            context.Error("/embedding_col", "COLUMN_TYPE_MISMATCH", "score_col must be numeric and embedding_col a vec<float32,N>.");
            return null;
        }

        if ((config.Lambda is null) == (config.LambdaParam is null))
        {
            context.Error(string.Empty, "MMR_LAMBDA", "Set exactly one of 'lambda' and 'lambda_param'.");
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
