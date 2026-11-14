using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using StrataRec.Core.Diagnostics;
using StrataRec.Llm.Templates;
using StrataRec.Models;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Llm;

/// <summary>Config of <c>LlmReranker@1</c> (design 5.9).</summary>
public sealed class LlmRerankerConfig
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
    /// Model text input name → Mustache template of its prompt (<see cref="PromptTemplate"/>): <c>{{#rows}}…{{/rows}}</c>
    /// over the window, <c>{{rank}}</c> and <c>col.*</c> inside it, <c>ctx.*</c> and <c>param.*</c> anywhere.
    /// </summary>
    public required Dictionary<string, string> Prompts { get; init; }

    /// <summary>The model's text output holding the answer: a JSON array of window ranks or entity ids (see <see cref="Answer"/>).</summary>
    public required string Output { get; init; }

    /// <summary>The leading rows the prompts show and the answer may reorder; later rows keep their order after them.</summary>
    public int Window { get; init; } = 20;

    /// <summary>
    /// What the answer lists: <c>rank</c>, the 1-based window positions <c>{{rank}}</c> shows, or <c>entity_id</c>, the
    /// rows' entity ids (JSON integers for int64 keys, strings for string keys). No default: the prompt decides.
    /// </summary>
    [AllowedValues("rank", "entity_id")]
    public required string Answer { get; init; }

    /// <summary>
    /// Window rows the answer does not list: <c>keep</c> them after the listed ones, in their previous order, or
    /// <c>drop</c> them.
    /// </summary>
    [AllowedValues("keep", "drop")]
    public string Unlisted { get; init; } = "keep";

    /// <summary>The most tokens the model may generate (design 5.9 rule 2), passed with the call.</summary>
    public int MaxOutputTokens { get; init; } = 256;
}

/// <summary>
/// <c>LlmReranker@1</c> (RowOrder; RowFilter with <c>"unlisted": "drop"</c>): reranks the leading <c>window</c> rows in
/// the order a generative model lists them, from prompts rendered with Mustache templates (design 5.9).
/// </summary>
/// <remarks>
/// <para>n = min(window, rows); no call when n is 0. Each prompt is rendered once with the first n rows, and the model is
/// called once: batch size 1, the request's seed, <c>max_output_tokens</c>, recorded and replayed by the digest of the
/// whole call (the prompts and the limit), since the answer belongs to the window, not to a row.</para>
/// <para>The answer is parsed by <see cref="LlmAnswer"/>: a JSON array (optionally in a Markdown code fence) of window
/// ranks or entity ids. Elements naming no window row, and repeats, are ignored and counted
/// (<see cref="StrataRecMetrics.ModelAnswerIds"/>). The new order is the listed rows in the answer's order, then the
/// unlisted window rows in their previous order (<c>keep</c>) or none (<c>drop</c>), then the rows after the window.</para>
/// <para>The node fails, changing nothing (<c>on_failure</c> decides; with <c>skip</c> the order stays), when the call
/// fails, when the answer is not such an array, or when no element names a window row. The message names the model and
/// quotes the start of the answer; when the client said it stopped at <c>max_output_tokens</c>, it says so, since a
/// cut-off array is the likely cause.</para>
/// </remarks>
public sealed class LlmRerankerFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("LlmReranker", 1, Effects.RowOrder | Effects.RowFilter, typeof(LlmRerankerConfig),
        "Reranks the leading rows in the order a generative model lists them, from prompts rendered with Mustache templates.");

    public Operator? Compile(OperatorCompileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var config = context.BindConfig<LlmRerankerConfig>();
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
        if (config.Window < 1)
        {
            context.Error("/window", "RANKER_WINDOW", $"window is {config.Window}, but it must be at least 1.");
            ok = false;
        }

        ok &= LlmNode.CheckMaxOutputTokens(context, config.MaxOutputTokens,
            "the answer lists up to 'window' ranks or ids, so allow a few tokens per row (the default is 256)");
        AnswerKind answer = default;
        switch (config.Answer)
        {
            case "rank":
                answer = AnswerKind.Rank;
                break;
            case "entity_id":
                answer = AnswerKind.EntityId;
                break;
            default:
                context.Error("/answer", "LLM_ANSWER", $"answer is '{config.Answer}', but it must be \"rank\" or \"entity_id\".",
                    "\"rank\" when the prompt shows {{rank}} and asks for positions, \"entity_id\" when it shows {{col.entity_id}} and asks for ids");
                ok = false;
                break;
        }

        var drop = false;
        switch (config.Unlisted)
        {
            case "keep":
                break;
            case "drop":
                drop = true;
                break;
            default:
                context.Error("/unlisted", "LLM_UNLISTED", $"unlisted is '{config.Unlisted}', but it must be \"keep\" or \"drop\".",
                    "\"keep\" places the window rows the answer does not list after the listed ones; \"drop\" removes them");
                ok = false;
                break;
        }

        ok &= LlmNode.CheckOutput(context, config.Output);
        var prompts = LlmNode.CompilePrompts(context, config.Prompts, PromptScope.Window,
            "{ \"user\": \"Rank:\\n{{#rows}}{{rank}}. {{col.item.title}}\\n{{/rows}}\" }", ref ok);
        if (!ok || !LlmNode.CheckSignatures(context, model, prompts, config.Output))
        {
            return null;
        }

        return new LlmReranker(model, prompts, config.Output, config.Window, answer, drop, config.MaxOutputTokens);
    }

    private sealed class LlmReranker(
        BoundModel model, (string Name, PromptTemplate Template)[] prompts, string output, int window, AnswerKind answer, bool drop, int maxOutputTokens)
        : Operator
    {
        private readonly KeyValuePair<string, object?>[] _used = LlmNode.Tags(model, "used");
        private readonly KeyValuePair<string, object?>[] _unknown = LlmNode.Tags(model, "unknown");
        private readonly KeyValuePair<string, object?>[] _repeated = LlmNode.Tags(model, "repeated");

        public override IoTarget ModelTarget => model.Target;

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var total = context.LiveRows.Length;
            var n = Math.Min(window, total);
            if (n == 0)
            {
                return;
            }

            // The rows as of the call, kept across the await; rented, and never handed to the model client.
            var memory = context.Memory;
            var rows = memory.Rent<int>(total);
            context.LiveRows.CopyTo(rows);
            var inputs = new NamedInput[prompts.Length];
            for (var i = 0; i < prompts.Length; i++)
            {
                inputs[i] = NamedInput.FromText(prompts[i].Name, prompts[i].Template.Render(context, rows.AsSpan(0, n)));
            }

            var writer = new ModelOutputWriter(memory);
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

            Apply(context, rows.AsSpan(0, total), n, text, status);
        }

        /// <summary>Parses the answer and sets the new order; fails the node, changing nothing, when the answer names no window row.</summary>
        /// <param name="rows">The live rows as of the call, in rank order; the first <paramref name="n"/> are the window.</param>
        private void Apply(OperatorContext context, ReadOnlySpan<int> rows, int n, string text, in ModelStatus status)
        {
            var memory = context.Memory;
            var shown = rows[..n];
            var positions = memory.Rent<int>(n);
            var listed = memory.RentCleared<bool>(n).AsSpan(0, n);
            var invalid = LlmAnswer.Parse(text, answer, context.Table, shown, memory, positions, listed, out var counts);
            if (invalid is not null)
            {
                context.Fail($"model '{model.Model}' did not answer a JSON array of {What(n)}: {invalid}; the answer starts {LlmAnswer.Quote(text)}{Cut(status)}");
                return;
            }

            Count(context, counts);
            if (counts.Used == 0)
            {
                context.Fail($"model '{model.Model}' answered {counts.Elements.ToString(CultureInfo.InvariantCulture)} element(s), none of them one of "
                    + $"the {What(n)}: {LlmAnswer.Quote(text)}{Cut(status)}");
                return;
            }

            var order = memory.Rent<int>(rows.Length);
            var count = 0;
            for (var i = 0; i < counts.Used; i++)
            {
                order[count++] = shown[positions[i]];
            }

            if (!drop)
            {
                for (var p = 0; p < n; p++)
                {
                    if (!listed[p])
                    {
                        order[count++] = shown[p];
                    }
                }
            }

            rows[n..].CopyTo(order.AsSpan(count));
            count += rows.Length - n;
            context.SetOrder(order.AsSpan(0, count));
        }

        /// <summary>What the answer must list, for messages.</summary>
        private string What(int n) => answer == AnswerKind.Rank
            ? $"window ranks (1 to {n.ToString(CultureInfo.InvariantCulture)})"
            : "entity ids of the window's rows";

        /// <summary>The hint for an answer the client says it cut at the limit; empty otherwise.</summary>
        private string Cut(in ModelStatus status) => LlmNode.Cut(status, maxOutputTokens, "shorten the window");

        private void Count(OperatorContext context, in AnswerCounts counts)
        {
            var ids = StrataRecMetrics.ModelAnswerIds;
            LlmNode.Count(ids, context, counts.Used, _used);
            LlmNode.Count(ids, context, counts.Unknown, _unknown);
            LlmNode.Count(ids, context, counts.Repeated, _repeated);
        }
    }
}

/// <summary>Registers the operators of StrataRec.Llm.</summary>
public static class LlmRegistration
{
    /// <summary>
    /// Adds the generative operators (<c>LlmReranker@1</c>, <c>LlmAnnotator@1</c>, <c>GenerativeRetriever@1</c>) to the
    /// operators a host's plans may use; their stand-in model client <c>fixed_text@1</c> is in the default model client
    /// registry already.
    /// </summary>
    public static OperatorRegistry AddLlm(this OperatorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(new LlmRerankerFactory()).Add(new LlmAnnotatorFactory()).Add(new GenerativeRetrieverFactory());
    }
}
