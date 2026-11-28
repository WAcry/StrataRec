using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

internal static class JsonNodeUtilities
{
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// True for JSON <c>null</c>. <see cref="JsonObject"/> stores a null member as a C# <see langword="null"/>, but a
    /// <see cref="JsonValue"/> wrapping a null <see cref="JsonElement"/> can also appear in hand-built trees.
    /// </summary>
    internal static bool IsJsonNull(JsonNode? node) =>
        node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null);

    /// <summary>
    /// Asked of the CLR value, not by writing it: writing throws, and not alike (an <see cref="ArgumentException"/> for
    /// a double, a <see cref="JsonException"/> for a Half), and <see cref="JsonNode.GetValueKind"/> says Number for it.
    /// A value read from text holds none: its number is text, and converting it would make a finite binary64 number
    /// such as <c>1e100</c> an infinite float.
    /// </summary>
    internal static bool IsNonFiniteNumber(JsonValue value, out double number)
    {
        number = 0;
        if (value.TryGetValue(out JsonElement _))
        {
            return false;
        }

        if (value.TryGetValue(out double d))
        {
            number = d;
        }
        else if (value.TryGetValue(out float f))
        {
            number = f;
        }
        else if (value.TryGetValue(out Half h))
        {
            number = (double)h;
        }
        else
        {
            return false;
        }

        return !double.IsFinite(number);
    }

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

    internal static string Display(JsonPointer pointer) =>
        pointer.IsRoot ? "the document root" : $"'{pointer}'";
}
