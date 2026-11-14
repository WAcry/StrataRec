using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Operators;

/// <summary>What a node may do to the candidate table (design 5.2). An operator declares a set.</summary>
[Flags]
public enum Effects : byte
{
    None = 0,

    /// <summary>Appends rows and writes columns for them (recall, exploration injection).</summary>
    RowSource = 1,

    /// <summary>Drops rows; the relative order of survivors is unchanged (filters, truncation).</summary>
    RowFilter = 2,

    /// <summary>Changes row order (sorting, MMR, sliding-window quota).</summary>
    RowOrder = 4,

    /// <summary>Writes new columns or new versions of flow columns (scoring, model inference).</summary>
    ColumnWriter = 8,
}

/// <summary>Static description of an operator type and version (design 5.2, 10.4).</summary>
/// <param name="Name">Operator name, e.g. <c>TopK</c>.</param>
/// <param name="Version">Major version; breaking config changes bump it (design 10.4).</param>
/// <param name="Effects">Effects the operator may exercise; enforced at runtime.</param>
/// <param name="ConfigType">C# type of the node's <c>config</c>; the single source of its JSON Schema (design 9.1).</param>
/// <param name="Description">One-line description for docs and tooling.</param>
public sealed record OperatorDescriptor(string Name, int Version, Effects Effects, Type ConfigType, string Description)
{
    /// <summary><c>Name@Version</c>, as written in <c>"op"</c>.</summary>
    public string Id => $"{Name}@{Version}";

    /// <summary>True when the operator is CPU-heavy enough to run off the request thread (design 5.4).</summary>
    public bool ComputeIntensive { get; init; }
}

/// <summary>A candidate column read by an operator.</summary>
/// <param name="LogicalId">Logical column id.</param>
/// <param name="FixedSlot">Physical slot for features/entity id/<c>@node</c> versions; -1 to resolve the committed version.</param>
/// <param name="Type">Declared type.</param>
/// <param name="Name">Column name without the <c>col.</c> prefix.</param>
public readonly record struct ColumnRef(int LogicalId, int FixedSlot, ColumnType Type, string Name);

/// <summary>A column an operator writes: a slot owned by its node, committed on success (design 4.6).</summary>
public readonly record struct OutputColumn(int LogicalId, int Slot, ColumnType Type, string Name);

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
    /// Maximum rows a <see cref="Effects.RowSource"/> operator may append for a request with these parameters
    /// (design 4.8: row limits bound per-request memory). Other operators return 0.
    /// </summary>
    public virtual int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => 0;

    /// <summary>Named model inputs (input name → column expression), for log taps' <c>columns_from</c> (design 6.9).</summary>
    public virtual IReadOnlyDictionary<string, string> NamedInputs => EmptyInputs;

    /// <summary>The model a model-calling operator scores with; its node's fallbacks are counted per model (design 13.1).</summary>
    internal virtual IoTarget? ModelTarget => null;

    private static readonly IReadOnlyDictionary<string, string> EmptyInputs = new Dictionary<string, string>();
}

/// <summary>Compiles node configs of one operator type into <see cref="Operator"/> instances.</summary>
public interface IOperatorFactory
{
    OperatorDescriptor Descriptor { get; }

    /// <summary>
    /// Validates <see cref="OperatorCompileContext.Config"/>, resolves every column, parameter, expression and client
    /// through <paramref name="context"/> (which records reads and writes) and returns the compiled operator. Report
    /// problems with <see cref="OperatorCompileContext.Error"/> and return null when the node cannot be compiled.
    /// </summary>
    Operator? Compile(OperatorCompileContext context);
}

/// <summary>Operators available in this binary, keyed by <c>Name@Version</c> (design 10.4 capabilities).</summary>
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

    /// <summary>A registry with the core standard operator library (design 16.1).</summary>
    public static OperatorRegistry CreateDefault() => new OperatorRegistry()
        .Add(new Standard.MultiSourceRecallFactory())
        .Add(new Standard.ExpressionFilterFactory())
        .Add(new Standard.ExpressionScorerFactory())
        .Add(new Standard.CompositeMultiplierScorerFactory())
        .Add(new Standard.TopKFactory())
        .Add(new Standard.SlidingWindowQuotaFactory())
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
