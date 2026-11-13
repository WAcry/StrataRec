namespace StrataRec.Expressions;

/// <summary>
/// Stable, machine-readable codes of <see cref="ExpressionDiagnostic"/>. Tools, CI checks and agents match on these
/// codes, so they never change meaning once released.
/// </summary>
public static class ExpressionErrorCodes
{
    /// <summary>The text is not a well-formed expression (tokenizer or grammar error).</summary>
    public const string Syntax = "EXPR_SYNTAX";

    /// <summary>The source exceeds <see cref="ExpressionCompiler.MaxSourceLength"/> characters.</summary>
    public const string TooLong = "EXPR_TOO_LONG";

    /// <summary>Nesting exceeds <see cref="CompileOptions.MaxDepth"/> (design 7.9).</summary>
    public const string TooDeep = "EXPR_TOO_DEEP";

    /// <summary>A name that is neither a symbol of the scope nor a <c>let</c> binding.</summary>
    public const string UnknownName = "EXPR_UNKNOWN_NAME";

    /// <summary>A call of a function outside the built-in whitelist (design 7.6).</summary>
    public const string UnknownFunction = "EXPR_UNKNOWN_FUNCTION";

    /// <summary><c>lookup()</c> names a table the scope does not provide.</summary>
    public const string UnknownTable = "EXPR_UNKNOWN_TABLE";

    /// <summary>Wrong number of arguments to a built-in function.</summary>
    public const string ArgCount = "EXPR_ARG_COUNT";

    /// <summary>An operand, argument or result has a type the operation or site does not accept.</summary>
    public const string TypeMismatch = "EXPR_TYPE_MISMATCH";

    /// <summary>A well-typed argument with an invalid value, e.g. non-ascending <c>piecewise_linear</c> knots.</summary>
    public const string InvalidArgument = "EXPR_INVALID_ARGUMENT";

    /// <summary>A <c>let</c> binding that shadows a namespace, a function or another binding.</summary>
    public const string InvalidLet = "EXPR_INVALID_LET";

    /// <summary>The result may be missing but the site requires a value (design 7.4 rule 4).</summary>
    public const string NullableResult = "EXPR_NULLABLE_RESULT";

    /// <summary>A request-level site (<c>when</c>, funnel key) reads a candidate column.</summary>
    public const string RowReferenceInRequestLevel = "EXPR_ROW_REFERENCE_IN_REQUEST_LEVEL";
}
