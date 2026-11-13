using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Expressions;

/// <summary>
/// An immutable, thread-safe compiled expression. One instance is shared by all requests using a plan.
/// </summary>
public abstract class CompiledExpression
{
    /// <summary>Original source text.</summary>
    public abstract string Source { get; }

    /// <summary>Promoted result type with nullability (int64, float64, bool, string, text, vec, list).</summary>
    public abstract ColumnType ResultType { get; }

    /// <summary>True when the expression reads any <c>col.*</c> symbol (otherwise it is request-level).</summary>
    public abstract bool IsRowDependent { get; }

    /// <summary>Every symbol the expression reads (deduplicated), for dependency analysis and fetch planning.</summary>
    public abstract IReadOnlyList<Symbol> Reads { get; }

    /// <summary>Normalized, optimized form of the expression for explain-plan output.</summary>
    public abstract string Describe();

    /// <summary>
    /// Evaluates a request-level expression (<see cref="IsRowDependent"/> is false).
    /// Returns <see cref="ScalarValue.Missing"/> when a nullable result is absent.
    /// </summary>
    /// <remarks>A list result that comes from a literal in the expression (<c>[1, 2]</c>, <c>ctx.l ?? ['a']</c>) is a
    /// fresh copy, because the compiled expression and its constants are shared by every request and thread. Other
    /// results are returned as they are, without copying: a list read from <c>ctx.*</c> or <c>param.*</c> is the
    /// caller's own array.</remarks>
    public abstract ScalarValue EvaluateScalar(in EvalContext context);

    /// <summary>
    /// Evaluates the expression for the given physical rows and writes each result into
    /// <paramref name="destination"/> at the same physical row. Rows not listed are left untouched.
    /// </summary>
    /// <remarks>
    /// <para>The destination's storage kind must be compatible with <see cref="ResultType"/>: float64 results may be
    /// written to float64 or float32 (narrowing) columns, int64 to int64, int32 (narrowing) or float columns, int64
    /// lists to <c>list&lt;int64&gt;</c> or <c>list&lt;int32&gt;</c> (narrowing) columns, and other kinds to the same kind.
    /// Missing results clear the destination's validity bit; present results set it.</para>
    /// <para>Narrowing to int32 storage never wraps. A value outside the int32 range (for a list: any element outside
    /// it, since a partial list would be wrong data) is out of range: the row is marked missing and counted in
    /// <see cref="ExpressionCounters.OutOfRange"/>. Use the overload with a replacement value to write the column's
    /// default instead (design 4.3).</para>
    /// <para>Non-finite values are written as-is; the engine applies column defaults and counting at commit
    /// (design 4.3). Request-level expressions broadcast their value to every listed row.</para>
    /// <para>The destination may be a column the expression reads (an in-place update such as <c>col.a ?? [7]</c>
    /// written into <c>col.a</c>): every listed row's result is computed from the values and validity the column had
    /// when the call started.</para>
    /// <para>No managed allocation for numeric and bool expressions in steady state.</para>
    /// </remarks>
    public void Evaluate(in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination) =>
        Evaluate(context, rows, destination, ScalarValue.Missing);

    /// <summary>
    /// Like <see cref="Evaluate(in EvalContext, ReadOnlySpan{int}, ColumnBuffer)"/>, writing
    /// <paramref name="outOfRange"/> into rows whose result does not fit the destination's int32 storage.
    /// </summary>
    /// <param name="outOfRange">The destination column's declared default (promoted: an int64 for <c>int32</c>, an int64
    /// list for <c>list&lt;int32&gt;</c>), or <see cref="ScalarValue.Missing"/> to mark such rows missing. Either way the
    /// row is counted in <see cref="ExpressionCounters.OutOfRange"/>. Ignored for destinations without int32 storage.</param>
    /// <exception cref="ArgumentException">The destination cannot hold the result type, or <paramref name="outOfRange"/>
    /// is present but does not fit the destination itself.</exception>
    public abstract void Evaluate(in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination, in ScalarValue outOfRange);
}
