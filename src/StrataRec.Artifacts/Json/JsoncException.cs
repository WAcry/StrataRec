using System.Text;
using System.Text.Json;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// A JSONC document could not be read: a syntax error, a duplicate key, invalid Unicode, or a value that does not
/// fit the target type. Carries the file name and a 1-based line and column so editors and CI logs can jump to it.
/// </summary>
/// <remarks>
/// Derives from <see cref="JsonException"/>, so existing handlers keep working. The inherited
/// <see cref="JsonException.LineNumber"/> and <see cref="JsonException.BytePositionInLine"/> keep their
/// System.Text.Json meaning (0-based, UTF-8 bytes); <see cref="Line"/> and <see cref="Column"/> are 1-based, with the
/// column counted in UTF-16 code units as most editors do.
/// </remarks>
public sealed class JsoncException : JsonException
{
    /// <summary>The document is not well-formed JSONC.</summary>
    public const string SyntaxErrorCode = "JSONC_SYNTAX";

    /// <summary>An object contains the same key twice (compared after unescaping).</summary>
    public const string DuplicateKeyCode = "JSONC_DUPLICATE_KEY";

    /// <summary>A string escape produces an unpaired UTF-16 surrogate, or the input text is not valid Unicode.</summary>
    public const string InvalidUnicodeCode = "JSONC_INVALID_UNICODE";

    /// <summary>The document is well-formed but does not deserialize into the requested type.</summary>
    public const string DeserializationErrorCode = "JSONC_DESERIALIZE";

    internal JsoncException(
        string code,
        string reason,
        string? fileName,
        int? line,
        int? column,
        long? bytePositionInLine,
        string? jsonPath = null,
        string? hint = null,
        Exception? innerException = null)
        : base(Format(code, reason, fileName, line, column, jsonPath, hint), jsonPath, line - 1, bytePositionInLine, innerException)
    {
        Code = code;
        Reason = reason;
        FileName = fileName;
        Line = line;
        Column = column;
        Hint = hint;
    }

    /// <summary>Stable machine-readable code, one of the <c>*Code</c> constants on this type.</summary>
    public string Code { get; }

    /// <summary>The problem without location information.</summary>
    public string Reason { get; }

    /// <summary>The file name passed to the reader, if any.</summary>
    public string? FileName { get; }

    /// <summary>1-based line, or <see langword="null"/> when System.Text.Json did not report a position.</summary>
    public int? Line { get; }

    /// <summary>1-based column in UTF-16 code units, or <see langword="null"/> when unknown.</summary>
    public int? Column { get; }

    /// <summary>Optional suggestion for fixing the document.</summary>
    public string? Hint { get; }

    private static string Format(
        string code, string reason, string? fileName, int? line, int? column, string? jsonPath, string? hint)
    {
        var builder = new StringBuilder();
        builder.Append(fileName ?? "<jsonc>");
        if (line is not null)
        {
            builder.Append('(').Append(line.Value);
            if (column is not null)
            {
                builder.Append(',').Append(column.Value);
            }

            builder.Append(')');
        }

        builder.Append(": ").Append(code).Append(": ").Append(reason);
        if (jsonPath is not null)
        {
            builder.Append(" (at ").Append(jsonPath).Append(')');
        }

        if (hint is not null)
        {
            builder.Append(" Hint: ").Append(hint);
        }

        return builder.ToString();
    }
}
