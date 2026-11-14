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
        var deadline = new Option<int?>("--deadline-ms")
        {
            Description = "Use this request deadline (milliseconds) instead of the request file's deadline_ms. The first request of a process " +
                          "also pays for compiling the code it runs (JIT), so a request that fits its deadline in a warm service may not fit " +
                          "here; raise it to see the logic without the clock. Node and source budgets are unchanged.",
        };
        deadline.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int?>() is <= 0)
            {
                result.AddError("--deadline-ms must be positive.");
            }
        });
        var command = new Command("run", "Execute a request locally and print the result as JSON.") { config, environment, request, arms, trace, logs, record, deadline };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(request)!,
            parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(trace), Console.Out, cancellationToken, parse.GetValue(logs), parse.GetValue(record),
            parse.GetValue(deadline)));
        return command;
    }

    internal static async Task<int> RunAsync(
        string config, string environment, string requestPath, string[] arms, bool trace, TextWriter output, CancellationToken cancellationToken, bool logs = false,
        string? recordPath = null, int? deadlineMs = null)
    {
        // Mistakes in the request file are reported like configuration errors, at file:line:column.
        var problems = new DiagnosticBag();
        var locator = DiagnosticLocator.ForWorkspace(config).AddFile(requestPath);
        var file = await ReadRequestAsync(requestPath, problems, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            Output.Diagnostics(problems.Items, "text", Console.Error, locator: locator);
            return 2;
        }

        var pipelineId = file.PipelineId;
        // The engine loads the workspace's local feature tables (design 6.6) before it serves; disposing it stops their sources.
        await using var engine = new RecEngine(CommonOptions.PlanEnvironment(config));
        var diagnostics = ServiceCollectionExtensions.LoadWorkspace(engine, config, environment, new[] { pipelineId });
        if (diagnostics.HasErrors || !engine.TryGetPipeline(pipelineId, out var pipeline))
        {
            Output.Diagnostics(diagnostics.Items, "text", Console.Error, locator: locator);
            return 1;
        }

        var inputs = ReadInputs(file, pipeline.Plan.RequestInputs, requestPath, problems);
        if (problems.HasErrors)
        {
            Output.Diagnostics(problems.Items, "text", Console.Error, locator: locator);
            return 2;
        }

        // --arms wins over the file's force_arms. A present force_arms is forced even when empty (the baseline), as an
        // empty DebugOptions.ForceArms is to the engine: presence, not length, decides.
        var cliArms = ExplainPlanCommand.SplitArms(arms);
        IReadOnlyList<string>? forced = cliArms.Count > 0 ? cliArms : file.ForceArms;

        var request = new RecRequest
        {
            PipelineId = pipelineId,
            RequestId = file.RequestId ?? "cli",
            Inputs = inputs,
            Assignment = file.Assignment,
            Deadline = TimeSpan.FromMilliseconds(deadlineMs ?? file.DeadlineMs ?? 1000),
            TimeMs = file.TimeMs,
            Seed = file.Seed,
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

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await engine.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && deadlineMs is null && System.Diagnostics.Stopwatch.GetElapsedTime(started) >= request.Deadline * 0.9)
        {
            // A one-shot process compiles what it runs on its first request: the deadline may be spent on that.
            Console.Error.WriteLine($"the request used its whole deadline ({request.Deadline.TotalMilliseconds} ms); the first request of a process " +
                                    "includes compiling the code it runs. Rerun with --deadline-ms 5000 to rule out the clock.");
        }

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
    /// Reads and binds the request file strictly, like an artifact (<see cref="ArtifactSerializer.TryBind{T}"/>): a
    /// misspelled member is an error with a did-you-mean rather than a key silently ignored. Null after reporting.
    /// </summary>
    private static async Task<RequestFile?> ReadRequestAsync(string requestPath, DiagnosticBag problems, CancellationToken cancellationToken)
    {
        JsonNode? document;
        try
        {
            document = Jsonc.Parse(await File.ReadAllBytesAsync(requestPath, cancellationToken).ConfigureAwait(false), requestPath);
        }
        catch (JsoncException ex)
        {
            problems.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, ex.Code, requestPath, string.Empty, ex.Reason, ex.Hint) { Line = ex.Line, Column = ex.Column });
            return null;
        }
        catch (IOException ex)
        {
            problems.Error("FILE_UNREADABLE", requestPath, string.Empty, ex.Message);
            return null;
        }

        if (!ArtifactSerializer.TryBind<RequestFile>(document, "the request", out var file, out var error))
        {
            problems.Error("REQUEST_INVALID", requestPath, error.Pointer, error.Message,
                error.Hint ?? "run 'stratarec run --help' for the members of a request file");
            return null;
        }

        CheckArms(file.ForceArms, "/force_arms", requestPath, problems);
        CheckArms(file.Assignment, "/assignment", requestPath, problems);
        if (file.DeadlineMs is <= 0)
        {
            problems.Error("REQUEST_INVALID", requestPath, "/deadline_ms", $"'deadline_ms' must be positive, got {file.DeadlineMs}.",
                "give the request a deadline in milliseconds, e.g. 200, or leave it out for 1000");
        }

        return problems.HasErrors ? null : file;
    }

    /// <summary>Every arm id is a string <c>experiment/arm</c>; JSON null is not one.</summary>
    private static void CheckArms(List<string>? arms, string pointer, string requestPath, DiagnosticBag problems)
    {
        for (var i = 0; arms is not null && i < arms.Count; i++)
        {
            if (arms[i] is null)
            {
                problems.Error("REQUEST_INVALID", requestPath, pointer + "/" + i, "An arm id must be a string, got null.",
                    "write each arm as \"experiment/arm\", e.g. \"exp_x/treatment\"");
            }
        }
    }

    /// <summary>The request's inputs, each checked against the pipeline's request_schema and reported where it is written.</summary>
    private static RequestInputs ReadInputs(RequestFile file, IReadOnlyDictionary<string, ContextField> declared, string requestPath, DiagnosticBag problems)
    {
        var inputs = new RequestInputs();
        if (file.Inputs is null)
        {
            return inputs;
        }

        foreach (var (name, value) in file.Inputs)
        {
            var pointer = "/inputs/" + JsonPointer.Escape(name);
            if (!declared.TryGetValue(name, out var field))
            {
                var names = new List<string>(declared.Keys);
                names.Sort(StringComparer.Ordinal);
                problems.Error("REQUEST_INVALID", requestPath, pointer, $"Input '{name}' is not declared in the request_schema of pipeline '{file.PipelineId}'.",
                    NameSuggestions.DidYouMean(name, names) ??
                    (names.Count > 0 ? "declared inputs: " + string.Join(", ", names) : "the pipeline declares no inputs; declare it under /request_schema"));
                continue;
            }

            if (value is null)
            {
                continue; // missing, as if absent
            }

            if (!ConfigValues.TryToScalar(value, field.InputType, out var scalar, out var error))
            {
                problems.Error("REQUEST_INVALID", requestPath, pointer, $"Input '{name}' {error}.",
                    $"the request_schema of pipeline '{file.PipelineId}' declares it as '{field.InputType}'");
                continue;
            }

            inputs.Set(name, scalar);
        }

        return inputs;
    }

    /// <summary>The request file of <c>stratarec run</c>.</summary>
    private sealed class RequestFile
    {
        /// <summary>The pipeline to run, by its pipeline_id.</summary>
        public required string PipelineId { get; init; }

        /// <summary>Request id; it seeds the request's randomness when seed is absent. Defaults to "cli".</summary>
        public string? RequestId { get; init; }

        /// <summary>Values for the pipeline's request_schema, by name.</summary>
        public JsonObject? Inputs { get; init; }

        /// <summary>The arms the experiment platform answered, as "experiment/arm"; [] is the answer "no arms".</summary>
        public List<string>? Assignment { get; init; }

        /// <summary>Arms to force over the assignment, as "experiment/arm"; [] forces the baseline.</summary>
        public List<string>? ForceArms { get; init; }

        /// <summary>End-to-end deadline in milliseconds; defaults to 1000.</summary>
        public double? DeadlineMs { get; init; }

        /// <summary>Request time in Unix milliseconds; defaults to now.</summary>
        public long? TimeMs { get; init; }

        /// <summary>Seed of the request's randomness; defaults to a hash of the request id.</summary>
        public long? Seed { get; init; }
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
