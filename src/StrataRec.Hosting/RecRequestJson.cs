using System.Text.Json;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>
/// What a caller's request document gives a <see cref="RecRequest"/>: the inputs, and the request id and the experiment
/// platform's assignment when the document has them. The host adds the rest (the pipeline, the deadline, debug options it
/// authorized).
/// </summary>
/// <param name="Inputs">The pipeline's request inputs, read by its request schema.</param>
/// <param name="RequestId">The caller's request id; null when the document has none (a host then uses its own, e.g. the trace id).</param>
/// <param name="Assignment">The arms the experiment platform answered, as <c>experiment/arm</c>; null when the document has none.</param>
public sealed record RecRequestDocument(RequestInputs Inputs, string? RequestId, IReadOnlyList<string>? Assignment);

/// <summary>
/// Reads the request document a caller sends a host, <c>{ "inputs": { … }, "request_id": "…", "assignment":
/// ["experiment/arm", …] }</c> with every member optional.
/// </summary>
/// <remarks>
/// <para>The inputs follow the pipeline's <c>request_schema</c> (<see cref="RequestSchema.TryRead"/>). What
/// System.Text.Json parses but a request must not hold (a member given twice, an escape that is not Unicode) is rejected
/// anywhere in the document first (<see cref="StrictJson"/>); so is an unknown member, with the closest name as a hint.
/// Each problem has the JSON pointer of the offending value.</para>
/// <para>Debug members (forced arms, a configuration override, a fixed time or seed) are not part of the document: who may
/// use them is the host's decision, and it sets them on <see cref="RecRequest"/> itself.</para>
/// </remarks>
public static class RecRequestJson
{
    private static readonly string[] Members = { "inputs", "request_id", "assignment" };

    /// <summary><see cref="JsonValueKind.Undefined"/> (an empty body) is a document without members.</summary>
    public static bool TryRead(
        in JsonElement document, RequestSchema schema, out RecRequestDocument request, out IReadOnlyList<JsonBindingError> errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        request = new RecRequestDocument(RequestInputs.Empty, null, null);
        string? requestId = null;
        List<string>? assignment = null;
        var inputs = default(JsonElement);
        if (document.ValueKind != JsonValueKind.Undefined)
        {
            if (document.ValueKind != JsonValueKind.Object)
            {
                errors = One(string.Empty, "The request must be a JSON object: { \"inputs\": { … }, \"request_id\": \"…\", \"assignment\": [\"experiment/arm\"] }.");
                return false;
            }

            if (StrictJson.Check(document) is { } strict)
            {
                errors = new[] { strict };
                return false;
            }

            foreach (var member in document.EnumerateObject())
            {
                var pointer = "/" + JsonPointer.Escape(member.Name);
                var value = member.Value;
                switch (member.Name)
                {
                    case "inputs":
                        inputs = value;
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
                        errors = One(pointer, $"Unknown member '{member.Name}' in the request.",
                            NameSuggestions.DidYouMean(member.Name, Members) ?? "a request has inputs, request_id and assignment");
                        return false;
                }
            }
        }

        if (!schema.TryRead(inputs, out var read, out errors))
        {
            return false;
        }

        request = new RecRequestDocument(read, requestId, assignment);
        return true;
    }

    private static JsonBindingError[] One(string pointer, string message, string? hint = null) => new[] { new JsonBindingError(pointer, message, hint) };
}
