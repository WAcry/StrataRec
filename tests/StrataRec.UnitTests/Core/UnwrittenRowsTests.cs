using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// A row has no value in a slot until something writes it (<see cref="CandidateTable"/>): rows appended before the slot
/// existed, rows appended after it, and rows a rollback released are all missing, whatever the pooled arrays held.
/// </summary>
public class UnwrittenRowsTests
{
    private static readonly ColumnType[] EveryKind =
    {
        ColumnType.Bool, ColumnType.Int32, ColumnType.Int64, ColumnType.Float32, ColumnType.Float64, ColumnType.String,
        ColumnType.Text, ColumnType.Vector(3), ColumnType.Parse("list<int32>"), ColumnType.Parse("list<int64>"),
        ColumnType.Parse("list<string>"),
    };

    [Fact]
    public void ASlotCreatedAfterRowsWereAppended_HasNoValueOnThem_ForEveryColumnKind_EvenFromADirtyPool()
    {
        // The pool hands out arrays full of bytes that look like data and like "valid" bits.
        var memory = new RequestMemory(new DirtyPool());
        var types = new List<ColumnType> { ColumnType.Int64 };
        types.AddRange(EveryKind);
        var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, types), 8, memory);
        table.AppendRows(new long[] { 10, 20, 30 }); // an earlier row source that writes none of these columns

        for (var slot = 1; slot < types.Count; slot++)
        {
            var buffer = table.GetOrCreateSlot(slot);
            for (var row = 0; row < 3; row++)
            {
                Assert.False(buffer.IsValid(row), $"{types[slot]} row {row}");
                Assert.True(ColumnValues.Get(buffer, row).IsMissing);
            }

            Assert.Equal(3, table.CountMissingLiveRows(slot));
        }

        // A later row source appends its row and writes it: only that row has a value.
        var first = table.AppendRows(new long[] { 40 });
        var score = (ScalarColumn<double>)table.GetOrCreateSlot(5);
        score.Set(first, 0.5);
        Assert.Equal(new[] { false, false, false, true }, Enumerable.Range(0, 4).Select(score.IsValid));
        Assert.Equal(3, table.CountMissingLiveRows(5));

        // Compact keeps the state of each row it keeps.
        table.Rows.Replace(new[] { 3, 0 }, allowDrop: true);
        table.Compact();
        score = (ScalarColumn<double>)table.TryGetSlot(5)!;
        Assert.Equal((true, 0.5, false), (score.IsValid(0), score[0], score.IsValid(1)));
        memory.Release();
    }

    [Fact]
    public void ABulkWrite_MarksTheRowsItNames_AndAScratchBufferStaysAllValid()
    {
        var memory = new RequestMemory(new DirtyPool());
        var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Float64 }), 200, memory);
        table.AppendRows(Enumerable.Range(0, 150).Select(i => (long)i).ToArray());
        var slot = (ScalarColumn<double>)table.GetOrCreateSlot(1);
        var written = new[] { 149, 3, 4, 5, 64, 63, 65, 3, 0 }; // any order, repeats, across word boundaries
        foreach (var row in written)
        {
            slot.Values[row] = row;
        }

        slot.SetValid(written);

        for (var row = 0; row < 200; row++)
        {
            Assert.Equal(written.Contains(row), slot.IsValid(row));
        }

        Assert.Equal(150 - written.Distinct().Count(), table.CountMissingLiveRows(1));

        // The range form, as a row source marks the rows it appended: across a word boundary, nothing outside the range.
        slot.SetValid(60, 70);
        Assert.Equal(150 - written.Union(Enumerable.Range(60, 70)).Count(), table.CountMissingLiveRows(1));
        Assert.True(slot.IsValid(129) && !slot.IsValid(130) && !slot.IsValid(59));

        var scratch = new ScalarColumn<double>(ColumnType.Float64, 200, memory);
        scratch.SetValid(written);
        Assert.Null(scratch.Validity);
        Assert.True(scratch.IsValid(7));
        memory.Release();
    }

    [Fact]
    public void CountMissingLiveRows_CountsOnlyLiveRows()
    {
        var memory = new RequestMemory(new DirtyPool());
        var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Float64.AsNullable() }), 130, memory);
        table.AppendRows(Enumerable.Range(0, 130).Select(i => (long)i).ToArray());

        Assert.Equal(130, table.CountMissingLiveRows(1)); // never created
        Assert.Equal(0, table.CountMissingLiveRows(TableSchema.EntityIdSlot));

        var slot = (ScalarColumn<double>)table.GetOrCreateSlot(1);
        for (var row = 0; row < 130; row += 2)
        {
            slot.Set(row, row);
        }

        Assert.Equal(65, table.CountMissingLiveRows(1));
        table.Rows.Retain(Enumerable.Range(0, 130).Select(r => r % 2 == 0 || r == 129).ToArray()); // drop the unwritten odd rows but 129
        Assert.Equal(1, table.CountMissingLiveRows(1));
        memory.Release();
    }

    [Fact]
    public void ASlotCreatedByAFailedAttempt_HasNoValueOnTheRowsItReleased_OrOnRowsNobodyWrote()
    {
        // Rows 0 and 1 come from a row source without the column. A row-source attempt appends rows 2 and 3, creates the
        // shared slot, writes its rows and fails; its fallback appends three rows into the released ones and writes them.
        var memory = new RequestMemory(new DirtyPool());
        var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Float64.AsNullable() }), 5, memory);
        table.AppendRows(new long[] { 1, 2 });

        var checkpoint = table.Checkpoint();
        var attempt = table.AppendRows(new long[] { 7, 8 });
        var failed = (ScalarColumn<double>)table.GetOrCreateSlot(1);
        failed.Set(attempt, 0.7);
        failed.Set(attempt + 1, 0.8);
        table.Rollback(checkpoint);

        Assert.Null(table.TryGetSlot(1)); // the attempt created it
        Assert.Equal(2, table.CountMissingLiveRows(1));

        var fallback = table.AppendRows(new long[] { 8, 9, 11 });
        Assert.Equal(2, fallback);
        var slot = (ScalarColumn<double>)table.GetOrCreateSlot(1);
        for (var row = fallback; row < fallback + 3; row++)
        {
            Assert.False(slot.IsValid(row)); // reused rows start missing again, not with the failed attempt's 0.7 and 0.8
            slot.Set(row, row);
        }

        Assert.Equal(new[] { false, false, true, true, true }, Enumerable.Range(0, 5).Select(slot.IsValid));
        Assert.Equal(2, table.CountMissingLiveRows(1));
        memory.Release();
    }

    [Fact]
    public void Rollback_DropsTheSlotsCreatedSinceTheCheckpoint_AndKeepsTheEarlierRowsOfTheOthers()
    {
        // Slot 1 is a shared row-source slot an earlier row source wrote; slot 2 a node's own output. The failed attempt
        // appends a row, writes it into slot 1, creates slots 2 and 3 and writes them.
        var memory = new RequestMemory(new DirtyPool());
        var schema = new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Float64, ColumnType.Float64, ColumnType.Float64 });
        var table = new CandidateTable(schema, 4, memory);
        table.AppendRows(new long[] { 1, 2 });
        var shared = (ScalarColumn<double>)table.GetOrCreateSlot(1);
        shared.Set(0, 0.1);
        shared.Set(1, 0.2);

        var checkpoint = table.Checkpoint();
        var row = table.AppendRows(new long[] { 3 });
        shared.Set(row, 0.3);
        foreach (var slot in new[] { 2, 3 })
        {
            var own = (ScalarColumn<double>)table.GetOrCreateSlot(slot);
            own.Set(0, 99);
            own.Set(row, 99);
            Bits.Set(table.GetFilled(slot), 0);
        }

        table.Rollback(checkpoint);

        Assert.Same(shared, table.TryGetSlot(1));
        Assert.Equal((true, 0.1, true, 0.2, false), (shared.IsValid(0), shared[0], shared.IsValid(1), shared[1], shared.IsValid(2)));
        Assert.Null(table.TryGetSlot(2)); // reads as never written, as in a run without the attempt
        Assert.Null(table.TryGetSlot(3));
        Assert.Equal(2, table.CountMissingLiveRows(2));
        Assert.False(Bits.Get(table.GetFilled(2), 0));

        // Created again by the next writer, with every row missing; a second rollback drops only what came after it.
        var again = table.GetOrCreateSlot(2);
        Assert.False(again.IsValid(0));
        var next = table.Checkpoint();
        table.GetOrCreateSlot(3);
        table.Rollback(next);
        Assert.Same(again, table.TryGetSlot(2));
        Assert.Null(table.TryGetSlot(3));
        memory.Release();
    }

    [Fact]
    public void SetEach_SetsTheSameBitsAsSettingThemOneByOne()
    {
        var random = new Random(4646);
        for (var round = 0; round < 200; round++)
        {
            var bitCount = random.Next(1, 700);
            var indices = new int[random.Next(0, 300)];
            var ascending = random.Next(3) == 0;
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = random.Next(bitCount);
            }

            if (ascending)
            {
                Array.Sort(indices);
            }

            var words = Bits.WordCount(bitCount);
            var initial = Enumerable.Range(0, words).Select(_ => (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1)).ToArray();
            var expected = (ulong[])initial.Clone();
            foreach (var index in indices)
            {
                Bits.Set(expected, index);
            }

            var actual = (ulong[])initial.Clone();
            Bits.SetEach(actual, indices);

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Hands out new arrays filled with 0xA5 bytes (references stay null), like a pool full of an earlier request's data.</summary>
    private sealed class DirtyPool : IBufferPool
    {
        public T[] Rent<T>(int minimumLength)
        {
            var array = new T[Math.Max(1, minimumLength) + 3];
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array)), array.Length * Unsafe.SizeOf<T>()).Fill(0xA5);
            }

            return array;
        }

        public void Return<T>(T[] array, bool clearArray)
        {
        }
    }
}
