namespace StrataRec.Artifacts.Model;

/// <summary>
/// The single entry point a serving node subscribes to (design 10.2): artifact hashes and the experiments in flight
/// with their layers. Which arms a request is in is the experimentation platform's answer, never part of a release.
/// </summary>
public sealed class ReleaseManifest
{
    public required string PipelineId { get; init; }

    public required string Environment { get; init; }

    /// <summary>
    /// Release order: a node applies a manifest only above the sequence its pipeline serves, so a slow or late delivery of an
    /// older release never replaces a newer one. A revocation is a newer release like any other (design 10.6).
    /// </summary>
    public long Seq { get; init; }

    public string? Previous { get; init; }

    /// <summary>
    /// Capabilities the baseline requires of the binary, computed by the build (design 10.4); each experiment lists what it
    /// needs beyond them (<see cref="ManifestExperiment.Requires"/>), and a node needs both.
    /// </summary>
    public List<string> Requires { get; init; } = new();

    public required ManifestArtifacts Artifacts { get; init; }

    public Dictionary<string, ManifestExperiment> Experiments { get; init; } = new();

    /// <summary>
    /// Experiments <c>stratarec revoke</c> removed, by name: <c>stratarec build</c> refuses to release one of them again
    /// unchanged, so the next build from a repository that still lists it does not undo the stop (design 10.6). A
    /// revocation keeps what the one before it listed; a build lists nothing (it released nothing revoked unchanged, and
    /// what it left out it left out on purpose). Nodes do not read it.
    /// </summary>
    public Dictionary<string, RevokedExperiment> Revoked { get; init; } = new();

    /// <summary>
    /// The stop in force on <paramref name="experiment"/>: its <see cref="Revoked"/> entry, unless this manifest releases
    /// the experiment again. One released again was brought back, and a controller that publishes it may have kept the
    /// entry (review 39); <c>build</c>, <c>revoke</c> and <c>status</c> all read stops through this.
    /// </summary>
    public bool TryGetStop(string experiment, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RevokedExperiment? stop)
    {
        if (Revoked.TryGetValue(experiment, out stop) && !Experiments.ContainsKey(experiment))
        {
            return true;
        }

        stop = null;
        return false;
    }
}

/// <summary>An experiment a revocation removed (<see cref="ReleaseManifest.Revoked"/>).</summary>
public sealed class RevokedExperiment
{
    /// <summary>Content id of the <see cref="ExperimentArtifact"/> it removed: a changed experiment is released again.</summary>
    public required string Artifact { get; init; }

    /// <summary>The sequence of the manifest that removed it.</summary>
    public long Seq { get; init; }
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
