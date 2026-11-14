using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Pipeline.Operators.Standard;

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
                context.Error("/group_col", "COLUMN_TYPE_MISMATCH", $"Group column '{config.GroupCol}' must be int64, int32 or string, not {g.Type}.");
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
            context.Error(at, "GROUP_AGGREGATE_VALUE", $"{spec.Fn} needs value_col, the numeric column it reads.");
            return Unusable(context, name);
        }
        else if (!context.TryReadColumn(spec.ValueCol, at + "/value_col", out var v))
        {
            return Unusable(context, name);
        }
        else if (!v.Type.IsNumeric)
        {
            context.Error(at + "/value_col", "COLUMN_TYPE_MISMATCH", $"{spec.Fn} reads a numeric column, and '{spec.ValueCol}' is {v.Type}.");
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
        var declared = context.Schema.TryGetColumn(name, out var logical) && !logical.Implicit;
        if (!context.TryWriteColumn(name, at, declared ? null : produced, out var output))
        {
            return null;
        }

        if (declared)
        {
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

            var memory = context.Memory;
            var groupOf = memory.Rent<int>(live.Length);
            var groups = group is { } g ? RowGroups.Assign(context.Read(g), live, groupOf, memory) : One(groupOf.AsSpan(0, live.Length));
            var perRow = memory.Rent<double>(live.Length);
            foreach (var output in outputs)
            {
                var values = perRow.AsSpan(0, live.Length);
                var present = output.Function == Function.Rank
                    ? Rank(context, output, live, groupOf, values)
                    : Aggregate(context, output, live, groupOf, groups, values);
                Write(context.Output(output.Column), live, values, present);
            }

            return ValueTask.CompletedTask;
        }

        private static int One(Span<int> groupOf)
        {
            groupOf.Clear();
            return 1;
        }

        /// <summary>Each row's group statistic into <paramref name="values"/>; whether each row has one.</summary>
        private static Span<bool> Aggregate(
            OperatorContext context, Output output, ReadOnlySpan<int> live, int[] groupOf, int groups, Span<double> values)
        {
            var memory = context.Memory;
            var n = live.Length;
            var column = output.Value is { } v ? context.Read(v) : null;
            var counts = memory.RentCleared<long>(groups);
            var totals = memory.RentCleared<double>(groups);
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
                totals[g] = counts[g] == 0 ? x : output.Function switch
                {
                    Function.Min => Math.Min(totals[g], x),
                    Function.Max => Math.Max(totals[g], x),
                    _ => totals[g] + x,
                };
                counts[g]++;
            }

            if (output.Function == Function.Std)
            {
                // Two passes: the squared deviations from the group's mean, which does not cancel as sum of squares does.
                var deviations = memory.RentCleared<double>(groups);
                for (var i = 0; i < n; i++)
                {
                    var g = groupOf[i];
                    if (g >= 0 && column is not null && column.IsValid(live[i]))
                    {
                        var d = Read(column, live[i]) - (totals[g] / counts[g]);
                        deviations[g] += d * d;
                    }
                }

                totals = deviations;
            }

            var present = context.Memory.Rent<bool>(n).AsSpan(0, n);
            for (var i = 0; i < n; i++)
            {
                var g = groupOf[i];
                present[i] = g >= 0 && counts[g] > 0;
                if (present[i])
                {
                    values[i] = output.Function switch
                    {
                        Function.Count => counts[g],
                        Function.Mean => totals[g] / counts[g],
                        Function.Std => Math.Sqrt(totals[g] / counts[g]),
                        _ => totals[g],
                    };
                }
            }

            return present;
        }

        private static Span<bool> Rank(OperatorContext context, Output output, ReadOnlySpan<int> live, int[] groupOf, Span<double> values)
        {
            var n = live.Length;
            var ranks = context.Memory.Rent<long>(n);
            RowRanking.RankWithinGroups(live, groupOf.AsSpan(0, n), output.Value is { } v ? context.Read(v) : null, context.Table,
                output.Ascending, ranks, context.Memory);
            var present = context.Memory.Rent<bool>(n).AsSpan(0, n);
            for (var i = 0; i < n; i++)
            {
                present[i] = groupOf[i] >= 0;
                values[i] = ranks[i];
            }

            return present;
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
