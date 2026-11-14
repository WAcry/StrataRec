using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Models;

namespace StrataRec.Samples.Host.Components;

/// <summary>
/// <c>logistic@1</c>: a tiny logistic regression, <c>p = sigmoid(bias + Σ weight_i · input_i)</c>, standing in for a
/// model server client. Its inputs are the names of its weights, each float32 <c>[batch]</c>; its one output is float32
/// <c>[batch]</c>.
/// </summary>
/// <remarks>
/// <para>Binding settings: <c>version</c> (required; the one version this binding serves), <c>weights</c> (required,
/// input name → number), <c>bias</c> (default 0) and <c>output</c> (default <c>p</c>).</para>
/// <para>The rules of the contract, and how this client keeps them:</para>
/// <list type="bullet">
/// <item>The signature is the training-serving contract (design 6.9): the engine checks every node calling the model
/// against it before the plan serves. It follows from the settings here, so it is built once and returned at once; that
/// also lets <c>validate</c>, which loads nothing, check the nodes. A client that asks a model server implements
/// <see cref="ILoadable"/> and answers after its load.</item>
/// <item>The client never sees a missing value: model inputs are non-nullable columns, so what a missing feature means
/// is decided in the catalog (a <c>default</c>, or a derived feature with <c>??</c>), the same for training and serving.</item>
/// <item>A call writes into the caller's output buffers and allocates nothing. A wrong input is a failed status with a
/// reason, not an exception: the node then applies its <c>on_failure</c> (here, a fallback scorer).</item>
/// <item>Cancellation: the computation takes microseconds and runs synchronously; a call whose token is already cancelled
/// does nothing. A remote client passes the token to its I/O; the engine stops waiting at the model's timeout or the
/// node's budget either way.</item>
/// </list>
/// </remarks>
public sealed class LogisticModelClient : IModelClient
{
    private readonly string _model;
    private readonly string _version;
    private readonly string _output;
    private readonly double _bias;
    private readonly string[] _inputs; // ordinal order
    private readonly double[] _weights;
    private readonly ModelSignature _signature;

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

        _signature = new ModelSignature(specs, new[] { new TensorSpec(output, TensorElementType.Float32, batch) });
    }

    public string ClientType => LogisticModelClientFactory.Type;

    public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken)
    {
        if (model != _model)
        {
            return ValueTask.FromException<ModelSignature>(new ArgumentException($"This client serves model '{_model}', not '{model}'.", nameof(model)));
        }

        if (version is not null && version != _version)
        {
            return ValueTask.FromException<ModelSignature>(new BindingException("/version", $"Model '{model}' has no version '{version}'; this binding serves '{_version}'.",
                $"call version '{_version}', or bind a client that serves '{version}'"));
        }

        return new ValueTask<ModelSignature>(_signature);
    }

    public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ModelStatus>(cancellationToken);
        }

        if (call.Version is { } version && version != _version)
        {
            return new ValueTask<ModelStatus>(ModelStatus.Failed($"Model '{_model}' has no version '{version}'."));
        }

        var batch = call.BatchSize;
        var logits = outputs.GetFloat32(_output, batch).Span[..batch];
        logits.Fill((float)_bias);
        var inputs = call.Inputs.Span;
        for (var w = 0; w < _inputs.Length; w++)
        {
            var found = false;
            foreach (var input in inputs)
            {
                if (input.Name != _inputs[w])
                {
                    continue;
                }

                // [batch] exactly: its data may be a longer pooled buffer, but its shape says how many rows it holds.
                if (input.ElementType != TensorElementType.Float32 || input.Shape is not [var rows] || rows != batch || input.Float32.Length < batch)
                {
                    return new ValueTask<ModelStatus>(ModelStatus.Failed($"Input '{input.Name}' must be float32 [{batch}]."));
                }

                var values = input.Float32.Span;
                var weight = (float)_weights[w];
                for (var r = 0; r < batch; r++)
                {
                    logits[r] += weight * values[r];
                }

                found = true;
                break;
            }

            if (!found)
            {
                return new ValueTask<ModelStatus>(ModelStatus.Failed($"Missing input '{_inputs[w]}'."));
            }
        }

        for (var r = 0; r < batch; r++)
        {
            logits[r] = 1f / (1f + MathF.Exp(-logits[r]));
        }

        return new ValueTask<ModelStatus>(ModelStatus.Ok);
    }
}

/// <summary>Creates <c>logistic@1</c> clients; registered in <see cref="SampleEnvironment"/>.</summary>
public sealed class LogisticModelClientFactory : IModelClientFactory
{
    public const string Type = "logistic@1";

    public string ClientType => Type;

    /// <summary>Reads and checks the settings; a wrong one is a <see cref="BindingException"/> at its pointer, with a hint.</summary>
    public IModelClient Create(BindingContext context)
    {
        string? version = null;
        string output = "p";
        double bias = 0;
        var weights = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in context.Settings)
        {
            switch (name)
            {
                case "version":
                    version = NonEmptyString(value, "/version", "the model version this binding serves, e.g. \"v3\"");
                    break;
                case "output":
                    output = NonEmptyString(value, "/output", "the name of the model's output, e.g. \"p_engage\"");
                    break;
                case "bias":
                    bias = Finite(value, "/bias");
                    break;
                case "weights":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        throw new BindingException("/weights", "'weights' must be an object: input name → weight.", "e.g. { \"ctr\": 20.0, \"freshness\": 1.0 }");
                    }

                    foreach (var weight in value.EnumerateObject())
                    {
                        weights[weight.Name] = Finite(weight.Value, "/weights/" + BindingContext.EscapePointerToken(weight.Name));
                    }

                    break;
                default:
                    throw new BindingException("/" + BindingContext.EscapePointerToken(name), $"logistic@1 has no setting '{name}'.",
                        "its settings are version, weights, bias and output");
            }
        }

        if (version is null)
        {
            throw new BindingException("/version", "'version' is required.", "give the model version this binding serves, e.g. \"v3\"");
        }

        if (weights.Count == 0)
        {
            throw new BindingException("/weights", "'weights' must name at least one input.", "e.g. { \"ctr\": 20.0, \"freshness\": 1.0 }");
        }

        var inputs = new string[weights.Count];
        var values = new double[weights.Count];
        var i = 0;
        foreach (var (name, weight) in weights)
        {
            inputs[i] = name;
            values[i++] = weight;
        }

        return new LogisticModelClient(context.Name, version, output, bias, inputs, values);
    }

    private static string NonEmptyString(JsonElement value, string pointer, string what) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new BindingException(pointer, $"'{pointer[1..]}' must be a non-empty string.", "give " + what);

    private static double Finite(JsonElement value, string pointer) =>
        value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble())
            ? value.GetDouble()
            : throw new BindingException(pointer, $"'{pointer}' must be a finite number.");
}
