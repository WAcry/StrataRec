using System.Diagnostics.Metrics;
using System.Globalization;
using StrataRec.Core.Diagnostics;
using StrataRec.Llm.Templates;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Core.Services;

namespace StrataRec.Llm;

internal static class LlmNode
{
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
            var lookup = model.Signature(context, version);
            if (!lookup.IsKnown)
            {
                // A failure is reported at /model; a pending or unchecked signature is checked when the engine loads it.
                ok &= lookup.Status != SignatureStatus.Failed;
                continue;
            }

            foreach (var problem in TextSignature.Check(model.Describe(version), context.Descriptor.Id, inputs, outputs, lookup.Signature))
            {
                context.Error(problem.ConfigKey, problem.Code, problem.Message, problem.Hint);
                ok = false;
            }
        }

        return ok;
    }

    public static string Cut(in ServiceStatus status, int maxOutputTokens, string remedy) => status.Truncated
        ? $"; the answer was cut at max_output_tokens ({maxOutputTokens.ToString(CultureInfo.InvariantCulture)}): raise max_output_tokens or {remedy}"
        : string.Empty;

    public static KeyValuePair<string, object?>[] Tags(BoundModel model, string outcome)
    {
        var tags = new KeyValuePair<string, object?>[model.Target.Tags.Length + 1];
        model.Target.Tags.CopyTo(tags, 0);
        tags[^1] = new(StrataRecTags.Outcome, outcome);
        return tags;
    }

    public static void Count(Counter<long> counter, OperatorContext context, int value, KeyValuePair<string, object?>[] tags)
    {
        if (value > 0 && counter.Enabled && !context.Replays)
        {
            counter.Add(value, tags);
        }
    }
}
