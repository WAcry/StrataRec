using System.Globalization;
using System.Text.Json;

namespace StrataRec.Models;

/// <summary>
/// <c>linear@1</c>: a deterministic in-process model computing, for each output,
/// <c>activation(bias + Σ weight_i · input_i)</c> over float32 <c>[batch]</c> inputs.
/// </summary>
/// <remarks>
/// <para>Intended for local development, tests, replay fixtures and as a cheap fallback model. Settings:</para>
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
        _inputs = outputs.SelectMany(o => o.Weights.Keys).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        _versions = versions is null ? null : new HashSet<string>(versions, StringComparer.Ordinal);
        _latencyMs = latencyMs;
        _fail = fail;
    }

    public string ClientType => LinearModelClientFactory.Type;

    public ModelSignature GetSignature(string model, string? version)
    {
        if (!string.Equals(model, _modelName, StringComparison.Ordinal))
        {
            throw new ArgumentException($"This client serves model '{_modelName}', not '{model}'.", nameof(model));
        }

        if (version is not null && _versions is not null && !_versions.Contains(version))
        {
            throw new ArgumentException($"Model '{model}' has no version '{version}'. Known: {string.Join(", ", _versions)}.", nameof(version));
        }

        var dyn = new[] { -1 };
        return new ModelSignature(
            _inputs.Select(i => new TensorSpec(i, TensorElementType.Float32, dyn)).ToArray(),
            _outputs.Select(o => new TensorSpec(o.Name, TensorElementType.Float32, dyn)).ToArray());
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

    public IModelClient Create(string modelName, IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (!settings.TryGetValue("outputs", out var outputsElement) || outputsElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"Model '{modelName}': linear@1 requires an 'outputs' object.");
        }

        var outputs = new List<LinearOutput>();
        foreach (var output in outputsElement.EnumerateObject())
        {
            var bias = output.Value.TryGetProperty("bias", out var b) ? b.GetDouble() : 0.0;
            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            if (output.Value.TryGetProperty("weights", out var w))
            {
                foreach (var p in w.EnumerateObject())
                {
                    weights[p.Name] = p.Value.GetDouble();
                }
            }

            var activation = output.Value.TryGetProperty("activation", out var a) ? a.GetString() : "identity";
            if (activation is not ("sigmoid" or "identity"))
            {
                throw new FormatException($"Model '{modelName}': unknown activation '{activation}' (use 'sigmoid' or 'identity').");
            }

            outputs.Add(new LinearOutput(output.Name, bias, weights, activation == "sigmoid"));
        }

        IEnumerable<string>? versions = settings.TryGetValue("versions", out var v)
            ? v.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : null;
        var latency = settings.TryGetValue("latency_ms", out var l) ? l.GetInt32() : 0;
        var fail = settings.TryGetValue("fail", out var f) && f.GetBoolean();
        return new LinearModelClient(modelName, outputs, versions, latency, fail);
    }

    public override string ToString() => Type.ToString(CultureInfo.InvariantCulture);
}
