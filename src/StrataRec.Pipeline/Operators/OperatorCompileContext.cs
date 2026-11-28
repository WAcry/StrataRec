using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// Everything an <see cref="IOperatorFactory"/> may use while compiling one node variant. Every column, parameter and
/// expression the operator resolves through this context is recorded as a read or write of the node.
/// </summary>
public sealed class OperatorCompileContext
{
    private readonly PlanSchema _schema;
    private readonly IExpressionScope _scope;
    private readonly ClientResolver _clients;
    private readonly DiagnosticBag _diagnostics;
    private readonly HashSet<int> _readColumns = new();
    private readonly HashSet<int> _currentReads = new();
    private readonly HashSet<int> _readFeatures = new();
    private readonly HashSet<int> _awaitedFeatures = new();
    private readonly HashSet<int> _requiredOutputs = new();
    private readonly HashSet<int> _readParams = new();
    private readonly HashSet<VersionRead> _versionReads = new();
    private readonly HashSet<NameRef> _requires = new();
    private readonly List<ColumnDeclaration> _declares = new();
    private readonly HashSet<int> _firstDeclared = new(); // columns nothing had declared before a write of this compile
    private readonly List<OutputColumn> _outputs = new();
    private readonly List<RequestOutput> _requestOutputs = new();
    private readonly HashSet<int> _requestReads = new();
    private readonly List<PipelineCall> _calls = new();
    private readonly CallerOfPipelines _caller;

    private bool _awaiting;

    internal OperatorCompileContext(
        PlanSchema schema,
        IExpressionScope scope,
        ClientResolver clients,
        DiagnosticBag diagnostics,
        string artifact,
        string nodeId,
        string pointer,
        JsonObject config,
        OperatorDescriptor descriptor,
        CallerOfPipelines caller = default)
    {
        _caller = caller;
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
    /// The names an operator may read, for looking one up without reading it. To read a name outside an expression, use
    /// <see cref="TryResolveRead"/>, which records the read.
    /// </summary>
    public IExpressionScope Scope => _scope;

    /// <summary>The plan schema (declared columns, parameters, features) for lookups.</summary>
    public PlanSchema Schema => _schema;

    public bool HasErrors { get; private set; }

    internal IReadOnlyCollection<int> ReadColumns => _readColumns;

    internal IReadOnlyCollection<int> CurrentReads => _currentReads;

    internal IReadOnlyCollection<int> ReadFeatures => _readFeatures;

    internal IReadOnlyCollection<int> AwaitedFeatures => _awaitedFeatures;

    internal IReadOnlyCollection<int> RequiredOutputs => _requiredOutputs;

    internal IReadOnlyCollection<int> ReadParams => _readParams;

    internal IReadOnlyCollection<VersionRead> VersionReads => _versionReads;

    internal IReadOnlyList<OutputColumn> Outputs => _outputs;

    internal IReadOnlyList<RequestOutput> RequestOutputs => _requestOutputs;

    internal IReadOnlyList<PipelineCall> Calls => _calls;

    internal IReadOnlyCollection<int> RequestReads => _requestReads;

    internal IReadOnlyCollection<NameRef> Requires => _requires;

    internal List<ColumnDeclaration> Declares => _declares;

    /// <summary>
    /// Deserializes <see cref="Config"/> strictly (unknown keys rejected). Returns null after reporting the error at
    /// the key at fault, in JSON words.
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
                HasErrors = true;
                return false;
            }

            Error(configKey, "COLUMN_UNKNOWN", $"Column '{name}' is not declared, produced or a catalog feature.",
                NameSuggestions.DidYouMean(name, _schema.KnownColumnNames()));
            return false;
        }

        if (requireNonNullable && symbol.Type.Nullable)
        {
            Error(configKey, "COLUMN_NULLABLE", $"Column '{name}' is nullable ({symbol.Type}), but {Descriptor.Id} needs a value for every row.",
                "declare a default for the column or feature, or derive a non-nullable feature with '??'");
            return false;
        }

        if (ReadsOwnOutput(symbol, configKey))
        {
            return false;
        }

        RecordSymbol(symbol);
        column = new ColumnRef(symbol.Id, symbol.FixedSlot, symbol.Type, name);
        return true;
    }

    /// <summary>
    /// With <paramref name="implicitType"/>, a column nothing has declared is declared with that type, and a declared
    /// one keeps its declared type, which the caller checks it can write; without, the column must be declared. A node
    /// that writes a declared column as declared and declares an undeclared one passes its type either way, rather than
    /// asking the schema first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The operator declares neither <see cref="Effects.ColumnWriter"/> nor
    /// <see cref="Effects.RowSource"/>.</exception>
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
                HasErrors = true;
                return false;
            }

            if (implicitType is null)
            {
                Error(configKey, "COLUMN_UNDECLARED", $"Output column '{name}' is not declared.",
                    $"declare it under /columns, e.g. \"{name}\": {{ \"type\": \"float64\", \"default\": 0.0 }}");
                return false;
            }

            logical = _schema.DeclareOutputColumn(name, implicitType.Value, flow: false, hasDefault: false, default, Pointer + "/config" + configKey, isImplicit: true);
            _firstDeclared.Add(logical.Id);
        }

        if (logical.Kind != ColumnKind.Output)
        {
            Error(configKey, "COLUMN_NOT_WRITABLE", $"'{name}' is a {logical.Kind.ToString().ToLowerInvariant()} column and cannot be written by a node.",
                "write a new column under score.* or another node-output namespace");
            return false;
        }

        if (implicitType is { } type && logical.Implicit && type != logical.Type)
        {
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

    /// <summary>
    /// Resolves a request column the node writes (<c>req.&lt;name&gt;</c>, declared under <c>request_columns</c>): the
    /// operator stages its value with <see cref="OperatorContext.SetRequestValue"/>, and it is published when the node
    /// commits. A request column is always declared.
    /// </summary>
    /// <param name="configKey">Pointer suffix under <c>/config</c> for diagnostics, e.g. <c>/values/candidates.count</c>.</param>
    /// <param name="suggested">The type the operator would write, for the hint when the column is not declared.</param>
    /// <exception cref="InvalidOperationException">The operator does not declare
    /// <see cref="Effects.RequestWriter"/>.</exception>
    public bool TryWriteRequestColumn(string name, string configKey, out RequestOutput output, ColumnType? suggested = null)
    {
        if ((Descriptor.Effects & Effects.RequestWriter) == 0)
        {
            throw new InvalidOperationException(
                $"Operator '{Descriptor.Id}' writes request column '{name}' but its descriptor does not declare RequestWriter.");
        }

        output = default;
        if (!_schema.TryGetRequestColumn(name, out var field))
        {
            if (_schema.IsUnusable("req." + name))
            {
                HasErrors = true;
                return false;
            }

            Error(configKey, "REQUEST_COLUMN_UNDECLARED", $"Request column '{name}' is not declared.",
                NameSuggestions.DidYouMean(name, _schema.KnownRequestColumnNames())
                ?? $"declare it under /request_columns: \"{name}\": {{ \"type\": \"{suggested?.ToString() ?? "int64"}\" }}");
            return false;
        }

        foreach (var existing in _requestOutputs)
        {
            if (existing.ContextId == field.Id)
            {
                Error(configKey, "COLUMN_WRITTEN_TWICE", $"Node writes request column '{name}' more than once.");
                return false;
            }
        }

        Require(new NameRef(SymbolKind.Context, field.Id));
        output = new RequestOutput(field.Id, _requestOutputs.Count, field.DeclaredType, name);
        _requestOutputs.Add(output);
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
            HasErrors = true;
            ExpressionErrors.Report(_diagnostics, diagnostics, Artifact, Pointer + "/config" + configKey, source);
            return null;
        }

        foreach (var symbol in expression.Names)
        {
            if (ReadsOwnOutput(symbol, configKey))
            {
                return null;
            }

            Require(symbol);
        }

        foreach (var symbol in expression.Reads)
        {
            RecordSymbol(symbol);
        }

        return expression;
    }

    /// <summary>
    /// Compiles a request-level expression (<c>ctx.*</c> and <c>param.*</c>; <c>col.*</c> is an error) whose context
    /// features the operator waits for itself while it runs (<see cref="OperatorContext.WhenEachReadyAsync"/>), instead
    /// of the executor filling them before the node starts. For an operator that sends several calls of which only some
    /// read context features (a recall source's query): each call waits for its own features and the others start at
    /// once. The features remain reads of the node: they are prefetched with the request's context, and data-flow
    /// checks, experiment footprints and taps see them. Returns null after reporting errors.
    /// </summary>
    /// <param name="expectedType">The result type the operator needs, or null for any; the result may be nullable.</param>
    public CompiledExpression? CompileAwaitedExpression(string? source, string configKey, ColumnType? expectedType = null)
    {
        _awaiting = true;
        try
        {
            return CompileExpression(source, configKey, new CompileOptions { ExpectedType = expectedType, AllowNullableResult = true, RequestLevelOnly = true });
        }
        finally
        {
            _awaiting = false;
        }
    }

    /// <summary>
    /// The context features <paramref name="expressions"/> read, compiled by <see cref="CompileAwaitedExpression"/>: what one
    /// call of the operator waits for before it evaluates them (<see cref="OperatorContext.WhenEachReadyAsync"/>). Request
    /// inputs and parameters are not waited for; expressions reading only those give <see cref="AwaitedReads.None"/>.
    /// </summary>
    public AwaitedReads AwaitedReadsOf(IReadOnlyList<CompiledExpression> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        var features = new List<int>();
        for (var e = 0; e < expressions.Count; e++)
        {
            var reads = expressions[e].Reads;
            for (var i = 0; i < reads.Count; i++) // an interface foreach would box the enumerator
            {
                var symbol = reads[i];
                if (symbol.Kind == SymbolKind.Context && _schema.ContextFields[symbol.Id] is { Kind: ContextFieldKind.Feature } field
                    && !features.Contains(field.FeatureIndex))
                {
                    features.Add(field.FeatureIndex);
                }
            }
        }

        if (features.Count == 0)
        {
            return AwaitedReads.None;
        }

        features.Sort();
        return new AwaitedReads([.. features]);
    }

    /// <summary>Resolves a parameter leaf the operator reads.</summary>
    /// <param name="within">A range the operator needs every value in (MMR's λ in [0, 1]): the parameter must be
    /// declared with a min and a max inside it. Values are checked against the declaration, and experiments cannot
    /// widen it, so every value the operator reads is in range.</param>
    public bool TryParam(string name, string configKey, out ParamRef parameter, StorageKind? expectedKind = null, (double Min, double Max)? within = null)
    {
        parameter = default;
        if (!_schema.TryGetParam(name, out var leaf))
        {
            if (_schema.IsUnusable("param." + name))
            {
                HasErrors = true;
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
    /// All leaves of a map parameter (e.g. every <c>recall_quotas.*</c>), ordered by key: the baseline's and every key
    /// an arm adds. A key only an arm adds is missing in a request without that arm, so the node must treat a missing
    /// leaf as an absent entry when it runs, and derive nothing from the set of leaves that a compile without that key
    /// would see otherwise (a position, a count). Binding one leaf by name (<see cref="TryParam"/>) needs it declared
    /// where the node is.
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
            HasErrors = true;
        }
        else if (result.Count == 0)
        {
            Error(configKey, "PARAM_UNKNOWN", $"'{mapName}' is not a map parameter with entries.", $"declare it in param_schema as map<string,T> and give it values in params");
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>
    /// Declares that the node calls pipeline <paramref name="pipelineId"/>, and returns the handle its operator calls
    /// with (<see cref="OperatorContext.CallPipelineAsync"/>). Say what the call needs of the pipeline with
    /// <see cref="TryCallInput"/>, <see cref="TryCallColumn"/>, <see cref="TryCallValue"/> and
    /// <see cref="CallAppendsRows"/>: a plan compiled with the pipelines it may call (a workspace) checks each here, at
    /// its config key; one compiled without them (an engine loading one release) is checked when the call is made, and
    /// a call that does not fit fails as a result.
    /// </summary>
    /// <param name="cache">The node's cache of the call's results, from its config (a member named <c>cache</c>, by
    /// convention); null runs the pipeline for every call.</param>
    /// <param name="cacheKey">Where the node's config has <paramref name="cache"/>: a cache that is wrong or not bound
    /// is reported there, and the call is declared without it.</param>
    /// <returns>False after reporting an error at <paramref name="configKey"/>.</returns>
    public bool TryCallPipeline(
        string pipelineId, string configKey, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PipelineCall? call,
        PipelineCallCacheConfig? cache = null, string cacheKey = "/cache")
    {
        call = null;
        if (string.IsNullOrEmpty(pipelineId))
        {
            Error(configKey, "PIPELINE_CALL_UNKNOWN", $"'{configKey.TrimStart('/')}' must name a pipeline.", "set it to the id of a pipeline the engine serves");
            return false;
        }

        if (pipelineId == _caller.PipelineId)
        {
            Error(configKey, "PIPELINE_CALL_CYCLE", $"Node '{NodeId}' calls its own pipeline '{pipelineId}'.", "a pipeline's calls may not lead back to it");
            return false;
        }

        PipelineInterface? known = null;
        if (_caller.Directory is { } directory)
        {
            var lookup = directory.Find(pipelineId, _caller.PipelineId, _caller.Environment);
            switch (lookup.Status)
            {
                case PipelineLookupStatus.Known:
                    known = lookup.Interface;
                    break;
                case PipelineLookupStatus.Unknown:
                    Error(configKey, "PIPELINE_CALL_UNKNOWN", $"There is no pipeline '{pipelineId}'.", lookup.Detail ?? "name a pipeline of the workspace");
                    return false;
                case PipelineLookupStatus.Cycle:
                    Error(configKey, "PIPELINE_CALL_CYCLE", $"Calling pipeline '{pipelineId}' leads back to '{_caller.PipelineId}': {lookup.Detail}.",
                        "a pipeline's calls may not lead back to it");
                    return false;
                default:
                    Error(configKey, "PIPELINE_CALL_FAILED", $"Pipeline '{pipelineId}', which node '{NodeId}' calls, does not compile: {lookup.Detail}",
                        $"fix '{pipelineId}' first (validate it on its own)");
                    return false;
            }
        }

        call = new PipelineCall(pipelineId, _caller.PipelineId, NodeId, configKey, _caller.Inputs, _schema.KeyType, known);
        if (cache is not null)
        {
            DeclareCallCache(call, cache, cacheKey);
        }

        _calls.Add(call);
        return true;
    }

    private void DeclareCallCache(PipelineCall call, PipelineCallCacheConfig cache, string cacheKey)
    {
        var at = Pointer + "/config" + cacheKey;
        var ok = true;
        long ttlMs = 0;
        if (cache.TtlMs is not { } ttl)
        {
            if (Features.Cache.CacheSettings.Unkept(null, cache.StaleIfErrorMs, cache.Version) is not { } given)
            {
                call.Cache = new PipelineCallCache(_clients.RequestPipelineCache(call.PipelineId), Features.Cache.CachePolicy.Unlimited, null);
                return;
            }

            HasErrors = true;
            _diagnostics.Error("CACHE_TTL", Artifact, at, $"The cache of node '{NodeId}' gives {given} but no 'ttl_ms'.",
                "add how long a result of the call is served, in milliseconds, e.g. \"ttl_ms\": 3600000; "
                + $"or remove {given}: a cache with no settings keeps nothing, and has the calls of one request share a result");
            return;
        }
        else if (!Features.Cache.CacheSettings.TryTtl(ttl, _diagnostics, Artifact, at))
        {
            ok = false;
        }
        else
        {
            ttlMs = ttl;
        }

        ok &= Features.Cache.CacheSettings.TryStale(cache.StaleIfErrorMs, "pipeline", _diagnostics, Artifact, at, out var staleMs);
        ok &= Features.Cache.CacheSettings.TryVersion(cache.Version, _diagnostics, Artifact, at);
        if (!_clients.TryGetPipelineCache(call.PipelineId, out var tiers, out var unbound, out var hint))
        {
            ok = false;
            if (unbound is not null)
            {
                _diagnostics.Error("PIPELINE_CALL_CACHE_UNBOUND", Artifact, at, unbound, hint);
            }
        }
        else if (tiers.Distributed is not null)
        {
            ok &= Features.Cache.CacheSettings.TryRetention("pipeline", call.PipelineId, ttlMs, staleMs, _diagnostics, Artifact, at);
        }

        if (!ok)
        {
            HasErrors = true;
            return;
        }

        call.Cache = new PipelineCallCache(tiers!, new Features.Cache.CachePolicy(ttlMs, staleMs), cache.Version);
    }

    /// <summary>
    /// Declares an input the operator passes with the call, of <paramref name="type"/>: the pipeline must declare it, of a
    /// type every such value converts to. Inputs the operator does not pass are passed on from the request by name.
    /// </summary>
    public bool TryCallInput(PipelineCall call, string name, string configKey, ColumnType type)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.PassesInput(name))
        {
            Error(configKey, "PIPELINE_CALL_INPUT", $"Input '{name}' is passed twice to pipeline '{call.PipelineId}'.", "pass each input once");
            return false;
        }

        call.AddInput(name, type);
        if (call.Interface is { } callee && PipelineCall.InputMismatch(callee, name, type) is { } mismatch)
        {
            Error(configKey, "PIPELINE_CALL_INPUT", Sentence(mismatch), !callee.Inputs.Contains(name)
                ? Spelling.DidYouMean(name, callee.Inputs.Names) ?? $"pass an input '{callee.PipelineId}' declares in its request_schema"
                : type.Nullable ? "pass a value that is never missing, e.g. with '?? <default>'" : null);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Declares an output column of the pipeline the operator reads from a result (<see cref="PipelineCallResult.Columns"/>),
    /// into <paramref name="into"/>: the pipeline's output must have it, of a type whose every value converts, and not
    /// nullable where <paramref name="into"/> is not.
    /// </summary>
    public bool TryCallColumn(PipelineCall call, string name, string configKey, ColumnType into) => TryCallOutput(call, name, configKey, into, value: false);

    /// <summary>Declares an output value of the pipeline (<c>output.values</c>) the operator reads, as <see cref="TryCallColumn"/> a column.</summary>
    public bool TryCallValue(PipelineCall call, string name, string configKey, ColumnType into) => TryCallOutput(call, name, configKey, into, value: true);

    /// <summary>
    /// Declares that the operator appends the result's rows to this pipeline's candidates: the pipeline's keys must be of
    /// this pipeline's key type.
    /// </summary>
    public bool CallAppendsRows(PipelineCall call, string configKey)
    {
        ArgumentNullException.ThrowIfNull(call);
        call.AppendsRows = true;
        if (call.Interface is { } callee && call.KeyMismatch(callee) is { } mismatch)
        {
            Error(configKey, "PIPELINE_CALL_KEY_TYPE", Sentence(mismatch), "call a pipeline whose 'entity_key_type' is this pipeline's");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Declares that the operator reads the ids of the result's rows as keys of <paramref name="type"/>, whatever this
    /// pipeline's own keys are: the pipeline's keys must be of that type.
    /// </summary>
    public bool CallReadsKeys(PipelineCall call, string configKey, EntityKeyType type)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.ReadsKeys is { } declared && declared != type)
        {
            // One call's ids are of one type: the second reading would be the only one checked when the call is made.
            Error(configKey, "PIPELINE_CALL_KEY_TYPE", $"The ids of pipeline '{call.PipelineId}' are read as {Key(declared)} and as {Key(type)}.",
                "a pipeline's keys are of one type: read its ids as that type everywhere");
            return false;
        }

        call.ReadsKeys = type;
        if (call.Interface is { } callee && PipelineCall.ReadKeyMismatch(callee, type) is { } mismatch)
        {
            Error(configKey, "PIPELINE_CALL_KEY_TYPE", Sentence(mismatch),
                $"read them into a list of {Key(callee.KeyType)}, the key type of '{callee.PipelineId}'");
            return false;
        }

        return true;
    }

    private static string Key(EntityKeyType type) => type == EntityKeyType.Int64 ? "int64" : "string";

    internal void CheckCalls()
    {
        foreach (var call in _calls)
        {
            if (call.Interface is { } callee && call.MissingInput(callee) is { } missing)
            {
                Error(call.ConfigKey, "PIPELINE_CALL_INPUT", Sentence(missing),
                    $"have the operator pass it (PipelineSource@1: \"inputs\", with '?? <default>' for a value that may be missing), or declare it required in the request_schema of '{call.Caller}', which passes inputs on by name");
            }
        }
    }

    private bool TryCallOutput(PipelineCall call, string name, string configKey, ColumnType into, bool value)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (value)
        {
            call.AddValue(name, into);
        }
        else
        {
            call.AddColumn(name, into);
        }

        if (call.Interface is { } callee && PipelineCall.OutputMismatch(callee, name, into, value) is { } mismatch)
        {
            var found = value ? callee.TryGetValue(name, out _) : callee.TryGetColumn(name, out _);
            Error(configKey, "PIPELINE_CALL_OUTPUT", Sentence(mismatch), found ? null : Spelling.DidYouMean(name, Names(value ? callee.Values : callee.Columns))
                ?? $"read an output {(value ? "value" : "column")} '{callee.PipelineId}' declares under 'output'");
            return false;
        }

        return true;
    }

    private static string Sentence(string clause) => char.ToUpperInvariant(clause[0]) + clause[1..] + ".";

    private static List<string> Names(ImmutableArray<PipelineOutput> outputs)
    {
        var names = new List<string>(outputs.Length);
        foreach (var output in outputs)
        {
            names.Add(output.Name);
        }

        return names;
    }

    /// <summary>
    /// The service bound to <paramref name="name"/>, shared with every plan of the same binding, for the node
    /// to call in <paramref name="role"/> (<see cref="Core.Diagnostics.IoRoles"/>: <c>model</c>, <c>recall</c>, or a role of its own, which
    /// its calls' telemetry is tagged with). Null after reporting an error at <paramref name="configKey"/>, or at the
    /// binding when the binding is wrong.
    /// </summary>
    public BoundService? Service(string name, string configKey, string role)
    {
        // A config can bind null or "" to a required string; that is a config error at its key, not an exception.
        if (string.IsNullOrEmpty(name))
        {
            Error(configKey, "SERVICE_UNBOUND", $"'{configKey.TrimStart('/')}' must name a service.",
                "set it to a service name that the bindings' 'services' declare");
            return null;
        }

        if (_clients.TryGetService(name, out var client, out var timeout, out var cache, out var error, out var hint))
        {
            return new BoundService(name, client, role, timeout, cache);
        }

        if (error is null)
        {
            HasErrors = true;
            return null;
        }

        Error(configKey, "SERVICE_UNBOUND", error, hint);
        return null;
    }

    /// <summary>
    /// The signature of one version of a service the node calls (from <see cref="Service"/>), for checking the node's
    /// config against it at load time. Check the node only when it is <see cref="SignatureStatus.Known"/>.
    /// <see cref="SignatureStatus.Failed"/> (the service has no such version, or does not describe itself) is already
    /// reported at <paramref name="configKey"/>: the node fails. <see cref="SignatureStatus.Pending"/> (the engine is
    /// still loading the client) and <see cref="SignatureStatus.Unchecked"/> (the plan is compiled without loading its
    /// clients, as by <c>validate</c>) are no error: compile without the check.
    /// </summary>
    public SignatureLookup Signature(BoundService service, string? version, string configKey)
    {
        ArgumentNullException.ThrowIfNull(service);
        var lookup = _clients.Signature(service.Client, service.Name, version);
        if (lookup.Status == SignatureStatus.Failed)
        {
            Error(configKey, "SERVICE_SIGNATURE", lookup.Error!, lookup.Hint);
        }

        return lookup;
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
    /// Resolves a name the operator reads outside an expression (<c>col.x</c>, <c>ctx.request.y</c>, <c>param.z</c>)
    /// and records the read, as <see cref="CompileExpression"/>, <see cref="TryReadColumn"/> and <see cref="TryParam"/>
    /// do for theirs: the fetch planner then fetches a feature before the node runs, and data-flow validation and
    /// experiment footprints see the read.
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

        if (ReadsOwnOutput(symbol, string.Empty))
        {
            reported = true;
            return false;
        }

        RecordSymbol(symbol);
        return true;
    }

    /// <summary>
    /// A column that nothing declared before a write of this node has no earlier version at all, and a read of it would
    /// be missing on every row, whatever the write computes. Refused here, where one compile sees it; a column another
    /// arm combination declares is judged per combination, by <see cref="CompiledNode.Requires"/>.
    /// </summary>
    private bool ReadsOwnOutput(in Symbol symbol, string configKey)
    {
        if (symbol.Kind != SymbolKind.Column || !_firstDeclared.Contains(symbol.Id))
        {
            return false;
        }

        var name = _schema.Columns[symbol.Id].Name;
        Error(configKey, "COLUMN_OWN_OUTPUT",
            $"Column '{name}' is written by this node and by nothing before it: a node reads what was committed before it ran, so there is nothing to read.",
            $"read '{name}' in a node after this one (the same operator, at a later order), or produce it in a node before");
        return true;
    }

    internal void Require(in Symbol symbol)
    {
        if (symbol.Kind is SymbolKind.Column or SymbolKind.Param or SymbolKind.Context)
        {
            Require(new NameRef(symbol.Kind, symbol.Id));
        }
    }

    private void Require(NameRef name) => _requires.Add(name);

    internal void RecordSymbol(in Symbol symbol)
    {
        Require(symbol);
        switch (symbol.Kind)
        {
            case SymbolKind.Column:
                _readColumns.Add(symbol.Id);
                var column = _schema.Columns[symbol.Id];
                if (!(column.Kind == ColumnKind.Output && symbol.FixedSlot >= 0))
                {
                    _currentReads.Add(symbol.Id);
                }

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
                    (_awaiting ? _awaitedFeatures : _readFeatures).Add(field.FeatureIndex);
                }
                else if (field.Kind == ContextFieldKind.RequestColumn)
                {
                    _requestReads.Add(field.Id);
                }

                break;
            case SymbolKind.Param:
                _readParams.Add(symbol.Id);
                break;
        }
    }
}

/// <summary>
/// The context features some awaited expressions read (<see cref="OperatorCompileContext.AwaitedReadsOf"/>), which an
/// operator waits for while it runs, with <see cref="OperatorContext.WhenEachReadyAsync"/>.
/// </summary>
public sealed class AwaitedReads
{
    internal AwaitedReads(ImmutableArray<int> features) => Features = features;

    /// <summary>Nothing to wait for: the expressions read request inputs and parameters only.</summary>
    public static AwaitedReads None { get; } = new(ImmutableArray<int>.Empty);

    public bool IsEmpty => Features.IsEmpty;

    /// <summary>The features, by index in the plan's feature list, ascending.</summary>
    internal ImmutableArray<int> Features { get; }
}

/// <param name="Directory">The pipelines the plan may call, when the compile knows them; null checks calls when they are made.</param>
internal readonly record struct CallerOfPipelines(string PipelineId, RequestSchema Inputs, IPipelineDirectory? Directory, PlanEnvironment Environment);
