using StrataRec.Artifacts.Model;
using StrataRec.Core.Tables;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// The arm a request is assigned in each layer (design 11.1). Index i is the layer index; -1 means no arm.
/// </summary>
public readonly struct RequestArms
{
    private readonly int[]? _armByLayer;

    public RequestArms(int[] armByLayer) => _armByLayer = armByLayer;

    public static RequestArms None => default;

    public int LayerCount => _armByLayer?.Length ?? 0;

    /// <summary>Global arm index assigned in <paramref name="layer"/>, or -1.</summary>
    public int ArmIn(int layer) => _armByLayer is null || layer >= _armByLayer.Length ? -1 : _armByLayer[layer];
}

/// <summary>An experiment layer: arms in one layer are mutually exclusive (design 11.1).</summary>
public sealed record LayerInfo(int Index, string Name);

/// <summary>An experiment in the plan.</summary>
public sealed record ExperimentInfo(int Index, string Id, int LayerIndex, int[] ArmIndices);

/// <summary>An experiment arm in the plan; <see cref="Id"/> is <c>experiment/arm</c>.</summary>
public sealed record ArmInfo(int Index, string Id, string ArmName, int ExperimentIndex, int LayerIndex);

/// <summary>
/// A position on the timeline (design 12.4): one node id at one order, with a variant per combination of arms in the
/// layers that touch it. A null variant means the node does not exist for that combination.
/// </summary>
public sealed class TimelineSlot
{
    public required int Order { get; init; }

    public required string NodeId { get; init; }

    /// <summary>Layers with at least one arm that modifies this node (L(n), design 12.3).</summary>
    public required int[] TouchingLayers { get; init; }

    /// <summary>Per touching layer: global arm index → local index (0 = the layer does not modify the node).</summary>
    public required Dictionary<int, int>[] LocalIndex { get; init; }

    /// <summary>Per touching layer: number of local indices (arms modifying the node + 1).</summary>
    public required int[] Radix { get; init; }

    /// <summary>Mixed-radix indexed variants; deduplicated variants are shared.</summary>
    public required CompiledNode?[] Variants { get; init; }

    /// <summary>Experiments whose site includes this position (design 12.6).</summary>
    public int[] SiteExperiments { get; set; } = Array.Empty<int>();

    /// <summary>Per site experiment: OR of its arms' <c>when</c> at this position; null means always.</summary>
    public CompiledExpression?[] SiteConditions { get; set; } = Array.Empty<CompiledExpression?>();

    /// <summary>Selects this request's variant: one mixed-radix index computation (design 12.3).</summary>
    public CompiledNode? Select(in RequestArms arms)
    {
        var index = 0;
        for (var i = 0; i < TouchingLayers.Length; i++)
        {
            var arm = arms.ArmIn(TouchingLayers[i]);
            var local = arm >= 0 && LocalIndex[i].TryGetValue(arm, out var l) ? l : 0;
            index = (index * Radix[i]) + local;
        }

        return Variants[index];
    }
}

/// <summary>Baseline parameter values plus per-arm overrides (design 12.2).</summary>
public sealed class ParamTable
{
    public ParamTable(ScalarValue[] baseline, (int Param, ScalarValue Value)[][] overridesByArm)
    {
        Baseline = baseline;
        OverridesByArm = overridesByArm;
    }

    public ScalarValue[] Baseline { get; }

    public (int Param, ScalarValue Value)[][] OverridesByArm { get; }

    /// <summary>Copies the baseline and applies the overrides of every assigned arm.</summary>
    public ScalarValue[] Snapshot(in RequestArms arms)
    {
        var snapshot = (ScalarValue[])Baseline.Clone();
        for (var layer = 0; layer < arms.LayerCount; layer++)
        {
            var arm = arms.ArmIn(layer);
            if (arm < 0 || arm >= OverridesByArm.Length)
            {
                continue;
            }

            foreach (var (param, value) in OverridesByArm[arm])
            {
                snapshot[param] = value;
            }
        }

        return snapshot;
    }
}

/// <summary>What the pipeline returns (design 3.4).</summary>
public sealed class OutputPlan
{
    public required ParamRef Limit { get; init; }

    public required (string Name, CompiledExpression Expression)[] Columns { get; init; }

    /// <summary>
    /// Experiments overriding a parameter the output reads (<c>limit_param</c> or a parameter in an output column);
    /// a parameter trigger site (design 12.6).
    /// </summary>
    public int[] ParamExperiments { get; set; } = Array.Empty<int>();
}

/// <summary>
/// The immutable unified execution plan (design 12): schema, feature plan, timeline of node variants, parameters and
/// experiment metadata. Compiled once per manifest in the background and swapped atomically.
/// </summary>
public sealed class ExecutionPlan
{
    public required string PipelineId { get; init; }

    /// <summary>Content id of the manifest this plan was built from (or a local identifier).</summary>
    public required string ManifestId { get; init; }

    public required PlanSchema Schema { get; init; }

    public required TableSchema TableSchema { get; init; }

    public required FeaturePlan Features { get; init; }

    public required TimelineSlot[] Timeline { get; init; }

    /// <summary>Fallback-only nodes (<c>role: fallback</c>) by id; selected per request like timeline positions.</summary>
    public required IReadOnlyDictionary<string, TimelineSlot> Fallbacks { get; init; }

    /// <summary>The fallback variant of <paramref name="node"/> for this request, if any.</summary>
    public CompiledNode? SelectFallback(CompiledNode node, in RequestArms arms) =>
        node.FallbackNodeId is { } id && Fallbacks.TryGetValue(id, out var slot) ? slot.Select(arms) : null;

    /// <summary>Every compiled node variant (including fallbacks), indexed by <see cref="CompiledNode.Index"/>.</summary>
    public required CompiledNode[] Nodes { get; init; }

    public required OutputPlan Output { get; init; }

    public required ParamTable Params { get; init; }

    public required LayerInfo[] Layers { get; init; }

    public required ExperimentInfo[] Experiments { get; init; }

    public required ArmInfo[] Arms { get; init; }

    /// <summary>Context features needed by any variant, the output or log taps; prefetched at request start.</summary>
    public required int[] ContextFeatures { get; init; }

    public IReadOnlyList<ConfigDiagnostic> Warnings { get; init; } = Array.Empty<ConfigDiagnostic>();

    public TimeSpan CompileTime { get; init; }

    /// <summary>Request inputs by name (context id and type) for validation.</summary>
    public required IReadOnlyDictionary<string, ContextField> RequestInputs { get; init; }

    /// <summary>Arm index by <c>experiment/arm</c> id.</summary>
    public bool TryGetArm(string id, out ArmInfo arm)
    {
        foreach (var a in Arms)
        {
            if (string.Equals(a.Id, id, StringComparison.Ordinal))
            {
                arm = a;
                return true;
            }
        }

        arm = null!;
        return false;
    }

    /// <summary>Builds <see cref="RequestArms"/> from <c>experiment/arm</c> ids (forced arms, external assignment).</summary>
    public RequestArms ArmsFromIds(IEnumerable<string> armIds)
    {
        var byLayer = new int[Layers.Length];
        Array.Fill(byLayer, -1);
        foreach (var id in armIds)
        {
            if (!TryGetArm(id, out var arm))
            {
                throw new ArgumentException($"Unknown arm '{id}'. Arms are named 'experiment/arm'.", nameof(armIds));
            }

            if (byLayer[arm.LayerIndex] >= 0 && byLayer[arm.LayerIndex] != arm.Index)
            {
                throw new ArgumentException($"Arms '{Arms[byLayer[arm.LayerIndex]].Id}' and '{id}' are in the same layer and cannot both be assigned.", nameof(armIds));
            }

            byLayer[arm.LayerIndex] = arm.Index;
        }

        return new RequestArms(byLayer);
    }
}
