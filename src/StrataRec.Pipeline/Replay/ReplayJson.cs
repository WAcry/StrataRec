using System.Buffers;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Logging;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Replay;

internal static class ReplayTiming
{
    public const string Ran = "ran";

    public const string DeadlineExceeded = "deadline_exceeded";

    public const string TimedOut = "timed_out";
}

/// <summary>
/// A value is always read with the type recorded next to it. Readers throw <see cref="ReplayBundleException"/> with the
/// pointer of the bad value.
/// </summary>
internal static class ReplayJson
{
    public static ColumnType TypeOf(in ScalarValue value) => value.Kind switch
    {
        StorageKind.Bool => ColumnType.Bool,
        StorageKind.Int64 => ColumnType.Int64,
        StorageKind.Float64 => ColumnType.Float64,
        StorageKind.List => ColumnType.List(value.ListElement),
        _ => ColumnType.String,
    };

    public static JsonElement Value(in ScalarValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            LogBatchJson.WriteValue(json, value, nonFiniteAsStrings: true);
        }

        return Parse(buffer);
    }

    public static JsonElement Values(ReadOnlySpan<ScalarValue> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var value in values)
            {
                LogBatchJson.WriteValue(json, value, nonFiniteAsStrings: true);
            }

            json.WriteEndArray();
        }

        return Parse(buffer);
    }

    public static JsonElement Key(in EntityKey key)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            WriteKey(json, key);
        }

        return Parse(buffer);
    }

    /// <summary>
    /// A feature call's keys in staging order: each a value for a one-part key, an array of its parts for a key of several
    /// (a shared part written in every key, so each key reads on its own).
    /// </summary>
    public static JsonElement Keys(in EntityKeyBatch keys)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            for (var i = 0; i < keys.Count; i++)
            {
                if (keys.PartCount == 1)
                {
                    WriteKey(json, keys.Key(i, 0));
                    continue;
                }

                json.WriteStartArray();
                for (var p = 0; p < keys.PartCount; p++)
                {
                    WriteKey(json, keys.Key(i, p));
                }

                json.WriteEndArray();
            }

            json.WriteEndArray();
        }

        return Parse(buffer);
    }

    public static JsonElement Keys(ReadOnlySpan<EntityKey> keys)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var key in keys)
            {
                WriteKey(json, key);
            }

            json.WriteEndArray();
        }

        return Parse(buffer);
    }

    public static ReplayOutput Tensor(in TensorValues values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            switch (values.ElementType)
            {
                case TensorElementType.Bool:
                    foreach (var value in values.Bool.Span)
                    {
                        LogBatchJson.WriteValue(json, ScalarValue.FromBool(value), nonFiniteAsStrings: true);
                    }

                    break;
                case TensorElementType.Int32:
                    foreach (var value in values.Int32.Span)
                    {
                        LogBatchJson.WriteValue(json, ScalarValue.FromInt64(value), nonFiniteAsStrings: true);
                    }

                    break;
                case TensorElementType.Int64:
                    foreach (var value in values.Int64.Span)
                    {
                        LogBatchJson.WriteValue(json, ScalarValue.FromInt64(value), nonFiniteAsStrings: true);
                    }

                    break;
                case TensorElementType.Float32:
                    foreach (var value in values.Float32.Span)
                    {
                        LogBatchJson.WriteValue(json, ScalarValue.FromFloat32(value), nonFiniteAsStrings: true);
                    }

                    break;
                case TensorElementType.Float64:
                    foreach (var value in values.Float64.Span)
                    {
                        LogBatchJson.WriteValue(json, ScalarValue.FromFloat64(value), nonFiniteAsStrings: true);
                    }

                    break;
                default:
                    foreach (var value in values.String.Span)
                    {
                        // An element the client left unwritten is null; the operator fails the call's node on it, and the bundle keeps it.
                        LogBatchJson.WriteValue(json, ScalarValue.FromString(value), nonFiniteAsStrings: true);
                    }

                    break;
            }

            json.WriteEndArray();
        }

        return new ReplayOutput { Type = TensorValues.Name(values.ElementType), Values = Parse(buffer) };
    }

    public static ReplayTensor Tensor(ReplayOutput output, string pointer)
    {
        if (!TensorValues.TryParse(output.Type, out var type) || type == TensorElementType.Text)
        {
            throw new ReplayBundleException(pointer + "/type", $"'{output.Type}' is not a tensor element type",
                "an output's type is bool, int32, int64, float32, float64 or string; a text output is under texts");
        }

        var json = output.Values;
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new ReplayBundleException(pointer + "/values", $"expected an array of {output.Type} values, got {json.ValueKind}");
        }

        var count = json.GetArrayLength();
        Array values = type switch
        {
            TensorElementType.Bool => new bool[count],
            TensorElementType.Int32 => new int[count],
            TensorElementType.Int64 => new long[count],
            TensorElementType.Float32 => new float[count],
            TensorElementType.Float64 => new double[count],
            _ => new string?[count],
        };
        var element = type switch
        {
            TensorElementType.Bool => ColumnType.Bool,
            TensorElementType.Int32 => ColumnType.Int32,
            TensorElementType.Int64 => ColumnType.Int64,
            TensorElementType.Float32 => ColumnType.Float32,
            TensorElementType.Float64 => ColumnType.Float64,
            _ => ColumnType.String.AsNullable(),
        };
        var i = 0;
        foreach (var item in json.EnumerateArray())
        {
            // The element's pointer only for an error: a bundle's outputs hold a value per row.
            if (!LogBatchJson.TryReadValue(item, element, out var value, out var error, nonFiniteAsStrings: true))
            {
                throw Malformed(ElementPointer(pointer, i), error!, output.Type);
            }

            if (value.IsMissing && type != TensorElementType.String)
            {
                throw new ReplayBundleException(ElementPointer(pointer, i), $"expected a {output.Type} value, got null", "every element of a tensor has a value");
            }

            switch (values)
            {
                case bool[] b:
                    b[i] = value.AsBool();
                    break;
                case int[] n:
                    n[i] = (int)value.AsInt64();
                    break;
                case long[] l:
                    l[i] = value.AsInt64();
                    break;
                case float[] f:
                    f[i] = (float)value.AsFloat64();
                    break;
                case double[] d:
                    d[i] = value.AsFloat64();
                    break;
                case string?[] t:
                    t[i] = value.IsMissing ? null : value.AsString();
                    break;
            }

            i++;
        }

        return new ReplayTensor(type, values);

        static string ElementPointer(string pointer, int i) => $"{pointer}/values/{i.ToString(CultureInfo.InvariantCulture)}";
    }

    public static ReplayTypedValue Typed(ColumnType type, in ScalarValue value) =>
        new() { Type = type.ToString(), Value = value.IsMissing ? null : Value(value) };

    public static Dictionary<string, ReplayTypedValue> Inputs(RequestInputs? given)
    {
        var inputs = new Dictionary<string, ReplayTypedValue>(StringComparer.Ordinal);
        if (given is not null)
        {
            foreach (var (name, value) in given)
            {
                inputs[name] = Typed(TypeOf(value), value);
            }
        }

        return inputs;
    }

    public static List<ReplayCallColumn> Columns(Execution.ResultColumns rows)
    {
        var columns = new List<ReplayCallColumn>(rows.Names.Count);
        for (var c = 0; c < rows.Names.Count; c++)
        {
            columns.Add(new ReplayCallColumn { Name = rows.Names[c], Type = rows.Types[c].ToString(), Values = Values(rows.Column(c)) });
        }

        return columns;
    }

    public static List<ReplayCallColumn> Columns(Execution.ResultValues values)
    {
        var columns = new List<ReplayCallColumn>(values.Count);
        foreach (var value in values.All)
        {
            columns.Add(new ReplayCallColumn { Name = value.Name, Type = value.Type.ToString(), Values = Values([value.Value]) });
        }

        return columns;
    }

    public static Operators.PipelineCallResult CallResult(ReplayPipelineCall call, string pointer)
    {
        if (!call.Succeeded)
        {
            return Operators.PipelineCallResult.Failed(call.Pipeline, call.Error ?? $"pipeline '{call.Pipeline}' failed");
        }

        var ids = Keys(call.Ids, pointer + "/ids");
        var names = new string[call.Columns.Count];
        var types = new ColumnType[names.Length];
        var columns = new ScalarValue[names.Length][];
        for (var c = 0; c < names.Length; c++)
        {
            var at = $"{pointer}/columns/{c.ToString(CultureInfo.InvariantCulture)}";
            names[c] = call.Columns[c].Name;
            types[c] = Type(call.Columns[c].Type, at + "/type");
            columns[c] = Values(call.Columns[c].Values, types[c], ids.Length, at + "/values");
        }

        var values = new Execution.ResultValue[call.Values.Count];
        for (var v = 0; v < values.Length; v++)
        {
            var at = $"{pointer}/values/{v.ToString(CultureInfo.InvariantCulture)}";
            var type = Type(call.Values[v].Type, at + "/type");
            values[v] = new Execution.ResultValue(call.Values[v].Name, type, Values(call.Values[v].Values, type, 1, at + "/values")[0]);
        }

        var cached = call.CachedFrom is { } from ? new Operators.CachedFrom(from.RequestId, from.TimeMs, from.CalledBy, from.Stale == true) : null;
        return new Operators.PipelineCallResult(
            call.Pipeline, true, null, new Execution.ResultColumns(ids, names, types, columns), Execution.ResultValues.OfOutput(values), cached)
        {
            CalledBy = call.CalledBy,
            Degraded = call.Degraded,
        };
    }

    public static ColumnType Type(string? text, string pointer)
    {
        if (!ColumnType.TryParse(text, out var type, out var error))
        {
            throw new ReplayBundleException(pointer, error, "use a type spelling such as \"float32\", \"int64?\" or \"list<float32,4>\"");
        }

        return type;
    }

    public static ScalarValue Value(JsonElement? json, ColumnType type, string pointer)
    {
        if (json is not { } element)
        {
            return ScalarValue.Missing;
        }

        if (!LogBatchJson.TryReadValue(element, type, out var value, out var error, nonFiniteAsStrings: true))
        {
            throw Malformed(pointer, error!, type.ToString());
        }

        return value;
    }

    private static ReplayBundleException Malformed(string pointer, string error, string type) => new(pointer, error,
        $"values use the log encoding of their recorded type ({type}), with \"NaN\", \"Infinity\" and \"-Infinity\" for non-finite floats");

    public static ScalarValue[] Values(JsonElement json, ColumnType type, int expected, string pointer)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new ReplayBundleException(pointer, $"expected an array of {type} values, got {json.ValueKind}");
        }

        if (json.GetArrayLength() != expected)
        {
            throw new ReplayBundleException(pointer, $"expected {expected} values (one per key), got {json.GetArrayLength()}",
                "values are aligned with the call's keys");
        }

        var values = new ScalarValue[expected];
        var i = 0;
        foreach (var element in json.EnumerateArray())
        {
            values[i] = Value(element, type, pointer + "/" + i.ToString(CultureInfo.InvariantCulture));
            i++;
        }

        return values;
    }

    public static EntityKey Key(JsonElement json, string pointer) => json.ValueKind switch
    {
        JsonValueKind.Number when json.TryGetInt64(out var id) => EntityKey.FromInt64(id),
        JsonValueKind.String => EntityKey.FromString(json.GetString()!),
        _ => throw new ReplayBundleException(pointer, $"an entity key must be an int64 number or a string, got {json.GetRawText()}"),
    };

    public static EntityKey[] Keys(JsonElement? json, string pointer)
    {
        if (json is not { } array)
        {
            return Array.Empty<EntityKey>();
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new ReplayBundleException(pointer, $"expected an array of entity keys, got {array.ValueKind}");
        }

        var keys = new EntityKey[array.GetArrayLength()];
        var i = 0;
        foreach (var element in array.EnumerateArray())
        {
            keys[i] = Key(element, pointer + "/" + i.ToString(CultureInfo.InvariantCulture));
            i++;
        }

        return keys;
    }

    public static EntityKey[] KeyTuples(JsonElement json, string pointer, out int partCount)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new ReplayBundleException(pointer, $"expected an array of entity keys, got {json.ValueKind}");
        }

        var count = json.GetArrayLength();
        partCount = count == 0 || json[0].ValueKind != JsonValueKind.Array ? 1 : json[0].GetArrayLength();
        if (partCount is < 1 or > EntityKeyLayout.MaxParts)
        {
            throw new ReplayBundleException(pointer + "/0", $"a key has 1 to {EntityKeyLayout.MaxParts} parts, got {partCount}",
                "write a one-part key as its value, a key of several parts as an array of them");
        }

        var keys = new EntityKey[count * partCount];
        var i = 0;
        foreach (var element in json.EnumerateArray())
        {
            var at = pointer + "/" + i.ToString(CultureInfo.InvariantCulture);
            if (partCount == 1)
            {
                keys[i++] = Key(element, at);
                continue;
            }

            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != partCount)
            {
                throw new ReplayBundleException(at, $"expected a key of {partCount} parts, as the call's first, got {element.GetRawText()}",
                    "every key of a call has the parts of the entity's key, in its order");
            }

            var p = 0;
            foreach (var part in element.EnumerateArray())
            {
                keys[(i * partCount) + p] = Key(part, at + "/" + p.ToString(CultureInfo.InvariantCulture));
                p++;
            }

            i++;
        }

        return keys;
    }

    public const string Outstanding = "outstanding";

    public const string OutstandingError = "still in flight when the request was aborted";

    /// <summary>
    /// A recorded status. An outstanding call never answered the request, so it replays like a cancelled one: the request
    /// stopped waiting for it.
    /// </summary>
    public static IoOutcome Outcome(string? status, string pointer) => status switch
    {
        "ok" => IoOutcome.Ok,
        "partial" => IoOutcome.Partial,
        "timeout" => IoOutcome.Timeout,
        "error" => IoOutcome.Error,
        "cancelled" or Outstanding => IoOutcome.Cancelled,
        _ => throw new ReplayBundleException(pointer, $"unknown status '{status}'", "use ok, partial, timeout, error, cancelled or outstanding"),
    };

    public static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    private static void WriteKey(Utf8JsonWriter json, in EntityKey key)
    {
        if (key.StringValue is { } s)
        {
            json.WriteStringValue(s);
        }
        else
        {
            json.WriteNumberValue(key.Int64Value);
        }
    }

    private static JsonElement Parse(ArrayBufferWriter<byte> buffer)
    {
        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
    }
}

internal readonly record struct ReplayTensor(TensorElementType Type, Array Values);
