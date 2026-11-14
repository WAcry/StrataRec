using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// What <see cref="Jsonc"/> rejects in a configuration file, for a value System.Text.Json has already parsed (a request
/// body read with <see cref="JsonDocument"/>): a member given twice in one object, and a name or string whose escapes
/// are not Unicode (an unpaired surrogate such as <c>\uD800</c>).
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
    public static JsonBindingError? Check(in JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                return CheckObject(value);
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (Check(item) is { } inner)
                    {
                        return Under(index.ToString(System.Globalization.CultureInfo.InvariantCulture), inner);
                    }

                    index++;
                }

                return null;
            case JsonValueKind.String when Escaped(JsonMarshal.GetRawUtf8Value(value)) && !Readable(value):
                return new JsonBindingError(string.Empty, $"The string {value.GetRawText()} is not valid Unicode: an escape is an unpaired surrogate.", UnicodeHint);
            default:
                return null;
        }
    }

    private const string UnicodeHint = "use a complete surrogate pair such as \\ud83d\\ude00, or write the character directly";

    private static JsonBindingError? CheckObject(in JsonElement value)
    {
        var count = 0;
        var escaped = false;
        foreach (var member in value.EnumerateObject())
        {
            count++;
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

        if (Duplicate(value, count, escaped) is { } duplicate)
        {
            return new JsonBindingError("/" + JsonPointer.Escape(duplicate), $"Member '{duplicate}' is given more than once.",
                "give each member once: which of two values would count is not defined");
        }

        foreach (var member in value.EnumerateObject())
        {
            if (Check(member.Value) is { } inner)
            {
                return Under(member.Name, inner);
            }
        }

        return null;
    }

    /// <summary>The name of the first member that repeats an earlier one, or null.</summary>
    private static string? Duplicate(in JsonElement value, int count, bool escaped)
    {
        if (count < 2)
        {
            return null;
        }

        if (escaped || count > PairwiseLimit)
        {
            // Escapes compare after unescaping ("a" and "a" are one name); a large object would cost pairs squared.
            var names = new HashSet<string>(count, StringComparer.Ordinal);
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
