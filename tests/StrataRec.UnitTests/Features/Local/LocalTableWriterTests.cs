using StrataRec.Core.Keys;
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

        // Without a limit, compacting before the last write gives the smallest table that holds the result.
        var reference = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { PageSize = 4096 });
        Setup(reference.Writer);
        reference.Writer.Compact();
        Last(reference.Writer);
        var needed = reference.Pin().Bytes;

        Assert.Equal(needed, BytesAfter(needed, w => { Setup(w); Last(w); }));

        // One byte less: the write compacts, still does not fit (first is new again), and fails with the content unchanged.
        var table = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = needed - 1, PageSize = 4096 });
        Setup(table.Writer);
        Assert.Throws<LocalTableFullException>(() => Last(table.Writer));
        var version = table.Pin();
        Assert.True(version.Bytes <= needed - 1);
        Assert.False(version.Find(2L).Found);
        Assert.Equal("b", version.Read(table.Schema.FieldIndex("s"), version.Find(1L)).AsString());
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
        // also grows the index and the table before it is smaller than the limit.
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

        void Last(IFeatureTableWriter writer) => WriteString(writer, 9, 1, first);

        var reference = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { PageSize = 4096 });
        Setup(reference.Writer);
        reference.Writer.Compact();
        Last(reference.Writer);
        var needed = reference.Pin().Bytes;
        Assert.Equal(needed, BytesAfter(needed, w => { Setup(w); Last(w); }));

        var table = new LocalFeatureTable(Schema(), new LocalFeatureTableOptions { MaxBytes = needed - 1, PageSize = 4096 });
        Setup(table.Writer);
        Assert.Throws<LocalTableFullException>(() => Last(table.Writer));
        Assert.True(table.Pin().Bytes <= needed - 1);
        Assert.False(table.Pin().Find(9L).Found);
        Assert.Equal(8, table.Pin().RecordCount);
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
