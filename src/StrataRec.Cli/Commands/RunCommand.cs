using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Features.Catalog;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
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
        var request = new Option<string>("--request", "-r")
        {
            Description = "Request JSON: pipeline_id, request_id, inputs, assignment (the arms the experiment platform answered, experiment/arm), " +
                          "deadline_ms, time_ms, seed, force_arms (forced arms over the assignment; [] forces the baseline).",
            Required = true,
        };
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Force these arms (experiment/arm), overriding the request file's force_arms and assignment; unknown arms fail the request.",
            AllowMultipleArgumentsPerToken = true,
        };
        var trace = new Option<bool>("--trace", "-t") { Description = "Include every node's outcome." };
        var logs = new Option<bool>("--logs", "-l") { Description = "Include the log batches the request's taps captured (nothing is written)." };
        var record = new Option<string?>("--record")
        {
            Description = "Also write a replay bundle of the request to this file (design 13.4); replay it with 'stratarec replay'.",
        };
        var command = new Command("run", "Execute a request locally and print the result as JSON.") { config, environment, request, arms, trace, logs, record };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(request)!,
            parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(trace), Console.Out, cancellationToken, parse.GetValue(logs), parse.GetValue(record)));
        return command;
    }

    internal static async Task<int> RunAsync(
        string config, string environment, string requestPath, string[] arms, bool trace, TextWriter output, CancellationToken cancellationToken, bool logs = false,
        string? recordPath = null)
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
        // The engine loads the workspace's local feature tables (design 6.6) before it serves; disposing it stops their sources.
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = Path.GetFullPath(config) });
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

                if (!ConfigValues.TryToScalar(value, field.InputType, out var scalar, out var error))
                {
                    Console.Error.WriteLine($"{requestPath}: /inputs/{name}: {error}");
                    return 2;
                }

                inputs.Set(name, scalar);
            }
        }

        if (!TryReadArms(document, "force_arms", requestPath, out var forceArms) || !TryReadArms(document, "assignment", requestPath, out var assignment))
        {
            return 2;
        }

        // --arms wins over the file's force_arms. A present force_arms is forced even when empty (the baseline), as an
        // empty DebugOptions.ForceArms is to the engine: presence, not length, decides.
        var cliArms = ExplainPlanCommand.SplitArms(arms);
        IReadOnlyList<string>? forced = cliArms.Count > 0 ? cliArms : forceArms;

        var request = new RecRequest
        {
            PipelineId = pipelineId,
            RequestId = document["request_id"]?.GetValue<string>() ?? "cli",
            Inputs = inputs,
            Assignment = assignment,
            Deadline = TimeSpan.FromMilliseconds(document["deadline_ms"]?.GetValue<double>() ?? 1000),
            TimeMs = document["time_ms"]?.GetValue<long>(),
            Seed = document["seed"]?.GetValue<long>(),
            Debug = new DebugOptions
            {
                ForceArms = forced,
                Trace = trace,
                CaptureLogs = logs,
                Record = recordPath is not null,
                // A request cancelled with Ctrl-C returns no result: its bundle comes through the callback.
                OnRecording = recordPath is null ? null : bundle => SaveAborted(bundle, recordPath),
            },
        };

        var result = await engine.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (recordPath is not null)
        {
            if (result.Recording is { } bundle)
            {
                ReplayBundleFile.Save(bundle, recordPath);
                Console.Error.WriteLine($"recorded {request.RequestId} to {recordPath}");
            }
            else
            {
                Console.Error.WriteLine($"nothing recorded: the request was rejected before execution ({result.Error})");
            }
        }

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

            if (result.LogBatches is { } batches)
            {
                json.WriteStartArray("logs");
                foreach (var batch in batches)
                {
                    Logging.LogBatchJson.Write(json, batch);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }));
        return result.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// Reads an optional array of <c>experiment/arm</c> ids: null when the member is absent, an empty list for <c>[]</c>
    /// (the platform answered no arms). A malformed member is reported with its pointer.
    /// </summary>
    private static bool TryReadArms(JsonObject document, string name, string requestPath, out List<string>? arms)
    {
        arms = null;
        if (!document.TryGetPropertyValue(name, out var node) || node is null)
        {
            return true;
        }

        if (node is not JsonArray array)
        {
            Console.Error.WriteLine($"{requestPath}: /{name}: expected an array of arm ids, e.g. [\"exp_x/treatment\"].");
            return false;
        }

        arms = new List<string>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonValue value || !value.TryGetValue<string>(out var id))
            {
                Console.Error.WriteLine($"{requestPath}: /{name}/{i}: an arm id is a string 'experiment/arm'.");
                return false;
            }

            arms.Add(id);
        }

        return true;
    }

    private static void SaveAborted(ReplayBundle bundle, string path)
    {
        if (!bundle.Result.Aborted)
        {
            return; // saved with the result
        }

        try
        {
            ReplayBundleFile.Save(bundle, path);
            Console.Error.WriteLine($"recorded aborted request {bundle.Request.RequestId} to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{path}: {ex.Message}");
        }
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
