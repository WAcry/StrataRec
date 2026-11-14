using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Features.Catalog;
using StrataRec.Hosting;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Samples.Host;

/// <summary>
/// <c>POST /recommend/{pipeline}</c>: the HTTP transport around <see cref="IRecEngine.ExecuteAsync"/>. The engine knows no
/// transport, so the host turns the body into a <see cref="RecRequest"/>, and the <see cref="RecResult"/> into a response.
/// </summary>
/// <remarks>
/// <para>The body: <c>{ "inputs": { … }, "request_id": "…", "assignment": ["experiment/arm", …] }</c>, all optional.
/// <c>inputs</c> follow the pipeline's <c>request_schema</c>; <c>assignment</c> is what the experiment platform answered
/// for this request (without it, a registered <c>IAssignmentProvider</c> is asked, and without one the request runs the
/// baseline).</para>
/// <para>Status codes: 404 for a pipeline the engine does not serve, 400 for a body or input the request schema rejects
/// (with the JSON pointer of the problem), 500 when the engine ran the request and it failed (with the engine's message).
/// Errors are RFC 9457 problem details. The engine reports a rejected input as a failed result like any other failure, so
/// the host checks inputs against the plan's request schema first, to tell the caller's mistake (400) from its own (500).</para>
/// </remarks>
public static class RecommendEndpoint
{
    public static async Task<IResult> HandleAsync(string pipeline, HttpRequest http, RecEngine engine, SampleSettings settings, CancellationToken cancellationToken)
    {
        if (!engine.TryGetPipeline(pipeline, out var served))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown pipeline", detail: $"Unknown pipeline '{pipeline}'.",
                extensions: new Dictionary<string, object?> { ["pipelines"] = engine.PipelineIds() });
        }

        JsonNode? body;
        try
        {
            body = http.ContentLength == 0 ? new JsonObject() : await JsonNode.ParseAsync(http.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return BadRequest(string.Empty, $"The body is not JSON: {ex.Message}");
        }

        if (!TryRead(body, served.Plan.RequestInputs, pipeline, out var parsed, out var pointer, out var problem))
        {
            return BadRequest(pointer, problem);
        }

        var request = new RecRequest
        {
            PipelineId = pipeline,
            RequestId = parsed.RequestId ?? http.HttpContext.TraceIdentifier, // ties the engine's span and logs to this HTTP request
            Inputs = parsed.Inputs,
            Assignment = parsed.Assignment,
            Deadline = settings.Deadline,
            // Debug options (trace, forced arms, recording) are the host's to authorize, e.g. for internal callers only.
        };

        // The caller's disconnect cancels the request: what it started stops, and nothing is written back.
        var result = await engine.ExecuteAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Recommendation failed", detail: result.Error,
                extensions: new Dictionary<string, object?> { ["request_id"] = request.RequestId, ["served_by"] = result.ServedBy });
        }

        return new Response(request.RequestId, result);
    }

    private static IResult BadRequest(string pointer, string message) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: message,
            extensions: new Dictionary<string, object?> { ["pointer"] = pointer });

    private readonly record struct Parsed(string? RequestId, RequestInputs Inputs, IReadOnlyList<string>? Assignment);

    /// <summary>
    /// Reads the body against the pipeline's request schema, as the engine will check it: every member known, every input
    /// declared and of its declared type, every non-nullable input present. A JSON <c>null</c> or an absent input is
    /// missing, which only a nullable input (<c>T?</c>) may be.
    /// </summary>
    private static bool TryRead(
        JsonNode? body, IReadOnlyDictionary<string, ContextField> schema, string pipeline, out Parsed parsed, out string pointer, out string problem)
    {
        parsed = default;
        pointer = string.Empty;
        problem = string.Empty;
        if (body is not JsonObject document)
        {
            problem = "The body must be a JSON object: { \"inputs\": { … }, \"request_id\": \"…\", \"assignment\": [\"experiment/arm\"] }.";
            return false;
        }

        string? requestId = null;
        List<string>? assignment = null;
        var inputs = new RequestInputs();
        foreach (var (name, value) in document)
        {
            pointer = "/" + JsonPointer.Escape(name);
            switch (name)
            {
                case "request_id":
                    if (value is not JsonValue id || !id.TryGetValue(out requestId) || requestId.Length == 0)
                    {
                        problem = "request_id must be a non-empty string.";
                        return false;
                    }

                    break;
                case "assignment":
                    if (value is not JsonArray arms)
                    {
                        problem = "assignment must be an array of arm ids, e.g. [\"exp_local_boost/strong\"].";
                        return false;
                    }

                    assignment = new List<string>(arms.Count);
                    for (var i = 0; i < arms.Count; i++)
                    {
                        if (arms[i] is not JsonValue arm || !arm.TryGetValue<string>(out var armId))
                        {
                            pointer += "/" + i.ToString(CultureInfo.InvariantCulture);
                            problem = "An arm id is a string 'experiment/arm'.";
                            return false;
                        }

                        assignment.Add(armId);
                    }

                    break;
                case "inputs":
                    if (value is not JsonObject values)
                    {
                        problem = "inputs must be an object: input name → value.";
                        return false;
                    }

                    foreach (var (input, inputValue) in values)
                    {
                        pointer = "/inputs/" + JsonPointer.Escape(input);
                        if (!schema.TryGetValue(input, out var field))
                        {
                            problem = $"'{input}' is not declared in the request_schema of '{pipeline}'. Declared: {string.Join(", ", schema.Keys)}.";
                            return false;
                        }

                        if (!ConfigValues.TryToScalar(inputValue, field.InputType, out var scalar, out var error))
                        {
                            problem = $"Input '{input}' {error}.";
                            return false;
                        }

                        if (!scalar.IsMissing)
                        {
                            inputs.Set(input, scalar);
                        }
                    }

                    break;
                default:
                    problem = $"Unknown member '{name}'; the body has inputs, request_id and assignment.";
                    return false;
            }
        }

        foreach (var (name, field) in schema)
        {
            if (!field.Type.Nullable && !inputs.ContainsKey(name))
            {
                pointer = "/inputs/" + JsonPointer.Escape(name);
                problem = $"'{name}' ({field.InputType}) is required.";
                return false;
            }
        }

        pointer = string.Empty;
        parsed = new Parsed(requestId, inputs, assignment);
        return true;
    }

    /// <summary>
    /// The ranked items and the record of the request's experiments, written straight to the response. Item columns are those
    /// the pipeline's <c>output</c> declares; a missing value is <c>null</c>.
    /// </summary>
    private sealed class Response(string requestId, RecResult result) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.ContentType = "application/json; charset=utf-8";
            await using var json = new Utf8JsonWriter(http.Response.Body);
            json.WriteStartObject();
            json.WriteString("request_id", requestId);
            json.WriteString("served_by", result.ServedBy);
            json.WriteString("manifest", result.ManifestId);
            json.WriteStartObject("experiments");
            WriteStrings(json, "assigned", result.Experiments.Assigned);
            WriteStrings(json, "triggered", result.Experiments.Triggered);
            json.WriteStartArray("ignored");
            foreach (var ignored in result.Experiments.Ignored)
            {
                json.WriteStartObject();
                json.WriteString("id", ignored.Id);
                json.WriteString("reason", IgnoredArm.Name(ignored.Reason));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteStartArray("items");
            foreach (var item in result.Items)
            {
                json.WriteStartObject();
                json.WriteNumber("rank", item.Rank);
                if (item.Id.IsString)
                {
                    json.WriteString("id", item.Id.StringValue);
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
            json.WriteEndObject();
            await json.FlushAsync(http.RequestAborted);
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

        /// <summary>A value as JSON: missing is <c>null</c>, and so is a non-finite float, which JSON cannot represent.</summary>
        private static void WriteValue(Utf8JsonWriter json, in ScalarValue value)
        {
            switch (value.Kind)
            {
                case StorageKind.Bool:
                    json.WriteBooleanValue(value.AsBool());
                    break;
                case StorageKind.Int32 or StorageKind.Int64:
                    json.WriteNumberValue(value.AsInt64());
                    break;
                case StorageKind.Float32 or StorageKind.Float64 when double.IsFinite(value.AsFloat64()):
                    json.WriteNumberValue(value.AsFloat64());
                    break;
                case StorageKind.String or StorageKind.Text:
                    json.WriteStringValue(value.AsString());
                    break;
                case StorageKind.ListInt64:
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
                default: // missing
                    json.WriteNullValue();
                    break;
            }
        }
    }
}
