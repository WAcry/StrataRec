using System.Globalization;
using System.Text.Json.Serialization;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Llm.Templates;
using StrataRec.Models;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Llm;

/// <summary>Config of <c>GenerativeRetriever@1</c> (design 5.9).</summary>
public sealed class GenerativeRetrieverConfig
{
    /// <summary>Logical model name, bound per environment.</summary>
    [JsonPropertyOrder(-1)]
    public required string Model { get; init; }

    /// <summary>Fixed model version; or use <see cref="VersionParam"/>.</summary>
    [JsonPropertyOrder(-1)]
    public string? Version { get; init; }

    /// <summary>String parameter (with an <c>enum</c> of allowed versions) selecting the version per request.</summary>
    [JsonPropertyOrder(-1)]
    public string? VersionParam { get; init; }

    /// <summary>
    /// Model text input name → Mustache template of its prompt for the request (<see cref="PromptTemplate"/>, per-request
    /// scope): <c>ctx.*</c> and <c>param.*</c>. No <c>col.*</c>: the prompt is rendered once, for no row.
    /// </summary>
    public required Dictionary<string, string> Prompts { get; init; }

    /// <summary>The model's text output holding the answer: a JSON array of entity ids (integers for int64 keys, strings for string keys).</summary>
    public required string Output { get; init; }

    /// <summary>
    /// The most rows the node appends: the first new ids of the answer. Also the node's row limit, which sizes the request's
    /// candidate table (design 4.8).
    /// </summary>
    public required int MaxItems { get; init; }

    /// <summary>
    /// A column for each appended row's 1-based position in the answer: <c>int64?</c> or <c>int32?</c> (undeclared:
    /// <c>int64?</c>). Rows the node did not append have no value in it.
    /// </summary>
    public string? RankCol { get; init; }

    /// <summary>The most tokens the model may generate (design 5.9 rule 2), passed with the call.</summary>
    public int MaxOutputTokens { get; init; } = 256;
}

/// <summary>
/// <c>GenerativeRetriever@1</c> (RowSource): appends the candidates a generative model names, the ones other sources did
/// not have, from prompts rendered once per request (design 5.9).
/// </summary>
/// <remarks>
/// <para>One call: batch size 1, the request's seed, <c>max_output_tokens</c>, recorded and replayed by the digest of the
/// whole call, since the answer belongs to the request, not to a row. The answer is parsed by <see cref="LlmAnswer"/>: a
/// JSON array (optionally in one Markdown code fence) of ids of the table's key type. An element of another type is
/// unknown, an id named before is repeated, an id the table already has is present; each is skipped, and after
/// <c>max_items</c> used ids the rest is over the limit. Every element is counted
/// (<see cref="StrataRecMetrics.ModelAnswerIds"/>). An empty array, or one with nothing new, appends nothing: a valid
/// answer.</para>
/// <para>An id already in the table keeps its row, its place and its values: a row source appends rows and writes only
/// those (design 4.6), so the node neither moves another source's row nor marks it; the count of <c>present</c> ids shows
/// how much the model repeats the other sources.</para>
/// <para>The appended rows have no features yet: the fetch planner fills them before any later node reads one, like the
/// rows of any row source (design 5.9 rule 4); an id no feature source knows has its features' defaults or missing values.</para>
/// <para>The node fails, appending nothing (<c>on_failure</c> decides), when the call fails or when the answer is not such
/// an array. The message names the model and quotes the start of the answer; when the client said it stopped at
/// <c>max_output_tokens</c>, it says so, since a cut-off array is the likely cause.</para>
/// </remarks>
public sealed class GenerativeRetrieverFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("GenerativeRetriever", 1, Effects.RowSource, typeof(GenerativeRetrieverConfig),
        "Appends the candidates a generative model names that the table does not have, from prompts rendered with Mustache templates.");

    public Operator? Compile(OperatorCompileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var config = context.BindConfig<GenerativeRetrieverConfig>();
        if (config is null)
        {
            return null;
        }

        var model = BoundModel.Resolve(context, config.Model, config.Version, config.VersionParam);
        if (model is null)
        {
            return null;
        }

        var ok = true;
        if (config.MaxItems < 1)
        {
            context.Error("/max_items", "LLM_MAX_ITEMS", $"max_items is {config.MaxItems.ToString(CultureInfo.InvariantCulture)}, but it must be at least 1.",
                "max_items is the most rows the node appends, the first new ids of the answer; it also sizes the request's candidate table");
            ok = false;
        }

        ok &= LlmNode.CheckMaxOutputTokens(context, config.MaxOutputTokens,
            "the answer lists up to 'max_items' new ids and whatever else the model names, so allow several tokens per id (the default is 256)");
        ok &= LlmNode.CheckOutput(context, config.Output);
        OutputColumn? rank = null;
        if (config.RankCol is not null)
        {
            rank = CompileRank(context, config.RankCol, ref ok);
        }

        var prompts = LlmNode.CompilePrompts(context, config.Prompts, PromptScope.Request,
            "{ \"user\": \"Suggest items for the query '{{ctx.request.query}}' as a JSON array of ids.\" }", ref ok);
        if (!ok || !LlmNode.CheckSignatures(context, model, prompts, config.Output))
        {
            return null;
        }

        return new GenerativeRetriever(model, prompts, config.Output, config.MaxItems, rank, config.MaxOutputTokens);
    }

    /// <summary><c>rank_col</c>: a nullable integer column, since the rows other sources append have no position in this answer.</summary>
    private OutputColumn? CompileRank(OperatorCompileContext context, string name, ref bool ok)
    {
        var declared = context.Schema.TryGetColumn(name, out _);
        if (!context.TryWriteColumn(name, "/rank_col", declared ? null : ColumnType.Int64.AsNullable(), out var written))
        {
            ok = false;
            return null;
        }

        if (!written.Type.IsInteger || !written.Type.Nullable)
        {
            context.Error("/rank_col", "COLUMN_TYPE_MISMATCH",
                $"Rank column '{name}' is {written.Type}, but {Descriptor.Id} writes it only for the rows it appends: it must be int64? or int32?.",
                $"declare '{name}' as int64? or int32?, or leave it undeclared to write int64?");
            ok = false;
            return null;
        }

        return written;
    }

    private sealed class GenerativeRetriever(
        BoundModel model, (string Name, PromptTemplate Template)[] prompts, string output, int maxItems, OutputColumn? rank, int maxOutputTokens)
        : Operator
    {
        private readonly KeyValuePair<string, object?>[] _used = LlmNode.Tags(model, "used");
        private readonly KeyValuePair<string, object?>[] _unknown = LlmNode.Tags(model, "unknown");
        private readonly KeyValuePair<string, object?>[] _repeated = LlmNode.Tags(model, "repeated");
        private readonly KeyValuePair<string, object?>[] _present = LlmNode.Tags(model, "present");
        private readonly KeyValuePair<string, object?>[] _overLimit = LlmNode.Tags(model, "over_limit");

        public override IoTarget ModelTarget => model.Target;

        public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => maxItems;

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var inputs = new NamedInput[prompts.Length];
            for (var i = 0; i < prompts.Length; i++)
            {
                inputs[i] = NamedInput.FromText(prompts[i].Name, prompts[i].Template.Render(context));
            }

            var writer = new ModelOutputWriter(context.Memory);
            var status = await model.InvokeAsync(context, model.Version(context), inputs, 1, writer, ReadOnlyMemory<int>.Empty, maxOutputTokens)
                .ConfigureAwait(false);
            if (!status.Succeeded)
            {
                context.Fail($"model '{model.Model}' failed: {status.Error}");
                return;
            }

            if (!writer.TryGetText(output, out var text))
            {
                context.Fail($"model '{model.Model}' did not return text output '{output}'{Cut(status)}");
                return;
            }

            Append(context, text, status);
        }

        /// <summary>Parses the answer and appends its new ids; fails the node, appending nothing, when it is not a JSON array.</summary>
        private void Append(OperatorContext context, string text, in ModelStatus status)
        {
            var table = context.Table;
            var memory = context.Memory;
            var stringKeys = table.KeyType == EntityKeyType.String;

            // The planner sized the table for max_items more rows (GetRowLimit); the check only keeps a miscount from throwing.
            var limit = Math.Min(maxItems, table.FreeRows);
            var int64Ids = stringKeys ? null : memory.Rent<long>(Math.Max(1, limit));
            var stringIds = stringKeys ? memory.Rent<string>(Math.Max(1, limit)) : null;
            var positions = memory.Rent<int>(Math.Max(1, limit));
            var invalid = LlmAnswer.ParseIds(text, table, limit, memory, int64Ids, stringIds, positions, out var counts);
            if (invalid is not null)
            {
                context.Fail($"model '{model.Model}' did not answer a JSON array of entity ids ({(stringKeys ? "strings" : "integers")}): {invalid}; "
                    + $"the answer starts {LlmAnswer.Quote(text)}{Cut(status)}");
                return;
            }

            var ids = StrataRecMetrics.ModelAnswerIds;
            LlmNode.Count(ids, context, counts.Used, _used);
            LlmNode.Count(ids, context, counts.Unknown, _unknown);
            LlmNode.Count(ids, context, counts.Repeated, _repeated);
            LlmNode.Count(ids, context, counts.Present, _present);
            LlmNode.Count(ids, context, counts.OverLimit, _overLimit);
            var count = counts.Used;
            if (count == 0)
            {
                return;
            }

            var first = stringKeys
                ? context.AppendRows(new ArraySegment<string>(stringIds!, 0, count))
                : context.AppendRows(int64Ids.AsSpan(0, count));
            if (rank is not { } column)
            {
                return;
            }

            // The appended rows are consecutive: values row by row, their validity once for the range.
            switch (context.Output(column))
            {
                case ScalarColumn<long> longs:
                    for (var i = 0; i < count; i++)
                    {
                        longs.Values[first + i] = positions[i];
                    }

                    longs.SetValid(first, count);
                    break;
                case ScalarColumn<int> ints:
                    for (var i = 0; i < count; i++)
                    {
                        ints.Values[first + i] = positions[i];
                    }

                    ints.SetValid(first, count);
                    break;
                default:
                    throw new InvalidOperationException($"Rank column '{column.Name}' is {column.Type}, not int64? or int32?.");
            }
        }

        /// <summary>The hint for an answer the client says it cut at the limit; empty otherwise.</summary>
        private string Cut(in ModelStatus status) => LlmNode.Cut(status, maxOutputTokens, "ask for fewer ids");
    }
}
