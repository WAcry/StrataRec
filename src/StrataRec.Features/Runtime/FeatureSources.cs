using System.Text.Json;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Features.Bindings;
using StrataRec.Features.Cache;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

namespace StrataRec.Features.Runtime;

/// <summary>How a feature source's values reach a request (design 6.6); chosen by the binding's <c>mode</c>.</summary>
public enum SupplyMode : byte
{
    /// <summary>Fetched from the source's connector by every request that needs them (the default).</summary>
    Fetch = 1,

    /// <summary>Read from an in-process table that an update source loads in the background: no I/O on the request path.</summary>
    Local,

    /// <summary>
    /// Read through two cache tiers in front of the connector (design 6.6 <c>cache</c>): an in-process table that the
    /// misses are written back to, then optionally a distributed cache shared by the instances; I/O only on a miss.
    /// </summary>
    Cache,
}

/// <summary>
/// How one (source, entity) group is supplied: its mode and everything the mode holds. Built only by
/// <see cref="FeatureSourceResolver"/>; plan building, the read path and the fetch path take the mode from
/// <see cref="Mode"/> (with switches over every mode, so a new mode fails to compile where it must be handled) and never
/// re-derive it from which members are set.
/// </summary>
public sealed class GroupSupply
{
    private GroupSupply(SupplyMode mode, IFeatureSourceConnector? connector, LocalGroupTable? table, CacheSupply? cache, int timeoutMs, int maxBatch)
    {
        Mode = mode;
        Connector = connector;
        Table = table;
        Cache = cache;
        TimeoutMs = timeoutMs;
        MaxBatch = maxBatch;
    }

    public SupplyMode Mode { get; }

    /// <summary>The connector a fetch calls: set for <see cref="SupplyMode.Fetch"/>, and for <see cref="SupplyMode.Cache"/>
    /// (the origin of its misses); null otherwise.</summary>
    public IFeatureSourceConnector? Connector { get; }

    /// <summary>The table a read reads: set for <see cref="SupplyMode.Local"/> and <see cref="SupplyMode.Cache"/>, null otherwise.</summary>
    public LocalGroupTable? Table { get; }

    /// <summary>The rest of a <see cref="SupplyMode.Cache"/> group's supply; null for the other modes.</summary>
    public CacheSupply? Cache { get; }

    /// <summary>
    /// Time limit of one call through the fetch path; for a cache group, of the whole read of its misses (the distributed
    /// tier, then the connector). Also a replay's answer to a local or cache read, which takes no time.
    /// </summary>
    public int TimeoutMs { get; }

    /// <summary>
    /// Keys per call through the fetch path: a fetched group's <c>max_batch</c>. A local or cache group goes through the
    /// fetch path only when a replay answers its reads, in one call each, as they were recorded; a cache group's own calls
    /// are chunked by <see cref="CacheSupply.MaxBatch"/>.
    /// </summary>
    public int MaxBatch { get; }

    internal static GroupSupply Fetch(IFeatureSourceConnector connector, int timeoutMs, int maxBatch) =>
        new(SupplyMode.Fetch, connector, null, null, timeoutMs, maxBatch);

    internal static GroupSupply Local(LocalGroupTable table) =>
        new(SupplyMode.Local, null, table, null, FeatureSourceResolver.DefaultTimeoutMs, FeatureSourceResolver.LocalMaxBatch);

    internal static GroupSupply ForCache(IFeatureSourceConnector connector, int timeoutMs, LocalGroupTable table, CacheSupply cache) =>
        new(SupplyMode.Cache, connector, table, cache, timeoutMs, FeatureSourceResolver.LocalMaxBatch);
}

/// <summary>
/// What a <see cref="SupplyMode.Cache"/> group has besides its connector and table (design 6.6 <c>cache</c>): its policy
/// (the table's <see cref="LocalTableCache"/>), the distributed tier if any, and how its misses are chunked and bounded.
/// </summary>
public sealed class CacheSupply
{
    internal CacheSupply(string sourceName, int maxBatch, DistributedCacheTier? distributed, int? distributedTimeoutMs, int[] memberOfTableField)
    {
        MaxBatch = maxBatch;
        Distributed = distributed;
        DistributedTimeoutMs = distributedTimeoutMs;
        MemberOfTableField = memberOfTableField;
        Io = new IoTarget(IoKind.FeatureCache, sourceName);
    }

    /// <summary>Keys per connector call, and per read of the distributed tier (the binding's <c>max_batch</c>).</summary>
    public int MaxBatch { get; }

    /// <summary>The distributed tier (<c>cache.distributed</c>), shared by the instances; null for an in-process cache only.</summary>
    public DistributedCacheTier? Distributed { get; }

    /// <summary><c>cache.distributed.timeout_ms</c>; null waits at most half of the read's budget for the distributed tier.</summary>
    public int? DistributedTimeoutMs { get; }

    /// <summary>
    /// For each table field, the first group member holding it: where a miss's record takes the field's value from, since
    /// a miss fetches every member, so that the records it writes back are complete.
    /// </summary>
    internal int[] MemberOfTableField { get; }

    /// <summary>Metric tags and span name of the distributed tier's reads (source kind <c>feature_cache</c>), built with the plan.</summary>
    public IoTarget Io { get; }
}

/// <summary>The local table a <see cref="FetchGroup"/> reads (design 6.6), and where each of the group's fields is in it.</summary>
public sealed class LocalGroupTable
{
    internal LocalGroupTable(LocalTableSpec spec, LocalFeatureTable table, int[] tableFields)
    {
        Spec = spec;
        Table = table;
        TableFields = tableFields;
    }

    public LocalTableSpec Spec { get; }

    /// <summary>The table, shared with every plan whose spec has the same key (<see cref="LocalTableSpec.Key"/>).</summary>
    public LocalFeatureTable Table { get; }

    /// <summary>Table field of each group member: <c>TableFields[i]</c> holds <see cref="FetchGroup.Fields"/>[i].</summary>
    public int[] TableFields { get; }
}

/// <summary>
/// Turns the bindings of a plan's feature sources into how each (source, entity) group is supplied (design 6.4, 6.6):
/// one <see cref="GroupSupply"/> per group. The one place that knows what each supply mode needs: which binding settings
/// belong to it, what it requires, and what it takes from the binary (a connector; an update source and a local table).
/// </summary>
/// <remarks>Every decision by mode is a switch over all <see cref="SupplyMode"/> values without a default arm (here and
/// in the fetcher), so adding a mode fails to compile (CS8509) at each place that has to handle it; a test checks that
/// every mode has a name the binding parses back.</remarks>
/// <remarks>Only bindings the plan's features use are resolved, so a pipeline is never rejected for another
/// pipeline's source. Every problem is a diagnostic with the binding's pointer and a hint.</remarks>
internal sealed class FeatureSourceResolver
{
    /// <summary>
    /// Every setting of <see cref="FeatureSourceBinding"/> (by its JSON name) with the modes it belongs to; the members the
    /// binding does not declare are connector settings, which belong to the modes with a connector
    /// (<see cref="TakesConnectorSettings"/>). A setting of a mode the binding is not in is an error. A setting means the
    /// same in every mode it belongs to, so a binding switched between them keeps it. A test checks that this lists every
    /// declared member, so a setting added to the binding cannot be silently accepted in a mode that ignores it.
    /// </summary>
    internal static readonly (string Name, SupplyMode[] Modes, Func<FeatureSourceBinding, bool> IsSet)[] ModeSettings =
    {
        ("connector", [SupplyMode.Fetch, SupplyMode.Cache], static b => b.Connector is not null),
        ("timeout_ms", [SupplyMode.Fetch, SupplyMode.Cache], static b => b.TimeoutMs is not null),
        ("max_batch", [SupplyMode.Fetch, SupplyMode.Cache], static b => b.MaxBatch is not null),
        ("lookup", [SupplyMode.Fetch], static b => b.Lookup is not null),
        ("max_bytes", [SupplyMode.Local, SupplyMode.Cache], static b => b.MaxBytes is not null),
        ("bootstrap", [SupplyMode.Local], static b => b.Bootstrap is not null),
        ("cache", [SupplyMode.Cache], static b => b.Cache is not null),
    };

    internal const int DefaultTimeoutMs = 50;
    private const int DefaultMaxBatch = 1000;

    /// <summary>A local group's chunk size when a replay answers its reads through the fetch path: one chunk, as recorded.</summary>
    internal const int LocalMaxBatch = 1 << 30;

    /// <summary>The longest <c>cache.ttl_ms</c> and <c>cache.stale_if_error_ms</c>: 2^61 ms, so their sums cannot overflow.</summary>
    private const long MaxCacheMs = 1L << 61;

    private readonly SourceBindingArtifact _bindings;
    private readonly FeatureConnectorRegistry _connectors;
    private readonly FeatureUpdateSourceRegistry _updateSources;
    private readonly FeatureCacheStoreRegistry _cacheStores;
    private readonly ILocalTableProvider _localTables;
    private readonly IBindingInstances _instances;
    private readonly string? _baseDirectory;
    private readonly TimeProvider _clock;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _artifact;
    private readonly Dictionary<string, SourceSupply?> _resolved = new(StringComparer.Ordinal);

    /// <param name="instances">Where connectors and distributed cache stores come from: the engine shares them across plans by
    /// binding identity.</param>
    /// <param name="clock">The clock of the cache tables the plan creates (write times and expiry).</param>
    public FeatureSourceResolver(
        SourceBindingArtifact bindings,
        FeatureConnectorRegistry connectors,
        FeatureUpdateSourceRegistry updateSources,
        FeatureCacheStoreRegistry cacheStores,
        ILocalTableProvider localTables,
        IBindingInstances instances,
        string? baseDirectory,
        TimeProvider clock,
        DiagnosticBag diagnostics)
    {
        _bindings = bindings;
        _connectors = connectors;
        _updateSources = updateSources;
        _cacheStores = cacheStores;
        _localTables = localTables;
        _instances = instances;
        _baseDirectory = baseDirectory;
        _clock = clock;
        _diagnostics = diagnostics;
        _artifact = "bindings:" + bindings.Environment;
    }

    /// <summary>The supply of a source, resolved once per source; null (diagnosed) when its binding is missing or wrong.</summary>
    /// <param name="featurePointer">The catalog pointer of a feature reading the source, for an unbound source.</param>
    public SourceSupply? Source(string sourceName, string featurePointer)
    {
        if (!_resolved.TryGetValue(sourceName, out var supply))
        {
            supply = Resolve(sourceName, featurePointer);
            _resolved[sourceName] = supply;
        }

        return supply;
    }

    /// <summary>
    /// The supply of one (source, entity) group of <paramref name="supply"/>'s source; null (diagnosed) when the group
    /// cannot be supplied that way: a connector without the group's key shape, an entity a local table cannot hold.
    /// </summary>
    public GroupSupply? Group(SourceSupply supply, EntityDefinition entity, FeatureRuntimeInfo[] members, KeyShape shape) => supply.Mode switch
    {
        SupplyMode.Fetch => SupportsShape(supply, entity, shape) ? GroupSupply.Fetch(supply.Connector!, supply.TimeoutMs, supply.MaxBatch) : null,
        SupplyMode.Local => LocalTable(supply, entity, members) is { } table ? GroupSupply.Local(table) : null,
        SupplyMode.Cache => CacheGroup(supply, entity, members, shape),
    };

    private bool SupportsShape(SourceSupply supply, EntityDefinition entity, KeyShape shape)
    {
        var connector = supply.Connector!;
        if (!connector.Supports(shape))
        {
            _diagnostics.Error("CONNECTOR_SHAPE", _artifact, supply.Pointer,
                $"Connector '{connector.ConnectorType}' does not support {shape} lookups needed by entity '{entity.Name}'.");
            return false;
        }

        return true;
    }

    /// <summary>A cache group: its table (which also rejects a cross entity), its connector's key shape, and where a miss's
    /// record takes each table field from.</summary>
    private GroupSupply? CacheGroup(SourceSupply supply, EntityDefinition entity, FeatureRuntimeInfo[] members, KeyShape shape)
    {
        if (LocalTable(supply, entity, members) is not { } table || !SupportsShape(supply, entity, shape))
        {
            return null;
        }

        var memberOfField = new int[table.Table.Schema.FieldCount];
        Array.Fill(memberOfField, -1);
        for (var i = members.Length - 1; i >= 0; i--)
        {
            memberOfField[table.TableFields[i]] = i; // the first member of each field wins
        }

        return GroupSupply.ForCache(supply.Connector!, supply.TimeoutMs, table,
            new CacheSupply(supply.Name, supply.MaxBatch, supply.Distributed, supply.DistributedTimeoutMs, memberOfField));
    }

    /// <summary>
    /// The table of one (source, entity) group of a local or cache source and the table field of each member; null
    /// (diagnosed) when the entity cannot be held in a table or the features disagree on a field's type.
    /// </summary>
    private LocalGroupTable? LocalTable(SourceSupply supply, EntityDefinition entity, FeatureRuntimeInfo[] members)
    {
        var pointer = supply.Pointer;
        var mode = ModeName(supply.Mode);
        if (entity.Kind == EntityKind.Cross)
        {
            _diagnostics.Error("LOCAL_CROSS_ENTITY", _artifact, pointer + "/mode",
                $"Source '{supply.Name}' is bound with mode '{mode}', but feature '{members[0].Definition.Name}' reads it for cross entity '{entity.Name}'. " +
                LocalTableSchema.CrossEntityMessage(entity.Name), "bind the source with mode 'fetch'");
            return null;
        }

        var definitions = new FeatureDefinition[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            definitions[i] = members[i].Definition;
        }

        var tableMode = supply.Mode switch
        {
            SupplyMode.Local => LocalTableMode.Local,
            SupplyMode.Cache => LocalTableMode.Cache,
            SupplyMode.Fetch => throw new InvalidOperationException("A fetched source has no table."),
        };
        if (!LocalTableSchema.TryFromFeatures(supply.Name, entity, tableMode, definitions, out var schema, out var conflict))
        {
            _diagnostics.Error("LOCAL_FIELD_TYPE_CONFLICT", "catalog", ConflictPointer(members, conflict!), conflict!,
                "give every feature that reads the field the same type, or read it through a derived feature");
            return null;
        }

        // One update source per table: a source may be stateful (a stream consumer), so tables never share one. The spec
        // creates it, with the base directory it pins for the identity too. A cache table's source is its writer.
        LocalTableSpec spec;
        try
        {
            spec = tableMode == LocalTableMode.Cache
                ? LocalTableSpec.CreateCache(schema, supply.Options!, supply.Binding, supply.Connector!, _artifact, pointer, _baseDirectory)
                : LocalTableSpec.Create(schema, supply.Options!, supply.Binding, supply.BootstrapFactory!, supply.Name, _artifact, pointer, _baseDirectory);
        }
        catch (FeatureUpdateException ex)
        {
            ReportSourceSettings(supply.Pointer, ex);
            return null;
        }

        LocalFeatureTable table;
        try
        {
            table = _localTables.Acquire(spec);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // max_bytes below what an empty table takes.
            _diagnostics.Error("LOCAL_MAX_BYTES", _artifact, pointer + "/max_bytes", FirstLine(ex.Message), "raise max_bytes");
            return null;
        }

        var fields = new int[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            fields[i] = schema.FieldIndex(members[i].Definition.Field!);
        }

        return new LocalGroupTable(spec, table, fields);
    }

    private SourceSupply? Resolve(string sourceName, string featurePointer)
    {
        if (!_bindings.Sources.TryGetValue(sourceName, out var binding))
        {
            _diagnostics.Error("SOURCE_UNBOUND", "catalog", featurePointer,
                $"Feature source '{sourceName}' has no binding in environment '{_bindings.Environment}'.",
                NameSuggestions.DidYouMean(sourceName, _bindings.Sources.Keys) ?? "add it under 'sources' in the bindings artifact");
            return null;
        }

        var pointer = "/sources/" + JsonPointer.Escape(sourceName);
        if (!TryParseMode(binding.Mode, out var mode))
        {
            _diagnostics.Error("SOURCE_MODE_UNSUPPORTED", _artifact, pointer + "/mode",
                $"Supply mode '{binding.Mode}' is not available in this build.", "use mode " + ModeNames());
            return null;
        }

        // Every problem of the binding at once: settings of the other mode, and what the mode itself requires.
        var settingsOk = CheckModeSettings(sourceName, binding, mode, pointer);
        var supply = mode switch
        {
            SupplyMode.Fetch => ResolveFetch(sourceName, binding, pointer),
            SupplyMode.Local => ResolveLocal(sourceName, binding, pointer),
            SupplyMode.Cache => ResolveCache(sourceName, binding, pointer),
        };
        return settingsOk ? supply : null;
    }

    /// <summary>The mode a binding's <c>mode</c> names; no mode is <see cref="SupplyMode.Fetch"/>. The inverse of <see cref="ModeName"/>.</summary>
    internal static bool TryParseMode(string? name, out SupplyMode mode)
    {
        mode = SupplyMode.Fetch;
        if (name is null)
        {
            return true;
        }

        foreach (var candidate in Enum.GetValues<SupplyMode>())
        {
            if (string.Equals(ModeName(candidate), name, StringComparison.Ordinal))
            {
                mode = candidate;
                return true;
            }
        }

        return false;
    }

    private static string ModeNames()
    {
        var names = new List<string>();
        foreach (var mode in Enum.GetValues<SupplyMode>())
        {
            names.Add("'" + ModeName(mode) + "'");
        }

        return string.Join(" or ", names);
    }

    /// <summary>Whether the binding's undeclared members (connector settings) belong to the mode: the modes with a connector.</summary>
    private static bool TakesConnectorSettings(SupplyMode mode) => mode switch
    {
        SupplyMode.Fetch or SupplyMode.Cache => true,
        SupplyMode.Local => false,
    };

    /// <summary>Reports every setting of a mode the binding is not in; true when there is none.</summary>
    private bool CheckModeSettings(string sourceName, FeatureSourceBinding binding, SupplyMode mode, string pointer)
    {
        var ok = true;
        foreach (var (name, modes, isSet) in ModeSettings)
        {
            if (Array.IndexOf(modes, mode) < 0 && isSet(binding))
            {
                ok = false;
                var names = new List<string>(modes.Length);
                var bindAs = new List<string>(modes.Length);
                foreach (var settingMode in modes)
                {
                    names.Add("'" + ModeName(settingMode) + "'");
                    bindAs.Add($"\"mode\": \"{ModeName(settingMode)}\"");
                }

                _diagnostics.Error("SOURCE_SETTING_WRONG_MODE", _artifact, pointer + "/" + name,
                    $"'{name}' is a setting of mode {string.Join(" and ", names)}, but source '{sourceName}' is bound with mode '{ModeName(mode)}'.",
                    $"remove '{name}', or bind the source with {string.Join(" or ", bindAs)}");
            }
        }

        if (!TakesConnectorSettings(mode))
        {
            foreach (var (name, _) in binding.Settings)
            {
                ok = false;
                _diagnostics.Error("SOURCE_SETTING_WRONG_MODE", _artifact, pointer + "/" + JsonPointer.Escape(name),
                    $"'{name}' is not a setting of mode '{ModeName(mode)}' (connector settings belong to modes 'fetch' and 'cache'); source '{sourceName}' is bound with mode '{ModeName(mode)}'.",
                    $"a {ModeName(mode)} binding takes {SettingsOf(mode)}; settings of the update source go inside 'bootstrap'");
            }
        }

        return ok;
    }

    private SourceSupply? ResolveFetch(string sourceName, FeatureSourceBinding binding, string pointer) =>
        ResolveConnector(sourceName, binding, pointer) is { } connector
            ? new SourceSupply(sourceName, pointer, binding, SupplyMode.Fetch)
            {
                Connector = connector,
                TimeoutMs = binding.TimeoutMs ?? DefaultTimeoutMs,
                MaxBatch = binding.MaxBatch is > 0 ? binding.MaxBatch.Value : DefaultMaxBatch,
            }
            : null;

    /// <summary>The binding's connector, shared by identity; null (diagnosed) when its type is unknown or its settings are wrong.</summary>
    private IFeatureSourceConnector? ResolveConnector(string sourceName, FeatureSourceBinding binding, string pointer)
    {
        var type = binding.Connector ?? string.Empty;
        if (!_connectors.TryGet(type, out var factory))
        {
            _diagnostics.Error("CONNECTOR_UNKNOWN", _artifact, pointer + (binding.Connector is null ? string.Empty : "/connector"),
                binding.Connector is null ? $"Source '{sourceName}' names no 'connector'." : $"Connector type '{type}' is not registered in this binary.",
                NameSuggestions.DidYouMean(type, _connectors.Types) ?? $"registered: {string.Join(", ", _connectors.Types)}");
            return null;
        }

        try
        {
            var spec = BindingSpec.Create(BindingSection.Sources, sourceName, type, binding.Settings, _baseDirectory, factory, _artifact);
            return _instances.Acquire(spec, factory.Create);
        }
        catch (BindingException ex)
        {
            _diagnostics.Error("CONNECTOR_CONFIG", _artifact, pointer + ex.Pointer, ex.Reason, ex.Hint);
            return null;
        }
        catch (Exception ex) when (ex is FormatException or IOException or JsonException or InvalidOperationException)
        {
            _diagnostics.Error("CONNECTOR_CONFIG", _artifact, pointer, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// A cache binding (design 6.6 <c>cache</c>): the connector it is in front of, <c>max_bytes</c> of its tables, and its
    /// <c>cache</c> settings, the distributed tier included. Every problem of the binding is reported at once.
    /// </summary>
    private SourceSupply? ResolveCache(string sourceName, FeatureSourceBinding binding, string pointer)
    {
        var connector = ResolveConnector(sourceName, binding, pointer);
        var ok = connector is not null;
        ok &= TryMaxBytes(sourceName, binding, pointer, SupplyMode.Cache, out var maxBytes);
        long ttlMs = 0, staleMs = 0;
        DistributedCacheTier? distributed = null;
        int? distributedTimeoutMs = null;
        if (binding.Cache is not { } cache)
        {
            ok = false;
            _diagnostics.Error("CACHE_SETTINGS_MISSING", _artifact, pointer, $"Source '{sourceName}' is bound with mode 'cache' but has no 'cache'.",
                "add how long values are served, e.g. \"cache\": { \"ttl_ms\": 60000 }");
        }
        else
        {
            if (cache.TtlMs is not { } ttl)
            {
                ok = false;
                _diagnostics.Error("CACHE_TTL", _artifact, pointer + "/cache", $"The cache of source '{sourceName}' has no 'ttl_ms'.",
                    "add how long a fetched value is served, in milliseconds, e.g. \"ttl_ms\": 60000");
            }
            else if (ttl is <= 0 or > MaxCacheMs)
            {
                ok = false;
                _diagnostics.Error("CACHE_TTL", _artifact, pointer + "/cache/ttl_ms", $"ttl_ms {ttl} is not between 1 and 2^61 milliseconds.",
                    "give how long a fetched value is served, e.g. \"ttl_ms\": 60000");
            }
            else
            {
                ttlMs = ttl;
            }

            if (cache.StaleIfErrorMs is { } stale && stale is < 0 or > MaxCacheMs)
            {
                ok = false;
                _diagnostics.Error("CACHE_STALE_IF_ERROR", _artifact, pointer + "/cache/stale_if_error_ms",
                    $"stale_if_error_ms {stale} is not between 0 and 2^61 milliseconds.",
                    "give how much longer an expired value may serve when the connector fails, e.g. 300000, or 0");
            }
            else
            {
                staleMs = cache.StaleIfErrorMs ?? 0;
            }

            if (cache.Distributed is { } shared)
            {
                ok &= TryDistributed(sourceName, shared, pointer + "/cache/distributed", out distributed);
                distributedTimeoutMs = shared.TimeoutMs;
                ok &= TryRetention(sourceName, ttlMs, staleMs, pointer + "/cache");
            }
        }

        if (!ok)
        {
            return null;
        }

        return new SourceSupply(sourceName, pointer, binding, SupplyMode.Cache)
        {
            Connector = connector,
            TimeoutMs = binding.TimeoutMs ?? DefaultTimeoutMs,
            MaxBatch = binding.MaxBatch is > 0 ? binding.MaxBatch.Value : DefaultMaxBatch,
            Options = new LocalFeatureTableOptions
            {
                MaxBytes = maxBytes,
                Cache = new CacheTableOptions { TtlMs = ttlMs, StaleIfErrorMs = staleMs, Clock = _clock },
            },
            Distributed = distributed,
            DistributedTimeoutMs = distributedTimeoutMs,
        };
    }

    /// <summary>
    /// Whether the distributed tier can be given the longest time to live a record of this binding needs (the TTL with all
    /// its jitter, plus the stale window): a <see cref="TimeSpan"/>. Diagnosed at the setting that takes it past.
    /// </summary>
    private bool TryRetention(string sourceName, long ttlMs, long staleMs, string pointer)
    {
        if (ttlMs == 0 || LocalTableCache.RetentionMs(ttlMs, staleMs) <= LocalTableCache.MaxRetentionMs)
        {
            return true; // a ttl_ms that is missing or wrong is diagnosed already
        }

        var setting = LocalTableCache.MaxLifetime(ttlMs) > LocalTableCache.MaxRetentionMs ? "ttl_ms" : "stale_if_error_ms";
        _diagnostics.Error("CACHE_RETENTION", _artifact, pointer + "/" + setting,
            $"The distributed cache of source '{sourceName}' would keep a record for up to {LocalTableCache.RetentionMs(ttlMs, staleMs)} ms "
            + $"(ttl_ms with its 10% jitter, plus stale_if_error_ms), more than the {LocalTableCache.MaxRetentionMs} ms a time to live can be.",
            $"lower ttl_ms or stale_if_error_ms so that ttl_ms × 1.1 + stale_if_error_ms is at most {LocalTableCache.MaxRetentionMs} (about 29,000 years)");
        return false;
    }

    /// <summary>The distributed tier of a cache binding: its store, created by its factory and shared by identity like any
    /// binding instance, and wrapped in the engine's write-back queue.</summary>
    private bool TryDistributed(string sourceName, DistributedCacheBinding shared, string pointer, out DistributedCacheTier? tier)
    {
        tier = null;
        var ok = true;
        if (shared.TimeoutMs is <= 0)
        {
            ok = false;
            _diagnostics.Error("DISTRIBUTED_CACHE_CONFIG", _artifact, pointer + "/timeout_ms", $"timeout_ms {shared.TimeoutMs} is not positive.",
                "give the longest a request waits for the distributed cache, in milliseconds, or leave it out (half the source's timeout_ms)");
        }

        var type = shared.Store ?? string.Empty;
        if (!_cacheStores.TryGet(type, out var factory))
        {
            _diagnostics.Error("DISTRIBUTED_CACHE_UNKNOWN", _artifact, pointer + (shared.Store is null ? string.Empty : "/store"),
                shared.Store is null ? "The distributed cache names no 'store'." : $"Distributed cache store type '{type}' is not registered in this binary.",
                NameSuggestions.DidYouMean(type, _cacheStores.Types) ?? StoreHint(type));
            return false;
        }

        try
        {
            var spec = BindingSpec.Create(BindingSection.SourceCaches, sourceName, type, shared.Settings, _baseDirectory, factory, _artifact);
            tier = _instances.Acquire(spec, context => new DistributedCacheTier(factory.Create(context)));
        }
        catch (BindingException ex)
        {
            _diagnostics.Error("DISTRIBUTED_CACHE_CONFIG", _artifact, pointer + ex.Pointer, ex.Reason, ex.Hint);
            return false;
        }

        return ok;
    }

    /// <summary>What to do about a store type the binary does not have: what it has, and where <c>redis@1</c> comes from.</summary>
    private string StoreHint(string type)
    {
        var registered = string.Join(", ", _cacheStores.Types);
        return (registered.Length == 0 ? "this binary registers no distributed cache store" : "registered: " + registered)
            + (type.StartsWith("redis@", StringComparison.Ordinal)
                ? "; redis@1 is in the package StrataRec.Redis: the host adds it with PlanEnvironment.CacheStores.AddRedis()"
                : "; or remove 'distributed' to cache in process only");
    }

    /// <summary>The binding's <c>max_bytes</c> (required in modes local and cache); false (diagnosed) when missing or not a positive size.</summary>
    private bool TryMaxBytes(string sourceName, FeatureSourceBinding binding, string pointer, SupplyMode mode, out long maxBytes)
    {
        maxBytes = 0;
        if (binding.MaxBytes is null)
        {
            _diagnostics.Error("LOCAL_MAX_BYTES", _artifact, pointer, $"Source '{sourceName}' is bound with mode '{ModeName(mode)}' but has no 'max_bytes'.",
                "add the most memory each of its tables may take, e.g. \"max_bytes\": \"256MiB\"");
            return false;
        }

        if (!ByteSize.TryParse(binding.MaxBytes, out maxBytes) || maxBytes == 0)
        {
            _diagnostics.Error("LOCAL_MAX_BYTES", _artifact, pointer + "/max_bytes", $"'{binding.MaxBytes}' is not a positive size.",
                "write an integer and a unit B, KiB, MiB, GiB or TiB, e.g. \"256MiB\"");
            return false;
        }

        return true;
    }

    private SourceSupply? ResolveLocal(string sourceName, FeatureSourceBinding binding, string pointer)
    {
        var ok = TryMaxBytes(sourceName, binding, pointer, SupplyMode.Local, out var maxBytes);
        IFeatureUpdateSourceFactory? factory = null;
        if (binding.Bootstrap is not { } bootstrap)
        {
            ok = false;
            _diagnostics.Error("LOCAL_BOOTSTRAP_MISSING", _artifact, pointer, $"Source '{sourceName}' is bound with mode 'local' but has no 'bootstrap'.",
                $"add the update source that loads it, e.g. \"bootstrap\": {{ \"source\": \"{SnapshotFilesUpdateSource.Type}\", \"uri\": \"<snapshot directory>\" }}");
        }
        else if (!_updateSources.TryGet(bootstrap.Source ?? string.Empty, out factory))
        {
            ok = false;
            var type = bootstrap.Source ?? string.Empty;
            _diagnostics.Error("UPDATE_SOURCE_UNKNOWN", _artifact, pointer + "/bootstrap" + (bootstrap.Source is null ? string.Empty : "/source"),
                bootstrap.Source is null ? "The bootstrap names no 'source'." : $"Update source type '{type}' is not registered in this binary.",
                NameSuggestions.DidYouMean(type, _updateSources.Types) ?? $"registered: {string.Join(", ", _updateSources.Types)}");
        }

        // The bootstrap settings are checked once for the source (not per entity), and even when the rest is wrong, so
        // every problem of the binding is reported at once.
        if (factory is not null)
        {
            try
            {
                _ = factory.Create(sourceName, binding.Bootstrap!.Settings, BindingContext.PinBaseDirectory(_baseDirectory));
            }
            catch (FeatureUpdateException ex)
            {
                ok = false;
                ReportSourceSettings(pointer, ex);
            }
        }

        if (!ok)
        {
            return null;
        }

        return new SourceSupply(sourceName, pointer, binding, SupplyMode.Local)
        {
            BootstrapFactory = factory,
            Options = new LocalFeatureTableOptions { MaxBytes = maxBytes },
            TimeoutMs = DefaultTimeoutMs,
            MaxBatch = LocalMaxBatch,
        };
    }

    private void ReportSourceSettings(string pointer, FeatureUpdateException ex) =>
        _diagnostics.Error("UPDATE_SOURCE_CONFIG", _artifact, pointer + "/bootstrap" + ex.Pointer, ex.Reason, ex.Hint);

    /// <summary>The pointer of the conflicting feature's type: the conflict message names the feature first.</summary>
    private static string ConflictPointer(FeatureRuntimeInfo[] members, string conflict)
    {
        foreach (var member in members)
        {
            if (conflict.StartsWith($"Feature '{member.Definition.Name}'", StringComparison.Ordinal))
            {
                return member.Definition.Pointer + "/type";
            }
        }

        return members[0].Definition.Pointer;
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOf('\n', StringComparison.Ordinal);
        var line = end < 0 ? message : message[..end];
        var parameter = line.IndexOf(" (Parameter", StringComparison.Ordinal);
        return parameter < 0 ? line : line[..parameter];
    }

    /// <summary>The mode's name in a binding (<c>"mode"</c>); <see cref="TryParseMode"/> reads it back.</summary>
    internal static string ModeName(SupplyMode mode) => mode switch
    {
        SupplyMode.Fetch => "fetch",
        SupplyMode.Local => "local",
        SupplyMode.Cache => "cache",
    };

    /// <summary>The declared settings of <paramref name="mode"/>, quoted, from <see cref="ModeSettings"/>.</summary>
    private static string SettingsOf(SupplyMode mode)
    {
        var names = new List<string>();
        foreach (var (name, modes, _) in ModeSettings)
        {
            if (Array.IndexOf(modes, mode) >= 0)
            {
                names.Add("'" + name + "'");
            }
        }

        return string.Join(" and ", names);
    }
}

/// <summary>A resolved feature source binding: its mode and what that mode needs.</summary>
internal sealed class SourceSupply(string name, string pointer, FeatureSourceBinding binding, SupplyMode mode)
{
    public string Name { get; } = name;

    /// <summary>The binding's pointer, e.g. <c>/sources/item_store</c>.</summary>
    public string Pointer { get; } = pointer;

    public FeatureSourceBinding Binding { get; } = binding;

    public SupplyMode Mode { get; } = mode;

    /// <summary>Modes fetch and cache.</summary>
    public IFeatureSourceConnector? Connector { get; init; }

    public int TimeoutMs { get; init; }

    public int MaxBatch { get; init; }

    /// <summary>Mode local: the factory of the bootstrap source.</summary>
    public IFeatureUpdateSourceFactory? BootstrapFactory { get; init; }

    /// <summary>Modes local and cache: the options of every table of the source.</summary>
    public LocalFeatureTableOptions? Options { get; init; }

    /// <summary>Mode cache: the distributed tier, if the binding has one.</summary>
    public DistributedCacheTier? Distributed { get; init; }

    /// <summary>Mode cache: <c>cache.distributed.timeout_ms</c>.</summary>
    public int? DistributedTimeoutMs { get; init; }
}
