using StrataRec.Core.Tables;
using StrataRec.Core.Values;

namespace StrataRec.Expressions;

/// <summary>Runtime inputs of an expression evaluation within one request.</summary>
/// <remarks>
/// <para>Opaque outside the expression engine: an operator takes it from its context and passes it to
/// <see cref="CompiledExpression"/>, and cannot reach the table or the request's arrays through it (it changes the table
/// only through the context; see <see cref="CandidateTableView"/>).</para>
/// <para><see cref="Table"/> and <see cref="Bindings"/> are required for row-level expressions and may be null for
/// request-level ones (<c>when</c>, context-only derived features, funnel keys).</para>
/// <para>An unresolved slot (<c>-1</c>) or a slot that was never written reads as missing.</para>
/// </remarks>
public readonly struct EvalContext
{
    public EvalContext(CandidateTable? table, OutputBindings? bindings, ScalarValue[] context, ScalarValue[] parameters, ExpressionCounters? counters = null)
    {
        Table = table;
        Bindings = bindings;
        Context = context;
        Params = parameters;
        Counters = counters;
    }

    internal CandidateTable? Table { get; }

    internal OutputBindings? Bindings { get; }

    /// <summary>Request-level values indexed by <see cref="SymbolKind.Context"/> symbol id.</summary>
    internal ScalarValue[] Context { get; }

    /// <summary>Parameter snapshot indexed by <see cref="SymbolKind.Param"/> symbol id.</summary>
    internal ScalarValue[] Params { get; }

    internal ExpressionCounters? Counters { get; }
}

/// <summary>Counters of total-semantics events observed during evaluation.</summary>
public sealed class ExpressionCounters
{
    private long _outOfRange;

    /// <summary>
    /// Number of values that did not fit the storage they were written to and were replaced by the column's default or
    /// marked missing (metric <c>column_out_of_range_total</c>): an int64 outside the int32 range written to
    /// an <c>int32</c> column, a list with such an element written to a <c>list&lt;int32&gt;</c> column (counted once
    /// per list), or a list of another length written to a <c>list&lt;E,N&gt;</c> column, feature or context value.
    /// </summary>
    /// <remarks>Kept apart from the non-finite count (<c>column_nonfinite_total</c>).</remarks>
    public long OutOfRange => Interlocked.Read(ref _outOfRange);

    public void AddOutOfRange(long count) => Interlocked.Add(ref _outOfRange, count);
}
