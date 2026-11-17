using System.Text;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Types;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>
/// How a model operator lays out a call's batch, which gives every tensor of the call its leading dimensions (design 5.9,
/// 6.9). The value a row contributes (one float32 or int64, or N of them for a <c>list&lt;E,N&gt;</c>) follows its column
/// type in every layout.
/// </summary>
internal enum TensorLayout
{
    /// <summary><c>ModelScorer@1</c>: one entry per row; a column or context value as <c>[rows]</c>.</summary>
    Rows,

    /// <summary>
    /// <c>PairwiseRanker@1</c>: one entry per pair of rows; a column as <c>[pairs, 2]</c> (the higher-ranked row first), a
    /// context value as <c>[pairs]</c>.
    /// </summary>
    Pairs,

    /// <summary><c>ListwiseReranker@1</c>: one entry, the list; a column as <c>[1, rows]</c>, a context value as <c>[1]</c>.</summary>
    List,
}

/// <summary>
/// The one rule for which tensors the model operators send and read, and so which model signatures they can call
/// (design 6.9). A version that does not match would load cleanly and then fail (or fall back) on every request, so every
/// version a node may use is checked, and a mismatch is a load error.
/// </summary>
/// <remarks>
/// <para>The value of an input follows the declared type of the column or context value it maps: <c>float32</c> and
/// <c>float64</c> are sent as float32; <c>bool</c> (0/1), <c>int32</c> and <c>int64</c> as int64, with their exact
/// values; a fixed-length list <c>list&lt;E,N&gt;</c> (an embedding, a window of ids) as its element would be, with a
/// trailing dimension N, row-major. A bool is an int64 because it is a category, not a measurement: an exact 0/1 that a
/// model embeds or compares, as it does int ids, and every non-float element type then goes the same way. This is the
/// built-in operators' mapping; an operator or model client of one's own may
/// convert differently. Nullable types, strings, text and lists of any length cannot be sent: a model needs a value in
/// every row, a tensor has no place for the others, and its trailing dimension must be the same in every row. The layout
/// (<see cref="TensorLayout"/>) adds the leading dimensions: <c>[rows]</c>, <c>[pairs, 2]</c> or <c>[1, rows]</c> for a
/// column, <c>[rows]</c>, <c>[pairs]</c> or <c>[1]</c> for a context value, which is broadcast to every batch entry.</para>
/// <para>The column decides, never the model's signature: replay records a digest of the inputs, element type and shape
/// included, and replay engines and <c>stratarec validate</c> compile plans without loading clients, so they have no
/// signature. If the signature decided, the same configuration would build different tensors depending on whether the
/// client was loaded, and a replay would report its inputs as changed.</para>
/// <para>So a version must declare exactly the mapped inputs (every input of a signature is required), each with the
/// element type its mapping gives and a shape of the same rank, and every mapped output as float32 with the layout's
/// shape. A dynamic dimension (rows, pairs, the list's length) must be declared <c>-1</c>; a fixed one (the pair's 2, the
/// list batch's 1, a list's N) as that size or <c>-1</c>, which a signature uses for "any". A model that declares
/// float32 for an int or bool column is fed a float32 derived feature, which design 6.9 rule 2 prefers anyway: one
/// definition for training and serving.</para>
/// </remarks>
internal static class ModelTensorRule
{
    /// <summary>One mismatch, located by a config key (a JSON pointer below the node's <c>config</c>).</summary>
    public readonly record struct Problem(string ConfigKey, string Code, string Message, string Hint);

    /// <summary>A mapped input as the check sees it.</summary>
    /// <param name="Name">The model input name.</param>
    /// <param name="Source">The reference it maps, e.g. <c>col.item.id</c>.</param>
    /// <param name="Type">The reference's declared type, which decides the tensor.</param>
    /// <param name="Broadcast">A request-level value (<c>ctx.*</c>, <c>param.*</c>), sent once per batch entry rather than per row.</param>
    public readonly record struct MappedInput(string Name, string Source, ColumnType Type, bool Broadcast = false);

    /// <summary>A model output the operator reads.</summary>
    /// <param name="Name">The model output name.</param>
    /// <param name="ConfigKey">Where the config names it, e.g. <c>/outputs/p_click</c> or <c>/output</c>.</param>
    public readonly record struct MappedOutput(string Name, string ConfigKey);

    /// <summary>The value one row contributes to an input's tensor.</summary>
    /// <param name="ElementType">Float32 or Int64.</param>
    /// <param name="Dimension">0 for one value per row; N for a <c>list&lt;E,N&gt;</c> per row.</param>
    public readonly record struct InputTensor(TensorElementType ElementType, int Dimension)
    {
        /// <summary>As a signature of <c>ModelScorer@1</c> declares it, e.g. <c>float32 [-1, 8]</c>.</summary>
        public override string ToString() => Describe(ElementType, InputShape(TensorLayout.Rows, false, -1, Dimension));
    }

    /// <summary>The operator that builds <paramref name="layout"/>'s tensors, as messages name it.</summary>
    public static string OperatorId(TensorLayout layout) => layout switch
    {
        TensorLayout.Rows => "ModelScorer@1",
        TensorLayout.Pairs => "PairwiseRanker@1",
        _ => "ListwiseReranker@1",
    };

    /// <summary>What the operator of <paramref name="layout"/> sends and reads, for hints.</summary>
    public static string TensorHint(TensorLayout layout) => layout switch
    {
        TensorLayout.Rows =>
            "ModelScorer@1 sends a float32 or float64 column or context value as float32 [-1], a bool, int32 or int64 one as "
            + "int64 [-1], and a list<E,N> one as its element would be with a trailing N (list<float32,N> as float32 [-1, N]), and reads "
            + "every output as float32 [-1] (one value per row; the batch dimension is dynamic). Change the model's signature, or call the "
            + "model from an operator that builds other tensors.",
        TensorLayout.Pairs =>
            "PairwiseRanker@1 sends a float32 or float64 column as float32 [-1, 2], a bool, int32 or int64 one as int64 [-1, 2] and a "
            + "list<E,N> one with a trailing N (list<float32,N> as float32 [-1, 2, N]; the two rows of each pair, the higher-ranked first), "
            + "a context value as float32 [-1], int64 [-1] or with a trailing N (the same in every pair), and reads the output as float32 [-1] "
            + "(one value per pair: the probability that its first row is preferred). The pair dimension is dynamic; the 2 and N may be "
            + "declared -1. Change the model's signature, or call the model from an operator that builds other tensors.",
        _ =>
            "ListwiseReranker@1 sends a float32 or float64 column as float32 [1, -1], a bool, int32 or int64 one as int64 [1, -1] and a "
            + "list<E,N> one with a trailing N (list<float32,N> as float32 [1, -1, N]; the window's rows as one list), a context value as "
            + "float32 [1], int64 [1] or with a trailing N, and reads the output as float32 [1, -1] (one score per row of the list). The "
            + "list's length is dynamic; the 1 and N may be declared -1. Change the model's signature, or call the model from an operator "
            + "that builds other tensors.",
    };

    /// <summary>The value an operator sends for a value of <paramref name="type"/>; false for a type it cannot send.</summary>
    public static bool TryGetInputTensor(ColumnType type, out InputTensor tensor)
    {
        tensor = default;
        if (type.Nullable)
        {
            return false;
        }

        var element = type.IsList ? type.Element : type.Kind;
        if (type.IsList && !type.IsFixedLength)
        {
            return false;
        }

        switch (element)
        {
            case StorageKind.Float32 or StorageKind.Float64:
                tensor = new InputTensor(TensorElementType.Float32, type.FixedLength);
                return true;
            case StorageKind.Bool or StorageKind.Int32 or StorageKind.Int64:
                tensor = new InputTensor(TensorElementType.Int64, type.FixedLength);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The tensor element type of a value of <paramref name="kind"/> (a scalar's kind or a list's element):
    /// float32 for a float, int64 for a bool or an integer.</summary>
    public static TensorElementType ElementTypeOf(StorageKind kind) =>
        kind is StorageKind.Float32 or StorageKind.Float64 ? TensorElementType.Float32 : TensorElementType.Int64;

    /// <summary>
    /// The shape of an input's tensor in <paramref name="layout"/>: <paramref name="length"/> is the batch's dynamic
    /// dimension (rows, pairs, or the list's rows), or -1 for the shape a signature declares; <paramref name="dimension"/>
    /// is a <c>list&lt;E,N&gt;</c>'s N, or 0. A context value has no row dimension of its own: it is repeated for every batch entry.
    /// </summary>
    public static int[] InputShape(TensorLayout layout, bool broadcast, int length, int dimension) => (layout, broadcast, dimension) switch
    {
        // The final array at once: one per input and batch, which a list's shape built in two steps allocated twice.
        (TensorLayout.Rows, _, 0) or (TensorLayout.Pairs, true, 0) => [length],
        (TensorLayout.Rows, _, _) or (TensorLayout.Pairs, true, _) => [length, dimension],
        (TensorLayout.Pairs, false, 0) => [length, 2],
        (TensorLayout.Pairs, false, _) => [length, 2, dimension],
        (_, true, 0) => [1],
        (_, true, _) => [1, dimension],
        (_, false, 0) => [1, length],
        _ => [1, length, dimension],
    };

    /// <summary>The shape of an output in <paramref name="layout"/>, as <see cref="InputShape"/>: one float32 per row, pair or list row.</summary>
    public static int[] OutputShape(TensorLayout layout, int length) => layout == TensorLayout.List ? [1, length] : [length];

    /// <summary>
    /// How many values of a column one entry of the batch's dynamic dimension holds: 2 for a pair, 1 for a row (in
    /// <see cref="TensorLayout.Rows"/>, and along the list).
    /// </summary>
    public static int ColumnEntries(TensorLayout layout) => layout == TensorLayout.Pairs ? 2 : 1;

    /// <summary>The compile error for an input whose type <see cref="TryGetInputTensor"/> rejects.</summary>
    public static Problem UnsendableInput(TensorLayout layout, string name, string source, ColumnType type)
    {
        var pointer = "/inputs/" + JsonPointer.Escape(name);
        if (type.Nullable)
        {
            return new Problem(pointer, "MODEL_INPUT_TYPE", $"Model input '{name}' maps {source}, which is nullable ({type}): a model needs a value in every row.",
                $"make {source} non-nullable (a feature with a default, a request input declared without '?'), or map a derived feature "
                + "that settles the missing case with '??' (design 6.9)");
        }

        if (type.IsList && !type.IsFixedLength && type.Element != StorageKind.String)
        {
            return new Problem(pointer, "MODEL_INPUT_TYPE",
                $"Model input '{name}' maps {source} ({type}), a list of any length: a tensor's trailing dimension is the same in every row.",
                $"declare the length of {source}, e.g. {ColumnType.List(type.Element, fixedLength: 64)}; a feature or column of that type keeps "
                + "every row at that length (a row of another length takes the default or is missing, design 4.3)");
        }

        return new Problem(pointer, "MODEL_INPUT_TYPE", $"Model input '{name}' maps {source} ({type}), which {OperatorId(layout)} cannot send as a tensor.",
            "map a float32, float64, bool, int32 or int64 column or context value, or a list<E,N> of those (a derived feature, design 6.9), "
            + "or call the model from an operator that builds other tensors");
    }

    /// <param name="label">The model and version, as messages name them.</param>
    /// <param name="layout">The layout of the operator that calls the model.</param>
    /// <param name="inputs">The config's <c>inputs</c>, with the declared type of each reference.</param>
    /// <param name="outputs">The model outputs the operator reads.</param>
    public static List<Problem> Check(
        string label, TensorLayout layout, IReadOnlyList<MappedInput> inputs, IReadOnlyList<MappedOutput> outputs, ModelSignature signature)
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
            else if (!TryGetInputTensor(input.Type, out var tensor))
            {
                problems.Add(UnsendableInput(layout, input.Name, input.Source, input.Type));
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
                problems.Add(new Problem(output.ConfigKey, "MODEL_SIGNATURE", $"{label} has no output '{output.Name}'.", "the model's outputs are: " + Names(signature.Outputs)));
            }
            else if (!AcceptsOutput(layout, spec))
            {
                problems.Add(new Problem(output.ConfigKey, "MODEL_TENSOR_UNSUPPORTED", $"{label} declares output '{output.Name}' as {Describe(spec)}.", TensorHint(layout)));
            }
        }

        return problems;
    }

    /// <summary>A float32 tensor of the layout's output shape: one value per row, pair or list row, the only output the operator reads.</summary>
    public static bool AcceptsOutput(TensorLayout layout, TensorSpec spec) => Accepts(spec, TensorElementType.Float32, OutputShape(layout, -1));

    /// <summary>
    /// True when <paramref name="spec"/> declares the element type and the shape built: the same rank, every dynamic
    /// dimension (-1 in <paramref name="expected"/>) declared -1, every fixed one declared as its size or -1.
    /// </summary>
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

    /// <summary>
    /// Either side may change: the model's declaration, or the mapping, to a reference that gives what the model declares:
    /// a derived feature of another type, or (in a layout that sends columns and context values differently) the other
    /// kind of reference.
    /// </summary>
    private static string MismatchHint(TensorLayout layout, MappedInput input, string sent, TensorSpec declared)
    {
        var fix = $"declare input '{input.Name}' as {sent} in the model's signature";
        if (ReferenceTypeFor(layout, input.Broadcast, declared) is { } type)
        {
            return $"{fix}, or map {Article(type)} {type} derived feature of {input.Source} instead (one definition for training and serving, design 6.9)";
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

    /// <summary>
    /// The type of a column (or, with <paramref name="broadcast"/>, a context value) that <paramref name="layout"/> sends
    /// as <paramref name="spec"/>, or null when none does. A list whose N is declared -1 is any <c>list&lt;E,N&gt;</c>.
    /// </summary>
    private static string? ReferenceTypeFor(TensorLayout layout, bool broadcast, TensorSpec spec)
    {
        var shape = spec.Shape;
        var leading = InputShape(layout, broadcast, -1, 0);
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

        if (shape.Count == leading.Length)
        {
            return spec.ElementType switch
            {
                TensorElementType.Float32 => "float32",
                TensorElementType.Int64 => "int64",
                _ => null,
            };
        }

        var element = spec.ElementType switch
        {
            TensorElementType.Float32 => "float32",
            TensorElementType.Int64 => "int64",
            _ => null,
        };
        var n = shape[shape.Count - 1];
        return element is null ? null
            : n > 0 ? $"list<{element},{n}>"
            : n == -1 ? $"list<{element},N>"
            : null;
    }

    private static string Article(string type) => type[0] == 'i' ? "an" : "a";

    /// <summary>A tensor spec as <c>int64 [-1, 2]</c>.</summary>
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
