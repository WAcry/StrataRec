using System.Text;
using StrataRec.Artifacts.Json;
using StrataRec.Models;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Llm;

/// <summary>
/// The load-time check of a model version that a generative operator calls with rendered prompts and reads text from
/// (design 6.9): every prompt is a text input of the signature, every input of the signature has a prompt, and every
/// output read is text. A version that does not match would load cleanly and then fail on every request, so every version
/// a node may use is checked, with the codes and hint style of the tensor operators' rule.
/// </summary>
/// <remarks>
/// Only the element type is checked: a text value is one string whatever shape the signature gives it (a scalar, or a
/// batch of one), and the operator sends exactly one per input.
/// </remarks>
public static class TextSignature
{
    /// <summary>One mismatch, located by a config key (a JSON pointer below the node's <c>config</c>).</summary>
    public readonly record struct Problem(string ConfigKey, string Code, string Message, string Hint);

    /// <param name="label">The model and version, as messages name them (<see cref="BoundModel.Describe"/>).</param>
    /// <param name="operatorId">The operator, as hints name it, e.g. <c>LlmReranker@1</c>.</param>
    /// <param name="prompts">The model text input names the config maps to templates, each with its config key.</param>
    /// <param name="outputs">The model text outputs the operator reads, each with its config key.</param>
    public static List<Problem> Check(
        string label, string operatorId, IReadOnlyList<(string Name, string ConfigKey)> prompts, IReadOnlyList<(string Name, string ConfigKey)> outputs,
        ModelSignature signature)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(signature);
        var problems = new List<Problem>();
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, key) in prompts)
        {
            mapped.Add(name);
            if (signature.FindInput(name) is not { } spec)
            {
                problems.Add(new Problem(key, "MODEL_SIGNATURE", $"{label} has no input '{name}'.", "the model's inputs are: " + Names(signature.Inputs)));
            }
            else if (spec.ElementType != TensorElementType.Text)
            {
                problems.Add(new Problem(key, "MODEL_TENSOR_MISMATCH",
                    $"{label} declares input '{name}' as {Describe(spec)}, but {operatorId} sends a rendered prompt, which is text.",
                    $"declare input '{name}' as text in the model's signature, or call the model from an operator that builds tensors (ModelScorer@1, "
                    + "PairwiseRanker@1, ListwiseReranker@1)"));
            }
        }

        foreach (var spec in signature.Inputs)
        {
            if (!mapped.Contains(spec.Name))
            {
                problems.Add(new Problem("/model", "MODEL_INPUT_MISSING", $"{label} requires input '{spec.Name}', which 'prompts' does not supply.",
                    $"add a template for '{spec.Name}' under 'prompts': every input in the model's signature is required"));
            }
        }

        foreach (var (name, key) in outputs)
        {
            if (signature.FindOutput(name) is not { } spec)
            {
                problems.Add(new Problem(key, "MODEL_SIGNATURE", $"{label} has no output '{name}'.", "the model's outputs are: " + Names(signature.Outputs)));
            }
            else if (spec.ElementType != TensorElementType.Text)
            {
                problems.Add(new Problem(key, "MODEL_TENSOR_UNSUPPORTED", $"{label} declares output '{name}' as {Describe(spec)}.",
                    $"{operatorId} reads the output as text (the generated answer): name a text output of the model, or read a tensor output from an "
                    + "operator that builds tensors"));
            }
        }

        return problems;
    }

    /// <summary>The config key of a model input mapped under <c>prompts</c>.</summary>
    public static string PromptKey(string name) => "/prompts/" + JsonPointer.Escape(name);

    /// <summary>A tensor spec as <c>float32 [-1, 2]</c> or <c>text []</c>.</summary>
    private static string Describe(TensorSpec spec)
    {
        var text = new StringBuilder(spec.ElementType.ToString().ToLowerInvariant()).Append(" [");
        var shape = spec.Shape ?? Array.Empty<int>();
        for (var d = 0; d < shape.Count; d++)
        {
            text.Append(d == 0 ? string.Empty : ", ").Append(shape[d]);
        }

        return text.Append(']').ToString();
    }

    private static string Names(IReadOnlyList<TensorSpec> specs)
    {
        if (specs.Count == 0)
        {
            return "(none)";
        }

        var names = new string[specs.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = specs[i].Name;
        }

        Array.Sort(names, StringComparer.Ordinal);
        return string.Join(", ", names);
    }
}
