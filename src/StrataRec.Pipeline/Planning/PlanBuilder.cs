using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Pipeline.Planning;

/// <summary>Binary-side capabilities and environment used to compile plans.</summary>
public sealed class PlanEnvironment
{
    public OperatorRegistry Operators { get; init; } = OperatorRegistry.CreateDefault();

    public FeatureConnectorRegistry Connectors { get; init; } = FeatureConnectorRegistry.CreateDefault();

    public ModelClientRegistry Models { get; init; } = ModelClientRegistry.CreateDefault();

    public CandidateSourceRegistry CandidateSources { get; init; } = CandidateSourceRegistry.CreateDefault();

    /// <summary>Directory that relative fixture paths in bindings resolve against (the config root).</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>Capability ids this binary provides (design 10.4).</summary>
    public List<string> Capabilities()
    {
        var caps = new List<string>(Operators.Ids);
        caps.AddRange(Connectors.Types);
        caps.AddRange(Models.Types);
        caps.AddRange(CandidateSources.Types);
        caps.Add(ExpressionCompiler.FunctionSetCapability);
        caps.Sort(StringComparer.Ordinal);
        return caps;
    }
}

/// <summary>
/// The schema-level inputs of a plan: the baseline plus every add-only entry contributed by experiment arms
/// (design 11.4). Collections are keyed by stable ids.
/// </summary>
public sealed class SchemaSources
{
    public required PipelineArtifact Pipeline { get; init; }

    public required FeatureCatalogArtifact Catalog { get; init; }

    public required SourceBindingArtifact Bindings { get; init; }

    public IReadOnlyList<DataTableArtifact> Tables { get; init; } = Array.Empty<DataTableArtifact>();

    /// <summary>Columns declared by arms (add-only), in addition to <c>Pipeline.Columns</c>.</summary>
    public IReadOnlyDictionary<string, ColumnSpec> ExtraColumns { get; init; } = new Dictionary<string, ColumnSpec>();

    /// <summary>Parameter schema entries declared by arms (add-only).</summary>
    public IReadOnlyDictionary<string, ParamSpec> ExtraParamSchema { get; init; } = new Dictionary<string, ParamSpec>();

    /// <summary>Leaves only some arm sets (new keys of map parameters, arm-declared parameters): leaf → map/param name.</summary>
    public IReadOnlyCollection<string> ExtraParamLeaves { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Compiles configuration into the pieces of an <see cref="ExecutionPlan"/>: the schema, node variants, the output
/// and the feature plan. The unified (multi-experiment) compiler drives it; with no experiments it compiles a plain
/// pipeline, which also serves as the reference implementation for differential tests (design 16.2).
/// </summary>
public sealed class PlanBuilder
{
    private readonly PlanEnvironment _environment;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<CompiledNode> _nodes = new();
    private readonly List<FeatureRuntimeInfo> _features = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PlanSchema? _schema;
    private IExpressionScope? _scope;
    private ClientResolver? _clients;
    private SchemaSources? _sources;
    private Dictionary<string, ContextField>? _requestInputs;

    public PlanBuilder(PlanEnvironment environment, DiagnosticBag diagnostics)
    {
        _environment = environment;
        _diagnostics = diagnostics;
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public PlanSchema Schema => _schema ?? throw new InvalidOperationException("Call DefineSchema first.");

    public IReadOnlyList<CompiledNode> Nodes => _nodes;

    private string PipelineLabel => "pipeline:" + _sources!.Pipeline.PipelineId;

    /// <summary>Builds the plan schema: request inputs, features, columns, parameters and lookup tables.</summary>
    public PlanSchema DefineSchema(SchemaSources sources)
    {
        _sources = sources;
        var pipeline = sources.Pipeline;
        var keyType = pipeline.EntityKeyType switch
        {
            "int64" => EntityKeyType.Int64,
            "string" => EntityKeyType.String,
            _ => (EntityKeyType)0,
        };
        if (keyType == 0)
        {
            _diagnostics.Error("PIPELINE_BAD_KEY_TYPE", PipelineLabel, "/entity_key_type", $"Unknown entity key type '{pipeline.EntityKeyType}'.", "use int64 or string");
            keyType = EntityKeyType.Int64;
        }

        var schema = new PlanSchema(keyType);
        _schema = schema;
        _scope = schema.CreateScope();
        _clients = new ClientResolver(sources.Bindings, _environment.Models, _environment.CandidateSources, _environment.BaseDirectory);

        // Request inputs.
        _requestInputs = new Dictionary<string, ContextField>(StringComparer.Ordinal);
        var inputTypes = new Dictionary<string, ColumnType>(StringComparer.Ordinal);
        foreach (var name in Sorted(pipeline.RequestSchema.Keys))
        {
            if (!ColumnType.TryParse(pipeline.RequestSchema[name].Type, out var type, out var error))
            {
                _diagnostics.Error("REQUEST_BAD_TYPE", PipelineLabel, "/request_schema/" + name + "/type", error);
                continue;
            }

            var field = schema.AddRequestInput(name, type.Promoted());
            _requestInputs[name] = field;
            inputTypes[name] = type;
        }

        DefineFeatures(sources, inputTypes);
        DefineColumns(pipeline.Columns, "/columns/", PipelineLabel);
        DefineColumns(sources.ExtraColumns, "/columns/", "arms");
        DefineParams(pipeline, sources);

        foreach (var table in sources.Tables)
        {
            try
            {
                schema.AddLookupTable(DataLookupTable.Create(table));
            }
            catch (FormatException ex)
            {
                _diagnostics.Error("TABLE_INVALID", "table:" + table.TableId, "/entries", ex.Message);
            }
        }

        return schema;
    }

    /// <summary>
    /// Compiles one node definition (a fully merged <c>/nodes/&lt;id&gt;</c> object) into a variant. Returns null after
    /// reporting errors.
    /// </summary>
    public CompiledNode? CompileNode(string nodeId, JsonObject definition, string artifact)
    {
        var schema = Schema;
        var pointer = "/nodes/" + nodeId;
        NodeSpec spec;
        try
        {
            spec = definition.Deserialize<NodeSpec>(ArtifactSerializer.Options) ?? throw new JsonException("node must be an object");
        }
        catch (JsonException ex)
        {
            _diagnostics.Error("NODE_INVALID", artifact, pointer, ex.Message, "a node needs \"op\" and \"order\" (or \"role\": \"fallback\")");
            return null;
        }

        if (!_environment.Operators.TryGet(spec.Op, out var factory))
        {
            _diagnostics.Error("OPERATOR_UNKNOWN", artifact, pointer + "/op", $"Operator '{spec.Op}' is not available in this binary.",
                NameSuggestions.DidYouMean(spec.Op, _environment.Operators.Ids) ?? "run 'stratarec operators' to list operators");
            return null;
        }

        var ok = true;
        var isFallback = spec.Role == "fallback";
        if (spec.Role is not (null or "fallback"))
        {
            _diagnostics.Error("NODE_BAD_ROLE", artifact, pointer + "/role", $"Unknown role '{spec.Role}'.", "the only role is 'fallback'");
            ok = false;
        }

        if (isFallback && spec.Order is not null)
        {
            _diagnostics.Error("NODE_FALLBACK_ORDER", artifact, pointer + "/order", "Fallback nodes are not on the timeline and must not have an order.");
            ok = false;
        }
        else if (!isFallback && spec.Order is null)
        {
            _diagnostics.Error("NODE_MISSING_ORDER", artifact, pointer, "Node needs an integer 'order'.", "orders are spaced by 100; insert between neighbours (e.g. 450)");
            ok = false;
        }

        var policy = FailurePolicy.FailRequest;
        string? fallbackId = null;
        switch (spec.OnFailure)
        {
            case null or "fail_request": break;
            case "skip": policy = FailurePolicy.Skip; break;
            case var s when s.StartsWith("fallback:", StringComparison.Ordinal) && s.Length > 9:
                policy = FailurePolicy.Fallback;
                fallbackId = s[9..];
                if (isFallback)
                {
                    _diagnostics.Error("NODE_NESTED_FALLBACK", artifact, pointer + "/on_failure", "Fallbacks have one level: a fallback's on_failure must be fail_request or skip.");
                    ok = false;
                }

                break;
            default:
                _diagnostics.Error("NODE_BAD_ON_FAILURE", artifact, pointer + "/on_failure", $"Unknown on_failure '{spec.OnFailure}'.", "use fail_request, skip or fallback:<node_id>");
                ok = false;
                break;
        }

        var compact = spec.Compact switch
        {
            null or "auto" => CompactMode.Auto,
            "always" => CompactMode.Always,
            "never" => CompactMode.Never,
            _ => (CompactMode)0,
        };
        if (compact == 0)
        {
            _diagnostics.Error("NODE_BAD_COMPACT", artifact, pointer + "/compact", $"Unknown compact mode '{spec.Compact}'.", "use auto, always or never");
            ok = false;
        }

        if (spec.TimeoutMs is <= 0 || spec.MinRemainingMs is < 0 || spec.FallbackReserveMs is < 0)
        {
            _diagnostics.Error("NODE_BAD_TIMING", artifact, pointer, "timeout_ms must be positive; min_remaining_ms and fallback_reserve_ms must not be negative.");
            ok = false;
        }

        CompiledExpression? when = null;
        if (spec.When is not null)
        {
            if (!ExpressionCompiler.TryCompile(spec.When, _scope!, CompileOptions.RequestCondition, out when, out var whenErrors))
            {
                foreach (var d in whenErrors)
                {
                    _diagnostics.Error(d.Code, artifact, pointer + "/when", $"{d.Message} (at offset {d.Offset} in '{spec.When}')", d.Hint);
                }

                ok = false;
            }
        }

        var context = new OperatorCompileContext(schema, _scope!, _clients!, _diagnostics, artifact, nodeId, pointer, spec.Config ?? new JsonObject(), factory.Descriptor);
        var op = factory.Compile(context);
        if (op is null || context.HasErrors || !ok)
        {
            return null;
        }

        if (when is not null)
        {
            foreach (var symbol in when.Reads)
            {
                context.RecordSymbol(symbol);
            }
        }

        var effects = factory.Descriptor.Effects;
        var node = new CompiledNode
        {
            NodeId = nodeId,
            Index = _nodes.Count,
            Pointer = pointer,
            Definition = definition,
            DefinitionHash = CanonicalJson.Hash(definition),
            Descriptor = factory.Descriptor,
            Operator = op,
            Order = spec.Order,
            IsFallbackRole = isFallback,
            When = when,
            TimeoutMs = spec.TimeoutMs,
            MinRemainingMs = spec.MinRemainingMs,
            OnFailure = policy,
            FallbackNodeId = fallbackId,
            FallbackReserveMs = spec.FallbackReserveMs ?? 0,
            FetchBarrier = (effects & (Effects.RowSource | Effects.RowFilter)) != 0 && (spec.FetchBarrier ?? true),
            Compact = compact,
            ReadColumns = ToSortedArray(context.ReadColumns),
            ReadFeatures = ToSortedArray(context.ReadFeatures),
            RequiredOutputs = ToSortedArray(context.RequiredOutputs),
            ReadParams = ToSortedArray(context.ReadParams),
            Outputs = ToArray(context.Outputs),
        };
        _nodes.Add(node);
        return node;
    }

    /// <summary>Compiles the output section (design 3.4).</summary>
    public OutputPlan? CompileOutput()
    {
        var output = _sources!.Pipeline.Output;
        var ok = true;
        ParamRef limit = default;
        if (!Schema.TryGetParam(output.LimitParam, out var leaf) || leaf.Type.Kind != StorageKind.Int64)
        {
            _diagnostics.Error("OUTPUT_BAD_LIMIT", PipelineLabel, "/output/limit_param", $"'{output.LimitParam}' must be a declared int64 parameter.");
            ok = false;
        }
        else
        {
            limit = new ParamRef(leaf.Id, leaf.Name, leaf.Type);
        }

        var columns = new List<(string, CompiledExpression)>();
        foreach (var name in Sorted(output.Columns.Keys))
        {
            var source = output.Columns[name];
            if (!ExpressionCompiler.TryCompile(source, _scope!, CompileOptions.Any, out var expression, out var errors))
            {
                foreach (var d in errors)
                {
                    _diagnostics.Error(d.Code, PipelineLabel, "/output/columns/" + name, $"{d.Message} (at offset {d.Offset} in '{source}')", d.Hint);
                }

                ok = false;
                continue;
            }

            columns.Add((name, expression));
        }

        return ok ? new OutputPlan { Limit = limit, Columns = columns.ToArray() } : null;
    }

    /// <summary>
    /// Static data-flow check of one fully merged node set (design 4.6, 5.3, 11.5): every non-nullable column a node
    /// reads has a producer that cannot be skipped before it; fixed columns have one producer per path; row sources
    /// after a producer write that column; nodes sharing an order are independent.
    /// </summary>
    public void ValidateDataFlow(IReadOnlyList<CompiledNode> timeline, IReadOnlyDictionary<string, CompiledNode> fallbacks, OutputPlan? output, string artifact)
    {
        var schema = Schema;
        var guaranteed = new HashSet<int>();
        var possible = new Dictionary<int, string>();
        var ordered = new List<CompiledNode>(timeline);
        ordered.Sort(static (a, b) => a.Order != b.Order ? a.Order!.Value.CompareTo(b.Order!.Value) : string.CompareOrdinal(a.NodeId, b.NodeId));

        for (var i = 0; i < ordered.Count; i++)
        {
            var node = ordered[i];

            // Same order ⇒ no dependency (design 5.3).
            for (var j = i + 1; j < ordered.Count && ordered[j].Order == node.Order; j++)
            {
                if (DependsOn(node, ordered[j]) || DependsOn(ordered[j], node))
                {
                    _diagnostics.Error("ORDER_DEPENDENCY", artifact, ordered[j].Pointer + "/order",
                        $"Nodes '{node.NodeId}' and '{ordered[j].NodeId}' share order {node.Order} but depend on each other.",
                        "give them different orders");
                }
            }

            foreach (var logical in node.RequiredOutputs)
            {
                if (!guaranteed.Contains(logical))
                {
                    var column = schema.Columns[logical];
                    var why = possible.TryGetValue(logical, out var producer)
                        ? $"its producer '{producer}' may be skipped (when, min_remaining_ms or on_failure: skip)"
                        : "no earlier node produces it";
                    _diagnostics.Error("COLUMN_NO_PRODUCER", artifact, node.Pointer,
                        $"Node '{node.NodeId}' reads non-nullable column '{column.Name}', but {why}.",
                        "make the producer unskippable or give it a fallback, or declare the column nullable and handle missing values with '??'");
                }
            }

            CompiledNode? fallback = null;
            if (node.OnFailure == FailurePolicy.Fallback)
            {
                if (!fallbacks.TryGetValue(node.FallbackNodeId!, out fallback))
                {
                    _diagnostics.Error("FALLBACK_UNKNOWN", artifact, node.Pointer + "/on_failure",
                        $"Fallback node '{node.FallbackNodeId}' does not exist or is not declared with \"role\": \"fallback\".");
                }
                else if (!SameOutputs(node, fallback))
                {
                    _diagnostics.Error("FALLBACK_OUTPUTS", artifact, fallback.Pointer,
                        $"Fallback '{fallback.NodeId}' must produce exactly the columns of '{node.NodeId}'.");
                }
            }

            var unskippable = node.When is null && node.MinRemainingMs is null
                && (node.OnFailure == FailurePolicy.FailRequest || (fallback is not null && fallback.OnFailure == FailurePolicy.FailRequest));

            if ((node.Effects & Effects.RowSource) != 0)
            {
                foreach (var logical in possible.Keys)
                {
                    var column = schema.Columns[logical];
                    if (!column.Type.Nullable && !Writes(node, logical))
                    {
                        _diagnostics.Error("ROW_SOURCE_AFTER_COLUMN", artifact, node.Pointer,
                            $"Row source '{node.NodeId}' appends rows after non-nullable column '{column.Name}' is produced but does not write it.",
                            "move the row source earlier, write the column, or declare the column nullable");
                    }
                }
            }

            foreach (var output2 in node.Outputs)
            {
                var column = schema.Columns[output2.LogicalId];
                if (possible.TryGetValue(output2.LogicalId, out var previous) && !column.Flow && (node.Effects & Effects.RowSource) == 0)
                {
                    _diagnostics.Error("COLUMN_MULTIPLE_PRODUCERS", artifact, node.Pointer,
                        $"Column '{column.Name}' is produced by both '{previous}' and '{node.NodeId}' on the same path.",
                        "declare it with \"flow\": true to allow rewriting, or write a new column");
                }

                possible[output2.LogicalId] = node.NodeId;
                if (unskippable)
                {
                    guaranteed.Add(output2.LogicalId);
                }
            }
        }

        if (output is not null)
        {
            foreach (var (name, expression) in output.Columns)
            {
                foreach (var symbol in expression.Reads)
                {
                    if (symbol is { Kind: SymbolKind.Column, FixedSlot: < 0 } && !symbol.Type.Nullable && !guaranteed.Contains(symbol.Id))
                    {
                        _diagnostics.Error("COLUMN_NO_PRODUCER", artifact, "/output/columns/" + name,
                            $"Output reads non-nullable column '{schema.Columns[symbol.Id].Name}', which is not guaranteed to be produced.");
                    }
                }
            }
        }
    }

    /// <summary>Builds the feature plan from the features registered by <see cref="DefineSchema"/>.</summary>
    public FeaturePlan BuildFeaturePlan()
    {
        var inputs = _requestInputs!;
        return FeaturePlan.Create(
            _features,
            _sources!.Bindings,
            _environment.Connectors,
            name => inputs.TryGetValue(name, out var f) ? f.Id : -1,
            _diagnostics,
            _environment.BaseDirectory);
    }

    /// <summary>Assembles the final plan. Call after every node, the output and the feature plan are compiled.</summary>
    public ExecutionPlan Build(
        string manifestId,
        FeaturePlan features,
        TimelineSlot[] timeline,
        IReadOnlyDictionary<string, TimelineSlot> fallbacks,
        OutputPlan output,
        ParamTable parameters,
        LayerInfo[] layers,
        ExperimentInfo[] experiments,
        ArmInfo[] arms)
    {
        var schema = Schema;

        // @node version references must name a node that writes the column (design 4.6).
        foreach (var (nodeId, logical) in schema.VersionReferences)
        {
            var found = false;
            foreach (var node in _nodes)
            {
                if (node.NodeId != nodeId)
                {
                    continue;
                }

                foreach (var o in node.Outputs)
                {
                    found |= o.LogicalId == logical;
                }
            }

            if (!found)
            {
                _diagnostics.Error("VERSION_REF_UNKNOWN", PipelineLabel, "/",
                    $"'col.{schema.Columns[logical].Name}@{nodeId}' refers to a node that never writes that column.");
            }
        }

        var contextFeatures = new HashSet<int>();
        foreach (var f in features.Features)
        {
            if (f.IsContext)
            {
                contextFeatures.Add(f.Index);
            }
        }

        return new ExecutionPlan
        {
            PipelineId = _sources!.Pipeline.PipelineId,
            ManifestId = manifestId,
            Schema = schema,
            TableSchema = schema.CreateTableSchema(),
            Features = features,
            Timeline = timeline,
            Fallbacks = fallbacks,
            Nodes = _nodes.ToArray(),
            Output = output,
            Params = parameters,
            Layers = layers,
            Experiments = experiments,
            Arms = arms,
            ContextFeatures = ToSortedArray(contextFeatures),
            Warnings = _diagnostics.Items,
            CompileTime = _clock.Elapsed,
            RequestInputs = _requestInputs!,
        };
    }

    /// <summary>Parses a parameter value for a leaf, reporting errors at <paramref name="pointer"/>.</summary>
    public bool TryParamValue(ParamLeaf leaf, JsonNode? node, string artifact, string pointer, out ScalarValue value)
    {
        if (!ConfigValues.TryToScalar(node, leaf.Type, out value, out var error))
        {
            _diagnostics.Error("PARAM_BAD_VALUE", artifact, pointer, $"Parameter '{leaf.Name}': {error}.");
            return false;
        }

        var invalid = leaf.Validate(value);
        if (invalid is not null)
        {
            _diagnostics.Error("PARAM_OUT_OF_RANGE", artifact, pointer, $"Parameter '{leaf.Name}': {invalid}.",
                "experiments cannot widen a baseline parameter's range (design 11.8)");
            return false;
        }

        return true;
    }

    private void DefineFeatures(SchemaSources sources, Dictionary<string, ColumnType> inputTypes)
    {
        var schema = Schema;
        var catalog = FeatureCatalog.Build(sources.Catalog, inputTypes, _diagnostics, "catalog:" + sources.Catalog.CatalogId);
        var pendingDerived = new List<FeatureDefinition>();
        foreach (var feature in catalog.Features)
        {
            if (feature.IsDerived)
            {
                pendingDerived.Add(feature);
                continue;
            }

            if (schema.TryGetColumn(feature.Name, out _))
            {
                _diagnostics.Error("FEATURE_DUPLICATE", "catalog", feature.Pointer, $"'{feature.Name}' collides with another column.");
                continue;
            }

            RegisterFeature(feature, feature.Type, null, Array.Empty<int>());
        }

        // Derived features may reference each other: compile in dependency order by retrying until no progress.
        var progress = true;
        var lastErrors = new Dictionary<string, IReadOnlyList<ExpressionDiagnostic>>(StringComparer.Ordinal);
        while (pendingDerived.Count > 0 && progress)
        {
            progress = false;
            for (var i = 0; i < pendingDerived.Count; i++)
            {
                var feature = pendingDerived[i];
                var options = new CompileOptions
                {
                    ExpectedType = feature.Type.AsNonNullable(),
                    AllowNullableResult = true,
                    RequestLevelOnly = feature.IsContext,
                };
                if (!ExpressionCompiler.TryCompile(feature.Expr!, _scope!, options, out var expression, out var errors))
                {
                    lastErrors[feature.Name] = errors;
                    continue;
                }

                var type = feature.HasDefault ? feature.Type.AsNonNullable() : feature.Type.WithNullable(expression.ResultType.Nullable);
                var dependencies = new List<int>();
                foreach (var symbol in expression.Reads)
                {
                    var index = FeatureIndexOf(symbol);
                    if (index >= 0)
                    {
                        dependencies.Add(index);
                    }
                    else if (symbol.Kind == SymbolKind.Param || (symbol.Kind == SymbolKind.Column && schema.Columns[symbol.Id].Kind == ColumnKind.Output))
                    {
                        _diagnostics.Error("FEATURE_BAD_REFERENCE", "catalog", feature.Pointer + "/expr",
                            $"Derived feature '{feature.Name}' reads '{symbol.Name}'; derived features may only read features, request inputs and col.entity_id.");
                    }
                }

                RegisterFeature(feature, type, expression, dependencies.ToArray());
                pendingDerived.RemoveAt(i);
                i--;
                progress = true;
            }
        }

        foreach (var feature in pendingDerived)
        {
            foreach (var d in lastErrors[feature.Name])
            {
                _diagnostics.Error(d.Code, "catalog", feature.Pointer + "/expr", $"{d.Message} (at offset {d.Offset} in '{feature.Expr}')", d.Hint);
            }
        }
    }

    private void RegisterFeature(FeatureDefinition feature, ColumnType type, CompiledExpression? expression, int[] dependencies)
    {
        var index = _features.Count;
        int slot = -1, contextId = -1;
        if (feature.IsContext)
        {
            contextId = Schema.AddContextFeature(feature.Name, type.Promoted(), index).Id;
        }
        else
        {
            slot = Schema.AddFeatureColumn(feature.Name, type, index, feature.Pointer).FixedSlot;
        }

        _features.Add(new FeatureRuntimeInfo
        {
            Index = index,
            Definition = feature,
            Type = type,
            Slot = slot,
            ContextId = contextId,
            Expression = expression,
            Dependencies = dependencies,
        });
    }

    private int FeatureIndexOf(in Symbol symbol)
    {
        if (symbol.Kind == SymbolKind.Column)
        {
            return Schema.Columns[symbol.Id].FeatureIndex;
        }

        return symbol.Kind == SymbolKind.Context ? Schema.ContextFields[symbol.Id].FeatureIndex : -1;
    }

    private void DefineColumns(IReadOnlyDictionary<string, ColumnSpec> columns, string pointerPrefix, string artifact)
    {
        var schema = Schema;
        foreach (var name in Sorted(columns.Keys))
        {
            var spec = columns[name];
            var pointer = pointerPrefix + name;
            if (schema.TryGetColumn(name, out _))
            {
                _diagnostics.Error("COLUMN_DUPLICATE", artifact, pointer, $"Column '{name}' is already declared (or is a feature).");
                continue;
            }

            if (!ColumnType.TryParse(spec.Type, out var type, out var error))
            {
                _diagnostics.Error("COLUMN_BAD_TYPE", artifact, pointer + "/type", error);
                continue;
            }

            var hasDefault = spec.Default is not null;
            var value = ScalarValue.Missing;
            if (hasDefault && !ConfigValues.TryToScalar(spec.Default, type.AsNonNullable(), out value, out var defaultError))
            {
                _diagnostics.Error("COLUMN_BAD_DEFAULT", artifact, pointer + "/default", defaultError);
                continue;
            }

            if (!type.Nullable && type.IsNumeric && !hasDefault)
            {
                _diagnostics.Error("COLUMN_NEEDS_DEFAULT", artifact, pointer,
                    $"Non-nullable numeric column '{name}' needs a 'default' (it replaces NaN/±∞ writes, design 4.3).",
                    "add \"default\": 0.0, or declare the type nullable");
                continue;
            }

            schema.DeclareOutputColumn(name, type, spec.Flow, hasDefault, value, pointer);
        }
    }

    private void DefineParams(PipelineArtifact pipeline, SchemaSources sources)
    {
        var schema = Schema;
        var specs = new Dictionary<string, (ParamSpec Spec, string Artifact)>(StringComparer.Ordinal);
        foreach (var (name, spec) in pipeline.ParamSchema)
        {
            specs[name] = (spec, PipelineLabel);
        }

        foreach (var (name, spec) in sources.ExtraParamSchema)
        {
            if (!specs.ContainsKey(name))
            {
                specs[name] = (spec, "arms");
            }
        }

        foreach (var name in Sorted(specs.Keys))
        {
            var (spec, artifact) = specs[name];
            var pointer = "/param_schema/" + name;
            var isMap = spec.Type.StartsWith("map<string,", StringComparison.Ordinal) && spec.Type.EndsWith('>');
            var leafTypeText = isMap ? spec.Type["map<string,".Length..^1] : spec.Type;
            if (!ColumnType.TryParse(leafTypeText, out var leafType, out var error)
                || leafType.Nullable
                || leafType.Kind is not (StorageKind.Int64 or StorageKind.Float64 or StorageKind.Bool or StorageKind.String))
            {
                _diagnostics.Error("PARAM_BAD_TYPE", artifact, pointer + "/type", error ?? $"Unsupported parameter type '{spec.Type}'.",
                    "use int64, float64, bool, string or map<string,T>");
                continue;
            }

            List<ScalarValue>? enumValues = null;
            if (spec.Enum is not null)
            {
                enumValues = new List<ScalarValue>();
                foreach (var e in spec.Enum)
                {
                    if (ConfigValues.TryToScalar(e, leafType, out var ev, out var enumError))
                    {
                        enumValues.Add(ev);
                    }
                    else
                    {
                        _diagnostics.Error("PARAM_BAD_ENUM", artifact, pointer + "/enum", enumError);
                    }
                }
            }

            var value = pipeline.Params[name];
            if (isMap)
            {
                var keys = new SortedSet<string>(StringComparer.Ordinal);
                if (value is JsonObject map)
                {
                    foreach (var (key, _) in map)
                    {
                        keys.Add(key);
                    }
                }
                else if (value is not null)
                {
                    _diagnostics.Error("PARAM_BAD_VALUE", PipelineLabel, "/params/" + name, $"Map parameter '{name}' needs an object value.");
                    continue;
                }

                foreach (var extra in sources.ExtraParamLeaves)
                {
                    if (extra.StartsWith(name + ".", StringComparison.Ordinal))
                    {
                        keys.Add(extra[(name.Length + 1)..]);
                    }
                }

                foreach (var key in keys)
                {
                    var leafName = name + "." + key;
                    var leaf = schema.AddParamLeaf(leafName, leafType, spec.Min, spec.Max, enumValues, ScalarValue.Missing, pointer, name);
                    var leafValue = value is JsonObject m ? m[key] : null;
                    if (leafValue is not null && TryParamValue(leaf, leafValue, PipelineLabel, "/params/" + name + "/" + key, out var parsed))
                    {
                        leaf.Baseline = parsed;
                    }
                }
            }
            else
            {
                var leaf = schema.AddParamLeaf(name, leafType, spec.Min, spec.Max, enumValues, ScalarValue.Missing, pointer);
                if (value is null)
                {
                    if (artifact == PipelineLabel)
                    {
                        _diagnostics.Error("PARAM_NO_VALUE", PipelineLabel, "/params/" + name, $"Parameter '{name}' is declared but has no baseline value.");
                    }
                }
                else if (TryParamValue(leaf, value, PipelineLabel, "/params/" + name, out var parsed))
                {
                    leaf.Baseline = parsed;
                }
            }
        }

        foreach (var (name, _) in pipeline.Params)
        {
            if (!specs.ContainsKey(name))
            {
                _diagnostics.Error("PARAM_UNDECLARED", PipelineLabel, "/params/" + name, $"Parameter '{name}' has a value but no param_schema entry.",
                    NameSuggestions.DidYouMean(name, specs.Keys) ?? $"declare it under /param_schema/{name}");
            }
        }
    }

    private static bool DependsOn(CompiledNode reader, CompiledNode writer)
    {
        if (reader.HasRowEffects || writer.HasRowEffects)
        {
            return true;
        }

        foreach (var output in writer.Outputs)
        {
            if (Array.IndexOf(reader.ReadColumns, output.LogicalId) >= 0 || Writes(reader, output.LogicalId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Writes(CompiledNode node, int logical)
    {
        foreach (var o in node.Outputs)
        {
            if (o.LogicalId == logical)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameOutputs(CompiledNode a, CompiledNode b)
    {
        if (a.Outputs.Length != b.Outputs.Length)
        {
            return false;
        }

        foreach (var o in a.Outputs)
        {
            if (!Writes(b, o.LogicalId))
            {
                return false;
            }
        }

        return true;
    }

    internal static List<string> Sorted(IEnumerable<string> keys)
    {
        var list = new List<string>(keys);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static int[] ToSortedArray(IReadOnlyCollection<int> values)
    {
        var array = new int[values.Count];
        var i = 0;
        foreach (var v in values)
        {
            array[i++] = v;
        }

        Array.Sort(array);
        return array;
    }

    private static T[] ToArray<T>(IReadOnlyList<T> values)
    {
        var array = new T[values.Count];
        for (var i = 0; i < array.Length; i++)
        {
            array[i] = values[i];
        }

        return array;
    }
}
