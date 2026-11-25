using System.Collections.Frozen;
using System.Collections.Immutable;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
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

    /// <summary>Whether both are the same assignment object (what <see cref="ArmYields.Resolve"/> returns when nothing yields).</summary>
    internal bool SameAs(in RequestArms other) => ReferenceEquals(_armByLayer, other._armByLayer);

    /// <summary>A copy of the arm per layer, to change (<see cref="ArmYields.Resolve"/>).</summary>
    internal int[] CopyArms() => _armByLayer is null ? [] : (int[])_armByLayer.Clone();
}

/// <summary>
/// An experiment layer (design 11.1): a request is in at most one arm per layer, so the arms of one layer never run
/// together, and arms of different layers may (every such combination is validated, design 11.5).
/// </summary>
/// <param name="Index">Layer index.</param>
/// <param name="Name">Layer name.</param>
public sealed record LayerInfo(int Index, string Name);

/// <summary>An experiment in the plan.</summary>
public sealed record ExperimentInfo(int Index, string Id, int LayerIndex, ImmutableArray<int> ArmIndices)
{
    /// <summary>
    /// The experiments (by index) this one yields to (a release's <c>yields_to</c>, <see cref="ArmYields"/>): on a request
    /// in an arm of one of them it runs in none of its arms. Ascending; empty for most.
    /// </summary>
    public ImmutableArray<int> YieldsTo { get; init; } = [];
}

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
    public required ImmutableArray<int> TouchingLayers { get; init; }

    /// <summary>Per touching layer: global arm index → local index (0 = the layer does not modify the node).</summary>
    public required ImmutableArray<FrozenDictionary<int, int>> LocalIndex { get; init; }

    /// <summary>Per touching layer: number of local indices (arms modifying the node + 1).</summary>
    public required ImmutableArray<int> Radix { get; init; }

    /// <summary>Mixed-radix indexed variants; deduplicated variants are shared.</summary>
    public required ImmutableArray<CompiledNode?> Variants { get; init; }

    /// <summary>Experiments whose site includes this position (design 12.6); assigned by the experiment compiler.</summary>
    public ImmutableArray<int> SiteExperiments { get; internal set; } = [];

    /// <summary>Selects this request's variant: one mixed-radix index computation (design 12.3).</summary>
    public CompiledNode? Select(in RequestArms arms) => Variants[IndexWith(arms, -1, -1)];

    /// <summary>
    /// The variant <paramref name="arm"/> (of <paramref name="layer"/>) has here together with the request's arms in the
    /// other layers: the node it would run in this request's place, which decides whether reaching this position triggers
    /// its experiment (design 12.6). Another layer may change the node's <c>when</c> or remove it.
    /// </summary>
    public CompiledNode? SelectWith(in RequestArms arms, int layer, int arm) => Variants[IndexWith(arms, layer, arm)];

    private int IndexWith(in RequestArms arms, int layer, int arm)
    {
        var index = 0;
        for (var i = 0; i < TouchingLayers.Length; i++)
        {
            var assigned = TouchingLayers[i] == layer ? arm : arms.ArmIn(TouchingLayers[i]);
            var local = assigned >= 0 && LocalIndex[i].TryGetValue(assigned, out var l) ? l : 0;
            index = (index * Radix[i]) + local;
        }

        return index;
    }
}

/// <summary>Baseline parameter values plus per-arm overrides (design 12.2).</summary>
public sealed class ParamTable
{
    public ParamTable(ImmutableArray<ScalarValue> baseline, ImmutableArray<ImmutableArray<(int Param, ScalarValue Value)>> overridesByArm)
    {
        Baseline = baseline;
        OverridesByArm = overridesByArm;
    }

    public ImmutableArray<ScalarValue> Baseline { get; }

    public ImmutableArray<ImmutableArray<(int Param, ScalarValue Value)>> OverridesByArm { get; }

    /// <summary>Copies the baseline and applies the overrides of every assigned arm.</summary>
    public ScalarValue[] Snapshot(in RequestArms arms)
    {
        var snapshot = Baseline.AsSpan().ToArray();
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

    /// <summary>The output columns in order. A column's type in results (<c>ResultColumns.Types</c>) and replay bundles is
    /// its expression's <see cref="PlanSchema.DeclaredType"/>: an output that is nothing but a reference keeps the
    /// declared type (an int32 column is int32, a float32 one prints as the floats it holds: <c>0.08</c>, not the double
    /// <c>0.07999999821186066</c>); a computed one has its promoted result type.</summary>
    public required ImmutableArray<(string Name, CompiledExpression Expression)> Columns { get; init; }

    /// <summary>
    /// The request-level values in order (<c>output.values</c>): each evaluated once when the result is built, after every
    /// node, and returned beside the items with its expression's <see cref="PlanSchema.DeclaredType"/> (a plain
    /// <c>req.x</c> has the request column's declared type).
    /// </summary>
    public ImmutableArray<(string Name, CompiledExpression Expression)> Values { get; init; } = [];

    /// <summary>The expressions of the output's columns, then of its values: for what looks at every expression it reads.</summary>
    public IEnumerable<CompiledExpression> Expressions()
    {
        foreach (var (_, expression) in Columns)
        {
            yield return expression;
        }

        foreach (var (_, expression) in Values)
        {
            yield return expression;
        }
    }

    /// <summary>The pinned versions the output's columns read (<c>col.x@node</c>), with the column that reads each.</summary>
    public ImmutableArray<(string Column, VersionRead Read)> VersionReads { get; init; } = [];

    /// <summary>
    /// Experiments overriding a parameter the output reads (<c>limit_param</c>, or a parameter in an output column or value);
    /// a parameter trigger site (design 12.6). Assigned by the experiment compiler.
    /// </summary>
    public ImmutableArray<int> ParamExperiments { get; internal set; } = [];
}

/// <summary>
/// The immutable unified execution plan (design 12): schema, feature plan, timeline of node variants, parameters and
/// experiment metadata. Compiled once per manifest in the background and swapped atomically.
/// </summary>
/// <remarks>
/// A plan is immutable once compiled: every concurrent request of it shares the one instance, and hosts inspect it
/// (<c>RecEngine.TryGetPipeline</c>, <c>EffectivePlan</c>, <c>PlanDiff</c>). So everything reachable from it is
/// read-only: collections are <see cref="ImmutableArray{T}"/> or read-only interfaces, and node and tap definitions are
/// <see cref="System.Text.Json.JsonElement"/>s. Code that wants to sort or edit a collection for display copies it first.
/// </remarks>
public sealed class ExecutionPlan
{
    public required string PipelineId { get; init; }

    /// <summary>Content id of the manifest this plan was built from (or a local identifier).</summary>
    public required string ManifestId { get; init; }

    public required PlanSchema Schema { get; init; }

    public required TableSchema TableSchema { get; init; }

    public required FeaturePlan Features { get; init; }

    public required ImmutableArray<TimelineSlot> Timeline { get; init; }

    /// <summary>Fallback-only nodes (<c>role: fallback</c>) by id; selected per request like timeline positions.</summary>
    public required IReadOnlyDictionary<string, TimelineSlot> Fallbacks { get; init; }

    /// <summary>The fallback variant of <paramref name="node"/> for this request, if any.</summary>
    public CompiledNode? SelectFallback(CompiledNode node, in RequestArms arms) =>
        node.FallbackNodeId is { } id && Fallbacks.TryGetValue(id, out var slot) ? slot.Select(arms) : null;

    /// <summary>Every compiled node variant (including fallbacks), indexed by <see cref="CompiledNode.Index"/>.</summary>
    public required ImmutableArray<CompiledNode> Nodes { get; init; }

    public required OutputPlan Output { get; init; }

    /// <summary>Funnel groups and log taps (design 8).</summary>
    public LogPlan Log { get; init; } = LogPlan.Empty;

    public required ParamTable Params { get; init; }

    public required ImmutableArray<LayerInfo> Layers { get; init; }

    public required ImmutableArray<ExperimentInfo> Experiments { get; init; }

    public required ImmutableArray<ArmInfo> Arms { get; init; }

    /// <summary>
    /// Which experiments yield to which (<see cref="ExperimentInfo.YieldsTo"/>): applied to every request's arms after its
    /// assignment, whatever its source, before anything selects by them.
    /// </summary>
    public ArmYields Yields { get; init; } = ArmYields.None;

    /// <summary>Context features needed by any variant, the output or log taps; prefetched at request start.</summary>
    public required ImmutableArray<int> ContextFeatures { get; init; }

    public IReadOnlyList<ConfigDiagnostic> Warnings { get; init; } = Array.Empty<ConfigDiagnostic>();

    public TimeSpan CompileTime { get; init; }

    /// <summary>
    /// The pipelines the plan's nodes call (<see cref="CompiledNode.Calls"/>), of every variant and fallback, sorted:
    /// which pipelines a request of this one may run.
    /// </summary>
    public ImmutableArray<string> Calls { get; init; } = [];

    /// <summary>The inputs the pipeline accepts (<c>request_schema</c>) and the rules a request's inputs follow.</summary>
    public required RequestSchema RequestSchema { get; init; }

    /// <summary>Metric tags of the plan's nodes, built once per plan (design 13.1 rule 5).</summary>
    internal Execution.PlanTelemetry Telemetry =>
        _telemetry ?? Interlocked.CompareExchange(ref _telemetry, Execution.PlanTelemetry.Create(this), null) ?? _telemetry!;

    private Execution.PlanTelemetry? _telemetry;

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

    /// <summary>
    /// Builds <see cref="RequestArms"/> from <c>experiment/arm</c> ids strictly: an unknown id or two arms of one layer
    /// throw. For forced arms, replays and tools, which must run exactly what they name; the arms an experimentation
    /// platform sends are resolved leniently instead (<c>ExternalArms</c>), which ignores what it cannot apply.
    /// </summary>
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

    /// <summary>
    /// Why <paramref name="arms"/> never run together as given (one's experiment yields to another's, <see cref="Yields"/>:
    /// a request in them runs without the arm that yields), or null when they do. For tools that show or compare a
    /// combination, which must not show one that never runs.
    /// </summary>
    public string? NeverRunTogether(in RequestArms arms)
    {
        for (var layer = 0; layer < arms.LayerCount; layer++)
        {
            var arm = arms.ArmIn(layer);
            if (arm >= 0 && Yields.YieldsTo(arm, arms) is var to and >= 0)
            {
                return $"'{Experiments[Arms[arm].ExperimentIndex].Id}' yields to '{Experiments[to].Id}': a request in both runs without '{Arms[arm].Id}'";
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>experiment/arm</c> ids of a request's arms, in layer order: what a request reports as assigned
    /// (<c>ExperimentRecord.Assigned</c>), whether its pipeline ran or not.
    /// </summary>
    public List<string> ArmIds(in RequestArms arms)
    {
        var ids = new List<string>(arms.LayerCount);
        for (var layer = 0; layer < arms.LayerCount; layer++)
        {
            var arm = arms.ArmIn(layer);
            if (arm >= 0)
            {
                ids.Add(Arms[arm].Id);
            }
        }

        return ids;
    }
}
