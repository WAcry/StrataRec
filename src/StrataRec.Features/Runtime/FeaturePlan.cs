using System.Collections.Immutable;
using System.Runtime.InteropServices;
using StrataRec.Artifacts.Json;
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

    /// <summary>
    /// Features that must be available first: derived inputs (given when the feature is registered), or the foreign key of
    /// the entity (set by <see cref="FeaturePlan.Create"/> when it groups the physical features).
    /// </summary>
    public ImmutableArray<int> Dependencies
    {
        get => _dependencies;
        init => _dependencies = value;
    }

    private ImmutableArray<int> _dependencies = [];

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

    /// <summary>Set once <see cref="FeaturePlan.Create"/> has taken the info for a plan.</summary>
    internal bool Planned { get; set; }

    /// <summary>A physical feature of an entity whose key reads features depends on those key features alone.</summary>
    internal void DependOnKey(ImmutableArray<int> keyFeatures) => _dependencies = keyFeatures;

    /// <summary>
    /// A derived context feature that a key reads, or an operator awaits while it runs (a recall source's query, design
    /// 5.4), directly or through other derived features (set by <see cref="FeaturePlan.Create"/>): the context prefetch
    /// computes it as soon as its inputs are stored, so the groups keyed by it start then and the operator's call is sent
    /// then; other derived context features are computed once the whole prefetch is.
    /// </summary>
    internal bool ComputedEarly { get; set; }
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

    /// <summary>The entity's key layout: what a fetch's keys are, and what the connector was checked to support.</summary>
    public EntityKeyLayout KeyLayout => Entity.KeyLayout;

    /// <summary>Where a request reads each key part, in part order (<see cref="KeyPartSource"/>).</summary>
    internal KeyPartSource[] KeyParts { get; init; } = [];

    /// <summary>The parts read per row (candidate parts), in part order; empty for a context-scoped entity, whose key is
    /// the request's.</summary>
    internal int[] VaryingParts { get; init; } = [];

    /// <summary>The features the key reads (its feature parts, each once): fetched before the group, and the group fails
    /// with any of them (design 6.7).</summary>
    public required ImmutableArray<int> KeyFeatures { get; init; }

    /// <summary>
    /// The context features among <see cref="KeyFeatures"/> (shared parts read from the request's context values): a
    /// context group starts, and a candidate group is fetched, once the context prefetch has stored them. Empty for a key of
    /// request inputs and candidate parts alone.
    /// </summary>
    internal int[] ContextKeyFeatures { get; init; } = [];

    /// <summary>
    /// The feature read as the key's only varying part (-1 for the candidate's entity id), or <see cref="int.MinValue"/>
    /// when several parts vary. One varying part (every one-part key, and one column beside request inputs) is resolved,
    /// deduplicated and looked up as one value per row, without a tuple: the per-row cost one-part keys always had.
    /// </summary>
    internal int OneVaryingFeature => VaryingParts.Length == 1 ? KeyParts[VaryingParts[0]].Feature : int.MinValue;

    /// <summary>Feature indices of the group's members, in field order (by feature name).</summary>
    public required ImmutableArray<int> FeatureIndices { get; init; }

    /// <summary>Connector field bindings of the members: <c>Fields[i]</c> is the field of <c>FeatureIndices[i]</c>.</summary>
    public required ImmutableArray<CompiledFieldBinding> Fields { get; init; }

    /// <summary>
    /// The arrays behind <see cref="FeatureIndices"/> and <see cref="Fields"/>, for the fetcher, which passes a whole group's
    /// members around as arrays (and recognizes them by reference); it never writes them.
    /// </summary>
    internal int[] FeatureIndexArray => ImmutableCollectionsMarshal.AsArray(FeatureIndices)!;

    /// <inheritdoc cref="FeatureIndexArray"/>
    internal CompiledFieldBinding[] FieldArray => ImmutableCollectionsMarshal.AsArray(Fields)!;

    public int Wave { get; internal set; }

    /// <summary>Metric tags and span name of this source (design 13.1), built once per plan on first use.</summary>
    public IoTarget Io => _io ?? Interlocked.CompareExchange(ref _io, new IoTarget(IoKind.FeatureSource, SourceName), null) ?? _io!;

    private IoTarget? _io;
}

/// <summary>Where a request reads one key part of a <see cref="FetchGroup"/>.</summary>
/// <param name="Context">The request's context value it is (a request input or a context key feature), or -1.</param>
/// <param name="Feature">The key feature it is (a candidate feature's column, or a context feature also read at
/// <paramref name="Context"/>), or -1; both -1 for <c>col.entity_id</c>.</param>
internal readonly record struct KeyPartSource(int Context, int Feature)
{
    public static KeyPartSource EntityId => new(-1, -1);

    public bool IsContext => Context >= 0;
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
            if (g.Supply.Table is not null && g.KeyLayout.VaryingCount >= 2)
            {
                TableTupleParts = Math.Max(TableTupleParts, g.KeyLayout.VaryingCount);
            }

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

    /// <summary>The most varying parts of a key of a group that holds a table (0 when none has two or more): a request
    /// readies a tuple index for its reads up front.</summary>
    internal int TableTupleParts { get; }

    /// <summary>
    /// Builds the plan: groups physical features by (source, entity), resolves each group's supply from the bindings
    /// (<see cref="FeatureSourceResolver"/>: a connector, or a local table from <paramref name="localTables"/>) and
    /// computes fetch waves. Problems (unbound sources, wrong settings, unsupported key layouts) are reported to
    /// <paramref name="diagnostics"/>.
    /// </summary>
    /// <param name="updateSources">Update-source factories for local bindings; null has the built-in ones.</param>
    /// <param name="localTables">Where local tables come from; null gives tables that are never loaded (compiling without serving).</param>
    /// <param name="instances">Where connectors and distributed cache stores come from; null creates them for this plan alone.</param>
    /// <param name="cacheStores">Distributed cache store factories for cache bindings; null has none.</param>
    /// <param name="clock">The clock of the cache tables the plan creates; null is the system clock.</param>
    /// <param name="catalog">The catalog the features come from: a <c>local</c> table holds every physical feature it
    /// binds to the table's source and entity, not only those of <paramref name="features"/> (see
    /// <see cref="NeedsWholeTable"/>); null holds the plan's features alone.</param>
    /// <param name="awaited">The context features operators await while they run rather than before they start (a recall
    /// source's query, design 5.4), by index in <paramref name="features"/>: a derived one is computed as soon as its inputs
    /// are stored (<see cref="FeatureRuntimeInfo.ComputedEarly"/>), so such an operator does not wait for the whole
    /// prefetch; null has none.</param>
    /// <exception cref="ArgumentException">An info of <paramref name="features"/> belongs to a plan already.</exception>
    /// <remarks>The plan takes <paramref name="features"/> as they are and fills in their groups, fields, key dependencies
    /// and waves. So an info belongs to one plan: one that a plan has taken (another plan's <see cref="Features"/>, which
    /// requests are using) is refused before anything changes (review 56).</remarks>
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
        TimeProvider? clock = null,
        string catalogArtifact = "catalog",
        FeatureCatalog? catalog = null,
        IReadOnlyCollection<int>? awaited = null)
    {
        foreach (var f in features)
        {
            if (f.Planned)
            {
                throw new ArgumentException(
                    $"Feature '{f.Definition.Name}' belongs to a feature plan already: a plan fills in its infos, so a new plan needs infos of its own.",
                    nameof(features));
            }
        }

        foreach (var f in features)
        {
            f.Planned = true;
        }

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
            localTables ?? UnloadedLocalTables.Instance, instances ?? UnsharedInstances.Instance, baseDirectory, clock ?? TimeProvider.System, diagnostics,
            catalogArtifact, catalog);

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

            if (resolver.Group(supply, entity, members) is not { } groupSupply
                || !TryKeyParts(entity, byName, contextIdOfRequestInput, diagnostics, catalogArtifact, out var keyParts, out var varying, out var keyFeatures, out var contextKeys))
            {
                continue;
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
                KeyParts = keyParts,
                VaryingParts = varying,
                KeyFeatures = keyFeatures,
                ContextKeyFeatures = contextKeys,
                FeatureIndices = ImmutableCollectionsMarshal.AsImmutableArray(featureIndices),
                Fields = ImmutableCollectionsMarshal.AsImmutableArray(fields),
            };
            for (var i = 0; i < members.Length; i++)
            {
                members[i].GroupIndex = group.Index;
                members[i].FieldIndex = i;
                if (keyFeatures.Length > 0)
                {
                    members[i].DependOnKey(keyFeatures);
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
        var derivedOrder = TopologicalDerivedOrder(all, diagnostics, catalogArtifact);
        AssignWaves(all, groups);
        MarkComputedEarly(all, groups, awaited);
        return new FeaturePlan(all, groups.ToArray(), derivedOrder);
    }

    /// <summary>
    /// Where a request reads each part of <paramref name="entity"/>'s key (design 6.8): a request input from the request's
    /// context values, <c>col.entity_id</c>, a candidate key feature's column, or a context key feature's context value; the
    /// group's members then depend on the key features. False (diagnosed) when a key feature is not in the plan.
    /// </summary>
    /// <remarks>The one place a part's kind becomes a source.</remarks>
    private static bool TryKeyParts(
        EntityDefinition entity, Dictionary<string, FeatureRuntimeInfo> byName, Func<string, int> contextIdOfRequestInput, DiagnosticBag diagnostics,
        string catalogArtifact, out KeyPartSource[] parts, out int[] varying, out ImmutableArray<int> keyFeatures, out int[] contextKeys)
    {
        parts = new KeyPartSource[entity.Key.Count];
        var varyingList = new List<int>(parts.Length);
        var features = new List<int>();
        var context = new List<int>();
        for (var p = 0; p < parts.Length; p++)
        {
            var part = entity.Key[p];
            if (part.Kind == EntityKeyPartKind.RequestInput)
            {
                parts[p] = new KeyPartSource(contextIdOfRequestInput(part.Name), -1);
                continue;
            }

            if (part.Kind == EntityKeyPartKind.EntityId)
            {
                parts[p] = KeyPartSource.EntityId;
                varyingList.Add(p);
                continue;
            }

            if (!byName.TryGetValue(part.Name, out var keyFeature))
            {
                diagnostics.Error("ENTITY_BAD_KEY", catalogArtifact, FeatureCatalog.KeyPointer(entity, p),
                    $"Key feature '{part.Name}' is not available.", "a key feature is compiled with the features keyed by it; check its declaration");
                varying = Array.Empty<int>();
                keyFeatures = [];
                contextKeys = Array.Empty<int>();
                return false;
            }

            if (!features.Contains(keyFeature.Index))
            {
                features.Add(keyFeature.Index);
            }

            if (part.Kind == EntityKeyPartKind.ContextFeature)
            {
                // Shared, like a request input: read from the request's context values once the prefetch has stored it.
                parts[p] = new KeyPartSource(keyFeature.ContextId, keyFeature.Index);
                context.Add(keyFeature.Index);
                continue;
            }

            parts[p] = new KeyPartSource(-1, keyFeature.Index);
            varyingList.Add(p);
        }

        varying = varyingList.ToArray();
        keyFeatures = [.. features];
        contextKeys = context.ToArray();
        return true;
    }

    /// <summary>
    /// Marks the derived context features keys read, whether the key is a context group's or a shared part of a candidate
    /// group's, and those <paramref name="awaited"/> lists, directly or through other derived features
    /// (<see cref="FeatureRuntimeInfo.ComputedEarly"/>): the prefetch computes them as soon as their inputs are stored.
    /// </summary>
    private static void MarkComputedEarly(FeatureRuntimeInfo[] features, IReadOnlyList<FetchGroup> groups, IReadOnlyCollection<int>? awaited)
    {
        foreach (var group in groups)
        {
            foreach (var k in group.ContextKeyFeatures)
            {
                Mark(k);
            }
        }

        if (awaited is not null)
        {
            foreach (var f in awaited)
            {
                Mark(f);
            }
        }

        void Mark(int f)
        {
            var info = features[f];
            if (!info.IsDerived || !info.IsContext || info.ComputedEarly)
            {
                return; // a physical one is stored by its group; one marked already (a cycle is rejected anyway)
            }

            info.ComputedEarly = true;
            foreach (var d in info.Dependencies)
            {
                Mark(d);
            }
        }
    }

    /// <summary>
    /// Whether a plan that reads one feature of <paramref name="source"/> must hold every physical feature of the source's
    /// tables, as the plan builder registers them (design 6.6). A <c>cache</c> table is filled by the misses of the plans
    /// that read it, and a record written back must be complete, or a later read of another field would take the field
    /// for missing: a miss fetches every member of the group, so every field of the table must be a member. A <c>local</c>
    /// table is loaded whole by its source, whatever the plan reads (<see cref="Create"/> lays it out from the catalog),
    /// and a <c>fetch</c> carries only the features a request needs: those plans hold only what they read. False for a
    /// source with no binding or an unknown mode, which the feature plan reports.
    /// </summary>
    public static bool NeedsWholeTable(SourceBindingArtifact bindings, string source)
    {
        if (!bindings.Sources.TryGetValue(source, out var binding) || !FeatureSourceResolver.TryParseMode(binding.Mode, out var mode))
        {
            return false;
        }

        return mode switch
        {
            SupplyMode.Fetch => false,
            SupplyMode.Local => false,
            SupplyMode.Cache => true,
        };
    }

    /// <summary>The requested features plus everything they depend on, in dependency order.</summary>
    public List<int> Closure(IEnumerable<int> roots)
    {
        var order = new int[Features.Count];
        var seen = new bool[Features.Count];
        var count = 0;
        foreach (var r in roots)
        {
            Visit(r, order, seen, ref count);
        }

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
    /// length; <paramref name="seen"/> must be all false and is left marking the closure. The roots are a span, so a
    /// node's read features (an immutable array) and a request's lists are read without boxing an enumerator.
    /// </summary>
    internal int Closure(ReadOnlySpan<int> roots, int[] order, bool[] seen)
    {
        var count = 0;
        foreach (var r in roots)
        {
            Visit(r, order, seen, ref count);
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
            group.Wave = GroupWave(group);
        }

        // A group is fetched in the wave after the latest of its key features (0 without any).
        int GroupWave(FetchGroup group)
        {
            var wave = 0;
            foreach (var key in group.KeyFeatures)
            {
                wave = Math.Max(wave, ReadyWave(key) + 1);
            }

            return wave;
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
                wave = info.GroupIndex >= 0 ? GroupWave(groups[info.GroupIndex]) : 0;
            }

            visiting[f] = false;
            ready[f] = wave;
            return wave;
        }
    }

    private static int[] TopologicalDerivedOrder(FeatureRuntimeInfo[] features, DiagnosticBag diagnostics, string catalogArtifact)
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
                diagnostics.Error("FEATURE_CYCLE", catalogArtifact, features[i].Definition.Pointer,
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
