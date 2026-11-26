using System.Text.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Pipeline.Execution;

namespace StrataRec.Hosting;

/// <summary>What a debugging caller gets in addition to the result every caller gets.</summary>
public sealed class RecResultJsonOptions
{
    /// <summary>Write <c>column_producers</c>: the node that produced each output column (lineage).</summary>
    public bool IncludeColumnProducers { get; init; }
}

/// <summary>
/// A <see cref="RecResult"/> as JSON: one document for every host and the CLI's <c>run</c>, so what a service answers and
/// what a developer sees locally read alike.
/// </summary>
/// <remarks>
/// <para>The document: <c>request_id</c>, <c>succeeded</c>, for a failure <c>error_kind</c> (<see cref="Name"/>),
/// <c>error</c> and <c>error_pointer</c> when there are, <c>served_by</c>, <c>manifest</c>, <c>experiments</c>
/// (<c>assigned</c>, <c>triggered</c>, <c>ignored</c> with <c>id</c> and <c>reason</c>), <c>items</c> (<c>rank</c>,
/// <c>id</c>, <c>columns</c>), <c>values</c> (the output's request-level values, name → value) when the pipeline returns
/// any, and <c>elapsed_ms</c>; then <c>column_producers</c> when asked for, and <c>trace</c>,
/// <c>logs</c> and <c>explanation</c> (<see cref="WriteExplanation"/>) when the request asked for them
/// (<see cref="DebugOptions"/>).</para>
/// <para>An item's output columns are an object of their own, so no column name (<c>id</c>, <c>rank</c>) can collide with
/// the item's members. An int64 id is a JSON number, written exactly; a string id a string. A missing value is
/// <c>null</c> (never 0), and so is a non-finite float, which JSON cannot represent; floats are written exactly, not
/// rounded: the shortest text that reads back as the same double, or for a float32 column as the same float.</para>
/// <para>Formatting and escaping are the writer's: a service writes compact, the CLI indented.</para>
/// </remarks>
public static class RecResultJson
{
    /// <summary>Writes <paramref name="result"/> as one JSON object.</summary>
    public static void Write(Utf8JsonWriter json, RecResult result, RecResultJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(result);
        json.WriteStartObject();
        json.WriteString("request_id", result.RequestId);
        json.WriteBoolean("succeeded", result.Succeeded);
        if (result.ErrorKind != RecErrorKind.None)
        {
            json.WriteString("error_kind", Name(result.ErrorKind));
        }

        if (result.Error is not null)
        {
            json.WriteString("error", result.Error);
        }

        if (result.ErrorPointer is not null)
        {
            json.WriteString("error_pointer", result.ErrorPointer);
        }

        json.WriteString("served_by", result.ServedBy);
        json.WriteString("manifest", result.ManifestId);
        WriteExperiments(json, result.Experiments);
        json.WritePropertyName("items");
        WriteItems(json, result.Columns);
        if (result.Values.Count > 0)
        {
            json.WritePropertyName("values");
            WriteValues(json, result.Values);
        }

        json.WriteNumber("elapsed_ms", Math.Round(result.ElapsedMs, 3));
        if (options?.IncludeColumnProducers == true && result.ColumnProducers is { } producers)
        {
            json.WriteStartObject("column_producers");
            foreach (var (column, node) in producers)
            {
                json.WriteString(column, node);
            }

            json.WriteEndObject();
        }

        if (result.Trace is { } outcomes)
        {
            json.WriteStartArray("trace");
            foreach (var outcome in outcomes)
            {
                WriteOutcome(json, outcome);
            }

            json.WriteEndArray();
        }

        if (result.Calls is { } calls)
        {
            json.WriteStartArray("calls");
            foreach (var call in calls)
            {
                json.WriteStartObject();
                json.WriteString("called_by", call.CalledBy);
                json.WriteString("caller", call.Caller);
                json.WriteString("node", call.Node);
                json.WriteNumber("seq", call.Sequence);
                json.WriteString("pipeline", call.PipelineId);
                json.WriteBoolean("succeeded", call.Succeeded);
                if (call.Error is not null)
                {
                    json.WriteString("error", call.Error);
                }

                json.WriteNumber("elapsed_ms", Math.Round(call.ElapsedMs, 3));
                json.WriteStartArray("arms");
                foreach (var arm in call.Arms)
                {
                    json.WriteStringValue(arm);
                }

                json.WriteEndArray();
                json.WriteStartArray("trace");
                foreach (var outcome in call.Trace)
                {
                    WriteOutcome(json, outcome);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        if (result.LogBatches is { } batches)
        {
            json.WriteStartArray("logs");
            foreach (var batch in batches)
            {
                Logging.LogBatchJson.Write(json, batch);
            }

            json.WriteEndArray();
        }

        if (result.Explanation is { } explanation)
        {
            json.WritePropertyName("explanation");
            WriteExplanation(json, explanation);
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// Explained candidates as one JSON object: <c>pipeline</c> (the run explained) and <c>candidates</c>, each
    /// with <c>id</c>, <c>verdict</c> (<c>returned</c>, <c>beyond_limit</c>, <c>dropped</c>, <c>not_recalled</c>,
    /// <c>no_result</c>), <c>rank</c> or <c>dropped_by</c> where they apply, and <c>steps</c>: per node <c>node</c>,
    /// <c>step</c> (<c>ran</c>, <c>rolled_back</c>, <c>skipped</c>), <c>detail</c>, <c>change</c> (<c>entered</c>,
    /// <c>stayed</c>, <c>dropped</c>, <c>absent</c>), <c>present</c>, <c>alive</c>, <c>rank</c>, <c>rank_before</c> when it
    /// differs, <c>values</c> (column → value), <c>reason</c> and <c>terms</c> (name → value) when there are.
    /// </summary>
    public static void WriteExplanation(Utf8JsonWriter json, RunExplanation explanation)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(explanation);
        json.WriteStartObject();
        json.WriteString("pipeline", explanation.PipelineId);
        json.WriteStartArray("candidates");
        foreach (var candidate in explanation.Candidates)
        {
            json.WriteStartObject();
            WriteId(json, candidate.Id);
            json.WriteString("verdict", VerdictName(candidate.Verdict));
            if (candidate.Rank is { } rank)
            {
                json.WriteNumber("rank", rank);
            }

            if (candidate.DroppedBy is { } droppedBy)
            {
                json.WriteString("dropped_by", droppedBy);
            }

            json.WriteStartArray("steps");
            foreach (var step in candidate.Steps)
            {
                WriteStep(json, step);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>The JSON name of a verdict: <c>returned</c>, <c>beyond_limit</c>, <c>dropped</c>, <c>not_recalled</c>, <c>no_result</c>.</summary>
    public static string VerdictName(ExplainVerdict verdict) => verdict switch
    {
        ExplainVerdict.Returned => "returned",
        ExplainVerdict.BeyondLimit => "beyond_limit",
        ExplainVerdict.Dropped => "dropped",
        ExplainVerdict.NotRecalled => "not_recalled",
        ExplainVerdict.NoResult => "no_result",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    /// <summary>The JSON name of a step kind: <c>ran</c>, <c>rolled_back</c>, <c>skipped</c>.</summary>
    public static string StepName(ExplainStepKind kind) => kind switch
    {
        ExplainStepKind.Ran => "ran",
        ExplainStepKind.RolledBack => "rolled_back",
        ExplainStepKind.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The JSON name of a change: <c>entered</c>, <c>stayed</c>, <c>dropped</c>, <c>absent</c>.</summary>
    public static string ChangeName(ExplainChange change) => change switch
    {
        ExplainChange.Entered => "entered",
        ExplainChange.Stayed => "stayed",
        ExplainChange.Dropped => "dropped",
        ExplainChange.Absent => "absent",
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    private static void WriteStep(Utf8JsonWriter json, ExplainStep step)
    {
        json.WriteStartObject();
        json.WriteString("node", step.Node);
        json.WriteString("step", StepName(step.Kind));
        if (step.Detail is not null)
        {
            json.WriteString("detail", step.Detail);
        }

        json.WriteString("change", ChangeName(step.Change));
        json.WriteBoolean("present", step.Present);
        json.WriteBoolean("alive", step.Alive);
        if (step.Rank is { } rank)
        {
            json.WriteNumber("rank", rank);
        }

        if (step.RankBefore is { } before && before != step.Rank)
        {
            json.WriteNumber("rank_before", before);
        }

        WriteNamed(json, "values", step.Values);
        if (step.Reason is not null)
        {
            json.WriteString("reason", step.Reason);
        }

        WriteNamed(json, "terms", step.Terms);
        json.WriteEndObject();
    }

    /// <summary>Named values as an object, in their order; nothing when there are none.</summary>
    private static void WriteNamed(Utf8JsonWriter json, string name, IReadOnlyList<ExplainValue> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        json.WriteStartObject(name);
        foreach (var value in values)
        {
            json.WritePropertyName(value.Name);
            WriteValue(json, value.Value);
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// A result's items as a JSON array, each <c>{ "rank", "id", "columns": { … } }</c>, written from the columns (no row
    /// dictionaries are made). The output columns are a member of their own, so a column named <c>id</c> or <c>rank</c>
    /// cannot collide with the item's (<c>stratarec replay</c> writes its items with this too: it flattened the columns
    /// into the item and wrote duplicate members). A column's declared type decides how its floats
    /// print (<see cref="ResultColumns.Types"/>): a <c>float32</c> or <c>list&lt;float32&gt;</c> column's as floats.
    /// </summary>
    public static void WriteItems(Utf8JsonWriter json, ResultColumns items)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(items);
        json.WriteStartArray();
        var ids = items.Ids;
        for (var i = 0; i < ids.Length; i++)
        {
            json.WriteStartObject();
            json.WriteNumber("rank", i + 1);
            WriteId(json, ids[i]);
            json.WriteStartObject("columns");
            for (var c = 0; c < items.Names.Count; c++)
            {
                json.WritePropertyName(items.Names[c]);
                WriteValue(json, items.Column(c)[i], float32: items.Types[c].ValueKind == StorageKind.Float32);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// A result's request-level values as one JSON object, name → value, encoded as the items' columns are: a missing or
    /// non-finite value is <c>null</c>, and a <c>float32</c> value's floats print as floats.
    /// </summary>
    public static void WriteValues(Utf8JsonWriter json, ResultValues values)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(values);
        json.WriteStartObject();
        foreach (var value in values.All)
        {
            json.WritePropertyName(value.Name);
            WriteValue(json, value.Value, float32: value.Type.ValueKind == StorageKind.Float32);
        }

        json.WriteEndObject();
    }

    private static void WriteId(Utf8JsonWriter json, Core.Keys.EntityKey id)
    {
        if (id.StringValue is { } text)
        {
            json.WriteString("id", text);
        }
        else
        {
            json.WriteNumber("id", id.Int64Value);
        }
    }

    /// <summary>
    /// A list as a JSON array of its elements, each written as a scalar of its element type is (a float32 list's
    /// elements as the floats they are, a non-finite element as null, as a non-finite float is).
    /// </summary>
    private static void WriteList(Utf8JsonWriter json, in ScalarValue value, bool float32)
    {
        json.WriteStartArray();
        switch (value.ListElement)
        {
            case StorageKind.Bool:
                foreach (var b in value.AsBoolList())
                {
                    json.WriteBooleanValue(b);
                }

                break;
            case StorageKind.Int64: // also list<int32> values, held promoted
                foreach (var number in value.AsInt64List())
                {
                    json.WriteNumberValue(number);
                }

                break;
            case StorageKind.Float64:
                foreach (var number in value.AsFloat64List())
                {
                    if (!double.IsFinite(number))
                    {
                        json.WriteNullValue();
                    }
                    else if (float32)
                    {
                        json.WriteNumberValue((float)number);
                    }
                    else
                    {
                        json.WriteNumberValue(number);
                    }
                }

                break;
            default:
                foreach (var text in value.AsStringList())
                {
                    json.WriteStringValue(text);
                }

                break;
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// A value as JSON: missing is <c>null</c>, and so is a non-finite float (or float list element), which JSON cannot
    /// represent; integers exact, floats in their shortest round-trip form, a float32 value (a float32 column's, a
    /// <c>list&lt;float32&gt;</c> element) as the float: <c>0.1</c>, not the double <c>0.10000000149011612</c> it widens to.
    /// Without a declared type, a value read from float32 storage is a float32 one (<see cref="ScalarValue.IsFloat32"/>).
    /// </summary>
    public static void WriteValue(Utf8JsonWriter json, in ScalarValue value) =>
        WriteValue(json, value, float32: value.IsFloat32 || value.IsFloat32List);

    /// <summary><see cref="WriteValue(Utf8JsonWriter, in ScalarValue)"/>, a float written as a float when
    /// <paramref name="float32"/>: the declared type's decision, not the value's origin.</summary>
    private static void WriteValue(Utf8JsonWriter json, in ScalarValue value, bool float32)
    {
        switch (value.Kind)
        {
            case StorageKind.Bool:
                json.WriteBooleanValue(value.AsBool());
                break;
            case StorageKind.Int64: // also int32 values: ScalarValue holds them promoted
                json.WriteNumberValue(value.AsInt64());
                break;
            case StorageKind.Float64 when float32 && double.IsFinite(value.AsFloat64()):
                json.WriteNumberValue(value.AsFloat32());
                break;
            case StorageKind.Float64 when double.IsFinite(value.AsFloat64()):
                json.WriteNumberValue(value.AsFloat64());
                break;
            case StorageKind.String:
                json.WriteStringValue(value.AsString());
                break;
            case StorageKind.List:
                WriteList(json, value, float32);
                break;
            default: // missing, or a non-finite float
                json.WriteNullValue();
                break;
        }
    }

    /// <summary>The JSON name of an error kind: <c>unknown_pipeline</c>, <c>invalid_request</c>, <c>deadline_exceeded</c>, <c>failed</c>.</summary>
    public static string Name(RecErrorKind kind) => kind switch
    {
        RecErrorKind.None => "none",
        RecErrorKind.UnknownPipeline => "unknown_pipeline",
        RecErrorKind.InvalidRequest => "invalid_request",
        RecErrorKind.DeadlineExceeded => "deadline_exceeded",
        RecErrorKind.Failed => "failed",
    };

    private static void WriteExperiments(Utf8JsonWriter json, ExperimentRecord experiments)
    {
        json.WriteStartObject("experiments");
        WriteStrings(json, "assigned", experiments.Assigned);
        WriteStrings(json, "triggered", experiments.Triggered);
        json.WriteStartArray("ignored");
        foreach (var ignored in experiments.Ignored)
        {
            json.WriteStartObject();
            json.WriteString("id", ignored.Id);
            json.WriteString("reason", IgnoredArm.Name(ignored.Reason));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteOutcome(Utf8JsonWriter json, NodeOutcome outcome)
    {
        json.WriteStartObject();
        json.WriteString("node", outcome.NodeId);
        json.WriteString("outcome", NodeOutcomeNames.Name(outcome.Kind));
        if (outcome.Detail is not null)
        {
            json.WriteString("detail", outcome.Detail);
        }

        if (outcome.ProducedBy is not null && outcome.ProducedBy != outcome.NodeId)
        {
            json.WriteString("produced_by", outcome.ProducedBy);
        }

        json.WriteNumber("ms", Math.Round(outcome.DurationMs, 3));
        json.WriteNumber("rows_in", outcome.RowsIn);
        json.WriteNumber("rows_out", outcome.RowsOut);
        json.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }
}
