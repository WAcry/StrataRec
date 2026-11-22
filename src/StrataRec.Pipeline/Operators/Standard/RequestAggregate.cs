using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>RequestAggregate@1</c>.</summary>
public sealed class RequestAggregateConfig
{
    /// <summary>
    /// Request column (declared under <c>request_columns</c>) → what it gets, over the live candidates when the node runs.
    /// An object, so an experiment adds, changes or deletes (null) one value.
    /// </summary>
    public required Dictionary<string, RequestAggregateValue> Values { get; init; }
}

/// <summary>One value of <c>RequestAggregate@1</c>.</summary>
public sealed class RequestAggregateValue
{
    /// <summary>
    /// <c>count</c> (the rows; with <see cref="Of"/>, the rows where it has a value), <c>sum</c>, <c>mean</c>, <c>min</c> or
    /// <c>max</c> of <see cref="Of"/> over the rows where it has a value. Missing values are left out, as SQL's aggregates
    /// leave out nulls, and with no value to aggregate every function but count is missing.
    /// </summary>
    public required string Fn { get; init; }

    /// <summary>
    /// The numeric ColumnExpr aggregated per row, e.g. <c>col.score.final</c>; count takes it optionally. An integer one
    /// sums, and has a min and max, as an integer; a float one as a float; a mean is a float.
    /// </summary>
    public string? Of { get; init; }

    /// <summary>
    /// A predicate (a non-nullable bool, as <c>ExpressionFilter@1</c>'s): only the rows where it holds are aggregated, e.g.
    /// <c>col.recall.primary_source == 'popular'</c>.
    /// </summary>
    public string? Where { get; init; }
}

/// <summary>
/// <c>RequestAggregate@1</c>: statistics of the live candidates written to request columns (RequestWriter), one value per
/// request: how many candidates a stage left, the best score, the share from one source. Later nodes read them as
/// <c>req.*</c> in expressions and <c>when</c> (a threshold relative to the best score, a reranker that runs only above a
/// number of candidates), taps log them in <c>context</c>, and <c>output.values</c> returns them to the caller.
/// </summary>
/// <remarks>
/// It aggregates the rows alive when it runs, in their order, so floating-point sums are deterministic. Each value
/// evaluates its expressions once over the live rows into buffers rented from the request's memory, one of each kind
/// for the node, which its values share; nothing is allocated per row. Integer sums and means are exact until the end: a sum outside int64 fails the node, whatever the order of
/// the rows. A float sum adds the rows in order, so it may overflow before the end where the exact one would not; a float
/// result the request column cannot hold (NaN, ±∞) is replaced when the node commits, as any request column's value is.
/// </remarks>
public sealed class RequestAggregateFactory : IOperatorFactory
{
    private static readonly string[] Functions = ["count", "sum", "mean", "min", "max"];

    public OperatorDescriptor Descriptor { get; } = new("RequestAggregate", 1, Effects.RequestWriter, typeof(RequestAggregateConfig),
        "Over the live candidates: count, sum, mean, min or max of an expression, written to request columns (req.*).");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<RequestAggregateConfig>();
        if (config is null)
        {
            return null;
        }

        if (config.Values.Count == 0)
        {
            context.Error("/values", "REQUEST_AGGREGATE_NO_VALUE", "The node computes nothing: 'values' is empty.",
                "add a value, e.g. \"candidates.count\": { \"fn\": \"count\" }, declared under /request_columns");
            return null;
        }

        var names = new List<string>(config.Values.Keys);
        names.Sort(StringComparer.Ordinal);
        var values = new Value[names.Count];
        var ok = true;
        for (var i = 0; i < names.Count; i++)
        {
            var compiled = CompileValue(context, names[i], config.Values[names[i]]);
            ok &= compiled is not null;
            values[i] = compiled!;
        }

        return ok ? new RequestAggregate(values) : null;
    }

    private static Value? CompileValue(OperatorCompileContext context, string name, RequestAggregateValue spec)
    {
        var at = "/values/" + JsonPointer.Escape(name);
        var fn = Array.IndexOf(Functions, spec.Fn);
        if (fn < 0)
        {
            context.Error(at + "/fn", "REQUEST_AGGREGATE_FN", $"'{spec.Fn}' is not a function of {context.Descriptor.Id}.",
                Spelling.DidYouMean(spec.Fn, Functions) ?? "use one of: " + string.Join(", ", Functions));
            return null;
        }

        var function = (Function)fn;
        var ok = true;
        CompiledExpression? of = null;
        if (spec.Of is not null)
        {
            of = context.CompileExpression(spec.Of, at + "/of", new CompileOptions { AllowNullableResult = true });
            if (of is null)
            {
                ok = false;
            }
            else if (!of.ResultType.IsNumeric)
            {
                context.Error(at + "/of", "COLUMN_TYPE_MISMATCH", $"{spec.Fn} reads a numeric expression, and '{spec.Of}' is {of.ResultType}.",
                    "give an integer or float expression; to count the rows where a condition holds, use \"fn\": \"count\" with \"where\"");
                ok = false;
            }
        }
        else if (function != Function.Count)
        {
            context.Error(at, "REQUEST_AGGREGATE_VALUE", $"{spec.Fn} needs 'of', the numeric expression it aggregates.",
                "add \"of\", e.g. \"of\": \"col.score.final\"; count needs none");
            ok = false;
        }

        var where = spec.Where is null ? null : context.CompileExpression(spec.Where, at + "/where", CompileOptions.Predicate);
        ok &= spec.Where is null || where is not null;
        var integer = function == Function.Count || (function != Function.Mean && of is { ResultType.Kind: StorageKind.Int64 });
        var produced = integer ? ColumnType.Int64 : ColumnType.Float64;
        var suggested = function == Function.Count ? produced : produced.AsNullable();
        if (!context.TryWriteRequestColumn(name, at, out var output, ok ? suggested : null) || !ok)
        {
            return null;
        }

        // The request column is declared; it must hold what the function writes, and be nullable where it can be missing.
        if (integer ? !output.Type.IsInteger : !output.Type.IsFloat)
        {
            context.Error(at, "COLUMN_TYPE_MISMATCH",
                $"{spec.Fn}{(of is null ? string.Empty : $" of '{spec.Of}'")} writes {(integer ? "an integer" : "a float")}, and request column '{name}' is declared as {output.Type}.",
                $"declare it as {suggested}");
            return null;
        }

        if (function != Function.Count && !output.Type.Nullable)
        {
            context.Error(at, "COLUMN_TYPE_MISMATCH",
                $"Request column '{name}' is declared {output.Type}, but {spec.Fn} has no value for a request with no candidate"
                + (where is null ? string.Empty : " where its 'where' holds") + (of is { ResultType.Nullable: true } ? $" and '{spec.Of}' has a value" : string.Empty) + ".",
                $"declare it as {output.Type.AsNullable()}, and read it with '??' or has()");
            return null;
        }

        return new Value(function, of, where, output);
    }

    private enum Function : byte
    {
        Count,
        Sum,
        Mean,
        Min,
        Max,
    }

    private sealed record Value(Function Function, CompiledExpression? Of, CompiledExpression? Where, RequestOutput Output);

    private sealed class RequestAggregate(Value[] values) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var buffers = default(Buffers);
            foreach (var value in values)
            {
                if (Aggregate(context, value, ref buffers) is not { } result)
                {
                    return ValueTask.CompletedTask; // the node failed (Fail)
                }

                context.SetRequestValue(value.Output, result);
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// The buffers the values evaluate their expressions into, made the first time one needs them and shared by the
        /// values after it: each evaluation writes every live row, value and validity, so the node holds at most one of
        /// each kind of the request's memory, however many values it computes.
        /// </summary>
        private struct Buffers
        {
            public ScalarColumn<bool>? Where;
            public ScalarColumn<long>? Integers;
            public ScalarColumn<double>? Floats;
        }

        /// <summary>The value over the live rows; null after failing the node.</summary>
        private static ScalarValue? Aggregate(OperatorContext context, Value value, ref Buffers buffers)
        {
            var live = context.LiveRows;
            if (live.Length == 0 || (value is { Function: Function.Count, Where: null, Of: null }))
            {
                return value.Function == Function.Count ? ScalarValue.FromInt64(live.Length) : ScalarValue.Missing;
            }

            var capacity = Math.Max(1, context.Table.StorageCapacity);
            ScalarColumn<bool>? where = null;
            if (value.Where is { } predicate)
            {
                where = buffers.Where ??= new ScalarColumn<bool>(ColumnType.Bool, capacity, context.Memory);
                predicate.Evaluate(context.EvalContext, live, where);
            }

            ColumnBuffer? of = null;
            if (value.Of is { } expression)
            {
                of = expression.ResultType.Kind == StorageKind.Int64
                    ? buffers.Integers ??= new ScalarColumn<long>(ColumnType.Int64.AsNullable(), capacity, context.Memory)
                    : buffers.Floats ??= new ScalarColumn<double>(ColumnType.Float64.AsNullable(), capacity, context.Memory);
                expression.Evaluate(context.EvalContext, live, of);
            }

            long count = 0;
            Int128 sum = 0; // of integer values: exact, so the order of the rows never decides whether it fits
            long extreme = 0; // the min or max of integer values
            double real = 0; // the sum, min, max or running mean of float values
            var integers = of as ScalarColumn<long>;
            var floats = of as ScalarColumn<double>;
            foreach (var row in live)
            {
                if ((where is not null && !where[row]) || (of is not null && !of.IsValid(row)))
                {
                    continue;
                }

                count++;
                if (value.Function == Function.Count)
                {
                    continue;
                }

                if (integers is not null)
                {
                    var x = integers[row];
                    sum += x;
                    extreme = count == 1 ? x : value.Function == Function.Min ? Math.Min(extreme, x) : Math.Max(extreme, x);
                }
                else if (floats is not null)
                {
                    var y = floats[row];
                    real = value.Function switch
                    {
                        Function.Sum => count == 1 ? y : real + y,
                        Function.Min => count == 1 ? y : Math.Min(real, y),
                        Function.Max => count == 1 ? y : Math.Max(real, y),
                        _ => RunningMean.Step(real, y, count),
                    };
                }
            }

            if (value.Function == Function.Count)
            {
                return ScalarValue.FromInt64(count);
            }

            if (count == 0)
            {
                return ScalarValue.Missing;
            }

            if (integers is null)
            {
                return ScalarValue.FromFloat64(real);
            }

            switch (value.Function)
            {
                case Function.Sum when sum < long.MinValue || sum > long.MaxValue:
                    context.Fail($"the sum of '{value.Of!.Source}' for '{value.Output.Name}' is {sum}, outside int64");
                    return null;
                case Function.Sum:
                    return ScalarValue.FromInt64((long)sum);
                case Function.Mean:
                    // The exact quotient, then the remainder's share: equal rows give their value, and a mean whose integer
                    // part a double holds is off by at most the rounding of that share (the sum as a double was not).
                    var (quotient, remainder) = Int128.DivRem(sum, count);
                    return ScalarValue.FromFloat64((double)quotient + (double)remainder / count);
                default:
                    return ScalarValue.FromInt64(extreme);
            }
        }
    }
}
