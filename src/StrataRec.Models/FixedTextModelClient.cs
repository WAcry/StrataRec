using System.Text.Json;
using StrataRec.Core.Bindings;

namespace StrataRec.Models;

/// <summary>
/// <c>fixed_text@1</c>: a stand-in for a generative model (an LLM reached through a text-in, text-out client) that answers
/// every call with the same fixed texts. A dev or test environment binds the real model's logical name to it, and the
/// pipelines that call the model (<c>LlmReranker@1</c>) stay unchanged, as with <c>random@1</c> for scoring models
/// (design 6.4 item 1).
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
/// count (<see cref="ModelStatus.OutputTokens"/> null) and no truncation. It is deterministic: the answer depends on
/// neither the prompts nor the seed, so a test chooses the answer it needs. A call allocates nothing; the client is
/// immutable, needs no load and holds nothing to release.</para>
/// </remarks>
public sealed class FixedTextModelClient : IModelClient
{
    private readonly string _modelName;
    private readonly string[] _inputs;
    private readonly (string Name, string Text)[] _outputs;
    private readonly ModelSignature _signature;

    /// <param name="inputs">Distinct, non-empty names.</param>
    /// <param name="outputs">Distinct, non-empty names, each with its fixed text.</param>
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

        _signature = new ModelSignature(inputSpecs, outputSpecs);
    }

    public string ClientType => FixedTextModelClientFactory.Type;

    /// <summary>Answers at once with the signature the settings declare, for any version.</summary>
    public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
        string.Equals(model, _modelName, StringComparison.Ordinal)
            ? new ValueTask<ModelSignature>(_signature)
            : ValueTask.FromException<ModelSignature>(new ArgumentException($"This client serves model '{_modelName}', not '{model}'.", nameof(model)));

    public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(outputs);
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
                        return new ValueTask<ModelStatus>(ModelStatus.Failed($"Input '{name}' must be text."));
                    }

                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return new ValueTask<ModelStatus>(ModelStatus.Failed($"Missing input '{name}'."));
            }
        }

        foreach (var (name, text) in _outputs)
        {
            outputs.SetText(name, text);
        }

        return new ValueTask<ModelStatus>(ModelStatus.Ok);
    }
}

/// <summary>Creates <see cref="FixedTextModelClient"/>s from binding settings.</summary>
public sealed class FixedTextModelClientFactory : IModelClientFactory
{
    public const string Type = "fixed_text@1";

    private const string Example = "\"inputs\": [\"system\", \"user\"], \"outputs\": { \"answer\": \"[3, 1, 2]\" }";

    public string ClientType => Type;

    /// <exception cref="BindingException">A setting is missing, unknown or invalid: at its pointer, with a hint.</exception>
    public IModelClient Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string[]? inputs = null;
        (string Name, string Text)[]? outputs = null;
        foreach (var (name, value) in context.Settings)
        {
            switch (name)
            {
                case "inputs":
                    inputs = Inputs(value);
                    break;
                case "outputs":
                    outputs = Outputs(value);
                    break;
                default:
                    throw new BindingException("/" + BindingContext.EscapePointerToken(name), $"Unknown setting '{name}'.",
                        $"{Type} takes 'inputs' (the model's text input names) and 'outputs' (each text output's fixed answer), e.g. {Example}");
            }
        }

        return new FixedTextModelClient(
            context.Name,
            inputs ?? throw Required("inputs", "the text inputs the model accepts, as its signature declares them"),
            outputs ?? throw Required("outputs", "each text output and the fixed text every call answers"));
    }

    private static BindingException Required(string setting, string what) =>
        new(string.Empty, $"{Type} needs '{setting}': {what}.", $"add it, e.g. {Example}");

    /// <summary><c>"inputs": ["system", "user"]</c>: distinct, non-empty names; at least one.</summary>
    private static string[] Inputs(JsonElement value)
    {
        const string Hint = "list the model's text input names, e.g. \"inputs\": [\"system\", \"user\"]: the prompts the operator renders";
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new BindingException("/inputs", $"'inputs' must be an array of names, got {value.GetRawText()}.", Hint);
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var pointer = "/inputs/" + names.Count;
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                throw new BindingException(pointer, $"'inputs' item {names.Count} must be a non-empty name, got {item.GetRawText()}.", Hint);
            }

            if (!seen.Add(name))
            {
                throw new BindingException(pointer, $"'{name}' is listed twice in 'inputs'.", "list each name once");
            }

            names.Add(name);
        }

        if (names.Count == 0)
        {
            throw new BindingException("/inputs", "'inputs' is empty: a generative model reads at least one prompt.", Hint);
        }

        return names.ToArray();
    }

    /// <summary><c>"outputs": { "answer": "[3, 1, 2]" }</c>: non-empty names, each with a string; at least one.</summary>
    private static (string Name, string Text)[] Outputs(JsonElement value)
    {
        const string Hint = "map each text output to the text every call answers, e.g. \"outputs\": { \"answer\": \"[3, 1, 2]\" }";
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new BindingException("/outputs", $"'outputs' must be an object mapping each output name to its fixed text, got {value.GetRawText()}.", Hint);
        }

        var outputs = new List<(string Name, string Text)>();
        foreach (var property in value.EnumerateObject())
        {
            var pointer = "/outputs/" + BindingContext.EscapePointerToken(property.Name);
            if (property.Name.Length == 0)
            {
                throw new BindingException(pointer, "'outputs' maps an empty name.", Hint);
            }

            // JSON objects may repeat a name; the reader keeps both, so a repeat is refused rather than silently picking one.
            foreach (var (existing, _) in outputs)
            {
                if (string.Equals(existing, property.Name, StringComparison.Ordinal))
                {
                    throw new BindingException(pointer, $"'{property.Name}' is listed twice in 'outputs'.", "list each name once");
                }
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new BindingException(pointer, $"Output '{property.Name}' must be a string (the fixed text), got {property.Value.GetRawText()}.",
                    "write the answer as a JSON string, e.g. \"[3, 1, 2]\" for a list of window ranks");
            }

            outputs.Add((property.Name, property.Value.GetString()!));
        }

        if (outputs.Count == 0)
        {
            throw new BindingException("/outputs", "'outputs' is empty: the model would produce nothing.", Hint);
        }

        return outputs.ToArray();
    }
}
