using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// Strict reader for JSONC, the authoring format of every StrataRec artifact: JSON
/// (<see href="https://www.rfc-editor.org/rfc/rfc8259">RFC 8259</see>) plus <c>//</c> and <c>/* */</c> comments and
/// trailing commas in objects and arrays.
/// </summary>
/// <remarks>
/// <para>Beyond the grammar, the reader rejects input that other JSON tools would interpret differently:</para>
/// <list type="bullet">
///   <item>
///     Duplicate keys in one object (compared after unescaping, so <c>"a"</c> and <c>"a"</c> collide).
///     RFC 8259 section 4 leaves their meaning undefined and I-JSON (RFC 7493 section 2.3) forbids them; silently
///     keeping the last one would hide copy-paste mistakes in configuration.
///   </item>
///   <item>
///     Text that is not valid Unicode: ill-formed UTF-8 in byte input (anywhere, including unescaped string values),
///     unpaired surrogates in string input, and string escapes that produce unpaired UTF-16 surrogates
///     (RFC 8259 section 8.1, RFC 7493 section 2.1).
///   </item>
///   <item>
///     Anything that is not a JSON number in number position: <c>NaN</c>, <c>Infinity</c>, hexadecimal, leading
///     <c>+</c> or leading zeros.
///   </item>
///   <item>Nesting deeper than <see cref="MaxDepth"/>.</item>
/// </list>
/// <para>
/// A leading UTF-8 byte order mark is ignored. Every failure is a <see cref="JsoncException"/> with the file name
/// and a 1-based line and column.
/// </para>
/// </remarks>
public static class Jsonc
{
    /// <summary>Maximum nesting depth of objects and arrays.</summary>
    public const int MaxDepth = 64;

    private static readonly JsonReaderOptions _readerOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = MaxDepth,
    };

    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = MaxDepth,
    };

    /// <summary>Parses a JSONC document into a mutable <see cref="JsonNode"/> tree.</summary>
    /// <param name="json">The document text.</param>
    /// <param name="fileName">Optional file name used in error messages.</param>
    /// <returns>The root node, or <see langword="null"/> when the document is the literal <c>null</c>.</returns>
    /// <exception cref="JsoncException">The document is not valid JSONC or breaks one of the strictness rules.</exception>
    public static JsonNode? Parse(string json, string? fileName = null) => Parse(Encode(json, fileName), fileName);

    /// <summary>Parses a UTF-8 encoded JSONC document into a mutable <see cref="JsonNode"/> tree.</summary>
    /// <inheritdoc cref="Parse(string, string?)"/>
    public static JsonNode? Parse(ReadOnlySpan<byte> utf8Json, string? fileName = null)
    {
        var json = SkipByteOrderMark(utf8Json);
        Validate(json, fileName);
        try
        {
            return JsonNode.Parse(json, nodeOptions: null, documentOptions: _documentOptions);
        }
        catch (JsonException ex)
        {
            // Unreachable in practice: Validate uses the same grammar. Kept so callers only ever see JsoncException.
            throw SyntaxError(json, fileName, ex);
        }
    }

    /// <summary>
    /// Parses a UTF-8 encoded JSONC document as strictly as <see cref="Parse(ReadOnlySpan{byte}, string?)"/> into a
    /// <see cref="JsonElement"/> that owns its data: for data read by key rather than edited, such as a connector's
    /// fixture file. Bytes, not text: a file decoded first would have its invalid bytes replaced, not reported.
    /// </summary>
    /// <exception cref="JsoncException">The document is not valid JSONC or breaks one of the strictness rules.</exception>
    public static JsonElement ParseElement(ReadOnlySpan<byte> utf8Json, string? fileName = null)
    {
        var json = SkipByteOrderMark(utf8Json);
        Validate(json, fileName);
        var reader = new Utf8JsonReader(json, _readerOptions);
        return JsonElement.ParseValue(ref reader);
    }

    /// <summary>
    /// The document as UTF-8, after the checks <see cref="Parse(string, string?)"/> makes: for a caller that binds the
    /// bytes itself, as strictly as a parsed file.
    /// </summary>
    /// <param name="hasNull">Whether the document has a <c>null</c> anywhere.</param>
    internal static ReadOnlyMemory<byte> Checked(string json, string? fileName, out bool hasNull)
    {
        var bytes = Encode(json, fileName);
        var start = SkipByteOrderMark(bytes).Length == bytes.Length ? 0 : 3;
        hasNull = Validate(bytes.AsSpan(start), fileName);
        return bytes.AsMemory(start);
    }

    private static byte[] Encode(string json, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonNodeUtilities.StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException ex)
        {
            var index = Math.Clamp(ex.Index, 0, json.Length);
            var prefix = json.AsSpan(0, index);
            var line = prefix.Count('\n') + 1;
            var column = index - (prefix.LastIndexOf('\n') + 1) + 1;
            throw new JsoncException(
                JsoncException.InvalidUnicodeCode,
                "The text contains an unpaired UTF-16 surrogate and is not valid Unicode.",
                fileName, line, column, null, innerException: ex);
        }
    }

    private static ReadOnlySpan<byte> SkipByteOrderMark(ReadOnlySpan<byte> utf8) =>
        utf8.StartsWith("﻿"u8) ? utf8[3..] : utf8; // U+FEFF encodes as EF BB BF

    /// <returns>Whether the document has a <c>null</c> token.</returns>
    private static bool Validate(ReadOnlySpan<byte> json, string? fileName)
    {
        // Utf8JsonReader finds the end of an unescaped string without validating its UTF-8, and JsonNode keeps such
        // values lazily, so invalid bytes would surface only when a value is read, far from this diagnostic boundary.
        if (!Utf8.IsValid(json))
        {
            var here = PositionOf(json, FirstInvalidUtf8Offset(json));
            throw new JsoncException(
                JsoncException.InvalidUnicodeCode,
                "The input is not valid UTF-8.",
                fileName, here.Line, here.Column, here.BytePositionInLine,
                hint: "save the file as UTF-8; escape binary data (for example as base64) instead of embedding raw bytes");
        }

        var reader = new Utf8JsonReader(json, _readerOptions);

        var keySets = new List<Dictionary<string, long>>();
        var hasNull = false;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        var depth = reader.CurrentDepth;
                        while (keySets.Count <= depth)
                        {
                            keySets.Add(new Dictionary<string, long>(StringComparer.Ordinal));
                        }

                        keySets[depth].Clear();
                        break;

                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        if (!keySets[reader.CurrentDepth - 1].TryAdd(name, reader.TokenStartIndex))
                        {
                            var first = PositionOf(json, keySets[reader.CurrentDepth - 1][name]);
                            var here = PositionOf(json, reader.TokenStartIndex);
                            throw new JsoncException(
                                JsoncException.DuplicateKeyCode,
                                $"Duplicate key '{name}'; it is already defined in the same object at line {first.Line}, column {first.Column}.",
                                fileName, here.Line, here.Column, here.BytePositionInLine,
                                hint: "remove or merge one of the definitions; duplicate keys are rejected rather than letting the last one win");
                        }

                        break;

                    case JsonTokenType.String when reader.ValueIsEscaped:
                        // Unescaping validates \u escapes: an unpaired surrogate throws InvalidOperationException.
                        _ = reader.GetString();
                        break;

                    case JsonTokenType.Null:
                        hasNull = true;
                        break;
                }
            }

            return hasNull;
        }
        catch (JsonException ex) when (ex is not JsoncException)
        {
            throw SyntaxError(json, fileName, ex);
        }
        catch (InvalidOperationException ex)
        {
            var here = PositionOf(json, reader.TokenStartIndex);
            throw new JsoncException(
                JsoncException.InvalidUnicodeCode,
                "A string escape produces an unpaired UTF-16 surrogate, which is not valid Unicode (RFC 7493 section 2.1).",
                fileName, here.Line, here.Column, here.BytePositionInLine,
                hint: "use a complete surrogate pair such as \\ud83d\\ude00, or write the character directly",
                innerException: ex);
        }
    }

    private static int FirstInvalidUtf8Offset(ReadOnlySpan<byte> json)
    {
        var offset = 0;
        while (offset < json.Length)
        {
            if (Rune.DecodeFromUtf8(json[offset..], out _, out var consumed) != OperationStatus.Done)
            {
                return offset;
            }

            offset += consumed;
        }

        return json.Length;
    }

    private static JsoncException SyntaxError(ReadOnlySpan<byte> json, string? fileName, JsonException ex)
    {
        var reason = StripLocation(ex.Message);
        if (ex.LineNumber is { } line && ex.BytePositionInLine is { } bytePosition)
        {
            var column = ColumnOf(json, line, bytePosition);
            return new JsoncException(
                JsoncException.SyntaxErrorCode, reason, fileName, (int)line + 1, column, bytePosition, innerException: ex);
        }

        return new JsoncException(JsoncException.SyntaxErrorCode, reason, fileName, null, null, null, innerException: ex);
    }

    private static string StripLocation(string message)
    {
        var cut = message.Length;
        var path = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (path >= 0)
        {
            cut = Math.Min(cut, path);
        }

        var line = message.IndexOf(" LineNumber: ", StringComparison.Ordinal);
        if (line >= 0)
        {
            cut = Math.Min(cut, line);
        }

        return message[..cut].TrimEnd();
    }

    /// <summary>Converts a byte offset into a 1-based line and UTF-16 column, plus the 0-based byte offset in the line.</summary>
    internal static (int Line, int Column, long BytePositionInLine) PositionOf(ReadOnlySpan<byte> json, long offset)
    {
        var end = (int)Math.Clamp(offset, 0, json.Length);
        var prefix = json[..end];
        var line = prefix.Count((byte)'\n') + 1;
        var lineStart = prefix.LastIndexOf((byte)'\n') + 1;
        var column = Encoding.UTF8.GetCharCount(prefix[lineStart..]) + 1;
        return (line, column, end - lineStart);
    }

    private static int ColumnOf(ReadOnlySpan<byte> json, long lineNumber, long bytePositionInLine)
    {
        var lineStart = 0;
        for (long l = 0; l < lineNumber; l++)
        {
            var next = json[lineStart..].IndexOf((byte)'\n');
            if (next < 0)
            {
                break;
            }

            lineStart += next + 1;
        }

        var end = (int)Math.Clamp(lineStart + bytePositionInLine, lineStart, json.Length);
        return Encoding.UTF8.GetCharCount(json[lineStart..end]) + 1;
    }
}
