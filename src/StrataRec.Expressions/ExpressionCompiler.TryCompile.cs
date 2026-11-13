using System.Diagnostics.CodeAnalysis;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Syntax;
using StrataRec.Expressions.Vectorized;

namespace StrataRec.Expressions;

public static partial class ExpressionCompiler
{
    /// <summary>Longest accepted source text in characters; bounds compile-time work for unreviewed drafts.</summary>
    public const int MaxSourceLength = 64 * 1024;

    /// <summary>Default options when none are given: any non-nullable result (design 7.4 rule 4).</summary>
    private static readonly CompileOptions DefaultOptions = new();

    /// <summary>Compiles an expression, returning diagnostics instead of throwing.</summary>
    /// <remarks>
    /// Pipeline (design 7.7): tokenize → parse (depth-checked while parsing, design 7.9) → bind (names, types,
    /// nullability; diagnostics are collected, not thrown) → result checks against <paramref name="options"/> →
    /// optimize (constant folding, CSE, invariant marking) → lower to the vectorized backend.
    /// </remarks>
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

        binder.CheckResult(bound, ast);
        if (found.Count > 0)
        {
            return false;
        }

        var optimized = Optimizer.Optimize(bound);
        expression = new VectorizedExpression(source, bound, optimized);
        return true;
    }
}
