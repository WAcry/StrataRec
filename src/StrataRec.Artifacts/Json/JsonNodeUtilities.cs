using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>Small helpers shared by the JSON standards implementations in this namespace.</summary>
internal static class JsonNodeUtilities
{
    /// <summary>UTF-8 without BOM that throws on unpaired surrogates instead of silently writing U+FFFD.</summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// True for JSON <c>null</c>. <see cref="JsonObject"/> stores a null member as a C# <see langword="null"/>, but a
    /// <see cref="JsonValue"/> wrapping a null <see cref="JsonElement"/> can also appear in hand-built trees.
    /// </summary>
    internal static bool IsJsonNull(JsonNode? node) =>
        node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null);

    /// <summary>
    /// Looks up a member by exact (ordinal) name, as RFC 7396 and RFC 6901 require, even when the object was created
    /// with <see cref="JsonNodeOptions.PropertyNameCaseInsensitive"/>.
    /// </summary>
    internal static bool TryGetMember(JsonObject obj, string name, out JsonNode? value)
    {
        if (obj.Options?.PropertyNameCaseInsensitive != true)
        {
            return obj.TryGetPropertyValue(name, out value);
        }

        foreach (var member in obj)
        {
            if (string.Equals(member.Key, name, StringComparison.Ordinal))
            {
                value = member.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Describes the JSON kind of a node for error messages, e.g. "an array" or "a string".</summary>
    internal static string DescribeKind(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "an array",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Null => "null",
            _ => "a value",
        },
        _ => "a value",
    };

    /// <summary>Formats a pointer for messages; the root pointer is the empty string, which reads badly in quotes.</summary>
    internal static string Display(JsonPointer pointer) =>
        pointer.IsRoot ? "the document root" : $"'{pointer}'";
}
