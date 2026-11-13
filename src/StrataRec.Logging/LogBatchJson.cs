using System.Text;
using System.Text.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Logging;

/// <summary>
/// The JSON debug encoding of a batch (design 8.7): envelope, then columns grouped by kind with a per-column list of
/// missing row indices. Production sinks should prefer Arrow IPC or Parquet; the envelope fields are the same.
/// </summary>
public static class LogBatchJson
{
    public static string Encode(LogBatch batch, bool indented = true)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            Write(json, batch);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void Write(Utf8JsonWriter json, LogBatch batch)
    {
        json.WriteStartObject();
        json.WriteString("request_id", batch.RequestId);
        json.WriteNumber("time_ms", batch.TimeMs);
        json.WriteString("pipeline_id", batch.PipelineId);
        json.WriteString("manifest", batch.ManifestId);
        json.WriteString("tap_id", batch.TapId);
        json.WriteString("schema", batch.SchemaId);
        json.WriteString("delivery", batch.Delivery == LogDelivery.Durable ? "durable" : "best_effort");
        json.WriteStartObject("producer");
        json.WriteString("id", batch.ProducerId);
        if (batch.Delivery == LogDelivery.Durable)
        {
            json.WriteNumber("seq", batch.Seq);
        }

        json.WriteEndObject();
        WriteStrings(json, "assigned_arms", batch.AssignedArms);
        WriteStrings(json, "triggered_experiments", batch.TriggeredExperiments);
        WriteMap(json, "node_outcomes", batch.NodeOutcomes);
        WriteMap(json, "column_producers", batch.ColumnProducers);
        json.WriteNumber("req_prob", batch.RequestProbability);
        json.WriteNumber("row_count", batch.RowCount);
        json.WriteStartArray("entity_ids");
        for (var i = 0; i < batch.RowCount; i++)
        {
            if (batch.Int64EntityIds is { } ids)
            {
                json.WriteNumberValue(ids[i]);
            }
            else
            {
                json.WriteStringValue(batch.StringEntityIds![i]);
            }
        }

        json.WriteEndArray();
        WriteInts(json, "ranks", batch.Ranks);
        if (batch.RanksBefore is { } before)
        {
            WriteInts(json, "ranks_before", before);
        }

        json.WriteStartArray("row_probs");
        foreach (var p in batch.RowProbabilities)
        {
            json.WriteNumberValue(p);
        }

        json.WriteEndArray();
        json.WriteStartObject("context");
        foreach (var (name, value) in batch.Context)
        {
            json.WritePropertyName(name);
            WriteValue(json, value);
        }

        json.WriteEndObject();
        WriteColumns(json, "float_cols", batch.Columns, static k => k is StorageKind.Float32 or StorageKind.Float64);
        WriteColumns(json, "int_cols", batch.Columns, static k => k is StorageKind.Int32 or StorageKind.Int64 or StorageKind.Bool);
        WriteColumns(json, "string_cols", batch.Columns, static k => k is StorageKind.String or StorageKind.Text);
        WriteColumns(json, "nested_cols", batch.Columns, static k => k is StorageKind.Vector or StorageKind.ListInt32 or StorageKind.ListInt64 or StorageKind.ListString);
        json.WriteStartObject("missing");
        foreach (var column in batch.Columns)
        {
            var any = false;
            for (var i = 0; i < column.Values.Length && !any; i++)
            {
                any = column.Values[i].IsMissing;
            }

            if (!any)
            {
                continue;
            }

            json.WriteStartArray(column.Name);
            for (var i = 0; i < column.Values.Length; i++)
            {
                if (column.Values[i].IsMissing)
                {
                    json.WriteNumberValue(i);
                }
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>Writes a value; missing becomes null (and is listed under <c>missing</c>).</summary>
    /// <param name="nonFiniteAsStrings">
    /// False for logs: a NaN or ±∞ float, and a vector with such a component, is written as null like a missing value
    /// (design 8.7). True for replay bundles, which must keep what the request observed: such values are written as the
    /// strings <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c>, per component in vectors.
    /// </param>
    public static void WriteValue(Utf8JsonWriter json, in ScalarValue value, bool nonFiniteAsStrings = false)
    {
        switch (value.Kind)
        {
            case 0:
                json.WriteNullValue();
                break;
            case StorageKind.Bool:
                json.WriteNumberValue(value.AsBool() ? 1 : 0);
                break;
            case StorageKind.Int32 or StorageKind.Int64:
                json.WriteNumberValue(value.AsInt64());
                break;
            case StorageKind.Float32 or StorageKind.Float64:
                var d = value.AsFloat64();
                if (double.IsFinite(d))
                {
                    json.WriteNumberValue(d);
                }
                else if (nonFiniteAsStrings)
                {
                    json.WriteStringValue(NonFiniteName(d));
                }
                else
                {
                    json.WriteNullValue();
                }

                break;
            case StorageKind.String or StorageKind.Text:
                json.WriteStringValue(value.AsString());
                break;
            case StorageKind.Vector:
                var vector = value.AsVector();
                if (!nonFiniteAsStrings && !AllFinite(vector))
                {
                    // Same rule as scalars: a non-finite value is logged as missing (capture already does this).
                    json.WriteNullValue();
                    break;
                }

                json.WriteStartArray();
                foreach (var f in vector)
                {
                    if (float.IsFinite(f))
                    {
                        json.WriteNumberValue(f);
                    }
                    else
                    {
                        json.WriteStringValue(NonFiniteName(f));
                    }
                }

                json.WriteEndArray();
                break;
            case StorageKind.ListInt64: // also list<int32> columns: values are held promoted (see ScalarValue)
                json.WriteStartArray();
                foreach (var l in value.AsInt64List())
                {
                    json.WriteNumberValue(l);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteStartArray();
                foreach (var s in value.AsStringList())
                {
                    json.WriteStringValue(s);
                }

                json.WriteEndArray();
                break;
        }
    }

    /// <summary>
    /// Reads a value written by <see cref="WriteValue"/> back as <paramref name="type"/> (promoted storage, like
    /// <see cref="ScalarValue"/>): null is missing, a bool is 0 or 1 (<c>true</c>/<c>false</c> are accepted too), an
    /// <c>int32</c> value or <c>list&lt;int32&gt;</c> element must fit int32. The encoding carries no type, so the reader
    /// supplies it, as a log consumer does from the batch's schema.
    /// </summary>
    /// <remarks>Non-finite floats were written as null, so they read back as missing (design 8.7).</remarks>
    /// <param name="nonFiniteAsStrings">
    /// Accept the strings <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c> as float values and vector components, as
    /// <see cref="WriteValue"/> writes them for replay bundles.
    /// </param>
    public static bool TryReadValue(JsonElement json, ColumnType type, out ScalarValue value, out string? error, bool nonFiniteAsStrings = false)
    {
        value = ScalarValue.Missing;
        error = null;
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        switch (type.Kind)
        {
            case StorageKind.Bool when json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var flag) && flag is 0 or 1:
                value = ScalarValue.FromBool(flag == 1);
                return true;
            case StorageKind.Bool when json.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = ScalarValue.FromBool(json.GetBoolean());
                return true;
            case StorageKind.Int32 or StorageKind.Int64 when json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var integer):
                if (type.Kind == StorageKind.Int32 && integer is < int.MinValue or > int.MaxValue)
                {
                    break;
                }

                value = ScalarValue.FromInt64(integer);
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when json.ValueKind == JsonValueKind.Number:
                value = ScalarValue.FromFloat64(json.GetDouble());
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when nonFiniteAsStrings && TryNonFinite(json, out var named):
                value = ScalarValue.FromFloat64(named);
                return true;
            case StorageKind.String when json.ValueKind == JsonValueKind.String:
                value = ScalarValue.FromString(json.GetString());
                return true;
            case StorageKind.Text when json.ValueKind == JsonValueKind.String:
                value = ScalarValue.FromText(json.GetString());
                return true;
            case StorageKind.Vector when json.ValueKind == JsonValueKind.Array:
                var vector = new float[json.GetArrayLength()];
                var v = 0;
                foreach (var element in json.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        vector[v++] = element.GetSingle();
                    }
                    else if (nonFiniteAsStrings && TryNonFinite(element, out var component))
                    {
                        vector[v++] = (float)component;
                    }
                    else
                    {
                        error = $"vector element {v} is {element.ValueKind}, not a number";
                        return false;
                    }
                }

                if (type.Dimension > 0 && vector.Length != type.Dimension)
                {
                    error = $"expected {type.Dimension} elements for {type}, got {vector.Length}";
                    return false;
                }

                value = ScalarValue.FromVector(vector);
                return true;
            case StorageKind.ListInt32 or StorageKind.ListInt64 when json.ValueKind == JsonValueKind.Array:
                var longs = new long[json.GetArrayLength()];
                var l = 0;
                foreach (var element in json.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var item)
                        || (type.Kind == StorageKind.ListInt32 && item is < int.MinValue or > int.MaxValue))
                    {
                        error = $"element {l} ({element.GetRawText()}) is not a valid {type} element";
                        return false;
                    }

                    longs[l++] = item;
                }

                value = ScalarValue.FromInt64List(longs);
                return true;
            case StorageKind.ListString when json.ValueKind == JsonValueKind.Array:
                var strings = new string[json.GetArrayLength()];
                var s = 0;
                foreach (var element in json.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        error = $"element {s} is {element.ValueKind}, not a string";
                        return false;
                    }

                    strings[s++] = element.GetString()!;
                }

                value = ScalarValue.FromStringList(strings);
                return true;
        }

        error = $"{json.GetRawText()} is not a valid {type}";
        return false;
    }

    /// <summary>The spelling of a non-finite value: <c>NaN</c>, <c>Infinity</c> or <c>-Infinity</c> (as JavaScript and System.Text.Json name them).</summary>
    public static string NonFiniteName(double value) =>
        double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";

    /// <summary>Reads a non-finite value written as its name.</summary>
    public static bool TryNonFinite(JsonElement json, out double value)
    {
        value = json.ValueKind != JsonValueKind.String ? 0.0 : json.GetString() switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            _ => 0.0,
        };
        return !double.IsFinite(value);
    }

    private static bool AllFinite(ReadOnlySpan<float> values)
    {
        foreach (var f in values)
        {
            if (!float.IsFinite(f))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteColumns(Utf8JsonWriter json, string name, LogColumn[] columns, Func<StorageKind, bool> kind)
    {
        json.WriteStartObject(name);
        foreach (var column in columns)
        {
            if (!kind(column.Type.Kind))
            {
                continue;
            }

            json.WriteStartArray(column.Name);
            foreach (var value in column.Values)
            {
                WriteValue(json, value);
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var v in values)
        {
            json.WriteStringValue(v);
        }

        json.WriteEndArray();
    }

    private static void WriteInts(Utf8JsonWriter json, string name, int[] values)
    {
        json.WriteStartArray(name);
        foreach (var v in values)
        {
            json.WriteNumberValue(v);
        }

        json.WriteEndArray();
    }

    private static void WriteMap(Utf8JsonWriter json, string name, IReadOnlyDictionary<string, string> map)
    {
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        json.WriteStartObject(name);
        foreach (var key in keys)
        {
            json.WriteString(key, map[key]);
        }

        json.WriteEndObject();
    }
}
