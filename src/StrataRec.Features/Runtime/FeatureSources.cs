using System.Collections.Immutable;
using System.Runtime.InteropServices;
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

/// <summary>How a feature source's values reach a request; chosen by the binding's <c>mode</c>.</summary>
public enum SupplyMode : byte
{
    /// <summary>Fetched from the source's connector by every request that needs them (the default).</summary>
    Fetch = 1,

    /// <summary>Read from an in-process table that an update source loads in the background: no I/O on the request path.</summary>
    Local,

    /// <summary>
    /// Read through two cache tiers in front of the connector: an in-process table that the
    /// misses are written back to, then optionally a distributed cache shared by the instances; I/O only on a miss.
    /// </summary>
    Cache,

    /// <summary>
    /// Asked of a service, once per request, for the features of an entity with args: the arguments are the
    /// call's inputs, the fields its outputs, and the service's own timeout and cache apply.
    /// </summary>
    Service,
}

/// <summary>
/// How one (source, entity) group is supplied: its mode and everything the mode holds. Built only by
/// <see cref="FeatureSourceResolver"/>; plan building, the read path and the fetch path take the mode from
/// <see cref="Mode"/> (with switches over every mode, so a new mode fails to compile where it must be handled) and never
/// re-derive it from which members are set.
/// </summary>
public sealed class GroupSupply
{
    private GroupSupply(
        SupplyMode mode, IFeatureSourceConnector? connector, LocalGroupTable? table, CacheSupply? cache, int timeoutMs, int maxBatch, IServiceSupply? service = null)
    {
        Mode = mode;
        Connector = connector;
        Service = service;
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

    /// <summary>The service a <see cref="SupplyMode.Service"/> group asks; null for the other modes.</summary>
    internal IServiceSupply? Service { get; }

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

    /// <summary>A service group: its fetch is bounded by the source's <c>timeout_ms</c>, as a connector's is; the service's own applies within it.</summary>
    internal static GroupSupply ForService(IServiceSupply service, int timeoutMs) => new(SupplyMode.Service, null, null, null, timeoutMs, 1, service);
}

/// <summary>
/// What a <see cref="SupplyMode.Cache"/> group has besides its connector and table: its times, the distributed tier if
/// any, and how its misses are chunked and bounded.
/// </summary>
public sealed class CacheSupply
{
    internal CacheSupply(string sourceName, CachePolicy policy, int maxBatch, DistributedCacheTier? distributed, int? distributedTimeoutMs, int[] memberOfTableField)
    {
        Policy = policy;
        MaxBatch = maxBatch;
        Distributed = distributed;
        DistributedTimeoutMs = distributedTimeoutMs;
        MemberOfTableField = memberOfTableField;
        Io = new IoTarget(IoRoles.Cache, sourceName);
    }

    /// <summary>The binding's <c>ttl_ms</c> and <c>stale_if_error_ms</c> as this plan compiled them: how fresh its reads
    /// find a record, in either tier.</summary>
    public CachePolicy Policy { get; }

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

    /// <summary>Metric tags and span name of the distributed tier's reads (role <c>cache</c>, target the source), built with the plan.</summary>
    public IoTarget Io { get; }
}

/// <summary>The local table a <see cref="FetchGroup"/> reads, and where each of the group's fields is in it.</summary>
public sealed class LocalGroupTable
{
    internal LocalGroupTable(LocalTableSpec spec, LocalFeatureTable table, int[] tableFields)
    {
        Spec = spec;
        Table = table;
        TableFields = ImmutableCollectionsMarshal.AsImmutableArray(tableFields);
    }

    public LocalTableSpec Spec { get; }

    /// <summary>The table, shared with every plan whose spec has the same key (<see cref="LocalTableSpec.Key"/>).</summary>
    public LocalFeatureTable Table { get; }

    /// <summary>Table field of each group member: <c>TableFields[i]</c> holds <see cref="FetchGroup.Fields"/>[i].</summary>
    public ImmutableArray<int> TableFields { get; }
}

/// <summary>
/// Turns the bindings of a plan's feature sources into how each (source, entity) group is supplied:
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
    /// Every setting of <see cref="FeatureSourceBinding"/> (by its JSON name) with the modes it belongs to
    /// (<see cref="SupplyMode.Cache"/> is a <c>fetch</c> binding with a <c>cache</c>, <see cref="TryMode"/>); the members
    /// the binding does not declare are connector settings, which belong to the modes with a connector
    /// (<see cref="TakesConnectorSettings"/>). A setting of a mode the binding is not in is an error. A setting means the
    /// same in every mode it belongs to, so a binding switched between them keeps it. A test checks that this lists every
    /// declared member, so a setting added to the binding cannot be silently accepted in a mode that ignores it.
    /// </summary>
    internal static readonly (string Name, SupplyMode[] Modes, Func<FeatureSourceBinding, bool> IsSet)[] ModeSettings =
    {
        ("connector", [SupplyMode.Fetch, SupplyMode.Cache], static b => b.Connector is not null),
        ("timeout_ms", [SupplyMode.Fetch, SupplyMode.Cache, SupplyMode.Service], static b => b.TimeoutMs is not null),
        ("max_batch", [SupplyMode.Fetch, SupplyMode.Cache], static b => b.MaxBatch is not null),
        ("max_bytes", [SupplyMode.Local], static b => b.MaxBytes is not null),
        ("bootstrap", [SupplyMode.Local], static b => b.Bootstrap is not null),
        ("cache", [SupplyMode.Fetch, SupplyMode.Cache], static b => b.Cache is not null),
        ("service", [SupplyMode.Service], static b => b.Service is not null),
    };

    internal const int DefaultTimeoutMs = 50;
    private const int DefaultMaxBatch = 1000;

    /// <summary>A local group's chunk size when a replay answers its reads through the fetch path: one chunk, as recorded.</summary>
    internal const int LocalMaxBatch = 1 << 30;

    private readonly SourceBindingArtifact _bindings;
    private readonly BindingResolver _resolver;
    private readonly ILocalTableProvider _localTables;
    private readonly string? _baseDirectory;
    private readonly TimeProvider _clock;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _artifact;
    private readonly string _catalogArtifact;
    private readonly FeatureCatalog? _catalog;
    private readonly Dictionary<string, SourceSupply?> _resolved = new(StringComparer.Ordinal);
    private readonly ServiceSupplyResolver? _services;

    /// <param name="resolver">Creates connectors, update sources and distributed cache stores, and reports their bindings' errors.</param>
    /// <param name="clock">The clock of the cache tables the plan creates (write times and expiry).</param>
    /// <param name="catalog">The catalog a local table's layout comes from (<see cref="TableFeatures"/>); null lays it
    /// out from the group's members.</param>
    /// <param name="services">The plan's services, for a source in mode <c>service</c>; null leaves the mode unavailable.</param>
    public FeatureSourceResolver(
        SourceBindingArtifact bindings,
        BindingResolver resolver,
        ILocalTableProvider localTables,
        TimeProvider clock,
        string catalogArtifact = "catalog",
        FeatureCatalog? catalog = null,
        ServiceSupplyResolver? services = null)
    {
        _services = services;
        _bindings = bindings;
        _catalogArtifact = catalogArtifact;
        _catalog = catalog;
        _resolver = resolver;
        _localTables = localTables;
        _baseDirectory = resolver.BaseDirectory;
        _clock = clock;
        _diagnostics = resolver.Diagnostics;
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
    /// cannot be supplied that way: a connector that does not support the entity's key layout, fields a table cannot hold.
    /// A table holds any key layout: <c>max_bytes</c> bounds its memory, and a cache's TTL and eviction its churn.
    /// </summary>
    public GroupSupply? Group(SourceSupply supply, EntityDefinition entity, FeatureRuntimeInfo[] members) => !Pairs(supply, entity) ? null : supply.Mode switch
    {
        SupplyMode.Fetch => SupportsKey(supply, entity) ? GroupSupply.Fetch(supply.Connector!, supply.TimeoutMs, supply.MaxBatch) : null,
        SupplyMode.Local => LocalTable(supply, entity, members) is { } table ? GroupSupply.Local(table) : null,
        SupplyMode.Cache => CacheGroup(supply, entity, members),
        SupplyMode.Service => OutputsServeTheirReaders(supply, members) ? GroupSupply.ForService(supply.Service!, supply.TimeoutMs) : null,
    };

    /// <summary>
    /// Whether the features of a service's group can all take what the service answers for each output they read: it
    /// answers an output once for a call, as one tensor, and a feature takes only its declared element type (no widening
    /// or rounding) and its length (one value for a scalar, its length for a fixed-length list, any for a list). Two
    /// readers of one output conflict when their element types differ or their lengths do; a scalar and a list of its
    /// type, or a list and a fixed-length list, can both be served. A conflict is a <c>SERVICE_OUTPUT_TYPE_CONFLICT</c>
    /// at the later feature's type, in name order, as a table's field of two types is (such a plan
    /// loaded, and every call failed; the first check compared whole types and rejected a list read as a
    /// fixed-length list too).
    /// </summary>
    private bool OutputsServeTheirReaders(SourceSupply supply, FeatureRuntimeInfo[] members)
    {
        if (members.Length < 2)
        {
            return true;
        }

        var sorted = (FeatureRuntimeInfo[])members.Clone();
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Definition.Name, b.Definition.Name));
        var readers = new Dictionary<string, (FeatureRuntimeInfo Typed, FeatureRuntimeInfo? Sized)>(sorted.Length, StringComparer.Ordinal);
        var ok = true;
        foreach (var member in sorted)
        {
            var field = member.Definition.Field!;
            var length = LengthTaken(member.Type);
            if (!readers.TryGetValue(field, out var first))
            {
                readers[field] = (member, length > 0 ? member : null);
                continue;
            }

            var other = ElementOf(first.Typed.Type) != ElementOf(member.Type) ? first.Typed
                : length > 0 && first.Sized is { } sized && LengthTaken(sized.Type) != length ? sized
                : null;
            if (other is not null)
            {
                _diagnostics.Error("SERVICE_OUTPUT_TYPE_CONFLICT", _catalogArtifact, member.Definition.Pointer + "/type",
                    $"Feature '{member.Definition.Name}' reads output '{field}' of source '{supply.Name}' as {member.Type.AsNonNullable()}, but feature " +
                    $"'{other.Definition.Name}' reads it as {other.Type.AsNonNullable()}: the service answers the output once, and no answer is both.",
                    "give every feature that reads the output the same type, or read it through a derived feature");
                ok = false;
            }
            else if (length > 0 && first.Sized is null)
            {
                readers[field] = (first.Typed, member);
            }
        }

        return ok;

        static Core.Types.StorageKind ElementOf(Core.Types.ColumnType type) => type.IsList ? type.Element : type.Kind;

        // The number of values a feature of the type takes: one for a scalar, the fixed length of a list; 0 for any.
        static int LengthTaken(Core.Types.ColumnType type) => !type.IsList ? 1 : type.IsFixedLength ? type.FixedLength : 0;
    }

    /// <summary>
    /// A service computes the features of an entity with args, and only those: a stored entity's are looked up by its key
    /// (a connector, a table), and an entity with args has no key to look up. A source may compute several: each
    /// entity's call is its own, by its arguments and the outputs its features read. A pair that does not match is a
    /// <c>SOURCE_SERVICE_ENTITY</c> at the binding.
    /// </summary>
    private bool Pairs(SourceSupply supply, EntityDefinition entity)
    {
        var service = supply.Mode == SupplyMode.Service;
        if (service == entity.HasArgs)
        {
            return true;
        }

        _diagnostics.Error("SOURCE_SERVICE_ENTITY", _artifact, supply.Pointer + (service ? "/service" : string.Empty),
            service
                ? $"Source '{supply.Name}' is bound to service '{supply.Binding.Service}', which computes the features of an entity with args; entity '{entity.Name}' has a key."
                : $"Entity '{entity.Name}' has args: a service computes its features, but source '{supply.Name}' is bound with mode '{ModeName(supply.Mode)}'.",
            service
                ? $"bind the source to a connector or a local table, or give '{entity.Name}' args instead of a key"
                : "bind the source with \"mode\": \"service\" and the \"service\" that computes them");
        return false;
    }

    /// <summary>
    /// Whether the connector supports the entity's key layout (asked once, here): a connector that does not is a
    /// <c>CONNECTOR_KEY</c> error at the binding, naming the layout, rather than a failure on the request path.
    /// </summary>
    private bool SupportsKey(SourceSupply supply, EntityDefinition entity)
    {
        var connector = supply.Connector!;
        var layout = entity.KeyLayout;
        if (!connector.Supports(layout))
        {
            _diagnostics.Error("CONNECTOR_KEY", _artifact, supply.Pointer,
                $"Connector '{connector.ConnectorType}' of source '{supply.Name}' does not support the key of entity '{entity.Name}': {layout}.",
                "bind the source to a connector that supports this key, or key the entity the way the connector reads it");
            return false;
        }

        return true;
    }

    /// <summary>A cache group: its table, its connector's support of the key, and where a miss's record takes each table
    /// field from.</summary>
    private GroupSupply? CacheGroup(SourceSupply supply, EntityDefinition entity, FeatureRuntimeInfo[] members)
    {
        if (LocalTable(supply, entity, members) is not { } table || !SupportsKey(supply, entity))
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
            new CacheSupply(supply.Name, supply.Policy!, supply.MaxBatch, supply.Distributed, supply.DistributedTimeoutMs, memberOfField));
    }

    /// <summary>
    /// The table of one (source, entity) group of a local or cached source and the table field of each member; null
    /// (diagnosed) when the features disagree on a field's type.
    /// </summary>
    private LocalGroupTable? LocalTable(SourceSupply supply, EntityDefinition entity, FeatureRuntimeInfo[] members)
    {
        var pointer = supply.Pointer;
        var tableMode = supply.Mode switch
        {
            SupplyMode.Local => LocalTableMode.Local,
            SupplyMode.Cache => LocalTableMode.Cache,
            SupplyMode.Fetch or SupplyMode.Service => throw new InvalidOperationException("A fetched or served source has no table."),
        };
        var definitions = TableFeatures(supply, entity, members);
        if (!LocalTableSchema.TryFromFeatures(supply.Name, entity, tableMode, definitions, out var schema, out var conflict))
        {
            _diagnostics.Error("LOCAL_FIELD_TYPE_CONFLICT", _catalogArtifact, ConflictPointer(definitions, conflict!), conflict!,
                "give every feature that reads the field the same type, or read it through a derived feature");
            return null;
        }

        // One update source per table: a source may be stateful (a stream consumer), so tables never share one. The spec
        // creates it, with the base directory it pins for the identity too. A cache table's source is its writer.
        LocalTableSpec spec;
        try
        {
            spec = tableMode == LocalTableMode.Cache
                ? LocalTableSpec.CreateCache(schema, supply.Options!, supply.Options!.Cache!.Origin!, _resolver.Registry, _artifact, pointer, _baseDirectory)
                : LocalTableSpec.Create(schema, supply.Options!, supply.Binding, supply.BootstrapFactory!, supply.Name, _artifact, pointer, _baseDirectory);
        }
        catch (BindingException ex)
        {
            _resolver.Report(BindingKind.UpdateSource, supply.Name, ex);
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
            var cache = tableMode == LocalTableMode.Cache;
            _diagnostics.Error(cache ? "CACHE_MAX_BYTES" : "LOCAL_MAX_BYTES", _artifact, pointer + (cache ? "/cache/memory/max_bytes" : "/max_bytes"),
                FirstLine(ex.Message), "raise max_bytes");
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
            _diagnostics.Error("SOURCE_UNBOUND", _catalogArtifact, featurePointer,
                $"Feature source '{sourceName}' has no binding in environment '{_bindings.Environment}'.",
                NameSuggestions.DidYouMean(sourceName, _bindings.Sources.Keys) ?? "add it under 'sources' in the bindings artifact");
            return null;
        }

        var pointer = "/sources/" + JsonPointer.Escape(sourceName);
        if (!TryMode(binding, out var mode))
        {
            _diagnostics.Error("SOURCE_MODE_UNSUPPORTED", _artifact, pointer + "/mode",
                $"Supply mode '{binding.Mode}' is not available in this build.",
                string.Equals(binding.Mode, "cache", StringComparison.Ordinal)
                    ? "a cache is a member of a fetch binding: remove \"mode\" and give the binding a \"cache\", e.g. { \"ttl_ms\": 60000, \"memory\": { \"max_bytes\": \"256MiB\" } }"
                    : "use mode " + ModeNames());
            return null;
        }

        // Every problem of the binding at once: settings of the other mode, and what the mode itself requires.
        var settingsOk = CheckModeSettings(sourceName, binding, mode, pointer);
        var supply = mode switch
        {
            SupplyMode.Fetch => ResolveFetch(sourceName, binding, pointer),
            SupplyMode.Local => ResolveLocal(sourceName, binding, pointer),
            SupplyMode.Cache => ResolveCache(sourceName, binding, pointer),
            SupplyMode.Service => ResolveService(sourceName, binding, pointer),
        };
        return settingsOk ? supply : null;
    }

    /// <summary>
    /// How a binding supplies its features: the mode its <c>mode</c> names (none is <c>fetch</c>), and for a <c>fetch</c>
    /// binding with a <c>cache</c>, <see cref="SupplyMode.Cache"/>. False when <c>mode</c> names none.
    /// </summary>
    internal static bool TryMode(FeatureSourceBinding binding, out SupplyMode mode)
    {
        mode = SupplyMode.Fetch;
        foreach (var candidate in Enum.GetValues<SupplyMode>())
        {
            if (candidate != SupplyMode.Cache && string.Equals(ModeName(candidate), binding.Mode ?? ModeName(SupplyMode.Fetch), StringComparison.Ordinal))
            {
                mode = candidate == SupplyMode.Fetch && binding.Cache is not null ? SupplyMode.Cache : candidate;
                return true;
            }
        }

        return false;
    }

    private static string ModeNames() => string.Join(" or ", QuotedNames(Enum.GetValues<SupplyMode>()));

    /// <summary>The names of <paramref name="modes"/> as a binding writes them, quoted, each once.</summary>
    private static List<string> QuotedNames(SupplyMode[] modes)
    {
        var names = new List<string>(modes.Length);
        foreach (var mode in modes)
        {
            var name = "'" + ModeName(mode) + "'";
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Whether the binding's undeclared members (connector settings) belong to the mode: the modes with a connector.</summary>
    private static bool TakesConnectorSettings(SupplyMode mode) => mode switch
    {
        SupplyMode.Fetch or SupplyMode.Cache => true,
        SupplyMode.Local or SupplyMode.Service => false,
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
                var names = QuotedNames(modes);
                var bindAs = new List<string>(names.Count);
                foreach (var quoted in names)
                {
                    bindAs.Add($"\"mode\": \"{quoted.Trim('\'')}\"");
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
                    $"'{name}' is not a setting of mode '{ModeName(mode)}' (connector settings belong to mode 'fetch'); source '{sourceName}' is bound with mode '{ModeName(mode)}'.",
                    $"a {ModeName(mode)} binding takes {SettingsOf(mode)}; "
                    + (mode == SupplyMode.Local ? "settings of the update source go inside 'bootstrap'" : "the service's settings go under 'services'"));
            }
        }

        return ok;
    }

    /// <summary>A service binding: the service it names, as the plan resolves it; null (diagnosed) without one.</summary>
    private SourceSupply? ResolveService(string sourceName, FeatureSourceBinding binding, string pointer)
    {
        if (binding.Service is not { } service)
        {
            _diagnostics.Error("SOURCE_SERVICE_MISSING", _artifact, pointer, $"Source '{sourceName}' is bound with mode 'service' but names no 'service'.",
                "name the service that computes its features, e.g. \"service\": \"intent_model\"");
            return null;
        }

        if (_services is null)
        {
            _diagnostics.Error("SOURCE_MODE_UNSUPPORTED", _artifact, pointer + "/mode", $"Supply mode 'service' of source '{sourceName}' needs the plan's services.",
                "compile the plan with its services");
            return null;
        }

        return _services(sourceName, service, pointer + "/service") is { } supply
            ? new SourceSupply(sourceName, pointer, binding, SupplyMode.Service) { Service = supply, TimeoutMs = binding.TimeoutMs ?? DefaultTimeoutMs }
            : null;
    }

    private SourceSupply? ResolveFetch(string sourceName, FeatureSourceBinding binding, string pointer) =>
        ResolveConnector(sourceName, binding) is { } connector
            ? new SourceSupply(sourceName, pointer, binding, SupplyMode.Fetch)
            {
                Connector = connector,
                TimeoutMs = binding.TimeoutMs ?? DefaultTimeoutMs,
                MaxBatch = binding.MaxBatch is > 0 ? binding.MaxBatch.Value : DefaultMaxBatch,
            }
            : null;

    /// <summary>The binding's connector, shared by identity; null (diagnosed) when its type is unknown or its settings are wrong.</summary>
    private IFeatureSourceConnector? ResolveConnector(string sourceName, FeatureSourceBinding binding) =>
        _resolver.Acquire<IFeatureSourceConnector>(BindingKind.Connector, sourceName, binding.Connector, binding.Settings);

    /// <summary>
    /// A fetch binding with a cache: the connector it is in front of and its <c>cache</c> settings, the tiers included.
    /// Every problem of the binding is reported at once.
    /// </summary>
    private SourceSupply? ResolveCache(string sourceName, FeatureSourceBinding binding, string pointer)
    {
        var connector = ResolveConnector(sourceName, binding);
        var ok = connector is not null;
        ok &= CacheSettings.TryRead(
            binding.Cache!, new CacheOwner(BindingKind.CacheStore, sourceName, "source", "connector"), _resolver, pointer + "/cache",
            binding.TimeoutMs ?? DefaultTimeoutMs, out var cache);
        if (!ok)
        {
            return null;
        }

        return new SourceSupply(sourceName, pointer, binding, SupplyMode.Cache)
        {
            Connector = connector,
            TimeoutMs = binding.TimeoutMs ?? DefaultTimeoutMs,
            MaxBatch = binding.MaxBatch is > 0 ? binding.MaxBatch.Value : DefaultMaxBatch,
            Options = cache.TableOptions(_clock, cache.Origin(OriginIdentity(binding))),
            Policy = cache.Policy,
            Distributed = cache.Distributed,
            DistributedTimeoutMs = cache.DistributedTimeoutMs,
        };
    }

    /// <summary>
    /// What a cached source's values come from when its cache names no <c>version</c>: its connector and the connector's
    /// settings, canonical, as the first 16 hex digits of their content id. Not <c>timeout_ms</c>, <c>max_batch</c> or
    /// <c>cache</c>, which decide when a value arrives and how long it is kept, not what it is: a release that only tunes
    /// those keeps both tiers' values (a service's cache follows the same rule, <c>ClientResolver.AnswerIdentity</c>).
    /// </summary>
    private static string OriginIdentity(FeatureSourceBinding binding)
    {
        var node = ArtifactSerializer.ToNode(binding).AsObject();
        node.Remove("mode");
        node.Remove("timeout_ms");
        node.Remove("max_batch");
        node.Remove("max_bytes");
        node.Remove("cache");
        return ContentHash.Compute(node).Substring(ContentHash.Prefix.Length, 16);
    }

    /// <summary>The binding's <c>max_bytes</c> (required in mode local); false (diagnosed) when missing or not a positive size.</summary>
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
        IBindingFactory<IFeatureUpdateSource>? factory = null;
        if (binding.Bootstrap is not { } bootstrap)
        {
            ok = false;
            _diagnostics.Error("LOCAL_BOOTSTRAP_MISSING", _artifact, pointer, $"Source '{sourceName}' is bound with mode 'local' but has no 'bootstrap'.",
                $"add the update source that loads it, e.g. \"bootstrap\": {{ \"source\": \"{SnapshotFilesUpdateSource.Type}\", \"uri\": \"<snapshot directory>\" }}");
        }
        else
        {
            // The bootstrap settings are checked once for the source (not per entity), and even when the rest is wrong, so
            // every problem of the binding is reported at once.
            factory = _resolver.Factory<IFeatureUpdateSource>(BindingKind.UpdateSource, sourceName, bootstrap.Source);
            ok &= factory is not null && _resolver.Check(factory, BindingKind.UpdateSource, sourceName, bootstrap.Settings);
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

    /// <summary>
    /// The features a table of <paramref name="supply"/>'s source and <paramref name="entity"/> holds, in name order. A
    /// <c>local</c> table holds every physical feature the catalog binds there, whichever of them the plan reads: its
    /// source loads whole records, so its layout, and with it the table's identity (<see cref="LocalTableSpec.Identity"/>),
    /// is the catalog's. Plans that read different features of it share one table, and a pipeline or an experiment that
    /// starts reading another of them does not reload it. A <c>cache</c> table holds the group's members, which are the
    /// whole table already (<see cref="FeaturePlan.NeedsWholeTable"/>), as is every table laid out without a catalog.
    /// </summary>
    private FeatureDefinition[] TableFeatures(SourceSupply supply, EntityDefinition entity, FeatureRuntimeInfo[] members)
    {
        var fromCatalog = _catalog is not null && supply.Mode switch
        {
            SupplyMode.Local => true,
            SupplyMode.Cache => false,
            SupplyMode.Fetch or SupplyMode.Service => throw new InvalidOperationException("A fetched or served source has no table."),
        };
        if (fromCatalog)
        {
            var features = new List<FeatureDefinition>();
            foreach (var feature in _catalog!.Features)
            {
                if (!feature.IsDerived && string.Equals(feature.Entity.Name, entity.Name, StringComparison.Ordinal)
                    && string.Equals(feature.Source, supply.Name, StringComparison.Ordinal))
                {
                    features.Add(feature);
                }
            }

            return features.ToArray();
        }

        var definitions = new FeatureDefinition[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            definitions[i] = members[i].Definition;
        }

        return definitions;
    }

    /// <summary>The pointer of the conflicting feature's type: the conflict message names the feature first.</summary>
    private static string ConflictPointer(FeatureDefinition[] features, string conflict)
    {
        foreach (var feature in features)
        {
            if (conflict.StartsWith($"Feature '{feature.Name}'", StringComparison.Ordinal))
            {
                return feature.Pointer + "/type";
            }
        }

        return features[0].Pointer;
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOf('\n', StringComparison.Ordinal);
        var line = end < 0 ? message : message[..end];
        var parameter = line.IndexOf(" (Parameter", StringComparison.Ordinal);
        return parameter < 0 ? line : line[..parameter];
    }

    /// <summary>The mode's name in a binding (<c>"mode"</c>); <see cref="TryMode"/> reads it back.</summary>
    internal static string ModeName(SupplyMode mode) => mode switch
    {
        SupplyMode.Fetch => "fetch",
        SupplyMode.Local => "local",
        SupplyMode.Cache => "fetch", // a fetch binding with a cache
        SupplyMode.Service => "service",
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

    /// <summary>Mode fetch, with or without a cache.</summary>
    public IFeatureSourceConnector? Connector { get; init; }

    public int TimeoutMs { get; init; }

    public int MaxBatch { get; init; }

    /// <summary>Mode local: the factory of the bootstrap source.</summary>
    public IBindingFactory<IFeatureUpdateSource>? BootstrapFactory { get; init; }

    /// <summary>Mode local, and a fetch binding with a cache: the options of every table of the source.</summary>
    public LocalFeatureTableOptions? Options { get; init; }

    /// <summary>A fetch binding with a cache: its times.</summary>
    public CachePolicy? Policy { get; init; }

    /// <summary>A fetch binding with a cache: the distributed tier, if the binding has one.</summary>
    public DistributedCacheTier? Distributed { get; init; }

    /// <summary>A fetch binding with a cache: <c>cache.distributed.timeout_ms</c>.</summary>
    public int? DistributedTimeoutMs { get; init; }

    /// <summary>Mode service: the service.</summary>
    public IServiceSupply? Service { get; init; }
}
