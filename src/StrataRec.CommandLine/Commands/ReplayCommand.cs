using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec replay</c> (design 13.4): re-executes recorded requests with their recorded external responses and timing
/// decisions, and prints each result with its diff against the recorded one, the node decisions and parameters that
/// changed, and every piece of data the bundle lacked. It replays on a workspace (<c>-c</c>), on a release manifest
/// (<c>--manifest</c>), or, given only <c>--store</c>, each bundle on the manifest it recorded (<see cref="ReplayEngines"/>).
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
        var expectIdentical = new Option<bool>("--expect-identical") { Description = "Exit with 1 unless every replay reproduces its recorded result exactly." };
        var explain = new Option<string[]>("--explain")
        {
            Description = $"Explain these candidates in each replayed run (entity ids of the pipeline's key type, comma or space separated, at most " +
                          $"{DebugOptions.MaxExplainIds}; design 13.3).",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("replay", "Replay recorded requests on a workspace, a release manifest, or (with only --store) the manifest each bundle " +
            "recorded, and diff against the recorded results. Exits with 2 unless every bundle was replayed; the report is printed either way.")
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
                var (replayed, unreadable, unresolved, identical) = Counts(results);
                output.WriteLine($"{Output.Count(results.Count, "bundle")}, {replayed} replayed, {unreadable} unreadable, " +
                    (unresolved > 0 ? $"{unresolved} without a configuration to replay on, " : string.Empty) +
                    $"{identical} reproduced the recorded result exactly; status {Status(results)}");
            }
        }

        var different = false;
        foreach (var result in results)
        {
            different |= result.Outcome is null || !result.Outcome.Report.Diff.Identical;
        }

        // A bundle not replayed (unreadable, or its configuration unavailable), or nothing to replay at all, is an input error
        // even when the rest replayed identically.
        return Status(results) != "ok" ? 2 : expectIdentical && different ? 1 : 0;
    }

    /// <summary>One bundle's replay, or why it was not replayed.</summary>
    /// <param name="Unresolved">Not replayed because the configuration it recorded is not available (not because the bundle is unreadable).</param>
    internal sealed record BundleReplay(string File, ReplayOutcome? Outcome, string? Error, bool Unresolved);

    private static (int Replayed, int Unreadable, int Unresolved, int Identical) Counts(List<BundleReplay> results)
    {
        int replayed = 0, unreadable = 0, unresolved = 0, identical = 0;
        foreach (var result in results)
        {
            replayed += result.Outcome is not null ? 1 : 0;
            unreadable += result.Error is not null && !result.Unresolved ? 1 : 0;
            unresolved += result.Unresolved ? 1 : 0;
            identical += result.Outcome?.Report.Diff.Identical == true ? 1 : 0;
        }

        return (replayed, unreadable, unresolved, identical);
    }

    /// <summary><c>ok</c> (every bundle replayed), <c>partial</c> (some not replayed) or <c>failed</c> (none replayed, or no bundles).</summary>
    private static string Status(List<BundleReplay> results)
    {
        var (replayed, _, _, _) = Counts(results);
        return replayed == 0 ? "failed" : replayed < results.Count ? "partial" : "ok";
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
        var (replayed, unreadable, unresolved, identical) = Counts(results);
        var gaps = 0;
        foreach (var result in results)
        {
            gaps += result.Outcome?.Report.Gaps.Count ?? 0;
        }

        json.WriteStartObject("summary");
        json.WriteString("status", Status(results));
        json.WriteNumber("bundles", results.Count);
        json.WriteNumber("replayed", replayed);
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
        json.WriteBoolean("aborted", outcome.Aborted); // stopped where the recording was aborted: no result, error is the reason
        if (result.Error is not null)
        {
            json.WriteString("error", result.Error);
        }

        json.WriteString("served_by", result.ServedBy);
        json.WriteNumber("elapsed_ms", Math.Round(report.ElapsedMs, 3));
        json.WriteStartArray("items");
        foreach (var item in result.Items)
        {
            json.WriteStartObject();
            json.WriteNumber("rank", item.Rank);
            WriteKey(json, "id", item.Id);
            foreach (var (name, value) in item.Columns)
            {
                json.WritePropertyName(name);
                Output.WriteValue(json, value);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
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

    internal static void WriteDiff(Utf8JsonWriter json, string name, ResultDiff diff)
    {
        json.WriteStartObject(name);
        json.WriteBoolean("identical", diff.Identical);
        json.WriteBoolean("recorded_succeeded", diff.LeftSucceeded);
        json.WriteBoolean("recorded_aborted", diff.LeftAborted);
        json.WriteBoolean("replayed_aborted", diff.RightAborted);
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
                    // The bundle encoding: NaN and ±∞ stay distinguishable from a missing value.
                    json.WritePropertyName("recorded");
                    Logging.LogBatchJson.WriteValue(json, column.Recorded, nonFiniteAsStrings: true);
                    json.WritePropertyName("replayed");
                    Logging.LogBatchJson.WriteValue(json, column.Replayed, nonFiniteAsStrings: true);
                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
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
        output.WriteLine($"  manifest  recorded {report.RecordedManifest}, replayed {report.ReplayedManifest}");
        foreach (var artifact in report.Artifacts)
        {
            output.WriteLine($"  artifact  {artifact.Name}: {artifact.Recorded ?? "-"} -> {artifact.Replayed ?? "-"}");
        }

        output.WriteLine($"  arms      {Join(report.ReplayedArms)}" + (Join(report.RecordedArms) == Join(report.ReplayedArms) ? string.Empty : $" (recorded {Join(report.RecordedArms)})"));
        var elapsed = report.ElapsedMs.ToString("0.###", CultureInfo.InvariantCulture);
        output.WriteLine(outcome.Aborted
            ? $"  result    aborted where the recording was ({outcome.AbortReason}): no result, in {result.ServedBy}, {elapsed} ms"
            : $"  result    {(result.Succeeded ? "succeeded" : "failed: " + result.Error)}, {Output.Count(result.Items.Count, "item")}, served by {result.ServedBy}, {elapsed} ms");
        output.WriteLine(diff switch
        {
            { Identical: true, LeftAborted: true } => "  diff      aborted, as recorded; node decisions up to the abort are compared below",
            { Identical: true } => "  diff      identical to the recorded result",
            { LeftAborted: true } => "  diff      differs: the recorded request was aborted, the replay returned a result",
            { RightAborted: true } => "  diff      differs: the replay was aborted, the recorded request returned a result",
            _ => $"  diff      differs: {diff.LeftCount} recorded vs {Output.Count(diff.RightCount, "replayed item")}, {diff.InBoth} in both " +
                 $"(overlap {diff.Overlap.ToString("0.###", CultureInfo.InvariantCulture)}), " +
                 $"kendall tau {(diff.KendallTau is { } tau ? tau.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")}",
        });
        if (diff.AddedColumns.Count > 0 || diff.RemovedColumns.Count > 0)
        {
            output.WriteLine($"    columns added: {Join(diff.AddedColumns)}; removed: {Join(diff.RemovedColumns)}");
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
