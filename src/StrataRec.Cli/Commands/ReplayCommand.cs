using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Cli.Commands;

/// <summary>
/// <c>stratarec replay</c> (design 13.4): re-executes recorded requests on a workspace with their recorded external
/// responses and timing decisions, and prints each result with its diff against the recorded one, the node decisions
/// and parameters that changed, and every piece of data the bundle lacked.
/// </summary>
internal static class ReplayCommand
{
    public static Command Create()
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var format = CommonOptions.Format();
        var bundle = new Option<string>("--bundle", "-b") { Description = "A replay bundle (from 'stratarec run --record'), or a directory of them.", Required = true };
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Replay with these arms (experiment/arm, comma or space separated) instead of the recorded assignment.",
            AllowMultipleArgumentsPerToken = true,
        };
        var expectIdentical = new Option<bool>("--expect-identical") { Description = "Exit with 1 unless every replay reproduces its recorded result exactly." };
        var command = new Command("replay", "Replay recorded requests on a workspace and diff against the recorded results.")
        {
            config, environment, format, bundle, arms, expectIdentical,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(bundle)!,
            parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(format)!, parse.GetValue(expectIdentical), Console.Out, cancellationToken));
        return command;
    }

    internal static async Task<int> RunAsync(
        string config, string environment, string bundlePath, string[] armArgs, string format, bool expectIdentical, TextWriter output, CancellationToken cancellationToken)
    {
        var engine = LoadEngine(config, environment, format);
        if (engine is null)
        {
            return 1;
        }

        var arms = ExplainPlanCommand.SplitArms(armArgs);
        var options = new ReplayOptions { Arms = arms.Count > 0 ? arms : null };
        var files = ReplayBundleFile.Find(bundlePath);
        var results = new List<(string File, ReplayOutcome? Outcome, string? Error)>();
        foreach (var file in files)
        {
            var (outcome, error) = await ReplayFileAsync(engine, file, options, cancellationToken).ConfigureAwait(false);
            results.Add((file, outcome, error));
        }

        var singleFile = files.Count == 1 && !Directory.Exists(bundlePath);
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
                foreach (var (file, outcome, error) in results)
                {
                    WriteReplay(json, file, outcome, error);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }));
        }
        else
        {
            foreach (var (file, outcome, error) in results)
            {
                WriteReplayText(output, file, outcome, error);
            }

            if (!singleFile)
            {
                var identical = 0;
                foreach (var (_, outcome, _) in results)
                {
                    identical += outcome?.Report.Diff.Identical == true ? 1 : 0;
                }

                output.WriteLine($"{results.Count} bundle(s), {identical} reproduced the recorded result exactly");
            }
        }

        var failed = false;
        var different = false;
        foreach (var (_, outcome, error) in results)
        {
            failed |= error is not null;
            different |= outcome is null || !outcome.Report.Diff.Identical;
        }

        return failed ? 2 : expectIdentical && different ? 1 : 0;
    }

    /// <summary>Loads every pipeline of a workspace into a fresh engine; prints diagnostics and returns null on errors.</summary>
    internal static RecEngine? LoadEngine(string config, string environment, string format)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = Path.GetFullPath(config) });
        var diagnostics = ServiceCollectionExtensions.LoadWorkspace(engine, config, environment);
        if (diagnostics.HasErrors)
        {
            Output.Diagnostics(diagnostics.Items, format, Console.Error);
            return null;
        }

        return engine;
    }

    /// <summary>Replays one bundle file; a malformed bundle is an error naming the file, the pointer and the fix.</summary>
    internal static async Task<(ReplayOutcome? Outcome, string? Error)> ReplayFileAsync(RecEngine engine, string file, ReplayOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var bundle = ReplayBundleFile.Load(file);
            return (await engine.ReplayAsync(bundle, options, cancellationToken).ConfigureAwait(false), null);
        }
        catch (ReplayBundleException ex)
        {
            var error = $"{file}: {ex.Message}" + (ex.Hint is null ? string.Empty : $" (hint: {ex.Hint})");
            Console.Error.WriteLine(error);
            return (null, error);
        }
        catch (IOException ex)
        {
            var error = $"{file}: {ex.Message}";
            Console.Error.WriteLine(error);
            return (null, error);
        }
    }

    private static void WriteReplaySummary(Utf8JsonWriter json, List<(string File, ReplayOutcome? Outcome, string? Error)> results)
    {
        int identical = 0, errors = 0, gaps = 0;
        foreach (var (_, outcome, error) in results)
        {
            identical += outcome?.Report.Diff.Identical == true ? 1 : 0;
            errors += error is not null ? 1 : 0;
            gaps += outcome?.Report.Gaps.Count ?? 0;
        }

        json.WriteStartObject("summary");
        json.WriteNumber("bundles", results.Count);
        json.WriteNumber("identical", identical);
        json.WriteNumber("unreadable", errors);
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
            json.WriteNumber("rank", change.Rank);
            WriteKey(json, "recorded_id", change.RecordedId);
            WriteKey(json, "replayed_id", change.ReplayedId);
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
            : $"  result    {(result.Succeeded ? "succeeded" : "failed: " + result.Error)}, {result.Items.Count} item(s), served by {result.ServedBy}, {elapsed} ms");
        output.WriteLine(diff switch
        {
            { Identical: true, LeftAborted: true } => "  diff      aborted, as recorded; node decisions up to the abort are compared below",
            { Identical: true } => "  diff      identical to the recorded result",
            { LeftAborted: true } => "  diff      differs: the recorded request was aborted, the replay returned a result",
            { RightAborted: true } => "  diff      differs: the replay was aborted, the recorded request returned a result",
            _ => $"  diff      differs: {diff.LeftCount} recorded vs {diff.RightCount} replayed item(s), overlap {diff.Overlap.ToString("0.###", CultureInfo.InvariantCulture)}, " +
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

            output.WriteLine($"    rank {change.Rank}: {Id(change.RecordedId)} -> {Id(change.ReplayedId)}" + (columns.Count > 0 ? "; " + string.Join("; ", columns) : string.Empty));
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

        output.WriteLine();

        static string Id(EntityKey? key) => key?.ToString() ?? "-";
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
