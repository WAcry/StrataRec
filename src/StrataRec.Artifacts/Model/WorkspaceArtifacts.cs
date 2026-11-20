using StrataRec.Artifacts.Json;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Authoring-side release file <c>releases/&lt;pipeline&gt;.&lt;env&gt;.jsonc</c> (design 10.1): which artifacts make up
/// a release and each in-flight experiment's layer. <c>stratarec build</c> turns it into a <see cref="ReleaseManifest"/>.
/// </summary>
/// <remarks>
/// It composes a release and nothing else: which arms a request is in (ratios, ramping, holdouts) is the
/// experimentation platform's, so ramping an experiment never changes a release.
/// </remarks>
public sealed class ReleaseArtifact
{
    public required string PipelineId { get; init; }

    public required string Environment { get; init; }

    /// <summary>Catalog id; defaults to the pipeline id.</summary>
    public string? Catalog { get; init; }

    /// <summary>Bindings environment; defaults to <see cref="Environment"/>.</summary>
    public string? Bindings { get; init; }

    public List<string> Tables { get; init; } = new();

    public Dictionary<string, ReleaseExperiment> Experiments { get; init; } = new();
}

/// <summary>An experiment in flight in a release.</summary>
public sealed class ReleaseExperiment
{
    /// <summary>
    /// The experiment's layer: a request is in at most one arm per layer, so the arms of one layer are never validated
    /// together and the arms of different layers are, unless one experiment yields to the other (design 11.1, 11.5).
    /// </summary>
    public required string Layer { get; init; }

    /// <summary>
    /// Experiments of this release this one yields to (design 11.1): on a request also in an arm of one of them, this
    /// experiment runs in none of its arms (its layer runs the baseline, and the request records it as yielded), so the
    /// two never run together, as if they shared a layer. For two experiments of different layers that conflict or
    /// interact (design 11.5) when putting them into one layer would cost too much traffic: this one gives up the
    /// other's share.
    /// </summary>
    public List<string>? YieldsTo { get; init; }
}

/// <summary>
/// Repository policy <c>policy.jsonc</c> (design 11.4): who reviews an experiment that touches a part of the pipeline.
/// </summary>
/// <remarks>
/// Review only: it does not change what compiles or serves, so it is not part of a release. What experiments may change
/// is the engine's rule (the patchable view), the same in every repository.
/// </remarks>
public sealed class PolicyArtifact
{
    /// <summary>
    /// RFC 6901 prefix into the patchable view → teams that review an experiment arm touching it, at or under the prefix,
    /// or replacing what contains it, e.g. <c>{ "/nodes/safety_filter": ["trust-safety"] }</c>. <c>stratarec explain-plan</c>
    /// lists each arm's owners for code review to route.
    /// </summary>
    public Dictionary<JsonPointer, List<string>> Owners { get; init; } = new();
}

/// <summary>Static lookup table for <c>lookup(table, key, default)</c> (design 7.6).</summary>
public sealed class DataTableArtifact
{
    public required string TableId { get; init; }

    /// <summary><c>int64</c> or <c>string</c>.</summary>
    public required string KeyType { get; init; }

    /// <summary><c>int64</c>, <c>float64</c>, <c>bool</c> or <c>string</c>.</summary>
    public required string ValueType { get; init; }

    /// <summary>Key (as a JSON property name) → value.</summary>
    public required Dictionary<string, JsonNode> Entries { get; init; }

    public string? Description { get; init; }
}
