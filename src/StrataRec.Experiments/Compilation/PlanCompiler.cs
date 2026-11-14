using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Compilation;

/// <summary>
/// Compiles a pipeline and its experiments into one unified <see cref="ExecutionPlan"/> (design 12): parameter
/// snapshots for parameter-only arms, precompiled node variants for every arm combination of the layers that touch a
/// node, timeline positions for added, deleted and moved nodes, and trigger sites.
/// </summary>
/// <remarks>
/// With no experiments this is the plain pipeline compiler. <see cref="CompileMerged"/> compiles baseline ⊕ a set of
/// arms as a plain pipeline; it is the reference the unified plan is differentially tested against (design 16.2).
/// </remarks>
public static class PlanCompiler
{
    /// <param name="bindings">
    /// Where the plan's local feature tables, clients and connectors come from (design 6.4, 6.6): the engine passes the
    /// plan's binding lease, which shares and loads them. Null gives ones that are never loaded, for compiling without
    /// serving (validate, build, explain): requests reading such a table fail the features, and model signatures that
    /// need a loaded client are checked when an engine loads the plan.
    /// </param>
    public static PlanCompilation Compile(PlanCompileRequest request, PlanEnvironment? environment = null, IPlanBindings? bindings = null)
    {
        var started = Stopwatch.GetTimestamp();
        var view = PatchableView.Create(request.Pipeline, request.Catalog);
        var compilation = new UnifiedCompilation(request, environment ?? new PlanEnvironment(), view, new DiagnosticBag(), buildPlan: true,
            bindings ?? new UnloadedBindings()).Run();
        StrataRecMetrics.PlanCompileDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>(StrataRecTags.Pipeline, request.Pipeline.PipelineId));
        return compilation;
    }

    /// <summary>
    /// Compiles the baseline with the patches of <paramref name="armIds"/> (<c>experiment/arm</c>) applied in layer
    /// order, as a plain pipeline without experiments. Running it equals running the unified plan with those arms.
    /// </summary>
    public static PlanCompilation CompileMerged(
        PlanCompileRequest request, IReadOnlyList<string> armIds, PlanEnvironment? environment = null, IPlanBindings? bindings = null)
    {
        var diagnostics = new DiagnosticBag();
        var view = PatchableView.Create(request.Pipeline, request.Catalog);
        var selected = new List<(string Layer, string Id, JsonObject Patch)>();
        foreach (var id in armIds)
        {
            var found = false;
            foreach (var placement in request.Experiments)
            {
                var e = placement.Experiment;
                if (id.StartsWith(e.ExperimentId + "/", StringComparison.Ordinal)
                    && e.Arms.TryGetValue(id[(e.ExperimentId.Length + 1)..], out var patch))
                {
                    selected.Add((placement.Layer, id, patch));
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new ArgumentException($"Unknown arm '{id}'. Arms are named 'experiment/arm'.", nameof(armIds));
            }
        }

        selected.Sort(static (a, b) => string.CompareOrdinal(a.Layer, b.Layer));
        foreach (var (_, id, patch) in selected)
        {
            var merged = view.Apply(patch, diagnostics, "experiment:" + id[..id.IndexOf('/')], "/arms/" + id[(id.IndexOf('/') + 1)..]);
            if (merged is null)
            {
                return new PlanCompilation { Diagnostics = diagnostics.Items, Report = UnifiedCompilation.EmptyReport };
            }

            view = merged;
        }

        var plain = UnifiedCompilation.WithoutExperiments(request);
        return new UnifiedCompilation(plain, environment ?? new PlanEnvironment(), view, diagnostics, buildPlan: true, bindings ?? new UnloadedBindings()).Run();
    }
}

/// <summary>The state of one compile. Not thread-safe; one instance per compile.</summary>
internal sealed partial class UnifiedCompilation
{
    internal static readonly PlanReport EmptyReport = new() { Nodes = Array.Empty<NodeVariantReport>(), Arms = Array.Empty<ArmReport>() };

    private readonly PlanCompileRequest _request;
    private readonly PlanEnvironment _environment;
    private readonly PatchableView _view;
    private readonly DiagnosticBag _diagnostics;
    private readonly bool _buildPlan;
    private readonly IPlanBindings _bindings;
    private readonly PlanBuilder _builder;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly string _pipelineLabel;

    private readonly List<string> _layers = new();
    private readonly List<(string Id, int Layer, int[] Arms)> _experiments = new();
    private readonly List<ArmPatch> _arms = new();
    private readonly SortedDictionary<string, NodeVariants> _variants = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, TapVariants> _taps = new(StringComparer.Ordinal);

    /// <summary>Union of add-only additions: section → name → (value, first arm).</summary>
    private readonly Dictionary<string, SortedDictionary<string, (JsonNode Value, ArmPatch Arm)>> _additions = new(StringComparer.Ordinal);

    /// <param name="bindings">One for the whole compile, its validation passes included: they compile the same nodes, so
    /// they share its clients and signatures. Only a compilation that builds the plan builds the feature plan, so only it
    /// acquires tables and connectors.</param>
    public UnifiedCompilation(
        PlanCompileRequest request, PlanEnvironment environment, PatchableView view, DiagnosticBag diagnostics, bool buildPlan, IPlanBindings bindings)
    {
        _request = request;
        _environment = environment;
        _view = view;
        _diagnostics = diagnostics;
        _buildPlan = buildPlan;
        _bindings = bindings;
        _builder = new PlanBuilder(environment, diagnostics, bindings);
        _pipelineLabel = "pipeline:" + request.Pipeline.PipelineId;
    }

    public static PlanCompileRequest WithoutExperiments(PlanCompileRequest r) => new()
    {
        Pipeline = r.Pipeline,
        Catalog = r.Catalog,
        Bindings = r.Bindings,
        Tables = r.Tables,
        Policy = r.Policy,
        ManifestId = r.ManifestId,
        ValidateArmsIndependently = false,
        MaxCombinationsPerNode = r.MaxCombinationsPerNode,
        MaxCombinationsChecked = r.MaxCombinationsChecked,
        Today = r.Today,
    };

    public PlanCompilation Run()
    {
        var start = _diagnostics.Count;
        DefineExperiments();
        Settle(start, Array.Empty<ArmPatch>());

        start = _diagnostics.Count;
        var sources = BuildSchemaSources();
        _builder.DefineSchema(sources);
        SettleAdditions(start);

        start = _diagnostics.Count;
        var parameters = BuildParams(out var leafExperiments);
        Settle(start, Array.Empty<ArmPatch>());

        CompileVariants();

        // After the nodes: operators declare implicit columns (e.g. recall.*) that the output may read.
        start = _diagnostics.Count;
        var output = _builder.CompileOutput();
        Settle(start, Array.Empty<ArmPatch>());
        CompileTapVariants();
        var (timeline, fallbacks) = BuildSlots();

        var report = new CombinationReport();
        if (output is not null)
        {
            ValidateCombinations(timeline, fallbacks, output, report);
        }

        ValidatePairs();
        ValidateBaselineAlone();
        if (_request.ValidateArmsIndependently)
        {
            ValidateArmsIndependently();
        }

        start = _diagnostics.Count;
        AssignTriggerSites(timeline, fallbacks, output, leafExperiments);
        Settle(start, Array.Empty<ArmPatch>());

        ExecutionPlan? plan = null;
        if (_buildPlan)
        {
            start = _diagnostics.Count;
            var features = _builder.BuildFeaturePlan();
            SettleAdditions(start);
            if (!_diagnostics.HasErrors && output is not null)
            {
                plan = _builder.Build(_request.ManifestId, features, timeline, fallbacks, output, parameters, LayerInfos(), ExperimentInfos(), ArmInfos(), BuildLogPlan());
                plan = _diagnostics.HasErrors ? null : plan;
            }
        }

        return new PlanCompilation
        {
            Plan = plan,
            Diagnostics = _diagnostics.Items,
            Report = new PlanReport
            {
                Nodes = NodeReports(),
                Arms = ArmReports(),
                CombinationsChecked = report.Checked,
                CombinationCheckExhaustive = report.Exhaustive,
            },
        };
    }

    // ------------------------------------------------------------------ experiments and arms

    private void DefineExperiments()
    {
        var placements = new List<ExperimentPlacement>(_request.Experiments);
        placements.Sort(static (a, b) =>
        {
            var c = string.CompareOrdinal(a.Layer, b.Layer);
            return c != 0 ? c : string.CompareOrdinal(a.Experiment.ExperimentId, b.Experiment.ExperimentId);
        });

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in placements)
        {
            var e = placement.Experiment;
            var artifact = "experiment:" + e.ExperimentId;
            if (!seen.Add(e.ExperimentId))
            {
                _diagnostics.Error("EXPERIMENT_DUPLICATE", artifact, "/experiment_id", $"Experiment '{e.ExperimentId}' is listed twice.");
                continue;
            }

            if (!ArmAnalyzer.IsValidId(e.ExperimentId))
            {
                _diagnostics.Error("BAD_ID", artifact, "/experiment_id", $"'{e.ExperimentId}' is not a valid experiment id.",
                    "ids start with a letter or '_' and contain letters, digits, '_', '.' or '-'");
            }

            if (!string.Equals(e.PipelineId, _request.Pipeline.PipelineId, StringComparison.Ordinal))
            {
                _diagnostics.Error("EXPERIMENT_WRONG_PIPELINE", artifact, "/pipeline_id",
                    $"Experiment targets pipeline '{e.PipelineId}', not '{_request.Pipeline.PipelineId}'.");
            }

            if (!DateOnly.TryParseExact(e.ExpiresAt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires))
            {
                _diagnostics.Error("EXPERIMENT_BAD_EXPIRY", artifact, "/expires_at", $"'{e.ExpiresAt}' is not a date.", "use yyyy-MM-dd");
            }
            else if (_request.Today is { } today && expires < today)
            {
                _diagnostics.Error("EXPERIMENT_EXPIRED", artifact, "/expires_at", $"Experiment expired on {e.ExpiresAt}.",
                    "graduate the winning arm into the baseline or remove the experiment (design 11.7)");
            }

            if (e.Arms.Count == 0)
            {
                _diagnostics.Error("EXPERIMENT_NO_ARMS", artifact, "/arms", "An experiment needs at least one arm.");
                continue;
            }

            var layer = _layers.IndexOf(placement.Layer);
            if (layer < 0)
            {
                layer = _layers.Count;
                _layers.Add(placement.Layer);
            }

            var experimentIndex = _experiments.Count;
            var armNames = new List<string>(e.Arms.Keys);
            armNames.Sort(StringComparer.Ordinal);
            var armIndices = new int[armNames.Count];
            for (var i = 0; i < armNames.Count; i++)
            {
                var name = armNames[i];
                var arm = new ArmPatch
                {
                    Index = _arms.Count,
                    Id = e.ExperimentId + "/" + name,
                    ArmName = name,
                    ExperimentIndex = experimentIndex,
                    ExperimentId = e.ExperimentId,
                    LayerIndex = layer,
                    Patch = e.Arms[name],
                    Artifact = artifact,
                    PointerPrefix = "/arms/" + JsonPointer.Escape(name),
                };
                if (!ArmAnalyzer.IsValidId(name))
                {
                    _diagnostics.Error("BAD_ID", artifact, arm.PointerPrefix, $"'{name}' is not a valid arm name.",
                        "arm names start with a letter or '_' and contain letters, digits, '_', '.' or '-'");
                    arm.Valid = false;
                }

                ArmAnalyzer.Analyze(arm, _view, _request.Policy, _diagnostics);
                armIndices[i] = arm.Index;
                _arms.Add(arm);
            }

            _experiments.Add((e.ExperimentId, layer, armIndices));
        }
    }

    // ------------------------------------------------------------------ schema (design 11.4)

    private SchemaSources BuildSchemaSources()
    {
        foreach (var section in ArmAnalyzer.SchemaSections)
        {
            _additions[section] = new SortedDictionary<string, (JsonNode, ArmPatch)>(StringComparer.Ordinal);
        }

        var extraLeaves = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var arm in _arms)
        {
            if (!arm.Valid)
            {
                continue;
            }

            foreach (var leaf in arm.ExtraParamLeaves)
            {
                extraLeaves.Add(leaf);
            }

            foreach (var (section, entries) in arm.Additions)
            {
                if (!_additions.TryGetValue(section, out var union))
                {
                    continue;
                }

                foreach (var (name, (value, pointer)) in entries)
                {
                    if (!union.TryGetValue(name, out var existing))
                    {
                        union[name] = (value, arm);
                    }
                    else if (!ArmAnalyzer.JsonEquals(existing.Value, value))
                    {
                        _diagnostics.Error("ADD_CONFLICT", arm.Artifact, pointer,
                            $"'/{section}/{name}' is also added by '{existing.Arm.Id}' with a different definition.",
                            "an added name has one meaning everywhere: use identical definitions or different names (design 11.4)");
                    }
                }
            }
        }

        var columns = ReadAdditions<ColumnSpec>("columns");
        var paramSchema = ReadAdditions<ParamSpec>("param_schema");
        var features = new Dictionary<string, FeatureSpec>(_view.Catalog.Features, StringComparer.Ordinal);
        foreach (var (name, spec) in ReadAdditions<FeatureSpec>("features"))
        {
            features[name] = spec;
        }

        var derived = new Dictionary<string, DerivedFeatureSpec>(_view.Catalog.DerivedFeatures, StringComparer.Ordinal);
        foreach (var (name, spec) in ReadAdditions<DerivedFeatureSpec>("derived_features"))
        {
            derived[name] = spec;
        }

        var funnelGroups = ReadAdditions<FunnelGroupSpec>("funnel_groups");

        // Baseline node ids appear in pointers and expressions (col.x@node); keep them well-formed.
        foreach (var (id, _) in _view.Nodes)
        {
            if (!ArmAnalyzer.IsValidNodeId(id))
            {
                _diagnostics.Error("BAD_ID", _pipelineLabel, "/nodes/" + JsonPointer.Escape(id), $"'{id}' is not a valid node id.", ArmAnalyzer.NodeIdHint);
            }
        }

        return new SchemaSources
        {
            Pipeline = _view.Pipeline,
            Catalog = new FeatureCatalogArtifact
            {
                CatalogId = _view.Catalog.CatalogId,
                Description = _view.Catalog.Description,
                Entities = _view.Catalog.Entities,
                Features = features,
                DerivedFeatures = derived,
            },
            Bindings = _request.Bindings,
            Tables = _request.Tables,
            ExtraColumns = columns,
            ExtraParamSchema = paramSchema,
            ExtraParamLeaves = extraLeaves,
            ExtraFunnelGroups = funnelGroups,
        };
    }

    private Dictionary<string, T> ReadAdditions<T>(string section)
        where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (name, (value, arm)) in _additions[section])
        {
            if (ArtifactSerializer.TryBind<T>(value, $"entry '{name}' of '{section}'", out var entry, out var error))
            {
                result[name] = entry;
            }
            else
            {
                _diagnostics.Error("PATCH_SCHEMA", arm.Artifact, arm.PointerPrefix + "/" + section + "/" + JsonPointer.Escape(name) + error.Pointer,
                    error.Message, error.Hint ?? "a new entry must be a complete definition (design 11.3)");
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ parameters (design 12.2)

    private ParamTable BuildParams(out Dictionary<int, SortedSet<int>> leafExperiments)
    {
        var schema = _builder.Schema;
        var baseline = new ScalarValue[schema.Params.Count];
        for (var i = 0; i < baseline.Length; i++)
        {
            baseline[i] = schema.Params[i].Baseline;
        }

        leafExperiments = new Dictionary<int, SortedSet<int>>();
        var overrides = new (int, ScalarValue)[_arms.Count][];
        foreach (var arm in _arms)
        {
            var list = new List<(int, ScalarValue)>();
            foreach (var ov in arm.ParamOverrides)
            {
                if (!schema.TryGetParam(ov.Leaf, out var leaf))
                {
                    if (!schema.IsUnusable("param." + ov.Leaf))
                    {
                        // A parameter declared with an error (an unknown type) is reported at its declaration.
                        var declared = new List<string>(schema.Params.Count);
                        foreach (var p in schema.Params)
                        {
                            declared.Add(p.Name);
                        }

                        _diagnostics.Error("PARAM_UNKNOWN", arm.Artifact, ov.Pointer, $"Parameter leaf '{ov.Leaf}' is not declared.",
                            NameSuggestions.DidYouMean(ov.Leaf, declared) ?? "declare it under /param_schema of the pipeline or of this arm");
                    }

                    continue;
                }

                if (_builder.TryParamValue(leaf, ov.Value, arm.Artifact, ov.Pointer, out var value))
                {
                    list.Add((leaf.Id, value));
                    if (!leafExperiments.TryGetValue(leaf.Id, out var set))
                    {
                        set = new SortedSet<int>();
                        leafExperiments[leaf.Id] = set;
                    }

                    set.Add(arm.ExperimentIndex);
                }
            }

            overrides[arm.Index] = list.ToArray();
        }

        return new ParamTable(baseline, overrides);
    }

    // ------------------------------------------------------------------ node variants (design 12.3)

    /// <summary>
    /// The arm combinations of the layers that touch one node or tap (design 12.3): per touching layer, "not
    /// modified" plus each arm that modifies it, indexed mixed-radix exactly like <see cref="TimelineSlot.Select"/>.
    /// </summary>
    internal class VariantSpace
    {
        /// <summary>Touching layers L(n), ascending (patches apply in this order).</summary>
        public required int[] Layers { get; init; }

        /// <summary>Per touching layer: the arms modifying the item; local index = position + 1.</summary>
        public required int[][] LocalArms { get; init; }

        public required Dictionary<int, int>[] LocalIndex { get; init; }

        public required int[] Radix { get; init; }

        public int IndexOf(in RequestArms arms)
        {
            var index = 0;
            for (var i = 0; i < Layers.Length; i++)
            {
                var arm = arms.ArmIn(Layers[i]);
                var local = arm >= 0 && LocalIndex[i].TryGetValue(arm, out var l) ? l : 0;
                index = (index * Radix[i]) + local;
            }

            return index;
        }

        /// <summary>
        /// The combination index for a set of arms (one per layer at most). Layers of this space without an arm in the
        /// set, and arms that do not modify the item, map to "not modified".
        /// </summary>
        public int IndexOfArms(IReadOnlyList<ArmPatch> arms)
        {
            var index = 0;
            for (var i = 0; i < Layers.Length; i++)
            {
                var local = 0;
                foreach (var arm in arms)
                {
                    if (arm.LayerIndex == Layers[i] && LocalIndex[i].TryGetValue(arm.Index, out var l))
                    {
                        local = l;
                    }
                }

                index = (index * Radix[i]) + local;
            }

            return index;
        }

        /// <summary>The arms (global indices) a combination index assigns, in layer order.</summary>
        public List<int> ArmsOf(int combination)
        {
            var arms = new List<int>();
            for (var i = Layers.Length - 1; i >= 0; i--)
            {
                var local = combination % Radix[i];
                combination /= Radix[i];
                if (local > 0)
                {
                    arms.Add(LocalArms[i][local - 1]);
                }
            }

            arms.Reverse();
            return arms;
        }
    }

    /// <summary>All arm combinations of the layers that touch one node id, and the variant compiled for each.</summary>
    internal sealed class NodeVariants : VariantSpace
    {
        public required string NodeId { get; init; }

        public required JsonObject?[] Definitions { get; init; }

        public required CompiledNode?[] Variants { get; init; }

        /// <summary>Combinations whose definition failed to compile (errors were reported).</summary>
        public required bool[] Failed { get; init; }

        public bool IsFallback { get; set; }
    }

    /// <summary>Variants of one log tap; taps vary independently of the node they attach to (design 12.3).</summary>
    internal sealed class TapVariants : VariantSpace
    {
        public required string TapId { get; init; }

        public required CompiledTap?[] Variants { get; init; }

        public required bool[] Failed { get; init; }
    }

    private readonly record struct Space(int[] Layers, int[][] LocalArms, Dictionary<int, int>[] LocalIndex, int[] Radix, int Count);

    /// <summary>L(x) and its arms for the arms that touch an item; null (after an error) when C(x) exceeds the limit.</summary>
    private Space? BuildSpace(Func<ArmPatch, bool> touches, string what, string pointer)
    {
        var byLayer = new SortedDictionary<int, List<int>>();
        foreach (var arm in _arms)
        {
            if (arm.Valid && touches(arm))
            {
                if (!byLayer.TryGetValue(arm.LayerIndex, out var list))
                {
                    list = new List<int>();
                    byLayer[arm.LayerIndex] = list;
                }

                list.Add(arm.Index);
            }
        }

        var layers = new int[byLayer.Count];
        var localArms = new int[byLayer.Count][];
        var localIndex = new Dictionary<int, int>[byLayer.Count];
        var radix = new int[byLayer.Count];
        long combinations = 1;
        var li = 0;
        foreach (var (layer, arms) in byLayer)
        {
            layers[li] = layer;
            localArms[li] = arms.ToArray();
            localIndex[li] = new Dictionary<int, int>();
            for (var i = 0; i < arms.Count; i++)
            {
                localIndex[li][arms[i]] = i + 1;
            }

            radix[li] = arms.Count + 1;

            // Saturates before it could overflow (review 18 #5): a wrapped product would slip under the limit.
            combinations = combinations > long.MaxValue / radix[li] ? long.MaxValue : combinations * radix[li];
            li++;
        }

        if (combinations > _request.MaxCombinationsPerNode)
        {
            var names = new List<string>();
            foreach (var layer in layers)
            {
                names.Add(_layers[layer]);
            }

            var count = combinations == long.MaxValue ? $"more than {long.MaxValue}" : combinations.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _diagnostics.Error("NODE_TOO_MANY_COMBINATIONS", _pipelineLabel, pointer,
                $"{what} is modified by arms in layers {string.Join(", ", names)}: {count} combinations exceed the limit of {_request.MaxCombinationsPerNode}.",
                "move some of these experiments into the same layer (their arms then exclude each other), or graduate one");
            return null;
        }

        return new Space(layers, localArms, localIndex, radix, (int)combinations);
    }

    /// <summary>Baseline definition with each assigned arm's <c>/section/id</c> patch applied in layer order.</summary>
    private static JsonNode? Merge(JsonNode? baseline, List<ArmPatch> arms, string section, string id)
    {
        var definition = baseline;
        foreach (var arm in arms)
        {
            if (arm.Patch[section] is JsonObject entries && entries.TryGetPropertyValue(id, out var part))
            {
                definition = JsonMergePatch.Apply(definition, part);
            }
        }

        return definition;
    }

    private void CompileVariants()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in _view.Nodes)
        {
            ids.Add(id);
        }

        foreach (var arm in _arms)
        {
            if (arm.Valid)
            {
                ids.UnionWith(arm.AddedNodes);
            }
        }

        foreach (var id in ids)
        {
            CompileNodeVariants(id);
        }
    }

    private void CompileNodeVariants(string nodeId)
    {
        var pointer = "/nodes/" + JsonPointer.Escape(nodeId);
        if (BuildSpace(arm => arm.TouchedNodes.Contains(nodeId), $"Node '{nodeId}'", pointer) is not { } space)
        {
            return;
        }

        var variants = new NodeVariants
        {
            NodeId = nodeId,
            Layers = space.Layers,
            LocalArms = space.LocalArms,
            LocalIndex = space.LocalIndex,
            Radix = space.Radix,
            Definitions = new JsonObject?[space.Count],
            Variants = new CompiledNode?[space.Count],
            Failed = new bool[space.Count],
        };
        _variants[nodeId] = variants;

        var baseline = _view.Nodes[nodeId]?.DeepClone();
        var byHash = new Dictionary<string, (CompiledNode? Node, bool Failed)>(StringComparer.Ordinal);
        bool? fallbackRole = null;
        for (var c = 0; c < space.Count; c++)
        {
            var armsInCombination = ArmPatches(variants.ArmsOf(c));
            var definition = Merge(baseline, armsInCombination, "nodes", nodeId);
            if (definition is null)
            {
                continue; // the node does not exist in this combination
            }

            var start = _diagnostics.Count;
            if (definition is not JsonObject node)
            {
                _diagnostics.Error("NODE_INVALID", _pipelineLabel, pointer, "A node must be an object.");
                variants.Failed[c] = true;
                Settle(start, armsInCombination);
                continue;
            }

            variants.Definitions[c] = node;
            var isFallback = node["role"] is JsonValue role && role.TryGetValue<string>(out var r) && r == "fallback";
            if (fallbackRole is { } expected && expected != isFallback)
            {
                _diagnostics.Error("NODE_ROLE_CHANGED", _pipelineLabel, pointer + "/role",
                    $"Experiments cannot turn '{nodeId}' between a timeline node and a fallback.", "add a new node id instead");
                variants.Failed[c] = true;
                Settle(start, armsInCombination);
                continue;
            }

            fallbackRole = isFallback;
            var hash = ContentHash.Compute(node);
            if (!byHash.TryGetValue(hash, out var compiled))
            {
                var result = _builder.CompileNode(nodeId, node, _pipelineLabel);
                compiled = (result, result is null);
                byHash[hash] = compiled;
            }

            variants.Variants[c] = compiled.Node;
            variants.Failed[c] = compiled.Failed;
            Settle(start, armsInCombination);
        }

        variants.IsFallback = fallbackRole == true;
    }

    /// <summary>Compiles every tap variant (design 8.3, 12.3). Runs after the nodes: <c>columns_from</c> needs them.</summary>
    private void CompileTapVariants()
    {
        var taps = (JsonObject)_view.Document["log_taps"]!;
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in taps)
        {
            ids.Add(id);
        }

        foreach (var arm in _arms)
        {
            if (arm.Valid)
            {
                ids.UnionWith(arm.TouchedTaps);
            }
        }

        foreach (var tapId in ids)
        {
            var pointer = "/log_taps/" + JsonPointer.Escape(tapId);

            // columns_from logs a model node's inputs: the tap must vary with every layer that changes that node, or a
            // treatment would serve one input contract while its training log records another (design 6.9, 8.3).
            var models = ColumnsFromNodes(tapId, taps[tapId] as JsonObject);
            if (BuildSpace(arm => arm.TouchedTaps.Contains(tapId) || TouchesAny(arm, models), $"Tap '{tapId}'", pointer) is not { } space)
            {
                continue;
            }

            var variants = new TapVariants
            {
                TapId = tapId,
                Layers = space.Layers,
                LocalArms = space.LocalArms,
                LocalIndex = space.LocalIndex,
                Radix = space.Radix,
                Variants = new CompiledTap?[space.Count],
                Failed = new bool[space.Count],
            };
            _taps[tapId] = variants;
            var baseline = taps[tapId]?.DeepClone();
            var byHash = new Dictionary<string, (CompiledTap? Tap, bool Failed)>(StringComparer.Ordinal);
            for (var c = 0; c < space.Count; c++)
            {
                var armsInCombination = ArmPatches(variants.ArmsOf(c));
                var definition = Merge(baseline, armsInCombination, "log_taps", tapId);
                if (definition is null)
                {
                    continue;
                }

                var start = _diagnostics.Count;
                if (definition is not JsonObject tap)
                {
                    _diagnostics.Error("TAP_INVALID", _pipelineLabel, pointer, "A tap must be an object.");
                    variants.Failed[c] = true;
                    Settle(start, armsInCombination);
                    continue;
                }

                var key = ContentHash.Compute(tap);
                foreach (var model in models)
                {
                    key += "|" + model + ":" + (_variants.TryGetValue(model, out var mv) ? mv.IndexOfArms(armsInCombination) : -1);
                }

                if (!byHash.TryGetValue(key, out var compiled))
                {
                    var result = _builder.CompileTap(tapId, tap, _pipelineLabel, nodeId => ResolveVariant(nodeId, armsInCombination));
                    // A node no combination has, with a suggestion for the typo. One that only some combinations have is
                    // judged in each (PlanBuilder.ValidateDataFlow), as a compile of that combination alone judges it.
                    if (result?.AttachAfter is { } after && !_variants.ContainsKey(after))
                    {
                        _diagnostics.Error("TAP_UNKNOWN_NODE", _pipelineLabel, pointer + "/attach/after", $"Tap '{tapId}' attaches after unknown node '{after}'.",
                            NameSuggestions.DidYouMean(after, _variants.Keys) ?? "attach after a node id from /nodes, or use {\"output\": true}");
                        result = null;
                    }

                    compiled = (result, result is null);
                    byHash[key] = compiled;
                }

                variants.Variants[c] = compiled.Tap;
                variants.Failed[c] = compiled.Failed;
                Settle(start, armsInCombination);
            }
        }
    }

    /// <summary>Model nodes named by <c>columns_from</c> in the baseline tap or in any arm's patch of it.</summary>
    private SortedSet<string> ColumnsFromNodes(string tapId, JsonObject? baseline)
    {
        var nodes = new SortedSet<string>(StringComparer.Ordinal);
        Add(baseline);
        foreach (var arm in _arms)
        {
            if (arm.Valid && arm.TouchedTaps.Contains(tapId) && arm.Patch["log_taps"] is JsonObject patched)
            {
                Add(patched[tapId] as JsonObject);
            }
        }

        return nodes;

        void Add(JsonObject? tap)
        {
            if (tap?["columns_from"] is JsonValue value && value.TryGetValue<string>(out var from) && from.EndsWith(".inputs", StringComparison.Ordinal))
            {
                nodes.Add(from[..^7]);
            }
        }
    }

    private static bool TouchesAny(ArmPatch arm, SortedSet<string> nodes)
    {
        foreach (var node in nodes)
        {
            if (arm.TouchedNodes.Contains(node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The variant of <paramref name="nodeId"/> in effect for an arm combination, or null when absent there.</summary>
    private CompiledNode? ResolveVariant(string nodeId, IReadOnlyList<ArmPatch> arms) =>
        _variants.TryGetValue(nodeId, out var variants) ? variants.Variants[variants.IndexOfArms(arms)] : null;

    private LogPlan BuildLogPlan()
    {
        var slots = new List<TapSlot>();
        foreach (var (tapId, v) in _taps)
        {
            slots.Add(new TapSlot
            {
                TapId = tapId,
                TouchingLayers = v.Layers,
                LocalIndex = v.LocalIndex,
                Radix = v.Radix,
                Variants = v.Variants,
            });
        }

        var groups = new CompiledFunnelGroup[_builder.FunnelGroups.Count];
        for (var i = 0; i < groups.Length; i++)
        {
            groups[i] = _builder.FunnelGroups[i];
        }

        return new LogPlan { Groups = groups, Taps = slots.ToArray() };
    }

    private (TimelineSlot[] Timeline, Dictionary<string, TimelineSlot> Fallbacks) BuildSlots()
    {
        var timeline = new List<TimelineSlot>();
        var fallbacks = new Dictionary<string, TimelineSlot>(StringComparer.Ordinal);
        foreach (var (nodeId, v) in _variants)
        {
            if (v.IsFallback)
            {
                fallbacks[nodeId] = NewSlot(v, 0, (CompiledNode?[])v.Variants.Clone());
                continue;
            }

            var orders = new SortedSet<int>();
            foreach (var node in v.Variants)
            {
                if (node?.Order is { } order)
                {
                    orders.Add(order);
                }
            }

            // A moved node occupies one position per order, with complementary variant arrays (design 12.4).
            foreach (var order in orders)
            {
                var selected = new CompiledNode?[v.Variants.Length];
                for (var i = 0; i < selected.Length; i++)
                {
                    selected[i] = v.Variants[i]?.Order == order ? v.Variants[i] : null;
                }

                timeline.Add(NewSlot(v, order, selected));
            }
        }

        timeline.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.NodeId, b.NodeId));
        return (timeline.ToArray(), fallbacks);
    }

    private static TimelineSlot NewSlot(NodeVariants v, int order, CompiledNode?[] variants) => new()
    {
        Order = order,
        NodeId = v.NodeId,
        TouchingLayers = v.Layers,
        LocalIndex = v.LocalIndex,
        Radix = v.Radix,
        Variants = variants,
    };

    // ------------------------------------------------------------------ trigger sites (design 12.6)

    private void AssignTriggerSites(TimelineSlot[] timeline, Dictionary<string, TimelineSlot> fallbacks, OutputPlan? output, Dictionary<int, SortedSet<int>> leafExperiments)
    {
        foreach (var slot in timeline)
        {
            AssignSite(slot, isFallback: false);
        }

        foreach (var (_, slot) in fallbacks)
        {
            AssignSite(slot, isFallback: true);
        }

        // Parameter sites: a node variant (or the output) that reads a leaf some arm of E overrides.
        foreach (var node in _builder.Nodes)
        {
            node.ParamExperiments = ExperimentsReading(node.ReadParams, leafExperiments);
        }

        if (output is not null)
        {
            var reads = new List<int> { output.Limit.Id };
            foreach (var (_, expression) in output.Columns)
            {
                foreach (var symbol in expression.Reads)
                {
                    if (symbol.Kind == SymbolKind.Param)
                    {
                        reads.Add(symbol.Id);
                    }
                }
            }

            output.ParamExperiments = ExperimentsReading(reads, leafExperiments);
        }
    }

    private static int[] ExperimentsReading(IReadOnlyList<int> leaves, Dictionary<int, SortedSet<int>> leafExperiments)
    {
        SortedSet<int>? result = null;
        foreach (var leaf in leaves)
        {
            if (leafExperiments.TryGetValue(leaf, out var set))
            {
                (result ??= new SortedSet<int>()).UnionWith(set);
            }
        }

        return result is null ? Array.Empty<int>() : ToArray(result);
    }

    private void AssignSite(TimelineSlot slot, bool isFallback)
    {
        var experiments = new SortedSet<int>();
        var variants = _variants[slot.NodeId];
        foreach (var arms in variants.LocalArms)
        {
            foreach (var arm in arms)
            {
                experiments.Add(_arms[arm].ExperimentIndex);
            }
        }

        var sites = new List<int>();
        var conditions = new List<CompiledExpression?>();
        foreach (var experiment in experiments)
        {
            // The site condition is the OR of each arm's effective node here: absent → false, no `when` → true.
            var any = false;
            var always = false;
            var sources = new List<string>();
            CompiledExpression? single = null;
            foreach (var armIndex in _experiments[experiment].Arms)
            {
                var node = slot.Select(OnlyArm(armIndex));
                if (node is null)
                {
                    continue;
                }

                any = true;
                if (node.When is null || isFallback)
                {
                    always = true;
                    break;
                }

                var source = node.Definition["when"]!.GetValue<string>();
                if (!sources.Contains(source))
                {
                    sources.Add(source);
                    single = node.When;
                }
            }

            if (!any)
            {
                continue;
            }

            CompiledExpression? condition = null;
            if (!always)
            {
                condition = sources.Count == 1
                    ? single
                    : _builder.CompileRequestCondition(JoinOr(sources), "experiment:" + _experiments[experiment].Id, "/nodes/" + JsonPointer.Escape(slot.NodeId) + "/when");
                if (condition is null)
                {
                    continue;
                }
            }

            sites.Add(experiment);
            conditions.Add(condition);
        }

        slot.SiteExperiments = sites.ToArray();
        slot.SiteConditions = conditions.ToArray();
    }

    private static string JoinOr(List<string> sources)
    {
        var builder = new StringBuilder();
        foreach (var source in sources)
        {
            if (builder.Length > 0)
            {
                builder.Append(" || ");
            }

            builder.Append('(').Append(source).Append(')');
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private RequestArms OnlyArm(int armIndex)
    {
        var byLayer = new int[_layers.Count];
        Array.Fill(byLayer, -1);
        byLayer[_arms[armIndex].LayerIndex] = armIndex;
        return new RequestArms(byLayer);
    }

    private List<ArmPatch> ArmPatches(List<int> indices)
    {
        var list = new List<ArmPatch>(indices.Count);
        foreach (var i in indices)
        {
            list.Add(_arms[i]);
        }

        return list;
    }

    /// <summary>
    /// Moves diagnostics recorded since <paramref name="start"/> back into the bag, deduplicated, and attributes each
    /// to the arm whose patch touches its location so the message says where to fix it.
    /// </summary>
    private void Settle(int start, IReadOnlyList<ArmPatch> arms)
    {
        if (_diagnostics.Count == start)
        {
            return;
        }

        foreach (var d in _diagnostics.RemoveFrom(start))
        {
            Report(d, arms);
        }
    }

    /// <summary>
    /// Reports a diagnostic once per root cause. The dedupe key is (owning arm, code, view pointer, message), so the
    /// same baseline problem seen again under arm combinations, or an arm's problem found by both the unified and the
    /// per-arm reference compile, is reported once.
    /// </summary>
    private void Report(ConfigDiagnostic d, IReadOnlyList<ArmPatch> arms)
    {
        var owner = string.Empty;
        var viewPointer = d.Pointer;
        var final = d;
        if (d.Artifact.StartsWith("experiment:", StringComparison.Ordinal))
        {
            owner = d.Artifact;
            foreach (var arm in _arms)
            {
                if (d.Artifact == arm.Artifact && (d.Pointer == arm.PointerPrefix || d.Pointer.StartsWith(arm.PointerPrefix + "/", StringComparison.Ordinal)))
                {
                    owner = arm.Id;
                    viewPointer = d.Pointer[arm.PointerPrefix.Length..];
                    break;
                }
            }
        }
        else if (arms.Count > 0)
        {
            ArmPatch? match = null;
            var matches = 0;
            foreach (var arm in arms)
            {
                if (arm.Relates(d.Pointer))
                {
                    match = arm;
                    matches++;
                }
            }

            var ids = new List<string>();
            foreach (var arm in arms)
            {
                if (matches != 1 || arm != match)
                {
                    ids.Add(arm.Id);
                }
            }

            if (matches == 1)
            {
                owner = match!.Id;
                var message = ids.Count == 0 ? d.Message : $"{d.Message} (together with {string.Join(", ", ids)})";
                final = d with { Artifact = match.Artifact, Pointer = match.PointerPrefix + d.Pointer, Message = message };
            }
            else
            {
                final = d with { Artifact = $"{d.Artifact} with {string.Join(" + ", ids)}" };
            }
        }

        if (_reported.Add(owner + "\n" + d.Code + "\n" + viewPointer + "\n" + d.Message))
        {
            _diagnostics.Add(final);
        }
    }

    /// <summary>Attributes schema diagnostics about add-only additions (e.g. a bad column type) to the arm that added them.</summary>
    private void SettleAdditions(int start)
    {
        if (_diagnostics.Count == start)
        {
            return;
        }

        foreach (var d in _diagnostics.RemoveFrom(start))
        {
            ArmPatch? arm = null;
            if (JsonPointer.TryParse(d.Pointer, out var pointer) && pointer.Depth >= 2
                && _additions.TryGetValue(pointer.Tokens[0], out var entries) && entries.TryGetValue(pointer.Tokens[1], out var added))
            {
                arm = added.Arm;
            }

            Report(arm is null ? d : d with { Artifact = arm.Artifact, Pointer = arm.PointerPrefix + d.Pointer }, Array.Empty<ArmPatch>());
        }
    }

    private LayerInfo[] LayerInfos()
    {
        var layers = new LayerInfo[_layers.Count];
        for (var i = 0; i < layers.Length; i++)
        {
            layers[i] = new LayerInfo(i, _layers[i]);
        }

        return layers;
    }

    private ExperimentInfo[] ExperimentInfos()
    {
        var experiments = new ExperimentInfo[_experiments.Count];
        for (var i = 0; i < experiments.Length; i++)
        {
            var (id, layer, arms) = _experiments[i];
            experiments[i] = new ExperimentInfo(i, id, layer, arms);
        }

        return experiments;
    }

    private ArmInfo[] ArmInfos()
    {
        var arms = new ArmInfo[_arms.Count];
        foreach (var arm in _arms)
        {
            arms[arm.Index] = new ArmInfo(arm.Index, arm.Id, arm.ArmName, arm.ExperimentIndex, arm.LayerIndex);
        }

        return arms;
    }

    private List<NodeVariantReport> NodeReports()
    {
        var reports = new List<NodeVariantReport>();
        foreach (var (nodeId, v) in _variants)
        {
            var layers = new List<string>();
            foreach (var layer in v.Layers)
            {
                layers.Add(_layers[layer]);
            }

            var distinct = new HashSet<CompiledNode>(ReferenceEqualityComparer.Instance);
            var orders = new SortedSet<int>();
            foreach (var node in v.Variants)
            {
                if (node is not null)
                {
                    distinct.Add(node);
                    if (node.Order is { } order)
                    {
                        orders.Add(order);
                    }
                }
            }

            reports.Add(new NodeVariantReport(nodeId, layers, v.Variants.Length, distinct.Count, ToArray(orders)));
        }

        return reports;
    }

    private List<ArmReport> ArmReports()
    {
        var reports = new List<ArmReport>();
        foreach (var arm in _arms)
        {
            var paths = new List<string>();
            foreach (var op in arm.Footprint)
            {
                paths.Add(op.ToString());
            }

            var leaves = new List<string>();
            foreach (var ov in arm.ParamOverrides)
            {
                leaves.Add(ov.Leaf);
            }

            var owners = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (prefix, teams) in _request.Policy.Owners)
            {
                if (arm.Relates(prefix))
                {
                    owners.UnionWith(teams);
                }
            }

            reports.Add(new ArmReport(arm.Id, _layers[arm.LayerIndex], paths, leaves, new List<string>(arm.TouchedNodes), new List<string>(owners)));
        }

        return reports;
    }

    private static int[] ToArray(SortedSet<int> set)
    {
        var array = new int[set.Count];
        set.CopyTo(array);
        return array;
    }
}
