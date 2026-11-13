using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>A feature bound into a plan: where it lives at runtime and how it is produced.</summary>
public sealed class FeatureRuntimeInfo
{
    public required int Index { get; init; }

    public required FeatureDefinition Definition { get; init; }

    /// <summary>Final type (derived features without a default take nullability from their expression).</summary>
    public required ColumnType Type { get; init; }

    /// <summary>Physical slot in the candidate table (candidate features), otherwise -1.</summary>
    public int Slot { get; init; } = -1;

    /// <summary>Index into the request's context values (context features), otherwise -1.</summary>
    public int ContextId { get; init; } = -1;

    /// <summary>Compiled expression of a derived feature.</summary>
    public CompiledExpression? Expression { get; init; }

    /// <summary>Features that must be available first: derived inputs, or the foreign key of the entity.</summary>
    public int[] Dependencies { get; set; } = Array.Empty<int>();

    /// <summary>Fetch group of a physical feature (-1 for derived).</summary>
    public int GroupIndex { get; internal set; } = -1;

    /// <summary>Field index inside the group's staging buffer.</summary>
    public int FieldIndex { get; internal set; } = -1;

    /// <summary>Fetch wave: 0 when keyed by <c>entity_id</c>/context, n+1 when keyed by a wave-n foreign key.</summary>
    public int Wave { get; internal set; }

    public bool IsContext => Definition.IsContext;

    public bool IsDerived => Definition.IsDerived;
}

/// <summary>Features fetched together: one source × one entity (design 6.8: same source within a wave is merged).</summary>
public sealed class FetchGroup
{
    public required int Index { get; init; }

    public required string SourceName { get; init; }

    public required EntityDefinition Entity { get; init; }

    public required IFeatureSourceConnector Connector { get; init; }

    public required KeyShape Shape { get; init; }

    public int TimeoutMs { get; init; } = 50;

    public int MaxBatch { get; init; } = 1000;

    /// <summary>Feature providing the candidate key (foreign keys), or -1 for <c>entity_id</c>.</summary>
    public int KeyFeatureIndex { get; init; } = -1;

    /// <summary>Context value providing the context key (context and cross entities), or -1.</summary>
    public int ContextKeyId { get; init; } = -1;

    public required int[] FeatureIndices { get; init; }

    public required CompiledFieldBinding[] Fields { get; init; }

    public int Wave { get; internal set; }
}

/// <summary>
/// Immutable, plan-level feature layout: runtime info per feature and the fetch groups (design 6.8). Built once when a
/// plan is compiled and shared by all its requests.
/// </summary>
public sealed class FeaturePlan
{
    private FeaturePlan(FeatureRuntimeInfo[] features, FetchGroup[] groups, int[] derivedOrder)
    {
        Features = features;
        Groups = groups;
        DerivedOrder = derivedOrder;
    }

    public IReadOnlyList<FeatureRuntimeInfo> Features { get; }

    public IReadOnlyList<FetchGroup> Groups { get; }

    /// <summary>Derived features in dependency order.</summary>
    public IReadOnlyList<int> DerivedOrder { get; }

    public int MaxWave
    {
        get
        {
            var max = 0;
            foreach (var g in Groups)
            {
                max = Math.Max(max, g.Wave);
            }

            return max;
        }
    }

    /// <summary>
    /// Builds the plan: groups physical features by (source, entity), creates connectors from the bindings and
    /// computes fetch waves. Problems (unbound sources, unsupported key shapes) are reported to <paramref name="diagnostics"/>.
    /// </summary>
    public static FeaturePlan Create(
        IReadOnlyList<FeatureRuntimeInfo> features,
        SourceBindingArtifact bindings,
        FeatureConnectorRegistry connectors,
        Func<string, int> contextIdOfRequestInput,
        DiagnosticBag diagnostics,
        string? baseDirectory)
    {
        var byName = new Dictionary<string, FeatureRuntimeInfo>(StringComparer.Ordinal);
        var grouped = new SortedDictionary<(string Source, string Entity), List<FeatureRuntimeInfo>>(GroupKeyComparer.Instance);
        foreach (var f in features)
        {
            byName[f.Definition.Name] = f;
            if (f.IsDerived)
            {
                continue;
            }

            var key = (f.Definition.Source!, f.Definition.Entity.Name);
            if (!grouped.TryGetValue(key, out var list))
            {
                list = new List<FeatureRuntimeInfo>();
                grouped[key] = list;
            }

            list.Add(f);
        }

        var groups = new List<FetchGroup>();
        var connectorCache = new Dictionary<string, IFeatureSourceConnector>(StringComparer.Ordinal);

        foreach (var (groupKey, memberList) in grouped)
        {
            var sourceName = groupKey.Source;
            memberList.Sort(static (a, b) => string.CompareOrdinal(a.Definition.Name, b.Definition.Name));
            var members = memberList.ToArray();
            var entity = members[0].Definition.Entity;
            var pointer = members[0].Definition.Pointer + "/source";
            if (!bindings.Sources.TryGetValue(sourceName, out var binding))
            {
                diagnostics.Error("SOURCE_UNBOUND", "catalog", pointer,
                    $"Feature source '{sourceName}' has no binding in environment '{bindings.Environment}'.",
                    NameSuggestions.DidYouMean(sourceName, bindings.Sources.Keys) ?? "add it under 'sources' in the bindings artifact");
                continue;
            }

            if (binding.Mode is not (null or "fetch"))
            {
                diagnostics.Error("SOURCE_MODE_UNSUPPORTED", $"bindings:{bindings.Environment}", "/sources/" + sourceName + "/mode",
                    $"Supply mode '{binding.Mode}' is not available in this build yet.", "use mode 'fetch'");
                continue;
            }

            if (!connectorCache.TryGetValue(sourceName, out var connector))
            {
                var type = binding.Connector ?? string.Empty;
                if (!connectors.TryGet(type, out var factory))
                {
                    diagnostics.Error("CONNECTOR_UNKNOWN", $"bindings:{bindings.Environment}", "/sources/" + sourceName + "/connector",
                        $"Connector type '{type}' is not registered in this binary.",
                        NameSuggestions.DidYouMean(type, connectors.Types) ?? $"registered: {string.Join(", ", connectors.Types)}");
                    continue;
                }

                try
                {
                    connector = factory.Create(sourceName, binding.Settings, baseDirectory);
                }
                catch (Exception ex) when (ex is FormatException or IOException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    diagnostics.Error("CONNECTOR_CONFIG", $"bindings:{bindings.Environment}", "/sources/" + sourceName, ex.Message);
                    continue;
                }

                connectorCache[sourceName] = connector;
            }

            var shape = entity.Kind switch
            {
                EntityKind.Context => KeyShape.Single,
                EntityKind.Cross => KeyShape.Cross,
                _ => KeyShape.Many,
            };
            if (!connector.Supports(shape))
            {
                diagnostics.Error("CONNECTOR_SHAPE", $"bindings:{bindings.Environment}", "/sources/" + sourceName,
                    $"Connector '{connector.ConnectorType}' does not support {shape} lookups needed by entity '{entity.Name}'.");
                continue;
            }

            var keyFeature = -1;
            if (entity.KeyIsFeature)
            {
                if (!byName.TryGetValue(entity.CandidateKeyColumn!, out var kf))
                {
                    diagnostics.Error("ENTITY_BAD_KEY", "catalog", "/entities/" + entity.Name, $"Key feature '{entity.CandidateKeyColumn}' is not available.");
                    continue;
                }

                keyFeature = kf.Index;
            }

            var featureIndices = new int[members.Length];
            var fields = new CompiledFieldBinding[members.Length];
            for (var i = 0; i < members.Length; i++)
            {
                featureIndices[i] = members[i].Index;
                fields[i] = new CompiledFieldBinding(members[i].Definition.Field!, i, members[i].Type);
            }

            var group = new FetchGroup
            {
                Index = groups.Count,
                SourceName = sourceName,
                Entity = entity,
                Connector = connector,
                Shape = shape,
                TimeoutMs = binding.TimeoutMs ?? 50,
                MaxBatch = binding.MaxBatch is > 0 ? binding.MaxBatch.Value : 1000,
                KeyFeatureIndex = keyFeature,
                ContextKeyId = entity.ContextKeyInput is null ? -1 : contextIdOfRequestInput(entity.ContextKeyInput),
                FeatureIndices = featureIndices,
                Fields = fields,
            };
            for (var i = 0; i < members.Length; i++)
            {
                members[i].GroupIndex = group.Index;
                members[i].FieldIndex = i;
                if (keyFeature >= 0)
                {
                    members[i].Dependencies = new[] { keyFeature };
                }
            }

            groups.Add(group);
        }

        // Waves: wave(group) = 0 for entity_id/context keys, otherwise wave of the key feature's group + 1.
        var all = new FeatureRuntimeInfo[features.Count];
        for (var i = 0; i < all.Length; i++)
        {
            all[i] = features[i];
        }

        foreach (var group in groups)
        {
            group.Wave = WaveOf(group, groups, all, depth: 0);
            foreach (var f in group.FeatureIndices)
            {
                all[f].Wave = group.Wave;
            }
        }

        var derivedOrder = TopologicalDerivedOrder(all, diagnostics);
        return new FeaturePlan(all, groups.ToArray(), derivedOrder);
    }

    /// <summary>The requested features plus everything they depend on, in dependency order.</summary>
    public List<int> Closure(IEnumerable<int> roots)
    {
        var seen = new HashSet<int>();
        var order = new List<int>();
        foreach (var r in roots)
        {
            Visit(r);
        }

        return order;

        void Visit(int f)
        {
            if (!seen.Add(f))
            {
                return;
            }

            foreach (var d in Features[f].Dependencies)
            {
                Visit(d);
            }

            order.Add(f);
        }
    }

    private static int WaveOf(FetchGroup group, List<FetchGroup> groups, FeatureRuntimeInfo[] features, int depth)
    {
        if (group.KeyFeatureIndex < 0 || depth > 16)
        {
            return 0;
        }

        var keyFeature = features[group.KeyFeatureIndex];
        if (keyFeature.IsDerived || keyFeature.GroupIndex < 0)
        {
            return 1;
        }

        return WaveOf(groups[keyFeature.GroupIndex], groups, features, depth + 1) + 1;
    }

    private static int[] TopologicalDerivedOrder(FeatureRuntimeInfo[] features, DiagnosticBag diagnostics)
    {
        var order = new List<int>();
        var state = new byte[features.Length]; // 0 = new, 1 = visiting, 2 = done
        foreach (var f in features)
        {
            if (f.IsDerived)
            {
                Visit(f.Index);
            }
        }

        return order.ToArray();

        void Visit(int i)
        {
            if (state[i] == 2)
            {
                return;
            }

            if (state[i] == 1)
            {
                diagnostics.Error("FEATURE_CYCLE", "catalog", features[i].Definition.Pointer,
                    $"Derived feature '{features[i].Definition.Name}' depends on itself.");
                return;
            }

            state[i] = 1;
            foreach (var d in features[i].Dependencies)
            {
                Visit(d);
            }

            state[i] = 2;
            if (features[i].IsDerived)
            {
                order.Add(i);
            }
        }
    }
}

/// <summary>Connector factories available in this binary, keyed by connector type (<c>name@version</c>).</summary>
public sealed class FeatureConnectorRegistry
{
    private readonly Dictionary<string, IFeatureConnectorFactory> _factories = new(StringComparer.Ordinal);

    public IEnumerable<string> Types
    {
        get
        {
            var types = new List<string>(_factories.Keys);
            types.Sort(StringComparer.Ordinal);
            return types;
        }
    }

    /// <summary>A registry with the built-in connectors (<c>memory@1</c>).</summary>
    public static FeatureConnectorRegistry CreateDefault() => new FeatureConnectorRegistry().Add(new MemoryFeatureConnectorFactory());

    public FeatureConnectorRegistry Add(IFeatureConnectorFactory factory)
    {
        _factories[factory.ConnectorType] = factory;
        return this;
    }

    public bool TryGet(string type, out IFeatureConnectorFactory factory) => _factories.TryGetValue(type, out factory!);
}

internal sealed class GroupKeyComparer : IComparer<(string Source, string Entity)>
{
    public static readonly GroupKeyComparer Instance = new();

    public int Compare((string Source, string Entity) x, (string Source, string Entity) y)
    {
        var c = string.CompareOrdinal(x.Source, y.Source);
        return c != 0 ? c : string.CompareOrdinal(x.Entity, y.Entity);
    }
}
