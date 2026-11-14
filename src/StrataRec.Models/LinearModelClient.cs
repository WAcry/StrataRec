using System.Globalization;
using System.Text.Json;
using StrataRec.Core.Bindings;

namespace StrataRec.Models;

/// <summary>
/// <c>linear@1</c>: a deterministic in-process model computing, for each output,
/// <c>activation(bias + Σ weight_i · input_i)</c> over float32 <c>[batch]</c> inputs.
/// </summary>
/// <remarks>
/// <para>Intended for local development, tests, replay fixtures and as a cheap fallback model. Its signature declares every
/// input and output float32 <c>[-1]</c>, one value per row: <c>ModelScorer@1</c> can feed it float32, float64 and bool
/// columns, and reports an int or vector column mapped to it at load time (the scorer sends those as int64 <c>[rows]</c>
/// and float32 <c>[rows, N]</c>). A call with another tensor fails. Settings:</para>
/// <code>
/// { "client": "linear@1",
///   "outputs": { "p_click": { "bias": -2.0, "weights": { "smoothed_ctr": 8.0 }, "activation": "sigmoid" } },
///   "versions": ["v12", "v13"],      // optional: accepted versions (any when omitted)
///   "latency_ms": 0,                 // optional: simulated latency
///   "fail": false }                  // optional: always fail (fault injection)
/// </code>
/// </remarks>
public sealed class LinearModelClient : IModelClient
{
    private readonly string _modelName;
    private readonly IReadOnlyList<LinearOutput> _outputs;
    private readonly IReadOnlyList<string> _inputs;
    private readonly HashSet<string>? _versions;
    private readonly int _latencyMs;
    private readonly bool _fail;

    public LinearModelClient(string modelName, IReadOnlyList<LinearOutput> outputs, IEnumerable<string>? versions = null, int latencyMs = 0, bool fail = false)
    {
        _modelName = modelName;
        _outputs = outputs;
        var inputs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            foreach (var name in output.Weights.Keys)
            {
                inputs.Add(name);
            }
        }

        _inputs = new List<string>(inputs);
        _versions = versions is null ? null : new HashSet<string>(versions, StringComparer.Ordinal);
        _latencyMs = latencyMs;
        _fail = fail;
    }

    public string ClientType => LinearModelClientFactory.Type;

    /// <summary>Answers at once: the signature follows from the settings.</summary>
    public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken)
    {
        if (!string.Equals(model, _modelName, StringComparison.Ordinal))
        {
            return ValueTask.FromException<ModelSignature>(new ArgumentException($"This client serves model '{_modelName}', not '{model}'.", nameof(model)));
        }

        if (version is not null && _versions is not null && !_versions.Contains(version))
        {
            return ValueTask.FromException<ModelSignature>(
                new ArgumentException($"Model '{model}' has no version '{version}'. Known: {string.Join(", ", _versions)}.", nameof(version)));
        }

        return new ValueTask<ModelSignature>(Signature());
    }

    private ModelSignature Signature()
    {
        var dyn = new[] { -1 };
        var inputSpecs = new TensorSpec[_inputs.Count];
        for (var i = 0; i < inputSpecs.Length; i++)
        {
            inputSpecs[i] = new TensorSpec(_inputs[i], TensorElementType.Float32, dyn);
        }

        var outputSpecs = new TensorSpec[_outputs.Count];
        for (var i = 0; i < outputSpecs.Length; i++)
        {
            outputSpecs[i] = new TensorSpec(_outputs[i].Name, TensorElementType.Float32, dyn);
        }

        return new ModelSignature(inputSpecs, outputSpecs);
    }

    public async ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
    {
        if (_latencyMs > 0)
        {
            await Task.Delay(_latencyMs, cancellationToken).ConfigureAwait(false);
        }

        if (_fail)
        {
            return ModelStatus.Failed($"Model '{_modelName}' is configured to fail.");
        }

        return Compute(call, outputs);
    }

    private ModelStatus Compute(ModelCall call, IModelOutputWriter outputs)
    {
        var batch = call.BatchSize;
        if (batch < 0)
        {
            return ModelStatus.Failed($"The call's batch size is {batch}; it must not be negative.");
        }

        var inputs = call.Inputs.Span;
        foreach (var output in _outputs)
        {
            var dest = outputs.GetFloat32(output.Name, batch).Span;
            dest.Fill((float)output.Bias);
            foreach (var (name, weight) in output.Weights)
            {
                var input = FindInput(inputs, name);
                if (input is null)
                {
                    return ModelStatus.Failed($"Missing input '{name}'.");
                }

                // [batch] exactly: its data may be a longer buffer, but its shape says how many rows it holds.
                if (input.Value.ElementType != TensorElementType.Float32 || input.Value.Shape is not [var rows] || rows != batch || input.Value.Float32.Length < batch)
                {
                    return ModelStatus.Failed($"Input '{name}' must be float32 with one value per row ({batch}).");
                }

                var data = input.Value.Float32.Span;
                for (var i = 0; i < batch; i++)
                {
                    dest[i] += (float)(weight * data[i]);
                }
            }

            if (output.Sigmoid)
            {
                for (var i = 0; i < batch; i++)
                {
                    dest[i] = 1f / (1f + MathF.Exp(-dest[i]));
                }
            }
        }

        return ModelStatus.Ok;
    }

    private static NamedInput? FindInput(ReadOnlySpan<NamedInput> inputs, string name)
    {
        foreach (var input in inputs)
        {
            if (input.Name == name)
            {
                return input;
            }
        }

        return null;
    }
}

/// <summary>One output of <see cref="LinearModelClient"/>.</summary>
public sealed record LinearOutput(string Name, double Bias, IReadOnlyDictionary<string, double> Weights, bool Sigmoid);

/// <summary>Factory for <c>linear@1</c>.</summary>
public sealed class LinearModelClientFactory : IModelClientFactory
{
    public const string Type = "linear@1";

    public string ClientType => Type;

    private const string Takes = "linear@1 takes 'outputs' (each output's 'bias', 'weights' and 'activation'), and optionally 'versions', 'latency_ms' and 'fail'";
    private const string OutputTakes = "an output takes 'bias' (a number), 'weights' (input name to number) and 'activation' ('sigmoid' or 'identity')";
    private static readonly string[] Settings = ["outputs", "versions", "latency_ms", "fail"];
    private static readonly string[] OutputSettings = ["bias", "weights", "activation"];

    /// <exception cref="BindingException">A setting is missing, unknown or invalid: at its pointer, with a hint.</exception>
    public IModelClient Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<LinearOutput>? outputs = null;
        List<string>? versions = null;
        var latency = 0;
        var fail = false;
        foreach (var (name, value) in context.Settings)
        {
            switch (name)
            {
                case "outputs":
                    outputs = Outputs(value);
                    break;
                case "versions":
                    versions = Versions(value);
                    break;
                case "latency_ms":
                    latency = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var ms) && ms >= 0
                        ? ms
                        : throw new BindingException("/latency_ms", $"'latency_ms' must be a whole number of milliseconds, 0 or more; got {value.GetRawText()}.");
                    break;
                case "fail":
                    fail = value.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? value.GetBoolean()
                        : throw new BindingException("/fail", $"'fail' must be true or false; got {value.GetRawText()}.");
                    break;
                default:
                    throw BindingException.Unknown(string.Empty, name, Settings, Takes);
            }
        }

        if (outputs is null)
        {
            throw new BindingException(string.Empty, "linear@1 needs 'outputs': the model's outputs, each a weighted sum of its inputs.",
                "add it, e.g. \"outputs\": { \"p_click\": { \"bias\": -2.0, \"weights\": { \"smoothed_ctr\": 8.0 }, \"activation\": \"sigmoid\" } }");
        }

        return new LinearModelClient(context.Name, outputs, versions, latency, fail);
    }

    private static List<LinearOutput> Outputs(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new BindingException("/outputs", $"'outputs' must be an object of output name to its bias, weights and activation; got {value.GetRawText()}.");
        }

        var outputs = new List<LinearOutput>();
        foreach (var output in value.EnumerateObject())
        {
            var pointer = "/outputs/" + BindingContext.EscapePointerToken(output.Name);
            if (output.Value.ValueKind != JsonValueKind.Object)
            {
                throw new BindingException(pointer, $"Output '{output.Name}' must be an object; got {output.Value.GetRawText()}.", OutputTakes);
            }

            var bias = 0.0;
            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            var sigmoid = false;
            foreach (var member in output.Value.EnumerateObject())
            {
                var at = pointer + "/" + BindingContext.EscapePointerToken(member.Name);
                switch (member.Name)
                {
                    case "bias":
                        bias = Number(member.Value, at, "'bias'");
                        break;
                    case "weights":
                        if (member.Value.ValueKind != JsonValueKind.Object)
                        {
                            throw new BindingException(at, $"'weights' must be an object of input name to weight; got {member.Value.GetRawText()}.");
                        }

                        foreach (var weight in member.Value.EnumerateObject())
                        {
                            weights[weight.Name] = Number(weight.Value, at + "/" + BindingContext.EscapePointerToken(weight.Name), $"The weight of '{weight.Name}'");
                        }

                        break;
                    case "activation":
                        sigmoid = member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString() switch
                        {
                            "sigmoid" => true,
                            "identity" => false,
                            _ => throw new BindingException(at, $"Unknown activation '{member.Value.GetString()}'.", "use 'sigmoid' or 'identity'"),
                        }
                        : throw new BindingException(at, $"'activation' must be 'sigmoid' or 'identity'; got {member.Value.GetRawText()}.");
                        break;
                    default:
                        throw BindingException.Unknown(pointer, member.Name, OutputSettings, OutputTakes);
                }
            }

            outputs.Add(new LinearOutput(output.Name, bias, weights, sigmoid));
        }

        return outputs;
    }

    private static List<string> Versions(JsonElement value)
    {
        var versions = new List<string>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                versions.Add(item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } version
                    ? version
                    : throw new BindingException("/versions/" + versions.Count, $"A version must be a non-empty string; got {item.GetRawText()}."));
            }

            return versions;
        }

        throw new BindingException("/versions", $"'versions' must be an array of the versions the model accepts; got {value.GetRawText()}.",
            "list them, e.g. \"versions\": [\"v12\", \"v13\"], or remove it to accept any");
    }

    private static double Number(JsonElement value, string pointer, string what) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw new BindingException(pointer, $"{what} must be a finite number; got {value.GetRawText()}.");

    public override string ToString() => Type.ToString(CultureInfo.InvariantCulture);
}
