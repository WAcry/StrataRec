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
    /// <inheritdoc cref="ManifestExperiment.Layer"/>
    public required string Layer { get; init; }
}

/// <summary>Repository policy <c>policy.jsonc</c> (design 11.4): patchable paths, owners, log budget.</summary>
public sealed class PolicyArtifact
{
    /// <summary>RFC 6901 prefixes experiments may add, delete and modify under.</summary>
    public List<string> ExperimentPatchable { get; init; } = new() { "/params", "/nodes", "/log_taps" };

    /// <summary>RFC 6901 prefixes experiments may only add new entries under.</summary>
    public List<string> ExperimentAddOnly { get; init; } = new() { "/columns", "/param_schema", "/features", "/derived_features", "/funnel_groups" };

    /// <summary>RFC 6901 prefix → owning teams that must approve changes (mapped to code review).</summary>
    public Dictionary<string, List<string>> Owners { get; init; } = new();

    /// <summary>Estimated log bytes per request allowed per pipeline (design 8.6).</summary>
    public long? LogBytesPerRequestBudget { get; init; }
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
