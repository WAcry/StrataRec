using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Compilation;

/// <summary>Everything needed to compile one pipeline's unified plan (design 10.3, 12).</summary>
public sealed class PlanCompileRequest
{
    public required PipelineArtifact Pipeline { get; init; }

    public required FeatureCatalogArtifact Catalog { get; init; }

    public required SourceBindingArtifact Bindings { get; init; }

    public IReadOnlyList<DataTableArtifact> Tables { get; init; } = Array.Empty<DataTableArtifact>();

    public PolicyArtifact Policy { get; init; } = new();

    /// <summary>Experiments in the manifest with their layers. Empty compiles the plain baseline.</summary>
    public IReadOnlyList<ExperimentPlacement> Experiments { get; init; } = Array.Empty<ExperimentPlacement>();

    /// <summary>Content id of the manifest, or a local label.</summary>
    public string ManifestId { get; init; } = "local";

    /// <summary>
    /// Content id of each artifact the request was assembled from (<c>pipeline:&lt;id&gt;</c>, <c>catalog:&lt;id&gt;</c>,
    /// <c>bindings:&lt;env&gt;</c>, <c>policy</c>, <c>table:&lt;id&gt;</c>, <c>experiment:&lt;id&gt;</c>), when the loader knows them.
    /// Not used by compilation; replay bundles record it (design 13.4) so a replay can say which artifacts changed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ArtifactIds { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Compile baseline ⊕ arm for every arm on its own (design 11.5 check 1). This is what proves the independence
    /// rule (design 11.6: an arm may only reference the baseline and itself). On by default; hosts that already ran
    /// manifest validation in CI may turn it off to shorten load time.
    /// </summary>
    public bool ValidateArmsIndependently { get; init; } = true;

    /// <summary>Upper bound on arm combinations per node, C(n) in design 12.3.</summary>
    public int MaxCombinationsPerNode { get; init; } = 1024;

    /// <summary>Upper bound on arm combinations enumerated by the data-flow check (design 11.5 check 3).</summary>
    public int MaxCombinationsChecked { get; init; } = 4096;

    /// <summary>When set, experiments past <c>expires_at</c> are rejected (design 11.7).</summary>
    public DateOnly? Today { get; init; }
}

/// <summary>An experiment and the layer it runs in (layers come from the manifest, not the artifact, design 11.2).</summary>
public sealed record ExperimentPlacement(ExperimentArtifact Experiment, string Layer);

/// <summary>The result of a compile: a plan when there were no errors, and every diagnostic either way.</summary>
public sealed class PlanCompilation
{
    public ExecutionPlan? Plan { get; init; }

    public required IReadOnlyList<ConfigDiagnostic> Diagnostics { get; init; }

    public required PlanReport Report { get; init; }

    public bool Succeeded => Plan is not null;

    /// <summary>Returns the plan or throws <see cref="ConfigException"/> listing every error.</summary>
    public ExecutionPlan GetPlanOrThrow()
    {
        if (Plan is not null)
        {
            return Plan;
        }

        var errors = new List<ConfigDiagnostic>();
        foreach (var d in Diagnostics)
        {
            if (d.IsError)
            {
                errors.Add(d);
            }
        }

        throw new ConfigException("Plan compilation", errors);
    }
}

/// <summary>What the compiler did, for <c>stratarec explain-plan</c> and load reports (design 12.3, 13.4).</summary>
public sealed class PlanReport
{
    public required IReadOnlyList<NodeVariantReport> Nodes { get; init; }

    public required IReadOnlyList<ArmReport> Arms { get; init; }

    /// <summary>Arm combinations checked by the data-flow check.</summary>
    public int CombinationsChecked { get; init; }

    /// <summary>False when the combination space exceeded the limit and only targeted combinations were checked.</summary>
    public bool CombinationCheckExhaustive { get; init; } = true;
}

/// <summary>Variants of one node id (design 12.3).</summary>
/// <param name="NodeId">Node id.</param>
/// <param name="Layers">Layers whose arms modify the node, L(n).</param>
/// <param name="Combinations">C(n).</param>
/// <param name="Variants">Distinct non-empty compiled definitions.</param>
/// <param name="Orders">Timeline orders the node occupies across combinations (empty for fallbacks).</param>
public sealed record NodeVariantReport(string NodeId, IReadOnlyList<string> Layers, int Combinations, int Variants, IReadOnlyList<int> Orders);

/// <summary>Footprint summary of one arm (design 11.5).</summary>
public sealed record ArmReport(
    string Id,
    string Layer,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> ParamLeaves,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> Owners);
