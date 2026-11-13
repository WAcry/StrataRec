using System.Diagnostics.CodeAnalysis;

namespace StrataRec.Expressions;

public static partial class ExpressionCompiler
{
    /// <summary>Compiles an expression, returning diagnostics instead of throwing.</summary>
    /// <remarks>Placeholder: replaced by the real front end and vectorized backend.</remarks>
    public static bool TryCompile(
        string source,
        IExpressionScope scope,
        CompileOptions? options,
        [NotNullWhen(true)] out CompiledExpression? expression,
        out IReadOnlyList<ExpressionDiagnostic> diagnostics)
    {
        throw new NotImplementedException();
    }
}
