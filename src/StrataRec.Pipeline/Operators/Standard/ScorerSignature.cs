using System.Text;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Types;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>
/// The one rule for which tensors <c>ModelScorer@1</c> sends and reads, and so which model signatures it can call
/// (design 6.9). A version that does not match would load cleanly and then fail (or fall back) on every request, so every
/// version a node may use is checked, and a mismatch is a load error.
/// </summary>
/// <remarks>
/// <para>The tensor of an input follows the declared type of the column or context value it maps: <c>float32</c>,
/// <c>float64</c> and <c>bool</c> (0/1) are sent as float32 <c>[rows]</c>; <c>int32</c> and <c>int64</c> as int64
/// <c>[rows]</c>, with their exact values; <c>vec&lt;float32,N&gt;</c> as float32 <c>[rows, N]</c>, row-major. Nullable
/// types, strings, text and lists cannot be sent: a model needs a value in every row, and a tensor has no place for the
/// others. A context value is broadcast to every row of the batch.</para>
/// <para>The column decides, never the model's signature: replay records a digest of each row's inputs, element type and
/// shape included, and replay engines and <c>stratarec validate</c> compile plans without loading clients, so they have no
/// signature. If the signature decided, the same configuration would build different tensors depending on whether the
/// client was loaded, and a replay would report its rows' inputs as changed.</para>
/// <para>So a version must declare exactly the mapped inputs (every input of a signature is required), each with the
/// element type and shape its mapping gives, <c>[-1]</c> or <c>[-1, N]</c> with the same N, and every mapped output as
/// float32 <c>[-1]</c>, one value per row. A model that declares float32 for an int column is fed a float32 derived
/// feature, which design 6.9 rule 2 prefers anyway: one definition for training and serving.</para>
/// </remarks>
internal static class ScorerSignature
{
    /// <summary>What the scorer sends and reads, for hints.</summary>
    public const string TensorHint =
        "ModelScorer@1 sends a float32, float64 or bool column or context value as float32 [-1], an int32 or int64 one as "
        + "int64 [-1] and a vec<float32,N> one as float32 [-1, N], and reads every output as float32 [-1] (one value per row; "
        + "the batch dimension is dynamic). Change the model's signature, or call the model from an operator that builds other tensors.";

    /// <summary>One mismatch, located by a config key (a JSON pointer below the node's <c>config</c>).</summary>
    public readonly record struct Problem(string ConfigKey, string Code, string Message, string Hint);

    /// <summary>A mapped input as the check sees it.</summary>
    /// <param name="Name">The model input name.</param>
    /// <param name="Source">The reference it maps, e.g. <c>col.item.id</c>.</param>
    /// <param name="Type">The reference's declared type, which decides the tensor.</param>
    public readonly record struct MappedInput(string Name, string Source, ColumnType Type);

    /// <summary>The tensor sent for an input.</summary>
    /// <param name="ElementType">Float32 or Int64.</param>
    /// <param name="Dimension">0 for one value per row (shape <c>[rows]</c>); N for a vector per row (<c>[rows, N]</c>).</param>
    public readonly record struct InputTensor(TensorElementType ElementType, int Dimension)
    {
        /// <summary>As a signature declares it, e.g. <c>float32 [-1, 8]</c>.</summary>
        public override string ToString() => Dimension == 0 ? $"{Name(ElementType)} [-1]" : $"{Name(ElementType)} [-1, {Dimension}]";
    }

    /// <summary>The tensor the scorer sends for a value of <paramref name="type"/>; false for a type it cannot send.</summary>
    public static bool TryGetInputTensor(ColumnType type, out InputTensor tensor)
    {
        tensor = default;
        if (type.Nullable)
        {
            return false;
        }

        switch (type.Kind)
        {
            case StorageKind.Float32 or StorageKind.Float64 or StorageKind.Bool:
                tensor = new InputTensor(TensorElementType.Float32, 0);
                return true;
            case StorageKind.Int32 or StorageKind.Int64:
                tensor = new InputTensor(TensorElementType.Int64, 0);
                return true;
            case StorageKind.Vector when type.Dimension > 0:
                tensor = new InputTensor(TensorElementType.Float32, type.Dimension);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The compile error for an input whose type <see cref="TryGetInputTensor"/> rejects.</summary>
    public static Problem UnsendableInput(string name, string source, ColumnType type)
    {
        var pointer = "/inputs/" + JsonPointer.Escape(name);
        return type.Nullable
            ? new Problem(pointer, "MODEL_INPUT_TYPE", $"Model input '{name}' maps {source}, which is nullable ({type}): a model needs a value in every row.",
                $"make {source} non-nullable (a feature with a default, a request input declared without '?'), or map a derived feature "
                + "that settles the missing case with '??' (design 6.9)")
            : new Problem(pointer, "MODEL_INPUT_TYPE", $"Model input '{name}' maps {source} ({type}), which ModelScorer@1 cannot send as a tensor.",
                "map a float32, float64, bool, int32, int64 or vec<float32,N> column or context value (a derived feature, design 6.9), "
                + "or call the model from an operator that builds other tensors");
    }

    /// <param name="label">The model and version, as messages name them.</param>
    /// <param name="inputs">The config's <c>inputs</c>, with the declared type of each reference.</param>
    /// <param name="outputs">The config's <c>outputs</c>: model output name → column.</param>
    public static List<Problem> Check(string label, IReadOnlyList<MappedInput> inputs, IReadOnlyDictionary<string, string> outputs, ModelSignature signature)
    {
        var problems = new List<Problem>();
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            mapped.Add(input.Name);
            var pointer = "/inputs/" + JsonPointer.Escape(input.Name);
            if (signature.FindInput(input.Name) is not { } spec)
            {
                problems.Add(new Problem(pointer, "MODEL_SIGNATURE", $"{label} has no input '{input.Name}'.", "the model's inputs are: " + Names(signature.Inputs)));
            }
            else if (!TryGetInputTensor(input.Type, out var sent))
            {
                problems.Add(UnsendableInput(input.Name, input.Source, input.Type));
            }
            else if (!Accepts(spec, sent))
            {
                problems.Add(new Problem(pointer, "MODEL_TENSOR_MISMATCH",
                    $"{label} declares input '{input.Name}' as {Describe(spec)}, but it maps {input.Source} ({input.Type}), which ModelScorer@1 sends as {sent}.",
                    MismatchHint(input, sent, spec)));
            }
        }

        foreach (var spec in signature.Inputs)
        {
            if (!mapped.Contains(spec.Name))
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

    /// <summary>A float32 tensor with one dynamic dimension: one value per row, the only kind of output the scorer reads.</summary>
    public static bool IsRowTensor(TensorSpec spec) =>
        spec.ElementType == TensorElementType.Float32 && spec.Shape is { Count: 1 } shape && shape[0] == -1;

    /// <summary>True when <paramref name="spec"/> declares exactly the tensor sent: <c>[-1]</c>, or <c>[-1, N]</c> with the same N.</summary>
    private static bool Accepts(TensorSpec spec, InputTensor sent)
    {
        if (spec.ElementType != sent.ElementType)
        {
            return false;
        }

        var shape = spec.Shape;
        return sent.Dimension == 0
            ? shape is { Count: 1 } && shape[0] == -1
            : shape is { Count: 2 } && shape[0] == -1 && shape[1] == sent.Dimension;
    }

    /// <summary>Either side may change: the model's declaration, or the mapping, to a column that gives what the model declares.</summary>
    private static string MismatchHint(MappedInput input, InputTensor sent, TensorSpec declared)
    {
        var fix = $"declare input '{input.Name}' as {sent} in the model's signature";
        if (ColumnTypeFor(declared) is not { } type)
        {
            return $"{fix}; no column gives {Describe(declared)}, so a model that needs it is called from an operator that builds other tensors";
        }

        var article = type[0] == 'i' ? "an" : "a";
        return $"{fix}, or map {article} {type} derived feature of {input.Source} instead (one definition for training and serving, design 6.9)";
    }

    /// <summary>The column type the scorer sends as <paramref name="spec"/>, or null when none does.</summary>
    private static string? ColumnTypeFor(TensorSpec spec)
    {
        var shape = spec.Shape;
        if (shape is not { Count: 1 or 2 } || shape[0] != -1)
        {
            return null;
        }

        return (spec.ElementType, shape.Count) switch
        {
            (TensorElementType.Float32, 1) => "float32",
            (TensorElementType.Int64, 1) => "int64",
            (TensorElementType.Float32, 2) when shape[1] > 0 => $"vec<float32,{shape[1]}>",
            _ => null,
        };
    }

    /// <summary>A tensor spec as <c>int64 [-1, 2]</c>.</summary>
    private static string Describe(TensorSpec spec)
    {
        var text = new StringBuilder(Name(spec.ElementType)).Append(" [");
        for (var d = 0; d < spec.Shape.Count; d++)
        {
            text.Append(d == 0 ? string.Empty : ", ").Append(spec.Shape[d]);
        }

        return text.Append(']').ToString();
    }

    private static string Name(TensorElementType type) => type.ToString().ToLowerInvariant();

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
