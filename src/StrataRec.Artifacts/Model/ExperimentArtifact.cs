using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// One experiment: a merge patch per arm against the patchable view. Traffic allocation is the
/// experimentation platform's, so ramping never changes this artifact's hash.
/// </summary>
public sealed class ExperimentArtifact
{
    /// <summary>
    /// The sections of the patchable view that come from the pipeline, in canonical order.
    /// </summary>
    public static IReadOnlyList<string> PipelineSections { get; } = new[] { "params", "param_schema", "columns", "request_columns", "nodes", "funnel_groups", "log_taps" };

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

    /// <summary>
    /// Interactions with experiments of other layers that were reviewed and may happen together: experiment id → the
    /// columns, e.g. <c>{ "exp_freshness_calibration": ["score.final"] }</c>, a request column written as
    /// <c>req.&lt;name&gt;</c>. The two experiments' interaction warnings on those columns are not repeated, so a
    /// reviewed pair stops warning on every validate, build and load; a new column still warns. An entry no interaction
    /// of this release needs is a warning, so a review does not outlive the interaction it was for.
    /// </summary>
    public Dictionary<string, List<string>>? ReviewedInteractions { get; init; }
}
