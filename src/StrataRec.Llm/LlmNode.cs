using System.Diagnostics.Metrics;
using System.Globalization;
using StrataRec.Core.Diagnostics;
using StrataRec.Llm.Templates;
using StrataRec.Models;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Llm;

/// <summary>
/// What the generative operators share around the model call: their prompts, the load-time check of every version's text
/// signature, the cost limit, the wording of a truncated answer and the tags of their answer metrics. One copy, so the
/// operators agree on codes, hints and messages.
/// </summary>
internal static class LlmNode
{
    /// <summary><c>max_output_tokens</c> is at least 1; false after reporting it.</summary>
    /// <param name="hint">What the limit has to allow for this operator's answers, with its default.</param>
    public static bool CheckMaxOutputTokens(OperatorCompileContext context, int maxOutputTokens, string hint)
    {
        if (maxOutputTokens >= 1)
        {
            return true;
        }

        context.Error("/max_output_tokens", "LLM_MAX_OUTPUT_TOKENS",
            $"max_output_tokens is {maxOutputTokens.ToString(CultureInfo.InvariantCulture)}, but it must be at least 1.", hint);
        return false;
    }

    /// <summary><c>output</c> names a text output; false after reporting it empty.</summary>
    public static bool CheckOutput(OperatorCompileContext context, string? output)
    {
        if (!string.IsNullOrEmpty(output))
        {
            return true;
        }

        context.Error("/output", "MODEL_OUTPUT", $"{context.Descriptor.Id} needs 'output': the name of the model's text output holding the answer.",
            "set \"output\" to a text output name of the model's signature");
        return false;
    }

    /// <summary>Every prompt, in ordinal order of the input names (the order a call sends them); sets <paramref name="ok"/> false after an error.</summary>
    /// <param name="example">A <c>prompts</c> value for the hint of an empty map.</param>
    public static (string Name, PromptTemplate Template)[] CompilePrompts(
        OperatorCompileContext context, Dictionary<string, string>? prompts, PromptScope scope, string example, ref bool ok)
    {
        if (prompts is null || prompts.Count == 0)
        {
            context.Error("/prompts", "LLM_PROMPTS", "'prompts' is empty: the model needs at least one prompt.",
                "map each text input of the model to a Mustache template, e.g. \"prompts\": " + example);
            ok = false;
            return [];
        }

        var names = new List<string>(prompts.Keys);
        names.Sort(StringComparer.Ordinal);
        var compiled = new (string Name, PromptTemplate Template)[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var template = PromptTemplate.Compile(context, prompts[names[i]], TextSignature.PromptKey(names[i]), scope);
            if (template is null)
            {
                ok = false;
                continue;
            }

            compiled[i] = (names[i], template);
        }

        return compiled;
    }

    /// <summary>Checks every version a request may call against the prompts sent and the output read; false after reporting a mismatch.</summary>
    public static bool CheckSignatures(OperatorCompileContext context, BoundModel model, (string Name, PromptTemplate Template)[] prompts, string output)
    {
        var inputs = new (string Name, string ConfigKey)[prompts.Length];
        for (var i = 0; i < prompts.Length; i++)
        {
            inputs[i] = (prompts[i].Name, TextSignature.PromptKey(prompts[i].Name));
        }

        (string Name, string ConfigKey)[] outputs = [(output, "/output")];
        var ok = true;
        foreach (var version in model.Versions)
        {
            // Null when it cannot be checked now: an error (reported), or the engine checks it at load.
            if (model.Signature(context, version) is not { } signature)
            {
                ok &= !context.HasErrors;
                continue;
            }

            foreach (var problem in TextSignature.Check(model.Describe(version), context.Descriptor.Id, inputs, outputs, signature))
            {
                context.Error(problem.ConfigKey, problem.Code, problem.Message, problem.Hint);
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    /// For a message about an answer: when the client said it stopped at <c>max_output_tokens</c>, that, since a cut-off
    /// answer is the likely cause, with <paramref name="remedy"/>; otherwise empty.
    /// </summary>
    /// <param name="remedy">What else shortens the answer, e.g. <c>shorten the window</c>.</param>
    public static string Cut(in ModelStatus status, int maxOutputTokens, string remedy) => status.Truncated
        ? $"; the answer was cut at max_output_tokens ({maxOutputTokens.ToString(CultureInfo.InvariantCulture)}): raise max_output_tokens or {remedy}"
        : string.Empty;

    /// <summary>The tags of an answer metric: the model's, and the outcome. Built once per compiled node.</summary>
    public static KeyValuePair<string, object?>[] Tags(BoundModel model, string outcome)
    {
        var tags = new KeyValuePair<string, object?>[model.Target.Tags.Length + 1];
        model.Target.Tags.CopyTo(tags, 0);
        tags[^1] = new(StrataRecTags.Outcome, outcome);
        return tags;
    }

    /// <summary>Adds <paramref name="value"/> when it is positive and the counter is enabled; a replay is not traffic and counts nothing.</summary>
    public static void Count(Counter<long> counter, OperatorContext context, int value, KeyValuePair<string, object?>[] tags)
    {
        if (value > 0 && counter.Enabled && !context.Replays)
        {
            counter.Add(value, tags);
        }
    }
}
