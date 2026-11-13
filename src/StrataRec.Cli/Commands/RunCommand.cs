using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;
using StrataRec.Hosting;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Cli.Commands;

/// <summary>
/// <c>stratarec run</c>: executes a request against a workspace locally (fixtures via <c>memory@1</c> and friends) and
/// prints the result as JSON: items, assignment and trigger record, and with <c>--trace</c> every node's outcome.
/// </summary>
internal static class RunCommand
{
    public static Command Create()
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var request = new Option<string>("--request", "-r") { Description = "Request JSON: pipeline_id, request_id, inputs, deadline_ms, time_ms, force_arms.", Required = true };
        var arms = new Option<string[]>("--arms", "-a") { Description = "Force these arms (experiment/arm).", AllowMultipleArgumentsPerToken = true };
        var trace = new Option<bool>("--trace", "-t") { Description = "Include every node's outcome." };
        var command = new Command("run", "Execute a request locally and print the result as JSON.") { config, environment, request, arms, trace };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(request)!,
            parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(trace), Console.Out, cancellationToken));
        return command;
    }

    internal static async Task<int> RunAsync(string config, string environment, string requestPath, string[] arms, bool trace, TextWriter output, CancellationToken cancellationToken)
    {
        JsonObject document;
        try
        {
            document = Jsonc.Parse(await File.ReadAllBytesAsync(requestPath, cancellationToken).ConfigureAwait(false), requestPath) as JsonObject
                       ?? throw new JsonException("the request must be a JSON object");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Console.Error.WriteLine($"{requestPath}: {ex.Message}");
            return 2;
        }

        var pipelineId = document["pipeline_id"]?.GetValue<string>();
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = Path.GetFullPath(config) });
        var diagnostics = ServiceCollectionExtensions.LoadWorkspace(engine, config, environment, pipelineId is null ? null : new[] { pipelineId });
        if (diagnostics.HasErrors || pipelineId is null || !engine.TryGetPipeline(pipelineId, out var pipeline))
        {
            if (pipelineId is null)
            {
                diagnostics.Error("REQUEST_INVALID", requestPath, "/pipeline_id", "The request needs a pipeline_id.");
            }

            Output.Diagnostics(diagnostics.Items, "text", Console.Error);
            return 1;
        }

        var inputs = new RequestInputs();
        if (document["inputs"] is JsonObject values)
        {
            foreach (var (name, value) in values)
            {
                if (!pipeline.Plan.RequestInputs.TryGetValue(name, out var field))
                {
                    Console.Error.WriteLine($"{requestPath}: /inputs/{name}: not declared in request_schema of '{pipelineId}'.");
                    return 2;
                }

                if (value is null)
                {
                    continue;
                }

                if (!ConfigValues.TryToScalar(value, field.Type, out var scalar, out var error))
                {
                    Console.Error.WriteLine($"{requestPath}: /inputs/{name}: {error}");
                    return 2;
                }

                inputs.Set(name, scalar);
            }
        }

        var forced = ExplainPlanCommand.SplitArms(arms);
        if (forced.Count == 0 && document["force_arms"] is JsonArray forceArms)
        {
            foreach (var arm in forceArms)
            {
                forced.Add(arm!.GetValue<string>());
            }
        }

        var request = new RecRequest
        {
            PipelineId = pipelineId,
            RequestId = document["request_id"]?.GetValue<string>() ?? "cli",
            Inputs = inputs,
            Deadline = TimeSpan.FromMilliseconds(document["deadline_ms"]?.GetValue<double>() ?? 1000),
            TimeMs = document["time_ms"]?.GetValue<long>(),
            Seed = document["seed"]?.GetValue<long>(),
            Debug = new DebugOptions { ForceArms = forced.Count > 0 ? forced : null, Trace = trace },
        };

        var result = await engine.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        output.WriteLine(Output.ToJson(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("succeeded", result.Succeeded);
            if (result.Error is not null)
            {
                json.WriteString("error", result.Error);
            }

            json.WriteString("served_by", result.ServedBy);
            json.WriteString("manifest", result.ManifestId);
            json.WriteStartObject("experiments");
            WriteStrings(json, "assigned", result.Experiments.Assigned);
            WriteStrings(json, "triggered", result.Experiments.Triggered);
            json.WriteEndObject();
            json.WriteStartArray("items");
            foreach (var item in result.Items)
            {
                json.WriteStartObject();
                json.WriteNumber("rank", item.Rank);
                json.WriteString("id", item.Id.ToString());
                foreach (var (name, value) in item.Columns)
                {
                    json.WritePropertyName(name);
                    Output.WriteValue(json, value);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            if (result.Trace is { } outcomes)
            {
                json.WriteStartArray("trace");
                foreach (var outcome in outcomes)
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

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }));
        return result.Succeeded ? 0 : 1;
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
}
