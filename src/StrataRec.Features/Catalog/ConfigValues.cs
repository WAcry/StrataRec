using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Features.Catalog;

/// <summary>Converts JSON configuration values (defaults, parameter values, request inputs) to typed scalars.</summary>
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
        value = ScalarValue.Missing;
        error = null;
        if (node is null)
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
            case StorageKind.Bool when node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                value = ScalarValue.FromBool(v.GetValue<bool>());
                return true;
            case StorageKind.Int32 or StorageKind.Int64 when node is JsonValue v && v.GetValueKind() == JsonValueKind.Number:
                if (!JsonIntegers.TryGetInt64(node, out var integer))
                {
                    error = JsonIntegers.IsIntegral(v.ToJsonString()) ? OutOfRange(type, node) : Expected(type, JsonWords.Value(node));
                    return false;
                }

                if (type.Kind == StorageKind.Int32 && !Int32Range.Fits(integer))
                {
                    error = OutOfRange(type, node);
                    return false;
                }

                value = ScalarValue.FromInt64(integer);
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when node is JsonValue v && v.GetValueKind() == JsonValueKind.Number:
                value = ScalarValue.FromFloat64(v.GetValue<double>());
                return true;
            case StorageKind.String or StorageKind.Text when node is JsonValue v && v.GetValueKind() == JsonValueKind.String:
                if (!JsonWords.TryText(v, out var text))
                {
                    error = Expected(type, JsonWords.Value(node));
                    return false;
                }

                value = type.Kind == StorageKind.String ? ScalarValue.FromString(text) : ScalarValue.FromText(text);
                return true;
            case StorageKind.Vector when node is JsonArray a:
                if (type.Dimension > 0 && a.Count != type.Dimension)
                {
                    error = Expected(type, a.Count == 1 ? "1 element" : $"{a.Count} elements");
                    return false;
                }

                var vec = new float[a.Count];
                for (var i = 0; i < vec.Length; i++)
                {
                    if (a[i] is not JsonValue e || e.GetValueKind() != JsonValueKind.Number)
                    {
                        error = Element(type, i, a[i]);
                        return false;
                    }

                    vec[i] = (float)e.GetValue<double>();
                }

                value = ScalarValue.FromVector(vec);
                return true;
            case StorageKind.ListInt64 or StorageKind.ListInt32 when node is JsonArray a:
                // A list<int32> value is held promoted, as int64 elements (like any int32 value); each element must
                // fit the storage it will be written to.
                var longs = new long[a.Count];
                for (var i = 0; i < longs.Length; i++)
                {
                    if (!JsonIntegers.TryGetInt64(a[i], out longs[i]))
                    {
                        error = a[i] is JsonValue e && e.GetValueKind() == JsonValueKind.Number && JsonIntegers.IsIntegral(e.ToJsonString())
                            ? OutOfRange(type, a[i], i)
                            : Element(type, i, a[i]);
                        return false;
                    }

                    if (type.Kind == StorageKind.ListInt32 && !Int32Range.Fits(longs[i]))
                    {
                        error = OutOfRange(type, a[i], i);
                        return false;
                    }
                }

                value = ScalarValue.FromInt64List(longs);
                return true;
            case StorageKind.ListString when node is JsonArray a:
                var strings = new string[a.Count];
                for (var i = 0; i < strings.Length; i++)
                {
                    if (a[i] is not JsonValue e || e.GetValueKind() != JsonValueKind.String || !JsonWords.TryText(e, out strings[i]))
                    {
                        error = Element(type, i, a[i]);
                        return false;
                    }
                }

                value = ScalarValue.FromStringList(strings);
                return true;
        }

        error = Expected(type, JsonWords.Value(node));
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

    private static string Element(ColumnType type, int index, JsonNode? element) =>
        $"must be {Describe(type)}; element {index} is {JsonWords.Value(element)}";

    /// <summary>
    /// The message for an integer outside the range of <paramref name="type"/> (of its elements, with
    /// <paramref name="element"/>); for int32 storage, with the fix of declaring the int64 form.
    /// </summary>
    private static string OutOfRange(ColumnType type, JsonNode? value, int element = -1)
    {
        var int32 = type.Kind is StorageKind.Int32 or StorageKind.ListInt32;
        var range = int32 ? $"the int32 range [{int.MinValue}, {int.MaxValue}]" : $"the int64 range [{long.MinValue}, {long.MaxValue}]";
        var fits64 = JsonIntegers.TryGetInt64(value, out _);
        var fix = int32 && fits64 ? $"; declare the type as {type.Promoted().AsNonNullable()} for larger values" : string.Empty;
        return element < 0
            ? $"must be within {range}, got {JsonWords.Value(value)}{fix}"
            : $"must be an array of integers within {range}; element {element} is {JsonWords.Value(value)}{fix}";
    }
}
