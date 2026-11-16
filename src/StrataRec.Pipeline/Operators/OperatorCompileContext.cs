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
    private readonly HashSet<NameRef> _requires = new();
    private readonly List<ColumnDeclaration> _declares = new();
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

    /// <summary>
    /// The names an operator may read, for looking one up without reading it (listing names, suggesting one). To read a
    /// name outside an expression, use <see cref="TryResolveRead"/>, which records the read.
    /// </summary>
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

    /// <summary>The names bound before the node declared them itself (<see cref="CompiledNode.Requires"/>).</summary>
    internal IReadOnlyCollection<NameRef> Requires => _requires;

    /// <summary>The columns written with a type of the node's own, in the order written (<see cref="CompiledNode.Declares"/>).</summary>
    internal List<ColumnDeclaration> Declares => _declares;

    /// <summary>
    /// Deserializes <see cref="Config"/> strictly (unknown keys rejected). Returns null after reporting the error at the
    /// key at fault, in JSON words (<see cref="Artifacts.Json.JsonBindingErrors"/>).
    /// </summary>
    public T? BindConfig<T>()
        where T : class
    {
        if (ArtifactSerializer.TryBind<T>(Config, $"the config of {Descriptor.Id}", out var config, out var error))
        {
            return config;
        }

        Error(error.Pointer, "NODE_BAD_CONFIG", error.Message,
            error.Hint ?? $"run 'stratarec schema operator {Descriptor.Id}' for the JSON Schema of its config");
        return null;
    }

    /// <summary>Resolves a column name (without <c>col.</c>) the operator reads.</summary>
    /// <param name="configKey">Pointer suffix under <c>/config</c> for diagnostics, e.g. <c>/score_col</c>.</param>
    public bool TryReadColumn(string name, string configKey, out ColumnRef column, bool requireNonNullable = false)
    {
        column = default;
        if (!_scope.TryResolve("col." + name, out var symbol))
        {
            if (_schema.IsUnusable("col." + name))
            {
                HasErrors = true; // declared with an error, reported at the declaration
                return false;
            }

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

    /// <summary>
    /// Resolves an output column the node writes. With <paramref name="implicitType"/>, a column nothing has declared is
    /// declared with that type, so the write declares it (<see cref="Planning.CompiledNode.Declares"/>), and a declared
    /// one keeps its declared type, which the caller checks it can write; without, the column must be declared. A node
    /// that writes a declared column as declared and declares an undeclared one passes its type either way, rather than
    /// asking the schema first: whether a column is declared can differ between arm combinations, which compile the node
    /// once (review 51).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The operator declares neither <see cref="Effects.ColumnWriter"/> nor <see cref="Effects.RowSource"/>: its descriptor
    /// is wrong, which is the operator author's bug, not the configuration's (the data-flow checks trust the effects).
    /// </exception>
    public bool TryWriteColumn(string name, string configKey, ColumnType? implicitType, out OutputColumn column)
    {
        if ((Descriptor.Effects & (Effects.ColumnWriter | Effects.RowSource)) == 0)
        {
            throw new InvalidOperationException(
                $"Operator '{Descriptor.Id}' writes column '{name}' but its descriptor declares neither ColumnWriter nor RowSource.");
        }

        column = default;
        if (!_schema.TryGetColumn(name, out var logical))
        {
            if (_schema.IsUnusable("col." + name))
            {
                HasErrors = true; // declared with an error, reported at the declaration
                return false;
            }

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

        if (implicitType is { } type && logical.Implicit && type != logical.Type)
        {
            // An undeclared column takes its type from the first variant or node that writes it, and every combination
            // runs on one schema: another variant writing another type would be read, or written, as the first one's.
            Error(configKey, "COLUMN_TYPE_MISMATCH",
                $"{Descriptor.Id} writes '{name}' as {type} here, but as {logical.Type} at {logical.Pointer}: a column has one type in every arm combination.",
                type.Kind == logical.Type.Kind
                    ? $"declare it under /columns as {type.AsNullable()}, which both can write"
                    : "write one of them to another column");
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

        // A write with a type of its own declares the column where nothing has; one without needs a declaration.
        if (implicitType is not null)
        {
            _declares.Add(new ColumnDeclaration(logical.Id, implicitType.Value));
        }
        else
        {
            Require(new NameRef(SymbolKind.Column, logical.Id));
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
            // No diagnostics: the expression reads a name declared with an error, reported at the declaration.
            HasErrors = true;
            ExpressionErrors.Report(_diagnostics, diagnostics, Artifact, Pointer + "/config" + configKey, source);
            return null;
        }

        foreach (var symbol in expression.Names)
        {
            Require(symbol);
        }

        foreach (var symbol in expression.Reads)
        {
            RecordSymbol(symbol);
        }

        return expression;
    }

    /// <summary>Resolves a parameter leaf the operator reads.</summary>
    /// <param name="within">A range the operator needs every value in (MMR's λ in [0, 1]): the parameter must be declared
    /// with a min and a max inside it. Values are checked against the declaration, and experiments cannot widen it
    /// (design 11.8), so every value the operator reads is in range, without a check per request.</param>
    public bool TryParam(string name, string configKey, out ParamRef parameter, StorageKind? expectedKind = null, (double Min, double Max)? within = null)
    {
        parameter = default;
        if (!_schema.TryGetParam(name, out var leaf))
        {
            if (_schema.IsUnusable("param." + name))
            {
                HasErrors = true; // declared with an error, reported at the declaration
                return false;
            }

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

        if (within is { } range && !(leaf.Min >= range.Min && leaf.Max <= range.Max))
        {
            var declared = leaf.Min is { } min && leaf.Max is { } max ? $"it is declared as [{min}, {max}]" : "it is declared without a min and a max";
            Error(configKey, "PARAM_RANGE", $"Parameter '{name}' must be declared within [{range.Min}, {range.Max}] for {Descriptor.Id}: {declared}.",
                $"declare \"min\": {range.Min}, \"max\": {range.Max} (or a narrower range) for it under /param_schema");
            return false;
        }

        _readParams.Add(leaf.Id);
        Require(new NameRef(SymbolKind.Param, leaf.Id));
        parameter = new ParamRef(leaf.Id, leaf.Name, leaf.Type);
        return true;
    }

    /// <summary>
    /// All leaves of a map parameter (e.g. every <c>recall_quotas.*</c>), ordered by key: the baseline's and every key an
    /// arm adds. A key only an arm adds is missing in a request without that arm, whose own compile does not have it, so
    /// the node must treat a missing leaf as an absent entry when it runs, and derive nothing from the set of leaves that
    /// a compile without that key would see otherwise (a position, a count). Binding one leaf by name
    /// (<see cref="TryParam"/>) needs it declared where the node is.
    /// </summary>
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

        if (result.Count == 0 && _schema.IsUnusable("param." + mapName))
        {
            HasErrors = true; // declared with an error, reported at the declaration
        }
        else if (result.Count == 0)
        {
            Error(configKey, "PARAM_UNKNOWN", $"'{mapName}' is not a map parameter with entries.", $"declare it in param_schema as map<string,T> and give it values in params");
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>The client bound to model <paramref name="name"/>, shared with every plan of the same binding (design 6.4).</summary>
    public (IModelClient Client, int? TimeoutMs)? Model(string name, string configKey)
    {
        // A config can bind null or "" to a required string; that is a config error at its key, not an exception (review 29).
        if (string.IsNullOrEmpty(name))
        {
            Error(configKey, "MODEL_UNBOUND", $"'{configKey.TrimStart('/')}' must name a model.",
                "set it to a model name that the bindings' 'models' declare");
            return null;
        }

        if (_clients.TryGetModel(name, out var client, out var timeout, out var error, out var hint))
        {
            return (client, timeout);
        }

        Unbound(configKey, "MODEL_UNBOUND", error, hint);
        return null;
    }

    /// <summary>
    /// The signature of one version of a model the node calls (from <see cref="Model"/>), for checking the node's config
    /// against it at load time (design 6.9). Check the node only when it is <see cref="SignatureStatus.Known"/>.
    /// <see cref="SignatureStatus.Failed"/> (the client has no such version) is already reported at
    /// <paramref name="configKey"/>: the node fails. <see cref="SignatureStatus.Pending"/> (the engine is still loading
    /// the client; it compiles the plan again with the answer) and <see cref="SignatureStatus.Unchecked"/> (the plan is
    /// compiled without loading its clients, as by <c>validate</c>) are no error: compile without the check.
    /// </summary>
    public SignatureLookup ModelSignature(IModelClient client, string model, string? version, string configKey)
    {
        var lookup = _clients.Signature(client, model, version);
        if (lookup.Status == SignatureStatus.Failed)
        {
            Error(configKey, "MODEL_SIGNATURE", lookup.Error!, lookup.Hint);
        }

        return lookup;
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

    /// <summary>
    /// Reports an error in a part of the string at <paramref name="configKey"/> (a template, a pattern): at
    /// <paramref name="offset"/>, the 0-based character offset of that part, which tools show as a column inside the string.
    /// </summary>
    public void Error(string configKey, int offset, string code, string message, string? hint = null)
    {
        HasErrors = true;
        _diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, code, Artifact, Pointer + "/config" + configKey, message, hint) { Offset = offset });
    }

    public void Warning(string configKey, string code, string message, string? hint = null) =>
        _diagnostics.Warning(code, Artifact, Pointer + "/config" + configKey, message, hint);

    private readonly HashSet<int> _selfReads = new();

    /// <summary>
    /// Resolves a name the operator reads outside an expression (<c>col.x</c>, <c>ctx.request.y</c>, <c>param.z</c>, as
    /// a prompt template's references are written) and records the read, as <see cref="CompileExpression"/>,
    /// <see cref="TryReadColumn"/> and <see cref="TryParam"/> do for theirs: the fetch planner then fetches a feature before
    /// the node runs, and data-flow validation and experiment footprints see the read (design 5.2, 6.8). One step, so a
    /// name cannot be read without being recorded (architecture audit: resolving through <see cref="Scope"/> and recording
    /// were two calls, and an operator that forgot the second compiled and ran without its feature fetched).
    /// </summary>
    /// <returns>
    /// False when the name is unknown, which the caller reports where it wrote the name, or declared with an error, which
    /// was reported at the declaration: <see cref="HasErrors"/> is then set, and the caller reports nothing.
    /// </returns>
    public bool TryResolveRead(string name, out Symbol symbol, out bool reported)
    {
        reported = false;
        if (!_scope.TryResolve(name, out symbol))
        {
            reported = _schema.IsUnusable(name);
            HasErrors |= reported;
            return false;
        }

        RecordSymbol(symbol);
        return true;
    }

    /// <summary>Records a name the node binds (<see cref="CompiledNode.Requires"/>), unless it declared that column itself.</summary>
    internal void Require(in Symbol symbol)
    {
        if (symbol.Kind is SymbolKind.Column or SymbolKind.Param or SymbolKind.Context)
        {
            Require(new NameRef(symbol.Kind, symbol.Id));
        }
    }

    private void Require(NameRef name)
    {
        if (name.Kind == SymbolKind.Column)
        {
            foreach (var declared in _declares)
            {
                if (declared.Logical == name.Id)
                {
                    return; // written by the node before, with a type of its own
                }
            }
        }

        _requires.Add(name);
    }

    /// <summary>Records a resolved name as a read of the node (<see cref="TryResolveRead"/>), and as a name it binds.</summary>
    internal void RecordSymbol(in Symbol symbol)
    {
        Require(symbol);
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
