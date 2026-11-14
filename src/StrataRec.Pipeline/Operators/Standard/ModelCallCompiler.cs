using StrataRec.Artifacts.Json;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>
/// The config keys every model operator shares (design 6.9): which model, which of its versions, and the columns and
/// context values that feed it. One definition, so <c>ModelScorer@1</c>, <c>PairwiseRanker@1</c> and
/// <c>ListwiseReranker@1</c> accept them with the same meaning, codes and messages.
/// </summary>
public abstract class ModelCallConfig
{
    /// <summary>Logical model name, bound per environment.</summary>
    public required string Model { get; init; }

    /// <summary>Fixed model version; or use <see cref="VersionParam"/>.</summary>
    public string? Version { get; init; }

    /// <summary>String parameter (with an <c>enum</c> of allowed versions) selecting the version per request.</summary>
    public string? VersionParam { get; init; }

    /// <summary>
    /// Model input name → column or context reference (<c>col.*</c> / <c>ctx.*</c>) of a non-nullable float32, float64,
    /// bool, int32, int64 or <c>vec&lt;float32,N&gt;</c> type; the type decides the tensor (<see cref="ModelTensorRule"/>).
    /// </summary>
    public required Dictionary<string, string> Inputs { get; init; }
}

/// <summary>
/// Compiles what every model operator shares, in the order the operators report it: the model's binding and the
/// versions a request may use (<see cref="Begin"/>), the inputs (<see cref="CompileInputs"/>), and the load-time check of
/// every version's signature against the tensors the operator's layout builds (<see cref="Finish"/>). Between those
/// steps each operator checks its own keys, so a config with errors reports them as before.
/// </summary>
internal sealed class ModelCallCompiler
{
    private readonly OperatorCompileContext _context;
    private readonly ModelCallConfig _config;
    private readonly TensorLayout _layout;
    private readonly IModelClient _client;
    private readonly int? _timeoutMs;
    private readonly List<string?> _versions;
    private readonly ParamRef? _versionParam;
    private ModelInput[] _inputs = Array.Empty<ModelInput>();
    private ModelTensorRule.MappedInput[] _mapped = Array.Empty<ModelTensorRule.MappedInput>();

    private ModelCallCompiler(
        OperatorCompileContext context, ModelCallConfig config, TensorLayout layout, IModelClient client, int? timeoutMs, List<string?> versions, ParamRef? versionParam)
    {
        _context = context;
        _config = config;
        _layout = layout;
        _client = client;
        _timeoutMs = timeoutMs;
        _versions = versions;
        _versionParam = versionParam;
    }

    /// <summary>
    /// Resolves the model's binding and its versions: <c>version</c>, or every value of the <c>enum</c> of the parameter
    /// <c>version_param</c> names, since each is checked at load time. Null after reporting an error.
    /// </summary>
    public static ModelCallCompiler? Begin(OperatorCompileContext context, ModelCallConfig config, TensorLayout layout)
    {
        var bound = context.Model(config.Model, "/model");
        if (bound is null)
        {
            return null;
        }

        var versions = new List<string?>();
        ParamRef? versionParam = null;
        if (config.VersionParam is not null)
        {
            if (config.Version is not null)
            {
                context.Error("/version", "MODEL_VERSION", "Set at most one of 'version' and 'version_param'.");
                return null;
            }

            if (!context.TryParam(config.VersionParam, "/version_param", out var p, StorageKind.String))
            {
                return null;
            }

            versionParam = p;
            if (!context.Schema.TryGetParam(p.Name, out var leaf) || leaf.Enum is null)
            {
                context.Error("/version_param", "MODEL_VERSION_ENUM", $"Parameter '{p.Name}' must declare an enum of allowed model versions.",
                    "every version is signature-checked at load time (design 6.9)");
                return null;
            }

            foreach (var v in leaf.Enum)
            {
                versions.Add(v.AsString());
            }
        }
        else
        {
            versions.Add(config.Version);
        }

        return new ModelCallCompiler(context, config, layout, bound.Value.Client, bound.Value.TimeoutMs, versions, versionParam);
    }

    /// <summary>
    /// Compiles every input in ordinal order of its name: a plain column or context reference of a type the rule can send.
    /// Reports each input that is not; false when any was not.
    /// </summary>
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

            if (expr.Reads.Count != 1 || !string.Equals(expr.Reads[0].Name, source.Trim(), StringComparison.Ordinal))
            {
                _context.Error(pointer, "MODEL_INPUT_NOT_REFERENCE", $"Model input '{name}' must be a plain column or context reference, not an expression.",
                    "define the transformation as a derived feature so training and serving share one definition (design 6.9)");
                ok = false;
                continue;
            }

            var symbol = expr.Reads[0];
            if (!ModelTensorRule.TryGetInputTensor(symbol.Type, out var tensor))
            {
                var problem = ModelTensorRule.UnsendableInput(_layout, name, symbol.Name, symbol.Type);
                _context.Error(problem.ConfigKey, problem.Code, problem.Message, problem.Hint);
                ok = false;
                continue;
            }

            _inputs[i] = new ModelInput(name, expr, symbol, tensor);
            _mapped[i] = new ModelTensorRule.MappedInput(name, symbol.Name, symbol.Type, _inputs[i].Broadcast);
        }

        return ok;
    }

    /// <summary>
    /// Checks the signature of every version against the inputs and <paramref name="outputs"/> (design 6.9), after the
    /// operator compiled its own keys without errors. Null after reporting a mismatch.
    /// </summary>
    public CompiledModelCall? Finish(IReadOnlyList<ModelTensorRule.MappedOutput> outputs)
    {
        var ok = true;
        foreach (var version in _versions)
        {
            // Null when it cannot be checked now: an error (reported), or the engine checks it at load (see ModelSignature).
            if (_context.ModelSignature(_client, _config.Model, version, "/model") is not { } signature)
            {
                ok &= !_context.HasErrors;
                continue;
            }

            foreach (var problem in ModelTensorRule.Check($"Model '{_config.Model}' version '{version ?? "default"}'", _layout, _mapped, outputs, signature))
            {
                _context.Error(problem.ConfigKey, problem.Code, problem.Message, problem.Hint);
                ok = false;
            }
        }

        return ok ? new CompiledModelCall(_config.Model, _config.Version, _versionParam, _client, _timeoutMs, _inputs) : null;
    }
}

/// <summary>
/// The model a node calls, compiled: its client, the version (fixed or chosen per request by a parameter), and the
/// inputs. Immutable and shared by every request of the node.
/// </summary>
internal sealed class CompiledModelCall
{
    private readonly string? _version;
    private readonly ParamRef? _versionParam;
    private readonly IModelClient _client;
    private readonly int? _timeoutMs;

    public CompiledModelCall(string model, string? version, ParamRef? versionParam, IModelClient client, int? timeoutMs, ModelInput[] inputs)
    {
        Model = model;
        _version = version;
        _versionParam = versionParam;
        _client = client;
        _timeoutMs = timeoutMs;
        Inputs = inputs;
        Target = new IoTarget(IoKind.Model, model);
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            named[input.Name] = input.Source;
        }

        NamedInputs = named;
    }

    /// <summary>The logical model name.</summary>
    public string Model { get; }

    /// <summary>In ordinal order of their names.</summary>
    public ModelInput[] Inputs { get; }

    /// <summary>The model's telemetry target: its duration, errors and fallbacks are tagged with the model's name.</summary>
    public IoTarget Target { get; }

    /// <summary>Model input name → source, for log taps' <c>columns_from</c> (design 6.9).</summary>
    public IReadOnlyDictionary<string, string> NamedInputs { get; }

    /// <summary>The version this request calls.</summary>
    public string? Version(OperatorContext context) => _versionParam is { } p ? context.Param(p).AsString() : _version;

    /// <summary>
    /// Sends one call through <see cref="OperatorContext.InvokeModelAsync"/>, with the request's seed. <paramref name="rows"/>
    /// are the physical rows of the batch for a call whose outputs are per row, or empty (see that method).
    /// </summary>
    public Task<ModelStatus> InvokeAsync(
        OperatorContext context, string? version, NamedInput[] inputs, int batchSize, IModelOutputWriter outputs, ReadOnlyMemory<int> rows)
    {
        var call = new ModelCall
        {
            Model = Model,
            Version = version,
            Inputs = inputs,
            BatchSize = batchSize,
            Options = new ModelCallOptions(Seed: context.Seed),
        };
        return context.InvokeModelAsync(_client, Target, call, outputs, rows, _timeoutMs);
    }
}
