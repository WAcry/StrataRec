using StrataRec.Core.Tables;
using StrataRec.Core.Values;

namespace StrataRec.Expressions;

/// <summary>Runtime inputs of an expression evaluation within one request.</summary>
/// <remarks>
/// <para><see cref="Table"/> and <see cref="Bindings"/> are required for row-level expressions and may be null for
/// request-level ones (<c>when</c>, context-only derived features, funnel keys).</para>
/// <para>A column symbol is read from <c>FixedSlot</c> when it is ≥ 0, otherwise from
/// <c>Bindings.Resolve(Id)</c>. An unresolved slot (<c>-1</c>) or a slot that was never written reads as missing.</para>
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

    public CandidateTable? Table { get; }

    public OutputBindings? Bindings { get; }

    /// <summary>Request-level values indexed by <see cref="SymbolKind.Context"/> symbol id.</summary>
    public ScalarValue[] Context { get; }

    /// <summary>Parameter snapshot indexed by <see cref="SymbolKind.Param"/> symbol id.</summary>
    public ScalarValue[] Params { get; }

    /// <summary>Optional counters for total-semantics events (e.g. integer modulo by zero, design 7.5).</summary>
    public ExpressionCounters? Counters { get; }
}

/// <summary>Counters of total-semantics events observed during evaluation.</summary>
public sealed class ExpressionCounters
{
    private long _intModZero;

    /// <summary>Number of <c>x % 0</c> evaluations (metric <c>expr_int_mod_zero_total</c>).</summary>
    public long IntModZero => Interlocked.Read(ref _intModZero);

    public void AddIntModZero(long count) => Interlocked.Add(ref _intModZero, count);
}
