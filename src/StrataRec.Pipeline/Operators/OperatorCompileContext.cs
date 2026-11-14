using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Models;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// Everything an <see cref="IOperatorFactory"/> may use while compiling one node variant. Every column, parameter
/// and expression the operator resolves through this context is recorded as a read or write of the node; those sets
/// drive fetch planning, data-flow validation and experiment footprints (design 5.2, 6.8, 11.5).
/// </summary>
public sealed class OperatorCompileContext
{
    private readonly PlanSchema _schema;
    private readonly IExpressionScope _scope;
    private readonly ClientResolver _clients;
    private readonly DiagnosticBag _diagnostics;
    private readonly HashSet<int> _readColumns = new();
    private readonly HashSet<int> _readFeatures = new();
    private readonly HashSet<int> _requiredOutputs = new();
    private readonly HashSet<int> _readParams = new();
    private readonly HashSet<VersionRead> _versionReads = new();
    private readonly List<OutputColumn> _outputs = new();

    internal OperatorCompileContext(
        PlanSchema schema,
        IExpressionScope scope,
        ClientResolver clients,
        DiagnosticBag diagnostics,
        string artifact,
        string nodeId,
        string pointer,
        JsonObject config,
        OperatorDescriptor descriptor)
    {
        _schema = schema;
        _scope = scope;
        _clients = clients;
        _diagnostics = diagnostics;
        Artifact = artifact;
        NodeId = nodeId;
        Pointer = pointer;
        Config = config;
        Descriptor = descriptor;
    }

    public string NodeId { get; }

    /// <summary>Pointer of the node, e.g. <c>/nodes/fusion</c>; config keys are reported under <c>/config</c>.</summary>
    public string Pointer { get; }

    public string Artifact { get; }

    public JsonObject Config { get; }

    public OperatorDescriptor Descriptor { get; }

    public IExpressionScope Scope => _scope;

    /// <summary>The plan schema (declared columns, parameters, features) for lookups.</summary>
    public PlanSchema Schema => _schema;

    public bool HasErrors { get; private set; }

    internal IReadOnlyCollection<int> ReadColumns => _readColumns;

    internal IReadOnlyCollection<int> ReadFeatures => _readFeatures;

    internal IReadOnlyCollection<int> RequiredOutputs => _requiredOutputs;

    internal IReadOnlyCollection<int> ReadParams => _readParams;

    internal IReadOnlyCollection<VersionRead> VersionReads => _versionReads;

    internal IReadOnlyList<OutputColumn> Outputs => _outputs;

    /// <summary>Deserializes <see cref="Config"/> strictly (unknown keys rejected). Returns null after reporting errors.</summary>
    public T? BindConfig<T>()
        where T : class
    {
        try
        {
            return Config.Deserialize<T>(ArtifactSerializer.Options)
                   ?? throw new JsonException("config must be an object");
        }
        catch (JsonException ex)
        {
            var path = ex.Path is { Length: > 1 } p ? "/" + p.TrimStart('$', '.').Replace('.', '/') : string.Empty;
            Error(path, "NODE_BAD_CONFIG", ex.Message, $"see the JSON Schema of {Descriptor.Id} (stratarec schema)");
            return null;
        }
    }

    /// <summary>Resolves a column name (without <c>col.</c>) the operator reads.</summary>
    /// <param name="configKey">Pointer suffix under <c>/config</c> for diagnostics, e.g. <c>/score_col</c>.</param>
    public bool TryReadColumn(string name, string configKey, out ColumnRef column, bool requireNonNullable = false)
    {
        column = default;
        if (!_scope.TryResolve("col." + name, out var symbol))
        {
            var known = new List<string>();
            foreach (var c in _schema.Columns)
            {
                known.Add(c.Name);
            }

            Error(configKey, "COLUMN_UNKNOWN", $"Column '{name}' is not declared, produced or a catalog feature.", NameSuggestions.DidYouMean(name, known));
            return false;
        }

        if (requireNonNullable && symbol.Type.Nullable)
        {
            Error(configKey, "COLUMN_NULLABLE", $"Column '{name}' is nullable ({symbol.Type}), but {Descriptor.Id} needs a value for every row.",
                "declare a default for the column or feature, or derive a non-nullable feature with '??'");
            return false;
        }

        RecordSymbol(symbol);
        column = new ColumnRef(symbol.Id, symbol.FixedSlot, symbol.Type, name);
        return true;
    }

    /// <summary>Resolves an output column the node writes. Undeclared columns take <paramref name="implicitType"/> if given.</summary>
    public bool TryWriteColumn(string name, string configKey, ColumnType? implicitType, out OutputColumn column)
    {
        column = default;
        if (!_schema.TryGetColumn(name, out var logical))
        {
            if (implicitType is null)
            {
                Error(configKey, "COLUMN_UNDECLARED", $"Output column '{name}' is not declared.",
                    $"declare it under /columns, e.g. \"{name}\": {{ \"type\": \"float64\", \"default\": 0.0 }}");
                return false;
            }

            logical = _schema.DeclareOutputColumn(name, implicitType.Value, flow: false, hasDefault: false, default, Pointer + "/config" + configKey, isImplicit: true);
        }

        if (logical.Kind != ColumnKind.Output)
        {
            Error(configKey, "COLUMN_NOT_WRITABLE", $"'{name}' is a {logical.Kind.ToString().ToLowerInvariant()} column and cannot be written by a node.",
                "write a new column under score.* or another node-output namespace");
            return false;
        }

        if (implicitType is { } expected && !logical.Implicit && expected.Kind != logical.Type.Kind)
        {
            Error(configKey, "COLUMN_TYPE_MISMATCH", $"{Descriptor.Id} writes '{name}' as {expected}, but it is declared as {logical.Type}.");
            return false;
        }

        foreach (var existing in _outputs)
        {
            if (existing.LogicalId == logical.Id)
            {
                Error(configKey, "COLUMN_WRITTEN_TWICE", $"Node writes column '{name}' more than once.");
                return false;
            }
        }

        var slot = (Descriptor.Effects & Effects.RowSource) != 0 ? _schema.GetRowSourceSlot(logical) : _schema.GetNodeSlot(NodeId, logical);
        column = new OutputColumn(logical.Id, slot, logical.Type, name);
        _outputs.Add(column);
        return true;
    }

    /// <summary>Compiles a ColumnExpr and records its reads. Returns null after reporting errors.</summary>
    public CompiledExpression? CompileExpression(string? source, string configKey, CompileOptions options)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            Error(configKey, "EXPR_EMPTY", "Expression is empty.");
            return null;
        }

        if (!ExpressionCompiler.TryCompile(source, _scope, options, out var expression, out var diagnostics))
        {
            foreach (var d in diagnostics)
            {
                Error(configKey, d.Code, $"{d.Message} (at offset {d.Offset} in '{source}')", d.Hint);
            }

            return null;
        }

        foreach (var symbol in expression.Reads)
        {
            RecordSymbol(symbol);
        }

        return expression;
    }

    /// <summary>Resolves a parameter leaf the operator reads.</summary>
    public bool TryParam(string name, string configKey, out ParamRef parameter, StorageKind? expectedKind = null)
    {
        parameter = default;
        if (!_schema.TryGetParam(name, out var leaf))
        {
            var known = new List<string>();
            foreach (var p in _schema.Params)
            {
                known.Add(p.Name);
            }

            Error(configKey, "PARAM_UNKNOWN", $"Parameter '{name}' is not declared in param_schema.", NameSuggestions.DidYouMean(name, known));
            return false;
        }

        if (expectedKind is { } kind && leaf.Type.Kind != kind)
        {
            Error(configKey, "PARAM_TYPE_MISMATCH", $"Parameter '{name}' has type {leaf.Type}, but {Descriptor.Id} needs {new ColumnType(kind)}.");
            return false;
        }

        _readParams.Add(leaf.Id);
        parameter = new ParamRef(leaf.Id, leaf.Name, leaf.Type);
        return true;
    }

    /// <summary>All leaves of a map parameter (e.g. every <c>recall_quotas.*</c>), ordered by key.</summary>
    public List<ParamRef> MapParam(string mapName, string configKey, StorageKind? expectedKind = null)
    {
        var result = new List<ParamRef>();
        foreach (var leaf in _schema.MapLeaves(mapName))
        {
            if (expectedKind is { } kind && leaf.Type.Kind != kind)
            {
                Error(configKey, "PARAM_TYPE_MISMATCH", $"Parameter '{leaf.Name}' has type {leaf.Type}, but {Descriptor.Id} needs {new ColumnType(kind)}.");
                continue;
            }

            _readParams.Add(leaf.Id);
            result.Add(new ParamRef(leaf.Id, leaf.Name, leaf.Type));
        }

        if (result.Count == 0)
        {
            Error(configKey, "PARAM_UNKNOWN", $"'{mapName}' is not a map parameter with entries.", $"declare it in param_schema as map<string,T> and give it values in params");
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>The client bound to model <paramref name="name"/>, shared with every plan of the same binding (design 6.4).</summary>
    public (IModelClient Client, int? TimeoutMs)? Model(string name, string configKey)
    {
        if (_clients.TryGetModel(name, out var client, out var timeout, out var error, out var hint))
        {
            return (client, timeout);
        }

        Unbound(configKey, "MODEL_UNBOUND", error, hint);
        return null;
    }

    /// <summary>
    /// The signature of one version of a model the node calls (from <see cref="Model"/>), for checking the node's config
    /// against it at load time (design 6.9). Null when the node cannot be checked against it now: an error at
    /// <paramref name="configKey"/> when the client has no such version (then <see cref="HasErrors"/> is set), nothing
    /// when the engine is still loading the client (it compiles the plan again with the answer) or when the plan is
    /// compiled without loading its clients (<c>validate</c>: the engine checks it when it loads the plan).
    /// </summary>
    public ModelSignature? ModelSignature(IModelClient client, string model, string? version, string configKey)
    {
        var lookup = _clients.Signature(client, model, version);
        return lookup.Status switch
        {
            SignatureStatus.Known => lookup.Signature,
            SignatureStatus.Failed => SignatureFailed(configKey, lookup.Error!, lookup.Hint),
            SignatureStatus.Pending or SignatureStatus.Unchecked => null,
        };
    }

    private ModelSignature? SignatureFailed(string configKey, string message, string? hint)
    {
        Error(configKey, "MODEL_SIGNATURE", message, hint);
        return null;
    }

    /// <summary>The source bound to candidate source <paramref name="name"/>, shared like <see cref="Model"/>.</summary>
    public (ICandidateSource Source, int? TimeoutMs)? CandidateSource(string name, string configKey)
    {
        if (_clients.TryGetCandidateSource(name, out var source, out var timeout, out var error, out var hint))
        {
            return (source, timeout);
        }

        Unbound(configKey, "CANDIDATE_SOURCE_UNBOUND", error, hint);
        return null;
    }

    /// <summary>A binding the node cannot use; a null error was reported at the binding's setting already.</summary>
    private void Unbound(string configKey, string code, string? error, string? hint)
    {
        if (error is null)
        {
            HasErrors = true;
            return;
        }

        Error(configKey, code, error, hint);
    }

    public void Error(string configKey, string code, string message, string? hint = null)
    {
        HasErrors = true;
        _diagnostics.Error(code, Artifact, Pointer + "/config" + configKey, message, hint);
    }

    public void Warning(string configKey, string code, string message, string? hint = null) =>
        _diagnostics.Warning(code, Artifact, Pointer + "/config" + configKey, message, hint);

    private readonly HashSet<int> _selfReads = new();

    internal void RecordSymbol(in Symbol symbol)
    {
        switch (symbol.Kind)
        {
            case SymbolKind.Column:
                _readColumns.Add(symbol.Id);
                var column = _schema.Columns[symbol.Id];
                if (column.Kind == ColumnKind.Feature)
                {
                    _readFeatures.Add(column.FeatureIndex);
                }
                else if (column.Kind == ColumnKind.Output && symbol.FixedSlot < 0 && !symbol.Type.Nullable)
                {
                    _requiredOutputs.Add(symbol.Id);
                }
                else if (column.Kind == ColumnKind.Output && symbol.FixedSlot >= 0)
                {
                    // A pinned version, col.x@node (the scope resolves only these to a slot of their own).
                    var pinned = symbol.Name[(symbol.Name.IndexOf('@', StringComparison.Ordinal) + 1)..];
                    if (pinned != NodeId)
                    {
                        _versionReads.Add(new VersionRead(pinned, symbol.Id));
                    }
                    else if (_selfReads.Add(symbol.Id))
                    {
                        // The node's own version does not exist until the node commits: it would read a fresh slot.
                        HasErrors = true;
                        _diagnostics.Error("VERSION_REF_SELF", Artifact, Pointer + "/config",
                            $"Node '{NodeId}' reads its own output '{symbol.Name}', which does not exist until the node has run.",
                            $"read 'col.{column.Name}' (the version before '{NodeId}') or a version pinned to an earlier node");
                    }
                }

                break;
            case SymbolKind.Context:
                var field = _schema.ContextFields[symbol.Id];
                if (field.Kind == ContextFieldKind.Feature)
                {
                    _readFeatures.Add(field.FeatureIndex);
                }

                break;
            case SymbolKind.Param:
                _readParams.Add(symbol.Id);
                break;
        }
    }
}
