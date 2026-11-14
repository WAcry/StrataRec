using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec compare</c>: the recorded-mode comparison of design 10.8. Replays every bundle on a baseline and a
/// candidate configuration (workspaces or release manifests, <see cref="ConfigRef"/>) and reports latency, error and
/// empty rates, Top-K overlap, Kendall τ, the score distribution change and the feature-missing rate, plus every gap the
/// candidate hit.
/// </summary>
internal static class CompareCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var candidate = CommonOptions.ConfigOrManifest();
        candidate.Description = "Candidate configuration directory; default 'config'. Not with --manifest.";
        var manifest = CommonOptions.Manifest();
        manifest.Description = "Candidate release manifest instead of a directory: its content id (sha256:<hex>) in --store, or a manifest file. Not with -c.";
        var store = CommonOptions.Store();
        var environment = CommonOptions.Environment();
        var format = CommonOptions.Format();
        var baseline = new Option<string>("--baseline")
        {
            Description = "Baseline configuration: a workspace directory (e.g. the main branch), a manifest content id (sha256:<hex>) in --store, or a manifest file.",
            Required = true,
        };
        var bundles = new Option<string>("--bundle", "-b") { Description = "A directory of replay bundles (or one bundle).", Required = true };
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Replay both sides with these arms (experiment/arm) instead of each bundle's recorded assignment.",
            AllowMultipleArgumentsPerToken = true,
        };
        var topK = new Option<int>("--top-k", "-k") { Description = "K of the Top-K overlap.", DefaultValueFactory = _ => 10 };
        var score = new Option<string>("--score") { Description = "Output column whose distribution is compared.", DefaultValueFactory = _ => "score" };
        var command = new Command("compare", "Replay recorded requests on a baseline and a candidate configuration and compare the results (design 10.8). " +
            "Exits with 2 unless every bundle was compared; the report is printed either way.")
        {
            candidate, manifest, store, environment, format, baseline, bundles, arms, topK, score,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(baseline)!, parse.GetValue(candidate), parse.GetValue(environment)!,
            parse.GetValue(bundles)!, parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(topK), parse.GetValue(score)!, parse.GetValue(format)!,
            parse.InvocationConfiguration.Output, cancellationToken, parse.GetValue(manifest), parse.GetValue(store), parse.InvocationConfiguration.Error));
        return command;
    }

    /// <param name="baselineRef">A workspace directory, manifest id or manifest file (<see cref="ConfigRef.Parse"/>).</param>
    /// <param name="candidateConfig">The candidate workspace (<c>-c</c>); with <paramref name="candidateManifest"/> null too, the workspace <c>config</c>.</param>
    /// <param name="errors">Where diagnostics go; standard error by default.</param>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string baselineRef, string? candidateConfig, string environment, string bundlePath, string[] armArgs, int topK, string scoreColumn, string format,
        TextWriter output, CancellationToken cancellationToken, string? candidateManifest = null, string? store = null, TextWriter? errors = null)
    {
        errors ??= Console.Error;
        if (topK < 1)
        {
            errors.WriteLine("--top-k must be at least 1.");
            return 2;
        }

        var diagnostics = new DiagnosticBag();
        var baselineConfig = ConfigRef.Parse(baselineRef, store, "--baseline", diagnostics, provided);
        var (candidateRef, ok) = ConfigRef.Select(candidateConfig, candidateManifest, store, "config", diagnostics, provided);
        if (baselineConfig is null || !ok)
        {
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 2;
        }

        // Each side resolves a bundle's pipelines as replay does: a fallback pipeline the request ran comes from the side's
        // workspace, or from the manifest it recorded when the side is a manifest (ReplayEngines).
        await using var baseline = await ReplayEngines.CreateAsync(provided, baselineConfig, store, environment, format, errors, cancellationToken).ConfigureAwait(false);
        await using var candidate = await ReplayEngines.CreateAsync(provided, candidateRef!, store, environment, format, errors, cancellationToken).ConfigureAwait(false);
        if (baseline is null || candidate is null)
        {
            return 1;
        }

        var arms = ExplainPlanCommand.SplitArms(armArgs);
        var options = new ReplayOptions { Arms = arms.Count > 0 ? arms : null };
        var pairs = new List<ReplayPair>();
        var files = ReplayBundleFile.Find(bundlePath);
        if (files.Count > 0)
        {
            // The first replay in the process pays for JIT compilation; unwarmed, that lands in whichever side replays
            // first and inflates its latency percentiles.
            await ReplayCommand.ReplayFileAsync(baseline, files[0], options, TextWriter.Null, cancellationToken).ConfigureAwait(false);
            await ReplayCommand.ReplayFileAsync(candidate, files[0], options, TextWriter.Null, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in files)
        {
            var before = await ReplayCommand.ReplayFileAsync(baseline, file, options, errors, cancellationToken).ConfigureAwait(false);
            if (before.Outcome is null)
            {
                pairs.Add(new ReplayPair(file, string.Empty, null, null, before.Error));
                continue;
            }

            var after = await ReplayCommand.ReplayFileAsync(candidate, file, options, errors, cancellationToken).ConfigureAwait(false);
            pairs.Add(new ReplayPair(file, before.Outcome.Bundle.Request.RequestId, before.Outcome, after.Outcome, after.Error));
        }

        if (pairs.Count == 0)
        {
            errors.WriteLine($"{bundlePath}: no replay bundles found.");
        }

        var report = ReplayComparison.Compare(pairs, topK, scoreColumn, Manifest(pairs, static p => p.Baseline), Manifest(pairs, static p => p.Candidate));
        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json => WriteJson(json, report, baselineConfig.Text, candidateRef!.Text)));
        }
        else
        {
            WriteText(output, report, baselineConfig.Text, candidateRef!.Text);
        }

        // Metrics over part of the batch (or none of it) must not pass for a clean comparison in CI.
        return report.Status == "ok" ? 0 : 2;
    }

    private static string Manifest(List<ReplayPair> pairs, Func<ReplayPair, ReplayOutcome?> side)
    {
        foreach (var pair in pairs)
        {
            if (side(pair) is { } outcome)
            {
                return outcome.Report.ReplayedManifest;
            }
        }

        return string.Empty;
    }

    private static void WriteJson(Utf8JsonWriter json, ComparisonReport report, string baseline, string candidate)
    {
        json.WriteStartObject();
        json.WriteString("mode", "recorded");
        json.WriteString("status", report.Status);
        json.WriteNumber("bundles", report.Bundles);
        json.WriteNumber("compared", report.Compared);
        json.WriteNumber("unreadable", report.Unreadable);
        json.WriteNumber("top_k", report.TopK);
        json.WriteString("score_column", report.ScoreColumn);
        WriteSide(json, "baseline", baseline, report.Baseline);
        WriteSide(json, "candidate", candidate, report.Candidate);
        json.WriteStartObject("comparison");
        json.WriteNumber("identical", report.Identical);
        WriteDistribution(json, "top_k_overlap", report.Overlap);
        WriteDistribution(json, "kendall_tau", report.KendallTau);
        json.WriteNumber("score_ks", Math.Round(report.ScoreKs, 6));
        WriteDistribution(json, "score_delta", report.ScoreDelta);
        json.WriteStartArray("feature_missing");
        foreach (var change in report.FeatureMissing)
        {
            json.WriteStartObject();
            json.WriteString("source", change.Source);
            json.WriteString("field", change.Field);
            WriteRate(json, "baseline", change.Baseline);
            WriteRate(json, "candidate", change.Candidate);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteStartArray("requests");
        foreach (var request in report.Requests)
        {
            json.WriteStartObject();
            json.WriteString("bundle", request.Name);
            json.WriteString("request_id", request.RequestId);
            if (request.Error is not null)
            {
                json.WriteString("error", request.Error);
            }

            json.WriteBoolean("baseline_succeeded", request.BaselineSucceeded);
            json.WriteBoolean("candidate_succeeded", request.CandidateSucceeded);
            json.WriteNumber("baseline_items", request.BaselineItems);
            json.WriteNumber("candidate_items", request.CandidateItems);
            json.WriteNumber("top_k_overlap", Math.Round(request.Overlap, 6));
            if (request.KendallTau is { } tau)
            {
                json.WriteNumber("kendall_tau", Math.Round(tau, 6));
            }
            else
            {
                json.WriteNull("kendall_tau");
            }

            json.WriteBoolean("identical", request.Identical);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteSide(Utf8JsonWriter json, string name, string config, ReplaySideSummary side)
    {
        json.WriteStartObject(name);
        json.WriteString("config", config);
        json.WriteString("manifest", side.Manifest);
        json.WriteNumber("requests", side.Requests);
        WriteDistribution(json, "latency_ms", side.LatencyMs);
        json.WriteNumber("error_rate", Math.Round(side.ErrorRate, 6));
        json.WriteNumber("aborted", side.Aborted);
        json.WriteNumber("empty_rate", Math.Round(side.EmptyRate, 6));
        json.WriteNumber("degraded_rate", Math.Round(side.DegradedRate, 6));
        json.WriteNumber("feature_missing_rate", Math.Round(side.FeatureMissingRate, 6));
        WriteDistribution(json, "score", side.Scores);
        json.WriteNumber("non_finite_scores", side.NonFiniteScores);
        json.WriteNumber("reproduced_recording", side.ReproducedRecording);
        ReplayCommand.WriteGaps(json, "gaps", side.Gaps);
        json.WriteEndObject();
    }

    private static void WriteDistribution(Utf8JsonWriter json, string name, Distribution distribution)
    {
        json.WriteStartObject(name);
        json.WriteNumber("count", distribution.Count);
        Write(json, "mean", distribution.Mean);
        Write(json, "min", distribution.Min);
        Write(json, "p50", distribution.P50);
        Write(json, "p90", distribution.P90);
        Write(json, "p95", distribution.P95);
        Write(json, "max", distribution.Max);
        json.WriteEndObject();

        static void Write(Utf8JsonWriter json, string name, double? value)
        {
            if (value is { } v)
            {
                json.WriteNumber(name, Math.Round(v, 6));
            }
            else
            {
                json.WriteNull(name);
            }
        }
    }

    private static void WriteRate(Utf8JsonWriter json, string name, double? rate)
    {
        if (rate is { } r)
        {
            json.WriteNumber(name, Math.Round(r, 6));
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteText(TextWriter output, ComparisonReport report, string baseline, string candidate)
    {
        output.WriteLine($"Recorded-mode comparison of {report.Bundles} bundle(s) (design 10.8); I/O is answered from the recordings.");
        output.WriteLine(report.Status switch
        {
            "ok" => $"status: ok, {report.Compared} compared",
            "partial" => $"status: partial, {report.Compared} compared, {report.Unreadable} unreadable (the metrics cover only the compared bundles)",
            _ => $"status: failed, nothing compared ({report.Unreadable} unreadable)",
        });
        output.WriteLine();
        // The baseline column is as wide as its longest value (paths and manifest ids can be long).
        var width = Math.Max(22, Math.Max(baseline.Length, report.Baseline.Manifest.Length) + 2);
        output.WriteLine($"{"",-26}{"baseline".PadRight(width)}candidate");
        Row("config", baseline, candidate);
        Row("manifest", report.Baseline.Manifest, report.Candidate.Manifest);
        Row("requests", Number(report.Baseline.Requests), Number(report.Candidate.Requests));
        Row("latency p50 / p95 (ms)", Pair(report.Baseline.LatencyMs.P50, report.Baseline.LatencyMs.P95), Pair(report.Candidate.LatencyMs.P50, report.Candidate.LatencyMs.P95));
        Row("error rate", Percent(report.Baseline.ErrorRate), Percent(report.Candidate.ErrorRate));
        if (report.Baseline.Aborted + report.Candidate.Aborted > 0)
        {
            Row("aborted (as recorded)", Number(report.Baseline.Aborted), Number(report.Candidate.Aborted));
        }

        Row("empty rate", Percent(report.Baseline.EmptyRate), Percent(report.Candidate.EmptyRate));
        Row("degraded rate", Percent(report.Baseline.DegradedRate), Percent(report.Candidate.DegradedRate));
        Row("feature missing rate", Percent(report.Baseline.FeatureMissingRate), Percent(report.Candidate.FeatureMissingRate));
        Row($"{report.ScoreColumn} mean / p50", Pair(report.Baseline.Scores.Mean, report.Baseline.Scores.P50), Pair(report.Candidate.Scores.Mean, report.Candidate.Scores.P50));
        if (report.Baseline.NonFiniteScores + report.Candidate.NonFiniteScores > 0)
        {
            Row($"non-finite {report.ScoreColumn}s", Number(report.Baseline.NonFiniteScores), Number(report.Candidate.NonFiniteScores));
        }

        Row("reproduced recording", Number(report.Baseline.ReproducedRecording), Number(report.Candidate.ReproducedRecording));
        output.WriteLine();
        output.WriteLine($"identical results           {report.Identical} of {report.Bundles}");
        output.WriteLine($"top-{report.TopK} overlap mean / min  {Pair(report.Overlap.Mean, report.Overlap.Min)}");
        output.WriteLine($"kendall tau mean / min      {Pair(report.KendallTau.Mean, report.KendallTau.Min)} ({report.KendallTau.Count} request(s) with 2+ shared items)");
        output.WriteLine($"{report.ScoreColumn} distribution KS      {Fixed(report.ScoreKs)}; per-item delta mean {Fixed(report.ScoreDelta.Mean)}");
        foreach (var change in report.FeatureMissing)
        {
            output.WriteLine($"feature missing             {change.Source}/{change.Field}: {Rate(change.Baseline)} -> {Rate(change.Candidate)}");
        }

        foreach (var gap in report.Candidate.Gaps)
        {
            output.WriteLine($"candidate gap               {gap.Description}");
        }

        foreach (var request in report.Requests)
        {
            if (request.Error is not null)
            {
                output.WriteLine($"not compared                {request.Error}");
            }
        }

        void Row(string label, string left, string right) => output.WriteLine($"{label,-26}{left.PadRight(width)}{right}");
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string Percent(double value) => value.ToString("0.##%", CultureInfo.InvariantCulture);
        static string Rate(double? value) => value is { } v ? Percent(v) : "not requested";
        static string Fixed(double? value) => value is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "n/a";
        static string Pair(double? a, double? b) => $"{Fixed(a)} / {Fixed(b)}";
    }
}
