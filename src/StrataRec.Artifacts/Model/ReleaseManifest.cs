namespace StrataRec.Artifacts.Model;

/// <summary>
/// The single entry point a serving node subscribes to (design 10.2): artifact hashes and the experiments in flight
/// with their layers. Which arms a request is in is the experimentation platform's answer, never part of a release.
/// </summary>
public sealed class ReleaseManifest
{
    public required string PipelineId { get; init; }

    public required string Environment { get; init; }

    public long Seq { get; init; }

    public long MinAcceptedSeq { get; init; }

    public string? Previous { get; init; }

    /// <summary>Capabilities required from the binary; computed by the build (design 10.4).</summary>
    public List<string> Requires { get; init; } = new();

    public required ManifestArtifacts Artifacts { get; init; }

    public Dictionary<string, ManifestExperiment> Experiments { get; init; } = new();
}

public sealed class ManifestArtifacts
{
    public required string Catalog { get; init; }

    public required string Bindings { get; init; }

    public required string Pipeline { get; init; }

    public Dictionary<string, string> Tables { get; init; } = new();

    /// <summary>
    /// The policy the release was validated with (patchable paths, design 11.4). Nodes re-run patch validation at load
    /// with the same policy, so a permissive repository policy is not rejected by stricter defaults.
    /// </summary>
    public string? Policy { get; init; }
}

public sealed class ManifestExperiment
{
    /// <summary>Content id of the <see cref="ExperimentArtifact"/>.</summary>
    public required string Artifact { get; init; }

    /// <summary>
    /// The experiment's layer: a request is in at most one arm per layer, so the arms of one layer are never validated
    /// together and the arms of different layers always are (design 11.5).
    /// </summary>
    public required string Layer { get; init; }
}
