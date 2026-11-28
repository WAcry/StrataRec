using System.Diagnostics.CodeAnalysis;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Syntax;
using StrataRec.Expressions.Vectorized;

namespace StrataRec.Expressions;

public static partial class ExpressionCompiler
{
    /// <summary>Longest accepted source text in characters.</summary>
    public const int MaxSourceLength = 64 * 1024;

    private static readonly CompileOptions DefaultOptions = new();

    /// <summary>
    /// Compiles an expression, returning diagnostics instead of throwing. Fails without diagnostics only when the
    /// expression reads a name the scope declares with an error (<see cref="IExpressionScope.IsUnusable"/>), whose
    /// diagnostic belongs to the declaration.
    /// </summary>
    public static bool TryCompile(
        string source,
        IExpressionScope scope,
        CompileOptions? options,
        [NotNullWhen(true)] out CompiledExpression? expression,
        out IReadOnlyList<ExpressionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        options ??= DefaultOptions;
        expression = null;

        var found = new List<ExpressionDiagnostic>();
        diagnostics = found;
        if (source.Length > MaxSourceLength)
        {
            found.Add(new ExpressionDiagnostic(ExpressionErrorCodes.TooLong,
                $"Expression is {source.Length} characters long; the limit is {MaxSourceLength}.", MaxSourceLength, 1,
                "split the logic into several expressions or a dedicated operator"));
            return false;
        }

        AstNode ast;
        try
        {
            ast = Parser.Parse(source, options.MaxDepth);
        }
        catch (SyntaxErrorException e)
        {
            found.Add(e.Diagnostic);
            return false;
        }

        var binder = new Binder(scope, options, found);
        var bound = binder.Bind(ast);

        // Let bindings and desugared built-ins make the IR deeper than the source; IR passes recurse over it.
        var irDepthLimit = Math.Max(128, options.MaxDepth * 8);
        if (bound.Depth > irDepthLimit)
        {
            found.Add(new ExpressionDiagnostic(ExpressionErrorCodes.TooDeep,
                $"After expanding let bindings the expression nests {bound.Depth} levels deep; the limit is {irDepthLimit}.",
                ast.Start, Math.Max(1, ast.Length), "flatten long chains of dependent let bindings or move the logic into an operator"));
            return false;
        }

        bound = binder.CheckResult(bound, ast);
        if (found.Count > 0 || binder.ReadsUnusable)
        {
            return false;
        }

        var written = ast;
        while (written is ParenAst paren)
        {
            written = paren.Inner;
        }

        var reference = written is NameAst && bound.Op is IrOp.Column or IrOp.Context or IrOp.Param ? bound.Symbol : (Symbol?)null;
        var optimized = Optimizer.Optimize(bound);
        expression = new VectorizedExpression(source, bound, optimized, binder.Names.ToArray(), reference);
        return true;
    }
}
