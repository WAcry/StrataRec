using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                    if (v.TryGetValue<long>(out var l))
                    {
                        value = ScalarValue.FromInt64(l);
                        return true;
                    }

                    var d0 = v.GetValue<double>();
                    if (Math.Floor(d0) == d0 && Math.Abs(d0) < 9.2e18)
                    {
                        value = ScalarValue.FromInt64((long)d0);
                        return true;
                    }

                    break;
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
                    var vec = a.Select(e => (float)e!.GetValue<double>()).ToArray();
                    if (type.Dimension > 0 && vec.Length != type.Dimension)
                    {
                        error = $"expected {type.Dimension} elements, got {vec.Length}";
                        return false;
                    }

                    value = ScalarValue.FromVector(vec);
                    return true;
                case StorageKind.ListInt64 when node is JsonArray a:
                    value = ScalarValue.FromInt64List(a.Select(e => e!.GetValue<long>()).ToArray());
                    return true;
                case StorageKind.ListString when node is JsonArray a:
                    value = ScalarValue.FromStringList(a.Select(e => e!.GetValue<string>()).ToArray());
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
}
