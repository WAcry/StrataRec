using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Pipeline.Operators.Standard;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// Ranking by a score column (design 5.7) compares the scores exactly: int64 scores beyond 2^53, where a double cannot
/// tell neighbours apart, keep their numeric order; missing and NaN go last; ties go to the lower entity id.
/// </summary>
public sealed class RowRankingTests
{
    private const long TwoTo53 = 1L << 53;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntityIdsJustAbove2To53_RankByTheirExactValue(bool ascending)
    {
        // The reported case: {"score_col": "entity_id", "k": 2}. As doubles both ids are 2^53 and tie, so the tie-break
        // put the lower id first whatever the direction.
        var ids = new[] { TwoTo53, TwoTo53 + 1 };
        var (table, memory) = Table(ids);

        var ranked = Rank(table, table.Int64Ids, k: 2, ascending, memory);

        Assert.Equal(ascending ? new[] { TwoTo53, TwoTo53 + 1 } : new[] { TwoTo53 + 1, TwoTo53 }, ranked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Int64ScoresAround2To53_AndAtTheExtremes_KeepTheirNumericOrder(bool ascending)
    {
        long[] scores =
        {
            TwoTo53 + 1, long.MinValue, TwoTo53, -TwoTo53 - 1, long.MaxValue, 0, TwoTo53 - 1, -1, long.MinValue + 1, TwoTo53 + 2,
            long.MaxValue - 1, 1, -TwoTo53,
        };
        var ids = Ids(scores.Length);
        var (table, memory) = Table(ids);
        var column = Int64Scores(table, scores);

        var ranked = Rank(table, column, scores.Length, ascending, memory);

        var expected = ids.OrderBy(id => scores[id - 1]).ToArray();
        Assert.Equal(ascending ? expected : expected.Reverse().ToArray(), ranked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedTopK_IsTheBestKInOrder_WithMissingLastAndTiesByEntityId(bool ascending)
    {
        // Ids 1..12; two ties (ids 3 and 9, ids 4 and 11), three missing values, values that differ only past 2^53.
        long?[] scores =
        {
            TwoTo53 + 5, null, TwoTo53 + 2, TwoTo53 + 7, long.MinValue, TwoTo53 + 3, null, TwoTo53 + 6, TwoTo53 + 2, long.MaxValue, TwoTo53 + 7,
            null,
        };
        var ids = Ids(scores.Length);
        var (table, memory) = Table(ids);
        var column = Int64Scores(table, scores);
        var full = Expected(ids, scores, ascending);

        for (var k = 0; k <= ids.Length; k++)
        {
            Assert.Equal(full.Take(k), Rank(table, column, k, ascending, memory));
        }

        // Missing values are last in both directions, and among themselves by entity id.
        Assert.Equal(new long[] { 2, 7, 12 }, full.TakeLast(3));
        Assert.Equal(ascending ? new long[] { 5, 3, 9 } : new long[] { 10, 4, 11 }, full.Take(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FloatScores_TieTheTwoZeros_AndPutNaNWithTheMissing(bool ascending)
    {
        double?[] scores = { 0.0, double.NaN, -0.0, double.NegativeInfinity, 1.5, null, double.PositiveInfinity, -2.5, double.Epsilon, -double.Epsilon };
        var ids = Ids(scores.Length);
        var (table, memory) = Table(ids);
        var column = (ScalarColumn<double>)table.GetOrCreateSlot(2);
        for (var row = 0; row < scores.Length; row++)
        {
            if (scores[row] is { } value)
            {
                column.Set(row, value);
            }
            else
            {
                column.SetMissing(row);
            }
        }

        var ranked = Rank(table, column, scores.Length, ascending, memory);

        // -0.0 (id 3) ties with 0.0 (id 1) and so follows it; NaN (id 2) and missing (id 6) are last, by id.
        long[] numeric = { 4, 8, 10, 1, 3, 9, 5, 7 };
        Assert.Equal((ascending ? numeric : DescendingWithTies(numeric)).Concat(new long[] { 2, 6 }), ranked);

        static long[] DescendingWithTies(long[] ascendingOrder)
        {
            var reversed = ascendingOrder.Reverse().ToList();
            var zero = reversed.IndexOf(3); // 3 then 1 reversed: ties keep the lower id first
            (reversed[zero], reversed[zero + 1]) = (reversed[zero + 1], reversed[zero]);
            return reversed.ToArray();
        }
    }

    [Theory]
    [InlineData(new[] { 0.0, -0.0 })]
    [InlineData(new[] { double.NegativeInfinity, -1e308, -1.0, -double.Epsilon, 0.0, double.Epsilon, 1.0, 1e308, double.PositiveInfinity })]
    public void Float64SortKeys_OrderLikeTheNumbers(double[] ascendingValues)
    {
        for (var i = 1; i < ascendingValues.Length; i++)
        {
            Assert.True(RowRanking.TryFloat64Key(ascendingValues[i - 1], out var lower));
            Assert.True(RowRanking.TryFloat64Key(ascendingValues[i], out var higher));
            Assert.Equal(ascendingValues[i - 1].CompareTo(ascendingValues[i]), lower.CompareTo(higher));
        }

        Assert.False(RowRanking.TryFloat64Key(double.NaN, out _));
    }

    [Fact]
    public void ScoresAnOperatorComputed_RankHigherFirst_NaNLast_TiesByEntityId_NotByTheIncomingOrder()
    {
        var memory = new RequestMemory();
        var table = new CandidateTable(new TableSchema(EntityKeyType.String, new[] { ColumnType.String }), 8, memory);
        table.AppendRows(new[] { "e", "d", "c", "b", "a", "f", "g", "h" });
        var rows = table.Rows.Order.ToArray();
        double[] scores = { 1, double.NaN, 2, -0.0, 0.0, double.PositiveInfinity, double.NaN, 1 };

        RowRanking.Sort(rows, scores, table, memory);

        // "c" (2) after "f" (∞); the zeros tie, so "a" before "b"; "e" and "h" tie at 1; the NaNs last, by id too.
        Assert.Equal(new[] { "f", "c", "e", "h", "a", "b", "d", "g" }, rows.Select(row => table.StringIds.Get(row)));
    }

    private static long[] Ids(int count) => Enumerable.Range(1, count).Select(i => (long)i).ToArray();

    /// <summary>The expected rank order: present scores by value (best first), ties and missing values by id.</summary>
    private static long[] Expected(long[] ids, long?[] scores, bool ascending)
    {
        var present = ids.Where(id => scores[id - 1] is not null);
        var ordered = ascending
            ? present.OrderBy(id => scores[id - 1]!.Value).ThenBy(id => id)
            : present.OrderByDescending(id => scores[id - 1]!.Value).ThenBy(id => id);
        return ordered.Concat(ids.Where(id => scores[id - 1] is null)).ToArray();
    }

    private static (CandidateTable Table, RequestMemory Memory) Table(long[] ids)
    {
        var memory = new RequestMemory();
        var schema = new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Parse("int64?"), ColumnType.Parse("float64?") });
        var table = new CandidateTable(schema, ids.Length, memory);
        table.AppendRows(ids);
        return (table, memory);
    }

    private static ScalarColumn<long> Int64Scores(CandidateTable table, long?[] scores)
    {
        var column = (ScalarColumn<long>)table.GetOrCreateSlot(1);
        for (var row = 0; row < scores.Length; row++)
        {
            if (scores[row] is { } value)
            {
                column.Set(row, value);
            }
            else
            {
                column.SetMissing(row);
            }
        }

        return column;
    }

    private static ScalarColumn<long> Int64Scores(CandidateTable table, long[] scores) =>
        Int64Scores(table, scores.Select(s => (long?)s).ToArray());

    /// <summary>The entity ids of the best <paramref name="k"/> rows, in rank order.</summary>
    private static long[] Rank(CandidateTable table, ColumnBuffer score, int k, bool ascending, RequestMemory memory)
    {
        var rows = table.Rows.Order.ToArray();
        RowRanking.SelectTop(rows, score, table, k, ascending, memory);
        return rows.Take(k).Select(row => table.Int64Ids[row]).ToArray();
    }
}
