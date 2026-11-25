using System.Text;
using System.Text.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Logging;

/// <summary>
/// The JSON debug encoding of a batch (design 8.7): envelope, then columns grouped by kind with a per-column list of
/// missing row indices. Production sinks should prefer Arrow IPC or Parquet; the envelope fields are the same.
/// </summary>
/// <remarks>Numbers are written as the field's type (<see cref="LogField.Type"/>) holds them: an <c>int32</c> as the
/// integer, a <c>float32</c> value or <c>list&lt;float32&gt;</c> element as the shortest text that reads back as the
/// same float (<c>0.1</c>, not the double <c>0.10000000149011612</c> it widens to), a <c>float64</c> one as the shortest
/// text of the double.</remarks>
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
        if (batch.CalledBy is { } calledBy)
        {
            json.WriteString("called_by", calledBy);
        }

        json.WriteString("manifest", batch.ManifestId);
        json.WriteString("tap_id", batch.TapId);
        json.WriteString("schema", batch.Schema.Id);
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

        json.WriteNumber("row_prob", batch.RowProbability);
        json.WriteStartObject("context");
        var fields = batch.Schema.Context;
        if (fields.Length != batch.Context.Length)
        {
            throw new ArgumentException($"The batch of tap '{batch.TapId}' has {batch.Context.Length} context fields, its schema {fields.Length}.", nameof(batch));
        }

        for (var i = 0; i < fields.Length; i++)
        {
            var (name, value) = batch.Context[i];
            if (!string.Equals(name, fields[i].Name, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Context field {i} of tap '{batch.TapId}' is '{name}', its schema's '{fields[i].Name}'.", nameof(batch));
            }

            json.WritePropertyName(name);
            WriteField(json, value, fields[i].Type);
        }

        json.WriteEndObject();
        WriteColumns(json, "float_cols", batch.Columns, static k => k is StorageKind.Float32 or StorageKind.Float64);
        WriteColumns(json, "int_cols", batch.Columns, static k => k is StorageKind.Int32 or StorageKind.Int64 or StorageKind.Bool);
        WriteColumns(json, "string_cols", batch.Columns, static k => k == StorageKind.String);
        WriteColumns(json, "nested_cols", batch.Columns, static k => k == StorageKind.List);
        json.WriteStartObject("missing");
        foreach (var column in batch.Columns)
        {
            if (column.Validity is null)
            {
                continue;
            }

            json.WriteStartArray(column.Name);
            for (var i = 0; i < column.RowCount; i++)
            {
                if (column.IsMissing(i))
                {
                    json.WriteNumberValue(i);
                }
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>
    /// A context field's value as its declared <paramref name="type"/> writes it: a <c>float32</c> field's value and a
    /// <c>list&lt;float32&gt;</c> field's elements as floats, by the type rather than by the value's flag (a value made by
    /// hand may lack it); every other value as <see cref="WriteValue"/> writes it, a float64 field's as the double.
    /// </summary>
    private static void WriteField(Utf8JsonWriter json, in ScalarValue value, ColumnType type)
    {
        if (type.ValueKind != StorageKind.Float32 || value.IsMissing)
        {
            WriteValue(json, value);
            return;
        }

        if (value.Kind != StorageKind.List)
        {
            var single = value.AsFloat32();
            if (float.IsFinite(single))
            {
                json.WriteNumberValue(single);
            }
            else
            {
                json.WriteNullValue();
            }

            return;
        }

        var elements = value.AsFloat64List();
        for (var i = 0; i < elements.Length; i++)
        {
            if (!float.IsFinite((float)elements[i]))
            {
                json.WriteNullValue(); // a list with a non-finite element is missing in logs, as a column's is
                return;
            }
        }

        json.WriteStartArray();
        foreach (var element in elements)
        {
            json.WriteNumberValue((float)element);
        }

        json.WriteEndArray();
    }

    /// <summary>Writes a value; missing becomes null (and is listed under <c>missing</c>). A float is written as the double
    /// it is held as, exact in both encodings; a batch writes its float32 fields as floats (<see cref="Write"/>).</summary>
    /// <param name="nonFiniteAsStrings">
    /// False for logs: a NaN or ±∞ float, and a float list with such an element, is written as null like a missing value
    /// (design 8.7). True for replay bundles, which must keep what the request observed: such values are written as the
    /// strings <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c>, per element in lists.
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
            case StorageKind.String:
                json.WriteStringValue(value.AsString());
                break;
            default:
                WriteList(json, value, nonFiniteAsStrings);
                break;
        }
    }

    /// <summary>A list value as a JSON array of its elements, each written as a scalar of its element type is: a bool as
    /// 0 or 1, numbers as numbers (<c>list&lt;int32&gt;</c> and <c>list&lt;float32&gt;</c> values are held promoted, see
    /// <see cref="ScalarValue"/>). A float list holding NaN or ±∞ is null in logs, as a non-finite float is, and keeps
    /// names per element in replay bundles.</summary>
    private static void WriteList(Utf8JsonWriter json, in ScalarValue value, bool nonFiniteAsStrings)
    {
        if (value.ListElement == StorageKind.Float64 && !nonFiniteAsStrings && !ColumnValues.AllFinite(value.AsFloat64List()))
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartArray();
        switch (value.ListElement)
        {
            case StorageKind.Bool:
                foreach (var b in value.AsBoolList())
                {
                    json.WriteNumberValue(b ? 1 : 0);
                }

                break;
            case StorageKind.Int64:
                foreach (var l in value.AsInt64List())
                {
                    json.WriteNumberValue(l);
                }

                break;
            case StorageKind.Float64:
                foreach (var d in value.AsFloat64List())
                {
                    if (double.IsFinite(d))
                    {
                        json.WriteNumberValue(d);
                    }
                    else
                    {
                        json.WriteStringValue(NonFiniteName(d));
                    }
                }

                break;
            default:
                foreach (var s in value.AsStringList())
                {
                    json.WriteStringValue(s);
                }

                break;
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// Reads a value written by <see cref="WriteValue"/> or a batch (<see cref="Write"/>) back as <paramref name="type"/>
    /// (promoted storage, like <see cref="ScalarValue"/>): null is missing, a bool is 0 or 1 (<c>true</c>/<c>false</c> are
    /// accepted too), an <c>int32</c> value or <c>list&lt;int32&gt;</c> element must fit int32. The encoding carries no
    /// type, so the reader supplies it, as a log consumer does from the batch's schema.
    /// </summary>
    /// <remarks>Non-finite floats were written as null, so they read back as missing (design 8.7); a number outside the
    /// type's range is not a value of it.</remarks>
    /// <param name="nonFiniteAsStrings">
    /// The replay-bundle encoding rather than the log's. A bundle accepts the strings <c>"NaN"</c>, <c>"Infinity"</c> and
    /// <c>"-Infinity"</c> as float values and float list elements, as <see cref="WriteValue"/> writes them for bundles, and
    /// reads a float32 value as the double written: a bundle keeps what the request saw, its inputs as the host gave them
    /// (a float32 input's double, which the replay converts to the float again). A log holds float32 values as floats, so
    /// there a float32 value reads as the float the text names (the shortest text of the float, or its exact double).
    /// </param>
    public static bool TryReadValue(JsonElement json, ColumnType type, out ScalarValue value, out string? error, bool nonFiniteAsStrings = false)
    {
        // The double as written; a float32 value (as every value of a float32 column is) also prints as the float it is.
        static ScalarValue Float(ColumnType type, double d) =>
            type.Kind == StorageKind.Float32 && ((double)(float)d).Equals(d)
                ? ScalarValue.FromFloat32((float)d)
                : ScalarValue.FromFloat64(d);

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
                // A JSON number is finite: one beyond the float64 range reads as an infinity, which a writer would have
                // spelled "Infinity". Read as one, it compared equal to a real infinity (review 84). A float32 value beyond
                // the float32 range is not one either: stored, it would be an infinity.
                if (type.Kind == StorageKind.Float32 && !nonFiniteAsStrings)
                {
                    // A log's float32 is parsed as a float, and its range judged on that float: through the double, it
                    // could round twice, to the other neighbour or past float.MaxValue.
                    if (!json.TryGetSingle(out var single) || !float.IsFinite(single))
                    {
                        error = $"{json.GetRawText()} is beyond the float32 range";
                        return false;
                    }

                    value = ScalarValue.FromFloat32(single);
                    return true;
                }

                var number = json.GetDouble();
                if (!double.IsFinite(number) || (type.Kind == StorageKind.Float32 && !float.IsFinite((float)number)))
                {
                    error = $"{json.GetRawText()} is beyond the {(type.Kind == StorageKind.Float32 ? "float32" : "float64")} range";
                    return false;
                }

                value = Float(type, number);
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when nonFiniteAsStrings && TryNonFinite(json, out var named):
                value = Float(type, named);
                return true;
            case StorageKind.String when json.ValueKind == JsonValueKind.String:
                value = ScalarValue.FromString(json.GetString());
                return true;
            case StorageKind.List when json.ValueKind == JsonValueKind.Array:
                return TryReadList(json, type, out value, out error, nonFiniteAsStrings);
        }

        error = $"{json.GetRawText()} is not a valid {type}";
        return false;
    }

    /// <summary>Reads a list written by <see cref="WriteList"/>: each element as <see cref="TryReadValue"/> reads a scalar
    /// of the element type.</summary>
    private static bool TryReadList(JsonElement json, ColumnType type, out ScalarValue value, out string? error, bool nonFiniteAsStrings)
    {
        value = ScalarValue.Missing;
        error = null;
        var element = type.ElementType;
        var count = json.GetArrayLength();
        var index = 0;
        switch (element.Kind)
        {
            case StorageKind.Bool:
                var bools = new bool[count];
                foreach (var item in json.EnumerateArray())
                {
                    if (!TryReadValue(item, element, out var b, out _) || b.IsMissing)
                    {
                        error = $"element {index} ({item.GetRawText()}) is not a valid {type} element";
                        return false;
                    }

                    bools[index++] = b.AsBool();
                }

                value = ScalarValue.FromBoolList(bools);
                return true;
            case StorageKind.Int32 or StorageKind.Int64:
                var longs = new long[count];
                foreach (var item in json.EnumerateArray())
                {
                    if (!TryReadValue(item, element, out var l, out _) || l.IsMissing)
                    {
                        error = $"element {index} ({item.GetRawText()}) is not a valid {type} element";
                        return false;
                    }

                    longs[index++] = l.AsInt64();
                }

                value = ScalarValue.FromInt64List(longs);
                return true;
            case StorageKind.Float32 or StorageKind.Float64:
                var doubles = new double[count];
                var single = element.Kind == StorageKind.Float32;
                foreach (var item in json.EnumerateArray())
                {
                    if (!TryReadValue(item, element, out var d, out _, nonFiniteAsStrings) || d.IsMissing)
                    {
                        error = $"element {index} ({item.GetRawText()}) is not a valid {type} element";
                        return false;
                    }

                    doubles[index++] = d.AsFloat64();
                    single &= d.IsFloat32 || !double.IsFinite(d.AsFloat64());
                }

                // As for a scalar: a list<float32> value whose elements are all floats prints as the floats they are.
                // Converting keeps the array: its elements are floats already.
                var read = ScalarValue.FromFloat64List(doubles);
                value = single && read.TryConvert(ColumnType.List(StorageKind.Float32), out var floats) ? floats : read;
                return true;
            default:
                var strings = new string[count];
                foreach (var item in json.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        error = $"element {index} is {item.ValueKind}, not a string";
                        return false;
                    }

                    strings[index++] = item.GetString()!;
                }

                value = ScalarValue.FromStringList(strings);
                return true;
        }
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
            for (var row = 0; row < column.RowCount; row++)
            {
                WriteRow(json, column, row);
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    /// <summary>A row of a column, from its typed arrays; missing is null. Logged values are finite (<see cref="LogColumn.Capture"/>).</summary>
    private static void WriteRow(Utf8JsonWriter json, LogColumn column, int row)
    {
        if (column.IsMissing(row))
        {
            json.WriteNullValue();
            return;
        }

        var (start, end) = column.Offsets is { } offsets ? (offsets[row], offsets[row + 1]) : (row, row + 1);
        if (column.Offsets is not null)
        {
            json.WriteStartArray();
        }

        for (var i = start; i < end; i++)
        {
            switch (column.Values)
            {
                case bool[] b:
                    json.WriteNumberValue(b[i] ? 1 : 0); // as every other bool of a log: 0/1 in int_cols (decisions)
                    break;
                case int[] n:
                    json.WriteNumberValue(n[i]);
                    break;
                case long[] l:
                    json.WriteNumberValue(l[i]);
                    break;
                case float[] f:
                    json.WriteNumberValue(f[i]); // the shortest text of the float
                    break;
                case double[] d:
                    json.WriteNumberValue(d[i]);
                    break;
                case string?[] s:
                    json.WriteStringValue(s[i]);
                    break;
            }
        }

        if (column.Offsets is not null)
        {
            json.WriteEndArray();
        }
    }

    /// <summary>
    /// The record a batch's <c>schema</c> id names (design 8.7): <c>{"log_schema": {"id", "tap_id", "columns", "context"}}</c>,
    /// each field with its name, type and expression. A sink writes it once per id, before the first batch that names it
    /// (<see cref="JsonLinesLogSink"/> does), so a reader of the log knows each column's type and definition.
    /// </summary>
    public static void WriteSchema(Utf8JsonWriter json, LogSchema schema)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(schema);
        json.WriteStartObject();
        json.WriteStartObject("log_schema");
        json.WriteString("id", schema.Id);
        WriteSchemaMembers(json, schema.TapId, schema.Columns, schema.Context);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>The record less its id, as one object: what <see cref="LogSchema.Create"/> hashes.</summary>
    internal static void WriteSchemaBody(
        Utf8JsonWriter json, string tapId, System.Collections.Immutable.ImmutableArray<LogField> columns, System.Collections.Immutable.ImmutableArray<LogField> context)
    {
        json.WriteStartObject();
        WriteSchemaMembers(json, tapId, columns, context);
        json.WriteEndObject();
    }

    private static void WriteSchemaMembers(
        Utf8JsonWriter json, string tapId, System.Collections.Immutable.ImmutableArray<LogField> columns, System.Collections.Immutable.ImmutableArray<LogField> context)
    {
        json.WriteString("tap_id", tapId);
        WriteFields(json, "columns", columns);
        WriteFields(json, "context", context);
    }

    /// <summary><see cref="WriteSchema"/> as a string.</summary>
    public static string EncodeSchema(LogSchema schema, bool indented = true)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            WriteSchema(json, schema);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFields(Utf8JsonWriter json, string name, System.Collections.Immutable.ImmutableArray<LogField> fields)
    {
        json.WriteStartArray(name);
        foreach (var field in fields)
        {
            json.WriteStartObject();
            json.WriteString("name", field.Name);
            json.WriteString("type", field.Type.ToString());
            json.WriteString("expr", field.Expression);
            json.WriteEndObject();
        }

        json.WriteEndArray();
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
