using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Baseline pipeline: request schema, column declarations, parameters, nodes, funnel groups, log taps
/// and output. Every experiment-patchable collection is an object keyed by a stable id.
/// </summary>
public sealed class PipelineArtifact
{
    public required string PipelineId { get; init; }

    public string? Description { get; init; }

    /// <summary><c>int64</c> or <c>string</c>.</summary>
    public string EntityKeyType { get; init; } = "int64";

    public Dictionary<string, RequestFieldSpec> RequestSchema { get; init; } = new();

    /// <summary>Candidate columns nodes write, one value per candidate, read as <c>col.&lt;name&gt;</c>.</summary>
    public Dictionary<string, ColumnSpec> Columns { get; init; } = new();

    /// <summary>
    /// Request columns nodes write, one value per request, read as <c>req.&lt;name&gt;</c> by what runs after the node
    /// that writes one: a later node's expressions and <c>when</c>, a log tap, the output's <c>columns</c> and
    /// <c>values</c> (not a funnel key, decided when the request starts, nor a derived feature). Written by
    /// <c>RequestAggregate@1</c> or by a custom operator. Declared as candidate columns are (type, default, flow): a
    /// value no node wrote is missing, and a non-nullable one needs a node that cannot be skipped to write it before
    /// every reader. Experiments may add request columns, not change these.
    /// </summary>
    public Dictionary<string, ColumnSpec> RequestColumns { get; init; } = new();

    public Dictionary<string, ParamSpec> ParamSchema { get; init; } = new();

    /// <summary>Parameter values; map-typed parameters are nested objects flattened to leaves.</summary>
    public JsonObject Params { get; init; } = new();

    public Dictionary<string, NodeSpec> Nodes { get; init; } = new();

    public Dictionary<string, FunnelGroupSpec> FunnelGroups { get; init; } = new();

    public Dictionary<string, LogTapSpec> LogTaps { get; init; } = new();

    public required OutputSpec Output { get; init; }

    /// <summary>Optional pipeline the host may serve when this one fails.</summary>
    public string? FallbackPipeline { get; init; }
}

public sealed class RequestFieldSpec
{
    /// <summary>Type spelling, e.g. <c>int64?</c>, <c>string</c>, <c>list&lt;int64&gt;</c>.</summary>
    public required string Type { get; init; }

    public string? Description { get; init; }
}

/// <summary>Declaration of a node-produced column: a candidate column, or a request column.</summary>
public sealed class ColumnSpec
{
    public required string Type { get; init; }

    /// <summary>Flow columns may be rewritten by later nodes on the same path.</summary>
    public bool Flow { get; init; }

    /// <summary>Replacement for non-finite numeric writes; required for non-nullable numeric columns.</summary>
    public JsonNode? Default { get; init; }

    public string? Description { get; init; }
}

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

public sealed class NodeSpec
{
    /// <summary>Semantic position; required unless <see cref="Role"/> is <c>fallback</c>.</summary>
    public int? Order { get; init; }

    /// <summary>Operator type and version, e.g. <c>TopK@1</c>.</summary>
    public required string Op { get; init; }

    public JsonObject? Config { get; init; }

    /// <summary>Request-level condition over <c>ctx.*</c>.</summary>
    public string? When { get; init; }

    /// <summary>
    /// Milliseconds one attempt of the node may take, at least 1; it never gets more than is left of the request's
    /// deadline. Past it the attempt fails as a timeout, and <c>on_failure</c> decides what follows. Default: what is left.
    /// </summary>
    public int? TimeoutMs { get; init; }

    /// <summary>
    /// The node is skipped, as by a false <c>when</c>, when less than this many milliseconds are left of the request's
    /// deadline as it is about to start. Default: it starts whatever is left.
    /// </summary>
    public int? MinRemainingMs { get; init; }

    /// <summary>
    /// What a failure of this node does: <c>fail_request</c> (default) fails the request, <c>skip</c> goes on
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

/// <summary>Request-level sampling group shared by taps.</summary>
public sealed class FunnelGroupSpec
{
    /// <summary>Key expressions; the first non-missing value is the sampling key.</summary>
    public required List<string> Key { get; init; }

    public required double Rate { get; init; }
}

public sealed class LogTapSpec
{
    public required string Group { get; init; }

    /// <summary><c>best_effort</c> (default) or <c>durable</c>.</summary>
    public string? Delivery { get; init; }

    public required LogTapAttachSpec Attach { get; init; }

    public LogTapRowsSpec? Rows { get; init; }

    public Dictionary<string, string>? Context { get; init; }

    public Dictionary<string, string>? Columns { get; init; }

    /// <summary>Expands to the named inputs of a node whose operator maps expressions to them (the model operators, or a custom operator's), e.g. <c>ctr_ranker.inputs</c>: the tap logs those expressions, evaluated at its own point.</summary>
    public string? ColumnsFrom { get; init; }

    /// <summary>Reserved: only <c>false</c> is accepted. A tap never triggers a fetch; it logs what the serving path fetched before its point.</summary>
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

public sealed class OutputSpec
{
    /// <summary>Parameter holding the number of results to return.</summary>
    public required string LimitParam { get; init; }

    /// <summary>Output name → column expression (usually a plain <c>col.*</c> reference).</summary>
    public Dictionary<string, string> Columns { get; init; } = new();

    /// <summary>
    /// Output name → request-level expression (<c>req.*</c>, <c>ctx.*</c>, <c>param.*</c>; usually a plain <c>req.*</c>
    /// reference), returned once with the result, beside its items.
    /// </summary>
    public Dictionary<string, string> Values { get; init; } = new();
}
