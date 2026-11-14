using System.Text.Json;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Samples.Host;

/// <summary>
/// <c>POST /recommend/{pipeline}</c>: the HTTP transport around <see cref="IRecEngine.ExecuteAsync"/>. The engine knows no
/// transport, so the host turns the body into a <see cref="RecRequest"/>, and the <see cref="RecResult"/> into a response;
/// the rules for inputs (<see cref="RequestSchema.TryRead"/>) and the result document (<see cref="RecResultJson"/>) are the
/// engine's, shared with the CLI.
/// </summary>
/// <remarks>
/// <para>The body: <c>{ "inputs": { … }, "request_id": "…", "assignment": ["experiment/arm", …] }</c>, all optional.
/// <c>inputs</c> follow the pipeline's <c>request_schema</c>; <c>assignment</c> is what the experiment platform answered
/// for this request (without it, a registered <c>IAssignmentProvider</c> is asked, and without one the request runs the
/// baseline).</para>
/// <para>Status codes follow the engine's <see cref="RecErrorKind"/>: 404 for a pipeline the engine does not serve, 400 for
/// a body or input the request schema rejects (with the JSON pointer of the problem), 504 when the deadline was spent
/// before the pipeline could start (waiting for the experiment platform), 500 when the pipeline ran and failed (with the
/// engine's message). Errors are RFC 9457 problem details.</para>
/// </remarks>
public static class RecommendEndpoint
{
    public static async Task<IResult> HandleAsync(string pipeline, HttpRequest http, IRecEngine engine, SampleSettings settings, CancellationToken cancellationToken)
    {
        // The inputs are read with the schema of the plan served now. A release loaded before ExecuteAsync may declare
        // others: the engine checks the inputs again against the plan it runs, and its answer (Answer) is the one that counts.
        if (!engine.TryGetRequestSchema(pipeline, out var schema))
        {
            return UnknownPipeline(pipeline, engine);
        }

        // A JsonDocument, not a JsonObject: a JsonObject materializes its members lazily and throws on a duplicate name
        // only when they are first enumerated; a document keeps both, which Read rejects as an invalid request.
        JsonDocument? body;
        try
        {
            body = http.ContentLength == 0 ? null : await JsonDocument.ParseAsync(http.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return BadRequest(new JsonBindingError(string.Empty, $"The body is not JSON: {ex.Message}", null));
        }

        RecRequest request;
        using (body)
        {
            if (!Read(body?.RootElement ?? default, schema, out var inputs, out var requestId, out var assignment, out var errors))
            {
                return BadRequest(errors);
            }

            request = new RecRequest
            {
                PipelineId = pipeline,
                RequestId = requestId ?? http.HttpContext.TraceIdentifier, // ties the engine's span and logs to this HTTP request
                Inputs = inputs,
                Assignment = assignment,
                Deadline = settings.Deadline,
                // Debug options (trace, forced arms, recording) are the host's to authorize, e.g. for internal callers only.
            };
        }

        // The caller's disconnect cancels the request: what it started stops, and nothing is written back.
        return Answer(await engine.ExecuteAsync(request, cancellationToken), engine);
    }

    /// <summary>The response to a result: the result document, or the problem its <see cref="RecResult.ErrorKind"/> names.</summary>
    public static IResult Answer(RecResult result, IRecEngine engine) => result.ErrorKind switch
    {
        RecErrorKind.None => new Response(result),
        RecErrorKind.UnknownPipeline => UnknownPipeline(result.ServedBy, engine), // unloaded since its schema was read
        RecErrorKind.InvalidRequest => BadRequest(new JsonBindingError(result.ErrorPointer ?? string.Empty, result.Error!, null)),
        RecErrorKind.DeadlineExceeded => Problem(StatusCodes.Status504GatewayTimeout, "Deadline exceeded", result),
        RecErrorKind.Failed => Problem(StatusCodes.Status500InternalServerError, "Recommendation failed", result),
    };

    private static readonly string[] Members = { "inputs", "request_id", "assignment" };

    /// <summary>
    /// The body's members. The inputs are read by the pipeline's request schema, with the engine's rules and words; the
    /// rest is this transport's. What System.Text.Json parses but a request must not hold (a member given twice, an escape
    /// that is not Unicode) is rejected first, anywhere in the body (<see cref="StrictJson"/>). An empty body has no members.
    /// </summary>
    private static bool Read(
        in JsonElement body, RequestSchema schema, out RequestInputs inputs, out string? requestId, out List<string>? assignment,
        out IReadOnlyList<JsonBindingError> errors)
    {
        inputs = RequestInputs.Empty;
        requestId = null;
        assignment = null;
        var inputsMember = default(JsonElement);
        if (body.ValueKind != JsonValueKind.Undefined)
        {
            if (body.ValueKind != JsonValueKind.Object)
            {
                errors = One(string.Empty, "The body must be a JSON object: { \"inputs\": { … }, \"request_id\": \"…\", \"assignment\": [\"experiment/arm\"] }.");
                return false;
            }

            if (StrictJson.Check(body) is { } strict)
            {
                errors = new[] { strict };
                return false;
            }

            foreach (var member in body.EnumerateObject())
            {
                var pointer = "/" + JsonPointer.Escape(member.Name);
                var value = member.Value;
                switch (member.Name)
                {
                    case "inputs":
                        inputsMember = value;
                        break;
                    case "request_id" when value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } id:
                        requestId = id;
                        break;
                    case "request_id":
                        errors = One(pointer, $"request_id must be a non-empty string, got {JsonWords.Value(value)}.");
                        return false;
                    case "assignment" when value.ValueKind == JsonValueKind.Array:
                        assignment = new List<string>(value.GetArrayLength());
                        foreach (var arm in value.EnumerateArray())
                        {
                            if (arm.ValueKind != JsonValueKind.String)
                            {
                                errors = One($"{pointer}/{assignment.Count}", $"An arm id is a string 'experiment/arm', got {JsonWords.Value(arm)}.");
                                return false;
                            }

                            assignment.Add(arm.GetString()!);
                        }

                        break;
                    case "assignment":
                        errors = One(pointer, $"assignment must be an array of arm ids, e.g. [\"exp_local_boost/strong\"], got {JsonWords.Value(value)}.");
                        return false;
                    default:
                        errors = One(pointer, $"Unknown member '{member.Name}' in the request body.",
                            NameSuggestions.DidYouMean(member.Name, Members) ?? "the body has inputs, request_id and assignment");
                        return false;
                }
            }
        }

        if (!schema.TryRead(inputsMember, out var read, out errors))
        {
            return false;
        }

        inputs = read;
        return true;
    }

    private static IResult UnknownPipeline(string pipeline, IRecEngine engine) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown pipeline", detail: $"Unknown pipeline '{pipeline}'.",
            extensions: new Dictionary<string, object?> { ["pipelines"] = engine.PipelineIds() });

    private static IResult Problem(int status, string title, RecResult result) =>
        TypedResults.Problem(statusCode: status, title: title, detail: result.Error,
            extensions: new Dictionary<string, object?> { ["request_id"] = result.RequestId, ["served_by"] = result.ServedBy });

    /// <summary>400 with the pointer of the first problem, and every problem under <c>errors</c>.</summary>
    private static IResult BadRequest(params IReadOnlyList<JsonBindingError> errors)
    {
        var all = new List<object>(errors.Count);
        foreach (var error in errors)
        {
            all.Add(new { pointer = error.Pointer, detail = error.Message, hint = error.Hint });
        }

        return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: errors[0].Message,
            extensions: new Dictionary<string, object?> { ["pointer"] = errors[0].Pointer, ["errors"] = all });
    }

    private static JsonBindingError[] One(string pointer, string message, string? hint = null) => new[] { new JsonBindingError(pointer, message, hint) };

    /// <summary>The result document (<see cref="RecResultJson"/>), written straight to the response.</summary>
    private sealed class Response(RecResult result) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.ContentType = "application/json; charset=utf-8";
            await using var json = new Utf8JsonWriter(http.Response.Body);
            RecResultJson.Write(json, result);
            await json.FlushAsync(http.RequestAborted);
        }
    }
}
