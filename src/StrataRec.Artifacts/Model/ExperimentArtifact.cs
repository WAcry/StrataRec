using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// One experiment: a merge patch per arm against the patchable view (design 9.4, 11.3). Traffic allocation is the
/// experimentation platform's, so ramping never changes this artifact's hash.
/// </summary>
public sealed class ExperimentArtifact
{
    /// <summary>
    /// The sections of the patchable view (design 11.4) that come from the pipeline, in canonical order: one list for the
    /// compiler, which joins the view, and the experiment's JSON Schema, which describes an arm's patch of it.
    /// </summary>
    public static IReadOnlyList<string> PipelineSections { get; } = new[] { "params", "param_schema", "columns", "nodes", "funnel_groups", "log_taps" };

    /// <summary>The sections of the patchable view that come from the feature catalog, after the pipeline's.</summary>
    public static IReadOnlyList<string> CatalogSections { get; } = new[] { "features", "derived_features" };

    public required string ExperimentId { get; init; }

    public required string PipelineId { get; init; }

    public required string Owner { get; init; }

    public required string Description { get; init; }

    /// <summary>ISO date (<c>yyyy-MM-dd</c>) after which the experiment may not enter new manifests.</summary>
    public required string ExpiresAt { get; init; }

    /// <summary>Arm id → RFC 7396 merge patch; control arms are usually <c>{}</c>.</summary>
    public required Dictionary<string, JsonObject> Arms { get; init; }
}
