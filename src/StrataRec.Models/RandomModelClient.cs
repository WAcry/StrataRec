using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Hashing;

namespace StrataRec.Models;

/// <summary>
/// <c>random@1</c>: a stand-in for a real model that scores each row with a deterministic pseudo-random value in [0, 1).
/// A dev or test environment binds the real model's logical name (e.g. <c>ctr_ranker</c>) to it instead of the model
/// service, and the pipelines and experiments that call the model stay unchanged (design 6.4 item 1).
/// </summary>
/// <remarks>
/// <para>The settings declare the signature it mimics; every tensor is float32 <c>[-1]</c>, what <c>ModelScorer@1</c>
/// sends and reads, so the scorer's load-time signature check passes as it would against the real model:</para>
/// <code>
/// { "client": "random@1",
///   "inputs": ["smoothed_ctr", "log_followers"],   // the model's input names
///   "outputs": ["p_click"],                        // its output names
///   "seed": 42 }                                   // optional: a fixed seed instead of the call's (the same scores in every request)
/// </code>
/// <para>Any version is accepted. A row's value of output <c>o</c> is the top 24 bits of the <see cref="StableHash"/> of
/// <c>(salt, seed, x_1, …, x_n)</c> in its canonical encoding (int64, int64, float64…), times 2^-24: <c>salt</c> is the
/// hash of the strings <c>(model, o)</c> (the binding's logical name), <c>seed</c> is the binding's or else the call's
/// <see cref="ModelCallOptions.Seed"/> (<c>ModelScorer@1</c> passes <c>ctx.request.seed</c>), and <c>x</c> are the row's
/// input values in ordinal order of the input names, widened exactly to float64. So a row's score depends on nothing
/// else: reordering and rebatching keep it, a new request seed changes it, and rows with equal inputs score equally, as
/// they would from a real model (ties rank by <c>entity_id</c>). The canonical encoding treats −0.0 as 0.0 and every NaN
/// alike, as <c>hash01</c> does. 24 bits keep every value exact in float32 and below 1.</para>
/// <para>A call without a seed fails, unless the binding fixes one: no hidden default seed. A call allocates nothing (the
/// hash's byte buffer is pooled); the client is immutable, needs no load and holds nothing to release.</para>
/// </remarks>
public sealed class RandomModelClient : IModelClient
{
    /// <summary>2^-24: the spacing of the 24-bit scores.</summary>
    private const float Unit = 1f / (1 << 24);

    private readonly string _modelName;
    private readonly string[] _inputs;
    private readonly string[] _outputs;
    private readonly long[] _salts;
    private readonly long? _seed;
    private readonly ModelSignature _signature;

    /// <param name="inputs">Distinct, non-empty names; at least one.</param>
    /// <param name="outputs">Distinct, non-empty names; at least one.</param>
    internal RandomModelClient(string modelName, string[] inputs, string[] outputs, long? seed)
    {
        _modelName = modelName;
        _inputs = (string[])inputs.Clone();
        Array.Sort(_inputs, StringComparer.Ordinal);
        _outputs = (string[])outputs.Clone();
        _seed = seed;
        _salts = new long[_outputs.Length];
        var salt = new StableHash.Builder();
        try
        {
            for (var o = 0; o < _outputs.Length; o++)
            {
                salt.Reset();
                salt.Add(modelName);
                salt.Add(_outputs[o]);
                _salts[o] = unchecked((long)salt.Finish());
            }
        }
        finally
        {
            salt.Dispose();
        }

        var rows = new[] { -1 };
        var inputSpecs = new TensorSpec[_inputs.Length];
        for (var j = 0; j < inputSpecs.Length; j++)
        {
            inputSpecs[j] = new TensorSpec(_inputs[j], TensorElementType.Float32, rows);
        }

        var outputSpecs = new TensorSpec[_outputs.Length];
        for (var o = 0; o < outputSpecs.Length; o++)
        {
            outputSpecs[o] = new TensorSpec(_outputs[o], TensorElementType.Float32, rows);
        }

        _signature = new ModelSignature(inputSpecs, outputSpecs);
    }

    public string ClientType => RandomModelClientFactory.Type;

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
                "pass ModelCallOptions.Seed (ModelScorer@1 passes ctx.request.seed), or set \"seed\" in the binding.");
        }

        var batch = call.BatchSize;
        var inputs = call.Inputs.Span;
        var ordered = true; // the call sends the inputs in the hash order, as ModelScorer@1 does: no lookups per row
        for (var j = 0; j < _inputs.Length; j++)
        {
            var slot = Find(inputs, j);
            if (slot < 0)
            {
                return ModelStatus.Failed($"Missing input '{_inputs[j]}'.");
            }

            if (inputs[slot].ElementType != TensorElementType.Float32 || inputs[slot].Float32.Length < batch)
            {
                return ModelStatus.Failed($"Input '{_inputs[j]}' must be float32 with one value per row ({batch}).");
            }

            ordered &= slot == j;
        }

        var hash = new StableHash.Builder();
        try
        {
            for (var o = 0; o < _outputs.Length; o++)
            {
                var scores = writer.GetFloat32(_outputs[o], batch).Span;
                for (var row = 0; row < batch; row++)
                {
                    hash.Reset();
                    hash.Add(_salts[o]);
                    hash.Add(seed);
                    for (var j = 0; j < _inputs.Length; j++)
                    {
                        hash.Add((double)inputs[ordered ? j : Find(inputs, j)].Float32.Span[row]);
                    }

                    scores[row] = (hash.Finish() >> 40) * Unit;
                }
            }
        }
        finally
        {
            hash.Dispose();
        }

        return ModelStatus.Ok;
    }

    /// <summary>The position of input <paramref name="j"/> (in hash order) in the call, or -1.</summary>
    private int Find(ReadOnlySpan<NamedInput> inputs, int j)
    {
        var name = _inputs[j];
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
}

/// <summary>Factory for <c>random@1</c>. <see cref="Create"/> only reads and checks the settings.</summary>
public sealed class RandomModelClientFactory : IModelClientFactory
{
    public const string Type = "random@1";

    public string ClientType => Type;

    /// <exception cref="BindingException">A setting is missing, unknown or invalid: at its pointer, with a hint.</exception>
    public IModelClient Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string[]? inputs = null;
        string[]? outputs = null;
        long? seed = null;
        foreach (var (name, value) in context.Settings)
        {
            switch (name)
            {
                case "inputs":
                    inputs = Names(value, name, "smoothed_ctr\", \"log_followers");
                    break;
                case "outputs":
                    outputs = Names(value, name, "p_click");
                    break;
                case "seed":
                    seed = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var fixedSeed)
                        ? fixedSeed
                        : throw new BindingException("/seed", $"'seed' must be an integer, got {value.GetRawText()}.",
                            "write an int64 such as \"seed\": 42 to give every request the same scores, or remove 'seed' to vary them with ctx.request.seed");
                    break;
                default:
                    throw new BindingException("/" + BindingContext.EscapePointerToken(name), $"Unknown setting '{name}'.",
                        $"{Type} takes 'inputs' and 'outputs' (the names the model declares) and optionally 'seed'");
            }
        }

        return new RandomModelClient(
            context.Name, inputs ?? throw Required("inputs", "smoothed_ctr\", \"log_followers"), outputs ?? throw Required("outputs", "p_click"), seed);
    }

    /// <summary>A list of distinct tensor names, e.g. <c>"inputs": ["smoothed_ctr"]</c>.</summary>
    private static string[] Names(JsonElement value, string setting, string example)
    {
        var pointer = "/" + setting;
        var hint = Hint(setting, example);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new BindingException(pointer, $"'{setting}' must be an array of names, got {value.GetRawText()}.", hint);
        }

        var names = new string[value.GetArrayLength()];
        if (names.Length == 0)
        {
            throw new BindingException(pointer, setting == "inputs"
                ? "'inputs' is empty: the scores derive from the inputs, so every row would score the same."
                : "'outputs' is empty: the model would produce nothing.", hint);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                throw new BindingException(pointer + "/" + index, $"'{setting}' item {index} must be a non-empty name, got {item.GetRawText()}.", hint);
            }

            if (!seen.Add(name))
            {
                throw new BindingException(pointer + "/" + index, $"'{name}' is listed twice in '{setting}'.", "list each name once");
            }

            names[index++] = name;
        }

        return names;
    }

    private static BindingException Required(string setting, string example) =>
        new("/" + setting, $"'{setting}' is required: the {setting[..^1]} names of the model {Type} stands in for.", Hint(setting, example));

    private static string Hint(string setting, string example) =>
        $"list the {setting[..^1]} names the model declares, e.g. \"{setting}\": [\"{example}\"]; each is a float32 tensor of shape [-1], one value per row";
}
