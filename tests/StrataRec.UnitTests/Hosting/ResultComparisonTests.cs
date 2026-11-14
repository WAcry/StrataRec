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
    public void KolmogorovSmirnov_LeavesOutNonFiniteValues_AndEndsForAnyInput()
    {
        // NaN compares false with everything: an index walk keyed on comparisons never advanced past one and looped forever.
        var results = Bounded(() => new[]
        {
            ResultComparison.KolmogorovSmirnov(new[] { double.NaN }, new[] { 1.0 }),
            ResultComparison.KolmogorovSmirnov(new[] { 1.0, double.NaN, 2.0 }, new[] { double.PositiveInfinity, 2.0, 1.0, double.NegativeInfinity }),
            ResultComparison.KolmogorovSmirnov(new[] { double.NaN, double.PositiveInfinity }, new[] { double.NegativeInfinity }),
            ResultComparison.KolmogorovSmirnov(new[] { 1.0, 2.0, double.NaN, 3.0, 4.0 }, new[] { 3.0, double.NaN, 4.0, 5.0, 6.0 }),
        });

        Assert.Equal(1.0, results[0]); // only one side has finite values
        Assert.Equal(0.0, results[1]);
        Assert.Equal(0.0, results[2]); // neither side has any
        Assert.Equal(0.5, results[3]);
    }

    [Fact]
    public void Summarize_LeavesOutNonFiniteValues()
    {
        var summary = ResultComparison.Summarize(new[] { 2.0, double.NaN, 1.0, double.PositiveInfinity, double.NegativeInfinity });

        Assert.Equal(new Distribution(2, 1.5, 1, 1, 2, 2, 2), summary);
        Assert.Equal(new Distribution(0, null, null, null, null, null, null), ResultComparison.Summarize(new[] { double.NaN }));
    }

    [Fact]
    public void Diff_TreatsNaNAsEqualToNaN()
    {
        var recorded = Result(true, (1, double.NaN, "a"));
        var replayed = Result(true, (1, double.NaN, "a"));
        foreach (var (left, right) in recorded.Columns.Zip(replayed.Columns))
        {
            ((Dictionary<string, ScalarValue>)left)["embedding"] = ScalarValue.FromVector(new[] { float.NaN, float.PositiveInfinity });
            ((Dictionary<string, ScalarValue>)right)["embedding"] = ScalarValue.FromVector(new[] { float.NaN, float.PositiveInfinity });
        }

        Assert.True(ResultComparison.Diff(recorded, replayed).Identical);
        Assert.False(ResultComparison.Diff(recorded, Result(true, (1, 0.5, "a"))).Identical);
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
    public void Diff_ListsEveryItemThatMovedChangedOrIsInOneResultOnly()
    {
        var recorded = Result(true, (1, 0.9, "a"), (2, 0.8, "b"), (3, 0.7, "c"));
        var replayed = Result(true, (1, 0.9, "a"), (3, 0.8, "c"), (2, 0.7, "b"), (4, 0.1, "d"));

        var diff = ResultComparison.Diff(recorded, replayed);

        Assert.False(diff.Identical);
        // Per item: 2 and 3 swapped (2 with its own new score), 4 only replayed.
        Assert.Equal(new[] { EntityKey.FromInt64(2), EntityKey.FromInt64(3), EntityKey.FromInt64(4) }, diff.Changes.Select(c => c.Id));
        Assert.Equal(new int?[] { 2, 3, null }, diff.Changes.Select(c => c.RecordedRank));
        Assert.Equal(new int?[] { 3, 2, 4 }, diff.Changes.Select(c => c.ReplayedRank));
        Assert.Equal(("score", 0.8, 0.7), (diff.Changes[0].Columns[0].Column, diff.Changes[0].Columns[0].Recorded.AsFloat64(), diff.Changes[0].Columns[0].Replayed.AsFloat64()));
        Assert.Equal(3, diff.InBoth);
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

    /// <summary>
    /// Runs <paramref name="compute"/> on a dedicated background thread with a generous bound, so a regression to an
    /// endless loop fails the test instead of hanging the run (no thread-pool timing involved).
    /// </summary>
    internal static T Bounded<T>(Func<T> compute)
    {
        T result = default!;
        var thread = new Thread(() => result = compute()) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the computation did not end");
        return result;
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
