using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// JSON values and kinds described in the words of the JSON a person wrote (<c>the string "350"</c>, <c>an array</c>,
/// <c>an integer</c>), so configuration errors never speak of the .NET types a document binds to.
/// </summary>
public static class JsonWords
{
    /// <summary>Longest value quoted in full; longer ones are cut, so a message stays one readable line.</summary>
    public const int MaxQuotedLength = 40;

    /// <summary>
    /// What a value is, for "got …": <c>null</c>, <c>true</c>, <c>false</c>, <c>the number 3.5</c>,
    /// <c>the string "350"</c>, <c>an array</c> or <c>an object</c>.
    /// </summary>
    public static string Value(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject:
                return "an object";
            case JsonArray:
                return "an array";
        }

        var value = (JsonValue)node;
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return TryText(value, out var text) ? "the string " + Quote(text) : "a string that is not valid Unicode";
            case JsonValueKind.Number:
                return "the number " + Cut(node.ToJsonString());
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                return Cut(node.ToJsonString());
        }
    }

    /// <summary>
    /// <see cref="Value(JsonNode?)"/> for a value read without building nodes (a request body read once); the same words
    /// for the same JSON, a string that is not valid Unicode included. An absent value (<see cref="JsonValueKind.Undefined"/>)
    /// is <c>nothing</c>.
    /// </summary>
    public static string Value(in JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => TryText(element, out var text) ? "the string " + Quote(text) : "a string that is not valid Unicode",
        JsonValueKind.Number => "the number " + Cut(element.GetRawText()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "nothing",
    };

    /// <summary>A kind of token, for a reader that only knows the token: <c>a number</c>, <c>an object</c>, …</summary>
    public static string Token(JsonTokenType token) => token switch
    {
        JsonTokenType.String or JsonTokenType.PropertyName => "a string",
        JsonTokenType.Number => "a number",
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Null => "null",
        JsonTokenType.StartArray or JsonTokenType.EndArray => "an array",
        JsonTokenType.StartObject or JsonTokenType.EndObject => "an object",
        _ => "nothing",
    };

    /// <summary>A string as JSON writes it, in double quotes, cut after <see cref="MaxQuotedLength"/> characters.</summary>
    public static string Quote(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxQuotedLength) + 3);
        builder.Append('"');
        var end = Math.Min(text.Length, MaxQuotedLength);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
        {
            end--; // never split a surrogate pair
        }

        for (var i = 0; i < end; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        if (end < text.Length)
        {
            builder.Append('…');
        }

        return builder.ToString();
    }

    /// <summary>Joins quoted names as a list a sentence can hold: <c>'a'</c>, <c>'a' and 'b'</c>, <c>'a', 'b' and 'c'</c>.</summary>
    public static string List(IReadOnlyList<string> names)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(i == names.Count - 1 ? " and " : ", ");
            }

            builder.Append('\'').Append(names[i]).Append('\'');
        }

        return builder.ToString();
    }

    /// <summary>
    /// A string value's text. Describing a value is how an error about it is told, so it must not fail itself: a string
    /// whose escapes are not valid UTF-16 (<c>"\uD800"</c>, an unpaired surrogate) has no text, and is said to be so.
    /// </summary>
    public static bool TryText(JsonValue value, out string text)
    {
        try
        {
            text = value.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary><see cref="TryText(JsonValue, out string)"/> for a string element.</summary>
    public static bool TryText(in JsonElement value, out string text)
    {
        try
        {
            text = value.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string Cut(string text) => text.Length <= MaxQuotedLength ? text : text[..MaxQuotedLength] + "…";
}
