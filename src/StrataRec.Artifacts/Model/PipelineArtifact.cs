using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Baseline pipeline (design 9.3): request schema, column declarations, parameters, nodes, funnel groups, log taps
/// and output. Every experiment-patchable collection is an object keyed by a stable id (design 11.3).
/// </summary>
public sealed class PipelineArtifact
{
    public required string PipelineId { get; init; }

    public string? Description { get; init; }

    /// <summary><c>int64</c> or <c>string</c>.</summary>
    public string EntityKeyType { get; init; } = "int64";

    public Dictionary<string, RequestFieldSpec> RequestSchema { get; init; } = new();

    public Dictionary<string, ColumnSpec> Columns { get; init; } = new();

    public Dictionary<string, ParamSpec> ParamSchema { get; init; } = new();

    /// <summary>Parameter values; map-typed parameters are nested objects flattened to leaves (design 11.8).</summary>
    public JsonObject Params { get; init; } = new();

    public Dictionary<string, NodeSpec> Nodes { get; init; } = new();

    public Dictionary<string, FunnelGroupSpec> FunnelGroups { get; init; } = new();

    public Dictionary<string, LogTapSpec> LogTaps { get; init; } = new();

    public required OutputSpec Output { get; init; }

    /// <summary>Optional pipeline the host may serve when this one fails (design 5.6).</summary>
    public string? FallbackPipeline { get; init; }
}

public sealed class RequestFieldSpec
{
    /// <summary>Type spelling, e.g. <c>int64?</c>, <c>string</c>, <c>list&lt;int64&gt;</c>.</summary>
    public required string Type { get; init; }

    public string? Description { get; init; }
}

/// <summary>Declaration of a node-produced column (design 4.6).</summary>
public sealed class ColumnSpec
{
    public required string Type { get; init; }

    /// <summary>Flow columns may be rewritten by later nodes on the same path.</summary>
    public bool Flow { get; init; }

    /// <summary>Replacement for non-finite numeric writes; required for non-nullable numeric columns.</summary>
    public JsonNode? Default { get; init; }

    public string? Description { get; init; }
}

/// <summary>Parameter schema entry (design 11.8).</summary>
public sealed class ParamSpec
{
    /// <summary><c>int64</c>, <c>float64</c>, <c>bool</c>, <c>string</c> or <c>map&lt;string,T&gt;</c>.</summary>
    public required string Type { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    [JsonPropertyName("enum")]
    public List<JsonNode>? Enum { get; init; }

    public string? Description { get; init; }
}

/// <summary>A pipeline node (design 5.1).</summary>
public sealed class NodeSpec
{
    /// <summary>Semantic position; required unless <see cref="Role"/> is <c>fallback</c>.</summary>
    public int? Order { get; init; }

    /// <summary>Operator type and version, e.g. <c>TopK@1</c>.</summary>
    public required string Op { get; init; }

    public JsonObject? Config { get; init; }

    /// <summary>Request-level condition over <c>ctx.*</c> (design 5.5).</summary>
    public string? When { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MinRemainingMs { get; init; }

    /// <summary>
    /// What a failure of this node does (design 5.6): <c>fail_request</c> (default) fails the request, <c>skip</c> goes on
    /// without the node's writes, <c>fallback</c> runs the node named by <c>fallback</c> in its place.
    /// </summary>
    [AllowedValues("fail_request", "skip", "fallback")]
    public string? OnFailure { get; init; }

    /// <summary>With <c>on_failure</c> <c>fallback</c>: the node, declared with <c>"role": "fallback"</c>, that runs when this one fails.</summary>
    public string? Fallback { get; init; }

    /// <summary>
    /// With <c>on_failure</c> <c>fallback</c>: milliseconds of the request's remaining time this node leaves to its fallback.
    /// Default: the fallback's <c>timeout_ms</c>, or 0 when it has none.
    /// </summary>
    public int? FallbackReserveMs { get; init; }

    /// <summary><c>fallback</c> for nodes that only run as another node's fallback.</summary>
    [AllowedValues("fallback")]
    public string? Role { get; init; }

    public bool? FetchBarrier { get; init; }

    /// <summary><c>auto</c> (default), <c>always</c> or <c>never</c>.</summary>
    [AllowedValues("auto", "always", "never")]
    public string? Compact { get; init; }

    public string? Description { get; init; }
}

/// <summary>Request-level sampling group shared by taps (design 8.2).</summary>
public sealed class FunnelGroupSpec
{
    /// <summary>Key expressions; the first non-missing value is the sampling key.</summary>
    public required List<string> Key { get; init; }

    public required double Rate { get; init; }
}

/// <summary>Declarative log capture point (design 8.3).</summary>
public sealed class LogTapSpec
{
    public required string Group { get; init; }

    /// <summary><c>best_effort</c> (default) or <c>durable</c>.</summary>
    public string? Delivery { get; init; }

    public required LogTapAttachSpec Attach { get; init; }

    public LogTapRowsSpec? Rows { get; init; }

    public Dictionary<string, string>? Context { get; init; }

    public Dictionary<string, string>? Columns { get; init; }

    /// <summary>Expands to a model node's inputs, e.g. <c>ctr_ranker.inputs</c>.</summary>
    public string? ColumnsFrom { get; init; }

    public bool? FetchMissing { get; init; }
}

public sealed class LogTapAttachSpec
{
    public string? After { get; init; }

    public bool? Output { get; init; }
}

public sealed class LogTapRowsSpec
{
    /// <summary><c>entered</c>, <c>survived</c> (default) or <c>dropped</c>.</summary>
    public string? Scope { get; init; }

    public string? Filter { get; init; }

    public double? Rate { get; init; }

    public int? MaxRows { get; init; }
}

/// <summary>What the pipeline returns (design 3.4).</summary>
public sealed class OutputSpec
{
    /// <summary>Parameter holding the number of results to return.</summary>
    public required string LimitParam { get; init; }

    /// <summary>Output name → column expression (usually a plain <c>col.*</c> reference).</summary>
    public Dictionary<string, string> Columns { get; init; } = new();
}
