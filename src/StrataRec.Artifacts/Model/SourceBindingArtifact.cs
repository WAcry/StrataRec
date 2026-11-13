using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Per-environment bindings of logical feature sources, models and candidate sources to concrete clients
/// (design 6.4). Experiments cannot patch this artifact.
/// </summary>
public sealed class SourceBindingArtifact
{
    public required string Environment { get; init; }

    public Dictionary<string, FeatureSourceBinding> Sources { get; init; } = new();

    public Dictionary<string, ClientBinding> Models { get; init; } = new();

    public Dictionary<string, ClientBinding> CandidateSources { get; init; } = new();

    /// <summary>
    /// The client, connector and update-source types the bindings name (design 10.4): a binary that lacks one cannot
    /// serve them. The one place that knows which binding settings name a type, so a new kind of setting is added here.
    /// </summary>
    public List<string> RequiredTypes()
    {
        var types = new List<string>();
        foreach (var (_, source) in Sources)
        {
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

        foreach (var (_, model) in Models)
        {
            types.Add(model.Client);
        }

        foreach (var (_, source) in CandidateSources)
        {
            types.Add(source.Client);
        }

        return types;
    }
}

/// <summary>Binding of a logical feature source (design 6.4, 6.6).</summary>
/// <remarks>
/// <c>mode</c> selects which of the other settings apply: <c>fetch</c> uses <c>connector</c>, <c>timeout_ms</c>,
/// <c>max_batch</c>, <c>lookup</c> and connector settings; <c>local</c> uses <c>max_bytes</c> and <c>bootstrap</c>;
/// <c>cache</c> uses the connector and its settings (the origin), <c>timeout_ms</c>, <c>max_batch</c>, <c>max_bytes</c>
/// and <c>cache</c>. A setting the binding's mode does not take is an error (the Features runtime checks it, with the
/// pointer of the setting), so a binding switched from one mode to another cannot keep a setting that silently does
/// nothing.
/// </remarks>
public sealed class FeatureSourceBinding
{
    /// <summary><c>fetch</c> (default), <c>local</c> or <c>cache</c>.</summary>
    [Description("Supply mode (design 6.6): 'fetch' (default) fetches from the connector on every request; 'local' serves the " +
        "features from an in-process table that 'bootstrap' loads in the background, with no I/O on the request path; 'cache' " +
        "keeps what the connector returned in an in-process table (and optionally a distributed cache) for 'cache.ttl_ms'. " +
        "Settings of another mode are errors.")]
    [AllowedValues("fetch", "local", "cache")]
    public string? Mode { get; init; }

    /// <summary>Connector type and version, e.g. <c>redis_hash@1</c> or <c>memory@1</c>.</summary>
    [Description("Modes fetch and cache, required: connector type and version, e.g. 'memory@1' (for cache, the origin the cache " +
        "is in front of). Other members of the binding not listed here are the connector's own settings (e.g. 'data_file').")]
    public string? Connector { get; init; }

    [Description("Modes fetch and cache: time limit of one connector call, in milliseconds (default 50). For cache, the whole " +
        "read of the keys a request misses (the distributed cache, then the connector) stays within it.")]
    public int? TimeoutMs { get; init; }

    /// <summary>Maximum keys per connector call; larger batches are split and sent concurrently.</summary>
    [Description("Modes fetch and cache: maximum keys per connector call (default 1000); larger batches are split and sent " +
        "concurrently. For cache, also the keys per read of the distributed cache.")]
    public int? MaxBatch { get; init; }

    /// <summary><c>context</c> for sources organized by context key (design 6.5).</summary>
    [Description("Mode fetch: 'context' for sources organized by context key (design 6.5).")]
    public string? Lookup { get; init; }

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

    /// <summary>Connector-specific settings (endpoint, credentials reference, data file, …); modes <c>fetch</c> and <c>cache</c>.</summary>
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
        "settings: for snapshot_files@1, 'uri' (the snapshot directory or its snapshot.json, relative to the config root) and " +
        "optionally 'reload' (an ISO 8601 duration such as \"PT1H\").")]
    public string? Source { get; init; }

    /// <summary>Settings of the update source, e.g. <c>uri</c> and <c>reload</c> for <c>snapshot_files@1</c>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();
}

/// <summary>
/// The cache policy of a source bound with mode <c>cache</c> (design 6.6): the in-process table serves each value for
/// <see cref="TtlMs"/> (RFC 9111 <c>max-age</c>), may serve it <see cref="StaleIfErrorMs"/> longer when the connector fails
/// (RFC 5861 <c>stale-if-error</c>), and <see cref="Distributed"/> optionally shares values between the service's instances.
/// </summary>
public sealed class CacheBinding
{
    [Description("Required: how long a value fetched from the connector is served, in milliseconds. Each key's lifetime varies " +
        "by ±10% with the key's hash, so values written together do not all expire together.")]
    public long? TtlMs { get; init; }

    [Description("How much longer (milliseconds, default 0) an expired value may be served when the connector fails or times " +
        "out; its readers are then Degraded.")]
    public long? StaleIfErrorMs { get; init; }

    [Description("A cache shared by the service's instances, read on an in-process miss before the connector, e.g. " +
        "{ \"store\": \"redis@1\", \"endpoint\": \"cache.internal:6379\" }. Only an optimisation: when it fails or is slow, " +
        "the keys go to the connector.")]
    public DistributedCacheBinding? Distributed { get; init; }
}

/// <summary>The distributed tier of a <c>cache</c> source: its store type, the framework's bound on its reads, and the store's own settings.</summary>
public sealed class DistributedCacheBinding
{
    [Description("Required: store type and version, e.g. 'redis@1'. The other members are the store's own settings: for " +
        "redis@1, 'endpoint' (host:port; for a cluster, any node), and optionally 'key_prefix' (default 'stratarec:'), 'user', " +
        "'password' (a secret:// reference), 'tls' (default false) and 'database' (default 0).")]
    public string? Store { get; init; }

    [Description("The longest a request waits for one read of the distributed cache, in milliseconds (default: half the " +
        "source's timeout_ms); always within the source's timeout_ms, so the connector keeps the rest of it.")]
    public int? TimeoutMs { get; init; }

    /// <summary>The store's own settings, e.g. <c>endpoint</c> and <c>credentials</c> for <c>redis@1</c>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();
}

/// <summary>Binding of a model or candidate source to a client implementation.</summary>
public sealed class ClientBinding
{
    /// <summary>Client type and version, e.g. <c>grpc_tensor@1</c>, <c>memory_list@1</c>, <c>linear@1</c>.</summary>
    public required string Client { get; init; }

    public int? TimeoutMs { get; init; }

    /// <summary>Client-specific settings.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();
}
