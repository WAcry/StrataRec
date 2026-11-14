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

                if (input.Value.ElementType != TensorElementType.Float32 || input.Value.Shape is not { Length: 1 } || input.Value.Float32.Length < batch)
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

    public IModelClient Create(BindingContext context)
    {
        var modelName = context.Name;
        var settings = context.Settings;
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

        List<string>? versions = null;
        if (settings.TryGetValue("versions", out var v))
        {
            versions = new List<string>();
            foreach (var e in v.EnumerateArray())
            {
                versions.Add(e.GetString()!);
            }
        }
        var latency = settings.TryGetValue("latency_ms", out var l) ? l.GetInt32() : 0;
        var fail = settings.TryGetValue("fail", out var f) && f.GetBoolean();
        return new LinearModelClient(modelName, outputs, versions, latency, fail);
    }

    public override string ToString() => Type.ToString(CultureInfo.InvariantCulture);
}
