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
    public abstract ScalarValue EvaluateScalar(in EvalContext context);

    /// <summary>
    /// Evaluates the expression for the given physical rows and writes each result into
    /// <paramref name="destination"/> at the same physical row. Rows not listed are left untouched.
    /// </summary>
    /// <remarks>
    /// <para>The destination's storage kind must be compatible with <see cref="ResultType"/>: float64 results may be
    /// written to float64 or float32 (narrowing) columns, int64 to int64 or int32 (truncating) or float columns, and
    /// other kinds to the same kind. Missing results clear the destination's validity bit; present results set it.</para>
    /// <para>Non-finite values are written as-is; the engine applies column defaults and counting at commit
    /// (design 4.3). Request-level expressions broadcast their value to every listed row.</para>
    /// <para>No managed allocation for numeric and bool expressions in steady state.</para>
    /// </remarks>
    public abstract void Evaluate(in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination);
}
