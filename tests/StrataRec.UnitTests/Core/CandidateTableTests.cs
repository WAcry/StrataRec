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
    public void Rollback_LeavesTheTableAsAtTheCheckpoint_AndTheReleasedRowsAreReusedClean()
    {
        var table = NewTable(6, out var memory);
        table.AppendRows(new long[] { 1, 2, 3 });
        var score = (ScalarColumn<double>)table.GetOrCreateSlot(1);
        var feature = (ScalarColumn<float>)table.GetOrCreateSlot(2);
        for (var r = 0; r < 3; r++)
        {
            score.Set(r, r);
            feature.Set(r, r);
            Bits.Set(table.GetFilled(2), r);
        }

        table.Rows.Retain(new[] { true, false, true }); // an earlier node filtered 2 out
        Assert.Equal(1, table.FindRow(EntityKey.FromInt64(2)));

        // The attempt appends 7 and 8, writes and fills them, looks them up, and re-ranks and drops rows.
        var checkpoint = table.Checkpoint();
        Assert.Equal(3, table.AppendRows(new long[] { 7, 8 }));
        for (var r = 3; r < 5; r++)
        {
            score.Set(r, 10 * r);
            feature.Set(r, r);
            Bits.Set(table.GetFilled(2), r);
        }

        Assert.Equal(3, table.FindRow(EntityKey.FromInt64(7)));
        table.Rows.Replace(new[] { 4, 0 }, allowDrop: true);

        table.Rollback(checkpoint);

        Assert.Equal((3, 3), (table.PhysicalRowCount, table.FreeRows));
        Assert.Equal(new[] { 0, 2 }, table.Rows.Order.ToArray());
        Assert.Equal(new[] { true, false, true, false, false }, Enumerable.Range(0, 5).Select(table.Rows.IsAlive));
        Assert.Equal(-1, table.FindRow(EntityKey.FromInt64(7)));
        Assert.Equal(-1, table.FindRow(EntityKey.FromInt64(8)));
        Assert.Equal(1, table.FindRow(EntityKey.FromInt64(2))); // filtered before the checkpoint: still known, never revived
        for (var r = 3; r < 5; r++)
        {
            // Released rows hold nothing: missing and unfilled in every slot.
            Assert.False(score.IsValid(r));
            Assert.False(feature.IsValid(r));
            Assert.False(Bits.Get(table.GetFilled(2), r));
            Assert.False(Bits.Get(table.GetFilled(TableSchema.EntityIdSlot), r));
        }

        Assert.Equal((2.0, 2f), (score[2], feature[2])); // rows from before keep their state
        Assert.True(table.IsFilledForLiveRows(2));

        // The released capacity is appended again, into the same physical rows, and the released keys may come back.
        Assert.Equal(3, table.AppendRows(new long[] { 8, 7, 9 }));
        Assert.Equal(new[] { 3, 4, 5 }, new long[] { 8, 7, 9 }.Select(id => table.FindRow(EntityKey.FromInt64(id))));
        for (var r = 3; r < 6; r++)
        {
            Assert.False(score.IsValid(r)); // not the attempt's values
            Assert.False(Bits.Get(table.GetFilled(2), r)); // to be fetched again
        }

        Assert.False(table.IsFilledForLiveRows(2));
        Assert.Throws<InvalidOperationException>(() => table.AppendRows(new long[] { 10 })); // and no more than that
        memory.Release();
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(500)]
    [InlineData(25)]
    public void StringIds_AppendedInBatches_AllocateNothingOnceThePoolIsWarm(int batch)
    {
        // String ids are distinct, so they are stored one string per row in pooled arrays: as a dictionary they took about
        // 200 KB a request for 5,000 ids that were not pooled (583 KB grown step by step; review 44), and duplicated the
        // key index. Measured on this thread, which runs the code under test alone; the first round warms the pools.
        var ids = Enumerable.Range(0, 5000).Select(i => "id-" + i).ToArray();
        var batches = Enumerable.Range(0, (ids.Length + batch - 1) / batch)
            .Select(b => (IReadOnlyList<string>)new ArraySegment<string>(ids, b * batch, Math.Min(batch, ids.Length - (b * batch)))).ToArray();
        long Appended()
        {
            var memory = new RequestMemory();
            var table = new CandidateTable(new TableSchema(EntityKeyType.String, new[] { ColumnType.String }), ids.Length, memory);
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var keys in batches)
            {
                table.AppendRows(keys);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(ids.Length, table.Rows.Count);
            Assert.Equal("id-4999", table.StringIds.Get(4999));
            memory.Release();
            return allocated;
        }

        Appended();
        var allocated = Appended();

        Assert.True(allocated < 4096, $"{allocated} B for 5,000 ids in batches of {batch}");
    }

    [Fact]
    public void Rollback_ReleasesKeysThatWereNeverLookedUp_ForStringKeysToo()
    {
        var memory = new RequestMemory();
        var table = new CandidateTable(new TableSchema(EntityKeyType.String, new[] { ColumnType.String }), 4, memory);
        table.AppendRows(new[] { "a", "b" });
        var checkpoint = table.Checkpoint();
        table.AppendRows(new[] { "c" });

        table.Rollback(checkpoint);

        Assert.Equal(-1, table.FindRow(EntityKey.FromString("c")));
        Assert.Equal(1, table.FindRow(EntityKey.FromString("b")));
        Assert.Equal(2, table.AppendRows(new[] { "c", "d" }));
        Assert.Equal((2, 3), (table.FindRow(EntityKey.FromString("c")), table.FindRow(EntityKey.FromString("d"))));
        memory.Release();
    }

    [Fact]
    public void Rollback_IsRefusedAcrossACompact_OrWithoutACheckpoint()
    {
        var table = NewTable(8, out var memory);
        table.AppendRows(new long[] { 1, 2, 3 });
        var checkpoint = table.Checkpoint();
        table.Rows.Truncate(2);
        table.Compact(); // rows moved: the checkpoint's row numbers mean something else now

        Assert.Throws<InvalidOperationException>(() => table.Rollback(checkpoint));
        Assert.Throws<InvalidOperationException>(() => table.Rollback(default));
        memory.Release();
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
    [InlineData("list<float32,64>", StorageKind.List, false, 64)]
    [InlineData("list<bool,3>?", StorageKind.List, true, 3)]
    [InlineData("list<int64>", StorageKind.List, false, 0)]
    [InlineData("list<float32>?", StorageKind.List, true, 0)]
    [InlineData("string", StorageKind.String, false, 0)]
    public void ColumnType_ParseRoundTrips(string text, StorageKind kind, bool nullable, int length)
    {
        var t = ColumnType.Parse(text);
        Assert.Equal(kind, t.Kind);
        Assert.Equal(nullable, t.Nullable);
        Assert.Equal(length, t.FixedLength);
        Assert.Equal(text, t.ToString());
    }

    [Fact]
    public void ColumnType_RejectsUnknown() => Assert.False(ColumnType.TryParse("float16", out _, out _));

    [Fact]
    public void FindRow_IndexesRowsAppendedAfterTheFirstLookup_FirstRowWins()
    {
        var table = NewTable(16, out var memory);
        Assert.Equal(-1, table.FindRow(EntityKey.FromInt64(10))); // empty table

        table.AppendRows(new long[] { 10, 20, 30 });
        Assert.Equal(1, table.FindRow(EntityKey.FromInt64(20)));
        Assert.Equal(-1, table.FindRow(EntityKey.FromInt64(40)));

        table.AppendRows(new long[] { 40, 20, 50 }); // 20 again: the first row keeps it
        Assert.Equal(3, table.FindRow(EntityKey.FromInt64(40)));
        Assert.Equal(1, table.FindRow(EntityKey.FromInt64(20)));
        Assert.Equal(5, table.FindRow(EntityKey.FromInt64(50)));
        Assert.Equal(-1, table.FindRow(EntityKey.FromString("20"))); // other key type

        // Dropped rows are still found until Compact; afterwards keys map to their new rows.
        table.Rows.Replace(new[] { 5, 0, 3 }, allowDrop: true);
        Assert.Equal(2, table.FindRow(EntityKey.FromInt64(30)));
        table.Compact();
        Assert.Equal(0, table.FindRow(EntityKey.FromInt64(50)));
        Assert.Equal(1, table.FindRow(EntityKey.FromInt64(10)));
        Assert.Equal(2, table.FindRow(EntityKey.FromInt64(40)));
        Assert.Equal(-1, table.FindRow(EntityKey.FromInt64(30)));

        table.AppendRows(new long[] { 30, 60 });
        Assert.Equal(3, table.FindRow(EntityKey.FromInt64(30)));
        Assert.Equal(4, table.FindRow(EntityKey.FromInt64(60)));
        memory.Release();
    }

    /// <summary>
    /// A key whose row a compaction released is still a key the request has seen (design 4.5, invariant 2), across
    /// several compactions and both key types; keys a rollback released were never appended (Gemini review 2026-11-14, finding 1).
    /// </summary>
    [Theory]
    [InlineData(EntityKeyType.Int64)]
    [InlineData(EntityKeyType.String)]
    public void ContainsKey_KnowsTheKeysACompactReleased_AcrossCompactions_ButNotWhatARollbackReleased(EntityKeyType keyType)
    {
        var memory = new RequestMemory();
        var table = new CandidateTable(new TableSchema(keyType, new[] { keyType == EntityKeyType.Int64 ? ColumnType.Int64 : ColumnType.String }), 8, memory);
        EntityKey K(long n) => keyType == EntityKeyType.Int64 ? EntityKey.FromInt64(n) : EntityKey.FromString("k" + n);
        void Append(params long[] keys)
        {
            if (keyType == EntityKeyType.Int64)
            {
                table.AppendRows(keys);
            }
            else
            {
                table.AppendRows(keys.Select(k => "k" + k).ToArray());
            }
        }

        Append(1, 2, 3, 4);
        table.Rows.Retain(new[] { false, true, false, false }); // only 2 stays
        Assert.True(table.ContainsKey(K(1))); // dropped, not yet released
        table.Compact();

        Assert.Equal(-1, table.FindRow(K(1))); // no row any more
        Assert.True(table.ContainsKey(K(1)) && table.ContainsKey(K(3)) && table.ContainsKey(K(4)) && table.ContainsKey(K(2)));
        Assert.False(table.ContainsKey(K(5)));
        Assert.False(table.ContainsKey(keyType == EntityKeyType.Int64 ? EntityKey.FromString("k1") : EntityKey.FromInt64(1))); // other key type

        var checkpoint = table.Checkpoint();
        Append(6);
        Assert.True(table.ContainsKey(K(6)));
        table.Rollback(checkpoint);
        Assert.False(table.ContainsKey(K(6))); // a failed attempt's key was never appended

        Append(5, 6);
        table.Rows.Retain(new[] { true, false, true }); // 2 and 6 stay; 5 is dropped
        table.Compact();
        Assert.Equal(-1, table.FindRow(K(5)));
        Assert.True(table.ContainsKey(K(5)) && table.ContainsKey(K(1)) && table.ContainsKey(K(6)));
        Assert.Equal((0, 1), (table.FindRow(K(2)), table.FindRow(K(6))));
        memory.Release();
    }

    [Fact]
    public void FindRow_StringKeys()
    {
        var memory = new RequestMemory();
        var table = new CandidateTable(new TableSchema(EntityKeyType.String, new[] { ColumnType.String }), 8, memory);
        table.AppendRows(new[] { "a", "B", "a" });
        Assert.Equal(0, table.FindRow(EntityKey.FromString("a")));
        Assert.Equal(1, table.FindRow(EntityKey.FromString("B")));
        Assert.Equal(-1, table.FindRow(EntityKey.FromString("b"))); // ordinal
        Assert.Equal(-1, table.FindRow(EntityKey.FromInt64(0)));

        table.AppendRows(new[] { "c" });
        Assert.Equal(3, table.FindRow(EntityKey.FromString("c")));
        memory.Release();
    }
}
