using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;

namespace StrataRec.UnitTests.Core;

public class CandidateTableTests
{
    private static CandidateTable NewTable(int capacity, out RequestMemory memory)
    {
        memory = new RequestMemory();
        var schema = new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Float64, ColumnType.Parse("float32?") });
        return new CandidateTable(schema, capacity, memory);
    }

    [Fact]
    public void Append_FilterReorder_Compact_PreservesValuesAndFillState()
    {
        var table = NewTable(16, out var memory);
        table.AppendRows(new long[] { 10, 20, 30, 40, 50 });
        var score = (ScalarColumn<double>)table.GetOrCreateSlot(1);
        var feature = (ScalarColumn<float>)table.GetOrCreateSlot(2);
        var filled = table.GetFilled(2);
        for (var r = 0; r < 5; r++)
        {
            score.Set(r, r * 1.5);
            feature.Set(r, r);
            Bits.Set(filled, r);
        }

        feature.SetMissing(3);

        // Drop rows 1 and 2, reverse the rest.
        table.Rows.Replace(new[] { 4, 3, 0 }, allowDrop: true);
        Assert.Equal(3, table.Rows.Count);
        Assert.False(table.Rows.IsAlive(1));
        Assert.Equal(3.0 / 5.0, table.LiveRatio, 6);

        table.Compact();

        Assert.Equal(3, table.PhysicalRowCount);
        Assert.Equal(new[] { 0, 1, 2 }, table.Rows.Order.ToArray());
        Assert.Equal(new long[] { 50, 40, 10 }, new[] { table.Int64Ids[0], table.Int64Ids[1], table.Int64Ids[2] });
        score = (ScalarColumn<double>)table.TryGetSlot(1)!;
        feature = (ScalarColumn<float>)table.TryGetSlot(2)!;
        Assert.Equal(6.0, score[0]);
        Assert.Equal(4.5, score[1]);
        Assert.False(feature.IsValid(1));
        Assert.True(feature.IsValid(0));
        Assert.True(table.IsFilledForLiveRows(2));
        Assert.Equal(1, table.FindRow(EntityKey.FromInt64(40)));

        memory.Release();
        Assert.True(memory.IsReturned);
    }

    [Fact]
    public void Replace_RejectsDeadOrDuplicateRows()
    {
        var table = NewTable(8, out _);
        table.AppendRows(new long[] { 1, 2, 3 });
        table.Rows.Truncate(2);
        Assert.Throws<InvalidOperationException>(() => table.Rows.Replace(new[] { 2 }, allowDrop: true));
        Assert.Throws<InvalidOperationException>(() => table.Rows.Replace(new[] { 0, 0 }, allowDrop: true));
        Assert.Throws<InvalidOperationException>(() => table.Rows.Replace(new[] { 1 }, allowDrop: false));
    }

    [Fact]
    public void Append_BeyondCapacity_Throws()
    {
        var table = NewTable(2, out _);
        Assert.Throws<InvalidOperationException>(() => table.AppendRows(new long[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("float32?", StorageKind.Float32, true, 0)]
    [InlineData("vec<float32,64>", StorageKind.Vector, false, 64)]
    [InlineData("list<int64>", StorageKind.ListInt64, false, 0)]
    [InlineData("string", StorageKind.String, false, 0)]
    public void ColumnType_ParseRoundTrips(string text, StorageKind kind, bool nullable, int dim)
    {
        var t = ColumnType.Parse(text);
        Assert.Equal(kind, t.Kind);
        Assert.Equal(nullable, t.Nullable);
        Assert.Equal(dim, t.Dimension);
        Assert.Equal(text, t.ToString());
    }

    [Fact]
    public void ColumnType_RejectsUnknown() => Assert.False(ColumnType.TryParse("float16", out _, out _));
}
