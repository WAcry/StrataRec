using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Cache;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Pipeline.Planning;

/// <summary>Binary-side capabilities and environment used to compile plans.</summary>
public sealed class PlanEnvironment
{
    public OperatorRegistry Operators { get; init; } = OperatorRegistry.CreateDefault();

    public FeatureConnectorRegistry Connectors { get; init; } = FeatureConnectorRegistry.CreateDefault();

    /// <summary>Update sources that load <c>local</c> feature tables (design 6.6), keyed by type like <see cref="Connectors"/>.</summary>
    public FeatureUpdateSourceRegistry UpdateSources { get; init; } = FeatureUpdateSourceRegistry.CreateDefault();

    /// <summary>
    /// Distributed cache stores of <c>cache</c> bindings (<c>cache.distributed.store</c>, design 6.6), keyed by type.
    /// Empty by default: StrataRec.Redis adds <c>redis@1</c>.
    /// </summary>
    public FeatureCacheStoreRegistry CacheStores { get; init; } = new();

    /// <summary>The clock of <c>cache</c> tables (write times and expiry); tests pass one they move by hand.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ModelClientRegistry Models { get; init; } = ModelClientRegistry.CreateDefault();

    public CandidateSourceRegistry CandidateSources { get; init; } = CandidateSourceRegistry.CreateDefault();

    /// <summary>Directory that relative fixture paths in bindings resolve against (the config root).</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>Capability ids this binary provides (design 10.4).</summary>
    public List<string> Capabilities()
    {
        var caps = new List<string>(Operators.Ids);
        caps.AddRange(Connectors.Types);
        caps.AddRange(UpdateSources.Types);
        caps.AddRange(CacheStores.Types);
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
    private readonly IPlanBindings _bindings;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<CompiledNode> _nodes = new();
    private readonly List<FeatureRuntimeInfo> _features = new();
    private readonly List<CompiledFunnelGroup> _groups = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PlanSchema? _schema;

    /// <summary>Nodes and funnel groups declared with an error, reported where they are declared: what refers to them fails
    /// without a second error.</summary>
    private readonly HashSet<string> _failedNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedGroups = new(StringComparer.Ordinal);
    private IExpressionScope? _scope;
    private ClientResolver? _clients;
    private SchemaSources? _sources;
    private Dictionary<string, ContextField>? _requestInputs;

    /// <param name="bindings">Where the plan's local tables, clients and connectors come from (design 6.4, 6.6): an
    /// engine's lease shares and loads them; null gives ones that are never loaded, for compiling without serving.</param>
    public PlanBuilder(PlanEnvironment environment, DiagnosticBag diagnostics, IPlanBindings? bindings = null)
    {
        _environment = environment;
        _diagnostics = diagnostics;
        _bindings = bindings ?? new UnloadedBindings();
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public PlanSchema Schema => _schema ?? throw new InvalidOperationException("Call DefineSchema first.");

    public IReadOnlyList<CompiledNode> Nodes => _nodes;

    public IReadOnlyList<CompiledFunnelGroup> FunnelGroups => _groups;

    private string PipelineLabel => "pipeline:" + _sources!.Pipeline.PipelineId;

    private string CatalogLabel => "catalog:" + _sources!.Catalog.CatalogId;

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
        _clients = new ClientResolver(sources.Bindings, _environment.Models, _environment.CandidateSources, _environment.BaseDirectory, _bindings, _diagnostics);

        // Request inputs.
        _requestInputs = new Dictionary<string, ContextField>(StringComparer.Ordinal);
        var inputTypes = new Dictionary<string, ColumnType>(StringComparer.Ordinal);
        var unusableInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Sorted(pipeline.RequestSchema.Keys))
        {
            if (!ConfigTypes.TryParse(pipeline.RequestSchema[name].Type, TypeSite.Column, out var type, out _, out var error, out var hint))
            {
                _diagnostics.Error("REQUEST_BAD_TYPE", PipelineLabel, "/request_schema/" + JsonPointer.Escape(name) + "/type", error, hint);
                schema.MarkUnusable("ctx.request." + name);
                unusableInputs.Add(name);
                continue;
            }

            var field = schema.AddRequestInput(name, type);
            _requestInputs[name] = field;
            inputTypes[name] = type;
        }

        DefineFeatures(sources, inputTypes, unusableInputs);
        DefineColumns(pipeline.Columns, "/columns/", PipelineLabel);
        DefineColumns(sources.ExtraColumns, "/columns/", "arms");
        DefineParams(pipeline, sources);

        DefineFunnelGroups(pipeline.FunnelGroups, PipelineLabel);
        DefineFunnelGroups(sources.ExtraFunnelGroups, "arms");

        foreach (var table in sources.Tables)
        {
            if (DataLookupTable.Create(table, _diagnostics) is { } lookup)
            {
                schema.AddLookupTable(lookup);
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
        var node = CompileNodeCore(nodeId, definition, artifact);
        if (node is null)
        {
            _failedNodes.Add(nodeId); // what refers to it fails without a second error (a tap's columns_from)
        }

        return node;
    }

    private CompiledNode? CompileNodeCore(string nodeId, JsonObject definition, string artifact)
    {
        var schema = Schema;
        var pointer = "/nodes/" + JsonPointer.Escape(nodeId);
        if (!ArtifactSerializer.TryBind<NodeSpec>(definition, $"node '{nodeId}'", out var spec, out var invalid))
        {
            _diagnostics.Error("NODE_INVALID", artifact, pointer + invalid.Pointer, invalid.Message,
                invalid.Hint ?? "a node needs \"op\" and \"order\" (or \"role\": \"fallback\"); run 'stratarec schema pipeline' for the full schema");
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
                ExpressionErrors.Report(_diagnostics, whenErrors, artifact, pointer + "/when", spec.When);
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
            VersionReads = ToSortedArray(context.VersionReads),
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
        var pointer = "/log_taps/" + JsonPointer.Escape(tapId);
        if (!ArtifactSerializer.TryBind<LogTapSpec>(definition, $"log tap '{tapId}'", out var spec, out var invalid))
        {
            _diagnostics.Error("TAP_INVALID", artifact, pointer + invalid.Pointer, invalid.Message,
                invalid.Hint ?? "a tap needs \"group\" and \"attach\"; run 'stratarec schema pipeline' for the full schema");
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

        if (group < 0 && _failedGroups.Contains(spec.Group))
        {
            ok = false; // the group's declaration has an error, reported there
        }
        else if (group < 0)
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

            if (model is null && nodeId is not null && _failedNodes.Contains(nodeId))
            {
                ok = false; // the node did not compile; its error is reported at the node
            }
            else if (model is null || model.Operator.NamedInputs.Count == 0)
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
        var versionReads = new HashSet<VersionRead>();
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
                    else if (symbol.Name.IndexOf('@', StringComparison.Ordinal) is > 0 and var at)
                    {
                        pinnedNodes.Add(symbol.Name[(at + 1)..]);
                        versionReads.Add(new VersionRead(symbol.Name[(at + 1)..], symbol.Id));
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
            VersionReads = ToSortedArray(versionReads),
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

        ExpressionErrors.Report(_diagnostics, errors, artifact, pointer, source);
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

                ExpressionErrors.Report(_diagnostics, errors, artifact, pointer + "/key/" + i, spec.Key[i]);
            }

            if (keys.Count != spec.Key.Count)
            {
                _failedGroups.Add(name);
            }
            else
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

        ExpressionErrors.Report(_diagnostics, errors, artifact, pointer, source);
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
            if (!Schema.IsUnusable("param." + output.LimitParam))
            {
                // A parameter declared with an error is reported at its declaration.
                ReportOutputLimit(output.LimitParam, leaf);
            }

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
                ExpressionErrors.Report(_diagnostics, errors, PipelineLabel, "/output/columns/" + JsonPointer.Escape(name), source);

                ok = false;
                continue;
            }

            columns.Add((name, expression));
        }

        if (!ok)
        {
            return null;
        }

        var versionReads = new List<(string, VersionRead)>();
        foreach (var (name, expression) in columns)
        {
            foreach (var symbol in expression.Reads)
            {
                if (symbol is { Kind: SymbolKind.Column, FixedSlot: >= 0 } && Schema.Columns[symbol.Id].Kind == ColumnKind.Output
                    && symbol.Name.IndexOf('@', StringComparison.Ordinal) is > 0 and var at)
                {
                    versionReads.Add((name, new VersionRead(symbol.Name[(at + 1)..], symbol.Id)));
                }
            }
        }

        return new OutputPlan { Limit = limit, Columns = columns.ToArray(), VersionReads = versionReads.ToArray() };
    }

    private void ReportOutputLimit(string name, ParamLeaf? leaf)
    {
        if (leaf is not null)
        {
            _diagnostics.Error("OUTPUT_BAD_LIMIT", PipelineLabel, "/output/limit_param",
                $"Parameter '{name}' has type {leaf.Type}, but the output limit must be an int64 parameter.",
                $"declare '{name}' with \"type\": \"int64\", or name an int64 parameter");
            return;
        }

        var int64Params = new List<string>();
        foreach (var p in Schema.Params)
        {
            if (p.Type.Kind == StorageKind.Int64 && p.MapName is null)
            {
                int64Params.Add(p.Name);
            }
        }

        _diagnostics.Error("OUTPUT_BAD_LIMIT", PipelineLabel, "/output/limit_param",
            $"'{name}' is not a declared parameter; the output limit must be an int64 parameter.",
            NameSuggestions.DidYouMean(name, int64Params)
            ?? $"declare it under /param_schema, e.g. \"{name}\": {{ \"type\": \"int64\", \"min\": 1 }}, with a value under /params");
    }

    /// <summary>
    /// Static data-flow check of one fully merged node set (design 4.6, 5.3, 11.5): every non-nullable column a node
    /// reads has a producer that cannot be skipped before it; fixed columns have one producer per path; row sources keep
    /// every non-nullable column covered (<see cref="ValidateRowSource"/>); nodes sharing an order are independent; every
    /// pinned version names a node of this set that writes it (<see cref="ValidateVersionRead"/>); taps attach after a node
    /// of this set and log only context features it reads (<see cref="ValidateTapDependencies"/>).
    /// </summary>
    /// <remarks>
    /// Each rule here looks at a few nodes and taps; <see cref="DataFlowScopes"/> says which, for a caller that cannot
    /// check every arm combination. A new rule needs its scope there, or past that limit it is not checked for the
    /// combinations of three layers and more.
    /// </remarks>
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
        var versions = new Dictionary<int, string>(); // columns a node other than a row source wrote a version of → that node
        var rowSources = new List<CompiledNode>();
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

            // Same order ⇒ no dependency (design 5.3). A position runs its node or, when that fails, its fallback, so
            // every branch of one position must be independent of every branch of the other (review 15): a fallback that
            // appends rows, filters or reads a column the other writes makes the order matter as much as its node would.
            var branches = Branches(node, fallbacks);
            for (var j = i + 1; j < ordered.Count && ordered[j].Order == node.Order; j++)
            {
                if (Dependent(branches, Branches(ordered[j], fallbacks)))
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
                    var (why, hint) = MissingProducer(schema.Columns[logical], node, possible, ordered, i);
                    _diagnostics.Error("COLUMN_NO_PRODUCER", artifact, node.Pointer,
                        $"Node '{node.NodeId}' reads non-nullable column '{schema.Columns[logical].Name}', but {why}.", hint);
                }
            }

            var fallback = branches.Fallback;
            if (node.OnFailure == FailurePolicy.Fallback)
            {
                if (fallback is null)
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

            // Either the node or, when it fails, its fallback runs here. A failed node is rolled back, so it is not the
            // fallback's predecessor: both are checked against the state before this position, and the state after it is
            // what either may leave. They may differ in effects; a fallback produces exactly the node's columns, so the
            // checks for one effect depend only on that state and those columns. Each runs once, for a branch with the
            // effect (the node itself when both have it), and errors point at that branch.
            var appender = IsRowSource(node) ? node : fallback is not null && IsRowSource(fallback) ? fallback : null;
            var rewriter = !IsRowSource(node) ? node : fallback is not null && !IsRowSource(fallback) ? fallback : null;
            if (appender is not null)
            {
                ValidateRowSource(appender, possible, versions, rowSources, artifact);
            }

            foreach (var output2 in node.Outputs)
            {
                var column = schema.Columns[output2.LogicalId];
                if (rewriter is not null && possible.TryGetValue(output2.LogicalId, out var previous) && !column.Flow)
                {
                    _diagnostics.Error("COLUMN_MULTIPLE_PRODUCERS", artifact, rewriter.Pointer,
                        $"Column '{column.Name}' is produced by both '{previous}' and '{rewriter.NodeId}' on the same path.",
                        "declare it with \"flow\": true to allow rewriting, or write a new column");
                }
            }

            // The state after this position: rows either branch may have appended, versions either may have written.
            if (appender is not null)
            {
                rowSources.Add(appender);
            }

            if (rewriter is not null)
            {
                foreach (var o in rewriter.Outputs)
                {
                    versions[o.LogicalId] = rewriter.NodeId;
                }
            }

            foreach (var output2 in node.Outputs)
            {
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

        // Pinned versions against the nodes of this combination (design 4.6).
        foreach (var node in ordered)
        {
            foreach (var read in node.VersionReads)
            {
                ValidateVersionRead(read, "Node", node.NodeId, node.Pointer, "/config", ordered, fallbacks, artifact);
            }
        }

        foreach (var (_, fallback) in fallbacks)
        {
            foreach (var read in fallback.VersionReads)
            {
                ValidateVersionRead(read, "Fallback", fallback.NodeId, fallback.Pointer, "/config", ordered, fallbacks, artifact);
            }
        }

        if (taps is not null)
        {
            foreach (var tap in taps)
            {
                foreach (var read in tap.VersionReads)
                {
                    ValidateVersionRead(read, "Tap", tap.TapId, "/log_taps/", tap.TapId, ordered, fallbacks, artifact);
                }
            }
        }

        if (output is not null)
        {
            foreach (var (name, read) in output.VersionReads)
            {
                ValidateVersionRead(read, "Output column", name, "/output/columns/", name, ordered, fallbacks, artifact);
            }
        }

        if (taps is not null)
        {
            ValidateTapDependencies(taps, ordered, fallbacks, output, artifact);
        }
    }

    /// <summary>
    /// Why a column a node reads may not exist when it runs, and the fix that fits: its producer may be skipped, runs
    /// later, or does not exist at all.
    /// </summary>
    private static (string Why, string Hint) MissingProducer(
        LogicalColumn column, CompiledNode reader, Dictionary<int, string> possible, List<CompiledNode> ordered, int readerIndex)
    {
        const string Nullable = "declare the column nullable and handle missing values with '??'";
        if (possible.TryGetValue(column.Id, out var producer))
        {
            return ($"its producer '{producer}' may be skipped (when, min_remaining_ms or on_failure: skip)",
                $"make '{producer}' unskippable or give it a fallback, or {Nullable}");
        }

        for (var j = readerIndex + 1; j < ordered.Count; j++)
        {
            if (Writes(ordered[j], column.Id))
            {
                var later = ordered[j];
                return ($"its producer '{later.NodeId}' runs later (order {later.Order}, after {reader.Order})",
                    $"give '{reader.NodeId}' an order above {later.Order}, or '{later.NodeId}' one below {reader.Order}");
            }
        }

        return ("no node writes it",
            $"add a node before '{reader.NodeId}' that writes '{column.Name}' (e.g. ExpressionScorer@1 with \"output_col\": \"{column.Name}\"), or {Nullable}");
    }

    /// <summary>
    /// What a tap needs from the nodes of one combination wherever it attaches (design 8.3, 8.5): its attach point is a
    /// node there, and every context feature it logs is one they or the output read. The engine prefetches the context
    /// features of every variant for every request, but that is how it runs the plan, not what the configuration says: a
    /// tap that logs a feature only an arm's node reads breaks when that experiment ends. So the rule is the one a compile
    /// of the combination alone applies.
    /// </summary>
    private void ValidateTapDependencies(
        IReadOnlyList<CompiledTap> taps, List<CompiledNode> timeline, IReadOnlyDictionary<string, CompiledNode> fallbacks, OutputPlan? output, string artifact)
    {
        HashSet<int>? read = null; // context features this combination fetches, built for the first tap that logs one
        foreach (var tap in taps)
        {
            if (tap.AttachAfter is { } after)
            {
                var found = false;
                foreach (var node in timeline)
                {
                    if (node.NodeId == after)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // A fallback runs in its node's place: the taps after that node capture what it produced.
                    _diagnostics.Error("TAP_UNKNOWN_NODE", artifact, "/log_taps/" + tap.TapId + "/attach/after",
                        fallbacks.ContainsKey(after)
                            ? $"Tap '{tap.TapId}' attaches after fallback '{after}', which only runs in place of another node; taps never fire after it."
                            : $"Tap '{tap.TapId}' attaches after '{after}', which is not a node of the pipeline.",
                        fallbacks.ContainsKey(after)
                            ? $"attach after the node that falls back to '{after}': its taps capture the fallback's output too"
                            : $"attach after a node that runs wherever the tap does (an arm that adds '{after}' can add the tap too), or use {{\"output\": true}}");
                }
            }

            if (tap.ReadContextFeatures.Length == 0)
            {
                continue;
            }

            if (read is null)
            {
                read = new HashSet<int>();
                foreach (var node in timeline)
                {
                    MarkFetched(read, node.ReadFeatures);
                }

                foreach (var (_, fallback) in fallbacks)
                {
                    MarkFetched(read, fallback.ReadFeatures);
                }

                if (output is not null)
                {
                    foreach (var (_, expression) in output.Columns)
                    {
                        MarkSymbols(read, expression);
                    }
                }
            }

            foreach (var f in tap.ReadContextFeatures)
            {
                if (!read.Contains(f))
                {
                    _diagnostics.Error("TAP_FEATURE_NOT_FETCHED", artifact, "/log_taps/" + tap.TapId,
                        $"Tap '{tap.TapId}' reads context feature '{_features[f].Definition.Name}', which no node or output column reads; taps never trigger fetches.",
                        "log a context feature that a node or the output reads wherever the tap runs (an arm that adds the reader can add the tap's field too)");
                }
            }
        }
    }

    /// <summary>
    /// A pinned version (<c>col.x@node</c>, design 4.6) read where the nodes of one combination run: the pinned node must be
    /// among them and write the column there, into a version of its own. That it may not run in a request (skipped,
    /// failed, after the reader) is the runtime's to handle: the read is missing. That it is not in this combination, or
    /// does not write the column in it, is a configuration mistake, whatever other combinations hold. Row sources write one
    /// version shared by every row source, so theirs cannot be pinned.
    /// </summary>
    /// <param name="kind">What reads it, for the message: <c>Node</c>, <c>Fallback</c>, <c>Tap</c> or <c>Output column</c>.</param>
    /// <param name="reader">The reader's id or name.</param>
    /// <param name="pointer">The reader's location with <paramref name="pointerSuffix"/>, joined only for an error: this runs for every combination.</param>
    private void ValidateVersionRead(
        VersionRead read, string kind, string reader, string pointer, string pointerSuffix, List<CompiledNode> timeline,
        IReadOnlyDictionary<string, CompiledNode> fallbacks, string artifact)
    {
        CompiledNode? pinned = null;
        foreach (var node in timeline)
        {
            if (node.NodeId == read.Node)
            {
                pinned = node;
                break;
            }
        }

        if (pinned is null && fallbacks.TryGetValue(read.Node, out var fallback))
        {
            pinned = fallback;
        }

        var unknown = pinned is null || !Writes(pinned, read.Logical);
        if (!unknown && !IsRowSource(pinned!))
        {
            return;
        }

        var column = Schema.Columns[read.Logical].Name;
        var where = pointer + pointerSuffix;
        if (unknown)
        {
            _diagnostics.Error("VERSION_REF_UNKNOWN", artifact, where,
                pinned is null
                    ? $"{kind} '{reader}' reads 'col.{column}@{read.Node}', but there is no node '{read.Node}'."
                    : $"{kind} '{reader}' reads 'col.{column}@{read.Node}', but '{read.Node}' does not write '{column}'.",
                $"pin a node that writes '{column}', or read 'col.{column}' (its latest version)");
        }
        else
        {
            // Row sources append into one shared slot per column (every row source's rows), not a per-node version.
            _diagnostics.Error("VERSION_REF_ROW_SOURCE", artifact, where,
                $"{kind} '{reader}' reads 'col.{column}@{read.Node}', which pins a row source's output; row sources have no per-node version.",
                $"read 'col.{column}' before a later node rewrites it, or copy it to a new column with an ExpressionScorer");
        }
    }

    /// <summary>
    /// The scope of every rule <see cref="ValidateDataFlow"/> checks, given every variant each node and tap may take
    /// (design 11.5 check 3): one rule applied to one subject, and the nodes and taps whose variants decide it. Nothing
    /// else does, so checking every combination of the variants of a scope's nodes and taps checks that rule as
    /// completely as checking every combination at all; a caller that cannot afford the whole space checks scope by scope.
    /// </summary>
    /// <remarks>
    /// <para>Nullability exempts a column only from the producer a reader needs; every other rule applies to it. A
    /// position runs its node or that node's fallback, so a scope holds both: the fallbacks of its nodes, and the nodes
    /// that fall back to a fallback in it.</para>
    /// <para>The rules and what decides them: a reader's non-nullable column (the reader and the column's producers);
    /// one producer per path and row sources' shadowing (the producers); row sources keeping a non-nullable column
    /// covered (a row source and the producers); independence of nodes that may share an order (the two nodes); a
    /// fallback's existence and outputs (the node); a pinned version read by a node or the output (the reader and the
    /// pinned node); a tap's columns, features and pinned versions (the tap, the nodes it attaches after, and the
    /// producers, feature readers or pinned node); a tap's attach point (the tap and the nodes it attaches after); a
    /// context feature a tap logs (the tap and the nodes that read the feature: the output reads the same everywhere).</para>
    /// </remarks>
    /// <param name="nodes">Node id → the variants it takes in some combination (timeline nodes and fallbacks).</param>
    /// <param name="taps">Tap id → the variants it takes in some combination.</param>
    /// <param name="output">The output, the same in every combination.</param>
    public List<DataFlowScope> DataFlowScopes(
        IReadOnlyDictionary<string, List<CompiledNode>> nodes, IReadOnlyDictionary<string, List<CompiledTap>> taps, OutputPlan output)
    {
        var schema = Schema;
        var producers = new SortedDictionary<int, SortedSet<string>>();
        var readers = new SortedDictionary<int, SortedSet<string>>();
        var featureReaders = new Dictionary<int, SortedSet<string>>();
        var rowSources = new SortedSet<string>(StringComparer.Ordinal);
        var fallbacksOf = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var referrers = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var orders = new SortedDictionary<string, (HashSet<int> Orders, string Pointer)>(StringComparer.Ordinal);
        foreach (var (id, variants) in nodes)
        {
            foreach (var node in variants)
            {
                foreach (var written in node.Outputs)
                {
                    Add(producers, written.LogicalId, id);
                }

                foreach (var logical in node.RequiredOutputs)
                {
                    Add(readers, logical, id);
                }

                var fetched = new HashSet<int>();
                MarkFetched(fetched, node.ReadFeatures);
                foreach (var feature in fetched)
                {
                    Add(featureReaders, feature, id);
                }

                if (IsRowSource(node))
                {
                    rowSources.Add(id);
                }

                if (node.OnFailure == FailurePolicy.Fallback && node.FallbackNodeId is { } fallback)
                {
                    Add(fallbacksOf, id, fallback);
                    Add(referrers, fallback, id);
                }

                if (node.Order is { } order)
                {
                    if (!orders.TryGetValue(id, out var entry))
                    {
                        entry = (new HashSet<int>(), node.Pointer);
                        orders[id] = entry;
                    }

                    entry.Orders.Add(order);
                }
            }
        }

        var none = new SortedSet<string>(StringComparer.Ordinal);
        var scopes = new List<DataFlowScope>();
        void Scope(string subject, string pointer, SortedSet<string> involved, string? tap = null)
        {
            // A position is its node or its fallback: close over both directions until nothing is added.
            var closed = new SortedSet<string>(involved, StringComparer.Ordinal);
            var pending = new Queue<string>(involved);
            while (pending.TryDequeue(out var id))
            {
                foreach (var related in new[] { fallbacksOf.GetValueOrDefault(id), referrers.GetValueOrDefault(id) })
                {
                    foreach (var other in related ?? none)
                    {
                        if (closed.Add(other))
                        {
                            pending.Enqueue(other);
                        }
                    }
                }
            }

            scopes.Add(new DataFlowScope(subject, pointer, closed, tap is null ? Array.Empty<string>() : new[] { tap }));
        }

        SortedSet<string> With(SortedSet<string>? set, IEnumerable<string> more)
        {
            var union = new SortedSet<string>(set ?? none, StringComparer.Ordinal);
            union.UnionWith(more);
            return union;
        }

        var columns = new SortedSet<int>(producers.Keys);
        columns.UnionWith(readers.Keys);
        foreach (var logical in columns)
        {
            var column = schema.Columns[logical];
            producers.TryGetValue(logical, out var produced);
            if (produced is not null)
            {
                Scope($"the producers of column '{column.Name}'", column.Pointer, produced);
                if (!column.Type.Nullable)
                {
                    foreach (var rowSource in rowSources)
                    {
                        Scope($"column '{column.Name}' and row source '{rowSource}'", column.Pointer, With(produced, new[] { rowSource }));
                    }
                }
            }

            foreach (var reader in readers.GetValueOrDefault(logical) ?? none)
            {
                Scope($"column '{column.Name}' read by '{reader}'", column.Pointer, With(produced, new[] { reader }));
            }
        }

        var ordered = new List<(string Id, HashSet<int> Orders, string Pointer)>();
        foreach (var (id, (set, pointer)) in orders)
        {
            ordered.Add((id, set, pointer));
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (ordered[i].Orders.Overlaps(ordered[j].Orders))
                {
                    Scope($"nodes '{ordered[i].Id}' and '{ordered[j].Id}' at one order", ordered[i].Pointer + "/order",
                        new SortedSet<string>(StringComparer.Ordinal) { ordered[i].Id, ordered[j].Id });
                }
            }
        }

        foreach (var (id, _) in fallbacksOf)
        {
            Scope($"node '{id}' and its fallback", nodes[id][0].Pointer + "/on_failure", new SortedSet<string>(StringComparer.Ordinal) { id });
        }

        // A pinned version read by a node or the output: the reader's variants say whether it is read, the pinned node's
        // whether it is written there and by a row source. A tap's is decided within the tap's scope for that node, below.
        var pins = new HashSet<(string Reader, string Node)>();
        foreach (var (id, variants) in nodes)
        {
            foreach (var node in variants)
            {
                foreach (var read in node.VersionReads)
                {
                    if (pins.Add((id, read.Node)))
                    {
                        Scope($"node '{id}' reading the version of '{read.Node}'", node.Pointer + "/config",
                            new SortedSet<string>(StringComparer.Ordinal) { id, read.Node });
                    }
                }
            }
        }

        foreach (var (name, read) in output.VersionReads)
        {
            Scope($"output column '{name}' reading the version of '{read.Node}'", "/output/columns/" + name,
                new SortedSet<string>(StringComparer.Ordinal) { read.Node });
        }

        foreach (var (tapId, variants) in taps)
        {
            var attach = new SortedSet<string>(StringComparer.Ordinal);
            var pinned = new SortedSet<string>(StringComparer.Ordinal);
            var reads = new SortedSet<int>();
            var features = new SortedSet<int>();
            var contextFeatures = new SortedSet<int>();
            foreach (var tap in variants)
            {
                if (tap.AttachAfter is { } after)
                {
                    attach.Add(after);
                }

                pinned.UnionWith(tap.PinnedNodes);
                reads.UnionWith(tap.ReadOutputs);
                features.UnionWith(tap.ReadFeatures);
                contextFeatures.UnionWith(tap.ReadContextFeatures);
            }

            var pointer = "/log_taps/" + tapId;
            if (attach.Count > 0)
            {
                Scope($"tap '{tapId}' and where it attaches", pointer + "/attach/after", new SortedSet<string>(attach, StringComparer.Ordinal), tapId);
            }

            foreach (var feature in contextFeatures)
            {
                // Fetched at request start for the whole request: who reads it matters, not where the tap attaches.
                Scope($"tap '{tapId}' reading context feature '{_features[feature].Definition.Name}'", pointer,
                    With(featureReaders.GetValueOrDefault(feature), Array.Empty<string>()), tapId);
            }

            foreach (var node in pinned)
            {
                Scope($"tap '{tapId}' reading the version of '{node}'", pointer, With(attach, new[] { node }), tapId);
            }

            foreach (var logical in reads)
            {
                Scope($"tap '{tapId}' reading '{schema.Columns[logical].Name}'", pointer, With(attach, producers.GetValueOrDefault(logical) ?? none), tapId);
            }

            foreach (var feature in features)
            {
                Scope($"tap '{tapId}' reading feature '{_features[feature].Definition.Name}'", pointer,
                    With(attach, featureReaders.GetValueOrDefault(feature) ?? none), tapId);
            }
        }

        return scopes;

        static void Add<TKey>(IDictionary<TKey, SortedSet<string>> map, TKey key, string id)
        {
            if (!map.TryGetValue(key, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                map[key] = set;
            }

            set.Add(id);
        }
    }

    /// <summary>
    /// Checks a row source against the columns around it on one path (design 4.6). Row sources write one shared version
    /// of each column, holding the rows each of them appended, and a row has no value in a column until something writes
    /// it (<c>CandidateTable</c>). So a row source that appends rows after a non-nullable column exists must write it
    /// (<c>ROW_SOURCE_AFTER_COLUMN</c>); every row source before the first one that writes a non-nullable column must write
    /// it too (<c>ROW_SOURCE_BEFORE_COLUMN</c>); and no row source may write a column after another node wrote its own
    /// version (<c>ROW_SOURCE_SHADOWS_VERSION</c>), since readers would switch to the shared version, which holds none of
    /// that node's values. The first two would fail every request that runs both nodes, at commit; the last would hide
    /// values without an error.
    /// </summary>
    private void ValidateRowSource(
        CompiledNode node, Dictionary<int, string> possible, Dictionary<int, string> versions, List<CompiledNode> earlier, string artifact)
    {
        var schema = Schema;
        foreach (var logical in possible.Keys)
        {
            var column = schema.Columns[logical];
            if (!column.Type.Nullable && !Writes(node, logical))
            {
                _diagnostics.Error("ROW_SOURCE_AFTER_COLUMN", artifact, node.Pointer,
                    $"Row source '{node.NodeId}' appends rows after non-nullable column '{column.Name}' is produced but does not write it.",
                    versions.TryGetValue(logical, out var writer)
                        ? $"move the row source before '{writer}', or declare the column nullable and handle missing values with '??'"
                        : "write the column in the row source, move it earlier, or declare the column nullable and handle missing values with '??'");
            }
        }

        foreach (var output in node.Outputs)
        {
            var column = schema.Columns[output.LogicalId];
            if (versions.TryGetValue(output.LogicalId, out var writer))
            {
                _diagnostics.Error("ROW_SOURCE_SHADOWS_VERSION", artifact, node.Pointer,
                    $"Row source '{node.NodeId}' writes '{column.Name}' after '{writer}' wrote its own version of it. Row sources write one " +
                    $"version shared by the rows they append, which has none of the values '{writer}' wrote, and readers would switch to it.",
                    $"move the row source before '{writer}', or write its values to another column and combine the two in a later node");
            }
            else if (!column.Type.Nullable && !possible.ContainsKey(output.LogicalId))
            {
                // The first row source to write the column: the rows appended before it have no value in it.
                foreach (var before in earlier)
                {
                    if (!Writes(before, output.LogicalId))
                    {
                        _diagnostics.Error("ROW_SOURCE_BEFORE_COLUMN", artifact, before.Pointer,
                            $"Row source '{before.NodeId}' appends rows but does not write non-nullable column '{column.Name}', which the " +
                            $"later row source '{node.NodeId}' writes for its own rows; the rows of '{before.NodeId}' would have no value in it.",
                            $"write '{column.Name}' in '{before.NodeId}' too, or declare it nullable " +
                            $"(\"{column.Name}\": {{ \"type\": \"{column.Type.AsNullable()}\" }}) and handle missing values with '??'");
                    }
                }
            }
        }
    }

    private static bool IsRowSource(CompiledNode node) => (node.Effects & Effects.RowSource) != 0;

    /// <summary>
    /// A tap is a passive observer (design 8.3, 8.5): every output column it reads must have a producer before the
    /// attach point, and every candidate feature must already have been fetched there. A pinned version
    /// (<c>col.x@node</c>) must not be written in the future (<see cref="ValidateVersionRead"/> checks that it is written).
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
            _environment.BaseDirectory,
            _environment.UpdateSources,
            _bindings,
            _bindings,
            _environment.CacheStores,
            _environment.Clock,
            CatalogLabel);
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
            _diagnostics.Error("PARAM_BAD_VALUE", artifact, pointer, $"Parameter '{leaf.Name}' {error}.");
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

    private void DefineFeatures(SchemaSources sources, Dictionary<string, ColumnType> inputTypes, HashSet<string> unusableInputs)
    {
        var schema = Schema;
        var catalog = FeatureCatalog.Build(sources.Catalog, inputTypes, _diagnostics, CatalogLabel, unusableInputs);
        foreach (var name in catalog.Unusable)
        {
            // Its entity decides the namespace; one that failed has none, so both are marked.
            schema.MarkUnusable("col." + name);
            schema.MarkUnusable("ctx." + name);
        }

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
                _diagnostics.Error("FEATURE_DUPLICATE", CatalogLabel, feature.Pointer, $"'{feature.Name}' collides with another column.");
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
                if (!ExpressionCompiler.TryCompile(feature.Expr!, _scope!, DerivedOptions(feature), out var expression, out var errors))
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
                        _diagnostics.Error("FEATURE_BAD_REFERENCE", CatalogLabel, feature.Pointer + "/expr",
                            $"Derived feature '{feature.Name}' reads '{symbol.Name}'; derived features may only read features, request inputs and col.entity_id.");
                    }
                }

                RegisterFeature(feature, type, expression, dependencies.ToArray());
                pendingDerived.RemoveAt(i);
                i--;
                progress = true;
            }
        }

        // One that failed without an error reads a name declared with an error: it is unusable too, and so is one that
        // failed only because it reads such a derived feature. Recompiling those says which: they now fail silently.
        for (var marked = true; marked;)
        {
            marked = false;
            foreach (var feature in pendingDerived)
            {
                if (lastErrors[feature.Name].Count == 0 && !schema.IsUnusable(feature.ExpressionName))
                {
                    schema.MarkUnusable(feature.ExpressionName);
                    marked = true;
                }
            }

            foreach (var feature in pendingDerived)
            {
                if (marked && !schema.IsUnusable(feature.ExpressionName))
                {
                    ExpressionCompiler.TryCompile(feature.Expr!, _scope!, DerivedOptions(feature), out _, out var errors);
                    lastErrors[feature.Name] = errors;
                }
            }
        }

        foreach (var feature in pendingDerived)
        {
            ExpressionErrors.Report(_diagnostics, lastErrors[feature.Name], CatalogLabel, feature.Pointer + "/expr", feature.Expr!);
        }

        static CompileOptions DerivedOptions(FeatureDefinition feature) => new()
        {
            ExpectedType = feature.Type.AsNonNullable(),
            AllowNullableResult = true,
            RequestLevelOnly = feature.IsContext,
        };
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

            if (!ConfigTypes.TryParse(spec.Type, TypeSite.Column, out var type, out _, out var error, out var hint))
            {
                _diagnostics.Error("COLUMN_BAD_TYPE", artifact, pointer + "/type", error, hint);
                schema.MarkUnusable("col." + name);
                continue;
            }

            var hasDefault = spec.Default is not null;
            var value = ScalarValue.Missing;
            if (hasDefault && !ConfigValues.TryToScalar(spec.Default, type.AsNonNullable(), out value, out var defaultError))
            {
                _diagnostics.Error("COLUMN_BAD_DEFAULT", artifact, pointer + "/default", $"The default of '{name}' {defaultError}.");
                schema.MarkUnusable("col." + name);
                continue;
            }

            if (!type.Nullable && type.IsNumeric && !hasDefault)
            {
                _diagnostics.Error("COLUMN_NEEDS_DEFAULT", artifact, pointer,
                    $"Non-nullable numeric column '{name}' needs a 'default' (it replaces NaN/±∞ writes, design 4.3).",
                    "add \"default\": 0.0, or declare the type nullable");
                schema.MarkUnusable("col." + name);
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
            if (!ConfigTypes.TryParse(spec.Type, TypeSite.Parameter, out var leafType, out var isMap, out var error, out var hint))
            {
                _diagnostics.Error("PARAM_BAD_TYPE", artifact, pointer + "/type", error, hint);
                schema.MarkUnusable("param." + name);
                continue;
            }

            List<ScalarValue>? enumValues = null;
            if (spec.Enum is not null)
            {
                enumValues = new List<ScalarValue>();
                for (var i = 0; i < spec.Enum.Count; i++)
                {
                    if (ConfigValues.TryToScalar(spec.Enum[i], leafType, out var ev, out var enumError))
                    {
                        enumValues.Add(ev);
                    }
                    else
                    {
                        _diagnostics.Error("PARAM_BAD_ENUM", artifact, pointer + "/enum/" + i, $"Each value of the enum {enumError}.",
                            "list values of the parameter's type");
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

    /// <summary>
    /// What may run at a node's position: the node and, when it fails over (<c>on_failure: fallback</c>), its fallback;
    /// null when the fallback is unknown (reported where the fallback is checked). The one description the order and the
    /// data-flow checks share.
    /// </summary>
    private static (CompiledNode Node, CompiledNode? Fallback) Branches(CompiledNode node, IReadOnlyDictionary<string, CompiledNode> fallbacks) =>
        (node, node.OnFailure == FailurePolicy.Fallback && fallbacks.TryGetValue(node.FallbackNodeId!, out var fallback) ? fallback : null);

    /// <summary>Whether any branch of one position depends on any branch of the other, in either direction.</summary>
    private static bool Dependent((CompiledNode Node, CompiledNode? Fallback) a, (CompiledNode Node, CompiledNode? Fallback) b)
    {
        return Either(a.Node, b.Node)
            || (a.Fallback is { } fa && (Either(fa, b.Node) || (b.Fallback is { } fb1 && Either(fa, fb1))))
            || (b.Fallback is { } fb && Either(a.Node, fb));

        static bool Either(CompiledNode x, CompiledNode y) => DependsOn(x, y) || DependsOn(y, x);
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

    private static VersionRead[] ToSortedArray(IReadOnlyCollection<VersionRead> values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<VersionRead>();
        }

        var array = new VersionRead[values.Count];
        var i = 0;
        foreach (var v in values)
        {
            array[i++] = v;
        }

        Array.Sort(array, static (a, b) => string.CompareOrdinal(a.Node, b.Node) is var c && c != 0 ? c : a.Logical.CompareTo(b.Logical));
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

/// <summary>One data-flow rule applied to one subject, and the nodes and taps whose variants decide it (<see cref="PlanBuilder.DataFlowScopes"/>).</summary>
/// <param name="Subject">What the rule is about, for a message, e.g. <c>the producers of column 'x'</c>.</param>
/// <param name="Pointer">Where the subject is declared: a column, a node or a tap.</param>
/// <param name="Nodes">Node ids (timeline nodes and fallbacks) whose variants decide the rule.</param>
/// <param name="Taps">Tap ids whose variants decide it.</param>
public sealed record DataFlowScope(string Subject, string Pointer, IReadOnlyCollection<string> Nodes, IReadOnlyCollection<string> Taps);
