using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Expressions;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>ExpressionFilter@1</c>.</summary>
public sealed class ExpressionFilterConfig
{
    /// <summary>Non-nullable bool ColumnExpr; rows where it is false are dropped.</summary>
    public required string Predicate { get; init; }
}

/// <summary><c>ExpressionFilter@1</c>: keeps rows matching a predicate (RowFilter).</summary>
public sealed class ExpressionFilterFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("ExpressionFilter", 1, Effects.RowFilter, typeof(ExpressionFilterConfig),
        "Drops rows for which a boolean ColumnExpr predicate is false.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<ExpressionFilterConfig>();
        var predicate = config is null ? null : context.CompileExpression(config.Predicate, "/predicate", CompileOptions.Predicate);
        return predicate is null ? null : new ExpressionFilter(predicate);
    }

    private sealed class ExpressionFilter(CompiledExpression predicate) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var rows = context.LiveRows;
            if (rows.Length == 0)
            {
                return ValueTask.CompletedTask;
            }

            var table = context.Table;
            var result = new ScalarColumn<bool>(ColumnType.Bool, Math.Max(1, table.StorageCapacity), context.Memory);
            predicate.Evaluate(context.EvalContext, rows, result);
            var keep = context.Memory.Rent<bool>(rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                keep[i] = result[rows[i]];
            }

            context.Retain(keep.AsSpan(0, rows.Length));
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Config of <c>ExpressionScorer@1</c>.</summary>
public sealed class ExpressionScorerConfig
{
    /// <summary>Column to write (declared under <c>columns</c>); may be a flow column read by <see cref="Expr"/>.</summary>
    public required string OutputCol { get; init; }

    public required string Expr { get; init; }
}

/// <summary><c>ExpressionScorer@1</c>: writes a ColumnExpr into a column (ColumnWriter).</summary>
public sealed class ExpressionScorerFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("ExpressionScorer", 1, Effects.ColumnWriter, typeof(ExpressionScorerConfig),
        "Evaluates a ColumnExpr for every live row and writes it to a column.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<ExpressionScorerConfig>();
        if (config is null)
        {
            return null;
        }

        // Compile before declaring the write so that reading the previous version of a flow column resolves correctly.
        if (!context.Schema.TryGetColumn(config.OutputCol, out var declared))
        {
            context.Error("/output_col", "COLUMN_UNDECLARED", $"Output column '{config.OutputCol}' is not declared.",
                $"declare it under /columns, e.g. \"{config.OutputCol}\": {{ \"type\": \"float64\", \"default\": 0.0 }}");
            return null;
        }

        var options = new CompileOptions { ExpectedType = declared.Type.AsNonNullable(), AllowNullableResult = declared.Type.Nullable };
        var expression = context.CompileExpression(config.Expr, "/expr", options);
        if (expression is null || !context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output))
        {
            return null;
        }

        return new ExpressionScorer(expression, output);
    }

    private sealed class ExpressionScorer(CompiledExpression expression, OutputColumn output) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            expression.Evaluate(context.EvalContext, context.LiveRows, context.Output(output));
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Config of <c>CompositeMultiplierScorer@1</c> (design 7.8).</summary>
public sealed class CompositeMultiplierScorerConfig
{
    public required string OutputCol { get; init; }

    /// <summary>Base utility (non-nullable float64).</summary>
    public required string BaseExpr { get; init; }

    /// <summary>Named multipliers; experiments add, change or delete (null) single entries without conflicts.</summary>
    public Dictionary<string, string> Multipliers { get; init; } = new();

    /// <summary>Named addends.</summary>
    public Dictionary<string, string> Addends { get; init; } = new();
}

/// <summary>
/// <c>CompositeMultiplierScorer@1</c>: <c>score = base × Π multipliers + Σ addends</c> (design 7.8). Terms are evaluated
/// in canonical (ordinal) key order so floating-point results are deterministic.
/// </summary>
public sealed class CompositeMultiplierScorerFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("CompositeMultiplierScorer", 1, Effects.ColumnWriter, typeof(CompositeMultiplierScorerConfig),
        "score = base × Π multipliers + Σ addends, with named, independently patchable terms.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<CompositeMultiplierScorerConfig>();
        if (config is null)
        {
            return null;
        }

        var options = CompileOptions.Score;
        var baseExpr = context.CompileExpression(config.BaseExpr, "/base_expr", options);
        var multipliers = CompileTerms(context, config.Multipliers, "/multipliers/", options);
        var addends = CompileTerms(context, config.Addends, "/addends/", options);
        if (baseExpr is null || multipliers is null || addends is null
            || !context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output))
        {
            return null;
        }

        if (!output.Type.IsFloat)
        {
            context.Error("/output_col", "COLUMN_TYPE_MISMATCH", $"Output column '{config.OutputCol}' must be float32 or float64, not {output.Type}.");
            return null;
        }

        return new CompositeMultiplierScorer(baseExpr, multipliers, addends, output);
    }

    private static (string Name, CompiledExpression Expr)[]? CompileTerms(
        OperatorCompileContext context, Dictionary<string, string> terms, string pointer, CompileOptions options)
    {
        var names = new List<string>(terms.Keys);
        names.Sort(StringComparer.Ordinal);
        var result = new (string, CompiledExpression)[names.Count];
        var ok = true;
        for (var i = 0; i < names.Count; i++)
        {
            var compiled = context.CompileExpression(terms[names[i]], pointer + names[i], options);
            ok &= compiled is not null;
            result[i] = (names[i], compiled!);
        }

        return ok ? result : null;
    }

    private sealed class CompositeMultiplierScorer(
        CompiledExpression baseExpr,
        (string Name, CompiledExpression Expr)[] multipliers,
        (string Name, CompiledExpression Expr)[] addends,
        OutputColumn output) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var rows = context.LiveRows;
            var capacity = Math.Max(1, context.Table.StorageCapacity);
            var eval = context.EvalContext;
            var score = new ScalarColumn<double>(ColumnType.Float64, capacity, context.Memory);
            baseExpr.Evaluate(eval, rows, score);
            if (multipliers.Length + addends.Length > 0)
            {
                var term = new ScalarColumn<double>(ColumnType.Float64, capacity, context.Memory);
                foreach (var (_, expr) in multipliers)
                {
                    expr.Evaluate(eval, rows, term);
                    foreach (var row in rows)
                    {
                        score[row] *= term[row];
                    }
                }

                foreach (var (_, expr) in addends)
                {
                    expr.Evaluate(eval, rows, term);
                    foreach (var row in rows)
                    {
                        score[row] += term[row];
                    }
                }
            }

            switch (context.Output(output))
            {
                case ScalarColumn<double> d:
                    foreach (var row in rows)
                    {
                        d.Set(row, score[row]);
                    }

                    break;
                case ScalarColumn<float> f:
                    foreach (var row in rows)
                    {
                        f.Set(row, (float)score[row]);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Output column '{output.Name}' must be float32 or float64.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
