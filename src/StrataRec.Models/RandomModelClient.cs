using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Hashing;

namespace StrataRec.Models;

/// <summary>
/// <c>random@1</c>: a stand-in for a real model that scores each element of its outputs (a row, a pair, a row of a list)
/// with a deterministic pseudo-random value in [0, 1). A dev or test environment binds the real model's logical name
/// (e.g. <c>ctr_ranker</c>) to it instead of the model service, and the pipelines and experiments that call the model stay
/// unchanged (design 6.4 item 1).
/// </summary>
/// <remarks>
/// <para>The settings declare the signature it mimics, so the calling operator's load-time signature check passes as it
/// would against the real model. The short form names the tensors, every one float32 <c>[-1]</c>: what
/// <c>ModelScorer@1</c> reads, and sends for float32 and float64 columns. It sends bool, int32 and int64 columns as int64
/// <c>[-1]</c>, so an input mapped to one of those needs the full form's <c>"type": "int64"</c>:</para>
/// <code>
/// { "client": "random@1",
///   "inputs": ["smoothed_ctr", "log_followers"],   // the model's input names
///   "outputs": ["p_click"],                        // its output names
///   "seed": 42 }                                   // optional: a fixed seed instead of the call's (the same scores in every request)
/// </code>
/// <para>The full form maps each name to its tensor as the real model's signature declares it: an input's <c>type</c>
/// (float32 or int64) and <c>shape</c>, an output's <c>shape</c> (outputs are float32; <c>type</c> may say so). Each
/// dimension is -1 (dynamic) or a positive size. Either form may be used for either setting. A pairwise model, as
/// <c>PairwiseRanker@1</c> calls it (a column as <c>[pairs, 2]</c>, a context value as <c>[pairs]</c>):</para>
/// <code>
/// "inputs":  { "final": { "type": "float32", "shape": [-1, 2] }, "item_id": { "type": "int64", "shape": [-1, 2] },
///              "quality": { "type": "float32", "shape": [-1] } },
/// "outputs": { "p_first": { "shape": [-1] } }
/// </code>
/// <para>and a listwise one, as <c>ListwiseReranker@1</c> calls it (a column as <c>[1, rows]</c>, a context value as
/// <c>[1]</c>): <c>"final": { "type": "float32", "shape": [1, -1] }</c>, <c>"seed": { "type": "int64", "shape": [1] }</c>,
/// <c>"scores": { "shape": [1, -1] }</c>.</para>
/// <para>One rule scores every shape. For each output, an input is <em>per element</em> when its declared shape starts
/// with the output's declared shape (the same dimensions, -1 matching only -1), and <em>shared</em> otherwise. The value
/// of output <c>o</c> at an element is the top 24 bits of the <see cref="StableHash"/> of <c>(salt, seed, x_1, …, x_n)</c>
/// in its canonical encoding, times 2^-24: <c>salt</c> is the hash of the strings <c>(model, o)</c> (the binding's
/// logical name), <c>seed</c> is the binding's or else the call's <see cref="ModelCallOptions.Seed"/> (the model operators
/// pass <c>ctx.request.seed</c>), and <c>x_i</c> is input i in ordinal order of the input names: its sub-tensor at the
/// element's index if it is per element, else the whole input, row-major, float32 values widened exactly to float64 and
/// int64 values as int64. Names, types and shapes are not hashed, so both forms give the same scores for the same
/// values. An output's shape in a call is the leading dimensions of its per-element inputs (they must agree, and the
/// first must be the call's <see cref="ModelCall.BatchSize"/>), so every output needs a per-element input: nothing else
/// would size it or vary it.</para>
/// <para>So a score depends only on what the model would see for that element: a row's own values (and the shared
/// inputs), a pair's two rows in their order, a list row's slice of the list. Reordering and rebatching keep it, a new
/// request seed changes it, and elements with equal inputs score equally, as they would from a real model (ties rank by
/// <c>entity_id</c>). Unlike a real model, a pair's preference and its reverse's are independent draws, and a list row's
/// score ignores the other rows of the list. The canonical encoding treats −0.0 as 0.0 and every NaN alike, as
/// <c>hash01</c> does. 24 bits keep every value exact in float32 and below 1. A context embedding (a <c>list&lt;float32,8&gt;</c>) of a
/// listwise model is declared with its size (<c>[1, 8]</c>): as <c>[1, -1]</c> it would start with the output's shape and count as
/// per element.</para>
/// <para>A call fails unless it sends every declared input with its declared element type and rank, every fixed
/// dimension equal, the first dimension of a per-element input equal to the batch size, and data for the whole shape. A
/// call without a seed fails, unless the binding fixes one: no hidden default seed. A call allocates nothing (the hash's
/// byte buffer is pooled, and the bookkeeping per input is on the stack, or pooled for a model of more than 32 inputs);
/// the client is immutable, needs no load and holds nothing to release.</para>
/// </remarks>
public sealed class RandomModelClient : IModelClient
{
    /// <summary>2^-24: the spacing of the 24-bit scores.</summary>
    private const float Unit = 1f / (1 << 24);

    /// <summary>The inputs whose per-call bookkeeping fits on the stack; a model with more rents it from the pool.</summary>
    private const int StackInputs = 32;

    private readonly string _modelName;

    /// <summary>In ordinal order of the names: the hash order.</summary>
    private readonly TensorSpec[] _inputs;

    private readonly int[][] _inputShapes;
    private readonly TensorSpec[] _outputs;
    private readonly int[] _outputRanks;

    /// <summary>Input j is per element of output o at <c>o · inputs + j</c>.</summary>
    private readonly bool[] _perElement;

    /// <summary>Input j is per element of some output, so its first dimension is the batch.</summary>
    private readonly bool[] _batched;

    /// <summary>The first input per element of each output, whose leading dimensions give the output's shape.</summary>
    private readonly int[] _sizing;

    private readonly long[] _salts;
    private readonly long? _seed;
    private readonly ModelSignature _signature;

    /// <param name="inputs">Distinct, non-empty names, float32 or int64, each dimension -1 or positive; at least one.</param>
    /// <param name="outputs">Distinct, non-empty names, float32, each with an input per element (<see cref="IsPerElement"/>); at least one.</param>
    internal RandomModelClient(string modelName, TensorSpec[] inputs, TensorSpec[] outputs, long? seed)
    {
        _modelName = modelName;
        var names = new string[inputs.Length];
        for (var j = 0; j < inputs.Length; j++)
        {
            names[j] = inputs[j].Name;
        }

        _inputs = (TensorSpec[])inputs.Clone();
        Array.Sort(names, _inputs, StringComparer.Ordinal);
        _inputShapes = new int[_inputs.Length][];
        for (var j = 0; j < _inputs.Length; j++)
        {
            _inputShapes[j] = ToArray(_inputs[j].Shape);
        }

        _outputs = (TensorSpec[])outputs.Clone();
        _outputRanks = new int[_outputs.Length];
        _perElement = new bool[_outputs.Length * _inputs.Length];
        _batched = new bool[_inputs.Length];
        _sizing = new int[_outputs.Length];
        for (var o = 0; o < _outputs.Length; o++)
        {
            _outputRanks[o] = _outputs[o].Shape.Count;
            _sizing[o] = -1;
            for (var j = 0; j < _inputs.Length; j++)
            {
                if (IsPerElement(_inputs[j].Shape, _outputs[o].Shape))
                {
                    _perElement[(o * _inputs.Length) + j] = true;
                    _batched[j] = true;
                    if (_sizing[o] < 0)
                    {
                        _sizing[o] = j;
                    }
                }
            }

            if (_sizing[o] < 0)
            {
                throw new ArgumentException($"Output '{_outputs[o].Name}' has no input per element.", nameof(outputs));
            }
        }

        _seed = seed;
        _salts = new long[_outputs.Length];
        var salt = new StableHash.Builder();
        try
        {
            for (var o = 0; o < _outputs.Length; o++)
            {
                salt.Reset();
                salt.Add(modelName);
                salt.Add(_outputs[o].Name);
                _salts[o] = unchecked((long)salt.Finish());
            }
        }
        finally
        {
            salt.Dispose();
        }

        _signature = new ModelSignature((TensorSpec[])_inputs.Clone(), (TensorSpec[])_outputs.Clone());
    }

    public string ClientType => RandomModelClientFactory.Type;

    /// <summary>
    /// True when an input of shape <paramref name="input"/> is scored per element of an output of shape
    /// <paramref name="output"/>: its declared shape starts with the output's, -1 matching only -1.
    /// </summary>
    internal static bool IsPerElement(IReadOnlyList<int> input, IReadOnlyList<int> output)
    {
        if (input.Count < output.Count)
        {
            return false;
        }

        for (var d = 0; d < output.Count; d++)
        {
            if (input[d] != output[d])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Answers at once with the signature the settings declare, for any version.</summary>
    public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
        string.Equals(model, _modelName, StringComparison.Ordinal)
            ? new ValueTask<ModelSignature>(_signature)
            : ValueTask.FromException<ModelSignature>(new ArgumentException($"This client serves model '{_modelName}', not '{model}'.", nameof(model)));

    public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
        new(Score(call, outputs));

    private ModelStatus Score(ModelCall call, IModelOutputWriter writer)
    {
        if ((_seed ?? call.Options.Seed) is not { } seed)
        {
            return ModelStatus.Failed($"Model '{_modelName}' ({RandomModelClientFactory.Type}) got a call without a seed and its binding fixes none; " +
                "pass ModelCallOptions.Seed (the model operators pass ctx.request.seed), or set \"seed\" in the binding.");
        }

        var batch = call.BatchSize;
        if (batch < 0)
        {
            return ModelStatus.Failed($"The call's batch size is {batch}; it must not be negative.");
        }

        var n = _inputs.Length;
        int[]? rented = null;
        Span<int> scratch = n <= StackInputs ? stackalloc int[2 * StackInputs] : (rented = ArrayPool<int>.Shared.Rent(2 * n)).AsSpan();
        try
        {
            return Evaluate(call.Inputs.Span, batch, seed, scratch[..n], scratch.Slice(n, n), writer);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    /// <param name="slots">Per input in hash order: its position in the call.</param>
    /// <param name="lengths">Per input in hash order, for the output being written: how many values it adds to an element's hash.</param>
    private ModelStatus Evaluate(ReadOnlySpan<NamedInput> inputs, int batch, long seed, Span<int> slots, Span<int> lengths, IModelOutputWriter writer)
    {
        var n = _inputs.Length;
        for (var j = 0; j < n; j++)
        {
            var slot = Find(inputs, j);
            if (slot < 0)
            {
                return ModelStatus.Failed($"Missing input '{_inputs[j].Name}'.");
            }

            if (!Fits(inputs[slot], j, batch))
            {
                return ModelStatus.Failed(Mismatch(inputs[slot], j, batch));
            }

            slots[j] = slot;
        }

        // Every output's shape before writing any: a failed call writes nothing.
        for (var o = 0; o < _outputs.Length; o++)
        {
            var sizing = inputs[slots[_sizing[o]]].Shape;
            for (var j = 0; j < n; j++)
            {
                if (_perElement[(o * n) + j] && !inputs[slots[j]].Shape.AsSpan(0, _outputRanks[o]).SequenceEqual(sizing.AsSpan(0, _outputRanks[o])))
                {
                    return ModelStatus.Failed($"Inputs '{_inputs[_sizing[o]].Name}' and '{_inputs[j].Name}' give output '{_outputs[o].Name}' ({Describe(_outputs[o])}) " +
                        $"different shapes, {Dims(sizing, _outputRanks[o])} and {Dims(inputs[slots[j]].Shape, _outputRanks[o])}: the inputs scored per element of an " +
                        "output must agree on its dimensions.");
                }
            }

            if (Elements(sizing, 0, _outputRanks[o]) > int.MaxValue)
            {
                return ModelStatus.Failed($"Output '{_outputs[o].Name}' would have {Dims(sizing, _outputRanks[o])} elements, more than an output buffer holds.");
            }
        }

        var hash = new StableHash.Builder();
        try
        {
            for (var o = 0; o < _outputs.Length; o++)
            {
                var rank = _outputRanks[o];
                var sizing = inputs[slots[_sizing[o]]].Shape;
                var count = (int)Elements(sizing, 0, rank);
                var scores = writer.GetFloat32(_outputs[o].Name, count).Span;
                if (scores.Length < count)
                {
                    return ModelStatus.Failed($"The output writer gave {scores.Length} values for output '{_outputs[o].Name}', which has {count}.");
                }

                if (count == 0)
                {
                    continue;
                }

                // With at least one element, every leading dimension is positive, so a sub-tensor holds at most the input's values.
                for (var j = 0; j < n; j++)
                {
                    var shape = inputs[slots[j]].Shape;
                    lengths[j] = (int)Elements(shape, _perElement[(o * n) + j] ? rank : 0, shape.Length);
                }

                for (var e = 0; e < count; e++)
                {
                    hash.Reset();
                    hash.Add(_salts[o]);
                    hash.Add(seed);
                    for (var j = 0; j < n; j++)
                    {
                        ref readonly var input = ref inputs[slots[j]];
                        var length = lengths[j];
                        var start = _perElement[(o * n) + j] ? e * length : 0;
                        if (input.ElementType == TensorElementType.Int64)
                        {
                            foreach (var value in input.Int64.Span.Slice(start, length))
                            {
                                hash.Add(value);
                            }
                        }
                        else
                        {
                            foreach (var value in input.Float32.Span.Slice(start, length))
                            {
                                hash.Add((double)value);
                            }
                        }
                    }

                    scores[e] = (hash.Finish() >> 40) * Unit;
                }
            }
        }
        finally
        {
            hash.Dispose();
        }

        return ModelStatus.Ok;
    }

    /// <summary>
    /// The call's input <paramref name="j"/> (in hash order) has the declared element type and rank, every fixed dimension
    /// equal, its first dimension the batch if it is per element of some output, and data for its whole shape.
    /// </summary>
    private bool Fits(in NamedInput input, int j, int batch)
    {
        var declared = _inputShapes[j];
        var shape = input.Shape;
        if (input.ElementType != _inputs[j].ElementType || shape is null || shape.Length != declared.Length)
        {
            return false;
        }

        for (var d = 0; d < shape.Length; d++)
        {
            if (shape[d] < 0 || (declared[d] > 0 && shape[d] != declared[d]) || (d == 0 && _batched[j] && shape[d] != batch))
            {
                return false;
            }
        }

        return Elements(shape, 0, shape.Length) <= Available(input);
    }

    private string Mismatch(in NamedInput input, int j, int batch)
    {
        var spec = _inputs[j];
        if (_batched[j] && _inputShapes[j] is [-1])
        {
            return $"Input '{spec.Name}' must be {TypeName(spec.ElementType)} with one value per row ({batch}).";
        }

        var expected = _batched[j] ? $"{Describe(spec)} with the call's batch size ({batch}) as its first dimension" : Describe(spec);
        var sent = input.Shape is { } shape ? Dims(shape, shape.Length) : "[]";
        return $"Input '{spec.Name}' must be {expected}; the call sends {TypeName(input.ElementType)} {sent} with {Available(input)} values.";
    }

    private static int Available(in NamedInput input) => input.ElementType switch
    {
        TensorElementType.Float32 => input.Float32.Length,
        TensorElementType.Int64 => input.Int64.Length,
        _ => 0,
    };

    /// <summary>
    /// The number of elements in dimensions [<paramref name="from"/>, <paramref name="to"/>) of a shape with no negative
    /// dimension: exact up to <see cref="int.MaxValue"/>, and <c>int.MaxValue + 1</c> for any more.
    /// </summary>
    private static long Elements(int[] shape, int from, int to)
    {
        for (var d = from; d < to; d++)
        {
            if (shape[d] == 0)
            {
                return 0;
            }
        }

        var count = 1L;
        for (var d = from; d < to; d++)
        {
            count *= shape[d];
            if (count > int.MaxValue)
            {
                return int.MaxValue + 1L;
            }
        }

        return count;
    }

    /// <summary>The position of input <paramref name="j"/> (in hash order) in the call, or -1.</summary>
    private int Find(ReadOnlySpan<NamedInput> inputs, int j)
    {
        var name = _inputs[j].Name;
        if (j < inputs.Length && string.Equals(inputs[j].Name, name, StringComparison.Ordinal))
        {
            return j;
        }

        for (var i = 0; i < inputs.Length; i++)
        {
            if (string.Equals(inputs[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int[] ToArray(IReadOnlyList<int> shape)
    {
        var dims = new int[shape.Count];
        for (var d = 0; d < dims.Length; d++)
        {
            dims[d] = shape[d];
        }

        return dims;
    }

    /// <summary>A tensor spec as <c>int64 [-1, 2]</c>.</summary>
    internal static string Describe(TensorSpec spec) => $"{TypeName(spec.ElementType)} {Dims(spec.Shape, spec.Shape.Count)}";

    /// <summary>The first <paramref name="rank"/> dimensions, as <c>[3, 2]</c>.</summary>
    internal static string Dims(IReadOnlyList<int> shape, int rank)
    {
        var text = new StringBuilder("[");
        for (var d = 0; d < rank; d++)
        {
            text.Append(d == 0 ? string.Empty : ", ").Append(shape[d]);
        }

        return text.Append(']').ToString();
    }

    internal static string TypeName(TensorElementType type) => type switch
    {
        TensorElementType.Float32 => "float32",
        TensorElementType.Int64 => "int64",
        _ => "text",
    };
}

/// <summary>Settings of <c>random@1</c>.</summary>
public sealed class RandomSettings
{
    /// <summary>
    /// The inputs of the model it stands in for, as the model declares them: their names, each a float32 tensor of shape
    /// <c>[-1]</c> (<c>["smoothed_ctr", "log_followers"]</c>), or each name mapped to its tensor
    /// (<c>{ "item_id": { "type": "int64", "shape": [-1, 2] } }</c>). At least one, each once.
    /// </summary>
    public required JsonElement Inputs { get; init; }

    /// <summary>
    /// The outputs, as the model declares them: their names, each a float32 tensor of shape <c>[-1]</c>
    /// (<c>["p_click"]</c>), or each name mapped to its shape (<c>{ "p_first": { "shape": [-1] } }</c>). At least one, each once.
    /// </summary>
    public required JsonElement Outputs { get; init; }

    /// <summary>A fixed seed instead of the call's (<c>ctx.request.seed</c>): every request gets the same scores.</summary>
    public long? Seed { get; init; }
}

/// <summary>A tensor of <c>random@1</c>'s inputs or outputs mapped by name, as the model declares it.</summary>
public sealed class RandomTensorSettings
{
    /// <summary>Required for an input: <c>float32</c> or <c>int64</c>. An output is <c>float32</c>.</summary>
    [AllowedValues("float32", "int64")]
    public string? Type { get; init; }

    /// <summary>The dimensions, each -1 (dynamic) or a positive size, e.g. <c>[-1, 2]</c>: at least one.</summary>
    public required List<int> Shape { get; init; }
}

/// <summary>Factory for <c>random@1</c>. <see cref="Create"/> only reads and checks the settings.</summary>
public sealed class RandomModelClientFactory : ModelClientFactory<RandomSettings>
{
    public const string Type = "random@1";

    private const string InputExample = "smoothed_ctr\", \"log_followers";
    private const string OutputExample = "p_click";

    public override string ClientType => Type;

    protected override IModelClient Create(RandomSettings settings, BindingContext context)
    {
        var (inputSpecs, _) = Parse(context, settings.Inputs, "inputs");
        var (outputSpecs, outputPointers) = Parse(context, settings.Outputs, "outputs");
        var seed = settings.Seed;
        for (var o = 0; o < outputSpecs.Length; o++)
        {
            var perElement = false;
            for (var j = 0; j < inputSpecs.Length && !perElement; j++)
            {
                perElement = RandomModelClient.IsPerElement(inputSpecs[j].Shape, outputSpecs[o].Shape);
            }

            if (!perElement)
            {
                var shape = RandomModelClient.Dims(outputSpecs[o].Shape, outputSpecs[o].Shape.Count);
                throw new BindingException(outputPointers[o],
                    $"Output '{outputSpecs[o].Name}' ({shape}) has no input whose shape starts with {shape}: no input is scored per element of it, so nothing "
                    + "would give its size or vary its scores.",
                    $"declare the shapes as the model does: the inputs it reads per element of '{outputSpecs[o].Name}' start with {shape} (-1 matches only -1), "
                    + "e.g. [-1] or [-1, N] for an output [-1] of one value per row, [-1, 2] for one per pair, [1, -1] or [1, -1, N] for an output [1, -1] of one per list row");
            }
        }

        return new RandomModelClient(context.Name, inputSpecs, outputSpecs, seed);
    }

    /// <summary>The declared tensors of a setting, and where to report a problem with each one's shape.</summary>
    private readonly record struct Tensors(TensorSpec[] Specs, string[] ShapePointers);

    /// <summary>
    /// <c>"inputs": ["smoothed_ctr"]</c> (each float32 <c>[-1]</c>) or <c>"inputs": { "item_id": { "type": "int64", "shape": [-1, 2] } }</c>:
    /// a setting of two forms, bound as the one it is written in.
    /// </summary>
    private static Tensors Parse(BindingContext context, JsonElement value, string setting)
    {
        var pointer = "/" + setting;
        var specs = new List<TensorSpec>();
        var pointers = new List<string>();
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                var names = BindingSettings.Bind<List<string>>(context, value, pointer, $"'{setting}' of {Type}");
                SettingChecks.Names(names, setting, Hint(setting));
                foreach (var name in names)
                {
                    pointers.Add(pointer + "/" + specs.Count);
                    specs.Add(new TensorSpec(name, TensorElementType.Float32, new[] { -1 }));
                }

                break;
            case JsonValueKind.Object:
                foreach (var (name, spec) in BindingSettings.Bind<Dictionary<string, RandomTensorSettings>>(context, value, pointer, $"'{setting}' of {Type}"))
                {
                    var specPointer = pointer + "/" + BindingContext.EscapePointerToken(name);
                    if (name.Length == 0)
                    {
                        throw new BindingException(specPointer, $"'{setting}' maps an empty name.", Hint(setting));
                    }

                    specs.Add(Spec(spec, setting == "inputs", name, specPointer));
                    pointers.Add(specPointer + "/shape");
                }

                break;
            default:
                throw new BindingException(pointer, $"'{setting}' must be an array of names or an object mapping each name to its tensor spec, got {value.GetRawText()}.",
                    Hint(setting));
        }

        if (specs.Count == 0)
        {
            throw new BindingException(pointer, setting == "inputs"
                ? "'inputs' is empty: the scores derive from the inputs, so every row would score the same."
                : "'outputs' is empty: the model would produce nothing.", Hint(setting));
        }

        return new Tensors(specs.ToArray(), pointers.ToArray());
    }

    /// <summary>One tensor spec of the full form: <c>{ "type": "int64", "shape": [-1, 2] }</c>; an output's type is float32.</summary>
    private static TensorSpec Spec(RandomTensorSettings spec, bool input, string name, string pointer)
    {
        var what = (input ? "input '" : "output '") + name + "'";
        var title = (input ? "Input '" : "Output '") + name + "'";
        var hint = SpecHint(input);
        var type = spec.Type switch
        {
            null when input => throw new BindingException(pointer + "/type", $"{title} needs a 'type': float32 or int64, as the model declares it.", hint),
            null or "float32" => TensorElementType.Float32,
            "int64" when input => TensorElementType.Int64,
            _ => throw new BindingException(pointer + "/type", input
                ? $"{title} has type \"{spec.Type}\", but the model operators send float32 and int64 tensors only."
                : $"{title} has type \"{spec.Type}\", but the model operators read float32 outputs only.", hint),
        };

        if (spec.Shape.Count == 0)
        {
            throw new BindingException(pointer + "/shape", $"The shape of {what} must have at least one dimension.", hint);
        }

        for (var d = 0; d < spec.Shape.Count; d++)
        {
            if (spec.Shape[d] != -1 && spec.Shape[d] < 1)
            {
                throw new BindingException(pointer + "/shape/" + d, $"Dimension {d} of {what} must be -1 (dynamic) or a positive size, got {spec.Shape[d]}.", hint);
            }
        }

        return new TensorSpec(name, type, spec.Shape.ToArray());
    }

    private static string Hint(string setting) => setting == "inputs"
        ? $"list the input names the model declares, e.g. \"inputs\": [\"{InputExample}\"], each a float32 tensor of shape [-1] (one value per row), "
          + "or map each name to its tensor as the model declares it, e.g. \"inputs\": { \"item_id\": { \"type\": \"int64\", \"shape\": [-1, 2] } }"
        : $"list the output names the model declares, e.g. \"outputs\": [\"{OutputExample}\"], each a float32 tensor of shape [-1] (one value per row), "
          + "or map each name to its shape as the model declares it, e.g. \"outputs\": { \"p_first\": { \"shape\": [-1] } }";

    private static string SpecHint(bool input) => input
        ? "declare the input as the model's signature does, e.g. \"item_id\": { \"type\": \"int64\", \"shape\": [-1, 2] }: the type is float32 or int64, "
          + "and each dimension is -1 (dynamic) or a positive size"
        : "declare the output as the model's signature does, e.g. \"p_first\": { \"shape\": [-1] }: outputs are float32, "
          + "and each dimension is -1 (dynamic) or a positive size";
}
