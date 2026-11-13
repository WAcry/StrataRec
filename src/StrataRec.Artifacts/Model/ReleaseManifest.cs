namespace StrataRec.Artifacts.Model;

/// <summary>
/// The single entry point a serving node subscribes to (design 10.2): artifact hashes, traffic assignment and the
/// experiments in flight.
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

    public AssignmentSpec Assignment { get; init; } = new();

    public Dictionary<string, ManifestExperiment> Experiments { get; init; } = new();
}

public sealed class ManifestArtifacts
{
    public required string Catalog { get; init; }

    public required string Bindings { get; init; }

    public required string Pipeline { get; init; }

    public Dictionary<string, string> Tables { get; init; } = new();
}

/// <summary>Domain/layer traffic split (design 10.2, 11.1).</summary>
public sealed class AssignmentSpec
{
    public int Buckets { get; init; } = 1000;

    public DomainSplitSpec? DomainSplit { get; init; }

    /// <summary>Domain → inclusive bucket range <c>[first, last]</c>.</summary>
    public Dictionary<string, DomainSpec> Domains { get; init; } = new();

    public Dictionary<string, LayerSpec> Layers { get; init; } = new();
}

public sealed class DomainSplitSpec
{
    public required string Salt { get; init; }

    /// <summary>Unit expressions; the first non-missing value is used (e.g. user id, then session id).</summary>
    public required List<string> Unit { get; init; }
}

public sealed class DomainSpec
{
    /// <summary>Inclusive bucket range <c>[first, last]</c>.</summary>
    public required int[] Buckets { get; init; }
}

public sealed class LayerSpec
{
    public required string Domain { get; init; }

    public required string Salt { get; init; }

    public required List<string> Unit { get; init; }
}

public sealed class ManifestExperiment
{
    /// <summary>Content id of the <see cref="ExperimentArtifact"/>.</summary>
    public required string Artifact { get; init; }

    public required string Layer { get; init; }

    /// <summary>Arm → inclusive bucket range in the layer; omitted with an external assignment provider.</summary>
    public Dictionary<string, int[]>? Arms { get; init; }
}
