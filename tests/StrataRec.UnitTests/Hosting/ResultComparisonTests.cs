using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Hosting.Replay;

namespace StrataRec.UnitTests.Hosting;

/// <summary>Ranking metrics of the recorded-mode comparison (design 10.8) and the result diff of a replay.</summary>
public sealed class ResultComparisonTests
{
    [Fact]
    public void Overlap_CountsSharedItemsOfTheTopK()
    {
        Assert.Equal(1.0, ResultComparison.Overlap(Ids(1, 2, 3), Ids(3, 2, 1), 3));
        Assert.Equal(0.5, ResultComparison.Overlap(Ids(1, 2, 3, 4), Ids(1, 9, 2, 3), 2));
        Assert.Equal(0.5, ResultComparison.Overlap(Ids(1, 2), Ids(1), 10)); // a shorter list does not inflate the overlap
        Assert.Equal(0.0, ResultComparison.Overlap(Ids(1), Ids(), 10));
        Assert.Equal(1.0, ResultComparison.Overlap(Ids(), Ids(), 10));
    }

    [Fact]
    public void KendallTau_RangesFromReversedToIdentical_OverSharedItems()
    {
        Assert.Equal(1.0, ResultComparison.KendallTau(Ids(1, 2, 3, 4), Ids(1, 2, 3, 4)));
        Assert.Equal(-1.0, ResultComparison.KendallTau(Ids(1, 2, 3, 4), Ids(4, 3, 2, 1)));
        Assert.Equal(4.0 / 6.0, ResultComparison.KendallTau(Ids(1, 2, 3, 4), Ids(2, 1, 3, 4))!.Value, 12); // 5 concordant, 1 discordant
        Assert.Equal(1.0, ResultComparison.KendallTau(Ids(1, 7, 2, 3), Ids(1, 2, 8, 3))); // unshared items are ignored
        Assert.Null(ResultComparison.KendallTau(Ids(1, 2), Ids(2, 9)));
    }

    [Fact]
    public void KolmogorovSmirnov_IsTheLargestGapBetweenTheEmpiricalDistributions()
    {
        Assert.Equal(0.0, ResultComparison.KolmogorovSmirnov(new[] { 1.0, 2.0, 3.0 }, new[] { 3.0, 1.0, 2.0 }));
        Assert.Equal(1.0, ResultComparison.KolmogorovSmirnov(new[] { 1.0, 2.0 }, new[] { 5.0, 6.0 }));
        Assert.Equal(0.5, ResultComparison.KolmogorovSmirnov(new[] { 1.0, 2.0, 3.0, 4.0 }, new[] { 3.0, 4.0, 5.0, 6.0 }));
        Assert.Equal(1.0, ResultComparison.KolmogorovSmirnov(new[] { 1.0 }, Array.Empty<double>()));
        Assert.Equal(0.0, ResultComparison.KolmogorovSmirnov(Array.Empty<double>(), Array.Empty<double>()));
    }

    [Fact]
    public void Summarize_UsesNearestRankPercentiles()
    {
        var values = Enumerable.Range(1, 20).Select(i => (double)i).Reverse().ToArray();

        var summary = ResultComparison.Summarize(values);

        Assert.Equal(new Distribution(20, 10.5, 1, 10, 18, 19, 20), summary);
        Assert.Equal(new Distribution(0, null, null, null, null, null, null), ResultComparison.Summarize(Array.Empty<double>()));
    }

    [Fact]
    public void Diff_ListsEveryRankWhoseItemOrValuesDiffer()
    {
        var recorded = Result(true, (1, 0.9, "a"), (2, 0.8, "b"), (3, 0.7, "c"));
        var replayed = Result(true, (1, 0.9, "a"), (3, 0.8, "c"), (2, 0.7, "b"), (4, 0.1, "d"));

        var diff = ResultComparison.Diff(recorded, replayed);

        Assert.False(diff.Identical);
        Assert.Equal(new[] { 2, 3, 4 }, diff.Changes.Select(c => c.Rank));
        Assert.Equal((EntityKey.FromInt64(2), EntityKey.FromInt64(3)), (diff.Changes[0].RecordedId, diff.Changes[0].ReplayedId));
        Assert.Null(diff.Changes[2].RecordedId);
        Assert.Equal(0.75, diff.Overlap);
        Assert.Equal(1.0 / 3.0, diff.KendallTau!.Value, 12);
        Assert.True(ResultComparison.Diff(recorded, recorded).Identical);
    }

    [Fact]
    public void Diff_SameItems_WithAChangedValueOrSuccess_IsNotIdentical()
    {
        var recorded = Result(true, (1, 0.9, "a"));

        var changedScore = ResultComparison.Diff(recorded, Result(true, (1, 0.91, "a")));
        var failed = ResultComparison.Diff(recorded, Result(false, (1, 0.9, "a")));

        var change = Assert.Single(Assert.Single(changedScore.Changes).Columns);
        Assert.Equal(("score", 0.9, 0.91), (change.Column, change.Recorded.AsFloat64(), change.Replayed.AsFloat64()));
        Assert.False(failed.Identical);
        Assert.Empty(failed.Changes);
    }

    [Fact]
    public void Diff_AColumnOnlyOneSideHas_IsReportedOnce_EvenWhenItsValuesAreMissing()
    {
        var recorded = Result(true, (1, 0.9, "a"), (2, 0.8, "b"));
        var replayed = Result(true, (1, 0.9, "a"), (2, 0.8, "b"));
        foreach (var columns in replayed.Columns)
        {
            ((Dictionary<string, ScalarValue>)columns)["age"] = ScalarValue.Missing;
        }

        var diff = ResultComparison.Diff(recorded, replayed);
        var reverse = ResultComparison.Diff(replayed, recorded);

        Assert.False(diff.Identical);
        Assert.Equal(new[] { "age" }, diff.AddedColumns);
        Assert.Empty(diff.Changes);
        Assert.Equal(new[] { "age" }, reverse.RemovedColumns);
    }

    private static EntityKey[] Ids(params long[] ids) => ids.Select(EntityKey.FromInt64).ToArray();

    private static ComparableResult Result(bool succeeded, params (long Id, double Score, string Category)[] items) => new()
    {
        Succeeded = succeeded,
        Ids = items.Select(i => EntityKey.FromInt64(i.Id)).ToList(),
        Columns = items.Select(i => (IReadOnlyDictionary<string, ScalarValue>)new Dictionary<string, ScalarValue>
        {
            ["score"] = ScalarValue.FromFloat64(i.Score),
            ["category"] = ScalarValue.FromString(i.Category),
        }).ToList(),
    };
}
