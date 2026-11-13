using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// Strict reader for JSONC, the authoring format of every StrataRec artifact (design 9.1): JSON
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
///   <item>String escapes that produce unpaired UTF-16 surrogates (RFC 7493 section 2.1).</item>
///   <item>
///     Anything that is not a JSON number in number position: <c>NaN</c>, <c>Infinity</c>, hexadecimal, leading
///     <c>+</c> or leading zeros. Quoted numbers are not converted to numbers by <see cref="Deserialize{T}(string, string?)"/>.
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
    /// <summary>Maximum nesting depth of objects and arrays; matches the System.Text.Json default.</summary>
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

    /// <summary>
    /// The read-only serializer options used for every artifact: comments skipped, trailing commas allowed, strict
    /// numbers (<see cref="JsonNumberHandling.Strict"/>), duplicate properties rejected, case-sensitive names.
    /// </summary>
    /// <remarks>
    /// Property names are not transformed by a naming policy: model types spell their snake_case JSON names
    /// explicitly with <see cref="JsonPropertyNameAttribute"/>. Copy the options
    /// (<c>new JsonSerializerOptions(Jsonc.SerializerOptions)</c>) to add converters or a source-generated resolver.
    /// </remarks>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

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

    /// <summary>Deserializes a JSONC document into <typeparamref name="T"/> using <see cref="SerializerOptions"/>.</summary>
    /// <param name="json">The document text.</param>
    /// <param name="fileName">Optional file name used in error messages.</param>
    /// <exception cref="JsoncException">
    /// The document is not valid JSONC, or does not fit <typeparamref name="T"/> (wrong type, missing required
    /// property, ...); <see cref="JsonException.Path"/> then holds the System.Text.Json path of the offending value.
    /// </exception>
    public static T? Deserialize<T>(string json, string? fileName = null) => Deserialize<T>(Encode(json, fileName), fileName);

    /// <summary>Deserializes a UTF-8 encoded JSONC document into <typeparamref name="T"/>.</summary>
    /// <inheritdoc cref="Deserialize{T}(string, string?)"/>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json, string? fileName = null)
    {
        var json = SkipByteOrderMark(utf8Json);
        Validate(json, fileName);
        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions);
        }
        catch (JsonException ex) when (ex is not JsoncException)
        {
            var reason = StripLocation(ex.Message);
            if (ex.LineNumber is { } line && ex.BytePositionInLine is { } bytePosition)
            {
                var column = ColumnOf(json, line, bytePosition);
                throw new JsoncException(
                    JsoncException.DeserializationErrorCode, reason, fileName, (int)line + 1, column, bytePosition, ex.Path, innerException: ex);
            }

            throw new JsoncException(
                JsoncException.DeserializationErrorCode, reason, fileName, null, null, null, ex.Path, innerException: ex);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.Strict,
            AllowDuplicateProperties = false,
            PropertyNameCaseInsensitive = false,
            MaxDepth = MaxDepth,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        return options;
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

    /// <summary>
    /// Runs the full grammar over the document and enforces the strictness rules that <see cref="JsonNode.Parse(ReadOnlySpan{byte}, JsonNodeOptions?, JsonDocumentOptions)"/>
    /// does not (duplicate keys, lone surrogates), with exact positions.
    /// </summary>
    private static void Validate(ReadOnlySpan<byte> json, string? fileName)
    {
        var reader = new Utf8JsonReader(json, _readerOptions);

        // Key sets indexed by the depth of the object that owns them; reused across sibling objects.
        var keySets = new List<Dictionary<string, long>>();
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
                }
            }
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

    /// <summary>Removes System.Text.Json's " Path: ... | LineNumber: ... | BytePositionInLine: ..." suffix.</summary>
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
    private static (int Line, int Column, long BytePositionInLine) PositionOf(ReadOnlySpan<byte> json, long offset)
    {
        var end = (int)Math.Clamp(offset, 0, json.Length);
        var prefix = json[..end];
        var line = prefix.Count((byte)'\n') + 1;
        var lineStart = prefix.LastIndexOf((byte)'\n') + 1;
        var column = Encoding.UTF8.GetCharCount(prefix[lineStart..]) + 1;
        return (line, column, end - lineStart);
    }

    /// <summary>Converts System.Text.Json's 0-based line and byte position into a 1-based UTF-16 column.</summary>
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
