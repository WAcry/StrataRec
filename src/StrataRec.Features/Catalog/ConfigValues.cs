using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Features.Catalog;

/// <summary>Converts JSON configuration values (defaults, parameter values, request inputs) to typed scalars.</summary>
public static class ConfigValues
{
    /// <summary>Converts <paramref name="node"/> to a value of <paramref name="type"/> (promoted storage).</summary>
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

            error = $"null is not a valid {type}";
            return false;
        }

        try
        {
            switch (type.Kind)
            {
                case StorageKind.Bool when node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                    value = ScalarValue.FromBool(v.GetValue<bool>());
                    return true;
                case StorageKind.Int32 or StorageKind.Int64 when node is JsonValue v && v.GetValueKind() == JsonValueKind.Number:
                    long integer;
                    if (!v.TryGetValue(out integer))
                    {
                        var d0 = v.GetValue<double>();
                        if (!(Math.Floor(d0) == d0 && Math.Abs(d0) < 9.2e18))
                        {
                            break;
                        }

                        integer = (long)d0;
                    }

                    if (type.Kind == StorageKind.Int32 && !Int32Range.Fits(integer))
                    {
                        error = OutsideInt32(node, type);
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
                    var vec = new float[a.Count];
                    for (var i = 0; i < vec.Length; i++)
                    {
                        vec[i] = (float)a[i]!.GetValue<double>();
                    }

                    if (type.Dimension > 0 && vec.Length != type.Dimension)
                    {
                        error = $"expected {type.Dimension} elements, got {vec.Length}";
                        return false;
                    }

                    value = ScalarValue.FromVector(vec);
                    return true;
                case StorageKind.ListInt64 or StorageKind.ListInt32 when node is JsonArray a:
                    // A list<int32> value is held promoted, as int64 elements (like any int32 value); each element must
                    // fit the storage it will be written to.
                    var longs = new long[a.Count];
                    for (var i = 0; i < longs.Length; i++)
                    {
                        longs[i] = a[i]!.GetValue<long>();
                        if (type.Kind == StorageKind.ListInt32 && !Int32Range.Fits(longs[i]))
                        {
                            error = OutsideInt32(node, type, i);
                            return false;
                        }
                    }

                    value = ScalarValue.FromInt64List(longs);
                    return true;
                case StorageKind.ListString when node is JsonArray a:
                    var strings = new string[a.Count];
                    for (var i = 0; i < strings.Length; i++)
                    {
                        strings[i] = a[i]!.GetValue<string>();
                    }

                    value = ScalarValue.FromStringList(strings);
                    return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            error = $"{node.ToJsonString()} is not a valid {type}: {ex.Message}";
            return false;
        }

        error = $"{node.ToJsonString()} is not a valid {type}";
        return false;
    }

    public static ScalarValue ToScalar(JsonNode? node, ColumnType type) =>
        TryToScalar(node, type, out var value, out var error) ? value : throw new FormatException(error);

    /// <summary>The message for an integer that int32 storage cannot hold; callers prefix the JSON pointer.</summary>
    private static string OutsideInt32(JsonNode node, ColumnType type, int element = -1) =>
        (element < 0
            ? $"{node.ToJsonString()} is outside the int32 range [{int.MinValue}, {int.MaxValue}] of {type}"
            : $"element {element} of {node.ToJsonString()} is outside the int32 range [{int.MinValue}, {int.MaxValue}] of {type}") +
        $"; use a value in range or declare the type as {type.Promoted()}";
}
