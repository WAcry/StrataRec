using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec replay</c>: re-executes recorded requests with their recorded external responses and timing
/// decisions, and prints each result with its diff against the recorded one, the node decisions and parameters that
/// changed, and every piece of data the bundle lacked. It replays on a workspace (<c>-c</c>), on a release manifest
/// (<c>--manifest</c>), or, given only <c>--store</c>, each bundle on the manifest it recorded (<see cref="ReplayEngines"/>).
/// A bundle of an aborted request is not replayed (<see cref="ReplayOutcome.NotReplayed"/>): its recorded abort and node
/// decisions are printed instead, and it counts neither as replayed nor as an error.
/// </summary>
internal static class ReplayCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var config = CommonOptions.ConfigOrManifest();
        var manifest = CommonOptions.Manifest();
        var store = CommonOptions.Store();
        store.Description += " With neither -c nor --manifest, each bundle replays on the manifest it recorded, read from this store.";
        var environment = CommonOptions.Environment();
        var format = CommonOptions.Format();
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Replay with these arms (experiment/arm, comma or space separated) instead of the recorded assignment.",
            AllowMultipleArgumentsPerToken = true,
        };
        var expectIdentical = new Option<bool>("--expect-identical")
        {
            Description = "Exit with 1 unless every replayed bundle reproduces its recorded result exactly (a bundle of an aborted request is not replayed, so not checked).",
        };
        var explain = new Option<string[]>("--explain")
        {
            Description = $"Explain these candidates in each replayed run (entity ids of the pipeline's key type, comma or space separated, at most " +
                          $"{DebugOptions.MaxExplainIds}).",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("replay", "Replay recorded requests on a workspace, a release manifest, or (with only --store) the manifest each bundle " +
            "recorded, and diff against the recorded results. A bundle of an aborted request is not replayed, since it has no result to reproduce: its " +
            "recorded abort reason and node decisions are printed instead. Exits with 2 when a bundle could not be replayed (unreadable, or no " +
            "configuration to replay it on) or no bundle was replayed; bundles of aborted requests count against neither. The report is printed either way.")
        {
            config, manifest, store, environment, format, arms, expectIdentical, explain,
        };
        var bundles = CommonOptions.Bundles(command, "Replay bundles (from 'stratarec run --record'), or directories of them.");
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(config), parse.GetValue(environment)!, CommonOptions.BundlePaths(parse, bundles),
            parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(format)!, parse.GetValue(expectIdentical), parse.InvocationConfiguration.Output,
            cancellationToken, parse.GetValue(manifest), parse.GetValue(store), parse.InvocationConfiguration.Error, parse.GetValue(explain)));
        return command;
    }

    /// <param name="config">The workspace (<c>-c</c>). With <paramref name="manifest"/> null too: each bundle's recorded manifest when
    /// <paramref name="store"/> is given, else the workspace <c>config</c>.</param>
    /// <param name="manifest">A manifest id or file (<c>--manifest</c>), read with <paramref name="store"/>.</param>
    /// <param name="errors">Where diagnostics and per-bundle errors go; standard error by default.</param>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string? config, string environment, IReadOnlyList<string> bundlePaths, string[] armArgs, string format, bool expectIdentical, TextWriter output, CancellationToken cancellationToken,
        string? manifest = null, string? store = null, TextWriter? errors = null, string[]? explain = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        var (reference, ok) = ConfigRef.Select(config, manifest, store, store is null ? "config" : null, diagnostics, provided);
        if (!ok)
        {
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 2;
        }

        await using var engines = await ReplayEngines.CreateAsync(provided, reference, store, environment, format, errors, cancellationToken).ConfigureAwait(false);
        if (engines is null)
        {
            return 1;
        }

        var arms = ExplainPlanCommand.SplitArms(armArgs);
        var options = new ReplayOptions { Arms = arms.Count > 0 ? arms : null };
        var files = ReplayBundleFile.Find(bundlePaths);
        var results = new List<BundleReplay>();
        foreach (var file in files)
        {
            results.Add(await ReplayFileAsync(engines, file, options, errors, cancellationToken, explain).ConfigureAwait(false));
        }

        if (files.Count == 0)
        {
            errors.WriteLine($"{string.Join(", ", bundlePaths)}: no replay bundles found.");
        }

        // One bundle file given: its report alone, without the summary of a set.
        var singleFile = files.Count == 1 && bundlePaths.Count == 1 && !Directory.Exists(bundlePaths[0]);
        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json =>
            {
                if (singleFile)
                {
                    WriteReplay(json, results[0].File, results[0].Outcome, results[0].Error);
                    return;
                }

                json.WriteStartObject();
                WriteReplaySummary(json, results);
                json.WriteStartArray("bundles");
                foreach (var result in results)
                {
                    WriteReplay(json, result.File, result.Outcome, result.Error);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }));
        }
        else
        {
            foreach (var result in results)
            {
                WriteReplayText(output, result.File, result.Outcome, result.Error);
            }

            if (!singleFile)
            {
                var (replayed, aborted, unreadable, unresolved, identical) = Counts(results);
                output.WriteLine($"{Output.Count(results.Count, "bundle")}, {replayed} replayed, " +
                    (aborted > 0 ? $"{aborted} aborted in recording (not replayed), " : string.Empty) + $"{unreadable} unreadable, " +
                    (unresolved > 0 ? $"{unresolved} without a configuration to replay on, " : string.Empty) +
                    $"{identical} reproduced the recorded result exactly; status {Status(results)}");
            }
        }

        var different = false;
        foreach (var result in results)
        {
            different |= result.Outcome is null || (result.Outcome.NotReplayed is null && !result.Outcome.Report.Diff.Identical);
        }

        // A bundle not replayed for an error (unreadable, or its configuration unavailable), or nothing replayed at all, is an
        // input error even when the rest replayed identically. A bundle of an aborted request is neither: it is reported.
        return Status(results) != "ok" ? 2 : expectIdentical && different ? 1 : 0;
    }

    /// <summary>One bundle's replay, or why it was not replayed.</summary>
    /// <param name="Unresolved">Not replayed because the configuration it recorded is not available (not because the bundle is unreadable).</param>
    internal sealed record BundleReplay(string File, ReplayOutcome? Outcome, string? Error, bool Unresolved);

    /// <returns>Bundles replayed, bundles of aborted requests (not replayed), unreadable bundles, bundles without a
    /// configuration to replay on, and replays that reproduced their recorded result.</returns>
    private static (int Replayed, int Aborted, int Unreadable, int Unresolved, int Identical) Counts(List<BundleReplay> results)
    {
        int replayed = 0, aborted = 0, unreadable = 0, unresolved = 0, identical = 0;
        foreach (var result in results)
        {
            replayed += result.Outcome is { NotReplayed: null } ? 1 : 0;
            aborted += result.Outcome is { NotReplayed: not null } ? 1 : 0;
            unreadable += result.Error is not null && !result.Unresolved ? 1 : 0;
            unresolved += result.Unresolved ? 1 : 0;
            identical += result.Outcome is { NotReplayed: null, Report.Diff.Identical: true } ? 1 : 0;
        }

        return (replayed, aborted, unreadable, unresolved, identical);
    }

    /// <summary>
    /// <c>ok</c> (every bundle replayed, or of an aborted request), <c>partial</c> (some not replayed for an error) or
    /// <c>failed</c> (none replayed, or no bundles).
    /// </summary>
    private static string Status(List<BundleReplay> results)
    {
        var (replayed, aborted, _, _, _) = Counts(results);
        return replayed == 0 ? "failed" : replayed + aborted < results.Count ? "partial" : "ok";
    }

    /// <summary>Replays one bundle file on the engine <paramref name="engines"/> chooses for it.</summary>
    /// <param name="explain">The <c>--explain</c> ids, read as the key type of the pipeline the bundle replays on; an id that is
    /// not one is the bundle's error.</param>
    internal static async Task<BundleReplay> ReplayFileAsync(
        ReplayEngines engines, string file, ReplayOptions options, TextWriter errors, CancellationToken cancellationToken, string[]? explain = null)
    {
        string? error;
        var unresolved = false;
        try
        {
            var bundle = ReplayBundleFile.Load(file);
            if (RecEngine.Aborted(bundle) is { } aborted)
            {
                return new BundleReplay(file, aborted, null, false); // nothing to replay, so no configuration to find
            }

            var (engine, problem, hint) = await engines.ForAsync(bundle, cancellationToken).ConfigureAwait(false);
            if (engine is not null && explain is { Length: > 0 } && engine.TryGetPipeline(bundle.Request.PipelineId, out var loaded))
            {
                var pipelineId = bundle.Request.PipelineId;
                var ids = RunCommand.ParseExplainIds(explain, loaded.Plan.TableSchema.KeyType, pipelineId, out var explainError);
                if (explainError is not null)
                {
                    errors.WriteLine($"{file}: {explainError}");
                    return new BundleReplay(file, null, $"{file}: {explainError}", false);
                }

                options = new ReplayOptions { Arms = options.Arms, ExplainIds = ids };
            }

            if (engine is not null)
            {
                return new BundleReplay(file, await engine.ReplayAsync(bundle, options, cancellationToken).ConfigureAwait(false), null, false);
            }

            error = $"{file}: {problem} (hint: {hint})";
            unresolved = true;
        }
        catch (ReplayBundleException ex)
        {
            error = $"{file}: {ex.Message}" + (ex.Hint is null ? string.Empty : $" (hint: {ex.Hint})");
        }
        catch (IOException ex)
        {
            error = $"{file}: {ex.Message}";
        }

        errors.WriteLine(error);
        return new BundleReplay(file, null, error, unresolved);
    }

    private static void WriteReplaySummary(Utf8JsonWriter json, List<BundleReplay> results)
    {
        var (replayed, aborted, unreadable, unresolved, identical) = Counts(results);
        var gaps = 0;
        foreach (var result in results)
        {
            gaps += result.Outcome?.Report.Gaps.Count ?? 0;
        }

        json.WriteStartObject("summary");
        json.WriteString("status", Status(results));
        json.WriteNumber("bundles", results.Count);
        json.WriteNumber("replayed", replayed);
        json.WriteNumber("aborted_in_recording", aborted);
        json.WriteNumber("identical", identical);
        json.WriteNumber("unreadable", unreadable);
        json.WriteNumber("unresolved", unresolved);
        json.WriteNumber("gaps", gaps);
        json.WriteEndObject();
    }

    internal static void WriteReplay(Utf8JsonWriter json, string file, ReplayOutcome? outcome, string? error)
    {
        json.WriteStartObject();
        json.WriteString("bundle", file);
        if (outcome is null)
        {
            json.WriteString("error", error);
            json.WriteEndObject();
            return;
        }

        var report = outcome.Report;
        var result = outcome.Result;
        json.WriteString("request_id", outcome.Bundle.Request.RequestId);
        json.WriteString("pipeline_id", outcome.Bundle.Request.PipelineId);
        if (outcome.NotReplayed is { } abort)
        {
            WriteNotReplayed(json, report, abort);
            json.WriteEndObject();
            return;
        }

        json.WriteStartObject("manifest");
        json.WriteString("recorded", report.RecordedManifest);
        json.WriteString("replayed", report.ReplayedManifest);
        WriteChanges(json, "changed_artifacts", report.Artifacts);
        json.WriteEndObject();
        json.WriteStartObject("arms");
        WriteStrings(json, "recorded", report.RecordedArms);
        WriteStrings(json, "replayed", report.ReplayedArms);
        json.WriteEndObject();
        json.WriteBoolean("succeeded", result.Succeeded);
        if (result.Error is not null)
        {
            json.WriteString("error", result.Error);
        }

        json.WriteString("served_by", result.ServedBy);
        json.WriteNumber("elapsed_ms", Math.Round(report.ElapsedMs, 3));
        json.WritePropertyName("items"); // as the service answers them (run prints the same)
        RecResultJson.WriteItems(json, result.Columns);
        WriteDiff(json, "diff", report.Diff);
        json.WriteStartArray("nodes");
        foreach (var node in report.Nodes)
        {
            json.WriteStartObject();
            json.WriteString("node", node.Node);
            json.WriteString("recorded", node.Recorded);
            json.WriteString("replayed", node.Replayed);
            json.WriteBoolean("changed", node.Changed);
            if (node.Changed)
            {
                json.WriteString("recorded_detail", node.RecordedDetail);
                json.WriteString("replayed_detail", node.ReplayedDetail);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteChanges(json, "changed_params", report.Params);
        WriteGaps(json, "gaps", report.Gaps);
        json.WriteStartArray("feature_fields");
        foreach (var stats in report.FieldStats)
        {
            json.WriteStartObject();
            json.WriteString("source", stats.Source);
            json.WriteString("field", stats.Field);
            json.WriteNumber("requested", stats.Requested);
            json.WriteNumber("missing", stats.Missing);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteStrings(json, "notes", report.Notes);
        if (result.Explanation is { } explanation)
        {
            json.WritePropertyName("explanation");
            RecResultJson.WriteExplanation(json, explanation); // as 'run' writes it
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// A bundle of an aborted request: what it recorded, and no replay (no result, diff, gaps or parameters, which only a
    /// replay has).
    /// </summary>
    private static void WriteNotReplayed(Utf8JsonWriter json, ReplayReport report, RecordedAbort abort)
    {
        json.WriteStartObject("manifest");
        json.WriteString("recorded", report.RecordedManifest);
        json.WriteEndObject();
        json.WriteStartObject("arms");
        WriteStrings(json, "recorded", report.RecordedArms);
        json.WriteEndObject();
        json.WriteStartObject("not_replayed");
        json.WriteString("recorded_abort", abort.Reason);
        json.WriteString("node", abort.Node);
        json.WriteEndObject();
        json.WriteStartArray("nodes");
        foreach (var node in report.Nodes)
        {
            json.WriteStartObject();
            json.WriteString("node", node.Node);
            json.WriteString("recorded", node.Recorded);
            json.WriteString("recorded_detail", node.RecordedDetail);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    internal static void WriteDiff(Utf8JsonWriter json, string name, ResultDiff diff)
    {
        json.WriteStartObject(name);
        json.WriteBoolean("identical", diff.Identical);
        json.WriteBoolean("recorded_succeeded", diff.LeftSucceeded);
        json.WriteNumber("recorded_items", diff.LeftCount);
        json.WriteNumber("replayed_items", diff.RightCount);
        json.WriteNumber("in_both", diff.InBoth);
        json.WriteNumber("overlap", Math.Round(diff.Overlap, 6));
        if (diff.KendallTau is { } tau)
        {
            json.WriteNumber("kendall_tau", Math.Round(tau, 6));
        }
        else
        {
            json.WriteNull("kendall_tau");
        }

        WriteStrings(json, "added_columns", diff.AddedColumns);
        WriteStrings(json, "removed_columns", diff.RemovedColumns);
        json.WriteStartArray("retyped_columns");
        foreach (var retyped in diff.RetypedColumns)
        {
            json.WriteStartObject();
            json.WriteString("column", retyped.Column);
            json.WriteString("recorded", retyped.Recorded.ToString());
            json.WriteString("replayed", retyped.Replayed.ToString());
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("changes");
        foreach (var change in diff.Changes)
        {
            json.WriteStartObject();
            WriteKey(json, "id", change.Id);
            WriteRank(json, "recorded_rank", change.RecordedRank);
            WriteRank(json, "replayed_rank", change.ReplayedRank);
            if (change.Columns.Count > 0)
            {
                json.WriteStartObject("columns");
                foreach (var column in change.Columns)
                {
                    json.WriteStartObject(column.Column);
                    // As the items: NaN and ±∞ stay distinguishable from a missing value.
                    json.WritePropertyName("recorded");
                    Output.WriteValue(json, column.Recorded);
                    json.WritePropertyName("replayed");
                    Output.WriteValue(json, column.Replayed);
                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("value_changes");
        foreach (var change in diff.ValueChanges)
        {
            json.WriteStartObject();
            json.WriteString("value", change.Name);
            WriteSide(json, "recorded", change.Recorded);
            WriteSide(json, "replayed", change.Replayed);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();

        // A side without the value is absent; one with it gives its type and value (NaN and ±∞ distinguishable from missing).
        static void WriteSide(Utf8JsonWriter json, string side, ResultValue? value)
        {
            if (value is not { } present)
            {
                return;
            }

            json.WriteStartObject(side);
            json.WriteString("type", present.Type.ToString());
            json.WritePropertyName("value");
            Output.WriteValue(json, present.Value);
            json.WriteEndObject();
        }
    }

    internal static void WriteGaps(Utf8JsonWriter json, string name, IReadOnlyList<ReplayGap> gaps)
    {
        json.WriteStartArray(name);
        foreach (var gap in gaps)
        {
            json.WriteStartObject();
            json.WriteString("kind", gap.Kind);
            json.WriteString("source", gap.Source);
            if (gap.Field is not null)
            {
                json.WriteString("field", gap.Field);
            }

            json.WriteString("reason", gap.Reason);
            json.WriteNumber("count", gap.Count);
            WriteStrings(json, "examples", gap.Examples);
            json.WriteString("description", gap.Description);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteChanges(Utf8JsonWriter json, string name, IReadOnlyList<ValueChange> changes)
    {
        json.WriteStartArray(name);
        foreach (var change in changes)
        {
            json.WriteStartObject();
            json.WriteString("name", change.Name);
            json.WriteString("recorded", change.Recorded);
            json.WriteString("replayed", change.Replayed);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteReplayText(TextWriter output, string file, ReplayOutcome? outcome, string? error)
    {
        if (outcome is null)
        {
            output.WriteLine($"{file}: not replayed: {error}");
            output.WriteLine();
            return;
        }

        var report = outcome.Report;
        var result = outcome.Result;
        var diff = report.Diff;
        output.WriteLine($"{file}: request {outcome.Bundle.Request.RequestId}, pipeline {outcome.Bundle.Request.PipelineId}");
        if (outcome.NotReplayed is { } abort)
        {
            output.WriteLine($"  manifest  recorded {report.RecordedManifest}");
            output.WriteLine($"  arms      {Join(report.RecordedArms)}");
            output.WriteLine($"  result    not replayed: the recording was aborted ({abort.Reason})" + (abort.Node is { } running ? $" while {running} ran" : string.Empty) +
                "; an aborted request has no result to reproduce");
            foreach (var node in report.Nodes)
            {
                output.WriteLine($"  recorded  {node.Node}: {node.Recorded}" + (node.RecordedDetail is { } recordedDetail ? $" ({recordedDetail})" : string.Empty));
            }

            output.WriteLine();
            return;
        }

        output.WriteLine($"  manifest  recorded {report.RecordedManifest}, replayed {report.ReplayedManifest}");
        foreach (var artifact in report.Artifacts)
        {
            output.WriteLine($"  artifact  {artifact.Name}: {artifact.Recorded ?? "-"} -> {artifact.Replayed ?? "-"}");
        }

        output.WriteLine($"  arms      {Join(report.ReplayedArms)}" + (Join(report.RecordedArms) == Join(report.ReplayedArms) ? string.Empty : $" (recorded {Join(report.RecordedArms)})"));
        var elapsed = report.ElapsedMs.ToString("0.###", CultureInfo.InvariantCulture);
        output.WriteLine($"  result    {(result.Succeeded ? "succeeded" : "failed: " + result.Error)}, {Output.Count(result.Items.Count, "item")}, served by {result.ServedBy}, {elapsed} ms");
        output.WriteLine(diff switch
        {
            { Identical: true } => "  diff      identical to the recorded result",
            _ => $"  diff      differs: {diff.LeftCount} recorded vs {Output.Count(diff.RightCount, "replayed item")}, {diff.InBoth} in both " +
                 $"(overlap {diff.Overlap.ToString("0.###", CultureInfo.InvariantCulture)}), " +
                 $"kendall tau {(diff.KendallTau is { } tau ? tau.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")}",
        });
        if (diff.AddedColumns.Count > 0 || diff.RemovedColumns.Count > 0)
        {
            output.WriteLine($"    columns added: {Join(diff.AddedColumns)}; removed: {Join(diff.RemovedColumns)}");
        }

        foreach (var retyped in diff.RetypedColumns)
        {
            output.WriteLine($"    column {retyped.Column}: recorded as {retyped.Recorded}, replayed as {retyped.Replayed}");
        }

        foreach (var change in diff.Changes)
        {
            var columns = new List<string>();
            foreach (var column in change.Columns)
            {
                columns.Add($"{column.Column} {column.Recorded} -> {column.Replayed}");
            }

            var ranks = (change.RecordedRank, change.ReplayedRank) switch
            {
                ({ } before, null) => $"rank {before} -> absent",
                (null, { } after) => $"absent -> rank {after}",
                ({ } before, { } after) when before != after => $"rank {before} -> {after}",
                _ => $"rank {change.RecordedRank}",
            };
            output.WriteLine($"    item {change.Id}: {ranks}" + (columns.Count > 0 ? "; " + string.Join("; ", columns) : string.Empty));
        }

        foreach (var change in diff.ValueChanges)
        {
            output.WriteLine($"    value {change.Name}: {Side(change.Recorded, change.Replayed)} -> {Side(change.Replayed, change.Recorded)}");
        }

        foreach (var node in report.Nodes)
        {
            if (node.Changed)
            {
                output.WriteLine($"  node      {node.Node}: {node.Recorded ?? "-"} -> {node.Replayed ?? "-"}" + (node.ReplayedDetail is { } detail ? $" ({detail})" : string.Empty));
            }
        }

        foreach (var param in report.Params)
        {
            output.WriteLine($"  param     {param.Name}: {param.Recorded ?? "-"} -> {param.Replayed ?? "-"}");
        }

        foreach (var gap in report.Gaps)
        {
            output.WriteLine($"  gap       {gap.Description}" + (gap.Examples.Count > 0 ? $" (e.g. {string.Join(", ", gap.Examples)})" : string.Empty));
        }

        foreach (var note in report.Notes)
        {
            output.WriteLine($"  note      {note}");
        }

        if (result.Explanation is { } explanation)
        {
            ExplanationText.Write(output, explanation, "  explain   ");
        }

        output.WriteLine();

        static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "(none)" : string.Join(", ", values);

        // One side of a value change: absent, or its value, with its type when the other side has another.
        static string Side(ResultValue? side, ResultValue? other) => side switch
        {
            null => "absent",
            { } v when other is { } o && !o.Type.Equals(v.Type) => $"{v.Type} {v.Value}",
            { } v => v.Value.ToString(),
        };
    }

    internal static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    private static void WriteRank(Utf8JsonWriter json, string name, int? rank)
    {
        if (rank is { } value)
        {
            json.WriteNumber(name, value);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteKey(Utf8JsonWriter json, string name, EntityKey? key)
    {
        switch (key)
        {
            case null:
                json.WriteNull(name);
                break;
            case { StringValue: { } s }:
                json.WriteString(name, s);
                break;
            case { } k:
                json.WriteNumber(name, k.Int64Value);
                break;
        }
    }
}
