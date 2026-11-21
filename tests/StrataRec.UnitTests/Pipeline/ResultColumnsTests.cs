using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// A result column by column, and its items as rows: the same ids, ranks and values; the rows made once, whoever reads
/// them first; each column's declared type; a result made by hand from rows has the same columns.
/// </summary>
public sealed class ResultColumnsTests
{
    private static readonly (string, ColumnType)[] SampleColumns = [("score", ColumnType.Parse("float64?")), ("category", ColumnType.String)];

    private static ResultColumns Sample() => new(
        [EntityKey.FromInt64(7), EntityKey.FromInt64(3)],
        ["score", "category"],
        [ColumnType.Parse("float64?"), ColumnType.String],
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
        Assert.Equal(-1, columns.IndexOf("Score"));
        Assert.Throws<KeyNotFoundException>(() => columns["missing"].Length);
    }

    [Fact]
    public void EachColumn_HasItsDeclaredType()
    {
        var columns = Sample();

        Assert.Equal(["float64?", "string"], columns.Types.Select(t => t.ToString()));
        Assert.Equal(ColumnType.String, columns.Types[columns.IndexOf("category")]);
        Assert.Throws<ArgumentException>(() => new ResultColumns([], ["a"], [], [[]])); // a type per name
    }

    [Fact]
    public void FromItems_GivesTheColumnsOfRowsMadeByHand()
    {
        var columns = Sample();

        var copy = ResultColumns.FromItems(SampleColumns, columns.Rows);

        Assert.Equal(columns.Names, copy.Names);
        Assert.Equal(columns.Types, copy.Types);
        Assert.Equal(columns.Ids.ToArray(), copy.Ids.ToArray());
        Assert.Equal(columns.Column(1).ToArray(), copy.Column(1).ToArray());
        Assert.Same(ResultColumns.Empty, ResultColumns.FromItems([], []));
        Assert.Equal(["score", "category"], ResultColumns.FromItems(SampleColumns, []).Names); // no items, still the output's columns
        var mismatched = new[]
        {
            new RankedItem(EntityKey.FromInt64(1), 1, new Dictionary<string, ScalarValue> { ["a"] = ScalarValue.FromInt64(1) }),
            new RankedItem(EntityKey.FromInt64(2), 2, new Dictionary<string, ScalarValue> { ["b"] = ScalarValue.FromInt64(1) }),
        };
        Assert.Contains("Item 1 has a column 'b', which the output does not", Assert.Throws<ArgumentException>(() => ResultColumns.FromItems([("a", ColumnType.Int64)], mismatched)).Message);
        var two = new RankedItem(EntityKey.FromInt64(1), 1, new Dictionary<string, ScalarValue> { ["x"] = ScalarValue.FromInt64(1), ["y"] = ScalarValue.FromInt64(7) });
        Assert.Contains("Column 'x' is defined twice", Assert.Throws<ArgumentException>(
            () => ResultColumns.FromItems([("x", ColumnType.Int64), ("x", ColumnType.Int64)], [two])).Message); // x twice, y dropped
        var ignoringCase = new RankedItem(EntityKey.FromInt64(1), 1,
            new Dictionary<string, ScalarValue>(StringComparer.OrdinalIgnoreCase) { ["x"] = ScalarValue.FromInt64(1), ["y"] = ScalarValue.FromInt64(7) });
        Assert.Contains("has a column 'y', which the output does not", Assert.Throws<ArgumentException>(
            () => ResultColumns.FromItems([("x", ColumnType.Int64), ("X", ColumnType.Int64)], [ignoringCase])).Message); // 'X' finds x
    }

    [Theory]
    [InlineData("int32", 4294967296L)] // outside the int32 range
    [InlineData("float64", 1L)] // an int64, not a float64
    [InlineData("int64", null)] // missing in a non-nullable column
    public void FromItems_RejectsAValueThatIsNotOneOfItsColumnsType(string type, long? value)
    {
        var item = new RankedItem(EntityKey.FromInt64(1), 1, new Dictionary<string, ScalarValue>
        {
            ["x"] = value is { } v ? ScalarValue.FromInt64(v) : ScalarValue.Missing,
        });

        Assert.Contains("not a value of", Assert.Throws<ArgumentException>(() => ResultColumns.FromItems([("x", ColumnType.Parse(type))], [item])).Message);
    }

    [Fact]
    public void AHostWithNarrowTypes_ReadsAColumnAtItsDeclaredWidth()
    {
        var item = new RankedItem(EntityKey.FromInt64(1), 1, new Dictionary<string, ScalarValue>
        {
            ["clicks"] = ScalarValue.FromInt64(int.MinValue),
            ["ctr"] = ScalarValue.FromFloat32(0.1f),
        });
        var columns = ResultColumns.FromItems([("clicks", ColumnType.Int32), ("ctr", ColumnType.Float32)], [item]);

        Assert.Equal(int.MinValue, columns["clicks"][0].AsInt32());
        Assert.Equal(0.1f, columns["ctr"][0].AsFloat32());
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
