using StrataRec.Core.Columns;
using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.UnitTests.Core;

/// <summary>Regression tests for findings from the Core review.</summary>
public class CoreReviewRegressionTests
{
    private static CandidateTable NewTable(int capacity, params ColumnType[] extra)
    {
        var schema = new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64 }.Concat(extra).ToArray());
        return new CandidateTable(schema, capacity, new RequestMemory());
    }

    [Fact]
    public void Compact_ShrinksStorageToSurvivorsPlusRemainingBudget()
    {
        var table = NewTable(1000, ColumnType.Vector(64));
        table.AppendRows(Enumerable.Range(0, 900).Select(i => (long)i).ToArray());
        var vec = (VectorColumn)table.GetOrCreateSlot(1);
        Assert.Equal(1000, vec.Capacity);

        table.Rows.Truncate(50);
        table.Compact();
        Assert.Equal(50 + 100, table.StorageCapacity);
        Assert.Equal(150, ((VectorColumn)table.TryGetSlot(1)!).Capacity);

        // The remaining append budget still fits after compaction.
        table.AppendRows(Enumerable.Range(5000, 100).Select(i => (long)i).ToArray());
        Assert.Equal(150, table.Rows.Count);
        Assert.Equal(0, table.FreeRows);
    }

    [Fact]
    public void Append_MarksExistingOutputSlotsMissingAndKeysFilled()
    {
        var table = NewTable(8, ColumnType.Float64.AsNullable());
        table.AppendRows(new long[] { 1, 2 });
        var score = (ScalarColumn<double>)table.GetOrCreateSlot(1);
        score.Set(0, 1.0);
        score.Set(1, 2.0);

        table.AppendRows(new long[] { 3 });
        Assert.True(score.IsValid(0));
        Assert.False(score.IsValid(2));
        Assert.True(table.IsFilledForLiveRows(TableSchema.EntityIdSlot));
    }

    [Fact]
    public void VectorCopy_RejectsDimensionMismatch()
    {
        var memory = new RequestMemory();
        var a = new VectorColumn(ColumnType.Vector(2), 4, memory);
        var b = new VectorColumn(ColumnType.Vector(3), 4, memory);
        a.Set(0, new float[] { 1, 2 });
        Assert.Throws<ArgumentException>(() => b.CopyFrom(a, 0, 0));
    }

    [Fact]
    public void ListSelfCopy_PreservesMissing()
    {
        var list = new ListColumn<long>(ColumnType.ListInt64.AsNullable(), 4, new RequestMemory());
        list.Set(0, new long[] { 7 });
        list.SetMissing(0);
        list.CopyFrom(list, 0, 0);
        Assert.False(list.IsValid(0));
    }

    [Fact]
    public void ScalarValue_EqualValuesHashEqually()
    {
        Assert.Equal(ScalarValue.FromInt64List(new long[] { 1, 2 }).GetHashCode(), ScalarValue.FromInt64List(new long[] { 1, 2 }).GetHashCode());
        Assert.Equal(ScalarValue.FromVector(new float[] { 1, 2 }).GetHashCode(), ScalarValue.FromVector(new float[] { 1, 2 }).GetHashCode());
        Assert.Equal(ScalarValue.FromFloat64(0.0), ScalarValue.FromFloat64(-0.0));
        Assert.Equal(ScalarValue.FromFloat64(0.0).GetHashCode(), ScalarValue.FromFloat64(-0.0).GetHashCode());
    }

    [Fact]
    public void ScalarValue_RejectsTwoPow63AsInt64()
    {
        Assert.False(ScalarValue.FromFloat64(9223372036854775808.0).TryConvert(ColumnType.Int64, out _));
        Assert.True(ScalarValue.FromFloat64(-9223372036854775808.0).TryConvert(ColumnType.Int64, out var min));
        Assert.Equal(long.MinValue, min.AsInt64());
    }

    [Fact]
    public void RequestMemory_RentAfterReturnThrows()
    {
        var memory = new RequestMemory();
        memory.Release();
        Assert.Throws<ObjectDisposedException>(() => memory.Rent<int>(16));
    }

    [Fact]
    public void StableHash_BuilderMatchesFixedArityOverloads()
    {
        using var b = new StableHash.Builder();
        b.Add(42L);
        b.Add(7L);
        Assert.Equal(StableHash.Hash(42, 7), b.Finish());
        b.Reset();
        b.Add("a much longer string key than the initial pooled buffer would hold " + new string('x', 200));
        var h1 = b.Finish();
        b.Reset();
        b.Add("a much longer string key than the initial pooled buffer would hold " + new string('x', 200));
        Assert.Equal(h1, b.Finish());
        Assert.InRange(StableHash.Hash01(1, 2), 0.0, 1.0);
    }
}
