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
/// <c>max_batch</c>, <c>lookup</c> and connector settings; <c>local</c> uses <c>max_bytes</c> and <c>bootstrap</c>. A
/// setting of the other mode is an error (the Features runtime checks it, with the pointer of the setting), so a binding
/// switched from one mode to the other cannot keep a setting that silently does nothing.
/// </remarks>
public sealed class FeatureSourceBinding
{
    /// <summary><c>fetch</c> (default) or <c>local</c>.</summary>
    [Description("Supply mode (design 6.6): 'fetch' (default) fetches from the connector on every request; 'local' serves the " +
        "features from an in-process table that 'bootstrap' loads in the background, with no I/O on the request path. " +
        "Settings of the other mode are errors.")]
    [AllowedValues("fetch", "local")]
    public string? Mode { get; init; }

    /// <summary>Connector type and version, e.g. <c>redis_hash@1</c> or <c>memory@1</c>.</summary>
    [Description("Mode fetch, required: connector type and version, e.g. 'memory@1'. Other members of the binding not listed " +
        "here are the connector's own settings (e.g. 'data_file').")]
    public string? Connector { get; init; }

    [Description("Mode fetch: time limit of one connector call, in milliseconds (default 50).")]
    public int? TimeoutMs { get; init; }

    /// <summary>Maximum keys per connector call; larger batches are split and sent concurrently.</summary>
    [Description("Mode fetch: maximum keys per connector call (default 1000); larger batches are split and sent concurrently.")]
    public int? MaxBatch { get; init; }

    /// <summary><c>context</c> for sources organized by context key (design 6.5).</summary>
    [Description("Mode fetch: 'context' for sources organized by context key (design 6.5).")]
    public string? Lookup { get; init; }

    /// <summary>Mode <c>local</c>: the most memory each of the source's tables may take, e.g. <c>"512MiB"</c>.</summary>
    [Description("Mode local, required: the most memory each table of the source (one per entity) may take, as an integer and " +
        "a unit B, KiB, MiB, GiB or TiB, e.g. \"512MiB\". A load that would exceed it fails and the plan is not served.")]
    [RegularExpression(ByteSizePattern)]
    public string? MaxBytes { get; init; }

    /// <summary>Mode <c>local</c>: where the table's data comes from.</summary>
    [Description("Mode local, required: the update source that loads the table in the background, e.g. " +
        "{ \"source\": \"snapshot_files@1\", \"uri\": \"fixtures/item_snapshot\" }. A plan is served once every table it " +
        "reads has completed its first load.")]
    public UpdateSourceBinding? Bootstrap { get; init; }

    /// <summary>Connector-specific settings (endpoint, credentials reference, data file, …); mode <c>fetch</c> only.</summary>
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
