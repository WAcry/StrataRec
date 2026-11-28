using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Diagnostics;
using StrataRec.Expressions;
using StrataRec.Pipeline.Planning;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>
/// The config keys every model operator shares: which model, which of its versions, and the columns and context values
/// that feed it. Ordered first in each operator's JSON Schema, before the operator's own keys.
/// </summary>
public abstract class ModelCallConfig
{
    /// <summary>Logical model name, bound per environment.</summary>
    [JsonPropertyOrder(-1)]
    public required string Model { get; init; }

    /// <summary>Fixed model version; or use <see cref="VersionParam"/>.</summary>
    [JsonPropertyOrder(-1)]
    public string? Version { get; init; }

    /// <summary>String parameter (with an <c>enum</c> of allowed versions) selecting the version per request.</summary>
    [JsonPropertyOrder(-1)]
    public string? VersionParam { get; init; }

    /// <summary>
    /// Model input name → column or context reference (<c>col.*</c> / <c>ctx.*</c>) of a non-nullable type, a list only
    /// of a fixed length (<c>list&lt;E,N&gt;</c>); its declared type is the tensor's.
    /// </summary>
    [JsonPropertyOrder(-1)]
    public required Dictionary<string, string> Inputs { get; init; }
}

/// <summary>
/// What every model operator compiles, in the order they report it: binding and versions (<see cref="Begin"/>), inputs
/// (<see cref="CompileInputs"/>), signatures (<see cref="Finish"/>). Each operator checks its own keys between the
/// steps.
/// </summary>
internal sealed class ModelCallCompiler
{
    private readonly OperatorCompileContext _context;
    private readonly ModelCallConfig _config;
    private readonly TensorLayout _layout;
    private readonly BoundModel _model;
    private ModelInput[] _inputs = Array.Empty<ModelInput>();
    private ModelTensorRule.MappedInput[] _mapped = Array.Empty<ModelTensorRule.MappedInput>();

    private ModelCallCompiler(OperatorCompileContext context, ModelCallConfig config, TensorLayout layout, BoundModel model)
    {
        _context = context;
        _config = config;
        _layout = layout;
        _model = model;
    }

    public static ModelCallCompiler? Begin(OperatorCompileContext context, ModelCallConfig config, TensorLayout layout)
    {
        var model = BoundModel.Resolve(context, config.Model, config.Version, config.VersionParam);
        return model is null ? null : new ModelCallCompiler(context, config, layout, model);
    }

    public bool CompileInputs()
    {
        var names = new List<string>(_config.Inputs.Keys);
        names.Sort(StringComparer.Ordinal);
        _inputs = new ModelInput[names.Count];
        _mapped = new ModelTensorRule.MappedInput[names.Count];
        var ok = true;
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var source = _config.Inputs[name];
            var pointer = "/inputs/" + JsonPointer.Escape(name);

            // Any type, nullable included: ModelTensorRule decides which types an input may have, and says why in model terms.
            var expr = _context.CompileExpression(source, pointer, CompileOptions.Any);
            if (expr is null)
            {
                ok = false;
                continue;
            }

            if (expr.Reference is null)
            {
                _context.Error(pointer, "MODEL_INPUT_NOT_REFERENCE", $"Model input '{name}' must be a plain column or context reference, not an expression.",
                    "define the transformation as a derived feature so training and serving share one definition");
                ok = false;
                continue;
            }

            var symbol = expr.Reads[0];
            var declared = _context.Schema.DeclaredType(expr);
            if (!ModelTensorRule.TryGetInputTensor(declared, out var tensor))
            {
                var problem = ModelTensorRule.UnsendableInput(name, symbol.Name, declared);
                _context.Error(problem.ConfigKey, problem.Code, problem.Message, problem.Hint);
                ok = false;
                continue;
            }

            _inputs[i] = new ModelInput(name, expr, symbol, declared, tensor);
            _mapped[i] = new ModelTensorRule.MappedInput(name, symbol.Name, declared, _inputs[i].Broadcast);
        }

        return ok;
    }

    /// <summary>
    /// Checks the signature of every version against the inputs and <paramref name="outputs"/>, after the operator
    /// compiled its own keys without errors.
    /// </summary>
    public CompiledModelCall? Finish(IReadOnlyList<ModelTensorRule.MappedOutput> outputs)
    {
        var ok = true;
        foreach (var version in _model.Versions)
        {
            var lookup = _model.Signature(_context, version);
            if (!lookup.IsKnown)
            {
                // A failure is reported at /model; a pending or unchecked signature is checked when the engine loads it.
                ok &= lookup.Status != SignatureStatus.Failed;
                continue;
            }

            foreach (var problem in ModelTensorRule.Check(_model.Describe(version), _layout, _mapped, outputs, lookup.Signature))
            {
                _context.Error(problem.ConfigKey, problem.Code, problem.Message, problem.Hint);
                ok = false;
            }
        }

        if (!ok)
        {
            return null;
        }

        var names = new string[outputs.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = outputs[i].Name;
        }

        return new CompiledModelCall(_model, _inputs, names);
    }
}

/// <summary>
/// Immutable and shared by every request of the node.
/// </summary>
internal sealed class CompiledModelCall
{
    private readonly BoundModel _model;
    private readonly string[] _outputs;

    public CompiledModelCall(BoundModel model, ModelInput[] inputs, string[] outputs)
    {
        _model = model;
        _outputs = outputs;
        Inputs = inputs;
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            named[input.Name] = input.Source;
        }

        NamedInputs = named;
    }

    public string Model => _model.Model;

    /// <summary>In ordinal order of their names.</summary>
    public ModelInput[] Inputs { get; }

    /// <summary>Model input name → source, for log taps' <c>columns_from</c>.</summary>
    public IReadOnlyDictionary<string, string> NamedInputs { get; }

    public string? Version(OperatorContext context) => _model.Version(context);

    public Task<ServiceStatus> InvokeAsync(
        OperatorContext context, string? version, NamedInput[] inputs, int batchSize, ServiceResponse response, ReadOnlySpan<int> rows) =>
        _model.InvokeAsync(context, version, inputs, batchSize, response, rows, _outputs);
}
