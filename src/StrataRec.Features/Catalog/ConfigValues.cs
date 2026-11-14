using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
/// </remarks>
public static class ConfigValues
{
    /// <summary>Converts <paramref name="node"/> to a value of <paramref name="type"/> (promoted storage).</summary>
    /// <param name="error">
    /// What is wrong, in the words of the JSON written (<see cref="JsonWords"/>), as the rest of a sentence whose subject
    /// the caller knows: <c>must be an integer (int64), got the string "1001"</c>. Callers write
    /// <c>Input 'user_id' {error}.</c>, <c>The default {error}.</c>
    /// </param>
    public static bool TryToScalar(JsonNode? node, ColumnType type, out ScalarValue value, [NotNullWhen(false)] out string? error)
    {
        if (node is null)
        {
            return TryToScalar(default(JsonElement), type, out value, out error);
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

        return TryToScalar(element, type, out value, out error);
    }

    /// <summary>
    /// Converts <paramref name="element"/> to a value of <paramref name="type"/> (promoted storage); JSON <c>null</c> and an
    /// absent value (<see cref="JsonValueKind.Undefined"/>) are missing. Allocates only the value (a string, an array).
    /// </summary>
    /// <param name="error">As for <see cref="TryToScalar(JsonNode?, ColumnType, out ScalarValue, out string?)"/>.</param>
    public static bool TryToScalar(in JsonElement element, ColumnType type, out ScalarValue value, [NotNullWhen(false)] out string? error)
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

                value = ScalarValue.FromInt64(integer);
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when kind == JsonValueKind.Number:
                if (!element.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    error = $"must be {Describe(type)} within the float64 range, got {JsonWords.Value(element)}";
                    return false;
                }

                value = ScalarValue.FromFloat64(number);
                return true;
            case StorageKind.String or StorageKind.Text when kind == JsonValueKind.String:
                if (!JsonWords.TryText(element, out var text))
                {
                    error = Expected(type, JsonWords.Value(element));
                    return false;
                }

                value = type.Kind == StorageKind.String ? ScalarValue.FromString(text) : ScalarValue.FromText(text);
                return true;
            case StorageKind.Vector when kind == JsonValueKind.Array:
                var length = element.GetArrayLength();
                if (type.Dimension > 0 && length != type.Dimension)
                {
                    error = Expected(type, length == 1 ? "1 element" : $"{length} elements");
                    return false;
                }

                var vector = new float[length];
                var v = 0;
                foreach (var component in element.EnumerateArray())
                {
                    // A component beyond float32 would be stored as an infinity: not a number the request gave.
                    if (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out var d) || !float.IsFinite((float)d))
                    {
                        error = Element(type, v, JsonWords.Value(component));
                        return false;
                    }

                    vector[v++] = (float)d;
                }

                value = ScalarValue.FromVector(vector);
                return true;
            case StorageKind.ListInt64 or StorageKind.ListInt32 when kind == JsonValueKind.Array:
                // A list<int32> value is held promoted, as int64 elements (like any int32 value); each element must
                // fit the storage it will be written to.
                var longs = new long[element.GetArrayLength()];
                var l = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !TryInteger(item, out longs[l]))
                    {
                        error = item.ValueKind == JsonValueKind.Number && JsonIntegers.IsIntegral(item.GetRawText())
                            ? OutOfRange(type, JsonWords.Value(item), fitsInt64: false, l)
                            : Element(type, l, JsonWords.Value(item));
                        return false;
                    }

                    if (type.Kind == StorageKind.ListInt32 && !Int32Range.Fits(longs[l]))
                    {
                        error = OutOfRange(type, JsonWords.Value(item), fitsInt64: true, l);
                        return false;
                    }

                    l++;
                }

                value = ScalarValue.FromInt64List(longs);
                return true;
            case StorageKind.ListString when kind == JsonValueKind.Array:
                var strings = new string[element.GetArrayLength()];
                var s = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !JsonWords.TryText(item, out strings[s]))
                    {
                        error = Element(type, s, JsonWords.Value(item));
                        return false;
                    }

                    s++;
                }

                value = ScalarValue.FromStringList(strings);
                return true;
        }

        error = Expected(type, JsonWords.Value(element));
        return false;
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
            case StorageKind.Int32 when value.Kind == StorageKind.Int64:
                error = OutOfRange(type, Words(value), fitsInt64: true);
                return false;
            case StorageKind.Int32 or StorageKind.Int64 when value.Kind == StorageKind.Float64 && Math.Floor(value.AsFloat64()) == value.AsFloat64():
                // An integral double ScalarValue.TryConvert refused: outside the type's range (or int64's).
                error = OutOfRange(type, Words(value), fitsInt64: Math.Abs(value.AsFloat64()) < 9.2e18);
                return false;
            case StorageKind.ListInt32 when value.Kind == StorageKind.ListInt64:
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
            case StorageKind.Vector when value.Kind == StorageKind.Vector:
                var length = value.AsVector().Length;
                error = Expected(type, length == 1 ? "1 element" : $"{length} elements");
                return false;
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
            StorageKind.Text => "a string",
            StorageKind.Vector => type.Dimension > 0 ? $"an array of {type.Dimension} numbers" : "an array of numbers",
            StorageKind.ListInt32 or StorageKind.ListInt64 => "an array of integers",
            StorageKind.ListString => "an array of strings",
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
        var int32 = type.Kind is StorageKind.Int32 or StorageKind.ListInt32;
        var range = int32 ? $"the int32 range [{int.MinValue}, {int.MaxValue}]" : $"the int64 range [{long.MinValue}, {long.MaxValue}]";
        var fix = int32 && fitsInt64 ? $"; declare the type as {type.Promoted()} for larger values" : string.Empty;
        return element < 0
            ? $"must be within {range}, got {got}{fix}"
            : $"must be an array of integers within {range}; element {element} is {got}{fix}";
    }

    private static bool IsArray(ColumnType type) =>
        type.Kind is StorageKind.Vector or StorageKind.ListInt32 or StorageKind.ListInt64 or StorageKind.ListString;

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
        StorageKind.String or StorageKind.Text => "the string " + JsonWords.Quote(value.AsString()),
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
}
