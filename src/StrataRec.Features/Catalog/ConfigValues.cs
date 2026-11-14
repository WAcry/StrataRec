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
            case StorageKind.Int32 or StorageKind.Int64 when TryInteger(node, out var integer):
                if (type.Kind == StorageKind.Int32 && !Int32Range.Fits(integer))
                {
                    error = $"must be within the int32 range [{int.MinValue}, {int.MaxValue}], got {JsonWords.Value(node)}; " +
                            $"declare the type as {type.Promoted().AsNonNullable()} for larger values";
                    return false;
                }

                value = ScalarValue.FromInt64(integer);
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when node is JsonValue v && v.GetValueKind() == JsonValueKind.Number:
                value = ScalarValue.FromFloat64(v.GetValue<double>());
                return true;
            case StorageKind.String when node is JsonValue v && v.GetValueKind() == JsonValueKind.String:
                value = ScalarValue.FromString(v.GetValue<string>());
                return true;
            case StorageKind.Text when node is JsonValue v && v.GetValueKind() == JsonValueKind.String:
                value = ScalarValue.FromText(v.GetValue<string>());
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
                    if (!TryInteger(a[i], out longs[i]))
                    {
                        error = Element(type, i, a[i]);
                        return false;
                    }

                    if (type.Kind == StorageKind.ListInt32 && !Int32Range.Fits(longs[i]))
                    {
                        error = $"must be an array of integers within the int32 range [{int.MinValue}, {int.MaxValue}]; element {i} is " +
                                $"{JsonWords.Value(a[i])}; declare the type as {type.Promoted().AsNonNullable()} for larger values";
                        return false;
                    }
                }

                value = ScalarValue.FromInt64List(longs);
                return true;
            case StorageKind.ListString when node is JsonArray a:
                var strings = new string[a.Count];
                for (var i = 0; i < strings.Length; i++)
                {
                    if (a[i] is not JsonValue e || e.GetValueKind() != JsonValueKind.String)
                    {
                        error = Element(type, i, a[i]);
                        return false;
                    }

                    strings[i] = e.GetValue<string>();
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

    /// <summary>A JSON number with no fraction (<c>3</c>, <c>3.0</c>, <c>3e2</c>) that a long holds.</summary>
    private static bool TryInteger(JsonNode? node, out long integer)
    {
        integer = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (v.TryGetValue(out integer))
        {
            return true;
        }

        var d = v.GetValue<double>();
        if (Math.Floor(d) == d && Math.Abs(d) < 9.2e18)
        {
            integer = (long)d;
            return true;
        }

        return false;
    }
}
