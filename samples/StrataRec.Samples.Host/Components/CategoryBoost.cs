using System.ComponentModel;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Samples.Host.Components;

/// <summary>
/// Config of <c>CategoryBoost@1</c>. The class is the operator's whole configuration contract: the engine binds a node's
/// <c>config</c> to it strictly (snake_case members, unknown members rejected), and its JSON Schema, with the
/// <see cref="DescriptionAttribute"/> texts, is generated from it (<c>dotnet run -- stratarec schema operator CategoryBoost@1</c>).
/// </summary>
public sealed class CategoryBoostConfig
{
    [Description("Float column to boost, e.g. score.engage. Non-nullable: a row without a score cannot be ranked.")]
    public required string ScoreCol { get; init; }

    [Description("String column whose value picks the boost, e.g. item.category. A row whose value is missing, or not " +
        "listed in 'boosts', keeps its score.")]
    public required string CategoryCol { get; init; }

    [Description("Column to write, declared under 'columns' as float32 or float64: the score times the row's boost.")]
    public required string OutputCol { get; init; }

    [Description("Category → multiplier, each finite and greater than 0, e.g. { \"local_news\": 1.5 }. An experiment " +
        "patches single entries (/nodes/<node>/config/boosts/<category>).")]
    public required Dictionary<string, double> Boosts { get; init; }
}

/// <summary>
/// <c>CategoryBoost@1</c>: a business rule as an operator. It multiplies a score by a per-category factor and writes
/// the result to another column; a sort after it (<c>TopK@1</c>) turns that into the ranking.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It only writes a column, so it declares <see cref="Effects.ColumnWriter"/> and nothing else; the engine
/// enforces the declaration.</item>
/// <item>Compile does every check, once per node variant: the config, the columns it reads (so the features it needs
/// are fetched before it runs) and the column it writes. The request path checks nothing again.</item>
/// <item>The compiled operator is immutable and shared by every request. Buffers come from the request's
/// <see cref="Core.Memory.RequestMemory"/>; per row it allocates nothing.</item>
/// <item>A row whose category is missing keeps its score: this operator's stated rule, not a default standing in for
/// the category.</item>
/// <item>It does no I/O, so it has nothing to cancel; a long CPU loop would check
/// <see cref="OperatorContext.CancellationToken"/>. A service is called through <see cref="OperatorContext.CallAsync"/>
/// (<see cref="StockFilterFactory"/> is one).</item>
/// </list>
/// </remarks>
public sealed class CategoryBoostFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("CategoryBoost", 1, Effects.ColumnWriter, typeof(CategoryBoostConfig),
        "Multiplies a score by a per-category factor and writes it to a column.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<CategoryBoostConfig>();
        if (config is null)
        {
            return null;
        }

        // Reads before the write: when output_col is a flow column that score_col also names, the read resolves to the
        // version before this node.
        if (!context.TryReadColumn(config.ScoreCol, "/score_col", out var score, requireNonNullable: true)
            || !context.TryReadColumn(config.CategoryCol, "/category_col", out var category))
        {
            return null;
        }

        var ok = true;
        if (!score.Type.IsFloat)
        {
            context.Error("/score_col", "COLUMN_TYPE_MISMATCH", $"Score column '{score.Name}' must be float32 or float64, not {score.Type}.");
            ok = false;
        }

        if (category.Type.Kind != StorageKind.String)
        {
            context.Error("/category_col", "COLUMN_TYPE_MISMATCH", $"Category column '{category.Name}' must be a string column, not {category.Type}.",
                "use a string feature or column such as item.category");
            ok = false;
        }

        // Checked in ordinal order, so the errors do not depend on how the JSON object was written. The map is built once
        // here and only read by requests, which a Dictionary allows from any number of threads.
        var names = new List<string>(config.Boosts.Keys);
        names.Sort(StringComparer.Ordinal);
        var boosts = new Dictionary<string, double>(names.Count, StringComparer.Ordinal);
        foreach (var name in names)
        {
            var factor = config.Boosts[name];
            if (!double.IsFinite(factor) || factor <= 0)
            {
                context.Error("/boosts/" + JsonPointer.Escape(name), "BOOST_INVALID", $"The boost of '{name}' is {factor}; it must be finite and greater than 0.",
                    "use 1.0 for no change, or remove the entry");
                ok = false;
            }

            boosts[name] = factor;
        }

        if (!ok || !context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output))
        {
            return null;
        }

        if (!output.Type.IsFloat)
        {
            context.Error("/output_col", "COLUMN_TYPE_MISMATCH", $"Output column '{output.Name}' must be float32 or float64, not {output.Type}.");
            return null;
        }

        return new CategoryBoost(score, category, output, boosts);
    }

    private sealed class CategoryBoost(ColumnRef score, ColumnRef category, OutputColumn output, Dictionary<string, double> boosts) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var rows = context.LiveRows;
            if (rows.Length == 0)
            {
                return ValueTask.CompletedTask;
            }

            if (context.Read(score) is not { } scores)
            {
                context.Fail($"score column '{score.Name}' was not produced in this request");
                return ValueTask.CompletedTask;
            }

            // A string column holds a string reference per row, and the rows of one category usually share an instance
            // (the local table's, or the one a connector interned), so a row holding the previous row's instance reuses
            // its factor without a lookup.
            var categories = context.Read(category) as StringColumn;
            var memory = context.Memory;

            var floats = scores as ScalarColumn<float>;
            var doubles = scores as ScalarColumn<double>;
            if (floats is null && doubles is null)
            {
                throw new InvalidOperationException($"Score column '{score.Name}' must be float32 or float64.");
            }

            var boosted = memory.Rent<double>(rows.Length);
            string? last = null;
            var lastFactor = 1.0;
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                var factor = 1.0;
                if (categories is not null && categories.IsValid(row))
                {
                    var value = categories.Get(row);
                    if (!ReferenceEquals(value, last))
                    {
                        last = value;
                        lastFactor = boosts.TryGetValue(value, out var listed) ? listed : 1.0;
                    }

                    factor = lastFactor;
                }

                boosted[i] = factor * (floats is not null ? floats[row] : doubles![row]);
            }

            switch (context.Output(output))
            {
                case ScalarColumn<float> f:
                    for (var i = 0; i < rows.Length; i++)
                    {
                        f.Set(rows[i], (float)boosted[i]);
                    }

                    break;
                case ScalarColumn<double> d:
                    for (var i = 0; i < rows.Length; i++)
                    {
                        d.Set(rows[i], boosted[i]);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Output column '{output.Name}' must be float32 or float64.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
