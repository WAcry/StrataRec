using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using StrataRec.Hosting.Replay;

namespace StrataRec.Cli.Commands;

/// <summary>
/// <c>stratarec compare</c>: the recorded-mode comparison of design 10.8. Replays every bundle on a baseline and a
/// candidate workspace and reports latency, error and empty rates, Top-K overlap, Kendall τ, the score distribution
/// change and the feature-missing rate, plus every gap the candidate hit.
/// </summary>
internal static class CompareCommand
{
    public static Command Create()
    {
        var candidate = CommonOptions.Config();
        candidate.Description = "Candidate configuration directory.";
        var environment = CommonOptions.Environment();
        var format = CommonOptions.Format();
        var baseline = new Option<string>("--baseline") { Description = "Baseline configuration directory (e.g. the main branch).", Required = true };
        var bundles = new Option<string>("--bundle", "-b") { Description = "A directory of replay bundles (or one bundle).", Required = true };
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Replay both sides with these arms (experiment/arm) instead of each bundle's recorded assignment.",
            AllowMultipleArgumentsPerToken = true,
        };
        var topK = new Option<int>("--top-k", "-k") { Description = "K of the Top-K overlap.", DefaultValueFactory = _ => 10 };
        var score = new Option<string>("--score") { Description = "Output column whose distribution is compared.", DefaultValueFactory = _ => "score" };
        var command = new Command("compare", "Replay recorded requests on a baseline and a candidate workspace and compare the results (design 10.8).")
        {
            candidate, environment, format, baseline, bundles, arms, topK, score,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(baseline)!, parse.GetValue(candidate)!, parse.GetValue(environment)!,
            parse.GetValue(bundles)!, parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(topK), parse.GetValue(score)!, parse.GetValue(format)!,
            Console.Out, cancellationToken));
        return command;
    }

    internal static async Task<int> RunAsync(
        string baselineConfig, string candidateConfig, string environment, string bundlePath, string[] armArgs, int topK, string scoreColumn, string format,
        TextWriter output, CancellationToken cancellationToken)
    {
        if (topK < 1)
        {
            Console.Error.WriteLine("--top-k must be at least 1.");
            return 2;
        }

        var baseline = ReplayCommand.LoadEngine(baselineConfig, environment, format);
        var candidate = ReplayCommand.LoadEngine(candidateConfig, environment, format);
        if (baseline is null || candidate is null)
        {
            return 1;
        }

        var arms = ExplainPlanCommand.SplitArms(armArgs);
        var options = new ReplayOptions { Arms = arms.Count > 0 ? arms : null };
        var pairs = new List<ReplayPair>();
        foreach (var file in ReplayBundleFile.Find(bundlePath))
        {
            var (before, error) = await ReplayCommand.ReplayFileAsync(baseline, file, options, cancellationToken).ConfigureAwait(false);
            if (before is null)
            {
                pairs.Add(new ReplayPair(file, string.Empty, null, null, error));
                continue;
            }

            var (after, candidateError) = await ReplayCommand.ReplayFileAsync(candidate, file, options, cancellationToken).ConfigureAwait(false);
            pairs.Add(new ReplayPair(file, before.Bundle.Request.RequestId, before, after, candidateError));
        }

        if (pairs.Count == 0)
        {
            Console.Error.WriteLine($"{bundlePath}: no replay bundles found.");
            return 2;
        }

        var report = ReplayComparison.Compare(pairs, topK, scoreColumn, Manifest(pairs, static p => p.Baseline), Manifest(pairs, static p => p.Candidate));
        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json => WriteJson(json, report, baselineConfig, candidateConfig)));
        }
        else
        {
            WriteText(output, report, baselineConfig, candidateConfig);
        }

        return 0;
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
        json.WriteNumber("bundles", report.Bundles);
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

    private static void WriteSide(Utf8JsonWriter json, string name, string workspace, ReplaySideSummary side)
    {
        json.WriteStartObject(name);
        json.WriteString("workspace", workspace);
        json.WriteString("manifest", side.Manifest);
        json.WriteNumber("requests", side.Requests);
        WriteDistribution(json, "latency_ms", side.LatencyMs);
        json.WriteNumber("error_rate", Math.Round(side.ErrorRate, 6));
        json.WriteNumber("empty_rate", Math.Round(side.EmptyRate, 6));
        json.WriteNumber("degraded_rate", Math.Round(side.DegradedRate, 6));
        json.WriteNumber("feature_missing_rate", Math.Round(side.FeatureMissingRate, 6));
        WriteDistribution(json, "score", side.Scores);
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
        output.WriteLine();
        output.WriteLine($"{"",-26}{"baseline",-22}{"candidate",-22}");
        Row("workspace", baseline, candidate);
        Row("manifest", report.Baseline.Manifest, report.Candidate.Manifest);
        Row("requests", Number(report.Baseline.Requests), Number(report.Candidate.Requests));
        Row("latency p50 / p95 (ms)", Pair(report.Baseline.LatencyMs.P50, report.Baseline.LatencyMs.P95), Pair(report.Candidate.LatencyMs.P50, report.Candidate.LatencyMs.P95));
        Row("error rate", Percent(report.Baseline.ErrorRate), Percent(report.Candidate.ErrorRate));
        Row("empty rate", Percent(report.Baseline.EmptyRate), Percent(report.Candidate.EmptyRate));
        Row("degraded rate", Percent(report.Baseline.DegradedRate), Percent(report.Candidate.DegradedRate));
        Row("feature missing rate", Percent(report.Baseline.FeatureMissingRate), Percent(report.Candidate.FeatureMissingRate));
        Row($"{report.ScoreColumn} mean / p50", Pair(report.Baseline.Scores.Mean, report.Baseline.Scores.P50), Pair(report.Candidate.Scores.Mean, report.Candidate.Scores.P50));
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

        void Row(string label, string left, string right) => output.WriteLine($"{label,-26}{left,-22}{right,-22}");
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string Percent(double value) => value.ToString("0.##%", CultureInfo.InvariantCulture);
        static string Rate(double? value) => value is { } v ? Percent(v) : "not requested";
        static string Fixed(double? value) => value is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "n/a";
        static string Pair(double? a, double? b) => $"{Fixed(a)} / {Fixed(b)}";
    }
}
