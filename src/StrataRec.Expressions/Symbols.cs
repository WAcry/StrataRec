using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Expressions;

/// <summary>What a name in an expression refers to (design 7.2).</summary>
public enum SymbolKind : byte
{
    /// <summary><c>col.*</c>: a candidate column. Resolved per request through <see cref="Core.Tables.OutputBindings"/>
    /// unless <see cref="Symbol.FixedSlot"/> is set.</summary>
    Column = 1,

    /// <summary><c>ctx.*</c>: a request-level value (request input, built-in, or context-entity feature).</summary>
    Context,

    /// <summary><c>param.*</c>: a parameter leaf from the request's parameter snapshot.</summary>
    Param,
}

/// <summary>A resolved name. Produced by <see cref="IExpressionScope"/> at compile time.</summary>
/// <param name="Kind">Namespace of the symbol.</param>
/// <param name="Name">Fully qualified name as written, e.g. <c>col.score.final@fusion</c>.</param>
/// <param name="Type">Declared (storage) type including nullability. The evaluator promotes int32/float32.</param>
/// <param name="Id">Column: logical column id. Context: index into <see cref="EvalContext.Context"/>.
/// Param: index into <see cref="EvalContext.Params"/>.</param>
/// <param name="FixedSlot">Column only: physical slot to read directly (features, <c>col.entity_id</c>, and
/// <c>@node</c> version references); -1 to resolve the committed slot through the request's output bindings.</param>
public readonly record struct Symbol(SymbolKind Kind, string Name, ColumnType Type, int Id, int FixedSlot = -1);

/// <summary>An immutable lookup table usable through <c>lookup(table_name, key, default)</c> (design 7.6).</summary>
public interface ILookupTable
{
    string Name { get; }

    /// <summary>Promoted key type (int64 or string).</summary>
    ColumnType KeyType { get; }

    /// <summary>Promoted, non-nullable value type (int64, float64, bool or string).</summary>
    ColumnType ValueType { get; }

    bool TryGet(in ScalarValue key, out ScalarValue value);
}

/// <summary>Compile-time name resolution for one expression site.</summary>
public interface IExpressionScope
{
    /// <summary>
    /// Resolves a dotted name such as <c>col.item.clicks_7d</c>, <c>ctx.user.tenure_days</c>, <c>param.w_click</c>,
    /// <c>param.recall_quotas.cf</c> or <c>col.score.final@fusion</c>.
    /// </summary>
    bool TryResolve(string name, out Symbol symbol);

    /// <summary>All names resolvable in this scope, used for "did you mean" suggestions.</summary>
    IEnumerable<string> KnownNames { get; }

    bool TryGetLookupTable(string name, out ILookupTable table);
}

/// <summary>A simple dictionary-backed scope, convenient for tests, tools and small embeddings.</summary>
public sealed class DictionaryScope : IExpressionScope
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ILookupTable> _tables = new(StringComparer.Ordinal);

    public IEnumerable<string> KnownNames => _symbols.Keys;

    public DictionaryScope Add(Symbol symbol)
    {
        _symbols[symbol.Name] = symbol;
        return this;
    }

    public DictionaryScope AddTable(ILookupTable table)
    {
        _tables[table.Name] = table;
        return this;
    }

    public bool TryResolve(string name, out Symbol symbol) => _symbols.TryGetValue(name, out symbol);

    public bool TryGetLookupTable(string name, out ILookupTable table) => _tables.TryGetValue(name, out table!);
}
