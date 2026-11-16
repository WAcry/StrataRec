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

    /// <summary>
    /// Capabilities the baseline requires of the binary, computed by the build (design 10.4); each experiment lists what it
    /// needs beyond them (<see cref="ManifestExperiment.Requires"/>), and a node needs both.
    /// </summary>
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

    /// <summary>
    /// Capabilities the experiment requires of the binary beyond the baseline's, computed by the build from what its
    /// compile used (design 10.4): removing the experiment removes them, so a revocation lets a binary that lacks them
    /// serve the rest, and a controller rolling a binary back knows which experiments to remove.
    /// </summary>
    public List<string> Requires { get; init; } = new();
}
