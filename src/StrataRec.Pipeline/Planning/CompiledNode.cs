using System.Text.Json.Nodes;
using StrataRec.Expressions;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Pipeline.Planning;

/// <summary>What happens when a node fails (design 5.6).</summary>
public enum FailurePolicy : byte
{
    FailRequest = 1,
    Skip,
    Fallback,
}

/// <summary>When the engine compacts the table after a row-set node (design 4.5).</summary>
public enum CompactMode : byte
{
    Auto = 1,
    Always,
    Never,
}

/// <summary>
/// A read of one node's own version of a column, <c>col.x@node</c> (design 4.6). Recorded on each node and tap variant
/// that reads it, so the data-flow check judges it against the nodes of every arm combination the reader runs in, as a
/// compile of that combination alone would, and not against nodes that only other combinations have.
/// </summary>
/// <param name="Node">The pinned node's id.</param>
/// <param name="Logical">The column's logical id.</param>
public readonly record struct VersionRead(string Node, int Logical);

/// <summary>
/// One compiled variant of a node: the operator instance plus everything the scheduler needs, all resolved to integer
/// ids at load time (design 3.3, 12.3). Immutable and shared by all requests of a plan.
/// </summary>
public sealed class CompiledNode
{
    public required string NodeId { get; init; }

    /// <summary>Plan-wide index, used for lineage (<c>column_producers</c>) and outcomes.</summary>
    public required int Index { get; init; }

    /// <summary>Where the node's definition lives, e.g. <c>/nodes/fusion</c>.</summary>
    public required string Pointer { get; init; }

    /// <summary>The fully merged definition this variant was compiled from (for explain-plan and dedup).</summary>
    public required JsonObject Definition { get; init; }

    /// <summary>RFC 8785 hash of <see cref="Definition"/>; equal hashes share a variant.</summary>
    public required string DefinitionHash { get; init; }

    public required OperatorDescriptor Descriptor { get; init; }

    public required Operator Operator { get; init; }

    public Effects Effects => Descriptor.Effects;

    /// <summary>Timeline order; null for fallback-only nodes.</summary>
    public int? Order { get; init; }

    public bool IsFallbackRole { get; init; }

    public CompiledExpression? When { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MinRemainingMs { get; init; }

    public FailurePolicy OnFailure { get; init; } = FailurePolicy.FailRequest;

    /// <summary>Fallback node id; its variant is selected per request from <see cref="ExecutionPlan.Fallbacks"/>.</summary>
    public string? FallbackNodeId { get; init; }

    public int FallbackReserveMs { get; init; }

    /// <summary>True when the node ends a fetch segment (design 5.4).</summary>
    public bool FetchBarrier { get; init; }

    public CompactMode Compact { get; init; } = CompactMode.Auto;

    /// <summary>Logical columns read (features, outputs, entity id).</summary>
    public required int[] ReadColumns { get; init; }

    /// <summary>Feature indices read directly (expanded to dependencies by the fetcher).</summary>
    public required int[] ReadFeatures { get; init; }

    /// <summary>Non-nullable output columns read through bindings; must be produced before the node runs (design 11.5 step 4).</summary>
    public required int[] RequiredOutputs { get; init; }

    public required int[] ReadParams { get; init; }

    /// <summary>Pinned versions the node reads (<c>col.x@node</c>), other than its own.</summary>
    public VersionRead[] VersionReads { get; init; } = Array.Empty<VersionRead>();

    public required OutputColumn[] Outputs { get; init; }

    /// <summary>Experiments whose arms override a parameter this node reads (trigger sites, design 12.6).</summary>
    public int[] ParamExperiments { get; set; } = Array.Empty<int>();

    public bool HasRowEffects => (Effects & (Effects.RowSource | Effects.RowFilter | Effects.RowOrder)) != 0;

    public bool IsRowSetNode => (Effects & (Effects.RowSource | Effects.RowFilter)) != 0;

    public override string ToString() => $"{NodeId} ({Descriptor.Id})";
}
