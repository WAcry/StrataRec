using System.ComponentModel;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Samples.Host.Components;

/// <summary>
/// Config of <c>CategoryBoost@1</c>. The class is the operator's whole configuration contract: the engine binds a node's
/// <c>config</c> to it strictly (snake_case members, unknown members rejected), and its JSON Schema, with the
/// <see cref="DescriptionAttribute"/> texts, is generated from it (<c>dotnet run -- schema CategoryBoost@1</c>).
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
/// <c>CategoryBoost@1</c>: a business rule as an operator. It multiplies a score by a per-category factor and writes the
/// result to another column; a sort after it (<c>TopK@1</c>) turns that into the ranking.
/// </summary>
/// <remarks>
/// <para>How an operator is written:</para>
/// <list type="bullet">
/// <item>Effects: it only writes a column, so it declares <see cref="Effects.ColumnWriter"/> and nothing else. The
/// engine enforces the declaration (filtering or reordering rows would throw), and data-flow checks and experiment
/// footprints rely on it.</item>
/// <item>Compile does every check, once per node variant: the config, the columns it reads (recorded as the node's reads,
/// so the features it needs are fetched before it runs) and the column it writes. Errors point into the config with a
/// hint; the request path checks nothing again.</item>
/// <item>The compiled operator is immutable and shared by every request. A request's buffers come from its
/// <see cref="Core.Memory.RequestMemory"/> and are never returned by the operator; per row it does array reads and one
/// multiplication, and allocates nothing.</item>
/// <item>Missing values: the score must be non-nullable (checked at compile). A row whose category is missing keeps its
/// score; that is this operator's stated rule, not a default value standing in for the category.</item>
/// <item>Budget and cancellation: it does no I/O and takes microseconds, so it has nothing to cancel; an attempt that
/// returns after the node's budget has timed out all the same. A long CPU loop would check
/// <see cref="OperatorContext.CancellationToken"/> between chunks. An operator that calls a model or a source does it
/// through <see cref="OperatorContext.InvokeModelAsync"/> or <see cref="OperatorContext.RetrieveAsync"/>, which bound,
/// record and replay the call.</item>
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

        // Ordinal order, so the compiled operator does not depend on how the JSON object was written.
        var names = new List<string>(config.Boosts.Keys);
        names.Sort(StringComparer.Ordinal);
        var boosts = new (string Category, double Factor)[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var factor = config.Boosts[names[i]];
            if (!double.IsFinite(factor) || factor <= 0)
            {
                context.Error("/boosts/" + JsonPointer.Escape(names[i]), "BOOST_INVALID", $"The boost of '{names[i]}' is {factor}; it must be finite and greater than 0.",
                    "use 1.0 for no change, or remove the entry");
                ok = false;
            }

            boosts[i] = (names[i], factor);
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

    private sealed class CategoryBoost(ColumnRef score, ColumnRef category, OutputColumn output, (string Category, double Factor)[] boosts) : Operator
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

            // A string column is dictionary-encoded: translate each listed category into the column's code once, then a row's
            // factor is one array read by its code. A column not produced in this request has no categories: no row is boosted.
            var categories = context.Read(category) as DictStringColumn;
            var memory = context.Memory;
            var factorByCode = memory.Rent<double>(categories?.Dictionary.Count ?? 1);
            factorByCode.AsSpan().Fill(1.0);
            if (categories is not null)
            {
                foreach (var (name, factor) in boosts)
                {
                    if (categories.FindCode(name) is var code and >= 0)
                    {
                        factorByCode[code] = factor;
                    }
                }
            }

            // The boosted score of each live row, by rank position.
            var floats = scores as ScalarColumn<float>;
            var doubles = scores as ScalarColumn<double>;
            if (floats is null && doubles is null)
            {
                throw new InvalidOperationException($"Score column '{score.Name}' must be float32 or float64.");
            }

            var boosted = memory.Rent<double>(rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                var factor = categories is not null && categories.IsValid(row) ? factorByCode[categories.Codes[row]] : 1.0;
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
