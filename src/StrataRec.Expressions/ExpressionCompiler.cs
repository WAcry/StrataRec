using StrataRec.Core.Types;

namespace StrataRec.Expressions;

/// <summary>Where an expression is used; determines what its result may be (design 7.4).</summary>
public sealed record CompileOptions
{
    /// <summary>
    /// Required result type (storage spelling, e.g. <c>float64</c>, <c>float32</c>, <c>bool</c>). The compiler checks
    /// the promoted result against it; int64 results are accepted where float64 is expected. Nullability of this type is
    /// ignored — see <see cref="AllowNullableResult"/>. <c>null</c> accepts any result type.
    /// </summary>
    public ColumnType? ExpectedType { get; init; }

    /// <summary>True when the result may be nullable (writing a nullable column, log tap columns).</summary>
    public bool AllowNullableResult { get; init; }

    /// <summary>True for request-level sites (<c>when</c>, funnel keys, context-only values): <c>col.*</c> is an error.</summary>
    public bool RequestLevelOnly { get; init; }

    /// <summary>Maximum nesting depth, checked while parsing (design 7.9).</summary>
    public int MaxDepth { get; init; } = 32;

    public static CompileOptions Any { get; } = new() { AllowNullableResult = true };

    public static CompileOptions Predicate { get; } = new() { ExpectedType = ColumnType.Bool };

    public static CompileOptions Score { get; } = new() { ExpectedType = ColumnType.Float64 };

    public static CompileOptions RequestCondition { get; } = new() { ExpectedType = ColumnType.Bool, RequestLevelOnly = true };
}

/// <summary>Entry point of the ColumnExpr compiler: parse → type/nullability check → IR → optimize → backend.</summary>
public static partial class ExpressionCompiler
{
    /// <summary>Version of the built-in function set; part of the binary's capabilities (design 7.6, 10.4).</summary>
    public const string FunctionSetCapability = "expr_functions@1";

    /// <summary>Compiles an expression or throws <see cref="ExpressionCompileException"/>.</summary>
    public static CompiledExpression Compile(string source, IExpressionScope scope, CompileOptions? options = null)
    {
        if (TryCompile(source, scope, options, out var expression, out var diagnostics))
        {
            return expression;
        }

        throw new ExpressionCompileException(source, diagnostics);
    }
}
