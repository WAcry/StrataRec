using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.UnitTests.Core;

/// <summary><c>list&lt;int32&gt;</c> storage (design 4.3): the type, the column, and the int32 range rule.</summary>
public class ListInt32Tests
{
    [Theory]
    [InlineData("list<int32>", StorageKind.ListInt32, false)]
    [InlineData("list<int32>?", StorageKind.ListInt32, true)]
    [InlineData(" list<int32> ? ", StorageKind.ListInt32, true)]
    [InlineData("list<int64>", StorageKind.ListInt64, false)]
    public void Type_ParsesAndPrints(string text, StorageKind kind, bool nullable)
    {
        var type = ColumnType.Parse(text);

        Assert.Equal(kind, type.Kind);
        Assert.Equal(nullable, type.Nullable);
        Assert.Equal(text.Replace(" ", string.Empty, StringComparison.Ordinal), type.ToString());
        Assert.Equal(type, ColumnType.Parse(type.ToString()));
    }

    [Fact]
    public void Type_IsAStorageType_PromotedToListInt64_LikeInt32()
    {
        Assert.Equal(ColumnType.ListInt64, ColumnType.ListInt32.Promoted());
        Assert.Equal(ColumnType.ListInt64.AsNullable(), ColumnType.ListInt32.AsNullable().Promoted());
        Assert.Equal(ColumnType.Int64, ColumnType.Int32.Promoted());
        Assert.True(ColumnType.ListInt32.IsInt32Storage);
        Assert.True(ColumnType.Int32.IsInt32Storage);
        Assert.False(ColumnType.ListInt64.IsInt32Storage);
        Assert.False(ColumnType.ListInt32.IsNumeric); // a list, not a scalar: no NaN rule, no mandatory default

        Assert.False(ColumnType.TryParse("list<int16>", out _, out var error));
        Assert.Contains("list<int32>", error);
    }

    [Fact]
    public void Column_RoundTripsThroughScalarValues_AsWidenedInt64Lists()
    {
        var memory = new RequestMemory();
        var column = ColumnBuffer.Create(ColumnType.ListInt32.AsNullable(), 8, memory);
        var lists = Assert.IsType<ListColumn<int>>(column);

        ColumnValues.Set(column, 0, ScalarValue.FromInt64List(new long[] { 1, -2, int.MaxValue, int.MinValue }));
        ColumnValues.Set(column, 1, ScalarValue.FromInt64List(Array.Empty<long>()));
        ColumnValues.Set(column, 2, ScalarValue.Missing);
        lists.Set(3, new[] { 7, 7 });

        Assert.Equal(new[] { 1, -2, int.MaxValue, int.MinValue }, lists.Get(0).ToArray());
        var first = ColumnValues.Get(column, 0);
        Assert.Equal(StorageKind.ListInt64, first.Kind); // ScalarValue holds int lists promoted, like int32 values
        Assert.Equal(new long[] { 1, -2, int.MaxValue, int.MinValue }, first.AsInt64List());
        Assert.Empty(ColumnValues.Get(column, 1).AsInt64List());
        Assert.True(ColumnValues.Get(column, 2).IsMissing);
        Assert.Equal(new long[] { 7, 7 }, ColumnValues.Get(column, 3).AsInt64List());
        Assert.Equal(2, lists.LengthOf(3));

        var copy = (ListColumn<int>)ColumnBuffer.Create(ColumnType.ListInt32.AsNullable(), 8, memory);
        copy.CopyFrom(column, 0, 5);
        copy.CopyFrom(column, 2, 6);
        Assert.Equal(new[] { 1, -2, int.MaxValue, int.MinValue }, copy.Get(5).ToArray());
        Assert.False(copy.IsValid(6));

        column.Gather(new[] { 3, 0, 2 }, 4); // Compact
        Assert.Equal(new long[] { 7, 7 }, ColumnValues.Get(column, 0).AsInt64List());
        Assert.Equal(new long[] { 1, -2, int.MaxValue, int.MinValue }, ColumnValues.Get(column, 1).AsInt64List());
        Assert.True(ColumnValues.Get(column, 2).IsMissing);
    }

    [Fact]
    public void Column_LivesInACandidateTableSlot()
    {
        var memory = new RequestMemory();
        var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Parse("list<int32>?") }), 4, memory);
        table.AppendRows(new long[] { 10, 20 });

        var slot = Assert.IsType<ListColumn<int>>(table.GetOrCreateSlot(1));
        slot.Set(1, new[] { 4, 5 });

        Assert.Equal(new long[] { 4, 5 }, ColumnValues.Get(table.TryGetSlot(1)!, 1).AsInt64List());
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(int.MinValue, true)]
    [InlineData(2147483648L, false)]
    [InlineData(-2147483649L, false)]
    [InlineData(4294967299L, false)] // 2^32 + 3: wraps to 3
    [InlineData(long.MaxValue, false)]
    [InlineData(long.MinValue, false)]
    public void Int32Range_Fits(long value, bool fits)
    {
        Assert.Equal(fits, Int32Range.Fits(value));
        Assert.Equal(fits, Int32Range.AllFit(new[] { 1L, value, -1L }));
    }

    [Fact]
    public void TrySet_RefusesOutOfRangeValues_InsteadOfWrapping_AndLeavesTheRowUntouched()
    {
        var memory = new RequestMemory();
        var scalars = (ScalarColumn<int>)ColumnBuffer.Create(ColumnType.Int32, 4, memory);
        var lists = (ListColumn<int>)ColumnBuffer.Create(ColumnType.ListInt32, 4, memory);
        scalars.Set(0, 5);
        lists.Set(0, new[] { 5 });

        Assert.False(ColumnValues.TrySet(scalars, 0, ScalarValue.FromInt64(4294967299)));
        Assert.False(ColumnValues.TrySet(lists, 0, ScalarValue.FromInt64List(new long[] { 1, 4294967299 })));
        Assert.Equal(5, scalars[0]);
        Assert.Equal(new[] { 5 }, lists.Get(0).ToArray());

        Assert.Throws<OverflowException>(() => ColumnValues.Set(scalars, 1, ScalarValue.FromInt64(2147483648)));
        var error = Assert.Throws<OverflowException>(() => ColumnValues.Set(lists, 1, ScalarValue.FromInt64List(new long[] { -2147483649 })));
        Assert.Contains("list<int32>", error.Message);

        Assert.True(ColumnValues.TrySet(scalars, 1, ScalarValue.FromInt64(int.MinValue)));
        Assert.Equal(int.MinValue, scalars[1]);
    }

    [Fact]
    public void FitsStorage_And_TryConvert_RespectTheInt32Range()
    {
        var wide = ScalarValue.FromInt64List(new long[] { 1, 2147483648 });
        var narrow = ScalarValue.FromInt64List(new long[] { 1, 2147483647 });

        Assert.False(ColumnValues.FitsStorage(wide, ColumnType.ListInt32));
        Assert.True(ColumnValues.FitsStorage(wide, ColumnType.ListInt64));
        Assert.True(ColumnValues.FitsStorage(narrow, ColumnType.ListInt32));
        Assert.False(ColumnValues.FitsStorage(ScalarValue.FromInt64(-2147483649), ColumnType.Int32));
        Assert.True(ColumnValues.FitsStorage(ScalarValue.Missing, ColumnType.Int32));

        // Request inputs and parameters are validated with TryConvert: a list<int32> input must fit.
        Assert.False(wide.TryConvert(ColumnType.ListInt32, out _));
        Assert.True(narrow.TryConvert(ColumnType.ListInt32, out var converted));
        Assert.Equal(StorageKind.ListInt64, converted.Kind);
        Assert.False(ScalarValue.FromInt64(3_000_000_000).TryConvert(ColumnType.Int32, out _));
        Assert.False(ScalarValue.FromFloat64(3e9).TryConvert(ColumnType.Int32, out _));
        Assert.True(ScalarValue.FromFloat64(-7).TryConvert(ColumnType.Int32, out var seven));
        Assert.Equal(-7, seven.AsInt64());
        Assert.True(ScalarValue.FromInt64(3_000_000_000).TryConvert(ColumnType.Int64, out _));
    }
}
