using System.Runtime.CompilerServices;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Local;
using static StrataRec.UnitTests.Features.Local.LocalTableTestSupport;

namespace StrataRec.UnitTests.Features.Local;

/// <summary>
/// The writer's pending buffer (list items and strings are staged in one growable byte array until commit) and the
/// <c>max_bytes</c> charge for new dictionary strings.
/// </summary>
public class LocalTableWriterTests
{
    // The pending buffer starts at 256 bytes: 64 int32 items or 32 int64 items fill it exactly.
    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(10_000)] // many growths in a row
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
    /// size mid-list. The item that crosses it must land in the grown buffer, not in the old one (where it would be lost
    /// and, at 451 chars, write past the old array's end), and the stale text bytes must not show through.
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

        // What it costs, measured without a limit, is exactly the limit it needs.
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
        // A record repeating its new strings (in a string field and in list items) is charged for each distinct one once.
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
        // As above with one string value (the writer's fast path). Keys 2-8 fill the index to its limit, so the last write
        // also grows the index.
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

    /// <summary>Text lengths whose chars end just before, at and after the staging buffer's growth points (256 bytes, then
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
        // Codex review 9, item 6: after a text that grew the staging buffer to a size that is not a multiple of 8, an
        // empty list's aligned start was past the buffer's end and Commit threw.
        var schema = Schema(fields: new[]
        {
            new LocalTableField("t", ColumnType.Text),
            new LocalTableField("li32", ColumnType.ListInt32),
            new LocalTableField("li64", ColumnType.ListInt64),
            new LocalTableField("ls", ColumnType.ListString),
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
        // Codex review 9, item 3: the write needs a compaction to have a chance, the compacted copy cannot make room for
        // it, and the write fails. The copy must not be published: same version, bytes and generation.
        var schema = Schema(fields: new[] { new LocalTableField("t", ColumnType.Text) });
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions { PageSize = 4096, MaxBytes = 42304, CompactionMinBytes = long.MaxValue });
        for (var key = 1; key <= 18; key++)
        {
            WriteText(table.Writer, key, 1, key % 2 == 1 ? 1268 : 756);
        }

        WriteText(table.Writer, 2, 2, 756);
        var before = table.Pin();
        Assert.Equal(42304, before.Bytes);
        Assert.True(before.SupersededBytes > 0);

        Assert.Throws<LocalTableFullException>(() => WriteText(table.Writer, 999, 1, 10_000));
        var after = table.Pin();
        Assert.Same(before, after);
        Assert.Equal(42304, after.Bytes);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(0, table.Compactions);
        Assert.Equal(18, after.RecordCount);
        Assert.False(after.Find(999L).Found);
        Assert.Equal(new string('t', 756), after.Read(0, after.Find(2L)).AsString());

        // An explicit compaction fits (the records are packed in their old order) and is published.
        table.Writer.Compact();
        Assert.NotSame(before, table.Pin());
        Assert.True(table.Pin().Bytes < before.Bytes);
        Assert.Equal(18, table.Pin().RecordCount);
    }

    [Fact]
    public void RejectedWrite_DoesNotKeepTheDiscardedCompactedCopyAlive()
    {
        // Codex review 11, item 3: the write's one string is looked up in the compacted copy built to make room for it;
        // the copy does not make room, the write fails, and the lookup cache kept the whole copy alive (40 KB here, a
        // second table at real size) until the next Begin, which a source stopped by the failure may never call.
        var schema = Schema(fields: new[] { new LocalTableField("t", ColumnType.Text), new LocalTableField("s", ColumnType.String) });
        var options = new LocalFeatureTableOptions { PageSize = 4096, MaxBytes = 46448, CompactionMinBytes = long.MaxValue };
        var table = new LocalFeatureTable(schema, options);
        FillTextAndCategory(table.Writer);
        var before = table.Pin();
        Assert.Equal(46448, before.Bytes);
        Assert.True(before.SupersededBytes > 0);

        var copy = FailWriteThatNeedsACompaction(table.Writer);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(copy.IsAlive, "the writer keeps the compacted copy of a rejected write alive");
        Assert.Same(before, table.Pin());
        Assert.Equal((46448L, before.Generation, 0L), (table.Pin().Bytes, table.Pin().Generation, table.Compactions));
        Assert.False(table.Pin().Find(999L).Found);
    }

    [Fact]
    public void ExplicitCompaction_ThatWouldNotFit_IsRejected_AndTheTableIsUnchanged()
    {
        // Codex review 11 (docs): a compacted copy can be larger than the table, so the guard that rejects it is reachable.
        // The table: key 1's empty text in a small first page, key 2's 80 KB of text in a page of its own. The copy opens
        // its first page for all the bytes still to copy (a whole 64 KiB page), puts key 1 in it, and key 2 does not fit:
        // it takes a second page, and the first stays almost empty.
        var schema = Schema(fields: new[] { new LocalTableField("t", ColumnType.Text) });
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions { PageSize = 65536, MaxBytes = 84632, CompactionMinBytes = long.MaxValue });
        WriteText(table.Writer, 1, 1, 0);
        WriteText(table.Writer, 2, 1, 40_000);
        var before = table.Pin();
        Assert.Equal(84632, before.Bytes);

        var full = Assert.Throws<LocalTableFullException>(() => table.Writer.Compact());

        Assert.Equal((84632L, 146072L), (full.MaxBytes, full.RequiredBytes));
        Assert.Same(before, table.Pin());
        Assert.Equal((84632L, before.Generation, 0L), (table.Pin().Bytes, table.Pin().Generation, table.Compactions));
        Assert.Equal(new string('t', 40_000), before.Read(0, before.Find(2L)).AsString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MaxBytes_HoldsAfterEveryOperation_AndAFailedOneChangesNothing(int seed)
    {
        // Random updates of 40 keys near the limit (which the budget path's compactions keep the table under), automatic
        // compactions (small CompactionMinBytes), explicit ones, and now and then a record too large for the limit, which
        // tries a compaction and must fail without a trace. A failure from the table being full is fine too; whatever
        // happens, the table never exceeds the limit and a failed operation changes nothing.
        var schema = Schema(fields: new[]
        {
            new LocalTableField("t", ColumnType.Text),
            new LocalTableField("s", ColumnType.String),
            new LocalTableField("li32", ColumnType.ListInt32),
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

    private static void WriteText(IFeatureTableWriter writer, long key, long version, int chars)
    {
        var builder = writer.Begin(key, version);
        builder.SetString(0, new string('t', chars));
        Assert.True(builder.Commit());
    }

    /// <summary>
    /// <paramref name="last"/> reuses a string only a superseded record of <paramref name="setup"/> holds. Below what it
    /// needs both without a compaction and after one, the write compacts, finds that the copy (which dropped the string,
    /// so the write adds it again) does not make room, and fails with the table unchanged. A writer that kept the string's
    /// charge from before the compaction would take the copy and end above the limit.
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

        // With room for the write as it is, it takes exactly that.
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

    /// <summary>20 records of 1,000 chars of text sharing one category, then key 1 again: one superseded record.</summary>
    private static void FillTextAndCategory(IFeatureTableWriter writer)
    {
        for (var key = 1; key <= 20; key++)
        {
            WriteTextAndCategory(writer, key, 1, 1_000);
        }

        WriteTextAndCategory(writer, 1, 2, 7);
    }

    private static void WriteTextAndCategory(IFeatureTableWriter writer, long key, long version, int chars)
    {
        var builder = writer.Begin(key, version);
        builder.SetString(writer.Schema.FieldIndex("t"), new string('t', chars));
        builder.SetString(writer.Schema.FieldIndex("s"), "category");
        builder.Commit();
    }

    /// <summary>A write of a million chars that fails after a compaction was tried; a weak reference to the copy it built.
    /// Not inlined, so no local of this frame keeps the copy alive in the caller.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FailWriteThatNeedsACompaction(LocalTableWriter writer)
    {
        WeakReference? copy = null;
        writer.CompactedCopyBuilt = version => copy = new WeakReference(version);
        try
        {
            Assert.Throws<LocalTableFullException>(() => WriteTextAndCategory(writer, 999, 1, 1_000_000));
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
