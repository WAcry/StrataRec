using System.Text.Json;

namespace StrataRec.Artifacts.Json;

/// <summary>Where a JSON pointer was found in a JSONC text.</summary>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column in UTF-16 code units, as <see cref="JsoncException"/> and most editors count.</param>
/// <param name="Exact">False when the pointer does not exist in the text and the position is that of its nearest existing ancestor.</param>
public readonly record struct JsoncLocation(int Line, int Column, bool Exact);

/// <summary>
/// Finds the line and column of an RFC 6901 pointer in the JSONC text it points into, so a tool can print a diagnostic
/// located by artifact and pointer as <c>file:line:column</c>, which terminals and editors open at the place.
/// </summary>
/// <remarks>
/// <para>An object member is located at its key, where an editor shows a misspelled or missing key; an array element and
/// the root at their value. With an offset into a string value (an expression or a template), the position is that
/// character's, counting escapes as written (<c>\"</c> is two columns, one character).</para>
/// <para>A pointer into a merged or patched view may not exist in the file (a node an arm adds, an entry a patch
/// changes): the position is then its nearest ancestor that exists, never a guess. Locating reads the text once with a
/// forward-only reader and skips every member off the path; it runs when a diagnostic is printed, not when one is
/// found.</para>
/// </remarks>
public static class JsoncLocator
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = Jsonc.MaxDepth,
    };

    /// <summary>Locates <paramref name="pointer"/> in UTF-8 JSONC text.</summary>
    /// <param name="utf8Jsonc">The text, as the file holds it (a UTF-8 byte order mark is skipped).</param>
    /// <param name="pointer">An RFC 6901 pointer; one that does not parse locates the root.</param>
    /// <param name="stringOffset">A 0-based UTF-16 offset into the string value at the pointer, or null for the value as a whole.</param>
    /// <returns>The position, or null when the text has no readable value at all.</returns>
    public static JsoncLocation? Locate(ReadOnlySpan<byte> utf8Jsonc, string pointer, int? stringOffset = null)
    {
        var json = utf8Jsonc.StartsWith("﻿"u8) ? utf8Jsonc[3..] : utf8Jsonc;
        var tokens = JsonPointer.TryParse(pointer, out var parsed) ? parsed.Tokens : default;
        var depth = tokens.IsDefault ? 0 : tokens.Length;
        var reader = new Utf8JsonReader(json, ReaderOptions);
        long at; // where the deepest match is shown: a member's key, an element's or the root's value
        long value; // where that match's value starts
        var matched = 0;
        try
        {
            if (!reader.Read())
            {
                return null;
            }

            at = value = reader.TokenStartIndex;
        }
        catch (JsonException)
        {
            return null;
        }

        try
        {
            while (matched < depth)
            {
                var token = tokens[matched];
                var found = false;
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var key = reader.TokenStartIndex;
                        var isMatch = reader.ValueTextEquals(token);
                        reader.Read();
                        if (isMatch)
                        {
                            at = key;
                            value = reader.TokenStartIndex;
                            found = true;
                            break;
                        }

                        reader.Skip();
                    }
                }
                else if (reader.TokenType == JsonTokenType.StartArray && IsIndex(token, out var target))
                {
                    for (var index = 0; reader.Read() && reader.TokenType != JsonTokenType.EndArray; index++)
                    {
                        if (index == target)
                        {
                            at = value = reader.TokenStartIndex;
                            found = true;
                            break;
                        }

                        reader.Skip();
                    }
                }

                if (!found)
                {
                    break;
                }

                matched++;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Malformed after the deepest match, or a key that is not valid Unicode (comparing it unescapes it): what was found stands.
        }

        var exact = matched == depth;
        var within = 0; // UTF-16 units into the character at 'at': 1 for the low surrogate of a raw non-BMP character
        if (exact && stringOffset is { } offset && value < json.Length && json[(int)value] == (byte)'"')
        {
            (at, within) = CharacterInString(json, (int)value, offset);
        }

        var position = Jsonc.PositionOf(json, at);
        return new JsoncLocation(position.Line, position.Column + within, exact);
    }

    /// <summary>
    /// Where UTF-16 unit <paramref name="offset"/> of the string literal starting (at its quote) at
    /// <paramref name="quote"/> is written: the byte offset of the character holding it, and how many UTF-16 units into
    /// that character it is (1 for the low surrogate of a non-BMP character written raw, whose two units share four
    /// bytes; 0 otherwise).
    /// </summary>
    private static (int Position, int Within) CharacterInString(ReadOnlySpan<byte> json, int quote, int offset)
    {
        var position = quote + 1;
        var units = 0;
        while (units < offset && position < json.Length && json[position] != (byte)'"')
        {
            var b = json[position];
            if (b == (byte)'\\')
            {
                // \uXXXX is one UTF-16 unit (a surrogate pair is written as two escapes); every other escape is one too.
                position += position + 1 < json.Length && json[position + 1] == (byte)'u' ? 6 : 2;
                units++;
            }
            else if (b < 0x80)
            {
                position++;
                units++;
            }
            else if (b >= 0xF0)
            {
                // A 4-byte sequence is a surrogate pair in UTF-16: an offset at its second unit is inside it.
                if (units + 1 == offset)
                {
                    return (position, 1);
                }

                position += 4;
                units += 2;
            }
            else
            {
                position += b >= 0xE0 ? 3 : 2;
                units++;
            }
        }

        return (Math.Min(position, json.Length), 0);
    }

    private static bool IsIndex(string token, out int index)
    {
        index = 0;
        if (token.Length == 0 || (token.Length > 1 && token[0] == '0'))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (c is < '0' or > '9' || index > (int.MaxValue - 9) / 10)
            {
                return false;
            }

            index = index * 10 + (c - '0');
        }

        return true;
    }
}
