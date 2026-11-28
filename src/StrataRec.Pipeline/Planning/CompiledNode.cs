using System.Collections.Immutable;
using System.Text.Json;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Pipeline.Planning;

/// <summary>What happens when a node fails.</summary>
public enum FailurePolicy : byte
{
    FailRequest = 1,
    Skip,
    Fallback,
}

/// <summary>When the engine compacts the table after a row-set node.</summary>
public enum CompactMode : byte
{
    Auto = 1,
    Always,
    Never,
}

/// <summary>
/// A read of one node's own version of a column, <c>col.x@node</c>.
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
/// ids at load time. Immutable and shared by all requests of a plan.
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

    /// <summary>Time left to the fallback; null leaves the fallback's own <see cref="TimeoutMs"/>.</summary>
    public int? FallbackReserveMs { get; init; }

    /// <summary>True when the node ends a fetch segment.</summary>
    public bool FetchBarrier { get; init; }

    public CompactMode Compact { get; init; } = CompactMode.Auto;

    /// <summary>Logical columns read (features, outputs, entity id).</summary>
    public required ImmutableArray<int> ReadColumns { get; init; }

    /// <summary>
    /// The columns of <see cref="ReadColumns"/> read at their current version, not only pinned (<c>col.x@node</c>): what
    /// a writer added before the node changes for it.
    /// </summary>
    public ImmutableArray<int> CurrentReads { get; init; } = [];

    /// <summary>
    /// Feature indices read directly, including the context features the operator waits for itself
    /// (<see cref="Operators.OperatorCompileContext.CompileAwaitedExpression"/>).
    /// </summary>
    public required ImmutableArray<int> ReadFeatures { get; init; }

    /// <summary>
    /// The features filled before the node runs: <see cref="ReadFeatures"/> but those only awaited expressions read, which
    /// the operator waits for call by call while it runs (a recall source's query).
    /// </summary>
    public required ImmutableArray<int> FilledFeatures { get; init; }

    /// <summary>Non-nullable output columns read through bindings; must be produced before the node runs.</summary>
    public required ImmutableArray<int> RequiredOutputs { get; init; }

    public required ImmutableArray<int> ReadParams { get; init; }

    /// <summary>Pinned versions the node reads (<c>col.x@node</c>), other than its own.</summary>
    public ImmutableArray<VersionRead> VersionReads { get; init; } = [];

    public required ImmutableArray<OutputColumn> Outputs { get; init; }

    /// <summary>The request columns the node writes (<see cref="Effects.RequestWriter"/>), published when it commits.</summary>
    public ImmutableArray<RequestOutput> RequestOutputs { get; init; } = [];

    /// <summary>The pipelines the node's operator declared it calls (<see cref="OperatorCompileContext.TryCallPipeline"/>).</summary>
    public ImmutableArray<PipelineCall> Calls { get; init; } = [];

    /// <summary>
    /// The request columns the node reads (<c>req.*</c>), by <see cref="ContextField.Id"/>, in its config and its
    /// <c>when</c>: each has its value as of the position (a later node's write is not yet there), and a non-nullable one
    /// must have a producer that cannot be skipped before it.
    /// </summary>
    public ImmutableArray<int> RequestReads { get; init; } = [];

    /// <summary>
    /// The node's <c>when</c> reads a request column: it is evaluated at the node's position, when what the nodes
    /// before it wrote is there, not ahead of it with the other conditions of its segment.
    /// </summary>
    public bool WhenReadsRequest { get; init; }

    /// <summary>
    /// The names the node binds: every column, parameter and context field it resolves (in a branch the optimizer drops
    /// too, and a column it writes but cannot declare). A compile of one arm combination alone knows a name only once it
    /// is declared there: by the baseline, by an arm in it, or (a column without a declaration) by a node compiled before.
    /// A write of the node itself does not declare what it reads: it reads the version committed before it ran.
    /// </summary>
    public ImmutableArray<NameRef> Requires { get; init; } = [];

    /// <summary>The columns the node writes with a type of its own (an implicit output), so its write declares an undeclared one.</summary>
    public ImmutableArray<ColumnDeclaration> Declares { get; init; } = [];

    /// <summary>
    /// Experiments whose arms override a parameter this node reads (trigger sites).
    /// </summary>
    public ImmutableArray<int> ParamExperiments { get; internal set; } = [];

    public bool HasRowEffects => (Effects & (Effects.RowSource | Effects.RowFilter | Effects.RowOrder)) != 0;

    public bool IsRowSetNode => (Effects & (Effects.RowSource | Effects.RowFilter)) != 0;

    public override string ToString() => $"{NodeId} ({Descriptor.Id})";
}
