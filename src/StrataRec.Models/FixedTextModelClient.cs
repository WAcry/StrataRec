using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Services;

namespace StrataRec.Models;

/// <summary>
/// <c>fixed_text@1</c>: a stand-in for a generative model (an LLM reached through a text-in, text-out client) that answers
/// every call with the same fixed texts. A dev or test environment binds the real model's logical name to it, and the
/// pipelines that call the model (<c>LlmReranker@1</c>) stay unchanged, as with <c>random@1</c> for scoring models
///.
/// </summary>
/// <remarks>
/// <para>The settings declare the text inputs it accepts and each output's fixed text:</para>
/// <code>
/// { "client": "fixed_text@1",
///   "inputs": ["system", "user"],              // the model's text inputs (the prompts the operator renders)
///   "outputs": { "answer": "[3, 1, 2]" } }     // each text output and what every call answers
/// </code>
/// <para>Its signature declares every input and output as text (one string, shape <c>[]</c>), for any version, so the
/// calling operator's load-time signature check passes as against the real model. A call fails unless it sends every
/// declared input as text, as a real model's would; it then sets every output to its fixed text and reports no token
/// count (<see cref="ServiceStatus.OutputTokens"/> null) and no truncation. It is deterministic: the answer depends on
/// neither the prompts nor the seed, so a test chooses the answer it needs. A call allocates nothing; the client is
/// immutable, needs no load and holds nothing to release.</para>
/// </remarks>
public sealed class FixedTextModelClient : IServiceClient
{
    private readonly string _modelName;
    private readonly string[] _inputs;
    private readonly (string Name, string Text)[] _outputs;
    private readonly ServiceSignature _signature;

    /// <param name="inputs">Distinct, non-empty names.</param>
    /// <param name="outputs">Distinct, non-empty names.</param>
    internal FixedTextModelClient(string modelName, string[] inputs, (string Name, string Text)[] outputs)
    {
        _modelName = modelName;
        _inputs = (string[])inputs.Clone();
        _outputs = ((string Name, string Text)[])outputs.Clone();
        var inputSpecs = new TensorSpec[_inputs.Length];
        for (var i = 0; i < inputSpecs.Length; i++)
        {
            inputSpecs[i] = new TensorSpec(_inputs[i], TensorElementType.Text, Array.Empty<int>());
        }

        var outputSpecs = new TensorSpec[_outputs.Length];
        for (var o = 0; o < outputSpecs.Length; o++)
        {
            outputSpecs[o] = new TensorSpec(_outputs[o].Name, TensorElementType.Text, Array.Empty<int>());
        }

        _signature = new ServiceSignature(inputSpecs, outputSpecs);
    }

    /// <summary>Answers at once with the signature the settings declare, for any version.</summary>
    public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
        string.Equals(model, _modelName, StringComparison.Ordinal)
            ? new ValueTask<ServiceSignature>(_signature)
            : ValueTask.FromException<ServiceSignature>(new ArgumentException($"This client serves model '{_modelName}', not '{model}'.", nameof(model)));

    public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(response);
        var inputs = call.Inputs.Span;
        foreach (var name in _inputs)
        {
            var found = false;
            foreach (var input in inputs)
            {
                if (string.Equals(input.Name, name, StringComparison.Ordinal))
                {
                    if (input.ElementType != TensorElementType.Text || input.Text is null)
                    {
                        return new ValueTask<ServiceStatus>(ServiceStatus.Failed($"Input '{name}' must be text."));
                    }

                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return new ValueTask<ServiceStatus>(ServiceStatus.Failed($"Missing input '{name}'."));
            }
        }

        foreach (var (name, text) in _outputs)
        {
            response.SetText(name, text);
        }

        return new ValueTask<ServiceStatus>(ServiceStatus.Ok);
    }
}

/// <summary>Settings of <c>fixed_text@1</c>.</summary>
public sealed class FixedTextSettings
{
    /// <summary>
    /// The model's text input names, as its signature declares them (the prompts the calling operator renders), e.g.
    /// <c>["system", "user"]</c>: at least one, each once.
    /// </summary>
    public required List<string> Inputs { get; init; }

    /// <summary>Each text output and the text every call answers, e.g. <c>{ "answer": "[3, 1, 2]" }</c>: at least one.</summary>
    public required Dictionary<string, string> Outputs { get; init; }
}

/// <summary>Creates <see cref="FixedTextModelClient"/>s from binding settings.</summary>
public sealed class FixedTextModelClientFactory : BindingFactory<IServiceClient, FixedTextSettings>
{
    public const string TypeName = "fixed_text@1";

    public override string Type => TypeName;

    protected override IServiceClient Create(FixedTextSettings settings, BindingContext context)
    {
        const string InputHint = "list the model's text input names, e.g. \"inputs\": [\"system\", \"user\"]: the prompts the operator renders";
        const string OutputHint = "map each text output to the text every call answers, e.g. \"outputs\": { \"answer\": \"[3, 1, 2]\" }";
        SettingChecks.Names(settings.Inputs, "inputs", InputHint);
        if (settings.Inputs.Count == 0)
        {
            throw new BindingException("/inputs", "'inputs' is empty: a generative model reads at least one prompt.", InputHint);
        }

        var outputs = new (string Name, string Text)[settings.Outputs.Count];
        var i = 0;
        foreach (var (name, text) in settings.Outputs)
        {
            if (name.Length == 0)
            {
                throw new BindingException("/outputs/", "'outputs' maps an empty name.", OutputHint);
            }

            outputs[i++] = (name, text);
        }

        if (outputs.Length == 0)
        {
            throw new BindingException("/outputs", "'outputs' is empty: the model would produce nothing.", OutputHint);
        }

        return new FixedTextModelClient(context.Name, settings.Inputs.ToArray(), outputs);
    }
}
