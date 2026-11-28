using System.Text;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>
/// How a model operator lays out a call's batch, which gives every tensor of the call its leading dimensions. The value a row contributes (one element of the column's declared type, or N of them for a
/// <c>list&lt;E,N&gt;</c>) follows its column type in every layout.
/// </summary>
internal enum TensorLayout
{
    Rows,

    Pairs,

    List,
}

internal static class ModelTensorRule
{
    /// <summary>One mismatch, located by a config key (a JSON pointer below the node's <c>config</c>).</summary>
    public readonly record struct Problem(string ConfigKey, string Code, string Message, string Hint);

    public readonly record struct MappedInput(string Name, string Source, ColumnType Type, bool Broadcast = false);

    public readonly record struct MappedOutput(string Name, string ConfigKey, ColumnType? Column = null);

    /// <summary>The value one row contributes to an input's tensor.</summary>
    /// <param name="ElementType">The element type of the column's declared type.</param>
    /// <param name="Dimension">0 for one value per row; N for a <c>list&lt;E,N&gt;</c> per row.</param>
    public readonly record struct InputTensor(TensorElementType ElementType, int Dimension)
    {
        public override string ToString() => Describe(ElementType, InputShape(TensorLayout.Rows, false, -1, Dimension));
    }

    public static string OperatorId(TensorLayout layout) => layout switch
    {
        TensorLayout.Rows => "ModelScorer@1",
        TensorLayout.Pairs => "PairwiseRanker@1",
        _ => "ListwiseReranker@1",
    };

    public static string TensorHint(TensorLayout layout) => layout switch
    {
        TensorLayout.Rows =>
            "ModelScorer@1 sends each column or context value as a tensor of its declared type, [-1] (one value per row), or [-1, N] for a "
            + "list<E,N> (list<float32,N> as float32 [-1, N]), and reads each output as a tensor of the type of the column it is written to, "
            + "[-1] or [-1, N] for a list<E,N> column (the batch dimension is dynamic). Change the model's signature, map a derived feature "
            + "of the type the model takes, or call the model from an operator that builds other tensors.",
        TensorLayout.Pairs =>
            "PairwiseRanker@1 sends each column as a tensor of its declared type [-1, 2] (the two rows of each pair, the higher-ranked "
            + "first), or [-1, 2, N] for a list<E,N>, and each context value as [-1] or [-1, N] (the same in every pair), and reads the "
            + "output as float32 or float64 [-1] (one value per pair: the probability that its first row is preferred). The pair dimension "
            + "is dynamic; the 2 and N may be declared -1. Change the model's signature, or call the model from an operator that builds "
            + "other tensors.",
        _ =>
            "ListwiseReranker@1 sends each column as a tensor of its declared type [1, -1] (the window's rows as one list), or [1, -1, N] "
            + "for a list<E,N>, and each context value as [1] or [1, N], and reads the output as float32 or float64 [1, -1] (one score per "
            + "row of the list). The list's length is dynamic; the 1 and N may be declared -1. Change the model's signature, or call the "
            + "model from an operator that builds other tensors.",
    };

    public static bool TryGetInputTensor(ColumnType type, out InputTensor tensor)
    {
        tensor = default;
        if (type.Nullable || (type.IsList && !type.IsFixedLength))
        {
            return false;
        }

        tensor = new InputTensor(ElementTypeOf(type.ValueKind), type.FixedLength);
        return true;
    }

    /// <summary>
    /// The tensor <c>ModelScorer@1</c> reads into a column of <paramref name="type"/>: as an input of the type would be
    /// sent, nullable or not (every row gets a value).
    /// </summary>
    public static bool TryGetOutputTensor(ColumnType type, out InputTensor tensor) => TryGetInputTensor(type.AsNonNullable(), out tensor);

    public static TensorElementType ElementTypeOf(StorageKind kind) => kind switch
    {
        StorageKind.Bool => TensorElementType.Bool,
        StorageKind.Int32 => TensorElementType.Int32,
        StorageKind.Int64 => TensorElementType.Int64,
        StorageKind.Float32 => TensorElementType.Float32,
        StorageKind.Float64 => TensorElementType.Float64,
        StorageKind.String => TensorElementType.String,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A tensor's elements are bool, int32, int64, float32, float64 or string."),
    };

    /// <summary>
    /// The shape of an input's tensor in <paramref name="layout"/>: <paramref name="length"/> is the batch's dynamic
    /// dimension (rows, pairs, or the list's rows), or -1 for the shape a signature declares; <paramref name="dimension"/>
    /// is a <c>list&lt;E,N&gt;</c>'s N, or 0. A context value has no row dimension of its own: it is repeated for every batch entry.
    /// </summary>
    public static int[] InputShape(TensorLayout layout, bool broadcast, int length, int dimension) => (layout, broadcast, dimension) switch
    {
        // The final array at once: one allocation per input and batch.
        (TensorLayout.Rows, _, 0) or (TensorLayout.Pairs, true, 0) => [length],
        (TensorLayout.Rows, _, _) or (TensorLayout.Pairs, true, _) => [length, dimension],
        (TensorLayout.Pairs, false, 0) => [length, 2],
        (TensorLayout.Pairs, false, _) => [length, 2, dimension],
        (_, true, 0) => [1],
        (_, true, _) => [1, dimension],
        (_, false, 0) => [1, length],
        _ => [1, length, dimension],
    };

    public static int[] OutputShape(TensorLayout layout, int length, int dimension = 0) => (layout, dimension) switch
    {
        (TensorLayout.List, 0) => [1, length],
        (TensorLayout.List, _) => [1, length, dimension],
        (_, 0) => [length],
        _ => [length, dimension],
    };

    public static int ColumnEntries(TensorLayout layout) => layout == TensorLayout.Pairs ? 2 : 1;

    public static Problem UnsendableInput(string name, string source, ColumnType type)
    {
        var pointer = "/inputs/" + JsonPointer.Escape(name);
        if (type.Nullable)
        {
            return new Problem(pointer, "MODEL_INPUT_TYPE", $"Model input '{name}' maps {source}, which is nullable ({type}): a model needs a value in every row.",
                $"make {source} non-nullable (a feature with a default, a request input declared without '?'), or map a derived feature "
                + "that settles the missing case with '??'");
        }

        if (type.IsList && !type.IsFixedLength)
        {
            return new Problem(pointer, "MODEL_INPUT_TYPE",
                $"Model input '{name}' maps {source} ({type}), a list of any length: a tensor's trailing dimension is the same in every row.",
                $"declare the length of {source}, e.g. {ColumnType.List(type.Element, fixedLength: 64)}; a feature or column of that type keeps "
                + "every row at that length (a row of another length takes the default or is missing)");
        }

        throw new ArgumentException($"{type} has a tensor: {source} is sendable.", nameof(type));
    }

    public static List<Problem> Check(
        string label, TensorLayout layout, IReadOnlyList<MappedInput> inputs, IReadOnlyList<MappedOutput> outputs, ServiceSignature signature)
    {
        var problems = new List<Problem>();
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            mapped.Add(input.Name);
            var pointer = "/inputs/" + JsonPointer.Escape(input.Name);
            if (signature.FindInput(input.Name) is not { } spec)
            {
                problems.Add(new Problem(pointer, "SERVICE_SIGNATURE", $"{label} has no input '{input.Name}'.", "the model's inputs are: " + Names(signature.Inputs)));
            }
            else if (!TryGetInputTensor(input.Type, out var tensor))
            {
                problems.Add(UnsendableInput(input.Name, input.Source, input.Type));
            }
            else if (!Accepts(spec, tensor.ElementType, InputShape(layout, input.Broadcast, -1, tensor.Dimension)))
            {
                var sent = Describe(tensor.ElementType, InputShape(layout, input.Broadcast, -1, tensor.Dimension));
                problems.Add(new Problem(pointer, "MODEL_TENSOR_MISMATCH",
                    $"{label} declares input '{input.Name}' as {Describe(spec)}, but it maps {input.Source} ({input.Type}), which {OperatorId(layout)} sends as {sent}.",
                    MismatchHint(layout, input, sent, spec)));
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

        foreach (var output in outputs)
        {
            if (signature.FindOutput(output.Name) is not { } spec)
            {
                problems.Add(new Problem(output.ConfigKey, "SERVICE_SIGNATURE", $"{label} has no output '{output.Name}'.", "the model's outputs are: " + Names(signature.Outputs)));
            }
            else if (output.Column is { } column)
            {
                // The operator checked that the column's type has a tensor before asking for the signature.
                TryGetOutputTensor(column, out var tensor);
                var shape = OutputShape(layout, -1, tensor.Dimension);
                if (!Accepts(spec, tensor.ElementType, shape))
                {
                    problems.Add(new Problem(output.ConfigKey, "MODEL_TENSOR_MISMATCH",
                        $"{label} declares output '{output.Name}' as {Describe(spec)}, but it is written to a {column} column, which {OperatorId(layout)} reads as {Describe(tensor.ElementType, shape)}.",
                        ColumnTypeFor(OutputShape(layout, -1), spec) is { } other
                            ? $"declare output '{output.Name}' as {Describe(tensor.ElementType, shape)} in the model's signature, or write it to {Article(other)} {other} column, which takes what the model returns"
                            : $"declare output '{output.Name}' as {Describe(tensor.ElementType, shape)} in the model's signature; no column takes {Describe(spec)}"));
                }
            }
            else if (!AcceptsScore(layout, spec))
            {
                problems.Add(new Problem(output.ConfigKey, "MODEL_TENSOR_UNSUPPORTED", $"{label} declares output '{output.Name}' as {Describe(spec)}.", TensorHint(layout)));
            }
        }

        return problems;
    }

    public static bool AcceptsScore(TensorLayout layout, TensorSpec spec) => IsScore(spec.ElementType) && Accepts(spec, spec.ElementType, OutputShape(layout, -1));

    public static bool IsScore(TensorElementType type) => type is TensorElementType.Float32 or TensorElementType.Float64;

    private static bool Accepts(TensorSpec spec, TensorElementType type, int[] expected)
    {
        if (spec.ElementType != type || spec.Shape is not { } shape || shape.Count != expected.Length)
        {
            return false;
        }

        for (var d = 0; d < expected.Length; d++)
        {
            if (shape[d] != -1 && shape[d] != expected[d])
            {
                return false;
            }
        }

        return true;
    }

    private static string MismatchHint(TensorLayout layout, MappedInput input, string sent, TensorSpec declared)
    {
        var fix = $"declare input '{input.Name}' as {sent} in the model's signature";
        if (ReferenceTypeFor(layout, input.Broadcast, declared) is { } type)
        {
            return $"{fix}, or map {Article(type)} {type} derived feature of {input.Source} instead (one definition for training and serving)";
        }

        if (layout != TensorLayout.Rows && ReferenceTypeFor(layout, !input.Broadcast, declared) is { } other)
        {
            var how = layout == TensorLayout.Pairs
                ? "a column's values of the two rows of each pair, and a context value once per pair"
                : "a column's values of the rows of the list, and a context value once per list";
            return $"{fix}, or map {Article(other)} {other} {(input.Broadcast ? "column" : "context value")} instead ({OperatorId(layout)} sends {how})";
        }

        return $"{fix}; no column gives {Describe(declared)}, so a model that needs it is called from an operator that builds other tensors";
    }

    private static string? ReferenceTypeFor(TensorLayout layout, bool broadcast, TensorSpec spec) =>
        ColumnTypeFor(InputShape(layout, broadcast, -1, 0), spec);

    private static string? ColumnTypeFor(int[] leading, TensorSpec spec)
    {
        var shape = spec.Shape;
        if (shape is null || shape.Count < leading.Length || shape.Count > leading.Length + 1)
        {
            return null;
        }

        for (var d = 0; d < leading.Length; d++)
        {
            if (shape[d] != -1 && shape[d] != leading[d])
            {
                return null;
            }
        }

        var element = spec.ElementType is TensorElementType.Bool or TensorElementType.Int32 or TensorElementType.Int64 or TensorElementType.Float32
            or TensorElementType.Float64 or TensorElementType.String ? TensorValues.Name(spec.ElementType) : null;
        if (shape.Count == leading.Length)
        {
            return element;
        }

        var n = shape[shape.Count - 1];
        return element is null ? null
            : n > 0 ? $"list<{element},{n}>"
            : n == -1 ? $"list<{element},N>"
            : null;
    }

    private static string Article(string type) => type[0] == 'i' ? "an" : "a";

    private static string Describe(TensorSpec spec) => Describe(spec.ElementType, spec.Shape ?? Array.Empty<int>());

    private static string Describe(TensorElementType type, IReadOnlyList<int> shape)
    {
        var text = new StringBuilder(Name(type)).Append(" [");
        for (var d = 0; d < shape.Count; d++)
        {
            text.Append(d == 0 ? string.Empty : ", ").Append(shape[d]);
        }

        return text.Append(']').ToString();
    }

    private static string Name(TensorElementType type) => TensorValues.Name(type);

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
