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

    /// <summary>Funnel groups declared by arms (add-only, design 8.3).</summary>
    public IReadOnlyDictionary<string, FunnelGroupSpec> ExtraFunnelGroups { get; init; } = new Dictionary<string, FunnelGroupSpec>();
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
    private readonly List<CompiledFunnelGroup> _groups = new();
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

    public IReadOnlyList<CompiledFunnelGroup> FunnelGroups => _groups;

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

            var field = schema.AddRequestInput(name, type);
            _requestInputs[name] = field;
            inputTypes[name] = type;
        }

        DefineFeatures(sources, inputTypes);
        DefineColumns(pipeline.Columns, "/columns/", PipelineLabel);
        DefineColumns(sources.ExtraColumns, "/columns/", "arms");
        DefineParams(pipeline, sources);

        DefineFunnelGroups(pipeline.FunnelGroups, PipelineLabel);
        DefineFunnelGroups(sources.ExtraFunnelGroups, "arms");

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

    /// <summary>
    /// Compiles one log tap definition (a fully merged <c>/log_taps/&lt;id&gt;</c> object) into a variant (design 8.3).
    /// Call after the nodes are compiled: <c>columns_from</c> expands a compiled model node's inputs. Returns null
    /// after reporting errors.
    /// </summary>
    /// <param name="resolveNode">
    /// Resolves a node id to the variant in effect where this tap variant runs (the unified plan compiles a tap per arm
    /// combination, design 12.3); <c>columns_from</c> expands that variant's inputs. Defaults to the only compiled node
    /// with the id.
    /// </param>
    public CompiledTap? CompileTap(string tapId, JsonObject definition, string artifact, Func<string, CompiledNode?>? resolveNode = null)
    {
        var pointer = "/log_taps/" + tapId;
        LogTapSpec spec;
        try
        {
            spec = definition.Deserialize<LogTapSpec>(ArtifactSerializer.Options) ?? throw new JsonException("tap must be an object");
        }
        catch (JsonException ex)
        {
            _diagnostics.Error("TAP_INVALID", artifact, pointer + ArtifactSerializer.JsonPathToPointer(ex.Path), ex.Message,
                "a tap needs \"group\" and \"attach\"; run 'stratarec schema pipeline' for the full schema");
            return null;
        }

        var ok = true;
        var group = -1;
        foreach (var g in _groups)
        {
            if (g.Name == spec.Group)
            {
                group = g.Index;
            }
        }

        if (group < 0)
        {
            var names = new List<string>();
            foreach (var g in _groups)
            {
                names.Add(g.Name);
            }

            _diagnostics.Error("TAP_UNKNOWN_GROUP", artifact, pointer + "/group", $"Funnel group '{spec.Group}' is not declared.",
                NameSuggestions.DidYouMean(spec.Group, names) ?? $"declare it under /funnel_groups/{spec.Group}");
            ok = false;
        }

        var delivery = spec.Delivery switch
        {
            null or "best_effort" => Logging.LogDelivery.BestEffort,
            "durable" => Logging.LogDelivery.Durable,
            _ => (Logging.LogDelivery)0,
        };
        if (delivery == 0)
        {
            _diagnostics.Error("TAP_BAD_DELIVERY", artifact, pointer + "/delivery", $"Unknown delivery '{spec.Delivery}'.", "use best_effort or durable");
            ok = false;
        }

        var output = spec.Attach.Output == true;
        if (output == (spec.Attach.After is not null))
        {
            _diagnostics.Error("TAP_BAD_ATTACH", artifact, pointer + "/attach", "A tap attaches either after a node or to the output.",
                "use {\"after\": \"<node_id>\"} or {\"output\": true}");
            ok = false;
        }

        var scope = spec.Rows?.Scope switch
        {
            null or "survived" => TapScope.Survived,
            "entered" => TapScope.Entered,
            "dropped" => TapScope.Dropped,
            _ => (TapScope)0,
        };
        if (scope == 0 || (output && scope != TapScope.Survived))
        {
            _diagnostics.Error("TAP_BAD_SCOPE", artifact, pointer + "/rows/scope", $"Invalid scope '{spec.Rows?.Scope}'.",
                output ? "output taps record the returned rows; omit scope" : "use entered, survived or dropped");
            ok = false;
        }

        if (spec.Rows?.Rate is { } rate && !(rate > 0 && rate <= 1))
        {
            _diagnostics.Error("TAP_BAD_RATE", artifact, pointer + "/rows/rate", "rows.rate must be in (0, 1].");
            ok = false;
        }

        if (spec.Rows?.MaxRows is < 1)
        {
            _diagnostics.Error("TAP_BAD_MAX_ROWS", artifact, pointer + "/rows/max_rows", "rows.max_rows must be at least 1.");
            ok = false;
        }

        if (spec.FetchMissing == true)
        {
            _diagnostics.Error("TAP_FETCH_MISSING_UNSUPPORTED", artifact, pointer + "/fetch_missing",
                "fetch_missing (design 8.5) is not supported by this binary yet.",
                "attach the tap after a node that already reads the feature, or log it from a later tap");
            ok = false;
        }

        var reads = new List<Symbol>();
        CompiledExpression? filter = null;
        if (spec.Rows?.Filter is { } filterSource)
        {
            filter = CompileTapExpression(filterSource, new CompileOptions { ExpectedType = ColumnType.Bool, AllowNullableResult = true }, artifact, pointer + "/rows/filter", reads, ref ok);
        }

        var context = new List<(string, CompiledExpression)>();
        if (spec.Context is not null)
        {
            foreach (var name in Sorted(spec.Context.Keys))
            {
                var e = CompileTapExpression(spec.Context[name], new CompileOptions { RequestLevelOnly = true, AllowNullableResult = true }, artifact, pointer + "/context/" + name, reads, ref ok);
                if (e is not null)
                {
                    context.Add((name, e));
                }
            }
        }

        var sources = new SortedDictionary<string, (string Source, string Pointer)>(StringComparer.Ordinal);
        if (spec.ColumnsFrom is { } from)
        {
            var nodeId = from.EndsWith(".inputs", StringComparison.Ordinal) ? from[..^7] : null;
            CompiledNode? model = null;
            if (nodeId is not null && resolveNode is not null)
            {
                model = resolveNode(nodeId);
            }
            else
            {
                foreach (var n in _nodes)
                {
                    if (n.NodeId == nodeId)
                    {
                        model = n;
                        break;
                    }
                }
            }

            if (model is null || model.Operator.NamedInputs.Count == 0)
            {
                _diagnostics.Error("TAP_BAD_COLUMNS_FROM", artifact, pointer + "/columns_from", $"'{from}' does not name a compiled model node's inputs.",
                    "use '<node_id>.inputs' for a ModelScorer node");
                ok = false;
            }
            else
            {
                foreach (var (name, source) in model.Operator.NamedInputs)
                {
                    sources[name] = (source, pointer + "/columns_from");
                }
            }
        }

        if (spec.Columns is not null)
        {
            foreach (var (name, source) in spec.Columns)
            {
                if (sources.ContainsKey(name))
                {
                    _diagnostics.Error("TAP_DUPLICATE_COLUMN", artifact, pointer + "/columns/" + name, $"Column '{name}' is also produced by columns_from.");
                    ok = false;
                    continue;
                }

                sources[name] = (source, pointer + "/columns/" + name);
            }
        }

        var columns = new List<(string, CompiledExpression)>();
        var columnSources = new List<int>();
        var pinned = new List<string?>();
        var types = new JsonObject();
        foreach (var (name, (source, columnPointer)) in sources)
        {
            var before = reads.Count;
            var e = CompileTapExpression(source, CompileOptions.Any, artifact, columnPointer, reads, ref ok);
            if (e is null)
            {
                continue;
            }

            columns.Add((name, e));
            types[name] = new JsonObject { ["type"] = e.ResultType.ToString(), ["expr"] = source };
            var direct = -1;
            string? pin = null;
            for (var i = before; i < reads.Count; i++)
            {
                if (reads[i] is { Kind: SymbolKind.Column } r && Schema.Columns[r.Id].Kind == ColumnKind.Output)
                {
                    direct = direct == -1 ? r.Id : -2;
                    var at = r.Name.LastIndexOf('@');
                    pin = r.FixedSlot >= 0 && at > 0 ? r.Name[(at + 1)..] : null;
                }
            }

            columnSources.Add(direct >= 0 ? direct : -1);
            pinned.Add(direct >= 0 ? pin : null);
        }

        var readOutputs = new SortedSet<int>();
        var readFeatures = new SortedSet<int>();
        var readContext = new SortedSet<int>();
        var pinnedNodes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var symbol in reads)
        {
            if (symbol.Kind == SymbolKind.Column)
            {
                var column = Schema.Columns[symbol.Id];
                if (column.Kind == ColumnKind.Feature)
                {
                    readFeatures.Add(column.FeatureIndex);
                }
                else if (column.Kind == ColumnKind.Output)
                {
                    if (symbol.FixedSlot < 0)
                    {
                        readOutputs.Add(symbol.Id);
                    }
                    else if (symbol.Name.LastIndexOf('@') is > 0 and var at)
                    {
                        pinnedNodes.Add(symbol.Name[(at + 1)..]);
                    }
                }
            }
            else if (symbol.Kind == SymbolKind.Context && Schema.ContextFields[symbol.Id].FeatureIndex is >= 0 and var f)
            {
                readContext.Add(f);
            }
        }

        if (!ok)
        {
            return null;
        }

        return new CompiledTap
        {
            TapId = tapId,
            Definition = definition,
            Group = group,
            Delivery = delivery,
            AttachAfter = spec.Attach.After,
            Scope = scope,
            Filter = filter,
            Rate = spec.Rows?.Rate ?? 1.0,
            MaxRows = spec.Rows?.MaxRows,
            Context = context.ToArray(),
            Columns = columns.ToArray(),
            SchemaId = CanonicalJson.Hash(new JsonObject { ["tap"] = definition.DeepClone(), ["types"] = types }),
            ReadOutputs = ToSortedArray(readOutputs),
            ReadFeatures = ToSortedArray(readFeatures),
            ReadContextFeatures = ToSortedArray(readContext),
            ColumnSources = columnSources.ToArray(),
            PinnedProducers = pinned.ToArray(),
            PinnedNodes = new List<string>(pinnedNodes).ToArray(),
        };
    }

    private CompiledExpression? CompileTapExpression(string source, CompileOptions options, string artifact, string pointer, List<Symbol> reads, ref bool ok)
    {
        if (ExpressionCompiler.TryCompile(source, _scope!, options, out var expression, out var errors))
        {
            foreach (var symbol in expression.Reads)
            {
                reads.Add(symbol);
            }

            return expression;
        }

        foreach (var d in errors)
        {
            _diagnostics.Error(d.Code, artifact, pointer, $"{d.Message} (at offset {d.Offset} in '{source}')", d.Hint);
        }

        ok = false;
        return null;
    }

    private void DefineFunnelGroups(IReadOnlyDictionary<string, FunnelGroupSpec> groups, string artifact)
    {
        foreach (var name in Sorted(groups.Keys))
        {
            var spec = groups[name];
            var pointer = "/funnel_groups/" + name;
            var exists = false;
            foreach (var g in _groups)
            {
                exists |= g.Name == name;
            }

            if (exists)
            {
                continue; // identical re-add (checked by the experiment compiler)
            }

            if (!(spec.Rate >= 0 && spec.Rate <= 1))
            {
                _diagnostics.Error("FUNNEL_BAD_RATE", artifact, pointer + "/rate", "rate must be in [0, 1].");
                continue;
            }

            if (spec.Key.Count == 0)
            {
                _diagnostics.Error("FUNNEL_NO_KEY", artifact, pointer + "/key", "A funnel group needs at least one key expression.", "e.g. [\"ctx.request.id\"]");
                continue;
            }

            var keys = new List<CompiledExpression>();
            for (var i = 0; i < spec.Key.Count; i++)
            {
                if (ExpressionCompiler.TryCompile(spec.Key[i], _scope!, new CompileOptions { RequestLevelOnly = true, AllowNullableResult = true }, out var key, out var errors))
                {
                    if (ReadsFetchedFeature(key) is { } feature)
                    {
                        // The group decision is made at request start (design 8.2), before any fetch: a fetched key
                        // would make every request wait for I/O just to decide whether to log.
                        _diagnostics.Error("FUNNEL_KEY_FETCHED", artifact, pointer + "/key/" + i,
                            $"Funnel group key '{spec.Key[i]}' reads fetched feature '{feature}'; keys are evaluated at request start, before any fetch.",
                            "key on request inputs, e.g. [\"ctx.request.user_id\", \"ctx.request.session_id\"]");
                        continue;
                    }

                    if (key.ResultType.Kind is not (StorageKind.Bool or StorageKind.Int32 or StorageKind.Int64 or StorageKind.Float32
                        or StorageKind.Float64 or StorageKind.String or StorageKind.Text))
                    {
                        // Sampling hashes the key with the canonical scalar encoding (design 5.7, 8.2).
                        _diagnostics.Error("FUNNEL_KEY_TYPE", artifact, pointer + "/key/" + i,
                            $"Funnel group key '{spec.Key[i]}' has type {key.ResultType}; a key must be a scalar (bool, integer, float or string).",
                            "key on a scalar request input such as ctx.request.user_id");
                        continue;
                    }

                    keys.Add(key);
                    continue;
                }

                foreach (var d in errors)
                {
                    _diagnostics.Error(d.Code, artifact, pointer + "/key/" + i, $"{d.Message} (at offset {d.Offset} in '{spec.Key[i]}')", d.Hint);
                }
            }

            if (keys.Count == spec.Key.Count)
            {
                _groups.Add(new CompiledFunnelGroup
                {
                    Index = _groups.Count,
                    Name = name,
                    Seed = Logging.CoordinatedSampling.GroupSeed(_sources!.Pipeline.PipelineId, name),
                    Rate = spec.Rate,
                    Keys = keys.ToArray(),
                });
            }
        }
    }

    private string? ReadsFetchedFeature(CompiledExpression expression)
    {
        foreach (var symbol in expression.Reads)
        {
            if (FeatureIndexOf(symbol) is >= 0 and var f)
            {
                return _features[f].Definition.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Compiles a request-level condition over <c>ctx.*</c> (design 5.5), e.g. a trigger site's OR of arms'
    /// <c>when</c> (design 12.6). Returns null after reporting errors at <paramref name="pointer"/>.
    /// </summary>
    public CompiledExpression? CompileRequestCondition(string source, string artifact, string pointer)
    {
        if (ExpressionCompiler.TryCompile(source, _scope!, CompileOptions.RequestCondition, out var expression, out var errors))
        {
            return expression;
        }

        foreach (var d in errors)
        {
            _diagnostics.Error(d.Code, artifact, pointer, $"{d.Message} (at offset {d.Offset} in '{source}')", d.Hint);
        }

        return null;
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
    public void ValidateDataFlow(
        IReadOnlyList<CompiledNode> timeline,
        IReadOnlyDictionary<string, CompiledNode> fallbacks,
        OutputPlan? output,
        string artifact,
        IReadOnlyList<CompiledTap>? taps = null)
    {
        var schema = Schema;
        var guaranteed = new HashSet<int>();
        var possible = new Dictionary<int, string>();
        var fetched = new HashSet<int>();
        var ordered = new List<CompiledNode>(timeline);
        ordered.Sort(static (a, b) => a.Order != b.Order ? a.Order!.Value.CompareTo(b.Order!.Value) : string.CompareOrdinal(a.NodeId, b.NodeId));
        var later = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in ordered)
        {
            later.Add(node.NodeId);
        }

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
                else
                {
                    foreach (var logical in fallback.RequiredOutputs)
                    {
                        if (!guaranteed.Contains(logical))
                        {
                            _diagnostics.Error("COLUMN_NO_PRODUCER", artifact, fallback.Pointer,
                                $"Fallback '{fallback.NodeId}' (for '{node.NodeId}') reads non-nullable column '{schema.Columns[logical].Name}', which is not guaranteed to be produced before it.",
                                "make the producer unskippable, or declare the column nullable and handle missing values with '??'");
                        }
                    }
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

            MarkFetched(fetched, node.ReadFeatures);
            if (fallback is not null)
            {
                MarkFetched(fetched, fallback.ReadFeatures);
            }

            later.Remove(node.NodeId);
            if (taps is not null)
            {
                foreach (var tap in taps)
                {
                    if (tap.AttachAfter == node.NodeId)
                    {
                        ValidateTap(tap, possible, fetched, later, artifact);
                    }
                }
            }
        }

        if (taps is not null && output is not null)
        {
            foreach (var (_, expression) in output.Columns)
            {
                foreach (var symbol in expression.Reads)
                {
                    if (symbol.Kind == SymbolKind.Column && schema.Columns[symbol.Id].FeatureIndex is >= 0 and var f)
                    {
                        MarkFetched(fetched, new[] { f });
                    }
                }
            }

            foreach (var tap in taps)
            {
                if (tap.AttachOutput)
                {
                    ValidateTap(tap, possible, fetched, later, artifact);
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

    /// <summary>
    /// A tap is a passive observer (design 8.3, 8.5): every output column it reads must have a producer before the
    /// attach point, and every candidate feature must already have been fetched there. A pinned version
    /// (<c>col.x@node</c>) may be absent (the node is not in this plan variant: missing), but never in the future.
    /// </summary>
    private void ValidateTap(CompiledTap tap, Dictionary<int, string> possible, HashSet<int> fetched, HashSet<string> later, string artifact)
    {
        var pointer = "/log_taps/" + tap.TapId;
        var where = tap.AttachOutput ? "the output" : $"'{tap.AttachAfter}'";
        foreach (var pinned in tap.PinnedNodes)
        {
            if (later.Contains(pinned))
            {
                _diagnostics.Error("TAP_PINNED_NOT_PRODUCED", artifact, pointer,
                    $"Tap '{tap.TapId}' reads the version written by '{pinned}', which runs after {where}.",
                    $"attach the tap after '{pinned}', or read a version produced before {where}");
            }
        }

        foreach (var logical in tap.ReadOutputs)
        {
            if (!possible.ContainsKey(logical))
            {
                _diagnostics.Error("TAP_COLUMN_NOT_PRODUCED", artifact, pointer,
                    $"Tap '{tap.TapId}' reads '{Schema.Columns[logical].Name}', which no node produces before {where}.",
                    "attach the tap later, or log a column that exists at this point");
            }
        }

        foreach (var feature in tap.ReadFeatures)
        {
            if (!fetched.Contains(feature))
            {
                _diagnostics.Error("TAP_FEATURE_NOT_FETCHED", artifact, pointer,
                    $"Tap '{tap.TapId}' reads feature '{_features[feature].Definition.Name}', which is not fetched before {where}; taps never trigger fetches.",
                    "attach the tap after a node that reads the feature");
            }
        }
    }

    /// <summary>Marks the features an expression reads (context or candidate), with their dependencies.</summary>
    private void MarkSymbols(HashSet<int> fetched, CompiledExpression? expression)
    {
        if (expression is null)
        {
            return;
        }

        foreach (var symbol in expression.Reads)
        {
            var f = FeatureIndexOf(symbol);
            if (f >= 0)
            {
                MarkFetched(fetched, new[] { f });
            }
        }
    }

    /// <summary>Marks features (and the features derived ones are computed from) as fetched.</summary>
    private void MarkFetched(HashSet<int> fetched, int[] features)
    {
        foreach (var f in features)
        {
            if (fetched.Add(f))
            {
                MarkFetched(fetched, _features[f].Dependencies);
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
        ArmInfo[] arms,
        LogPlan? log = null)
    {
        var schema = Schema;

        // @node version references must name a node that writes the column (design 4.6).
        foreach (var (nodeId, logical) in schema.VersionReferences)
        {
            var found = false;
            var rowSource = false;
            foreach (var node in _nodes)
            {
                if (node.NodeId != nodeId)
                {
                    continue;
                }

                foreach (var o in node.Outputs)
                {
                    if (o.LogicalId == logical)
                    {
                        found = true;
                        rowSource |= (node.Effects & Effects.RowSource) != 0;
                    }
                }
            }

            if (rowSource)
            {
                // Row sources append into one shared slot per column (every row source's rows), not a per-node version.
                _diagnostics.Error("VERSION_REF_ROW_SOURCE", PipelineLabel, "/",
                    $"'col.{schema.Columns[logical].Name}@{nodeId}' pins a row source's output; row sources have no per-node version.",
                    $"read 'col.{schema.Columns[logical].Name}' before a later node rewrites it, or copy it to a new column with an ExpressionScorer");
            }
            else if (!found)
            {
                _diagnostics.Error("VERSION_REF_UNKNOWN", PipelineLabel, "/",
                    $"'col.{schema.Columns[logical].Name}@{nodeId}' refers to a node that never writes that column.");
            }
        }

        log ??= LogPlan.Empty;

        // Context features the serving path reads: nodes of every variant (including fallbacks and `when`), trigger
        // sites and the output, with everything they are derived from. Only these are prefetched; taps are passive
        // observers (design 8.5) and must not add a context fetch to every request, sampled or not.
        var serving = new HashSet<int>();
        foreach (var node in _nodes)
        {
            MarkFetched(serving, node.ReadFeatures);
        }

        foreach (var slot in timeline)
        {
            foreach (var condition in slot.SiteConditions)
            {
                MarkSymbols(serving, condition);
            }
        }

        foreach (var (_, expression) in output.Columns)
        {
            MarkSymbols(serving, expression);
        }

        foreach (var slot in log.Taps)
        {
            var reported = new HashSet<int>();
            foreach (var tap in slot.Variants)
            {
                if (tap is null)
                {
                    continue;
                }

                foreach (var f in tap.ReadContextFeatures)
                {
                    if (!serving.Contains(f) && reported.Add(f))
                    {
                        _diagnostics.Error("TAP_FEATURE_NOT_FETCHED", PipelineLabel, "/log_taps/" + tap.TapId,
                            $"Tap '{tap.TapId}' reads context feature '{_features[f].Definition.Name}', which the pipeline never fetches; taps never trigger fetches.",
                            "log a context feature a node, trigger condition or the output already reads");
                    }
                }
            }
        }

        var contextFeatures = new HashSet<int>();
        foreach (var f in features.Features)
        {
            if (f.IsContext && serving.Contains(f.Index))
            {
                contextFeatures.Add(f.Index);
            }
        }

        var plan = new ExecutionPlan
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
            Log = log,
            Params = parameters,
            Layers = layers,
            Experiments = experiments,
            Arms = arms,
            ContextFeatures = ToSortedArray(contextFeatures),
            Warnings = _diagnostics.Items,
            CompileTime = _clock.Elapsed,
            RequestInputs = _requestInputs!,
        };
        _ = plan.Telemetry; // metric tags are built with the plan, not on its first request
        return plan;
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
