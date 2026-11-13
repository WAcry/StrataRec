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
        WriteColumns(json, "nested_cols", batch.Columns, static k => k is StorageKind.Vector or StorageKind.ListInt64 or StorageKind.ListString);
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
    public static void WriteValue(Utf8JsonWriter json, in ScalarValue value)
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
                else
                {
                    json.WriteNullValue();
                }

                break;
            case StorageKind.String or StorageKind.Text:
                json.WriteStringValue(value.AsString());
                break;
            case StorageKind.Vector:
                json.WriteStartArray();
                foreach (var f in value.AsVector())
                {
                    json.WriteNumberValue(f);
                }

                json.WriteEndArray();
                break;
            case StorageKind.ListInt64:
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
