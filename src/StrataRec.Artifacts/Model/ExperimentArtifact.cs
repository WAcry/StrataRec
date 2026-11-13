using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// One experiment: a merge patch per arm against the patchable view (design 9.4, 11.3). Traffic allocation is the
/// experimentation platform's, so ramping never changes this artifact's hash.
/// </summary>
public sealed class ExperimentArtifact
{
    public required string ExperimentId { get; init; }

    public required string PipelineId { get; init; }

    public required string Owner { get; init; }

    public required string Description { get; init; }

    /// <summary>ISO date (<c>yyyy-MM-dd</c>) after which the experiment may not enter new manifests.</summary>
    public required string ExpiresAt { get; init; }

    /// <summary>Arm id → RFC 7396 merge patch; control arms are usually <c>{}</c>.</summary>
    public required Dictionary<string, JsonObject> Arms { get; init; }
}
