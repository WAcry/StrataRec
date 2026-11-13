using System.Text;
using StrataRec.Artifacts.Json;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>
/// The one rule for which model signatures <c>ModelScorer@1</c> can call (design 6.9), checked for every version a node
/// may use. At run time the scorer sends exactly the inputs its config maps, each as a float32 tensor of shape [rows],
/// and reads one float32 per row from each mapped output; every input in a signature is required. A version that does
/// not match would load cleanly and then fail (or fall back) on every request, so it is a load error instead.
/// </summary>
internal static class ScorerSignature
{
    /// <summary>What the scorer sends and reads, for hints.</summary>
    public const string TensorHint =
        "ModelScorer@1 sends every input and reads every output as float32 with shape [-1] (one value per row; the batch "
        + "dimension is dynamic). Change the model's signature, or call the model from an operator that builds other tensors.";

    /// <summary>One mismatch, located by a config key (a JSON pointer below the node's <c>config</c>).</summary>
    public readonly record struct Problem(string ConfigKey, string Code, string Message, string Hint);

    /// <param name="label">The model and version, as messages name them.</param>
    /// <param name="inputs">The config's <c>inputs</c>: model input name → column.</param>
    /// <param name="outputs">The config's <c>outputs</c>: model output name → column.</param>
    public static List<Problem> Check(string label, IReadOnlyDictionary<string, string> inputs, IReadOnlyDictionary<string, string> outputs, ModelSignature signature)
    {
        var problems = new List<Problem>();
        foreach (var (name, _) in inputs)
        {
            var pointer = "/inputs/" + JsonPointer.Escape(name);
            if (signature.FindInput(name) is not { } spec)
            {
                problems.Add(new Problem(pointer, "MODEL_SIGNATURE", $"{label} has no input '{name}'.", "the model's inputs are: " + Names(signature.Inputs)));
            }
            else if (!IsRowTensor(spec))
            {
                problems.Add(new Problem(pointer, "MODEL_TENSOR_UNSUPPORTED", $"{label} declares input '{name}' as {Describe(spec)}.", TensorHint));
            }
        }

        foreach (var spec in signature.Inputs)
        {
            if (!inputs.ContainsKey(spec.Name))
            {
                problems.Add(new Problem("/model", "MODEL_INPUT_MISSING", $"{label} requires input '{spec.Name}', which 'inputs' does not supply.",
                    $"map '{spec.Name}' in 'inputs' to a column or context reference: every input in the model's signature is required"));
            }
        }

        foreach (var (name, _) in outputs)
        {
            var pointer = "/outputs/" + JsonPointer.Escape(name);
            if (signature.FindOutput(name) is not { } spec)
            {
                problems.Add(new Problem(pointer, "MODEL_SIGNATURE", $"{label} has no output '{name}'.", "the model's outputs are: " + Names(signature.Outputs)));
            }
            else if (!IsRowTensor(spec))
            {
                problems.Add(new Problem(pointer, "MODEL_TENSOR_UNSUPPORTED", $"{label} declares output '{name}' as {Describe(spec)}.", TensorHint));
            }
        }

        return problems;
    }

    /// <summary>A float32 tensor with one dynamic dimension: the only kind the scorer builds and reads.</summary>
    public static bool IsRowTensor(TensorSpec spec) =>
        spec.ElementType == TensorElementType.Float32 && spec.Shape is { Count: 1 } shape && shape[0] == -1;

    /// <summary>A tensor spec as <c>int64 [-1, 2]</c>.</summary>
    private static string Describe(TensorSpec spec)
    {
        var text = new StringBuilder(spec.ElementType.ToString().ToLowerInvariant()).Append(" [");
        for (var d = 0; d < spec.Shape.Count; d++)
        {
            text.Append(d == 0 ? string.Empty : ", ").Append(spec.Shape[d]);
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
