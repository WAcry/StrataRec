using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.UnitTests.Core;

public class ListTakenTests
{
    [Fact]
    public void ListsThatTakeTheSamePositions_StayAligned_WhateverTheirElements()
    {
        var memory = new RequestMemory();
        var ids = new ListColumn<long>(ColumnType.Parse("list<int64>"), 4, memory);
        var labels = new ListColumn<string>(ColumnType.Parse("list<string>"), 4, memory);
        ids.Set(1, [10, 20, 30, 40]);
        labels.Set(1, ["a", "b", "c", "d"]);
        ListColumn[] from = [ids, labels];
        ListColumn[] into = [new ListColumn<long>(ids.Type, 4, memory), new ListColumn<string>(labels.Type, 4, memory)];

        for (var c = 0; c < from.Length; c++)
        {
            into[c].SetTaken(2, from[c], 1, [3, 0, 2]);
        }

        Assert.Equal(new long[] { 40, 10, 30 }, ((ListColumn<long>)into[0]).Get(2).ToArray());
        Assert.Equal(new[] { "d", "a", "c" }, ((ListColumn<string>)into[1]).Get(2).ToArray());
        Assert.True(into[0].IsValid(2) && into[1].IsValid(2));
    }

    [Fact]
    public void ARowTakenFromItself_ReadsWhatItHeldBefore_AlsoWhenTheArenaGrows()
    {
        var memory = new RequestMemory();
        var column = new ListColumn<long>(ColumnType.Parse("list<int64>"), 1, memory);
        var values = new long[5000];
        var positions = new int[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i;
            positions[2 * i] = positions[(2 * i) + 1] = values.Length - 1 - i;
        }

        column.Set(0, values);

        column.SetTaken(0, column, 0, positions);

        var taken = column.Get(0);
        Assert.Equal(positions.Length, taken.Length);
        for (var i = 0; i < positions.Length; i++)
        {
            Assert.Equal(positions[i], taken[i]);
        }
    }

    [Fact]
    public void AColumnOfOtherElements_IsRefused_AndAPositionOutsideTheList()
    {
        var memory = new RequestMemory();
        var ids = new ListColumn<long>(ColumnType.Parse("list<int64>"), 2, memory);
        var floats = new ListColumn<double>(ColumnType.Parse("list<float64>"), 2, memory);
        ids.Set(0, [1, 2]);

        Assert.Throws<ArgumentException>(() => floats.SetTaken(0, ids, 0, [0]));
        Assert.Throws<IndexOutOfRangeException>(() => ids.SetTaken(1, ids, 0, [2]));
    }

    [Fact]
    public void APositionOutsideTheList_LeavesTheRowAsItWas()
    {
        var memory = new RequestMemory();
        var ids = new ListColumn<long>(ColumnType.Parse("list<int64>"), 2, memory);
        ids.Set(0, [10, 20]);
        ids.Set(1, [42]);

        Assert.Throws<IndexOutOfRangeException>(() => ids.SetTaken(1, ids, 0, [1, 8]));
        Assert.Throws<IndexOutOfRangeException>(() => ids.SetTaken(1, ids, 0, [-1]));

        Assert.True(ids.IsValid(1));
        Assert.Equal(new long[] { 42 }, ids.Get(1).ToArray());
        Assert.Equal(new long[] { 10, 20 }, ids.Get(0).ToArray());
    }
}
