using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Features.Bindings;
using StrataRec.Features.Cache;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

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

    /// <summary>Position of the feature among its group's members (<see cref="FetchGroup.Fields"/>).</summary>
    public int FieldIndex { get; internal set; } = -1;

    /// <summary>
    /// Scheduling wave, derived from the dependency graph (design 6.8 step 3). For a physical candidate feature, the
    /// wave its fetch runs in: 0 when keyed by <c>entity_id</c>/context, w + 1 when keyed by a feature that becomes
    /// available in wave w. For a derived feature, the wave after whose fetches it is computed: the latest wave of its
    /// inputs, or -1 when it needs no candidate fetch at all (only <c>entity_id</c>, request inputs or context
    /// features), in which case it is computed before wave 0.
    /// </summary>
    /// <remarks>
    /// This places a derived foreign key after its own inputs and strictly before the groups keyed by it; treating
    /// every derived key as "wave 1" would fetch those groups before the key exists.
    /// </remarks>
    public int Wave { get; internal set; }

    public bool IsContext => Definition.IsContext;

    public bool IsDerived => Definition.IsDerived;

    /// <summary>
    /// Tags of <see cref="StrataRecMetrics.ColumnNonFinite"/> for this feature (<c>stratarec.column</c>), built once per
    /// plan on first use: non-finite values are rare, and features are not produced by a node.
    /// </summary>
    internal KeyValuePair<string, object?>[] NonFiniteTags =>
        _nonFiniteTags ?? Interlocked.CompareExchange(ref _nonFiniteTags, [new(StrataRecTags.Column, Definition.Name)], null) ?? _nonFiniteTags!;

    /// <summary>
    /// Tags of <see cref="StrataRecMetrics.SourceValues"/> for this physical feature: its source, its name, and the
    /// outcome. Built once per plan on first use, when a listener is enabled: a feature belongs to one fetch group, so
    /// these are the tags of that group's field.
    /// </summary>
    internal SourceValueTags ValueTags =>
        _valueTags ?? Interlocked.CompareExchange(ref _valueTags, new SourceValueTags(Definition.Source!, Definition.Name), null) ?? _valueTags!;

    private KeyValuePair<string, object?>[]? _nonFiniteTags;
    private SourceValueTags? _valueTags;
}

/// <summary>The two tag sets of <see cref="StrataRecMetrics.SourceValues"/> for one feature of a source.</summary>
internal sealed class SourceValueTags(string source, string feature)
{
    public KeyValuePair<string, object?>[] Present { get; } = Tags(source, feature, "present");

    public KeyValuePair<string, object?>[] Missing { get; } = Tags(source, feature, "missing");

    private static KeyValuePair<string, object?>[] Tags(string source, string feature, string outcome) =>
    [
        new(StrataRecTags.Source, source),
        new(StrataRecTags.SourceKind, "feature"),
        new(StrataRecTags.Feature, feature),
        new(StrataRecTags.Outcome, outcome),
    ];
}

/// <summary>
/// Features that are supplied together: one source × one entity (design 6.8: same source within a wave is merged).
/// How is the group's <see cref="Supply"/>: a fetched group calls its connector, a local group reads its table (design 6.6).
/// </summary>
/// <remarks>
/// A group is the unit of merging, not of fetching: each fetch or read carries only the members the request needs at
/// that point (late materialization), so other members can be supplied later, for other rows, or never.
/// </remarks>
public sealed class FetchGroup
{
    public required int Index { get; init; }

    public required string SourceName { get; init; }

    public required EntityDefinition Entity { get; init; }

    /// <summary>The mode and what it holds (a connector, a table), as the bindings resolved it.</summary>
    public required GroupSupply Supply { get; init; }

    public required KeyShape Shape { get; init; }

    /// <summary>Feature providing the candidate key (foreign keys), or -1 for <c>entity_id</c>.</summary>
    public int KeyFeatureIndex { get; init; } = -1;

    /// <summary>Context value providing the context key (context and cross entities), or -1.</summary>
    public int ContextKeyId { get; init; } = -1;

    public required int[] FeatureIndices { get; init; }

    public required CompiledFieldBinding[] Fields { get; init; }

    public int Wave { get; internal set; }

    /// <summary>Metric tags and span name of this source (design 13.1), built once per plan on first use.</summary>
    public IoTarget Io => _io ?? Interlocked.CompareExchange(ref _io, new IoTarget(IoKind.FeatureSource, SourceName), null) ?? _io!;

    private IoTarget? _io;
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
        foreach (var g in groups)
        {
            MaxWave = Math.Max(MaxWave, g.Wave);
            HasTables |= g.Supply.Table is not null;
            _ = g.Io; // build the source's metric tags with the plan, not on its first request
        }
    }

    public IReadOnlyList<FeatureRuntimeInfo> Features { get; }

    public IReadOnlyList<FetchGroup> Groups { get; }

    /// <summary>Derived features in dependency order.</summary>
    public IReadOnlyList<int> DerivedOrder { get; }

    /// <summary>Highest fetch wave of any group (0 when nothing is keyed by a feature).</summary>
    public int MaxWave { get; }

    /// <summary>Whether a group holds a table, which a request may read: it then readies the buffers of its reads up front.</summary>
    public bool HasTables { get; }

    /// <summary>
    /// Builds the plan: groups physical features by (source, entity), resolves each group's supply from the bindings
    /// (<see cref="FeatureSourceResolver"/>: a connector, or a local table from <paramref name="localTables"/>) and
    /// computes fetch waves. Problems (unbound sources, wrong settings, unsupported key shapes) are reported to
    /// <paramref name="diagnostics"/>.
    /// </summary>
    /// <param name="updateSources">Update-source factories for local bindings; null has the built-in ones.</param>
    /// <param name="localTables">Where local tables come from; null gives tables that are never loaded (compiling without serving).</param>
    /// <param name="instances">Where connectors and distributed cache stores come from; null creates them for this plan alone.</param>
    /// <param name="cacheStores">Distributed cache store factories for cache bindings; null has none.</param>
    /// <param name="clock">The clock of the cache tables the plan creates; null is the system clock.</param>
    public static FeaturePlan Create(
        IReadOnlyList<FeatureRuntimeInfo> features,
        SourceBindingArtifact bindings,
        FeatureConnectorRegistry connectors,
        Func<string, int> contextIdOfRequestInput,
        DiagnosticBag diagnostics,
        string? baseDirectory,
        FeatureUpdateSourceRegistry? updateSources = null,
        ILocalTableProvider? localTables = null,
        IBindingInstances? instances = null,
        FeatureCacheStoreRegistry? cacheStores = null,
        TimeProvider? clock = null)
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
        var resolver = new FeatureSourceResolver(
            bindings, connectors, updateSources ?? FeatureUpdateSourceRegistry.CreateDefault(), cacheStores ?? new FeatureCacheStoreRegistry(),
            localTables ?? UnloadedLocalTables.Instance, instances ?? UnsharedInstances.Instance, baseDirectory, clock ?? TimeProvider.System, diagnostics);

        foreach (var (groupKey, memberList) in grouped)
        {
            var sourceName = groupKey.Source;
            memberList.Sort(static (a, b) => string.CompareOrdinal(a.Definition.Name, b.Definition.Name));
            var members = memberList.ToArray();
            var entity = members[0].Definition.Entity;
            if (resolver.Source(sourceName, members[0].Definition.Pointer + "/source") is not { } supply)
            {
                continue;
            }

            var shape = entity.Kind switch
            {
                EntityKind.Context => KeyShape.Single,
                EntityKind.Cross => KeyShape.Cross,
                _ => KeyShape.Many,
            };
            if (resolver.Group(supply, entity, members, shape) is not { } groupSupply)
            {
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
                Supply = groupSupply,
                Shape = shape,
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

        var all = new FeatureRuntimeInfo[features.Count];
        for (var i = 0; i < all.Length; i++)
        {
            all[i] = features[i];
        }

        // Cycles are reported here; the wave computation below only has to terminate on them.
        var derivedOrder = TopologicalDerivedOrder(all, diagnostics);
        AssignWaves(all, groups);
        return new FeaturePlan(all, groups.ToArray(), derivedOrder);
    }

    /// <summary>The requested features plus everything they depend on, in dependency order.</summary>
    public List<int> Closure(IEnumerable<int> roots)
    {
        var order = new int[Features.Count];
        var count = Closure(roots, order, new bool[Features.Count]);
        var result = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(order[i]);
        }

        return result;
    }

    /// <summary>
    /// <see cref="Closure(IEnumerable{int})"/> into caller-provided buffers (the request's rented arrays), so a request
    /// computes its fetch closures without allocating: <paramref name="order"/> receives the closure, of the returned
    /// length; <paramref name="seen"/> must be all false and is left marking the closure.
    /// </summary>
    internal int Closure(IEnumerable<int> roots, int[] order, bool[] seen)
    {
        var count = 0;
        switch (roots)
        {
            // Typed loops: an interface foreach over an array or list boxes its enumerator.
            case int[] array:
                foreach (var r in array)
                {
                    Visit(r, order, seen, ref count);
                }

                break;
            case List<int> list:
                foreach (var r in list)
                {
                    Visit(r, order, seen, ref count);
                }

                break;
            default:
                foreach (var r in roots)
                {
                    Visit(r, order, seen, ref count);
                }

                break;
        }

        return count;
    }

    private void Visit(int feature, int[] order, bool[] seen, ref int count)
    {
        if (seen[feature])
        {
            return;
        }

        seen[feature] = true;
        foreach (var d in Features[feature].Dependencies)
        {
            Visit(d, order, seen, ref count);
        }

        order[count++] = feature;
    }

    /// <summary>
    /// Assigns <see cref="FeatureRuntimeInfo.Wave"/> to every feature and <see cref="FetchGroup.Wave"/> to every group
    /// from the full dependency graph: ready(physical) = wave of its group = 0 for <c>entity_id</c>/context keys,
    /// otherwise ready(key) + 1; ready(derived) = max ready(input), or -1 without candidate fetch inputs; context
    /// features are -1 (they are fetched with the request, before any candidate wave).
    /// </summary>
    internal static void AssignWaves(FeatureRuntimeInfo[] features, IReadOnlyList<FetchGroup> groups)
    {
        const int Unknown = int.MinValue;
        var ready = new int[features.Length];
        Array.Fill(ready, Unknown);
        var visiting = new bool[features.Length];
        for (var f = 0; f < features.Length; f++)
        {
            features[f].Wave = ReadyWave(f);
        }

        foreach (var group in groups)
        {
            group.Wave = group.KeyFeatureIndex < 0 ? 0 : Math.Max(0, ReadyWave(group.KeyFeatureIndex) + 1);
        }

        int ReadyWave(int f)
        {
            if (ready[f] != Unknown)
            {
                return ready[f];
            }

            if (visiting[f])
            {
                return -1; // a cycle (already reported as FEATURE_CYCLE / ENTITY_KEY_CYCLE); the plan is rejected anyway
            }

            visiting[f] = true;
            var info = features[f];
            int wave;
            if (info.IsContext)
            {
                wave = -1;
            }
            else if (info.IsDerived)
            {
                wave = -1;
                foreach (var d in info.Dependencies)
                {
                    wave = Math.Max(wave, ReadyWave(d));
                }
            }
            else
            {
                var key = info.GroupIndex >= 0 ? groups[info.GroupIndex].KeyFeatureIndex : -1;
                wave = key < 0 ? 0 : Math.Max(0, ReadyWave(key) + 1);
            }

            visiting[f] = false;
            ready[f] = wave;
            return wave;
        }
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
