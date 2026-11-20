using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using StrataRec.Core.Diagnostics;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// What <see cref="Jsonc"/> rejects in a configuration file, for a value System.Text.Json has already parsed (a request
/// body read with <see cref="JsonDocument"/>): a member given twice in one object, a name or string whose escapes are not
/// Unicode (an unpaired surrogate such as <c>\uD800</c>), and a name or string whose bytes are not UTF-8 (a raw
/// <c>0xFF</c>, which <see cref="JsonDocument"/> keeps until the text is read, review 33).
/// </summary>
/// <remarks>
/// System.Text.Json parses both. What it does with them later is not an answer to the caller: a <c>JsonObject</c> throws
/// when its members are first enumerated, a reader of a <see cref="JsonElement"/> sees both values of a duplicate and keeps
/// one, and reading such a string or name throws. Checked once where the request comes in, either is an invalid request
/// at its pointer (RFC 8259 section 4 leaves duplicate names undefined; I-JSON, RFC 7493, forbids both). The check walks
/// the value once and allocates nothing for objects of a few members with plain names; errors only on the error path.
/// </remarks>
public static class StrictJson
{
    /// <summary>Objects with more members are checked for duplicates with a set instead of by pairs.</summary>
    private const int PairwiseLimit = 16;

    /// <summary>
    /// The first problem in <paramref name="value"/>, at the pointer of the value at fault relative to it (a duplicate member
    /// at its second occurrence); null when there is none.
    /// </summary>
    public static JsonBindingError? Check(in JsonElement value) => Check(value, ignoreCase: false);

    /// <param name="ignoreCase">Whether two names that differ only in case are one, as they are to a node read with <see cref="JsonNodeOptions.PropertyNameCaseInsensitive"/>.</param>
    private static JsonBindingError? Check(in JsonElement value, bool ignoreCase)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                return CheckObject(value, ignoreCase);
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (Check(item, ignoreCase) is { } inner)
                    {
                        return Under(index.ToString(System.Globalization.CultureInfo.InvariantCulture), inner);
                    }

                    index++;
                }

                return null;
            case JsonValueKind.String when !Utf8.IsValid(JsonMarshal.GetRawUtf8Value(value)):
                return new JsonBindingError(string.Empty, "The string is not valid UTF-8 text.", Utf8Hint);
            case JsonValueKind.String when Escaped(JsonMarshal.GetRawUtf8Value(value)) && !Readable(value):
                return new JsonBindingError(string.Empty, $"The string {value.GetRawText()} is not valid Unicode: an escape is an unpaired surrogate.", UnicodeHint);
            default:
                return null;
        }
    }

    /// <summary>
    /// <see cref="Check(in JsonElement)"/> for a node tree a host built in code, before the engine reads it, so that no
    /// later read throws (review 79, review 81). Each object is enumerated once here, inside a catch: an object
    /// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/> made from text keeps that text until it
    /// is first enumerated, and then throws when two members have one name (by ordinal comparison, or ignoring case when
    /// its <see cref="JsonNodeOptions.PropertyNameCaseInsensitive"/> says so) or a name is not Unicode, at whatever level
    /// and in whatever reader that happens. Such an object is reported at the member at fault, found in the text it still
    /// writes; one whose fault cannot be found there, at the object. A string read from text is checked as a request's is.
    /// A tree nested deeper than <paramref name="maxDepth"/> objects and arrays (the outermost is the first) is reported at
    /// the first one too deep, without reading further. What the tree holds that JSON cannot write (NaN, a CLR value) is
    /// not this check's: the canonical form reports it at its pointer.
    /// </summary>
    /// <returns>The first problem, at its pointer relative to <paramref name="node"/>; null when there is none. A tree
    /// that has none has been enumerated throughout: reading it again throws nothing this check looks for.</returns>
    public static JsonBindingError? Check(JsonNode? node, int maxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        return CheckNode(node, 1, maxDepth);
    }

    private static JsonBindingError? CheckNode(JsonNode? node, int level, int maxDepth)
    {
        switch (node)
        {
            case JsonObject obj:
                if (level > maxDepth)
                {
                    return TooDeep(maxDepth);
                }

                if (Enumerate(obj) is { } unreadable)
                {
                    return unreadable;
                }

                foreach (var (name, value) in obj)
                {
                    if (CheckNode(value, level + 1, maxDepth) is { } inner)
                    {
                        return Under(name, inner);
                    }
                }

                return null;
            case JsonArray array:
                if (level > maxDepth)
                {
                    return TooDeep(maxDepth);
                }

                for (var i = 0; i < array.Count; i++)
                {
                    if (CheckNode(array[i], level + 1, maxDepth) is { } inner)
                    {
                        return Under(i.ToString(System.Globalization.CultureInfo.InvariantCulture), inner);
                    }
                }

                return null;
            case JsonValue value when value.TryGetValue(out JsonElement element):
                return Check(element); // read from text: a string's escapes and bytes (JsonValue holds no element of an object or array)
            default:
                return null;
        }
    }

    /// <summary>
    /// Enumerates <paramref name="obj"/>, which an object read from text does only once its members are first read; null
    /// when that succeeds. One that throws is still the text it was read from, and the same checks on that text, under
    /// the object's own comparison of names, find the member at fault.
    /// </summary>
    private static JsonBindingError? Enumerate(JsonObject obj)
    {
        try
        {
            _ = obj.Count;
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            var ignoreCase = obj.Options is { PropertyNameCaseInsensitive: true };
            try
            {
                var text = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(text))
                {
                    obj.WriteTo(writer);
                }

                using var document = JsonDocument.Parse(text.WrittenMemory, new JsonDocumentOptions { MaxDepth = WrittenDepth });
                if (Check(document.RootElement, ignoreCase) is { } found)
                {
                    return found;
                }
            }
            catch (Exception unreadable) when (unreadable is ArgumentException or InvalidOperationException or JsonException)
            {
                // Its text cannot be written either (a name that is not Unicode is unescaped to be written): at the object.
            }

            return new JsonBindingError(string.Empty, $"The members of this object cannot be read: {ExceptionText.Of(ex)}",
                "give each member once, under a name that is valid Unicode (a complete surrogate pair such as \\ud83d\\ude00, or the character directly)");
        }
    }

    /// <summary>How deep the text of an object that failed to enumerate may nest: what a <see cref="Utf8JsonWriter"/> writes by default.</summary>
    private const int WrittenDepth = 1000;

    private static JsonBindingError TooDeep(int maxDepth) =>
        new(string.Empty, $"Objects and arrays nest deeper than {maxDepth} levels here: this one is level {maxDepth + 1}.",
            $"nest objects and arrays at most {maxDepth} levels deep, counting the outermost");

    private const string UnicodeHint = "use a complete surrogate pair such as \\ud83d\\ude00, or write the character directly";

    private const string Utf8Hint = "send the body as UTF-8 (Content-Type: application/json; charset=utf-8)";

    private static JsonBindingError? CheckObject(in JsonElement value, bool ignoreCase)
    {
        var count = 0;
        var escaped = false;
        foreach (var member in value.EnumerateObject())
        {
            count++;

            // Reported at the object: a name that is not text has no pointer.
            if (!Utf8.IsValid(JsonMarshal.GetRawUtf8PropertyName(member)))
            {
                return new JsonBindingError(string.Empty, $"The name of member {count} of this object is not valid UTF-8 text.", Utf8Hint);
            }

            if (Escaped(JsonMarshal.GetRawUtf8PropertyName(member)))
            {
                if (!Readable(member))
                {
                    var written = Encoding.UTF8.GetString(JsonMarshal.GetRawUtf8PropertyName(member));
                    return new JsonBindingError(string.Empty, $"The member name \"{written}\" is not valid Unicode: an escape is an unpaired surrogate.", UnicodeHint);
                }

                escaped = true;
            }
        }

        if (Duplicate(value, count, escaped, ignoreCase) is { } duplicate)
        {
            var message = ignoreCase
                ? $"Member '{duplicate}' is given more than once: the object compares names ignoring case (JsonNodeOptions.PropertyNameCaseInsensitive)."
                : $"Member '{duplicate}' is given more than once.";
            return new JsonBindingError("/" + JsonPointer.Escape(duplicate), message, "give each member once: which of two values would count is not defined");
        }

        foreach (var member in value.EnumerateObject())
        {
            if (Check(member.Value, ignoreCase) is { } inner)
            {
                return Under(member.Name, inner);
            }
        }

        return null;
    }

    /// <summary>The name of the first member that repeats an earlier one, or null.</summary>
    private static string? Duplicate(in JsonElement value, int count, bool escaped, bool ignoreCase)
    {
        if (count < 2)
        {
            return null;
        }

        if (escaped || ignoreCase || count > PairwiseLimit)
        {
            // Escapes compare after unescaping ("a" and "a" are one name); a large object would cost pairs squared.
            var names = new HashSet<string>(count, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!names.Add(member.Name))
                {
                    return member.Name;
                }
            }

            return null;
        }

        var i = 0;
        foreach (var member in value.EnumerateObject())
        {
            var name = JsonMarshal.GetRawUtf8PropertyName(member);
            var j = 0;
            foreach (var earlier in value.EnumerateObject())
            {
                if (j++ == i)
                {
                    break;
                }

                if (name.SequenceEqual(JsonMarshal.GetRawUtf8PropertyName(earlier)))
                {
                    return member.Name;
                }
            }

            i++;
        }

        return null;
    }

    private static JsonBindingError Under(string segment, JsonBindingError inner) =>
        inner with { Pointer = "/" + JsonPointer.Escape(segment) + inner.Pointer };

    private static bool Escaped(ReadOnlySpan<byte> raw) => raw.IndexOf((byte)'\\') >= 0;

    /// <summary>Whether an escaped string reads as Unicode: unescaping an unpaired surrogate throws, in this one call.</summary>
    private static bool Readable(in JsonElement value)
    {
        try
        {
            _ = value.GetString();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Readable(in JsonProperty member)
    {
        try
        {
            _ = member.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
