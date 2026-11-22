using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>The mean of values taken one at a time, in row order (<c>GroupAggregate@1</c>, <c>RequestAggregate@1</c>).</summary>
internal static class RunningMean
{
    /// <summary>
    /// The mean after its <paramref name="k"/>-th value <paramref name="x"/>: equal values leave it exactly the value (a mean
    /// from the sum is off by its rounding, and the deviations from it made a std of equal values > 0, a z-score of ±1). The
    /// step is x/k − mean/k, not (x − mean)/k: neither a sum nor the difference of two large values of opposite signs
    /// overflows (review 45).
    /// </summary>
    public static double Step(double mean, double x, long k) => mean + ((x / k) - (mean / k));
}

/// <summary>Config of <c>GroupAggregate@1</c>.</summary>
public sealed class GroupAggregateConfig
{
    /// <summary>
    /// Grouping column (int64, int32 or string), e.g. <c>item.author_id</c>. Omitted, all rows form one group. A row whose
    /// key is missing is in no group: its outputs are missing.
    /// </summary>
    public string? GroupCol { get; init; }

    /// <summary>
    /// Output column → what it gets. An object, so an experiment adds, changes or deletes (null) one output. A column not
    /// declared under <c>columns</c> is declared by the node: int64 for count and rank, float64 for the others, nullable
    /// when a value can be missing.
    /// </summary>
    public required Dictionary<string, GroupAggregateOutput> Outputs { get; init; }
}

/// <summary>One output of <c>GroupAggregate@1</c>.</summary>
public sealed class GroupAggregateOutput
{
    /// <summary>
    /// <c>count</c> (rows of the group), <c>sum</c>, <c>mean</c>, <c>min</c>, <c>max</c>, <c>std</c> (population standard
    /// deviation) of <see cref="ValueCol"/>, or <c>rank</c>: the row's 1-based place in its group, ordered by
    /// <see cref="ValueCol"/> as <c>TopK@1</c> orders (missing last, ties by entity_id ascending).
    /// </summary>
    public required string Fn { get; init; }

    /// <summary>Numeric column the function reads; count takes none. Missing values are left out (a column holds no NaN: a
    /// commit makes non-finite values the default or missing).</summary>
    public string? ValueCol { get; init; }

    /// <summary>rank only: lower values first.</summary>
    public bool Ascending { get; init; }
}

/// <summary>
/// <c>GroupAggregate@1</c>: per group of rows (by a column, or all rows), a statistic written back to every row of the
/// group (ColumnWriter). Composes with the other operators instead of growing options: at most two items per author is
/// <c>rank</c> then <c>ExpressionFilter</c> on <c>col.author_rank &lt;= 2</c>; a score normalized over the request is
/// <c>mean</c> and <c>std</c> without a group, then <c>ExpressionScorer</c>; a merchant's score in grouped results is
/// <c>max</c> by merchant.
/// </summary>
/// <remarks>
/// It aggregates the rows alive when it runs, in their order, so floating-point sums are deterministic; values are read
/// as float64, as <c>MmrDiversifier@1</c> reads scores (int64 values beyond 2^53 lose their last bits). Groups take
/// dense ids (<see cref="RowGroups"/>) and every buffer is rented from the request's memory: nothing is allocated per
/// row, and the cost is O(n) per output, O(n log n) for integer keys and for rank.
/// </remarks>
public sealed class GroupAggregateFactory : IOperatorFactory
{
    private static readonly string[] Functions = ["count", "sum", "mean", "min", "max", "std", "rank"];

    public OperatorDescriptor Descriptor { get; } = new("GroupAggregate", 1, Effects.ColumnWriter, typeof(GroupAggregateConfig),
        "Per group of rows (by a column, or all rows): count, sum, mean, min, max, std or rank, written back to every row of the group.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<GroupAggregateConfig>();
        if (config is null)
        {
            // The outputs the config names cannot be declared: marked unusable, their readers fail without a second error.
            if (context.Config["outputs"] is JsonObject named)
            {
                foreach (var (name, _) in named)
                {
                    Unusable(context, name);
                }
            }

            return null;
        }

        // A wrong group column fails the node, not its outputs' readers: every output is still compiled, so each error is
        // reported once and a column it cannot declare is marked unusable (as MultiSourceRecall@1 declares its columns).
        ColumnRef? group = null;
        var groupFailed = false;
        if (config.GroupCol is not null)
        {
            if (!context.TryReadColumn(config.GroupCol, "/group_col", out var g))
            {
                groupFailed = true;
            }
            else if (g.Type.Kind is not (StorageKind.Int64 or StorageKind.Int32 or StorageKind.String))
            {
                context.Error("/group_col", "COLUMN_TYPE_MISMATCH", $"Group column '{config.GroupCol}' must be int64, int32 or string, not {g.Type}.",
                    "group by a column that names a group, such as an id or a category (a float is a measurement, and equal values would be a coincidence)");
                groupFailed = true;
            }
            else
            {
                group = g;
            }
        }

        if (config.Outputs.Count == 0)
        {
            context.Error("/outputs", "GROUP_AGGREGATE_NO_OUTPUT", "The node computes nothing: 'outputs' is empty.",
                "add an output, e.g. \"author.count\": { \"fn\": \"count\" }");
            return null;
        }

        var names = new List<string>(config.Outputs.Keys);
        names.Sort(StringComparer.Ordinal);
        var outputs = new Output[names.Count];
        var ok = true;
        for (var i = 0; i < names.Count; i++)
        {
            var compiled = CompileOutput(context, names[i], config.Outputs[names[i]], group, groupFailed);
            ok &= compiled is not null;
            outputs[i] = compiled!;
        }

        return ok && !groupFailed ? new GroupAggregate(group, outputs) : null;
    }

    private static Output? CompileOutput(OperatorCompileContext context, string name, GroupAggregateOutput spec, ColumnRef? group, bool groupFailed)
    {
        var at = "/outputs/" + JsonPointer.Escape(name);
        var fn = Array.IndexOf(Functions, spec.Fn);
        if (fn < 0)
        {
            context.Error(at + "/fn", "GROUP_AGGREGATE_FN", $"'{spec.Fn}' is not a function of {context.Descriptor.Id}.",
                Spelling.DidYouMean(spec.Fn, Functions) ?? "use one of: " + string.Join(", ", Functions));
            return Unusable(context, name);
        }

        var function = (Function)fn;
        ColumnRef? value = null;
        if (function == Function.Count)
        {
            if (spec.ValueCol is not null)
            {
                context.Error(at + "/value_col", "GROUP_AGGREGATE_VALUE", "count counts the rows of a group and reads no column.",
                    "remove value_col, or count the rows that have a value with sum of an expression that is 1 or 0");
                return Unusable(context, name);
            }
        }
        else if (spec.ValueCol is null)
        {
            context.Error(at, "GROUP_AGGREGATE_VALUE", $"{spec.Fn} needs value_col, the numeric column it reads.",
                $"add \"value_col\", e.g. \"value_col\": \"score.final\"" + (function == Function.Rank ? "" : "; count needs none"));
            return Unusable(context, name);
        }
        else if (!context.TryReadColumn(spec.ValueCol, at + "/value_col", out var v))
        {
            return Unusable(context, name);
        }
        else if (!v.Type.IsNumeric)
        {
            context.Error(at + "/value_col", "COLUMN_TYPE_MISMATCH", $"{spec.Fn} reads a numeric column, and '{spec.ValueCol}' is {v.Type}.",
                "give an int32, int64, float32 or float64 column; to count the rows of a group, use \"fn\": \"count\" without value_col");
            return Unusable(context, name);
        }
        else
        {
            value = v;
        }

        if (spec.Ascending && function != Function.Rank)
        {
            context.Error(at + "/ascending", "GROUP_AGGREGATE_VALUE", $"ascending orders a rank; {spec.Fn} has no order.", "remove it");
            return Unusable(context, name);
        }

        if (groupFailed)
        {
            return Unusable(context, name); // whether it may be missing depends on the group column
        }

        // A row is missing its value when it is in no group, or when its group has no value to aggregate (rank places
        // rows without one last, as TopK does, so it always has one).
        var integer = function is Function.Count or Function.Rank;
        var mayBeMissing = group is { Type.Nullable: true } || (!integer && value is { Type.Nullable: true });
        var produced = integer ? ColumnType.Int64 : ColumnType.Float64;
        produced = mayBeMissing ? produced.AsNullable() : produced;
        if (!context.TryWriteColumn(name, at, produced, out var output))
        {
            return null;
        }

        // A declared column keeps its type, which must be one it can write; an undeclared one is what it produces.
        var kinds = integer ? "int64 or int32" : "float64 or float32";
        if (integer ? !output.Type.IsInteger : !output.Type.IsFloat)
        {
            context.Error(at, "COLUMN_TYPE_MISMATCH", $"{spec.Fn} writes {kinds}, and '{name}' is declared as {output.Type}.",
                $"declare it as {produced}");
            return null;
        }

        if (mayBeMissing && !output.Type.Nullable)
        {
            context.Error(at, "COLUMN_TYPE_MISMATCH",
                $"'{name}' is declared {output.Type}, but {spec.Fn} leaves it missing for a row "
                + (group is { Type.Nullable: true } ? $"whose {group.Value.Name} is missing" : $"of a group with no {value!.Value.Name}") + ".",
                $"declare it as {output.Type.AsNullable()}, and read it with '??' or has()");
            return null;
        }

        return new Output(function, value, spec.Ascending, output);
    }

    /// <summary>
    /// An output the node cannot declare for an error reported already: readers of an undeclared one fail without a second
    /// error (a declared one they read as declared). Null.
    /// </summary>
    private static Output? Unusable(OperatorCompileContext context, string name)
    {
        if (!context.Schema.TryGetColumn(name, out var declared) || declared.Implicit)
        {
            context.Schema.MarkUnusable("col." + name);
        }

        return null;
    }

    private enum Function : byte
    {
        Count,
        Sum,
        Mean,
        Min,
        Max,
        Std,
        Rank,
    }

    private sealed record Output(Function Function, ColumnRef? Value, bool Ascending, OutputColumn Column);

    private sealed class GroupAggregate(ColumnRef? group, Output[] outputs) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var live = context.LiveRows;
            if (live.Length == 0)
            {
                return ValueTask.CompletedTask;
            }

            // Every buffer here is over once the outputs are written: scratch, given back at once, so the outputs reuse
            // each other's buffers and a high-cardinality grouping holds them only while this node runs.
            var memory = context.Memory;
            var groupOf = memory.RentScratch<int>(live.Length);
            var perRow = memory.RentScratch<double>(live.Length);
            var present = memory.RentScratch<bool>(live.Length);
            var groups = group is { } g ? RowGroups.Assign(context.Read(g), live, groupOf, memory) : One(groupOf.AsSpan(0, live.Length));
            foreach (var output in outputs)
            {
                var values = perRow.AsSpan(0, live.Length);
                var has = present.AsSpan(0, live.Length);
                if (output.Function == Function.Rank)
                {
                    Rank(context, output, live, groupOf, values, has);
                }
                else
                {
                    Aggregate(context, output, live, groupOf, groups, values, has);
                }

                Write(context.Output(output.Column), live, values, has);
            }

            memory.ReturnScratch(present);
            memory.ReturnScratch(perRow);
            memory.ReturnScratch(groupOf);
            return ValueTask.CompletedTask;
        }

        private static int One(Span<int> groupOf)
        {
            groupOf.Clear();
            return 1;
        }

        /// <summary>Each row's group statistic into <paramref name="values"/>; whether each row has one into <paramref name="present"/>.</summary>
        private static void Aggregate(
            OperatorContext context, Output output, ReadOnlySpan<int> live, int[] groupOf, int groups, Span<double> values, Span<bool> present)
        {
            var memory = context.Memory;
            var n = live.Length;
            var column = output.Value is { } v ? context.Read(v) : null;
            var counts = memory.RentScratch<long>(groups);
            var totals = memory.RentScratch<double>(groups); // the sum, min or max; the running mean for mean and std
            var peaks = output.Function == Function.Std ? memory.RentScratch<double>(groups) : null; // the largest |x|
            counts.AsSpan(0, groups).Clear();
            totals.AsSpan(0, groups).Clear();
            peaks?.AsSpan(0, groups).Clear();
            for (var i = 0; i < n; i++)
            {
                var g = groupOf[i];
                if (g < 0)
                {
                    continue;
                }

                if (output.Function == Function.Count)
                {
                    counts[g]++;
                    continue;
                }

                if (column is null || !column.IsValid(live[i]))
                {
                    continue;
                }

                var x = Read(column, live[i]);
                var k = ++counts[g];
                switch (output.Function)
                {
                    case Function.Min:
                        totals[g] = k == 1 ? x : Math.Min(totals[g], x);
                        break;
                    case Function.Max:
                        totals[g] = k == 1 ? x : Math.Max(totals[g], x);
                        break;
                    case Function.Sum:
                        totals[g] = k == 1 ? x : totals[g] + x;
                        break;
                    default:
                        totals[g] = RunningMean.Step(totals[g], x, k);
                        if (peaks is not null)
                        {
                            peaks[g] = Math.Max(peaks[g], Math.Abs(x));
                        }

                        break;
                }
            }

            double[]? scales = null, sums = null, squares = null;
            if (output.Function == Function.Std)
            {
                scales = memory.RentScratch<double>(groups);
                sums = memory.RentScratch<double>(groups);
                squares = memory.RentScratch<double>(groups);
                Deviations(column!, live, groupOf, totals, peaks!, scales, sums, squares, groups);
            }

            for (var i = 0; i < n; i++)
            {
                var g = groupOf[i];
                present[i] = g >= 0 && counts[g] > 0;
                if (present[i])
                {
                    values[i] = output.Function switch
                    {
                        Function.Count => counts[g],
                        Function.Std => Std(scales![g], sums![g], squares![g], counts[g], peaks![g]),
                        _ => totals[g],
                    };
                }
            }

            if (scales is not null)
            {
                memory.ReturnScratch(squares!);
                memory.ReturnScratch(sums!);
                memory.ReturnScratch(scales);
                memory.ReturnScratch(peaks!);
            }

            memory.ReturnScratch(totals);
            memory.ReturnScratch(counts);
        }

        /// <summary>
        /// The factor a group's values are taken at: halved when its largest |x| is over half the largest float64, so that
        /// a deviation between values of opposite signs does not overflow; as they are otherwise. Halving is exact but for
        /// subnormals, which in such a group are far below the deviations' rounding.
        /// </summary>
        private static double Factor(double peak) => peak > double.MaxValue / 2 ? 0.5 : 1;

        /// <summary>
        /// The deviations of each group's values from its center (its running mean, held within ±its largest |x|), taken at
        /// the group's <see cref="Factor"/>, as <c>scale · sums</c> (their signed sum) and <c>scale² · squares</c> (the sum
        /// of their squares), with every deviation divided by the group's largest (LAPACK's dlassq): no square of a large
        /// deviation overflows and none of a small one underflows, so the std is right at every scale a float64 holds
        /// (±1e200 has std 1e200 and ±1e-200 has 1e-200; summing the squares themselves made the first infinite and the
        /// second 0, pre-release assessment).
        /// </summary>
        /// <remarks>
        /// The signed sum is the corrected two-pass term: the running mean is rounded at the values' scale, and the
        /// deviations from it alone made the std of 1e16+6 and 1e16+4 alternating √2 instead of 1, and that of 0 and the
        /// smallest subnormal that subnormal instead of 0 (review 78); the variance about the true mean is
        /// <c>E[d²] − E[d]²</c> of the deviations d from any center. Holding the center within ±largest |x| (a rounded mean
        /// can step past it) keeps every deviation within twice that, which the factor keeps finite.
        /// </remarks>
        private static void Deviations(
            ColumnBuffer column, ReadOnlySpan<int> live, int[] groupOf, double[] means, double[] peaks, double[] scales, double[] sums,
            double[] squares, int groups)
        {
            for (var g = 0; g < groups; g++)
            {
                means[g] = Math.Clamp(means[g], -peaks[g], peaks[g]) * Factor(peaks[g]); // the center, at the group's factor
                scales[g] = 0;
                sums[g] = 0;
                squares[g] = 0;
            }

            for (var i = 0; i < live.Length; i++)
            {
                var g = groupOf[i];
                if (g < 0 || !column.IsValid(live[i]))
                {
                    continue;
                }

                var d = (Read(column, live[i]) * Factor(peaks[g])) - means[g];
                var size = Math.Abs(d);
                if (size == 0)
                {
                    continue; // equal to the center: adds nothing, and the std of equal values stays exactly 0
                }

                if (scales[g] < size)
                {
                    var r = scales[g] / size;
                    sums[g] = Math.CopySign(1, d) + (sums[g] * r);
                    squares[g] = 1 + (squares[g] * r * r);
                    scales[g] = size;
                }
                else
                {
                    var r = d / scales[g];
                    sums[g] += r;
                    squares[g] += r * r;
                }
            }
        }

        /// <summary>
        /// A group's population std from its <see cref="Deviations"/>: <c>scale · √(squares/n − (sums/n)²)</c>, the
        /// difference clamped at 0, then bounded by the group's largest |x| (a population std is at most that) before it
        /// is divided by the factor: rounding can no longer carry it past the largest float64 (four −MaxValue then four
        /// +MaxValue have std MaxValue; the rounded mean made it infinite and the node failed, review 78).
        /// </summary>
        private static double Std(double scale, double sums, double squares, long count, double peak)
        {
            if (scale == 0)
            {
                return 0;
            }

            var mean = sums / count;
            var variance = (squares / count) - (mean * mean);
            var factor = Factor(peak);
            return variance > 0 ? Math.Min(scale * Math.Sqrt(variance), peak * factor) / factor : 0;
        }

        /// <summary>Each row's rank within its group into <paramref name="values"/>; whether it has one into <paramref name="present"/>.</summary>
        private static void Rank(
            OperatorContext context, Output output, ReadOnlySpan<int> live, int[] groupOf, Span<double> values, Span<bool> present)
        {
            var n = live.Length;
            var ranks = context.Memory.RentScratch<long>(n);
            RowRanking.RankWithinGroups(live, groupOf.AsSpan(0, n), output.Value is { } v ? context.Read(v) : null, context.Table,
                output.Ascending, ranks, context.Memory);
            for (var i = 0; i < n; i++)
            {
                present[i] = groupOf[i] >= 0;
                values[i] = ranks[i];
            }

            context.Memory.ReturnScratch(ranks);
        }

        private static double Read(ColumnBuffer column, int row) => column switch
        {
            ScalarColumn<double> d => d[row],
            ScalarColumn<float> f => f[row],
            ScalarColumn<long> l => l[row],
            ScalarColumn<int> i => i[row],
            _ => throw new InvalidOperationException($"Column of type {column.Type} is not numeric."),
        };

        private static void Write(ColumnBuffer target, ReadOnlySpan<int> live, ReadOnlySpan<double> values, ReadOnlySpan<bool> present)
        {
            for (var i = 0; i < live.Length; i++)
            {
                var row = live[i];
                if (!present[i])
                {
                    target.SetMissing(row);
                    continue;
                }

                switch (target)
                {
                    case ScalarColumn<long> l:
                        l.Set(row, (long)values[i]);
                        break;
                    case ScalarColumn<int> x:
                        x.Set(row, (int)values[i]);
                        break;
                    case ScalarColumn<double> d:
                        d.Set(row, values[i]);
                        break;
                    case ScalarColumn<float> f:
                        f.Set(row, (float)values[i]);
                        break;
                }
            }
        }
    }
}
