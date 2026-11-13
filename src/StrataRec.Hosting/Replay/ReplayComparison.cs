using StrataRec.Core.Types;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Hosting.Replay;

/// <summary>One bundle replayed on a baseline and a candidate configuration.</summary>
/// <param name="Name">Bundle file (or another label).</param>
/// <param name="RequestId">Recorded request id.</param>
/// <param name="Baseline">The baseline replay; null when it could not run (see <paramref name="Error"/>).</param>
/// <param name="Candidate">The candidate replay; null when it could not run.</param>
/// <param name="Error">Why the bundle could not be replayed at all (unreadable bundle, bad value).</param>
public sealed record ReplayPair(string Name, string RequestId, ReplayOutcome? Baseline, ReplayOutcome? Candidate, string? Error = null);

/// <summary>Aggregate metrics of one side of a batch comparison.</summary>
public sealed class ReplaySideSummary
{
    public required string Manifest { get; init; }

    /// <summary>Requests replayed on this side.</summary>
    public required int Requests { get; init; }

    /// <summary>Engine time per replayed request; recorded mode answers I/O instantly, so this is CPU work only.</summary>
    public required Distribution LatencyMs { get; init; }

    /// <summary>Requests that returned no result, including those whose replay stopped where the recording was aborted.</summary>
    public required double ErrorRate { get; init; }

    /// <summary>Requests whose replay stopped where the recording was aborted (counted in <see cref="ErrorRate"/>, as serving counts aborts).</summary>
    public required int Aborted { get; init; }

    /// <summary>Requests that succeeded with no items.</summary>
    public required double EmptyRate { get; init; }

    /// <summary>
    /// Requests that succeeded with degraded service: served by a fallback node or pipeline, or with a node that degraded,
    /// failed and was skipped, or was skipped for lack of time or input. A failed request is an error, not degraded service.
    /// </summary>
    public required double DegradedRate { get; init; }

    /// <summary>Missing feature values / requested feature values (absent at the source, or not in the bundle).</summary>
    public required double FeatureMissingRate { get; init; }

    /// <summary>Distribution of the score column's finite values over every returned item.</summary>
    public required Distribution Scores { get; init; }

    /// <summary>Returned items whose score is NaN or ±∞; they are left out of <see cref="Scores"/> and the KS statistic.</summary>
    public required int NonFiniteScores { get; init; }

    /// <summary>Requests whose replay reproduced the recorded result exactly.</summary>
    public required int ReproducedRecording { get; init; }

    /// <summary>Gaps summed over requests.</summary>
    public required IReadOnlyList<ReplayGap> Gaps { get; init; }
}

/// <summary>Feature-missing rate of one field on both sides; null where a side never requested the field.</summary>
public sealed record FieldMissingChange(string Source, string Field, double? Baseline, double? Candidate);

/// <summary>Per-request comparison of the baseline and candidate results.</summary>
public sealed record RequestComparison(
    string Name,
    string RequestId,
    bool BaselineSucceeded,
    bool CandidateSucceeded,
    int BaselineItems,
    int CandidateItems,
    double Overlap,
    double? KendallTau,
    bool Identical,
    string? Error);

/// <summary>The recorded-mode comparison of design 10.8 over a set of bundles.</summary>
public sealed class ComparisonReport
{
    public required int Bundles { get; init; }

    /// <summary>Bundles replayed on both sides; only these enter the metrics.</summary>
    public required int Compared { get; init; }

    /// <summary>Bundles that could not be read or replayed on one side or both (see <see cref="RequestComparison.Error"/>).</summary>
    public required int Unreadable { get; init; }

    /// <summary>
    /// <c>ok</c> (every bundle compared), <c>partial</c> (some bundles unreadable: the metrics cover the rest) or
    /// <c>failed</c> (nothing compared, including when there were no bundles).
    /// </summary>
    public string Status => Compared == 0 ? "failed" : Unreadable > 0 ? "partial" : "ok";

    public required int TopK { get; init; }

    public required string ScoreColumn { get; init; }

    public required ReplaySideSummary Baseline { get; init; }

    public required ReplaySideSummary Candidate { get; init; }

    /// <summary>Mean Top-K overlap of candidate vs baseline over requests that replayed on both sides.</summary>
    public required Distribution Overlap { get; init; }

    /// <summary>Kendall τ of candidate vs baseline over requests with at least two shared items.</summary>
    public required Distribution KendallTau { get; init; }

    /// <summary>Two-sample Kolmogorov–Smirnov statistic between the baseline and candidate distributions of finite scores.</summary>
    public required double ScoreKs { get; init; }

    /// <summary>Candidate − baseline score of items both returned for the same request, where both scores are finite.</summary>
    public required Distribution ScoreDelta { get; init; }

    /// <summary>Requests whose candidate result equals the baseline result exactly.</summary>
    public required int Identical { get; init; }

    /// <summary>Fields whose feature-missing rate differs between the sides, largest change first.</summary>
    public required IReadOnlyList<FieldMissingChange> FeatureMissing { get; init; }

    public required IReadOnlyList<RequestComparison> Requests { get; init; }
}

/// <summary>Builds the batch comparison report of design 10.8 (recorded mode).</summary>
public static class ReplayComparison
{
    /// <param name="topK">K of the Top-K overlap.</param>
    /// <param name="scoreColumn">Output column whose distribution is compared, e.g. <c>score</c>.</param>
    public static ComparisonReport Compare(IReadOnlyList<ReplayPair> pairs, int topK, string scoreColumn, string baselineManifest, string candidateManifest)
    {
        var requests = new List<RequestComparison>(pairs.Count);
        var overlaps = new List<double>();
        var taus = new List<double>();
        var deltas = new List<double>();
        var identical = 0;
        var compared = 0;
        var unreadable = 0;
        foreach (var pair in pairs)
        {
            unreadable += pair.Error is not null ? 1 : 0;
            if (pair.Baseline is not { } baseline || pair.Candidate is not { } candidate)
            {
                requests.Add(new RequestComparison(pair.Name, pair.RequestId, pair.Baseline?.Result.Succeeded ?? false, pair.Candidate?.Result.Succeeded ?? false,
                    pair.Baseline?.Result.Items.Count ?? 0, pair.Candidate?.Result.Items.Count ?? 0, 0, null, false, pair.Error));
                continue;
            }

            compared++;
            var left = ComparableResult.From(baseline.Result, baseline.Aborted);
            var right = ComparableResult.From(candidate.Result, candidate.Aborted);
            var diff = ResultComparison.Diff(left, right);
            var overlap = ResultComparison.Overlap(left.Ids, right.Ids, topK);
            overlaps.Add(overlap);
            if (diff.KendallTau is { } tau)
            {
                taus.Add(tau);
            }

            identical += diff.Identical ? 1 : 0;
            AddDeltas(baseline.Result, candidate.Result, scoreColumn, deltas);
            requests.Add(new RequestComparison(pair.Name, pair.RequestId, left.Succeeded, right.Succeeded, left.Ids.Count, right.Ids.Count, overlap, diff.KendallTau,
                diff.Identical, null));
        }

        var baselineSide = Summarize(pairs, static p => p.Baseline, scoreColumn, baselineManifest, out var baselineScores, out var baselineFields);
        var candidateSide = Summarize(pairs, static p => p.Candidate, scoreColumn, candidateManifest, out var candidateScores, out var candidateFields);
        return new ComparisonReport
        {
            Bundles = pairs.Count,
            Compared = compared,
            Unreadable = unreadable,
            TopK = topK,
            ScoreColumn = scoreColumn,
            Baseline = baselineSide,
            Candidate = candidateSide,
            Overlap = ResultComparison.Summarize(overlaps),
            KendallTau = ResultComparison.Summarize(taus),
            ScoreKs = ResultComparison.KolmogorovSmirnov(baselineScores, candidateScores),
            ScoreDelta = ResultComparison.Summarize(deltas),
            Identical = identical,
            FeatureMissing = FieldChanges(baselineFields, candidateFields),
            Requests = requests,
        };
    }

    private static ReplaySideSummary Summarize(
        IReadOnlyList<ReplayPair> pairs, Func<ReplayPair, ReplayOutcome?> side, string scoreColumn, string manifest,
        out List<double> scores, out Dictionary<(string Source, string Field), long[]> fields)
    {
        scores = new List<double>();
        fields = new Dictionary<(string Source, string Field), long[]>();
        var latencies = new List<double>();
        var gaps = new Dictionary<(string Kind, string Source, string? Field, string Reason), (int Count, List<string> Examples)>();
        int count = 0, errors = 0, aborted = 0, empty = 0, degraded = 0, reproduced = 0, nonFinite = 0;
        long requested = 0, missing = 0;
        foreach (var pair in pairs)
        {
            if (side(pair) is not { } outcome)
            {
                continue;
            }

            count++;
            var result = outcome.Result;
            latencies.Add(outcome.Report.ElapsedMs);
            errors += result.Succeeded ? 0 : 1;
            aborted += outcome.Aborted ? 1 : 0;
            empty += result.Succeeded && result.Items.Count == 0 ? 1 : 0;
            degraded += result.Succeeded && IsDegraded(outcome) ? 1 : 0;
            reproduced += outcome.Report.Diff.Identical ? 1 : 0;
            foreach (var item in result.Items)
            {
                if (Score(item, scoreColumn) is not { } score)
                {
                    continue;
                }

                if (double.IsFinite(score))
                {
                    scores.Add(score);
                }
                else
                {
                    nonFinite++;
                }
            }

            foreach (var stat in outcome.Report.FieldStats)
            {
                requested += stat.Requested;
                missing += stat.Missing;
                if (!fields.TryGetValue((stat.Source, stat.Field), out var counts))
                {
                    counts = new long[2];
                    fields[(stat.Source, stat.Field)] = counts;
                }

                counts[0] += stat.Requested;
                counts[1] += stat.Missing;
            }

            foreach (var gap in outcome.Report.Gaps)
            {
                var key = (gap.Kind, gap.Source, gap.Field, gap.Reason);
                var entry = gaps.TryGetValue(key, out var existing) ? existing : (0, new List<string>());
                foreach (var example in gap.Examples)
                {
                    if (entry.Item2.Count < 5 && !entry.Item2.Contains(example))
                    {
                        entry.Item2.Add(example);
                    }
                }

                gaps[key] = (entry.Item1 + gap.Count, entry.Item2);
            }
        }

        var gapList = new List<ReplayGap>(gaps.Count);
        foreach (var ((kind, source, field, reason), (total, examples)) in gaps)
        {
            gapList.Add(new ReplayGap(kind, source, field, reason, total, examples));
        }

        gapList.Sort(static (a, b) => b.Count.CompareTo(a.Count) is var c && c != 0 ? c : string.CompareOrdinal(a.Source + a.Field, b.Source + b.Field));
        return new ReplaySideSummary
        {
            Manifest = manifest,
            Requests = count,
            LatencyMs = ResultComparison.Summarize(latencies),
            ErrorRate = Rate(errors, count),
            Aborted = aborted,
            EmptyRate = Rate(empty, count),
            DegradedRate = Rate(degraded, count),
            FeatureMissingRate = requested == 0 ? 0.0 : (double)missing / requested,
            Scores = ResultComparison.Summarize(scores),
            NonFiniteScores = nonFinite,
            ReproducedRecording = reproduced,
            Gaps = gapList,
        };
    }

    /// <summary>
    /// The service of a successful request was degraded: it was served by a fallback pipeline, or a node degraded, fell
    /// back, failed and was skipped, or was skipped for lack of time or input. Only meaningful for a request that succeeded.
    /// </summary>
    private static bool IsDegraded(ReplayOutcome outcome)
    {
        if (outcome.Result.ServedBy != outcome.Bundle.Request.PipelineId)
        {
            return true;
        }

        foreach (var node in outcome.Report.Nodes)
        {
            if (node.Replayed is "degraded" or "fallback" or "failed" or "skipped_deadline" or "skipped_input")
            {
                return true;
            }
        }

        return false;
    }

    private static void AddDeltas(RecResult baseline, RecResult candidate, string scoreColumn, List<double> deltas)
    {
        var byId = new Dictionary<Core.Keys.EntityKey, double>();
        foreach (var item in baseline.Items)
        {
            if (Score(item, scoreColumn) is { } score && double.IsFinite(score))
            {
                byId.TryAdd(item.Id, score);
            }
        }

        foreach (var item in candidate.Items)
        {
            if (Score(item, scoreColumn) is { } score && double.IsFinite(score) && byId.TryGetValue(item.Id, out var before))
            {
                deltas.Add(score - before);
            }
        }
    }

    /// <summary>The item's numeric score; null when the column is absent, missing or not a number.</summary>
    private static double? Score(Pipeline.Execution.RankedItem item, string scoreColumn) =>
        item.Columns.TryGetValue(scoreColumn, out var score) && score.Kind is StorageKind.Float64 or StorageKind.Int64 ? score.AsFloat64() : null;

    private static List<FieldMissingChange> FieldChanges(Dictionary<(string Source, string Field), long[]> baseline, Dictionary<(string Source, string Field), long[]> candidate)
    {
        var keys = new HashSet<(string Source, string Field)>(baseline.Keys);
        keys.UnionWith(candidate.Keys);
        var changes = new List<FieldMissingChange>();
        foreach (var key in keys)
        {
            double? before = baseline.TryGetValue(key, out var b) ? Rate(b[1], b[0]) : null;
            double? after = candidate.TryGetValue(key, out var c) ? Rate(c[1], c[0]) : null;
            if (before != after)
            {
                changes.Add(new FieldMissingChange(key.Source, key.Field, before, after));
            }
        }

        changes.Sort(static (a, b) =>
        {
            var c = Math.Abs((b.Candidate ?? 0) - (b.Baseline ?? 0)).CompareTo(Math.Abs((a.Candidate ?? 0) - (a.Baseline ?? 0)));
            return c != 0 ? c : string.CompareOrdinal(a.Source + "/" + a.Field, b.Source + "/" + b.Field);
        });
        return changes;
    }

    private static double Rate(long part, long whole) => whole == 0 ? 0.0 : (double)part / whole;
}
