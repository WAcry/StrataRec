using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Compilation;

/// <summary>Everything needed to compile one pipeline's unified plan (design 10.3, 12).</summary>
public sealed class PlanCompileRequest
{
    public required PipelineArtifact Pipeline { get; init; }

    public required FeatureCatalogArtifact Catalog { get; init; }

    public required SourceBindingArtifact Bindings { get; init; }

    public IReadOnlyList<DataTableArtifact> Tables { get; init; } = Array.Empty<DataTableArtifact>();

    /// <summary>
    /// The pipelines this one's nodes may call, when the loader knows them (a workspace's): calls are then checked at
    /// compile time. Null, as when an engine loads one pipeline's release, checks them when they are made. Not part of the
    /// configuration: the engine's copy of what a plan was compiled from (for a configuration override) has none, so an
    /// override's calls are checked when they are made.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IPipelineDirectory? Pipelines { get; init; }

    /// <summary>Who reviews what (<c>policy.jsonc</c>): only for the arm reports' owners; a release does not carry it.</summary>
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

    /// <summary>
    /// When set, experiments past <c>expires_at</c> are rejected and those expiring within
    /// <see cref="ExpiryWarningDays"/> are warned about (design 11.7). Set only where a release is decided
    /// (<c>validate</c>, <c>build</c>): loading a plan, replaying a recorded request or explaining one must not start
    /// failing on a calendar date.
    /// </summary>
    public DateOnly? Today { get; init; }

    /// <summary>How many days before <c>expires_at</c> an experiment is warned about.</summary>
    public const int ExpiryWarningDays = 14;
}

/// <summary>
/// What a release needs of a binary (design 10.4): the baseline's capabilities, and each experiment's beyond them, so
/// removing an experiment removes exactly what it needed.
/// </summary>
/// <param name="Baseline">What the pipeline without experiments needs, sorted.</param>
/// <param name="Experiments">Experiment id → what it needs that the baseline does not, sorted (empty for most).</param>
public sealed record ReleaseRequirements(IReadOnlyList<string> Baseline, IReadOnlyDictionary<string, IReadOnlyList<string>> Experiments);

/// <summary>An experiment and the layer it runs in (layers come from the manifest, not the artifact, design 11.2).</summary>
public sealed record ExperimentPlacement(ExperimentArtifact Experiment, string Layer)
{
    /// <summary>
    /// The experiments it yields to (<see cref="ManifestExperiment.YieldsTo"/>). One the compile does not have yields
    /// nothing: a release that stopped the other experiment (design 10.6) loads without being checked again.
    /// </summary>
    public IReadOnlyList<string> YieldsTo { get; init; } = [];

    /// <summary>
    /// The release file or manifest that placed it, as diagnostics name it: what is wrong with <see cref="YieldsTo"/> is
    /// reported there, at <c>/experiments/&lt;id&gt;/yields_to/&lt;i&gt;</c>.
    /// </summary>
    public string Release { get; init; } = "release";
}

/// <summary>The result of a compile: a plan when there were no errors, and every diagnostic either way.</summary>
public sealed class PlanCompilation
{
    public ExecutionPlan? Plan { get; init; }

    public required IReadOnlyList<ConfigDiagnostic> Diagnostics { get; init; }

    public required PlanReport Report { get; init; }

    /// <summary>
    /// What the compiled configuration needs of a binary (design 10.4), sorted: the operator of every node it compiled
    /// and the types of the bindings it bound (the client and distributed cache store of every service it calls, the
    /// connector, update source and distributed store of every feature source it reads). Every arm's included; <see cref="PlanCompiler.Requirements"/>
    /// tells the baseline's from each experiment's.
    /// </summary>
    public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The configuration override <see cref="PlanCompiler.CompileMerged"/> compiled, as its check read it
    /// (<see cref="StrictJson.Check(JsonNode?, int, out JsonNode?)"/>): a copy of the host's tree in which every CLR value
    /// it wrapped is the JSON it wrote once (but a number or string the canonical form reports), and a wrapped
    /// <see cref="System.Text.Json.JsonElement"/> the JSON of the element. Whoever keeps the override for this
    /// compile (a recording of the request) keeps this one: the host's tree would write those values again, perhaps
    /// differently (review 83). Null without an override, or when the override failed its check.
    /// </summary>
    public JsonObject? ConfigOverride { get; internal set; }

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

    /// <summary>Each yield of the release (design 11.1), with what it keeps from running together.</summary>
    public IReadOnlyList<YieldReport> Yields { get; init; } = [];
}

/// <summary>
/// One experiment yielding to another (<see cref="ExperimentPlacement.YieldsTo"/>): <paramref name="Experiment"/> runs in
/// none of its arms on a request also in an arm of <paramref name="To"/>, so it loses the traffic <paramref name="To"/>
/// runs on.
/// </summary>
/// <param name="Resolves">What would be checked between their arms if they could run together: the conflicting paths
/// (<c>PATCH_CONFLICT</c>) and the interactions (<c>INTERACTION_FLOW_COLUMN</c>, <c>INTERACTION_READ</c>), sorted; empty
/// when there is neither (<c>YIELD_UNUSED</c>).</param>
public sealed record YieldReport(string Experiment, string To, IReadOnlyList<string> Resolves);

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
