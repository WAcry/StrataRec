using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Operators;

/// <summary>What a node may do to the candidate table and the request's columns. An operator declares a set.</summary>
[Flags]
public enum Effects : byte
{
    None = 0,

    /// <summary>Appends rows and writes columns for them.</summary>
    RowSource = 1,

    /// <summary>Drops rows; the relative order of survivors is unchanged.</summary>
    RowFilter = 2,

    RowOrder = 4,

    /// <summary>Writes new columns or new versions of flow columns.</summary>
    ColumnWriter = 8,

    /// <summary>Writes request columns, one value per request (<c>req.*</c>).</summary>
    RequestWriter = 16,
}

/// <param name="Version">Major version; breaking config changes bump it.</param>
/// <param name="Effects">
/// Effects the operator may exercise, enforced: row changes at run time by <see cref="OperatorContext"/>, column writes at
/// compile time by <see cref="OperatorCompileContext.TryWriteColumn"/> (<see cref="Effects.ColumnWriter"/>, or
/// <see cref="Effects.RowSource"/> for the columns of the rows it appends), request column writes at compile time by
/// <see cref="OperatorCompileContext.TryWriteRequestColumn"/> (<see cref="Effects.RequestWriter"/>).
/// </param>
/// <param name="ConfigType">C# type of the node's <c>config</c>; the single source of its JSON Schema.</param>
/// <param name="Description">One-line description for docs and tooling.</param>
public sealed record OperatorDescriptor(string Name, int Version, Effects Effects, Type ConfigType, string Description)
{
    /// <summary><c>Name@Version</c>, as written in <c>"op"</c>.</summary>
    public string Id => $"{Name}@{Version}";
}

/// <summary>A candidate column read by an operator.</summary>
/// <param name="FixedSlot">Physical slot for features/entity id/<c>@node</c> versions; -1 to resolve the committed version.</param>
/// <param name="Type">Declared type.</param>
/// <param name="Name">Column name without the <c>col.</c> prefix.</param>
public readonly record struct ColumnRef(int LogicalId, int FixedSlot, ColumnType Type, string Name);

/// <summary>A column an operator writes: a slot owned by its node, committed on success.</summary>
public readonly record struct OutputColumn(int LogicalId, int Slot, ColumnType Type, string Name);

/// <summary>
/// A request column an operator writes (<see cref="OperatorContext.SetRequestValue"/>): staged while the node runs,
/// published to <c>req.&lt;name&gt;</c> when it commits, dropped when it fails.
/// </summary>
/// <param name="ContextId">The request-level value it is (<see cref="Planning.ContextField.Id"/>).</param>
/// <param name="Index">Its position among the request columns the node writes, in the order it resolved them. A handle
/// is only the node's way to name what it resolved: the value is checked against that, not against the handle's
/// <paramref name="Type"/>.</param>
/// <param name="Type">Declared type.</param>
/// <param name="Name">Name without the <c>req.</c> prefix.</param>
public readonly record struct RequestOutput(int ContextId, int Index, ColumnType Type, string Name);

/// <summary>A parameter leaf an operator reads from the request's snapshot.</summary>
public readonly record struct ParamRef(int Id, string Name, ColumnType Type);

/// <summary>
/// A compiled, immutable, thread-safe operator instance: one per node variant, shared by all requests.
/// </summary>
public abstract class Operator
{
    /// <summary>Runs the operator for one request. Throwing (or returning after <see cref="OperatorContext.Fail"/>) fails the node.</summary>
    public abstract ValueTask ExecuteAsync(OperatorContext context);

    /// <summary>
    /// Maximum rows a <see cref="Effects.RowSource"/> operator may append for a request with these parameters. Other
    /// operators return 0.
    /// </summary>
    public virtual int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => 0;

    /// <summary>
    /// The named inputs an operator sends (input name → the expression it evaluates for it), for log taps'
    /// <c>columns_from</c>: a tap's <c>columns_from</c> expands to these expressions, evaluated where the tap is, so it
    /// logs what they read at the tap's point, not a copy of what the calls were sent. The model operators give theirs;
    /// a custom operator that maps expressions to a service's inputs may too.
    /// </summary>
    public virtual IReadOnlyDictionary<string, string> NamedInputs => EmptyInputs;

    private static readonly IReadOnlyDictionary<string, string> EmptyInputs = new Dictionary<string, string>();
}

/// <summary>Compiles node configs of one operator type into <see cref="Operator"/> instances.</summary>
public interface IOperatorFactory
{
    OperatorDescriptor Descriptor { get; }

    /// <summary>
    /// Validates <see cref="OperatorCompileContext.Config"/>, resolves every column, parameter, expression and client
    /// through <paramref name="context"/> (which records reads and writes) and returns the compiled operator. Report
    /// problems with <see cref="OperatorCompileContext.Error(string, string, string, string?)"/> and return null when the node cannot be compiled.
    /// </summary>
    Operator? Compile(OperatorCompileContext context);
}

/// <summary>Operators available in this binary, keyed by <c>Name@Version</c>.</summary>
public sealed class OperatorRegistry
{
    private readonly Dictionary<string, IOperatorFactory> _factories = new(StringComparer.Ordinal);

    public IEnumerable<IOperatorFactory> Factories
    {
        get
        {
            var list = new List<IOperatorFactory>(_factories.Values);
            list.Sort(static (a, b) => string.CompareOrdinal(a.Descriptor.Id, b.Descriptor.Id));
            return list;
        }
    }

    public IEnumerable<string> Ids
    {
        get
        {
            var list = new List<string>(_factories.Keys);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }

    /// <summary>A registry with the core standard operator library.</summary>
    public static OperatorRegistry CreateDefault() => new OperatorRegistry()
        .Add(new Standard.MultiSourceRecallFactory())
        .Add(new Standard.PipelineSourceFactory())
        .Add(new Standard.PipelineColumnsFactory())
        .Add(new Standard.ExpressionFilterFactory())
        .Add(new Standard.ExpressionScorerFactory())
        .Add(new Standard.CompositeMultiplierScorerFactory())
        .Add(new Standard.TopKFactory())
        .Add(new Standard.SlidingWindowQuotaFactory())
        .Add(new Standard.GroupAggregateFactory())
        .Add(new Standard.RequestAggregateFactory())
        .Add(new Standard.ModelScorerFactory())
        .Add(new Standard.PairwiseRankerFactory())
        .Add(new Standard.ListwiseRerankerFactory())
        .Add(new Standard.MmrDiversifierFactory());

    public OperatorRegistry Add(IOperatorFactory factory)
    {
        _factories[factory.Descriptor.Id] = factory;
        return this;
    }

    public bool TryGet(string id, out IOperatorFactory factory) => _factories.TryGetValue(id, out factory!);
}
