using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;

namespace StrataRec.UnitTests.Hosting;

public sealed class ReplayComparisonTests
{
    [Fact]
    public void NonFiniteScores_AreCountedPerSide_AndLeftOutOfTheDistributionsAndKs()
    {
        var baseline = Outcome(true, Items(double.NaN, 0.5, double.PositiveInfinity));
        var candidate = Outcome(true, Items(double.NaN, 0.25, 0.75));

        var report = ResultComparisonTests.Bounded(() =>
            ReplayComparison.Compare(new[] { new ReplayPair("a", "r", baseline, candidate) }, 10, "score", "base", "cand"));

        Assert.Equal((2, 1), (report.Baseline.NonFiniteScores, report.Candidate.NonFiniteScores));
        Assert.Equal((1, 2), (report.Baseline.Scores.Count, report.Candidate.Scores.Count));
        Assert.Equal(0.5, report.ScoreKs);
        Assert.Equal(1, report.ScoreDelta.Count);
        Assert.Equal(-0.25, report.ScoreDelta.Mean);
    }

    [Fact]
    public void AFailedRequest_IsAnError_NotDegradedService()
    {
        var failed = Outcome(false, Items(), ("ctr_ranker", "failed"));
        var servedByFallback = Outcome(true, Items(0.5), ("ctr_ranker", "fallback"));
        var skipped = Outcome(true, Items(0.5), ("diversity_cap", "failed"));
        var clean = Outcome(true, Items(0.5), ("ctr_ranker", "succeeded"));

        var report = ReplayComparison.Compare(
            new[] { Pair(failed), Pair(servedByFallback), Pair(skipped), Pair(clean) }, 10, "score", "base", "base");

        Assert.Equal(0.25, report.Baseline.ErrorRate);
        Assert.Equal(0.5, report.Baseline.DegradedRate);
    }

    [Fact]
    public void AnAbortedRecording_IsLeftOutOfBothSides_AndCountedApart()
    {
        var clean = Outcome(true, Items(0.5));
        var abortedOutcome = Outcome(false, Items());
        var aborted = new ReplayOutcome
        {
            Bundle = abortedOutcome.Bundle, Result = abortedOutcome.Result, Report = abortedOutcome.Report,
            NotReplayed = new RecordedAbort("cancelled by the caller", "ctr_ranker"),
        };

        var report = ReplayComparison.Compare(new[] { Pair(aborted), Pair(clean) }, 10, "score", "base", "base");

        Assert.Equal((2, 1, 0, 1), (report.Bundles, report.Compared, report.Unreadable, report.AbortedInRecording));
        Assert.Equal("ok", report.Status);
        Assert.Equal((1, 0.0, 1), (report.Baseline.Requests, report.Baseline.ErrorRate, report.Baseline.ReproducedRecording));
        Assert.Equal((1, 0.0, 1), (report.Candidate.Requests, report.Candidate.ErrorRate, report.Candidate.ReproducedRecording));
        Assert.Equal(1, report.Baseline.LatencyMs.Count);
        Assert.Equal((1, 1), (report.Identical, report.Overlap.Count));
        Assert.True(Assert.Single(report.Requests).BaselineSucceeded);

        var onlyAborted = ReplayComparison.Compare(new[] { Pair(aborted) }, 10, "score", "base", "base");

        Assert.Equal(("failed", 0, 1), (onlyAborted.Status, onlyAborted.Compared, onlyAborted.AbortedInRecording));
    }

    [Fact]
    public void ABundleOnlyOneSideReplayed_IsInNeitherSidesRates()
    {
        var clean = Outcome(true, Items(0.5));
        var failedOnBaselineOnly = new ReplayPair("extra", "r2", Outcome(false, Items()), null, "the candidate does not serve its pipeline");

        var report = ReplayComparison.Compare(new[] { Pair(clean), failedOnBaselineOnly }, 10, "score", "base", "cand");

        Assert.Equal((2, 1, 1), (report.Bundles, report.Compared, report.Unreadable));
        Assert.Equal((1, 0.0), (report.Baseline.Requests, report.Baseline.ErrorRate));
        Assert.Equal((1, 0.0), (report.Candidate.Requests, report.Candidate.ErrorRate));
        Assert.Equal(2, report.Requests.Count);
    }

    private static ReplayPair Pair(ReplayOutcome outcome) => new("b", "r", outcome, outcome);

    private static List<RankedItem> Items(params double[] scores)
    {
        var items = new List<RankedItem>();
        for (var i = 0; i < scores.Length; i++)
        {
            items.Add(new RankedItem(EntityKey.FromInt64(i + 1), i + 1, new Dictionary<string, ScalarValue> { ["score"] = ScalarValue.FromFloat64(scores[i]) }));
        }

        return items;
    }

    internal static ReplayOutcome Outcome(bool succeeded, List<RankedItem> items, params (string Node, string Outcome)[] nodes)
    {
        var result = new RecResult
        {
            RequestId = "r",
            Succeeded = succeeded,
            ErrorKind = succeeded ? RecErrorKind.None : RecErrorKind.Failed,
            Error = succeeded ? null : "node failed",
            Columns = ResultColumns.FromItems([("score", StrataRec.Core.Types.ColumnType.Float64)], items),
            Experiments = new ExperimentRecord(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IgnoredArm>()),
            ManifestId = "m",
            ServedBy = "p",
        };
        var comparable = ComparableResult.From(result);
        return new ReplayOutcome
        {
            Bundle = new ReplayBundle
            {
                Format = ReplayBundle.CurrentFormat,
                Request = new ReplayRequestRecord
                {
                    PipelineId = "p", RequestId = "r", Seed = 1, TimeMs = 0, DeadlineMs = 100, Assignment = new ReplayAssignmentRecord { Source = "none" },
                },
                Manifest = new ReplayManifestRecord { Id = "m" },
                Runs = new List<ReplayRunRecord>(),
                Result = new ReplayResultRecord { Succeeded = succeeded },
            },
            Result = result,
            Report = new ReplayReport
            {
                RecordedManifest = "m",
                ReplayedManifest = "m",
                Artifacts = Array.Empty<ValueChange>(),
                RecordedArms = Array.Empty<string>(),
                ReplayedArms = Array.Empty<string>(),
                Notes = Array.Empty<string>(),
                Nodes = nodes.Select(n => new NodeComparison(n.Node, n.Outcome, n.Outcome, null, null)).ToList(),
                Params = Array.Empty<ValueChange>(),
                Gaps = Array.Empty<StrataRec.Pipeline.Replay.ReplayGap>(),
                FieldStats = Array.Empty<StrataRec.Pipeline.Replay.ReplayFieldStats>(),
                Diff = ResultComparison.Diff(comparable, comparable),
            },
        };
    }
}
