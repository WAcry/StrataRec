using System.Collections.Immutable;
using System.Text.Json;
using StrataRec.Core.Types;
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
/// A name a node, tap or the output binds (<see cref="CompiledNode.Requires"/>): a column, a parameter leaf or a context
/// field, by its id in the plan schema.
/// </summary>
public readonly record struct NameRef(SymbolKind Kind, int Id);

/// <summary>
/// A column a node's write declares where nothing else has (<see cref="CompiledNode.Declares"/>), with the type it
/// declares it as: the unified plan's schema may hold an arm's declaration instead, which a combination without that arm
/// does not have.
/// </summary>
/// <param name="Logical">The column's logical id.</param>
/// <param name="Type">The type the write declares it as.</param>
public readonly record struct ColumnDeclaration(int Logical, ColumnType Type);

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

    /// <summary>
    /// The fully merged definition this variant was compiled from (for explain-plan and dedup). A read-only element, since
    /// every request and host shares it; <c>JsonObject.Create(Definition)</c> gives a tree of one's own to edit.
    /// </summary>
    public required JsonElement Definition { get; init; }

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

    /// <summary>Time left to the fallback; null leaves the fallback's own <see cref="TimeoutMs"/> (design 5.7).</summary>
    public int? FallbackReserveMs { get; init; }

    /// <summary>True when the node ends a fetch segment (design 5.4).</summary>
    public bool FetchBarrier { get; init; }

    public CompactMode Compact { get; init; } = CompactMode.Auto;

    /// <summary>Logical columns read (features, outputs, entity id).</summary>
    public required ImmutableArray<int> ReadColumns { get; init; }

    /// <summary>
    /// The columns of <see cref="ReadColumns"/> read at their current version, not only pinned (<c>col.x@node</c>): what
    /// a writer added before the node changes for it (design 11.5).
    /// </summary>
    public ImmutableArray<int> CurrentReads { get; init; } = [];

    /// <summary>
    /// Feature indices read directly (expanded to dependencies by the fetcher), including the context features the operator
    /// waits for itself (<see cref="Operators.OperatorCompileContext.CompileAwaitedExpression"/>): what the node reads, for
    /// prefetching, data-flow checks, footprints and taps.
    /// </summary>
    public required ImmutableArray<int> ReadFeatures { get; init; }

    /// <summary>
    /// The features filled before the node runs: <see cref="ReadFeatures"/> but those only awaited expressions read, which
    /// the operator waits for call by call while it runs (a recall source's query, design 5.4).
    /// </summary>
    public required ImmutableArray<int> FilledFeatures { get; init; }

    /// <summary>Non-nullable output columns read through bindings; must be produced before the node runs (design 11.5 step 4).</summary>
    public required ImmutableArray<int> RequiredOutputs { get; init; }

    public required ImmutableArray<int> ReadParams { get; init; }

    /// <summary>Pinned versions the node reads (<c>col.x@node</c>), other than its own.</summary>
    public ImmutableArray<VersionRead> VersionReads { get; init; } = [];

    public required ImmutableArray<OutputColumn> Outputs { get; init; }

    /// <summary>
    /// The names the node binds that it has not declared itself before binding them: every column, parameter and context
    /// field it resolves (in a branch the optimizer drops too, and a column it writes but cannot declare), but not a
    /// column it reads after a write of its own that could declare it (GroupAggregate@1 writes <c>a</c>, then reads it for
    /// <c>b</c>). A compile of one arm combination alone knows a name only once it is declared there: by the baseline, by an
    /// arm in it, or (a column without a declaration) by a node compiled before. The order of reads and writes inside one
    /// compile is not in <see cref="ReadColumns"/> and <see cref="Outputs"/>, so it is recorded (reviews 50, 51).
    /// </summary>
    public ImmutableArray<NameRef> Requires { get; init; } = [];

    /// <summary>The columns the node writes with a type of its own (an implicit output), so its write declares an undeclared one.</summary>
    public ImmutableArray<ColumnDeclaration> Declares { get; init; } = [];

    /// <summary>
    /// Experiments whose arms override a parameter this node reads (trigger sites, design 12.6). Assigned by the experiment
    /// compiler once every variant is compiled, before the plan is built.
    /// </summary>
    public ImmutableArray<int> ParamExperiments { get; internal set; } = [];

    public bool HasRowEffects => (Effects & (Effects.RowSource | Effects.RowFilter | Effects.RowOrder)) != 0;

    public bool IsRowSetNode => (Effects & (Effects.RowSource | Effects.RowFilter)) != 0;

    public override string ToString() => $"{NodeId} ({Descriptor.Id})";
}
