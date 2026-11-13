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
}

/// <summary>Binding of a logical feature source (design 6.4, 6.6).</summary>
public sealed class FeatureSourceBinding
{
    /// <summary><c>fetch</c> (default), <c>cache</c> or <c>local</c>.</summary>
    public string? Mode { get; init; }

    /// <summary>Connector type and version, e.g. <c>redis_hash@1</c> or <c>memory@1</c>.</summary>
    public string? Connector { get; init; }

    public int? TimeoutMs { get; init; }

    /// <summary>Maximum keys per connector call; larger batches are split and sent concurrently.</summary>
    public int? MaxBatch { get; init; }

    /// <summary><c>context</c> for sources organized by context key (design 6.5).</summary>
    public string? Lookup { get; init; }

    /// <summary>Connector-specific settings (endpoint, credentials reference, data file, …).</summary>
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
