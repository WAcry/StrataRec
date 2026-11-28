using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

internal static class RunCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var config = CommonOptions.ConfigOrManifest();
        var manifest = CommonOptions.Manifest();
        manifest.Description += " Only its pipeline is loaded, as a node loads it, bindings included.";
        var store = CommonOptions.Store();
        var environment = CommonOptions.Environment();
        var request = new Option<string>("--request", "-r")
        {
            Description = "Request JSON: pipeline_id, request_id, inputs, assignment (the arms the experiment platform answered, experiment/arm), " +
                          "deadline_ms, time_ms, seed, force_arms (forced arms over the assignment; [] forces the baseline), config_override (a merge patch of the " +
                          "pipeline to try on this request, applied after the arms); " +
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
            Description = "Also write a replay bundle of the request to this file; the command that replays it is printed.",
        };
        var deadline = new Option<int?>("--deadline-ms")
        {
            Description = "Use this request deadline (milliseconds) instead of the request file's deadline_ms. The first request of a process " +
                          "also pays for compiling the code it runs (JIT), so a request that fits its deadline in a warm service may not fit " +
                          "here; raise it to see the logic without the clock. Node and source budgets are unchanged.",
        };
        deadline.Validators.Add(static result =>
        {
            if (CommonOptions.Converted<int>(result) is <= 0)
            {
                result.AddError("--deadline-ms must be positive.");
            }
        });
        var explain = new Option<string[]>("--explain")
        {
            Description = $"Explain these candidates (entity ids of the pipeline's key type, comma or space separated, at most {DebugOptions.MaxExplainIds}): " +
                          "the result's 'explanation' says where each ended and its state after every node.",
            AllowMultipleArgumentsPerToken = true,
        };
        var format = CommonOptions.Format();
        format.Description = "Output format: json, the document a service answers with (the default), or text, for reading: the items one per line, " +
                             "and the trace and explained candidates when asked for.";
        format.DefaultValueFactory = _ => "json";
        var command = new Command("run",
            "Execute a request locally on a workspace or a release manifest and print the result (JSON, or text with --format text). Exits with 0 when the request " +
            "succeeds, 1 when it fails (the result says why), the configuration does not load or the --record file cannot be written, 2 when the request file or an " +
            "--explain id is invalid, and on usage errors.")
        {
            config, manifest, store, environment, request, arms, trace, logs, record, deadline, explain, format,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(config), parse.GetValue(environment)!, parse.GetValue(request)!,
            parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(trace), parse.InvocationConfiguration.Output, cancellationToken, parse.GetValue(logs),
            parse.GetValue(record), parse.GetValue(deadline), parse.InvocationConfiguration.Error, parse.GetValue(explain), parse.GetValue(format)!,
            parse.GetValue(manifest), parse.GetValue(store)));
        return command;
    }

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

    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string? config, string environment, string requestPath, string[] arms, bool trace, TextWriter output,
        CancellationToken cancellationToken, bool logs = false, string? recordPath = null, int? deadlineMs = null, TextWriter? errors = null,
        string[]? explain = null, string format = "json", string? manifest = null, string? store = null)
    {
        errors ??= Console.Error;
        var problems = new DiagnosticBag();
        var (reference, ok) = ConfigRef.Select(config, manifest, store, "config", problems, provided);
        if (!ok)
        {
            Output.Diagnostics(problems.Items, "text", errors);
            return 2;
        }

        var locator = (reference!.Locator() ?? new DiagnosticLocator()).AddFile(requestPath);
        var file = await ReadRequestAsync(requestPath, reference.IsManifest ? null : reference.Text, problems, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            Output.Diagnostics(problems.Items, "text", errors, locator: locator);
            return 2;
        }

        var pipelineId = file.PipelineId;
        (ReleaseManifest Manifest, string Id, IArtifactStore Store)? release = null;
        if (reference.IsManifest)
        {
            release = await reference.ReadManifestAsync(problems, cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                Output.Diagnostics(problems.Items, "text", errors, locator: locator);
                return 1;
            }

            if (release.Value.Manifest.PipelineId != pipelineId)
            {
                problems.Error("REQUEST_INVALID", requestPath, "/pipeline_id",
                    $"The request runs pipeline '{pipelineId}', and the manifest releases pipeline '{release.Value.Manifest.PipelineId}'.",
                    $"pass a manifest of pipeline '{pipelineId}', or set \"pipeline_id\": \"{release.Value.Manifest.PipelineId}\"");
                Output.Diagnostics(problems.Items, "text", errors, locator: locator);
                return 2;
            }
        }

        await using var engine = new RecEngine(reference.PlanEnvironment());
        var (diagnostics, notices) = await LoadAsync(engine, reference, release, environment, pipelineId, cancellationToken).ConfigureAwait(false);
        if (diagnostics.HasErrors || !engine.TryGetRequestSchema(pipelineId, out var schema))
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: locator);
            if (reference.IsManifest && ConfigRef.CurrentDirectoryNote(diagnostics.Items) is { } note)
            {
                errors.WriteLine(note);
            }

            return 1;
        }

        if (notices.Count > 0)
        {
            Output.Diagnostics(notices, "text", errors);
        }

        if (!schema.TryRead(file.Inputs ?? default, out var inputs, out var inputErrors))
        {
            foreach (var error in inputErrors)
            {
                problems.Error("REQUEST_INVALID", requestPath, error.Pointer, error.Message, error.Hint);
            }

            Output.Diagnostics(problems.Items, "text", errors, locator: locator);
            return 2;
        }

        engine.TryGetPipeline(pipelineId, out var loaded);
        var explainIds = ParseExplainIds(explain, loaded.Plan.TableSchema.KeyType, pipelineId, out var explainError);
        if (explainError is not null)
        {
            errors.WriteLine(explainError);
            return 2;
        }

        // A present force_arms is forced even when empty (the baseline), as an empty DebugOptions.ForceArms is to the
        // engine: presence, not length, decides.
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
                ConfigOverride = file.ConfigOverride,
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
            errors.WriteLine($"the request used its whole deadline ({request.Deadline.TotalMilliseconds} ms); the first request of a process " +
                             "includes compiling the code it runs. Rerun with --deadline-ms 5000 to rule out the clock.");
        }

        // A bundle that cannot be written is said, and the result printed all the same: the request ran.
        var recorded = true;
        if (recordPath is not null)
        {
            if (result.Recording is { } bundle)
            {
                var saving = new DiagnosticBag();
                recorded = Output.WriteFile("--record", recordPath, path => ReplayBundleFile.Save(bundle, path), saving);
                if (recorded)
                {
                    errors.WriteLine($"recorded {request.RequestId} to {recordPath}; replay it with: {ReplayCommandLine(reference, recordPath)}");
                }
                else
                {
                    Output.Diagnostics(saving.Items, "text", errors);
                }
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
            var options = new RecResultJsonOptions { IncludeColumnProducers = trace };
            output.WriteLine(Output.ToJson(json => RecResultJson.Write(json, result, options)));
        }
        return result.Succeeded && recorded ? 0 : 1;
    }

    /// <summary>
    /// The command that replays a bundle just recorded, to copy into a shell: each path quoted for the shell, and one that
    /// starts with '-' written ./-b, which the command line would otherwise read as an option.
    /// </summary>
    internal static string ReplayCommandLine(string config, string bundle) =>
        $"stratarec replay -c {InitCommand.Quote(AsPath(config))} {InitCommand.Quote(AsPath(bundle))}";

    /// <summary>
    /// The same for a run on <paramref name="reference"/>: a bundle of a manifest replays on it, from its store, where the
    /// manifest's id is found; a manifest file is named, since it need not be in the store.
    /// </summary>
    private static string ReplayCommandLine(ConfigRef reference, string bundle) => reference.Kind switch
    {
        ConfigRefKind.ManifestId => $"stratarec replay -s {InitCommand.Quote(AsPath(reference.Store!))} {InitCommand.Quote(AsPath(bundle))}",
        ConfigRefKind.ManifestFile =>
            $"stratarec replay --manifest {InitCommand.Quote(AsPath(reference.Text))} -s {InitCommand.Quote(AsPath(reference.Store!))} {InitCommand.Quote(AsPath(bundle))}",
        _ => ReplayCommandLine(reference.Text, bundle),
    };

    private static async Task<(DiagnosticBag Diagnostics, List<ConfigDiagnostic> Notices)> LoadAsync(
        RecEngine engine, ConfigRef reference, (ReleaseManifest Manifest, string Id, IArtifactStore Store)? release, string environment, string pipelineId,
        CancellationToken cancellationToken)
    {
        if (release is not { } read)
        {
            return (await ServiceCollectionExtensions.LoadWorkspaceAsync(engine, reference.Text, environment, new[] { pipelineId }, cancellationToken)
                .ConfigureAwait(false), []);
        }

        var diagnostics = new DiagnosticBag();
        var loaded = await engine.LoadManifestAsync(read.Manifest, read.Id, read.Store, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(loaded.Diagnostics);
        if (!loaded.Applied && !diagnostics.HasErrors)
        {
            diagnostics.Error("MANIFEST_NOT_APPLIED", "manifest:" + read.Id, string.Empty, $"The manifest was not applied: {loaded.Reason}.");
        }

        // Each on its own: a pipeline may both fall back and call.
        var notices = new List<ConfigDiagnostic>();
        if (loaded.Applied && engine.TryGetPipeline(pipelineId, out var served) && served.FallbackPipeline is { } fallback && fallback != pipelineId)
        {
            notices.Add(new ConfigDiagnostic(DiagnosticSeverity.Warning, "RUN_FALLBACK_NOT_LOADED", "manifest:" + read.Id, string.Empty,
                $"Pipeline '{pipelineId}' falls back to '{fallback}', which a run on one manifest does not load: a request that fails here does not fall back.",
                "run on the workspace (-c) to try the fallback too"));
        }

        if (loaded.Applied && engine.TryGetPipeline(pipelineId, out var calling) && !calling.Plan.Calls.IsEmpty)
        {
            notices.Add(new ConfigDiagnostic(DiagnosticSeverity.Warning, "RUN_CALLED_NOT_LOADED", "manifest:" + read.Id, string.Empty,
                $"Pipeline '{pipelineId}' calls {string.Join(", ", calling.Plan.Calls)}, which a run on one manifest does not load: those calls fail.",
                "run on the workspace (-c) to run the pipelines it calls too"));
        }

        return (diagnostics, notices);
    }

    private static string AsPath(string path) => path.StartsWith('-') ? "./" + path : path;

    private static async Task<RequestFile?> ReadRequestAsync(string requestPath, string? config, DiagnosticBag problems, CancellationToken cancellationToken)
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

        var reference = config is null
            ? "../" + ArtifactSchemas.DirectoryName + "/" + ArtifactSchemas.FileName(SchemaFiles.RequestKind) // a manifest has no workspace: still the request schema, not the helper's default (the pipeline's)
            : Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(requestPath))!,
                Path.Combine(Path.GetFullPath(config), ArtifactSchemas.DirectoryName, ArtifactSchemas.FileName(SchemaFiles.RequestKind))).Replace('\\', '/');
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

        // null is the binder's error (a non-nullable member); an empty string binds, and names no pipeline.
        if (file.PipelineId.Length == 0)
        {
            problems.Error("REQUEST_INVALID", requestPath, "/pipeline_id", "'pipeline_id' must name a pipeline, got an empty string.",
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

        /// <summary>
        /// A configuration to try on this request alone: an RFC 7396 merge patch of the pipeline as an experiment arm
        /// writes one (params, nodes, log_taps, columns, param_schema, funnel_groups, features, derived_features),
        /// applied after the arms, so it wins over them; its taps are captured, never written.
        /// </summary>
        public JsonObject? ConfigOverride { get; init; }

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

        var saving = new DiagnosticBag();
        if (Output.WriteFile("--record", path, file => ReplayBundleFile.Save(bundle, file), saving))
        {
            errors.WriteLine($"recorded aborted request {bundle.Request.RequestId} to {path}");
        }
        else
        {
            Output.Diagnostics(saving.Items, "text", errors);
        }
    }
}
