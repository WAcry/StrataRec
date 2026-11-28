using System.Runtime.CompilerServices;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;
using static StrataRec.UnitTests.Features.Local.LocalTableTestSupport;

namespace StrataRec.UnitTests.Features.Local;

public class LocalTableWriterTests
{
    // The pending buffer starts at 256 bytes: 64 int32 items or 32 int64 items fill it exactly.
    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(10_000)]
    public void Int32ListItems_SurviveGrowthOfAFreshBuffer(int count)
    {
        var table = Table();
        var field = table.Schema.FieldIndex("li32");
        var builder = table.Writer.Begin(1L, 1);
        builder.StartList(field);
        for (var i = 1; i <= count; i++)
        {
            builder.AddInt32Item(field, i);
        }

        Assert.True(builder.Commit());
        Assert.Equal(Sequence(count), table.Pin().Read(field, table.Pin().Find(1L)).AsInt64List().ToArray());
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(5_000)]
    public void Int64ListItems_SurviveGrowthOfAFreshBuffer(int count)
    {
        var table = Table();
        var field = table.Schema.FieldIndex("li64");
        var builder = table.Writer.Begin(1L, 1);
        builder.StartList(field);
        for (var i = 1; i <= count; i++)
        {
            builder.AddInt64Item(field, i);
        }

        Assert.True(builder.Commit());
        Assert.Equal(Sequence(count), table.Pin().Read(field, table.Pin().Find(1L)).AsInt64List().ToArray());
    }

    [Theory]
    [InlineData(40)]
    [InlineData(43)]
    [InlineData(3_000)]
    public void StringListItems_SurviveGrowthOfAFreshBuffer(int count)
    {
        var table = Table();
        var field = table.Schema.FieldIndex("ls");
        var expected = new string[count];
        var builder = table.Writer.Begin(1L, 1);
        builder.StartList(field);
        for (var i = 0; i < count; i++)
        {
            expected[i] = "tag-" + i;
            builder.AddStringItem(field, expected[i]);
        }

        Assert.True(builder.Commit());
        Assert.Equal(expected, table.Pin().Read(field, table.Pin().Find(1L)).AsStringList().ToArray());
    }

    /// <summary>
    /// A text of n chars leaves the buffer at 2n bytes, not a power of two; the next record's list then crosses that
    /// size mid-list.
    /// </summary>
    [Theory]
    [InlineData(450)]
    [InlineData(451)]
    public void ListItems_CrossingAReusedBufferOfOddSize_AreKept(int textChars)
    {
        var table = Table();
        var schema = table.Schema;
        var text = table.Writer.Begin(1L, 1);
        text.SetString(schema.FieldIndex("t"), new string('x', textChars));
        Assert.True(text.Commit());

        var capacity = 2 * textChars;
        var int32Count = (capacity / 4) + 1;
        var int64Count = (capacity / 8) + 1;
        var stringCount = (capacity / 8) + 1; // a one-char item takes 8 bytes: length, char, padding
        var builder = table.Writer.Begin(2L, 1);
        builder.SetInt32List(schema.FieldIndex("li32"), Sequence32(int32Count));
        Assert.True(builder.Commit());
        builder = table.Writer.Begin(3L, 1);
        builder.SetInt64List(schema.FieldIndex("li64"), Sequence(int64Count));
        Assert.True(builder.Commit());
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
        {
            strings[i] = ((char)('a' + (i % 26))).ToString();
        }

        builder = table.Writer.Begin(4L, 1);
        builder.SetStringList(schema.FieldIndex("ls"), strings);
        Assert.True(builder.Commit());

        var version = table.Pin();
        Assert.Equal(new string('x', textChars), version.Read(schema.FieldIndex("t"), version.Find(1L)).AsString());
        Assert.Equal(Sequence(int32Count), version.Read(schema.FieldIndex("li32"), version.Find(2L)).AsInt64List().ToArray());
        Assert.Equal(Sequence(int64Count), version.Read(schema.FieldIndex("li64"), version.Find(3L)).AsInt64List().ToArray());
        Assert.Equal(strings, version.Read(schema.FieldIndex("ls"), version.Find(4L)).AsStringList().ToArray());
    }

    [Fact]
    public void MaxBytes_ChargesEveryGrowthOfTheDictionary()
    {
        // 100 new strings in one record grow the dictionary array (16 → 128) and its code map (16 → 256) several times.
        var strings = new string[100];
        for (var i = 0; i < strings.Length; i++)
        {
            strings[i] = "distinct-" + i;
        }

        var options = new LocalFeatureTableOptions { MaxBytes = 9000, PageSize = 4096 };
        var table = new LocalFeatureTable(Schema(), options);
        var before = table.Pin().Bytes;
        var builder = table.Writer.Begin(1L, 1);
        builder.SetStringList(table.Schema.FieldIndex("ls"), strings);
        Assert.Throws<LocalTableFullException>(() => builder.Commit());
        Assert.Equal(before, table.Pin().Bytes);
        Assert.Equal(0, table.Pin().RecordCount);

        var needed = BytesAfter(long.MaxValue, w => WriteStrings(w, 1, strings));
        Assert.True(needed > 9000);
        Assert.Equal(needed, BytesAfter(needed, w => WriteStrings(w, 1, strings)));
        AssertRejected(needed - 1, w => WriteStrings(w, 1, strings));
    }

    [Fact]
    public void MaxBytes_DoesNotChargeAStringTheDictionaryHolds()
    {
        var value = new string('v', 1000);
        var table = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = 7000, PageSize = 4096 });
        var field = table.Schema.FieldIndex("s");
        for (var key = 1L; key <= 2; key++)
        {
            var builder = table.Writer.Begin(key, 1);
            builder.SetString(field, value);
            Assert.True(builder.Commit());
        }

        var version = table.Pin();
        Assert.True(version.Bytes <= 7000);
        Assert.Equal(value, version.Read(field, version.Find(2L)).AsString());
    }

    [Fact]
    public void MaxBytes_ChargesARepeatedNewStringOnce()
    {
        void Write(IFeatureTableWriter writer)
        {
            var builder = writer.Begin(1L, 1);
            builder.SetString(writer.Schema.FieldIndex("s"), "repeated-value");
            builder.SetStringList(writer.Schema.FieldIndex("ls"), new[] { "repeated-value", "other", "other", "repeated-value", "third" });
            builder.Commit();
        }

        var needed = BytesAfter(long.MaxValue, Write);
        Assert.Equal(needed, BytesAfter(needed, Write));
        AssertRejected(needed - 1, Write);
    }

    [Fact]
    public void MaxBytes_RechargesStringsAfterACompactionDropsThem()
    {
        // Key 1's first record is the only user of `first`; once it is superseded, a compaction drops `first` from the
        // dictionary, so key 2's use of it is new again and must be charged after that compaction.
        var first = new string('f', 600);
        var second = new string('s', 300);
        void Setup(IFeatureTableWriter writer)
        {
            WriteString(writer, 1, 1, first);
            WriteString(writer, 1, 2, "b");
        }

        void Last(IFeatureTableWriter writer)
        {
            var builder = writer.Begin(2L, 1);
            builder.SetString(writer.Schema.FieldIndex("s"), first);
            builder.SetStringList(writer.Schema.FieldIndex("ls"), new[] { second });
            builder.Commit();
        }

        AssertRechargedAfterCompaction(Setup, Last, lastKey: 2);
    }

    [Fact]
    public void MaxBytes_OneNewString_IsChargedExactly()
    {
        var value = new string('n', 700);
        var needed = BytesAfter(long.MaxValue, w => WriteString(w, 1, 1, value));
        Assert.Equal(needed, BytesAfter(needed, w => WriteString(w, 1, 1, value)));
        AssertRejected(needed - 1, w => WriteString(w, 1, 1, value));
    }

    [Fact]
    public void MaxBytes_RechargesASingleStringAfterACompactionDropsIt()
    {
        // Keys 2-8 fill the index to its limit, so the last write also grows the index.
        var first = new string('f', 600);
        void Setup(IFeatureTableWriter writer)
        {
            WriteString(writer, 1, 1, first);
            WriteString(writer, 1, 2, "b");
            for (var key = 2; key <= 8; key++)
            {
                WriteString(writer, key, 1, "b");
            }
        }

        AssertRechargedAfterCompaction(Setup, w => WriteString(w, 9, 1, first), lastKey: 9);
    }

    [Fact]
    public void MaxBytes_BelowAnEmptyTable_IsRejectedAtConstruction()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = 1, InitialCapacity = 1024 }));
        Assert.Contains("max_bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("raise max_bytes", error.Message, StringComparison.Ordinal);

        var empty = new LocalFeatureTable(Schema()).Pin().Bytes;
        Assert.Equal(empty, new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = empty }).Pin().Bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = empty - 1 }));
    }

    /// <summary>
    /// A string costs its record only a 4-byte code, so a field rewritten with long new values supersedes small records
    /// and leaves its old values in the dictionary. The automatic compaction counts those strings (once no current
    /// record references them) with the superseded records: here the records alone (20 superseded of 120) would never
    /// compact, while most of the table's bytes are strings nothing reads.
    /// </summary>
    [Fact]
    public void StringsOnlySupersededRecordsHeld_DriveTheAutomaticCompaction_AndAStringACurrentRecordHoldsIsNotCounted()
    {
        var schema = Schema(fields: new[]
        {
            new LocalTableField("d", ColumnType.String),
            new LocalTableField("c", ColumnType.String),
            new LocalTableField("tags", ColumnType.List(StorageKind.String)),
        });
        static string Long(long key, int version) => new string((char)('a' + version), 2_000) + key;
        static void Write(LocalTableWriter writer, long key, long version, string description)
        {
            var builder = writer.Begin(key, version);
            builder.SetString(0, description);
            builder.SetString(1, "shared");
            builder.SetStringList(2, new[] { "shared", "tag" + (key % 3) });
            Assert.True(builder.Commit());
        }

        static void Fill(LocalTableWriter writer)
        {
            for (var key = 1; key <= 100; key++)
            {
                Write(writer, key, 1, "short " + key);
            }

            for (var version = 2; version <= 5; version++)
            {
                for (var key = 1; key <= 5; key++)
                {
                    Write(writer, key, version, Long(key, version));
                }
            }
        }

        var watched = new LocalFeatureTable(schema, new LocalFeatureTableOptions { CompactionMinBytes = long.MaxValue });
        Fill(watched.Writer);
        var version = watched.Pin();
        long dead = 0; // keys 1-5: the short description and versions 2-4
        for (var key = 1; key <= 5; key++)
        {
            dead += TableVersion.StringObjectBytes(("short " + key).Length) + (3 * TableVersion.StringObjectBytes(Long(key, 2).Length));
        }

        Assert.Equal(dead, version.UnreferencedStringBytes);
        Assert.True(version.SupersededBytes * 2 < version.RecordBytes, "the records alone would not compact");
        Assert.True((version.SupersededBytes + version.UnreferencedStringBytes) * 2 > version.RecordBytes + version.StringBytes);

        watched.Writer.Compact();
        var compacted = watched.Pin();
        Assert.Equal((0L, 0L), (compacted.SupersededBytes, compacted.UnreferencedStringBytes));
        Assert.Equal(version.StringBytes - dead, compacted.StringBytes);
        Write(watched.Writer, 1, 6, "short again"); // after the compaction, key 1's long value is the only dead string
        Assert.Equal(TableVersion.StringObjectBytes(Long(1, 5).Length), watched.Pin().UnreferencedStringBytes);
        Write(watched.Writer, 2, 6, Long(3, 5)); // key 3's current value: shared, not new, and key 2's goes
        Assert.Equal(2 * TableVersion.StringObjectBytes(Long(1, 5).Length), watched.Pin().UnreferencedStringBytes);

        var automatic = new LocalFeatureTable(schema, new LocalFeatureTableOptions { CompactionMinBytes = 0 });
        Fill(automatic.Writer);
        Assert.True(automatic.Compactions > 0);
        var after = automatic.Pin();
        Assert.True(after.StringBytes < version.StringBytes - (dead / 2), $"{after.StringBytes} B of strings");
        for (var key = 1L; key <= 100; key++)
        {
            var record = after.Find(key);
            Assert.Equal(key <= 5 ? Long(key, 5) : "short " + key, after.Read(0, record).AsString());
            Assert.Equal("shared", after.Read(1, record).AsString());
            Assert.Equal(new[] { "shared", "tag" + (key % 3) }, after.Read(2, record).AsStringList().ToArray());
        }
    }

    /// <summary>String lengths whose chars end just before, at and after the staging buffer's growth points (256 bytes, then
    /// sizes that are not multiples of 8, where a list's aligned start would land past the end).</summary>
    public static IEnumerable<object[]> EmptyListsAfterText()
    {
        foreach (var list in new[] { "li32", "li64", "ls" })
        {
            foreach (var chars in new[] { 0, 1, 127, 128, 129, 130, 255, 256, 257, 258, 259, 1000, 1001 })
            {
                yield return new object[] { list, chars };
            }
        }
    }

    [Theory]
    [MemberData(nameof(EmptyListsAfterText))]
    public void EmptyList_AfterTextOfAnyLength_IsKept_AndIsNotMissing(string list, int textChars)
    {
        var schema = Schema(fields: new[]
        {
            new LocalTableField("t", ColumnType.String),
            new LocalTableField("li32", ColumnType.List(StorageKind.Int32)),
            new LocalTableField("li64", ColumnType.List(StorageKind.Int64)),
            new LocalTableField("ls", ColumnType.List(StorageKind.String)),
        });
        var table = new LocalFeatureTable(schema);
        var field = schema.FieldIndex(list);
        var text = new string('x', textChars);

        var builder = table.Writer.Begin(1L, 1);
        builder.SetString(0, text);
        builder.StartList(field);
        Assert.True(builder.Commit());
        builder = table.Writer.Begin(2L, 1);
        builder.SetString(0, text);
        builder.SetMissing(field);
        Assert.True(builder.Commit());

        var version = table.Pin();
        var empty = version.Read(field, version.Find(1L));
        Assert.False(empty.IsMissing);
        Assert.Equal(0, list == "ls" ? empty.AsStringList().ToArray().Length : empty.AsInt64List().ToArray().Length);
        Assert.True(version.Read(field, version.Find(2L)).IsMissing);
        Assert.Equal(text, version.Read(0, version.Find(1L)).AsString());
        Assert.Equal(text, version.Read(0, version.Find(2L)).AsString());
    }

    [Fact]
    public void FailedWrite_LeavesTheTableAsItWas_ThoughACompactionWasTried()
    {
        var schema = Schema(fields: new[] { new LocalTableField("l", ColumnType.List(StorageKind.Int32)) });
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions { PageSize = 4096, MaxBytes = 42368, CompactionMinBytes = long.MaxValue });
        for (var key = 1; key <= 18; key++)
        {
            WritePayload(table.Writer, key, 1, key % 2 == 1 ? 2536 : 1512);
        }

        WritePayload(table.Writer, 2, 2, 1512);
        var before = table.Pin();
        Assert.Equal(42368, before.Bytes);
        Assert.True(before.SupersededBytes > 0);

        Assert.Throws<LocalTableFullException>(() => WritePayload(table.Writer, 999, 1, 20_000));
        var after = table.Pin();
        Assert.Same(before, after);
        Assert.Equal(42368, after.Bytes);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(0, table.Compactions);
        Assert.Equal(18, after.RecordCount);
        Assert.False(after.Find(999L).Found);
        Assert.Equal(378, after.Read(0, after.Find(2L)).ListLength);

        table.Writer.Compact();
        Assert.NotSame(before, table.Pin());
        Assert.True(table.Pin().Bytes < before.Bytes);
        Assert.Equal(18, table.Pin().RecordCount);
    }

    [Theory]
    [InlineData("empty reload")]
    [InlineData("compaction")]
    public void AReplacedVersion_IsNotKeptAliveByTheWriter_WhenNothingIsWrittenAfter(string replacement)
    {
        var table = Table();
        var old = WriteThenReplace(table, replacement);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(old.IsAlive, $"the writer keeps the version replaced by the {replacement} alive");
    }

    /// <summary>Not inlined, so no local here keeps it alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WriteThenReplace(LocalFeatureTable table, string replacement)
    {
        for (var id = 1; id <= 200; id++)
        {
            Write(table.Writer, EntityKey.FromInt64(id), 1, Values(id));
        }

        Write(table.Writer, EntityKey.FromInt64(1), 2, Values(1));
        var old = new WeakReference(table.Pin());
        if (replacement == "compaction")
        {
            table.Writer.Compact();
        }
        else
        {
            table.Writer.BeginReload();
            table.Writer.CompleteReload(UpdatePosition.Single(1));
        }

        Assert.NotSame(old.Target, table.Pin());
        return old;
    }

    [Fact]
    public void RejectedWrite_DoesNotKeepTheDiscardedCompactedCopyAlive()
    {
        var schema = Schema(fields: new[] { new LocalTableField("l", ColumnType.List(StorageKind.Int32)), new LocalTableField("s", ColumnType.String) });
        var options = new LocalFeatureTableOptions { PageSize = 4096, MaxBytes = 46512, CompactionMinBytes = long.MaxValue };
        var table = new LocalFeatureTable(schema, options);
        FillPayloadAndCategory(table.Writer);
        var before = table.Pin();
        Assert.Equal(46512, before.Bytes);
        Assert.True(before.SupersededBytes > 0);

        var copy = FailWriteThatNeedsACompaction(table.Writer);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(copy.IsAlive, "the writer keeps the compacted copy of a rejected write alive");
        Assert.Same(before, table.Pin());
        Assert.Equal((46512L, before.Generation, 0L), (table.Pin().Bytes, table.Pin().Generation, table.Compactions));
        Assert.False(table.Pin().Find(999L).Found);
    }

    [Fact]
    public void CacheWriteBack_CanCollectARejectedCompactionWhileEvicting()
    {
        var schema = Schema(mode: LocalTableMode.Cache, fields: [new("l", ColumnType.List(StorageKind.Int32)), new("s", ColumnType.String)]);
        var clock = new ManualClock();
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions
        {
            PageSize = 4096, MaxBytes = 46512, CompactionMinBytes = long.MaxValue, Cache = new CacheTableOptions { TtlMs = 60_000, Clock = clock },
        });
        FillPayloadAndCategory(table.Writer);
        var cache = table.Cache!;
        var record = new CacheRecordWriter(cache.Layout, 128);
        record.Begin(clock.NowMs, cache.Fingerprint, absent: false);
        record.List<int>(0, new int[5_000]); // too large after packing the live records, fits after eviction
        record.String(1, "category");
        var (offset, length) = record.End();
        Assert.True(cache.TryWriteBack(new CacheWriteBatch([EntityKey.FromInt64(999)], [record.Bytes.AsMemory(offset, length)], 1)));

        WeakReference? previous = null;
        table.Writer.CompactedCopyBuilt = copy =>
        {
            if (previous is not null)
            {
                GC.Collect();
                Assert.False(previous.IsAlive, "the rejected copy is still alive while the next copy is built");
            }

            previous = new WeakReference(copy);
        };
        cache.WriteQueued(table.Writer);
        Assert.Equal("category", table.Pin().Read(1, table.Pin().Find(999L)).AsString());
    }

    [Fact]
    public void ExplicitCompaction_ThatWouldNotFit_IsRejected_AndTheTableIsUnchanged()
    {
        // The copy opens its first page for all the bytes still to copy (a whole 64 KiB page), puts key 1 in it, and
        // key 2 does not fit: it takes a second page, and the first stays almost empty.
        var schema = Schema(fields: new[] { new LocalTableField("l", ColumnType.List(StorageKind.Int32)) });
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions { PageSize = 65536, MaxBytes = 84696, CompactionMinBytes = long.MaxValue });
        WritePayload(table.Writer, 1, 1, 0);
        WritePayload(table.Writer, 2, 1, 80_000);
        var before = table.Pin();
        Assert.Equal(84696, before.Bytes);

        var full = Assert.Throws<LocalTableFullException>(() => table.Writer.Compact());

        Assert.Equal((84696L, 146136L), (full.MaxBytes, full.RequiredBytes));
        Assert.Same(before, table.Pin());
        Assert.Equal((84696L, before.Generation, 0L), (table.Pin().Bytes, table.Pin().Generation, table.Compactions));
        Assert.Equal(20_000, before.Read(0, before.Find(2L)).ListLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MaxBytes_HoldsAfterEveryOperation_AndAFailedOneChangesNothing(int seed)
    {
        var schema = Schema(fields: new[]
        {
            new LocalTableField("t", ColumnType.String),
            new LocalTableField("s", ColumnType.String),
            new LocalTableField("li32", ColumnType.List(StorageKind.Int32)),
        });
        var options = new LocalFeatureTableOptions { PageSize = 1024, MaxBytes = 34_000, CompactionMinBytes = 512 };
        var table = new LocalFeatureTable(schema, options);
        var model = new Dictionary<long, (string Text, string String, int[] List)>();
        var random = new Random(seed);
        var failures = 0;
        var tooLarge = 0;
        for (var op = 1; op <= 3000; op++)
        {
            var before = table.Pin();
            var bytes = before.Bytes;
            try
            {
                if (op % 101 == 0)
                {
                    table.Writer.Compact();
                }
                else
                {
                    var key = 1 + random.Next(40);
                    var chars = op % 37 == 0 ? 20_000 : random.Next(300);
                    tooLarge += op % 37 == 0 ? 1 : 0;
                    var value = (new string((char)('a' + random.Next(26)), chars), "s" + random.Next(60), Sequence32(random.Next(20)));
                    var builder = table.Writer.Begin(key, op);
                    builder.SetString(0, value.Item1);
                    builder.SetString(1, value.Item2);
                    builder.SetInt32List(2, value.Item3);
                    builder.Commit();
                    model[key] = value;
                }
            }
            catch (LocalTableFullException)
            {
                failures++;
                Assert.Same(before, table.Pin());
                Assert.Equal(bytes, table.Pin().Bytes);
                Assert.Equal(before.Generation, table.Pin().Generation);
            }

            Assert.True(table.Pin().Bytes <= options.MaxBytes, $"operation {op}: {table.Pin().Bytes} bytes");
        }

        Assert.InRange(failures, tooLarge, tooLarge + 300);
        Assert.True(table.Compactions > 0);
        var version = table.Pin();
        Assert.Equal(model.Count, version.RecordCount);
        foreach (var (key, (text, value, list)) in model)
        {
            var record = version.Find(key);
            Assert.Equal(text, version.Read(0, record).AsString());
            Assert.Equal(value, version.Read(1, record).AsString());
            Assert.Equal(list.Select(x => (long)x).ToArray(), version.Read(2, record).AsInt64List().ToArray());
        }
    }

    private static void WritePayload(IFeatureTableWriter writer, long key, long version, int bytes)
    {
        var builder = writer.Begin(key, version);
        builder.SetInt32List(0, new int[(bytes + 3) / 4]);
        Assert.True(builder.Commit());
    }

    /// <summary>
    /// <paramref name="last"/> reuses a string only a superseded record of <paramref name="setup"/> holds. A writer
    /// that kept the string's charge from before the compaction would take the copy and end above the limit.
    /// </summary>
    private static void AssertRechargedAfterCompaction(Action<IFeatureTableWriter> setup, Action<IFeatureTableWriter> last, long lastKey)
    {
        var plain = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { PageSize = 4096 });
        setup(plain.Writer);
        last(plain.Writer);
        var withoutCompaction = plain.Pin().Bytes;
        Assert.Equal(0, plain.Compactions);

        var compacted = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { PageSize = 4096 });
        setup(compacted.Writer);
        compacted.Writer.Compact();
        last(compacted.Writer);
        var afterCompaction = compacted.Pin().Bytes;

        Assert.Equal(withoutCompaction, BytesAfter(withoutCompaction, w => { setup(w); last(w); }));

        var limit = Math.Min(withoutCompaction, afterCompaction) - 1;
        var table = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = limit, PageSize = 4096 });
        setup(table.Writer);
        var before = table.Pin();
        Assert.True(before.SupersededBytes > 0);
        Assert.Throws<LocalTableFullException>(() => last(table.Writer));
        Assert.Same(before, table.Pin());
        Assert.Equal(before.Generation, table.Pin().Generation);
        Assert.Equal(0, table.Compactions);
        Assert.False(table.Pin().Find(lastKey).Found);
        Assert.Equal("b", before.Read(table.Schema.FieldIndex("s"), before.Find(1L)).AsString());
    }

    private static void FillPayloadAndCategory(IFeatureTableWriter writer)
    {
        for (var key = 1; key <= 20; key++)
        {
            WritePayloadAndCategory(writer, key, 1, 2_000);
        }

        WritePayloadAndCategory(writer, 1, 2, 14);
    }

    private static void WritePayloadAndCategory(IFeatureTableWriter writer, long key, long version, int bytes)
    {
        var builder = writer.Begin(key, version);
        builder.SetInt32List(writer.Schema.FieldIndex("l"), new int[(bytes + 3) / 4]);
        builder.SetString(writer.Schema.FieldIndex("s"), "category");
        builder.Commit();
    }

    /// <summary>Not inlined, so no local of this frame keeps the copy alive in the caller.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FailWriteThatNeedsACompaction(LocalTableWriter writer)
    {
        WeakReference? copy = null;
        writer.CompactedCopyBuilt = version => copy = new WeakReference(version);
        try
        {
            Assert.Throws<LocalTableFullException>(() => WritePayloadAndCategory(writer, 999, 1, 2_000_000));
        }
        finally
        {
            writer.CompactedCopyBuilt = null;
        }

        Assert.NotNull(copy);
        return copy;
    }

    private static long BytesAfter(long maxBytes, Action<IFeatureTableWriter> write)
    {
        var table = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = maxBytes, PageSize = 4096 });
        write(table.Writer);
        Assert.True(table.Pin().Bytes <= maxBytes);
        return table.Pin().Bytes;
    }

    private static void AssertRejected(long maxBytes, Action<IFeatureTableWriter> write)
    {
        var table = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = maxBytes, PageSize = 4096 });
        var before = table.Pin().Bytes;
        Assert.Throws<LocalTableFullException>(() => write(table.Writer));
        Assert.Equal(before, table.Pin().Bytes);
        Assert.Equal(0, table.Pin().RecordCount);
    }

    private static void WriteStrings(IFeatureTableWriter writer, long key, string[] items)
    {
        var builder = writer.Begin(key, 1);
        builder.SetStringList(writer.Schema.FieldIndex("ls"), items);
        builder.Commit();
    }

    private static void WriteString(IFeatureTableWriter writer, long key, long version, string value)
    {
        var builder = writer.Begin(EntityKey.FromInt64(key), version);
        builder.SetString(writer.Schema.FieldIndex("s"), value);
        Assert.True(builder.Commit());
    }

    private static long[] Sequence(int count)
    {
        var values = new long[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = i + 1;
        }

        return values;
    }

    private static int[] Sequence32(int count)
    {
        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = i + 1;
        }

        return values;
    }
}
