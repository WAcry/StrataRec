using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Services;

namespace StrataRec.Samples.Host.Components;

/// <summary>
/// <c>logistic@1</c>: a tiny logistic regression, <c>p = sigmoid(bias + Σ weight_i · input_i)</c>, standing in for a
/// model server client. Its inputs are the names of its weights, each float32 <c>[batch]</c>; its one output is float32
/// <c>[batch]</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The signature is the training-serving contract, checked against every node calling the model before the plan
/// serves. It follows from the settings, so it is returned at once; a client asking a model server implements
/// <see cref="ILoadable"/> and answers after its load.</item>
/// <item>Model inputs are non-nullable: what a missing feature means is decided in the catalog, the same for training
/// and serving.</item>
/// <item>A call writes into the caller's buffers and, up to 64 inputs, allocates nothing. A wrong input is a failed
/// status with a reason, not an exception: the node then applies its <c>on_failure</c>.</item>
/// <item>A remote client passes the token to its I/O; the engine stops waiting at the timeout either way.</item>
/// </list>
/// </remarks>
public sealed class LogisticModelClient : IServiceClient
{
    private readonly string _model;
    private readonly string _version;
    private readonly string _output;
    private readonly double _bias;
    private readonly string[] _inputs; // ordinal order
    private readonly double[] _weights;
    private readonly ServiceSignature _signature;

    public LogisticModelClient(string model, string version, string output, double bias, string[] inputs, double[] weights)
    {
        _model = model;
        _version = version;
        _output = output;
        _bias = bias;
        _inputs = inputs;
        _weights = weights;
        var batch = new[] { -1 };
        var specs = new TensorSpec[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            specs[i] = new TensorSpec(inputs[i], TensorElementType.Float32, batch);
        }

        _signature = new ServiceSignature(specs, new[] { new TensorSpec(output, TensorElementType.Float32, batch) });
    }

    public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken)
    {
        if (model != _model)
        {
            return ValueTask.FromException<ServiceSignature>(new ArgumentException($"This client serves model '{_model}', not '{model}'.", nameof(model)));
        }

        if (version is not null && version != _version)
        {
            return ValueTask.FromException<ServiceSignature>(new BindingException("/version", $"Model '{model}' has no version '{version}'; this binding serves '{_version}'.",
                $"call version '{_version}', or bind a client that serves '{version}'"));
        }

        return new ValueTask<ServiceSignature>(_signature);
    }

    public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ServiceStatus>(cancellationToken);
        }

        if (call.Version is { } version && version != _version)
        {
            return new ValueTask<ServiceStatus>(ServiceStatus.Failed($"Model '{_model}' has no version '{version}'."));
        }

        // Each weight's input, checked once: [batch] exactly; its data may be a longer pooled buffer, but its shape
        // says how many rows it holds.
        var batch = call.BatchSize;
        var inputs = call.Inputs.Span;
        Span<int> inputOf = _inputs.Length <= 64 ? stackalloc int[_inputs.Length] : new int[_inputs.Length];
        for (var w = 0; w < _inputs.Length; w++)
        {
            inputOf[w] = -1;
            for (var i = 0; i < inputs.Length; i++)
            {
                if (inputs[i].Name == _inputs[w])
                {
                    inputOf[w] = i;
                    break;
                }
            }

            if (inputOf[w] < 0)
            {
                return new ValueTask<ServiceStatus>(ServiceStatus.Failed($"Missing input '{_inputs[w]}'."));
            }

            var input = inputs[inputOf[w]];
            if (input.ElementType != TensorElementType.Float32 || input.Shape is not [var rows] || rows != batch || input.Float32.Length < batch)
            {
                return new ValueTask<ServiceStatus>(ServiceStatus.Failed($"Input '{input.Name}' must be float32 [{batch}]."));
            }
        }

        // In double, as the coefficients were checked: in float a large weight and bias that cancel (1e40 and -1e40)
        // would overflow to infinities and give NaN.
        var probabilities = response.GetValues<float>(_output, batch).Span[..batch];
        for (var r = 0; r < batch; r++)
        {
            var logit = _bias;
            for (var w = 0; w < _weights.Length; w++)
            {
                logit += _weights[w] * inputs[inputOf[w]].Float32.Span[r];
            }

            if (double.IsNaN(logit))
            {
                return new ValueTask<ServiceStatus>(ServiceStatus.Failed($"The logit of row {r} is not a number: the weights overflow."));
            }

            probabilities[r] = (float)(1.0 / (1.0 + Math.Exp(-logit)));
        }

        return new ValueTask<ServiceStatus>(ServiceStatus.Ok);
    }
}

/// <summary>
/// The settings of <c>logistic@1</c>: the members of a binding besides <c>client</c> and <c>timeout_ms</c>. The engine binds
/// them strictly (an unknown, misspelled or missing member, or one of the wrong kind, is an error at its pointer), and
/// the schema editors check bindings with is generated from this type, its documentation included.
/// </summary>
public sealed class LogisticSettings
{
    /// <summary>The one model version this binding serves, e.g. <c>v3</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Input name to its weight, e.g. <c>{ "ctr": 20.0, "freshness": 1.0 }</c>: at least one. The model's inputs are these names.</summary>
    public required Dictionary<string, double> Weights { get; init; }

    /// <summary>Added to the weighted sum (default 0).</summary>
    public double Bias { get; init; }

    /// <summary>The name of the model's one output (default <c>p</c>).</summary>
    public string Output { get; init; } = "p";
}

/// <summary>Creates <c>logistic@1</c> clients; registered in <see cref="SampleEnvironment"/>.</summary>
public sealed class LogisticModelClientFactory : BindingFactory<IServiceClient, LogisticSettings>
{
    public const string TypeName = "logistic@1";

    public override string Type => TypeName;

    /// <summary>Checks what the settings type cannot say; a wrong setting is a <see cref="BindingException"/> at its pointer, with a hint.</summary>
    protected override IServiceClient Create(LogisticSettings settings, BindingContext context)
    {
        if (settings.Version.Length == 0)
        {
            throw new BindingException("/version", "'version' must not be empty.", "give the model version this binding serves, e.g. \"v3\"");
        }

        if (settings.Output.Length == 0)
        {
            throw new BindingException("/output", "'output' must not be empty.", "give the name of the model's output, e.g. \"p_engage\"");
        }

        if (settings.Weights.Count == 0)
        {
            throw new BindingException("/weights", "'weights' must name at least one input.", "e.g. { \"ctr\": 20.0, \"freshness\": 1.0 }");
        }

        var weights = new SortedDictionary<string, double>(settings.Weights, StringComparer.Ordinal);
        var inputs = new string[weights.Count];
        var values = new double[weights.Count];
        var i = 0;
        foreach (var (name, weight) in weights)
        {
            inputs[i] = name;
            values[i++] = weight;
        }

        return new LogisticModelClient(context.Name, settings.Version, settings.Output, settings.Bias, inputs, values);
    }
}
