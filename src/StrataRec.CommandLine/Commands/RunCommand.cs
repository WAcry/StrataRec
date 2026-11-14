using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec run</c>: executes a request against a workspace locally (fixtures via <c>memory@1</c> and friends) and
/// prints the result as JSON (<see cref="RecResultJson"/>, the document a service answers with): items, assignment and
/// trigger record, with <c>--trace</c> every node's outcome and the node that produced each output column, and with
/// <c>--explain</c> the explained candidates (design 13.3). <c>--format text</c> prints the same for reading
/// (<see cref="ResultText"/>).
/// </summary>
internal static class RunCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var request = new Option<string>("--request", "-r")
        {
            Description = "Request JSON: pipeline_id, request_id, inputs, assignment (the arms the experiment platform answered, experiment/arm), " +
                          "deadline_ms, time_ms, seed, force_arms (forced arms over the assignment; [] forces the baseline); " +
                          "'stratarec schema request' prints its JSON Schema, which \"$schema\" may name for an editor.",
            Required = true,
        };
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Force these arms (experiment/arm), overriding the request file's force_arms and assignment; unknown arms fail the request.",
            AllowMultipleArgumentsPerToken = true,
        };
        var trace = new Option<bool>("--trace", "-t") { Description = "Include every node's outcome, and the node that produced each output column." };
        var logs = new Option<bool>("--logs", "-l") { Description = "Include the log batches the request's taps captured (nothing is written)." };
        var record = new Option<string?>("--record")
        {
            Description = "Also write a replay bundle of the request to this file (design 13.4); replay it with 'stratarec replay -c <workspace> <file>'.",
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
        var explain = new Option<string[]>("--explain")
        {
            Description = $"Explain these candidates (entity ids of the pipeline's key type, comma or space separated, at most {DebugOptions.MaxExplainIds}): " +
                          "the result's 'explanation' says where each ended and its state after every node (design 13.3).",
            AllowMultipleArgumentsPerToken = true,
        };
        var format = CommonOptions.Format();
        format.Description = "Output format: json, the document a service answers with (the default), or text, for reading: the items one per line, " +
                             "and the trace and explained candidates when asked for.";
        format.DefaultValueFactory = _ => "json";
        var command = new Command("run",
            "Execute a request locally and print the result (JSON, or text with --format text). Exits with 0 when the request succeeds, 1 when it fails (the result says why) or the " +
            "configuration does not load, 2 when the request file or an --explain id is invalid.")
        {
            config, environment, request, arms, trace, logs, record, deadline, explain, format,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(request)!,
            parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(trace), parse.InvocationConfiguration.Output, cancellationToken, parse.GetValue(logs),
            parse.GetValue(record), parse.GetValue(deadline), parse.InvocationConfiguration.Error, parse.GetValue(explain), parse.GetValue(format)!));
        return command;
    }

    /// <summary>
    /// The ids of <c>--explain</c> (comma or space separated) read as the pipeline's key type; null, after reporting, when
    /// one is not an id of that type. None given is null: nothing to explain.
    /// </summary>
    internal static IReadOnlyList<EntityKey>? ParseExplainIds(string[]? args, EntityKeyType keyType, string pipelineId, out string? error)
    {
        error = null;
        var ids = new List<EntityKey>();
        foreach (var arg in args ?? Array.Empty<string>())
        {
            foreach (var part in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!ExplainTargets.TryParse(part, keyType, out var id))
                {
                    error = $"--explain: '{part}' is not an entity id of pipeline '{pipelineId}', whose ids are {ExplainTargets.Name(keyType)}s";
                    return null;
                }

                ids.Add(id);
            }
        }

        return ids.Count == 0 ? null : ids;
    }

    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    /// <param name="errors">Where diagnostics and progress go; standard error by default.</param>
    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string config, string environment, string requestPath, string[] arms, bool trace, TextWriter output,
        CancellationToken cancellationToken, bool logs = false, string? recordPath = null, int? deadlineMs = null, TextWriter? errors = null,
        string[]? explain = null, string format = "json")
    {
        errors ??= Console.Error;

        // Mistakes in the request file are reported like configuration errors, at file:line:column.
        var problems = new DiagnosticBag();
        var locator = DiagnosticLocator.ForWorkspace(config).AddFile(requestPath);
        var file = await ReadRequestAsync(requestPath, config, problems, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            Output.Diagnostics(problems.Items, "text", errors, locator: locator);
            return 2;
        }

        var pipelineId = file.PipelineId;
        // The engine loads the workspace's local feature tables (design 6.6) before it serves; disposing it stops their sources.
        await using var engine = new RecEngine(CommonOptions.ForRoot(provided, config));
        var diagnostics = await ServiceCollectionExtensions.LoadWorkspaceAsync(engine, config, environment, new[] { pipelineId }, cancellationToken).ConfigureAwait(false);
        if (diagnostics.HasErrors || !engine.TryGetRequestSchema(pipelineId, out var schema))
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: locator);
            return 1;
        }

        // The inputs as a service reads them (RequestSchema.TryRead): the same rules and words, here at file:line:column.
        if (!schema.TryRead(file.Inputs ?? default, out var inputs, out var inputErrors))
        {
            foreach (var error in inputErrors)
            {
                problems.Error("REQUEST_INVALID", requestPath, error.Pointer, error.Message, error.Hint);
            }

            Output.Diagnostics(problems.Items, "text", errors, locator: locator);
            return 2;
        }

        // The ids as the pipeline keys its candidates: 42 is an int64 for an int64-keyed pipeline, a string otherwise.
        engine.TryGetPipeline(pipelineId, out var loaded);
        var explainIds = ParseExplainIds(explain, loaded.Plan.TableSchema.KeyType, pipelineId, out var explainError);
        if (explainError is not null)
        {
            errors.WriteLine(explainError);
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
                ExplainIds = explainIds,
                // A request cancelled with Ctrl-C returns no result: its bundle comes through the callback.
                OnRecording = recordPath is null ? null : bundle => SaveAborted(bundle, recordPath, errors),
            },
        };

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await engine.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && deadlineMs is null && System.Diagnostics.Stopwatch.GetElapsedTime(started) >= request.Deadline * 0.9)
        {
            // A one-shot process compiles what it runs on its first request: the deadline may be spent on that.
            errors.WriteLine($"the request used its whole deadline ({request.Deadline.TotalMilliseconds} ms); the first request of a process " +
                             "includes compiling the code it runs. Rerun with --deadline-ms 5000 to rule out the clock.");
        }

        if (recordPath is not null)
        {
            if (result.Recording is { } bundle)
            {
                ReplayBundleFile.Save(bundle, recordPath);
                errors.WriteLine($"recorded {request.RequestId} to {recordPath}; replay it with: " +
                                 $"stratarec replay -c {InitCommand.Quote(config)} {InitCommand.Quote(recordPath)}");
            }
            else
            {
                errors.WriteLine($"nothing recorded: the request was rejected before execution ({result.Error})");
            }
        }

        if (format == "text")
        {
            ResultText.Write(output, result);
        }
        else
        {
            // The document a service answers with (RecResultJson), plus the lineage --trace asks for.
            var options = new RecResultJsonOptions { IncludeColumnProducers = trace };
            output.WriteLine(Output.ToJson(json => RecResultJson.Write(json, result, options)));
        }
        return result.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// Reads and binds the request file strictly, like an artifact (<see cref="ArtifactSerializer.TryBind{T}"/>): a
    /// misspelled member is an error with a did-you-mean rather than a key silently ignored, and a member given twice is
    /// an error at its line (<see cref="Jsonc"/>) rather than one of the two values. A top-level <c>$schema</c> is the
    /// editor's, as in a workspace file (<see cref="ArtifactSchemas.TryRemoveSchemaMember"/>). Null after reporting.
    /// </summary>
    /// <param name="config">The workspace, whose <c>schemas/</c> the hint about <c>$schema</c> names.</param>
    private static async Task<RequestFile?> ReadRequestAsync(string requestPath, string config, DiagnosticBag problems, CancellationToken cancellationToken)
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

        var schema = Path.Combine(Path.GetFullPath(config), ArtifactSchemas.DirectoryName, ArtifactSchemas.FileName(SchemaFiles.RequestKind));
        var reference = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(requestPath))!, schema).Replace('\\', '/');
        if (!ArtifactSchemas.TryRemoveSchemaMember(document, reference, out var schemaError))
        {
            problems.Error("REQUEST_INVALID", requestPath, schemaError.Pointer, schemaError.Message, schemaError.Hint);
            return null;
        }

        if (!ArtifactSerializer.TryBind<RequestFile>(document, "the request", out var file, out var error))
        {
            problems.Error("REQUEST_INVALID", requestPath, error.Pointer, error.Message,
                error.Hint ?? "run 'stratarec run --help' for the members of a request file");
            return null;
        }

        // A required member written as null binds (it is present): it names no pipeline.
        if (string.IsNullOrEmpty(file.PipelineId))
        {
            problems.Error("REQUEST_INVALID", requestPath, "/pipeline_id",
                $"'pipeline_id' must name a pipeline, got {(file.PipelineId is null ? "null" : "an empty string")}.",
                "give the id of the pipeline to run, e.g. \"pipeline_id\": \"home_feed\"");
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

    /// <summary>The request file of <c>stratarec run</c>; its JSON Schema is <c>schemas/request.schema.json</c> (<see cref="SchemaFiles"/>).</summary>
    internal sealed class RequestFile
    {
        /// <summary>The pipeline to run, by its pipeline_id.</summary>
        public required string PipelineId { get; init; }

        /// <summary>Request id; it seeds the request's randomness when seed is absent. Defaults to "cli".</summary>
        public string? RequestId { get; init; }

        /// <summary>Values for the pipeline's request_schema, by name.</summary>
        public JsonElement? Inputs { get; init; }

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

    private static void SaveAborted(ReplayBundle bundle, string path, TextWriter errors)
    {
        if (!bundle.Result.Aborted)
        {
            return; // saved with the result
        }

        try
        {
            ReplayBundleFile.Save(bundle, path);
            errors.WriteLine($"recorded aborted request {bundle.Request.RequestId} to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.WriteLine($"{path}: {ex.Message}");
        }
    }
}
