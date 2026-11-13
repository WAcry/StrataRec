using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;

namespace StrataRec.UnitTests.Hosting;

/// <summary>Side summaries of the recorded-mode comparison (design 10.8): error, degraded and score metrics.</summary>
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
        Assert.Equal(1, report.ScoreDelta.Count); // only item 2 has a finite score on both sides
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
        Assert.Equal(0.5, report.Baseline.DegradedRate); // the fallback and the skipped failure; not the failed request
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
            Succeeded = succeeded,
            Error = succeeded ? null : "node failed",
            Items = items,
            Experiments = new ExperimentRecord(Array.Empty<string>(), Array.Empty<string>(), 0),
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
