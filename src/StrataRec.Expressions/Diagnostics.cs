namespace StrataRec.Expressions;

/// <summary>A compile-time problem in an expression, with its location in the source text.</summary>
/// <param name="Code">Stable machine-readable code, e.g. <c>EXPR_UNKNOWN_NAME</c>, <c>EXPR_NULLABLE_RESULT</c>.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="Offset">0-based character offset into the source.</param>
/// <param name="Length">Length of the offending span (≥ 1 where possible).</param>
/// <param name="Hint">Optional fix suggestion, e.g. <c>did you mean 'col.item.ctr_score'?</c> or
/// <c>add '?? 0.0' to handle missing values</c>.</param>
public sealed record ExpressionDiagnostic(string Code, string Message, int Offset, int Length, string? Hint = null)
{
    public override string ToString() =>
        Hint is null ? $"{Code} at {Offset}: {Message}" : $"{Code} at {Offset}: {Message} ({Hint})";
}

/// <summary>Thrown by <see cref="ExpressionCompiler.Compile"/> when an expression does not compile.</summary>
public sealed class ExpressionCompileException : Exception
{
    public ExpressionCompileException(string source, IReadOnlyList<ExpressionDiagnostic> diagnostics)
        : base(Format(source, diagnostics))
    {
        Source = source;
        Diagnostics = diagnostics;
    }

    public new string Source { get; }

    public IReadOnlyList<ExpressionDiagnostic> Diagnostics { get; }

    private static string Format(string source, IReadOnlyList<ExpressionDiagnostic> diagnostics) =>
        $"Expression '{source}' does not compile: " + string.Join("; ", diagnostics);
}
