using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Per-environment bindings of logical feature sources and services to concrete clients. Experiments cannot
/// patch this artifact.
/// </summary>
public sealed class SourceBindingArtifact
{
    public required string Environment { get; init; }

    public Dictionary<string, FeatureSourceBinding> Sources { get; init; } = new();

    /// <summary>
    /// The services the pipeline calls, by logical name: model servers, recall sources, any external
    /// service. A model operator's <c>model</c> and a recall's sources name them.
    /// </summary>
    public Dictionary<string, ClientBinding> Services { get; init; } = new();

    /// <summary>
    /// Adds to <paramref name="types"/> the client, connector, update-source and cache-store types the named bindings use:
    /// a binary that lacks one cannot serve them. The one place that knows which binding settings name a
    /// type, so a new kind of setting is added here. A name without a binding adds nothing (it is an error where it is used).
    /// </summary>
    /// <param name="featureSources">Feature sources a plan reads: their connector, bootstrap source and distributed store.</param>
    /// <param name="services">Services a plan binds: their client and distributed store.</param>
    public void AddTypes(ISet<string> types, IEnumerable<string> featureSources, IEnumerable<string> services)
    {
        ArgumentNullException.ThrowIfNull(types);
        foreach (var name in featureSources)
        {
            if (!Sources.TryGetValue(name, out var source))
            {
                continue;
            }

            if (source.Connector is { } connector)
            {
                types.Add(connector);
            }

            if (source.Bootstrap?.Source is { Length: > 0 } bootstrap)
            {
                types.Add(bootstrap);
            }

            if (source.Cache?.Distributed?.Store is { Length: > 0 } store)
            {
                types.Add(store);
            }
        }

        foreach (var name in services)
        {
            if (Services.TryGetValue(name, out var service))
            {
                types.Add(service.Client);
                if (service.Cache?.Distributed?.Store is { Length: > 0 } store)
                {
                    types.Add(store);
                }
            }
        }
    }
}

/// <summary>Binding of a logical feature source.</summary>
/// <remarks>
/// <c>mode</c> says where the values come from and selects which of the other settings apply: <c>fetch</c> uses
/// <c>connector</c>, <c>timeout_ms</c>, <c>max_batch</c>, connector settings and optionally <c>cache</c>; <c>local</c>
/// uses <c>max_bytes</c> and <c>bootstrap</c>; <c>service</c> uses <c>service</c> and <c>timeout_ms</c>. A setting the
/// binding's mode does not take is an error (the Features runtime checks it, with the pointer of the setting), so a
/// binding switched from one mode to another cannot keep a setting that silently does nothing.
/// </remarks>
public sealed class FeatureSourceBinding
{
    /// <summary><c>fetch</c> (default), <c>local</c> or <c>service</c>.</summary>
    [Description("Where the features come from: 'fetch' (default) from the connector, on every request or, with a 'cache', " +
        "when the cache has no fresh value; 'local' from an in-process table that 'bootstrap' loads in the background, with no " +
        "I/O on the request path; 'service' from the service named by 'service', asked once per request for the features of " +
        "an entity with args. Settings of another mode are errors.")]
    [AllowedValues("fetch", "local", "service")]
    public string? Mode { get; init; }

    /// <summary>Mode <c>service</c>: the service (under <c>services</c>) that computes the features of an entity with args.</summary>
    [Description("Mode service, required: the service, under 'services', that computes the features of an entity with args: " +
        "called once per request with the arguments as its inputs, each feature's 'field' the output it reads. Its timeout_ms " +
        "and cache are the service's.")]
    public string? Service { get; init; }

    /// <summary>Connector type and version, e.g. <c>redis_hash@1</c> or <c>memory@1</c>.</summary>
    [Description("Mode fetch, required: connector type and version, e.g. 'memory@1'. Other members of the binding not listed " +
        "here are the connector's own settings (e.g. 'data_file').")]
    public string? Connector { get; init; }

    [Description("Modes fetch and service: time limit of one connector call, in milliseconds (default 50). With a cache, the " +
        "whole read of the keys a request misses (the distributed tier, then the connector) stays within it; for service, the " +
        "whole call of the service (its cache, then its client), whose own timeout_ms applies within it.")]
    public int? TimeoutMs { get; init; }

    /// <summary>Maximum keys per connector call; larger batches are split into the fewest calls, of even size, sent concurrently.</summary>
    [Description("Mode fetch: maximum keys per connector call (default 1000); larger batches are split into the " +
        "fewest calls, of even size (9 keys at most 4 a call go as 3, 3 and 3), sent concurrently. With a cache, also the keys per " +
        "read of its distributed tier.")]
    public int? MaxBatch { get; init; }

    /// <summary>Mode <c>local</c>: the most memory each of the source's tables may take, e.g. <c>"512MiB"</c>.</summary>
    [Description("Mode local, required: the most memory each table of the source (one per entity) may take, as an " +
        "integer and a unit B, KiB, MiB, GiB or TiB, e.g. \"512MiB\". A load that would exceed it fails and the plan is " +
        "not served. (A cache's memory is 'cache.memory.max_bytes'.)")]
    [RegularExpression(ByteSizePattern)]
    public string? MaxBytes { get; init; }

    /// <summary>Mode <c>local</c>: where the table's data comes from.</summary>
    [Description("Mode local, required: the update source that loads the table in the background, e.g. " +
        "{ \"source\": \"snapshot_files@1\", \"uri\": \"fixtures/item_snapshot\" }. A plan is served once every table it " +
        "reads has completed its first load.")]
    public UpdateSourceBinding? Bootstrap { get; init; }

    /// <summary>Mode <c>fetch</c>: keeps what the connector returned for a time, in process, in a shared store, or both.</summary>
    [Description("Mode fetch, optional: keeps what the connector returned for 'ttl_ms', in process ('memory'), in a store the " +
        "deployment's instances share ('distributed'), or both, e.g. { \"ttl_ms\": 60000, \"memory\": { \"max_bytes\": \"256MiB\" } }.")]
    public CacheBinding? Cache { get; init; }

    /// <summary>Connector-specific settings (endpoint, credentials reference, data file, …), bound to the settings type of its
    /// factory; mode <c>fetch</c>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();

    /// <summary>The form of a size in bytes (<see cref="MaxBytes"/>, a cache's): an integer and an IEC unit (IEC 80000-13).</summary>
    public const string ByteSizePattern = "^[0-9]+(B|KiB|MiB|GiB|TiB)$";
}

/// <summary>An update source of a <c>local</c> feature source: its type and its own settings.</summary>
public sealed class UpdateSourceBinding
{
    /// <summary>Update-source type and version, e.g. <c>snapshot_files@1</c>.</summary>
    [Description("Required: update-source type and version, e.g. 'snapshot_files@1'. The other members are the source's own " +
        "settings, which the schema describes for each type of the binary that wrote it.")]
    public string? Source { get; init; }

    /// <summary>Settings of the update source, e.g. <c>uri</c> and <c>reload</c> for <c>snapshot_files@1</c>, bound to the settings type of its factory.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();
}

/// <summary>
/// A cache: of a feature source's connector, of a service's answers, or (its tiers only) of the calls of a pipeline.
/// Each value is served for <see cref="TtlMs"/> (RFC 9111 <c>max-age</c>) and may be served
/// <see cref="StaleIfErrorMs"/> longer when the origin (the connector, the service) fails (RFC 5861
/// <c>stale-if-error</c>). It is kept in process (<see cref="Memory"/>), in a store the deployment's instances share
/// (<see cref="Distributed"/>), or both: at least one.
/// </summary>
public sealed class CacheBinding
{
    [Description("Required: how long a value fetched from the origin (the connector, or the service) is served, in milliseconds. " +
        "Each key's lifetime varies by ±10% with the key's hash, so values written together do not all expire together.")]
    public long? TtlMs { get; init; }

    [Description("How much longer (milliseconds, default 0) an expired value may be served when the origin fails or times " +
        "out; its readers are then Degraded.")]
    public long? StaleIfErrorMs { get; init; }

    [Description("Names what the cached values come from, e.g. \"3\". Without it the cache follows the configuration: a change " +
        "of the connector or client, or of its own settings, starts the cache anew, since other settings may give other " +
        "values. With it the cache follows this name instead: the values are kept across such changes, until you change the " +
        "name. Changing ttl_ms, stale_if_error_ms, timeouts, max_bytes or the distributed store never starts it anew. " +
        "Letters, digits, '.', '_' and '-', at most 64.")]
    [RegularExpression(VersionPattern)]
    public string? Version { get; init; }

    [Description("The in-process tier: { \"max_bytes\": \"256MiB\" }. Hits take no I/O; each instance has its own. Worth it " +
        "for keys many requests share (items); for keys of one user on a deployment of many instances, 'distributed' alone " +
        "saves the memory.")]
    public MemoryCacheBinding? Memory { get; init; }

    [Description("The tier the deployment's instances share, read when the in-process tier (if any) has no fresh value, before " +
        "the origin, e.g. { \"store\": \"redis@1\", \"endpoint\": \"cache.internal:6379\" }. Only an optimisation: when " +
        "it fails or is slow, the keys go to the origin.")]
    public DistributedCacheBinding? Distributed { get; init; }

    /// <summary>The form of <see cref="Version"/>.</summary>
    public const string VersionPattern = "^[A-Za-z0-9._-]{1,64}$";
}

/// <summary>The in-process tier of a cache.</summary>
public sealed class MemoryCacheBinding
{
    [Description("Required: the most memory the tier's table may take (for a source, each of its entities' tables), as an " +
        "integer and a unit B, KiB, MiB, GiB or TiB, e.g. \"64MiB\". A table that reaches it drops expired records first, then " +
        "favors retaining recently read records.")]
    [RegularExpression(FeatureSourceBinding.ByteSizePattern)]
    public string? MaxBytes { get; init; }
}

/// <summary>The distributed tier of a cache: its store type, the framework's bound on its reads, and the store's own settings.</summary>
public sealed class DistributedCacheBinding
{
    [Description("Required: store type and version, e.g. 'redis@1'. The other members are the store's own settings, which the " +
        "schema describes for each type of the binary that wrote it.")]
    public string? Store { get; init; }

    [Description("The longest a request waits for one read of the distributed cache, in milliseconds (default: half the " +
        "source's or the service's timeout_ms; required for a service without one); always within that timeout_ms, so the " +
        "origin keeps the rest of it.")]
    public int? TimeoutMs { get; init; }

    /// <summary>The store's own settings, e.g. <c>endpoint</c> and <c>password</c> for <c>redis@1</c>, bound to the settings type of its factory.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();
}

/// <summary>Binding of a service to a client implementation.</summary>
public sealed class ClientBinding
{
    [Description("Required: client type and version, e.g. 'linear@1' or 'random@1' for a model, 'memory_list@1' for a recall source. " +
        "The other members are the client's own settings, which the schema describes for each type of the binary that wrote it.")]
    public required string Client { get; init; }

    [Description("Time limit of one call to the client, in milliseconds. The node's deadline bounds the call too, and alone without it.")]
    public int? TimeoutMs { get; init; }

    [Description("Caches the service's answers, e.g. { \"ttl_ms\": 60000, \"memory\": { \"max_bytes\": \"64MiB\" } }: a call about no items whole, " +
        "one about items item by item (the items a call misses go to the service in one call). Keyed by the service version, the " +
        "outputs read and the inputs, not by the request's seed: a service that samples by the seed reuses one answer for ttl_ms.")]
    public CacheBinding? Cache { get; init; }

    /// <summary>The client's own settings, bound to the settings type of its factory.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();
}
