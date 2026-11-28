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
/// <c>0xFF</c>, which <see cref="JsonDocument"/> keeps until the text is read).
/// </summary>
/// <remarks>
/// Checked once where the request comes in, either is an invalid request at its pointer (RFC 8259 section 4 leaves
/// duplicate names undefined; I-JSON, RFC 7493, forbids both).
/// </remarks>
public static class StrictJson
{
    private const int PairwiseLimit = 16;

    /// <summary>
    /// The first problem in <paramref name="value"/>, at the pointer of the value at fault relative to it (a duplicate member
    /// at its second occurrence); null when there is none.
    /// </summary>
    public static JsonBindingError? Check(in JsonElement value) => Check(value, ignoreCase: false);

    /// <param name="level">The level of <paramref name="value"/>, the root being 1.</param>
    /// <param name="maxDepth">The levels an object or array may be at; the first one deeper is the problem. Text the
    /// reader parsed is bounded by its own limit; an element built in code is not, and is not walked past this.</param>
    private static JsonBindingError? Check(in JsonElement value, bool ignoreCase, int level = 1, int maxDepth = int.MaxValue)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object when level > maxDepth:
            case JsonValueKind.Array when level > maxDepth:
                return TooDeep(maxDepth);
            case JsonValueKind.Object:
                return CheckObject(value, ignoreCase, level, maxDepth);
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (Check(item, ignoreCase, level + 1, maxDepth) is { } inner)
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
    /// A tree nested deeper than <paramref name="maxDepth"/> objects and arrays (the outermost is the first) is
    /// reported at the first one too deep, without reading further.
    /// </summary>
    /// <remarks>
    /// <para>What the check read is what the caller must read from then on, <paramref name="copy"/>: reading the host's
    /// tree again would write its CLR values again.</para>
    /// </remarks>
    /// <param name="copy">When there is no problem, a copy of the tree as checked, which shares no node with it: each
    /// wrapped CLR value is the JSON it wrote here, read back (but a number or string the canonical form reports, and a
    /// <see cref="JsonElement"/>, copied from the element), and every object compares names by ordinal. Null when there is a problem (or <paramref name="node"/> is null).</param>
    /// <returns>The first problem, at its pointer relative to <paramref name="node"/>; null when there is none. The copy
    /// of a tree that has none reads without throwing anything this check looks for.</returns>
    public static JsonBindingError? Check(JsonNode? node, int maxDepth, out JsonNode? copy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        var error = CheckNode(node, 1, maxDepth, out copy);
        if (error is not null)
        {
            copy = null;
        }

        return error;
    }

    private static JsonBindingError? CheckNode(JsonNode? node, int level, int maxDepth, out JsonNode? copy)
    {
        copy = null;
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                if (level > maxDepth)
                {
                    return TooDeep(maxDepth);
                }

                if (Enumerate(obj) is { } unreadable)
                {
                    return unreadable;
                }

                var copied = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    if (CheckNode(value, level + 1, maxDepth, out var member) is { } inner)
                    {
                        return Under(name, inner);
                    }

                    copied.Add(name, member); // unique by ordinal: the object's own comparison found none twice
                }

                copy = copied;
                return null;
            }
            case JsonArray array:
            {
                if (level > maxDepth)
                {
                    return TooDeep(maxDepth);
                }

                var copied = new JsonArray();
                for (var i = 0; i < array.Count; i++)
                {
                    if (CheckNode(array[i], level + 1, maxDepth, out var item) is { } inner)
                    {
                        return Under(i.ToString(System.Globalization.CultureInfo.InvariantCulture), inner);
                    }

                    copied.Add(item);
                }

                copy = copied;
                return null;
            }
            case JsonValue value when value.TryGetValue(out JsonElement element):
                return CheckElement(element, level, maxDepth, out copy);
            case JsonValue value:
                return CheckClrValue(value, level, maxDepth, out copy);
            default:
                throw new ArgumentException($"Unsupported node type {node.GetType().Name}.", nameof(node));
        }
    }

    /// <summary>A host's CLR value is written once, here, and the text it wrote is what is checked and copied: nothing
    /// after the check runs its converter again.</summary>
    private static JsonBindingError? CheckClrValue(JsonValue value, int level, int maxDepth, out JsonNode? copy)
    {
        copy = null;
        if (value.TryGetValue(out string? text) && !IsUnicode(text))
        {
            copy = JsonValue.Create(text); // the writer would put U+FFFD in its place: the canonical form's, at its pointer
            return null;
        }

        JsonNode? read;
        try
        {
            read = JsonNode.Parse(Write(value).Span, documentOptions: new JsonDocumentOptions { MaxDepth = WrittenDepth });
        }
        catch (Exception ex)
        {
            if (JsonNodeUtilities.IsNonFiniteNumber(value, out var number))
            {
                copy = JsonValue.Create(number);
                return null;
            }

            // Writing the host's value runs the host's code (a converter, a getter) on what it holds: whatever that throws
            // is the value's mistake.
            return new JsonBindingError(string.Empty, $"This value cannot be written as JSON: {ExceptionText.Of(ex)}",
                "give a value System.Text.Json can write (no cycle, no NaN or infinity), or build it from JsonObject and JsonArray");
        }

        return CheckNode(read, level, maxDepth, out copy);
    }

    /// <summary>An element is walked and copied as itself, never written by a converter or read back from its text
    /// (which may keep JSONC comments, or be deeper than the reader allows).</summary>
    private static JsonBindingError? CheckElement(JsonElement element, int level, int maxDepth, out JsonNode? copy)
    {
        copy = null;
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return new JsonBindingError(string.Empty, "This value holds no JSON: it wraps a default JsonElement.",
                "wrap an element read from JSON, or leave the member out");
        }

        if (Check(element, ignoreCase: false, level, maxDepth) is { } invalid)
        {
            return invalid;
        }

        copy = Copy(element.Clone());
        return null;
    }

    /// <summary>Copies an element the check passed: its names are text, none twice, and its depth is within the limit.</summary>
    private static JsonNode? Copy(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var member in element.EnumerateObject())
                {
                    obj.Add(member.Name, Copy(member.Value));
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Copy(item));
                }

                return array;
            default:
                return JsonValue.Create(element);
        }
    }

    private static bool IsUnicode(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlyMemory<byte> Write(JsonNode node)
    {
        var text = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(text))
        {
            node.WriteTo(writer);
        }

        return text.WrittenMemory;
    }

    /// <summary>
    /// One that throws is still the text it was read from, and the same checks on that text, under the object's own
    /// comparison of names, find the member at fault.
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
                using var document = JsonDocument.Parse(Write(obj), new JsonDocumentOptions { MaxDepth = WrittenDepth });
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

    /// <summary>How deep the text written here (<see cref="Write"/>) may nest when it is read back: what a <see cref="Utf8JsonWriter"/> writes by default.</summary>
    private const int WrittenDepth = 1000;

    private static JsonBindingError TooDeep(int maxDepth) =>
        new(string.Empty, $"Objects and arrays nest deeper than {maxDepth} levels here: this one is level {maxDepth + 1}.",
            $"nest objects and arrays at most {maxDepth} levels deep, counting the outermost");

    private const string UnicodeHint = "use a complete surrogate pair such as \\ud83d\\ude00, or write the character directly";

    private const string Utf8Hint = "send the body as UTF-8 (Content-Type: application/json; charset=utf-8)";

    private static JsonBindingError? CheckObject(in JsonElement value, bool ignoreCase, int level, int maxDepth)
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
            if (Check(member.Value, ignoreCase, level + 1, maxDepth) is { } inner)
            {
                return Under(member.Name, inner);
            }
        }

        return null;
    }

    private static string? Duplicate(in JsonElement value, int count, bool escaped, bool ignoreCase)
    {
        if (count < 2)
        {
            return null;
        }

        if (escaped || ignoreCase || count > PairwiseLimit)
        {
            // Escapes compare after unescaping ("a" and "a" are one name).
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
