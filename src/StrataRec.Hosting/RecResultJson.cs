using System.Text.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Pipeline.Execution;

namespace StrataRec.Hosting;

/// <summary>What a debugging caller gets in addition to the result every caller gets.</summary>
public sealed class RecResultJsonOptions
{
    /// <summary>Write <c>column_producers</c>: the node that produced each output column (lineage, design 8.7).</summary>
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
/// <c>id</c>, <c>columns</c>) and <c>elapsed_ms</c>; then <c>column_producers</c> when asked for, and <c>trace</c>,
/// <c>logs</c> and <c>explanation</c> (<see cref="WriteExplanation"/>) when the request asked for them
/// (<see cref="DebugOptions"/>).</para>
/// <para>An item's output columns are an object of their own, so no column name (<c>id</c>, <c>rank</c>) can collide with
/// the item's members. An int64 id is a JSON number, written exactly; a string id a string. A missing value is
/// <c>null</c> (never 0), and so is a non-finite float, which JSON cannot represent; floats are written exactly (the
/// shortest text that reads back as the same double), not rounded.</para>
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
        json.WriteStartArray("items");
        foreach (var item in result.Items)
        {
            json.WriteStartObject();
            json.WriteNumber("rank", item.Rank);
            if (item.Id.StringValue is { } text)
            {
                json.WriteString("id", text);
            }
            else
            {
                json.WriteNumber("id", item.Id.Int64Value);
            }

            json.WriteStartObject("columns");
            foreach (var (name, value) in item.Columns)
            {
                json.WritePropertyName(name);
                WriteValue(json, value);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndArray();
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
    /// Explained candidates (design 13.3) as one JSON object: <c>pipeline</c> (the run explained) and <c>candidates</c>, each
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
    /// A value as JSON: missing is <c>null</c>, and so is a non-finite float (or vector component), which JSON cannot
    /// represent; integers exact, floats in their shortest round-trip form.
    /// </summary>
    public static void WriteValue(Utf8JsonWriter json, in ScalarValue value)
    {
        switch (value.Kind)
        {
            case StorageKind.Bool:
                json.WriteBooleanValue(value.AsBool());
                break;
            case StorageKind.Int64: // also int32 values: ScalarValue holds them promoted
                json.WriteNumberValue(value.AsInt64());
                break;
            case StorageKind.Float64 when double.IsFinite(value.AsFloat64()):
                json.WriteNumberValue(value.AsFloat64());
                break;
            case StorageKind.String or StorageKind.Text:
                json.WriteStringValue(value.AsString());
                break;
            case StorageKind.Vector:
                json.WriteStartArray();
                foreach (var component in value.AsVector())
                {
                    if (float.IsFinite(component))
                    {
                        json.WriteNumberValue(component);
                    }
                    else
                    {
                        json.WriteNullValue();
                    }
                }

                json.WriteEndArray();
                break;
            case StorageKind.ListInt64: // also list<int32> values, held promoted
                json.WriteStartArray();
                foreach (var number in value.AsInt64List())
                {
                    json.WriteNumberValue(number);
                }

                json.WriteEndArray();
                break;
            case StorageKind.ListString:
                json.WriteStartArray();
                foreach (var text in value.AsStringList())
                {
                    json.WriteStringValue(text);
                }

                json.WriteEndArray();
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
        json.WriteString("outcome", outcome.Kind.ToString().ToLowerInvariant());
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
