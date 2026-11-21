using System.ComponentModel.DataAnnotations;
using System.Globalization;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Services;

namespace StrataRec.Models;

/// <summary>
/// <c>linear@1</c>: a deterministic in-process model computing, for each output,
/// <c>activation(bias + Σ weight_i · input_i)</c> over float32 <c>[batch]</c> inputs.
/// </summary>
/// <remarks>
/// <para>Intended for local development, tests, replay fixtures and as a cheap fallback model. Its signature declares every
/// input and output float32 <c>[-1]</c>, one value per row: <c>ModelScorer@1</c> sends every column as its declared type,
/// so it feeds float32 columns and writes float32 ones, and reports a column of another type mapped to it at load time
/// (a float64, bool or int one weighs in through a float32 derived feature, e.g. <c>is_new ? 1.0 : 0.0</c>). A call with
/// another tensor fails. Settings:</para>
/// <code>
/// { "client": "linear@1",
///   "outputs": { "p_click": { "bias": -2.0, "weights": { "smoothed_ctr": 8.0 }, "activation": "sigmoid" } },
///   "versions": ["v12", "v13"],      // optional: accepted versions (any when omitted)
///   "latency_ms": 0,                 // optional: simulated latency
///   "fail": false }                  // optional: always fail (fault injection)
/// </code>
/// </remarks>
public sealed class LinearModelClient : IServiceClient
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

    /// <summary>Answers at once: the signature follows from the settings.</summary>
    public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken)
    {
        if (!string.Equals(model, _modelName, StringComparison.Ordinal))
        {
            return ValueTask.FromException<ServiceSignature>(new ArgumentException($"This client serves model '{_modelName}', not '{model}'.", nameof(model)));
        }

        if (version is not null && _versions is not null && !_versions.Contains(version))
        {
            return ValueTask.FromException<ServiceSignature>(
                new ArgumentException($"Model '{model}' has no version '{version}'. Known: {string.Join(", ", _versions)}.", nameof(version)));
        }

        return new ValueTask<ServiceSignature>(Signature());
    }

    private ServiceSignature Signature()
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

        return new ServiceSignature(inputSpecs, outputSpecs);
    }

    public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
    {
        if (_latencyMs > 0)
        {
            await Task.Delay(_latencyMs, cancellationToken).ConfigureAwait(false);
        }

        if (_fail)
        {
            return ServiceStatus.Failed($"Model '{_modelName}' is configured to fail.");
        }

        return Compute(call, response);
    }

    private ServiceStatus Compute(ServiceCall call, IServiceResponse outputs)
    {
        var batch = call.BatchSize;
        if (batch < 0)
        {
            return ServiceStatus.Failed($"The call's batch size is {batch}; it must not be negative.");
        }

        var inputs = call.Inputs.Span;
        foreach (var output in _outputs)
        {
            var dest = outputs.GetValues<float>(output.Name, batch).Span;
            dest.Fill((float)output.Bias);
            foreach (var (name, weight) in output.Weights)
            {
                var input = FindInput(inputs, name);
                if (input is null)
                {
                    return ServiceStatus.Failed($"Missing input '{name}'.");
                }

                // [batch] exactly: its data may be a longer buffer, but its shape says how many rows it holds.
                if (input.Value.ElementType != TensorElementType.Float32 || input.Value.Shape is not [var rows] || rows != batch || input.Value.Float32.Length < batch)
                {
                    return ServiceStatus.Failed($"Input '{name}' must be float32 with one value per row ({batch}).");
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

        return ServiceStatus.Ok;
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

/// <summary>Settings of <c>linear@1</c>.</summary>
public sealed class LinearSettings
{
    /// <summary>
    /// The model's outputs by name, each a weighted sum of its inputs, e.g.
    /// <c>{ "p_click": { "bias": -2.0, "weights": { "smoothed_ctr": 8.0 }, "activation": "sigmoid" } }</c>.
    /// </summary>
    public required Dictionary<string, LinearOutputSettings> Outputs { get; init; }

    /// <summary>The model versions a call may ask for, e.g. <c>["v12", "v13"]</c>; any version when omitted.</summary>
    public List<string>? Versions { get; init; }

    /// <summary>Simulated latency of every call, in milliseconds (default 0).</summary>
    public int LatencyMs { get; init; }

    /// <summary>Fail every call (fault injection).</summary>
    public bool Fail { get; init; }
}

/// <summary>One output of <c>linear@1</c>: <c>activation(bias + Σ weight · input)</c>.</summary>
public sealed class LinearOutputSettings
{
    /// <summary>Added to the weighted sum (default 0).</summary>
    public double Bias { get; init; }

    /// <summary>Input name to its weight. The model's inputs are the names the outputs weigh.</summary>
    public Dictionary<string, double> Weights { get; init; } = new();

    /// <summary><c>sigmoid</c>, or <c>identity</c> (the default).</summary>
    [AllowedValues("sigmoid", "identity")]
    public string Activation { get; init; } = "identity";
}

/// <summary>Factory for <c>linear@1</c>.</summary>
public sealed class LinearModelClientFactory : BindingFactory<IServiceClient, LinearSettings>
{
    public const string TypeName = "linear@1";

    public override string Type => TypeName;

    protected override IServiceClient Create(LinearSettings settings, BindingContext context)
    {
        var outputs = new List<LinearOutput>(settings.Outputs.Count);
        foreach (var (name, output) in settings.Outputs)
        {
            var sigmoid = output.Activation switch
            {
                "sigmoid" => true,
                "identity" => false,
                _ => throw new BindingException("/outputs/" + BindingContext.EscapePointerToken(name) + "/activation",
                    $"Unknown activation '{output.Activation}'.", "use 'sigmoid' or 'identity'"),
            };
            outputs.Add(new LinearOutput(name, output.Bias, output.Weights, sigmoid));
        }

        if (settings.Versions is { } versions)
        {
            for (var i = 0; i < versions.Count; i++)
            {
                if (versions[i].Length == 0)
                {
                    throw new BindingException("/versions/" + i, "A version must be a non-empty string.",
                        "list the versions a call may ask for, e.g. \"versions\": [\"v12\", \"v13\"], or remove 'versions' to accept any");
                }
            }
        }

        SettingChecks.Latency(settings.LatencyMs);
        return new LinearModelClient(context.Name, outputs, settings.Versions, settings.LatencyMs, settings.Fail);
    }

    public override string ToString() => Type.ToString(CultureInfo.InvariantCulture);
}
