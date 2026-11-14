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
            value = ScalarValue.Missing;
            error = Expected(type, "a value holding " + NotUnicode);
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
                switch (Integer(element, out var integer))
                {
                    case Exactness.NotInteger:
                        error = Expected(type, JsonWords.Value(element));
                        return false;
                    case Exactness.OutsideInt64:
                        error = OutsideInt64(JsonWords.Value(element));
                        return false;
                }

                if (type.Kind == StorageKind.Int32 && !Int32Range.Fits(integer))
                {
                    error = OutsideInt32(type, JsonWords.Value(element));
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
                if (!TryGetString(element, out var text))
                {
                    error = Expected(type, NotUnicode);
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
                        error = Element(type, v, component);
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
                    var exactness = item.ValueKind == JsonValueKind.Number ? Integer(item, out longs[l]) : Exactness.NotInteger;
                    if (exactness == Exactness.NotInteger)
                    {
                        error = Element(type, l, item);
                        return false;
                    }

                    if (exactness == Exactness.OutsideInt64)
                    {
                        error = $"must be an array of integers within the int64 range [{long.MinValue}, {long.MaxValue}]; element {l} is {JsonWords.Value(item)}";
                        return false;
                    }

                    if (type.Kind == StorageKind.ListInt32 && !Int32Range.Fits(longs[l]))
                    {
                        error = ElementOutsideInt32(type, l, JsonWords.Value(item));
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
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        error = Element(type, s, item);
                        return false;
                    }

                    if (!TryGetString(item, out var entry))
                    {
                        error = $"must be {Describe(type)}; element {s} is {NotUnicode}";
                        return false;
                    }

                    strings[s++] = entry;
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
            case StorageKind.Int32 when value.Kind == StorageKind.Int64 || (value.Kind == StorageKind.Float64 && Math.Floor(value.AsFloat64()) == value.AsFloat64()):
                error = OutsideInt32(type, Words(value));
                return false;
            case StorageKind.ListInt32 when value.Kind == StorageKind.ListInt64:
                var list = value.AsInt64List();
                for (var i = 0; i < list.Length; i++)
                {
                    if (!Int32Range.Fits(list[i]))
                    {
                        error = ElementOutsideInt32(type, i, "the number " + list[i].ToString(CultureInfo.InvariantCulture));
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

    private const string NotUnicode = "a string that is not valid Unicode (an unpaired surrogate escape)";

    private static string Expected(ColumnType type, string got) => $"must be {Describe(type)}, got {got}";

    private static string Element(ColumnType type, int index, in JsonElement element) =>
        $"must be {Describe(type)}; element {index} is {JsonWords.Value(element)}";

    /// <summary>The larger type keeps the nullability written, so the suggestion can be pasted as it is.</summary>
    private static string OutsideInt32(ColumnType type, string got) =>
        $"must be within the int32 range [{int.MinValue}, {int.MaxValue}], got {got}; declare the type as {type.Promoted()} for larger values";

    private static string OutsideInt64(string got) => $"must be within the int64 range [{long.MinValue}, {long.MaxValue}], got {got}";

    private static string ElementOutsideInt32(ColumnType type, int index, string got) =>
        $"must be an array of integers within the int32 range [{int.MinValue}, {int.MaxValue}]; element {index} is {got}; " +
        $"declare the type as {type.Promoted()} for larger values";

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
    /// A string value, or false for one whose escapes produce an unpaired surrogate: System.Text.Json parses such a string
    /// and throws only when it is read, an <see cref="InvalidOperationException"/> of that one call.
    /// </summary>
    private static bool TryGetString(in JsonElement element, [NotNullWhen(true)] out string? text)
    {
        try
        {
            text = element.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            text = null;
            return false;
        }
    }

    /// <summary>
    /// The element a node holds: the parsed one when it has one (no copy), otherwise the node serialized, which only
    /// configuration built in code (and arrays, which are nodes of their own) pays for, at load. False for a node that
    /// cannot be written as JSON: one holding a parsed string whose escape is an unpaired surrogate, which cannot be read
    /// to be written (the serializer wraps that error, or not). Nothing else in a node makes it throw.
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
        catch (Exception ex) when (ex is JsonException { InnerException: InvalidOperationException or ArgumentException } or InvalidOperationException or ArgumentException)
        {
            element = default;
            return false;
        }
    }

    private enum Exactness
    {
        Integer,
        NotInteger,
        OutsideInt64,
    }

    /// <summary>
    /// The integer a JSON number denotes (<c>3</c>, <c>3.0</c>, <c>3e2</c>, <c>30e-1</c>), decided by its digits: a number
    /// is converted only when its exact value is an integer a long holds. Reading it as a double first would round
    /// <c>9007199254740993.0</c> to <c>9007199254740992</c> and accept <c>9007199254740992.5</c> as an integer: ids and
    /// histories would change silently.
    /// </summary>
    private static Exactness Integer(in JsonElement number, out long integer) =>
        number.TryGetInt64(out integer) ? Exactness.Integer : ExactInteger(JsonMarshal.GetRawUtf8Value(number), out integer);

    /// <summary>
    /// Parses a JSON number token (RFC 8259 grammar, already validated by the reader) as an exact integer: the mantissa's
    /// digits times a power of ten, an integer when no nonzero digit falls after the point.
    /// </summary>
    private static Exactness ExactInteger(ReadOnlySpan<byte> token, out long integer)
    {
        integer = 0;
        var i = 0;
        var negative = token[0] == (byte)'-';
        if (negative)
        {
            i++;
        }

        var integerStart = i;
        while (i < token.Length && char.IsAsciiDigit((char)token[i]))
        {
            i++;
        }

        var integerDigits = token[integerStart..i];
        var fractionDigits = ReadOnlySpan<byte>.Empty;
        if (i < token.Length && token[i] == (byte)'.')
        {
            var fractionStart = ++i;
            while (i < token.Length && char.IsAsciiDigit((char)token[i]))
            {
                i++;
            }

            fractionDigits = token[fractionStart..i];
        }

        long exponent = 0;
        if (i < token.Length && (token[i] | 0x20) == (byte)'e')
        {
            i++;
            var exponentNegative = token[i] == (byte)'-';
            if (token[i] is (byte)'-' or (byte)'+')
            {
                i++;
            }

            for (; i < token.Length; i++)
            {
                exponent = Math.Min((exponent * 10) + (token[i] - '0'), 1_000_000); // saturates: far beyond any long either way
            }

            exponent = exponentNegative ? -exponent : exponent;
        }

        // The mantissa's digits without leading or trailing zeros; each trailing zero raises the power of ten.
        var digits = integerDigits.Length + fractionDigits.Length;
        var first = 0;
        while (first < digits && Digit(integerDigits, fractionDigits, first) == 0)
        {
            first++;
        }

        if (first == digits)
        {
            return Exactness.Integer; // zero, however written
        }

        var last = digits - 1;
        while (Digit(integerDigits, fractionDigits, last) == 0)
        {
            last--;
        }

        var scale = exponent - fractionDigits.Length + (digits - 1 - last);
        if (scale < 0)
        {
            return Exactness.NotInteger; // a nonzero digit after the point
        }

        if (last - first + 1 + scale > 19)
        {
            return Exactness.OutsideInt64; // twenty digits or more
        }

        ulong magnitude = 0;
        for (var d = first; d <= last; d++)
        {
            magnitude = (magnitude * 10) + (ulong)Digit(integerDigits, fractionDigits, d);
        }

        for (var s = 0L; s < scale; s++)
        {
            magnitude *= 10; // at most 19 digits: no overflow of a ulong
        }

        if (magnitude > (negative ? (ulong)long.MaxValue + 1 : long.MaxValue))
        {
            return Exactness.OutsideInt64;
        }

        integer = negative ? (long)(0 - magnitude) : (long)magnitude;
        return Exactness.Integer;

        static int Digit(ReadOnlySpan<byte> integerPart, ReadOnlySpan<byte> fractionPart, int index) =>
            (index < integerPart.Length ? integerPart[index] : fractionPart[index - integerPart.Length]) - '0';
    }
}
