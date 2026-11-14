using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Serialization;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Llm.Templates;
using StrataRec.Models;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Llm;

/// <summary>Config of <c>LlmAnnotator@1</c> (design 5.9).</summary>
public sealed class LlmAnnotatorConfig
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
    /// Model text input name → Mustache template of its prompt for one row (<see cref="PromptTemplate"/>, per-row scope):
    /// <c>col.*</c> is the row's value, anywhere; <c>ctx.*</c> and <c>param.*</c> as everywhere. There is no <c>rows</c>
    /// section and no <c>rank</c>: each row is its own call.
    /// </summary>
    public required Dictionary<string, string> Prompts { get; init; }

    /// <summary>The model's text output holding the answer: the row's value.</summary>
    public required string Output { get; init; }

    /// <summary>
    /// The column written: nullable, since the rows after the window and the rows whose answer is not usable have no value.
    /// <c>text?</c>, <c>string?</c>, <c>bool?</c>, <c>int32?</c>, <c>int64?</c>, <c>float32?</c> or <c>float64?</c>; an
    /// undeclared column is <c>text?</c>, or <c>string?</c> with <see cref="Categories"/>.
    /// </summary>
    public required string Column { get; init; }

    /// <summary>For a <c>string?</c> column: the answers it allows, compared exactly; any other answer is not usable.</summary>
    public List<string>? Categories { get; init; }

    /// <summary>The leading rows annotated, one model call each; the rows after them stay missing.</summary>
    public int Window { get; init; } = 20;

    /// <summary>The most tokens the model may generate per answer (design 5.9 rule 2), passed with each call.</summary>
    public int MaxOutputTokens { get; init; } = 64;
}

/// <summary>
/// <c>LlmAnnotator@1</c> (ColumnWriter): writes a generative model's answer for each of the leading <c>window</c> rows into
/// a column (a recommendation reason, a category, a flag, a number), from prompts rendered per row (design 5.9).
/// </summary>
/// <remarks>
/// <para>n = min(window, rows); no call when n is 0. Each row is its own call, all sent at once: batch size 1, the row as the
/// call's row, so a recording keys the answer by the row's entity and a replay answers each entity whatever window it is
/// in; the request's seed and <c>max_output_tokens</c>. A row's prompt depends only on that row and the request, so its
/// answer can be reused per entity; a prompt over several rows would tie every answer to the window.</para>
/// <para>Each answer is parsed by the column's type (<see cref="LlmAnswer"/>): text and strings trimmed, with one outer
/// code fence unwrapped, and not empty; a string one of <c>categories</c> when given; a bool <c>true</c> or <c>false</c>;
/// a number in JSON's syntax that is finite and fits. A row whose call failed (or threw, or returned no text) or whose
/// answer is not usable keeps no value: missing is explicit, and never a guess. Each row is counted
/// (<see cref="StrataRecMetrics.ModelAnswers"/>: used, unusable, failed).</para>
/// <para>When some rows have no value, the node is Degraded, with the counts, the first failure and the first unusable
/// answer quoted; when every call failed, the node fails, writing nothing, and <c>on_failure</c> decides. Answers that are
/// all unusable leave the node Degraded: the calls worked, and the column says which rows have no value.</para>
/// </remarks>
public sealed class LlmAnnotatorFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("LlmAnnotator", 1, Effects.ColumnWriter, typeof(LlmAnnotatorConfig),
        "Writes a generative model's answer for each leading row into a column, from prompts rendered per row with Mustache templates.");

    public Operator? Compile(OperatorCompileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var config = context.BindConfig<LlmAnnotatorConfig>();
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
            context.Error("/window", "LLM_WINDOW", $"window is {config.Window.ToString(CultureInfo.InvariantCulture)}, but it must be at least 1.",
                "the window is the leading rows annotated, one model call each (the default is 20)");
            ok = false;
        }

        ok &= LlmNode.CheckMaxOutputTokens(context, config.MaxOutputTokens,
            "the answer is one row's value, so allow for the longest value the prompt asks for (the default is 64)");
        ok &= LlmNode.CheckOutput(context, config.Output);
        var categories = CompileCategories(context, config.Categories, ref ok);
        var column = CompileColumn(context, config.Column, categories is not null, ref ok);
        var prompts = LlmNode.CompilePrompts(context, config.Prompts, PromptScope.Row,
            "{ \"user\": \"In one sentence, why would this user like {{col.item.title}}?\" }", ref ok);
        if (!ok || column is not { } written || !LlmNode.CheckSignatures(context, model, prompts, config.Output))
        {
            return null;
        }

        return new LlmAnnotator(model, prompts, config.Output, written, categories, config.Window, config.MaxOutputTokens);
    }

    /// <summary>The allowed answers: non-empty, without surrounding whitespace (an answer is trimmed), each once.</summary>
    private static string[]? CompileCategories(OperatorCompileContext context, List<string>? categories, ref bool ok)
    {
        if (categories is null)
        {
            return null;
        }

        if (categories.Count == 0)
        {
            context.Error("/categories", "LLM_CATEGORIES", "'categories' is empty, so no answer could be used.",
                "list the answers the column allows, e.g. \"categories\": [\"sports\", \"music\"], or remove 'categories' to take any text");
            ok = false;
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < categories.Count; i++)
        {
            var category = categories[i];
            var key = "/categories/" + i.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(category))
            {
                context.Error(key, "LLM_CATEGORIES", $"Category {i.ToString(CultureInfo.InvariantCulture)} is empty, so no answer could match it.",
                    "remove it: an answer is trimmed, and an empty answer is not usable");
                ok = false;
            }
            else if (category.AsSpan().Trim().Length != category.Length)
            {
                context.Error(key, "LLM_CATEGORIES", $"Category '{category}' starts or ends with whitespace, which an answer never does: answers are trimmed.",
                    "remove the surrounding whitespace");
                ok = false;
            }
            else if (!seen.Add(category))
            {
                context.Error(key, "LLM_CATEGORIES", $"Category '{category}' is listed twice.", "list each category once");
                ok = false;
            }
        }

        return categories.ToArray();
    }

    /// <summary>The nullable column of a type an answer can be parsed as; an undeclared one is text?, or string? with categories.</summary>
    private OutputColumn? CompileColumn(OperatorCompileContext context, string? name, bool categories, ref bool ok)
    {
        if (string.IsNullOrEmpty(name))
        {
            context.Error("/column", "LLM_COLUMN", $"{Descriptor.Id} needs 'column': the column the answers are written to.",
                "set \"column\" to a column name, e.g. \"llm.reason\"; an undeclared column is text?, or string? with 'categories'");
            ok = false;
            return null;
        }

        var declared = context.Schema.TryGetColumn(name, out _);
        var implicitType = categories ? ColumnType.String.AsNullable() : ColumnType.Text.AsNullable();
        if (!context.TryWriteColumn(name, "/column", declared ? null : implicitType, out var written))
        {
            ok = false;
            return null;
        }

        var type = written.Type;
        if (!type.Nullable || type.Kind is not (StorageKind.Text or StorageKind.String or StorageKind.Bool or StorageKind.Int32 or StorageKind.Int64
                or StorageKind.Float32 or StorageKind.Float64))
        {
            var parsable = type.Kind is StorageKind.Text or StorageKind.String or StorageKind.Bool or StorageKind.Int32 or StorageKind.Int64
                or StorageKind.Float32 or StorageKind.Float64;
            context.Error("/column", "COLUMN_TYPE_MISMATCH",
                $"Column '{name}' is {type}, but {Descriptor.Id} writes only the window's rows whose answers it can use, so it must be text?, string?, bool?, "
                + "int32?, int64?, float32? or float64?.",
                parsable
                    ? $"declare '{name}' as {type.AsNullable()}: the rows after the window and the rows whose answer is not usable have no value"
                    : $"declare '{name}' as one of those types, or leave it undeclared to write text?");
            ok = false;
            return null;
        }

        if (categories && type.Kind != StorageKind.String)
        {
            context.Error("/categories", "LLM_CATEGORIES", $"'categories' are the answers a string? column allows, but column '{name}' is {type}.",
                $"declare '{name}' as string? (or leave it undeclared), or remove 'categories'");
            ok = false;
            return null;
        }

        return written;
    }

    private sealed class LlmAnnotator(
        BoundModel model, (string Name, PromptTemplate Template)[] prompts, string output, OutputColumn column, string[]? categories, int window,
        int maxOutputTokens)
        : Operator
    {
        private static readonly char[] TypeNameSeparators = ['.', '+'];

        private readonly KeyValuePair<string, object?>[] _used = LlmNode.Tags(model, "used");
        private readonly KeyValuePair<string, object?>[] _unusable = LlmNode.Tags(model, "unusable");
        private readonly KeyValuePair<string, object?>[] _failed = LlmNode.Tags(model, "failed");

        public override IoTarget ModelTarget => model.Target;

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var n = Math.Min(window, context.LiveRows.Length);
            if (n == 0)
            {
                return;
            }

            // The window as of the calls, kept across the awaits; rented, and never handed to the model client.
            var memory = context.Memory;
            var rows = memory.Rent<int>(n);
            context.LiveRows[..n].CopyTo(rows);
            // Every prompt is rendered before any call is sent, so nothing is in flight if a render throws.
            var inputs = new NamedInput[n][];
            for (var i = 0; i < n; i++)
            {
                inputs[i] = new NamedInput[prompts.Length];
                for (var p = 0; p < prompts.Length; p++)
                {
                    inputs[i][p] = NamedInput.FromText(prompts[p].Name, prompts[p].Template.Render(context, rows[i]));
                }
            }

            var version = model.Version(context);
            var writers = new ModelOutputWriter[n];
            var calls = new Task<ModelStatus>[n];
            for (var i = 0; i < n; i++)
            {
                writers[i] = new ModelOutputWriter(memory);
                calls[i] = model.InvokeAsync(context, version, inputs[i], 1, writers[i], rows.AsMemory(i, 1), maxOutputTokens);
            }

            // Each wait is bounded by the model's timeout and the node's budget. A call that failed or threw is that row's
            // failure; the budget running out or the caller cancelling cuts every call off, and fails the node once all ended.
            var statuses = new ModelStatus[n];
            OperationCanceledException? cutOff = null;
            for (var i = 0; i < n; i++)
            {
                try
                {
                    statuses[i] = await calls[i].ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    cutOff ??= ex;
                }
                catch (Exception ex)
                {
                    statuses[i] = ModelStatus.Failed(Threw(ex));
                }
            }

            if (cutOff is not null)
            {
                ExceptionDispatchInfo.Throw(cutOff);
            }

            Apply(context, rows.AsSpan(0, n), statuses, writers);
        }

        /// <summary>Writes every usable answer; fails the node when every call failed, and degrades it when some rows have no value.</summary>
        private void Apply(OperatorContext context, ReadOnlySpan<int> rows, ModelStatus[] statuses, ModelOutputWriter[] writers)
        {
            int used = 0, unusable = 0, failed = 0;
            string? firstFailure = null;
            string? firstUnusable = null;
            ColumnBuffer? buffer = null;
            for (var i = 0; i < rows.Length; i++)
            {
                var status = statuses[i];
                if (!status.Succeeded)
                {
                    failed++;
                    firstFailure ??= status.Error ?? "failed";
                    continue;
                }

                if (!writers[i].TryGetText(output, out var text))
                {
                    failed++;
                    firstFailure ??= $"did not return text output '{output}'";
                    continue;
                }

                var value = LlmAnswer.ParseValue(text, column.Type.Kind, categories, context.Memory, out var why);
                if (value.IsMissing)
                {
                    unusable++;
                    firstUnusable ??= $"{LlmAnswer.Quote(text)}: {why}{LlmNode.Cut(status, maxOutputTokens, "ask for a shorter answer")}";
                    continue;
                }

                ColumnValues.Set(buffer ??= context.Output(column), rows[i], value);
                used++;
            }

            var answers = StrataRecMetrics.ModelAnswers;
            LlmNode.Count(answers, context, used, _used);
            LlmNode.Count(answers, context, unusable, _unusable);
            LlmNode.Count(answers, context, failed, _failed);
            if (failed == rows.Length)
            {
                context.Fail($"model '{model.Model}' failed on every row of the window ({Count(rows.Length)}): {firstFailure}");
                return;
            }

            if (used == rows.Length)
            {
                return;
            }

            var reason = new StringBuilder().Append($"model '{model.Model}' annotated {Count(used)} of {Count(rows.Length)} rows");
            if (failed > 0)
            {
                reason.Append($"; {Count(failed)} call(s) failed, the first: {firstFailure}");
            }

            if (unusable > 0)
            {
                reason.Append($"; {Count(unusable)} answer(s) not usable as {column.Type}, the first {firstUnusable}");
            }

            context.ReportDegraded(reason.ToString());
        }

        private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// A call that threw, as the node's failure would name it: the exception's type name, the recorded one in a replay that
        /// could not create the type again, so a replay reports the same.
        /// </summary>
        private static string Threw(Exception ex)
        {
            var name = ex.GetType().Name;
            if (ex is ReplayedCallException { OriginalType: { } original })
            {
                var arguments = original.IndexOf('[', StringComparison.Ordinal);
                var full = arguments < 0 ? original : original[..arguments];
                name = full[(full.LastIndexOfAny(TypeNameSeparators) + 1)..];
            }

            return $"threw {name}: {ex.Message}";
        }
    }
}
