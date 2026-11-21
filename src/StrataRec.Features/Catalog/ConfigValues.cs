using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Features.Catalog;

/// <summary>Converts JSON configuration values (defaults, parameter values, request inputs) to typed scalars.</summary>
/// <remarks>
/// One set of rules and words for every form a value arrives in: a <see cref="JsonElement"/> (a request body read once,
/// without building nodes) is the implementation; a <see cref="JsonNode"/> (configuration) is read through it; a typed
/// <see cref="ScalarValue"/> a host passed is checked with the words the same value would get as JSON. So a request input
/// of the wrong type reads alike whether a host, the CLI or the engine reports it. No input makes these methods throw: a
/// value they cannot represent (a number beyond its type, a string escape that is not Unicode) is an error like any other.
/// One rule tells the two JSON forms apart: configuration is stored canonical (RFC 8785), so an integer it holds must be
/// one its canonical form keeps (<see cref="NotCanonical(in JsonElement, long)"/>); a request is never stored so, and
/// reads <c>9007199254740993.0</c> as the integer written.
/// </remarks>
public static class ConfigValues
{
    /// <summary>
    /// Converts <paramref name="node"/>, a configuration value, to a value of <paramref name="type"/> (promoted storage). An
    /// integer written with a fraction or an exponent beyond ±2^53 whose canonical form (RFC 8785) is another integer is an
    /// error: the configuration is stored in that form, and a plan built from it would read the other integer (review 79).
    /// </summary>
    /// <param name="error">
    /// What is wrong, in the words of the JSON written (<see cref="JsonWords"/>), as the rest of a sentence whose subject
    /// the caller knows: <c>must be an integer (int64), got the string "1001"</c>. Callers write
    /// <c>Input 'user_id' {error}.</c>, <c>The default {error}.</c>
    /// </param>
    public static bool TryToScalar(JsonNode? node, ColumnType type, out ScalarValue value, [NotNullWhen(false)] out string? error)
    {
        if (node is null)
        {
            return TryToScalar(default(JsonElement), type, canonical: true, out value, out error);
        }

        if (!TryAsElement(node, out var element))
        {
            // A parsed string that is not valid Unicode, inside an array or object, which cannot be written to be read: the
            // array's item at fault is named, as for an element (an earlier item of the wrong kind is not, the one case
            // where the two forms differ).
            value = ScalarValue.Missing;
            error = node is JsonArray array && IsArray(type) && Unreadable(array) is >= 0 and var index
                ? Element(type, index, JsonWords.Value(array[index]))
                : Expected(type, JsonWords.Value(node));
            return false;
        }

        return TryToScalar(element, type, canonical: true, out value, out error);
    }

    /// <summary>
    /// Converts <paramref name="element"/>, a request's value, to a value of <paramref name="type"/> (promoted storage); JSON
    /// <c>null</c> and an absent value (<see cref="JsonValueKind.Undefined"/>) are missing. Allocates only the value (a
    /// string, an array).
    /// </summary>
    /// <param name="error">As for <see cref="TryToScalar(JsonNode?, ColumnType, out ScalarValue, out string?)"/>.</param>
    public static bool TryToScalar(in JsonElement element, ColumnType type, out ScalarValue value, [NotNullWhen(false)] out string? error) =>
        TryToScalar(element, type, canonical: false, out value, out error);

    /// <param name="canonical">The value is configuration, stored canonical: an integer must keep its value in that form.</param>
    private static bool TryToScalar(in JsonElement element, ColumnType type, bool canonical, out ScalarValue value, [NotNullWhen(false)] out string? error)
    {
        value = ScalarValue.Missing;
        error = null;
        var kind = element.ValueKind;
        if (kind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (type.Nullable)
            {
                return true;
            }

            error = Expected(type, "null");
            return false;
        }

        switch (type.Kind)
        {
            case StorageKind.Bool when kind is JsonValueKind.True or JsonValueKind.False:
                value = ScalarValue.FromBool(kind == JsonValueKind.True);
                return true;
            case StorageKind.Int32 or StorageKind.Int64 when kind == JsonValueKind.Number:
                if (!TryInteger(element, out var integer))
                {
                    error = JsonIntegers.IsIntegral(element.GetRawText()) ? OutOfRange(type, JsonWords.Value(element), fitsInt64: false)
                        : Expected(type, JsonWords.Value(element));
                    return false;
                }

                if (type.Kind == StorageKind.Int32 && !Int32Range.Fits(integer))
                {
                    error = OutOfRange(type, JsonWords.Value(element), fitsInt64: true);
                    return false;
                }

                if (canonical && NotCanonical(element, integer) is { } stored)
                {
                    error = NotCanonical(type, element.GetRawText(), stored);
                    return false;
                }

                value = ScalarValue.FromInt64(integer);
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when kind == JsonValueKind.Number:
                if (!element.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    error = $"must be {Describe(type)} within the float64 range, got {JsonWords.Value(element)}";
                    return false;
                }

                if (type.Kind == StorageKind.Float32 && !Float32Range.Fits(number))
                {
                    error = FloatOutOfRange(type, JsonWords.Value(element));
                    return false;
                }

                value = ScalarValue.FromFloat64(number);
                return true;
            case StorageKind.String when kind == JsonValueKind.String:
                if (!JsonWords.TryText(element, out var text))
                {
                    error = Expected(type, JsonWords.Value(element));
                    return false;
                }

                value = ScalarValue.FromString(text);
                return true;
            case StorageKind.List when kind == JsonValueKind.Array:
                return TryList(element, type, canonical, out value, out error);
        }

        error = Expected(type, JsonWords.Value(element));
        return false;
    }

    /// <summary>A JSON array as a list of <paramref name="type"/>'s elements, each read as a scalar of the element type
    /// is (an int32 element within int32, a float within float64), and as many as a <c>list&lt;E,N&gt;</c> holds; held
    /// promoted, as every list value is.</summary>
    private static bool TryList(in JsonElement array, ColumnType type, bool canonical, out ScalarValue value, [NotNullWhen(false)] out string? error)
    {
        value = ScalarValue.Missing;
        error = null;
        var count = array.GetArrayLength();
        if (!ColumnValues.FitsLength(count, type))
        {
            error = Expected(type, Elements(count));
            return false;
        }

        var index = 0;
        switch (type.Element)
        {
            case StorageKind.Bool:
                var bools = new bool[count];
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        error = Element(type, index, JsonWords.Value(item));
                        return false;
                    }

                    bools[index++] = item.ValueKind == JsonValueKind.True;
                }

                value = ScalarValue.FromBoolList(bools);
                return true;
            case StorageKind.Int32 or StorageKind.Int64:
                var longs = new long[count];
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !TryInteger(item, out longs[index]))
                    {
                        error = item.ValueKind == JsonValueKind.Number && JsonIntegers.IsIntegral(item.GetRawText())
                            ? OutOfRange(type, JsonWords.Value(item), fitsInt64: false, index)
                            : Element(type, index, JsonWords.Value(item));
                        return false;
                    }

                    if (type.Element == StorageKind.Int32 && !Int32Range.Fits(longs[index]))
                    {
                        error = OutOfRange(type, JsonWords.Value(item), fitsInt64: true, index);
                        return false;
                    }

                    if (canonical && NotCanonical(item, longs[index]) is { } stored)
                    {
                        error = NotCanonical(type, item.GetRawText(), stored, index);
                        return false;
                    }

                    index++;
                }

                value = ScalarValue.FromInt64List(longs);
                return true;
            case StorageKind.Float32 or StorageKind.Float64:
                var doubles = new double[count];
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out doubles[index]) || !double.IsFinite(doubles[index]))
                    {
                        error = item.ValueKind == JsonValueKind.Number
                            ? $"must be {Describe(type)} within the float64 range; element {index} is {JsonWords.Value(item)}"
                            : Element(type, index, JsonWords.Value(item));
                        return false;
                    }

                    if (type.Element == StorageKind.Float32 && !Float32Range.Fits(doubles[index]))
                    {
                        error = FloatOutOfRange(type, JsonWords.Value(item), index);
                        return false;
                    }

                    index++;
                }

                value = ScalarValue.FromFloat64List(doubles);
                return true;
            default:
                var strings = new string[count];
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !JsonWords.TryText(item, out strings[index]))
                    {
                        error = Element(type, index, JsonWords.Value(item));
                        return false;
                    }

                    index++;
                }

                value = ScalarValue.FromStringList(strings);
                return true;
        }
    }

    /// <summary>
    /// Converts a typed value (a request input a host passed as a <see cref="ScalarValue"/>) to <paramref name="type"/>'s
    /// promoted form, as <see cref="ScalarValue.TryConvert"/> does. A value that does not convert is told in the words the
    /// same value would get as JSON (<see cref="TryToScalar(in JsonElement, ColumnType, out ScalarValue, out string?)"/>):
    /// <c>must be an integer (int64), got the string "1001"</c>. A missing value is <c>null</c>.
    /// </summary>
    public static bool TryConvert(in ScalarValue value, ColumnType type, out ScalarValue converted, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (value.TryConvert(type, out converted))
        {
            return true;
        }

        // Only why it failed is worked out here, on the error path: TryConvert decided.
        switch (type.Kind)
        {
            case StorageKind.List when value.Kind == StorageKind.List && !ColumnValues.FitsLength(value.ListLength, type):
                error = Expected(type, Elements(value.ListLength));
                return false;
            case StorageKind.Int32 when value.Kind == StorageKind.Int64:
                error = OutOfRange(type, Words(value), fitsInt64: true);
                return false;
            case StorageKind.Int32 or StorageKind.Int64 when value.Kind == StorageKind.Float64 && Math.Floor(value.AsFloat64()) == value.AsFloat64():
                // An integral double ScalarValue.TryConvert refused: outside the type's range (or int64's).
                error = OutOfRange(type, Words(value), fitsInt64: Math.Abs(value.AsFloat64()) < 9.2e18);
                return false;
            case StorageKind.List when type.Element == StorageKind.Int32 && value.ListElement == StorageKind.Int64:
                var list = value.AsInt64List();
                for (var i = 0; i < list.Length; i++)
                {
                    if (!Int32Range.Fits(list[i]))
                    {
                        error = OutOfRange(type, "the number " + list[i].ToString(CultureInfo.InvariantCulture), fitsInt64: true, i);
                        return false;
                    }
                }

                break;
            case StorageKind.Float32 when value.Kind == StorageKind.Float64:
                error = FloatOutOfRange(type, Words(value));
                return false;
            case StorageKind.List when type.Element == StorageKind.String && value.ListElement == StorageKind.String:
                var strings = value.AsStringList();
                for (var i = 0; i < strings.Length; i++)
                {
                    if (strings[i] is null)
                    {
                        error = Element(type, i, "null: a list's elements are values");
                        return false;
                    }
                }

                break;
            case StorageKind.List when type.Element == StorageKind.Float32 && value.ListElement == StorageKind.Float64:
                var floats = value.AsFloat64List();
                for (var i = 0; i < floats.Length; i++)
                {
                    if (!Float32Range.Fits(floats[i]))
                    {
                        error = FloatOutOfRange(type, "the number " + floats[i].ToString("R", CultureInfo.InvariantCulture), i);
                        return false;
                    }
                }

                break;
        }

        error = Expected(type, Words(value));
        return false;
    }

    public static ScalarValue ToScalar(JsonNode? node, ColumnType type) =>
        TryToScalar(node, type, out var value, out var error) ? value : throw new FormatException("The value " + error + ".");

    /// <summary>What a value of <paramref name="type"/> is written as in JSON, e.g. <c>an integer (int64)</c>.</summary>
    public static string Describe(ColumnType type)
    {
        var name = type.AsNonNullable().ToString();
        var shape = type.Kind switch
        {
            StorageKind.Bool => "true or false",
            StorageKind.Int32 or StorageKind.Int64 => "an integer",
            StorageKind.Float32 or StorageKind.Float64 => "a number",
            StorageKind.String => "a string",
            StorageKind.List => (type.IsFixedLength ? $"an array of {type.FixedLength} " : "an array of ") + type.Element switch
            {
                StorageKind.Bool => type.IsFixedLength ? "true/false values" : "true or false",
                StorageKind.Int32 or StorageKind.Int64 => "integers",
                StorageKind.Float32 or StorageKind.Float64 => "numbers",
                _ => "strings",
            },
            _ => "a value",
        };
        return type.Kind == StorageKind.String ? shape : $"{shape} ({name})";
    }

    private static string Expected(ColumnType type, string got) => $"must be {Describe(type)}, got {got}";

    private static string Element(ColumnType type, int index, string got) => $"must be {Describe(type)}; element {index} is {got}";

    /// <summary>
    /// An integer outside the range of <paramref name="type"/> (of its elements, with <paramref name="element"/>); for int32
    /// storage of a value int64 holds, with the fix of declaring the int64 form. The larger type keeps the nullability
    /// written, so the suggestion can be pasted as it is: <c>int64</c> for an <c>int32?</c> input would make it required.
    /// </summary>
    private static string OutOfRange(ColumnType type, string got, bool fitsInt64, int element = -1)
    {
        var int32 = type.IsInt32Storage;
        var range = int32 ? $"the int32 range [{int.MinValue}, {int.MaxValue}]" : $"the int64 range [{long.MinValue}, {long.MaxValue}]";
        var fix = int32 && fitsInt64 ? $"; declare the type as {type.Promoted()} for larger values" : string.Empty;
        return element < 0
            ? $"must be within {range}, got {got}{fix}"
            : $"must be an array of integers within {range}; element {element} is {got}{fix}";
    }

    /// <summary>A finite number beyond float32 for float32 storage, which would hold it as an infinity (<see cref="Float32Range"/>).</summary>
    private static string FloatOutOfRange(ColumnType type, string got, int element = -1)
    {
        var fix = $"; declare the type as {type.Promoted()} for larger values";
        return element < 0
            ? $"must be within the float32 range {Float32Range.Text}, got {got}{fix}"
            : $"must be an array of numbers within the float32 range {Float32Range.Text}; element {element} is {got}{fix}";
    }

    private static bool IsArray(ColumnType type) => type.Kind == StorageKind.List;

    private static string Elements(int count) => count == 1 ? "1 element" : $"{count} elements";

    /// <summary>
    /// The index of the first item of <paramref name="array"/> that cannot be read: a string that is not valid Unicode (a
    /// parsed value holds its element unread), or a container holding one; -1 when there is none.
    /// </summary>
    private static int Unreadable(JsonArray array)
    {
        for (var i = 0; i < array.Count; i++)
        {
            var unreadable = array[i] switch
            {
                JsonValue value => value.GetValueKind() == JsonValueKind.String && !JsonWords.TryText(value, out _),
                { } container => !TryAsElement(container, out _),
                null => false,
            };
            if (unreadable)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A typed value as the JSON that would carry it is told (<see cref="JsonWords.Value(in JsonElement)"/>).</summary>
    private static string Words(in ScalarValue value) => value.Kind switch
    {
        0 => "null",
        StorageKind.Bool => value.AsBool() ? "true" : "false",
        StorageKind.Int64 => "the number " + value.AsInt64().ToString(CultureInfo.InvariantCulture),
        StorageKind.Float64 => "the number " + value.AsFloat64().ToString("R", CultureInfo.InvariantCulture),
        StorageKind.String => "the string " + JsonWords.Quote(value.AsString()),
        _ => "an array",
    };

    /// <summary>
    /// The element a node holds: the parsed one when it has one (no copy), otherwise the node serialized, which only
    /// configuration built in code (and arrays, which are nodes of their own) pays for, at load. False for a node that
    /// cannot be written as JSON: one holding a parsed string whose escape is an unpaired surrogate, which cannot be read
    /// to be written (the serializer may wrap that error). Nothing else in a node makes it throw.
    /// </summary>
    private static bool TryAsElement(JsonNode node, out JsonElement element)
    {
        if (node is JsonValue value && value.TryGetValue(out element))
        {
            return true;
        }

        try
        {
            element = JsonSerializer.SerializeToElement(node);
            return true;
        }
        catch (Exception ex) when (ex is JsonException { InnerException: InvalidOperationException } or InvalidOperationException)
        {
            element = default;
            return false;
        }
    }

    /// <summary>
    /// The integer a JSON number denotes (<c>3</c>, <c>3.0</c>, <c>3e2</c>), exactly (<see cref="JsonIntegers"/>): never
    /// through a double, which would round <c>9007199254740993.0</c> and accept <c>9007199254740992.5</c>. Plain integer
    /// text is read without allocating.
    /// </summary>
    private static bool TryInteger(in JsonElement number, out long integer) =>
        number.TryGetInt64(out integer) || JsonIntegers.TryParse(number.GetRawText(), out integer);

    /// <summary>
    /// The canonical form (RFC 8785) of an integer written with a fraction or an exponent (<c>1152921504606846976.0</c>)
    /// when that form is a different integer; null otherwise. Configuration is stored canonical, where such a number is a
    /// double: past ±2^53 its shortest digits can be another integer (2^60 becomes <c>1152921504606847000</c>), so a plan
    /// run from the workspace and the same plan run from its manifest bound different values, and the effective plan
    /// <c>diff</c> canonicalizes failed (review 79). The canonicalizer refuses such an integer written as plain digits
    /// (<see cref="JsonCanonicalizer"/>, when an artifact or an override is hashed), but cannot tell <c>….0</c> from a
    /// float; the binding can. One whose canonical form is itself (<c>1e18</c>) is kept, as plain digits are. Allocates
    /// only for such a number past 2^53.
    /// </summary>
    private static string? NotCanonical(in JsonElement number, long integer)
    {
        const long TwoPow53 = 1L << 53;
        if (integer is >= -TwoPow53 and <= TwoPow53 || JsonMarshal.GetRawUtf8Value(number).IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0)
        {
            return null; // up to 2^53 every integer is a double, written as its digits
        }

        var canonical = JsonCanonicalizer.FormatNumber(number.GetDouble());
        return JsonIntegers.TryParse(canonical, out var stored) && stored == integer ? null : canonical;
    }

    /// <summary>An integer its canonical form would change (<see cref="NotCanonical(in JsonElement, long)"/>), in the canonicalizer's words.</summary>
    private static string NotCanonical(ColumnType type, string literal, string canonical, int element = -1)
    {
        var got = $"the number {literal}, whose canonical form would be {canonical}, a different integer; " +
                  "write integers beyond ±2^53 (9007199254740992) as strings, of a string type";
        return element < 0
            ? $"must be {Describe(type)} that RFC 8785 represents exactly, got {got}"
            : $"must be {Describe(type)} that RFC 8785 represents exactly; element {element} is {got}";
    }
}
