using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Per-environment bindings of logical feature sources and services to concrete clients (design 6.4). Experiments cannot
/// patch this artifact.
/// </summary>
public sealed class SourceBindingArtifact
{
    public required string Environment { get; init; }

    public Dictionary<string, FeatureSourceBinding> Sources { get; init; } = new();

    /// <summary>
    /// The services the pipeline calls (design 6.9), by logical name: model servers, recall sources, any external
    /// service. A model operator's <c>model</c> and a recall's sources name them.
    /// </summary>
    public Dictionary<string, ClientBinding> Services { get; init; } = new();

    /// <summary>
    /// Adds to <paramref name="types"/> the client, connector, update-source and cache-store types the named bindings use
    /// (design 10.4): a binary that lacks one cannot serve them. The one place that knows which binding settings name a
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

/// <summary>Binding of a logical feature source (design 6.4, 6.6).</summary>
/// <remarks>
/// <c>mode</c> selects which of the other settings apply: <c>fetch</c> uses <c>connector</c>, <c>timeout_ms</c>,
/// <c>max_batch</c> and connector settings; <c>local</c> uses <c>max_bytes</c> and <c>bootstrap</c>;
/// <c>cache</c> uses the connector and its settings (the origin), <c>timeout_ms</c>, <c>max_batch</c>, <c>max_bytes</c>
/// and <c>cache</c>. A setting the binding's mode does not take is an error (the Features runtime checks it, with the
/// pointer of the setting), so a binding switched from one mode to another cannot keep a setting that silently does
/// nothing.
/// </remarks>
public sealed class FeatureSourceBinding
{
    /// <summary><c>fetch</c> (default), <c>local</c>, <c>cache</c> or <c>service</c>.</summary>
    [Description("Supply mode (design 6.6): 'fetch' (default) fetches from the connector on every request; 'local' serves the " +
        "features from an in-process table that 'bootstrap' loads in the background, with no I/O on the request path; 'cache' " +
        "keeps what the connector returned in an in-process table (and optionally a distributed cache) for 'cache.ttl_ms'; " +
        "'service' asks the service named by 'service' once per request, for the features of an entity with args. " +
        "Settings of another mode are errors.")]
    [AllowedValues("fetch", "local", "cache", "service")]
    public string? Mode { get; init; }

    /// <summary>Mode <c>service</c>: the service (under <c>services</c>) that computes the features of an entity with args.</summary>
    [Description("Mode service, required: the service, under 'services', that computes the features of an entity with args: " +
        "called once per request with the arguments as its inputs, each feature's 'field' the output it reads. Its timeout_ms " +
        "and cache are the service's.")]
    public string? Service { get; init; }

    /// <summary>Connector type and version, e.g. <c>redis_hash@1</c> or <c>memory@1</c>.</summary>
    [Description("Modes fetch and cache, required: connector type and version, e.g. 'memory@1' (for cache, the origin the cache " +
        "is in front of). Other members of the binding not listed here are the connector's own settings (e.g. 'data_file').")]
    public string? Connector { get; init; }

    [Description("Modes fetch, cache and service: time limit of one connector call, in milliseconds (default 50). For cache, the " +
        "whole read of the keys a request misses (the distributed cache, then the connector) stays within it; for service, the " +
        "whole call of the service (its cache, then its client), whose own timeout_ms applies within it.")]
    public int? TimeoutMs { get; init; }

    /// <summary>Maximum keys per connector call; larger batches are split into the fewest calls, of even size, sent concurrently.</summary>
    [Description("Modes fetch and cache: maximum keys per connector call (default 1000); larger batches are split into the " +
        "fewest calls, of even size (9 keys at most 4 a call go as 3, 3 and 3), sent concurrently. For cache, also the keys per " +
        "read of the distributed cache.")]
    public int? MaxBatch { get; init; }

    /// <summary>Modes <c>local</c> and <c>cache</c>: the most memory each of the source's tables may take, e.g. <c>"512MiB"</c>.</summary>
    [Description("Modes local and cache, required: the most memory each table of the source (one per entity) may take, as an " +
        "integer and a unit B, KiB, MiB, GiB or TiB, e.g. \"512MiB\". A local load that would exceed it fails and the plan is " +
        "not served; a cache that reaches it drops its expired records, then its oldest.")]
    [RegularExpression(ByteSizePattern)]
    public string? MaxBytes { get; init; }

    /// <summary>Mode <c>local</c>: where the table's data comes from.</summary>
    [Description("Mode local, required: the update source that loads the table in the background, e.g. " +
        "{ \"source\": \"snapshot_files@1\", \"uri\": \"fixtures/item_snapshot\" }. A plan is served once every table it " +
        "reads has completed its first load.")]
    public UpdateSourceBinding? Bootstrap { get; init; }

    /// <summary>Mode <c>cache</c>: how long values are kept, and where else they are shared.</summary>
    [Description("Mode cache, required: { \"ttl_ms\": 60000 }, optionally \"stale_if_error_ms\" and a \"distributed\" cache " +
        "shared by the service's instances.")]
    public CacheBinding? Cache { get; init; }

    /// <summary>Connector-specific settings (endpoint, credentials reference, data file, …), bound to the settings type of its
    /// factory; modes <c>fetch</c> and <c>cache</c>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();

    /// <summary>The form of <see cref="MaxBytes"/>: an integer and an IEC unit (IEC 80000-13).</summary>
    public const string ByteSizePattern = "^[0-9]+(B|KiB|MiB|GiB|TiB)$";
}

/// <summary>An update source of a <c>local</c> feature source (design 6.6): its type and its own settings.</summary>
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
/// The cache policy of a source bound with mode <c>cache</c> (design 6.6), or of a service (<see cref="ServiceCacheBinding"/>):
/// the in-process table serves each value for <see cref="TtlMs"/> (RFC 9111 <c>max-age</c>), may serve it
/// <see cref="StaleIfErrorMs"/> longer when the origin (the connector, the service) fails (RFC 5861 <c>stale-if-error</c>),
/// and <see cref="Distributed"/> optionally shares values between the deployment's instances.
/// </summary>
public class CacheBinding
{
    [Description("Required: how long a value fetched from the origin (the connector, or the service) is served, in milliseconds. " +
        "Each key's lifetime varies by ±10% with the key's hash, so values written together do not all expire together.")]
    public long? TtlMs { get; init; }

    [Description("How much longer (milliseconds, default 0) an expired value may be served when the origin fails or times " +
        "out; its readers are then Degraded.")]
    public long? StaleIfErrorMs { get; init; }

    [Description("A cache shared by the deployment's instances, read on an in-process miss before the origin, e.g. " +
        "{ \"store\": \"redis@1\", \"endpoint\": \"cache.internal:6379\" }. Only an optimisation: when it fails or is slow, " +
        "the keys go to the origin.")]
    public DistributedCacheBinding? Distributed { get; init; }
}

/// <summary>
/// The cache of a service's calls (design 6.9): as a <c>cache</c> source's (<see cref="CacheBinding"/>), and the most memory
/// its in-process table may take. A call about no items is cached whole, one about items item by item.
/// </summary>
public sealed class ServiceCacheBinding : CacheBinding
{
    [Description("Required: the most memory the service's in-process cache table may take, as an integer and a unit B, KiB, " +
        "MiB, GiB or TiB, e.g. \"64MiB\". A cache that reaches it drops its expired records, then its oldest.")]
    [RegularExpression(FeatureSourceBinding.ByteSizePattern)]
    public string? MaxBytes { get; init; }
}

/// <summary>The distributed tier of a <c>cache</c> source: its store type, the framework's bound on its reads, and the store's own settings.</summary>
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

    [Description("Caches the service's answers, e.g. { \"ttl_ms\": 60000, \"max_bytes\": \"64MiB\" }: a call about no items whole, " +
        "one about items item by item (the items a call misses go to the service in one call). Keyed by the service version, the " +
        "outputs read and the inputs, not by the request's seed: a service that samples by the seed reuses one answer for ttl_ms.")]
    public ServiceCacheBinding? Cache { get; init; }

    /// <summary>The client's own settings, bound to the settings type of its factory.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();
}
