using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// A result column by column, and its items as rows: the same ids, ranks and values; the rows made once, whoever reads
/// them first; a result made by hand from rows has the same columns.
/// </summary>
public sealed class ResultColumnsTests
{
    private static ResultColumns Sample() => new(
        [EntityKey.FromInt64(7), EntityKey.FromInt64(3)],
        ["score", "category"],
        [[ScalarValue.FromFloat64(0.9), ScalarValue.Missing], [ScalarValue.FromString("food"), ScalarValue.FromString("tech")]]);

    [Fact]
    public void TheRows_HoldWhatTheColumnsHold_InRankOrder()
    {
        var columns = Sample();

        var rows = columns.Rows;

        Assert.Equal(2, columns.Count);
        Assert.Equal([7L, 3L], rows.Select(r => r.Id.Int64Value));
        Assert.Equal([1, 2], rows.Select(r => r.Rank));
        Assert.Equal(["score", "category"], rows[0].Columns.Keys); // the output's order
        Assert.Equal(0.9, rows[0].Columns["score"].AsFloat64());
        Assert.True(rows[1].Columns["score"].IsMissing);
        Assert.Equal("tech", columns["category"][1].AsString());
        Assert.Same(rows, columns.Rows); // made once
    }

    [Fact]
    public void AColumnTheOutputLacks_IsNotFound()
    {
        var columns = Sample();

        Assert.False(columns.TryGetColumn("Score", out _)); // names are ordinal
        Assert.Throws<KeyNotFoundException>(() => columns["missing"].Length);
    }

    [Fact]
    public void FromItems_GivesTheColumnsOfRowsMadeByHand()
    {
        var columns = Sample();

        var copy = ResultColumns.FromItems(columns.Rows);

        Assert.Equal(columns.Names, copy.Names);
        Assert.Equal(columns.Ids.ToArray(), copy.Ids.ToArray());
        Assert.Equal(columns.Column(1).ToArray(), copy.Column(1).ToArray());
        Assert.Same(ResultColumns.Empty, ResultColumns.FromItems([]));
        var mismatched = new[]
        {
            new RankedItem(EntityKey.FromInt64(1), 1, new Dictionary<string, ScalarValue> { ["a"] = ScalarValue.FromInt64(1) }),
            new RankedItem(EntityKey.FromInt64(2), 2, new Dictionary<string, ScalarValue> { ["b"] = ScalarValue.FromInt64(1) }),
        };
        Assert.Contains("no column 'a'", Assert.Throws<ArgumentException>(() => ResultColumns.FromItems(mismatched)).Message);
    }

    [Fact]
    public void TheRows_AreMadeOnce_WhenReadFromManyThreads()
    {
        var columns = Sample();
        var seen = new IReadOnlyList<RankedItem>[16];

        Parallel.For(0, seen.Length, i => seen[i] = columns.Rows);

        Assert.All(seen, rows => Assert.Same(seen[0], rows));
    }

    [Fact]
    public void Json_WritesTheItemsFromTheColumns()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            StrataRec.Hosting.RecResultJson.WriteItems(json, Sample());
        }

        Assert.Equal("""[{"rank":1,"id":7,"columns":{"score":0.9,"category":"food"}},{"rank":2,"id":3,"columns":{"score":null,"category":"tech"}}]""",
            System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }
}
