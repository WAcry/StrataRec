using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// Strings a connector writes (design 6.5): <see cref="IFeatureBatchWriter.SetString"/> keeps the connector's instance, and
/// <see cref="IFeatureBatchWriter.Intern"/> / <see cref="IFeatureBatchWriter.InternUtf8"/> give one instance per distinct
/// value within the call, so a field whose values repeat across keys costs a string per value, not per key.
/// </summary>
public sealed class StagingStringTests
{
    private static readonly CompiledFieldBinding[] Fields =
    [
        new("category", 0, ColumnType.String),
        new("title", 1, ColumnType.String),
        new("tags", 2, ColumnType.List(StorageKind.String)),
    ];

    [Fact]
    public void Intern_GivesOneInstancePerDistinctValue_AcrossTheKeysOfACall()
    {
        var staging = new StagingBuffer(Fields, 100, new RequestMemory());
        for (var key = 0; key < 100; key++)
        {
            staging.SetString(0, key, staging.Intern((key % 2 == 0 ? "music" : "sports").AsSpan()));
            staging.SetString(1, key, "title " + key); // distinct per key: a string of its own, not interned
            staging.SetStringList(2, key, [staging.InternUtf8("red"u8), staging.InternUtf8(key % 2 == 0 ? "shoe"u8 : "hat"u8)]);
        }

        var category = (StringColumn)staging.Field(0);
        var tags = (ListColumn<string>)staging.Field(2);
        Assert.Equal("music", category.Get(0));
        Assert.Equal("sports", category.Get(1));
        Assert.Same(category.Get(0), category.Get(98));
        Assert.Same(category.Get(1), category.Get(99));
        Assert.Equal(["red", "hat"], tags.Get(99).ToArray());
        Assert.Same(tags.Get(0)[0], tags.Get(99)[0]);
        Assert.Same(tags.Get(0)[1], tags.Get(98)[1]);
        Assert.Equal("title 7", staging.Get(1, 7).AsString());
        Assert.True(staging.HasValue(0, 99) && staging.HasValue(1, 99) && staging.HasValue(2, 99));
    }

    [Fact]
    public void Intern_IsScopedToTheWriter_NotTheProcess()
    {
        var first = new StagingBuffer(Fields, 4, new RequestMemory());
        var second = new StagingBuffer(Fields, 4, new RequestMemory());

        var a = first.Intern("music".AsSpan());
        var b = second.InternUtf8("music"u8);

        Assert.Equal(a, b);
        Assert.NotSame(a, b); // another call's writer: another table, and nothing kept beyond it
        Assert.Same(a, first.InternUtf8("music"u8));
    }

    [Fact]
    public void Intern_StartsOverForEachCall_WhenACompletedCallsWriterIsBoundToTheNextChunk()
    {
        // Review 75: a writer reused for the next call after a call completed synchronously kept the first call's
        // values, so a first call of 4,096 distinct values left the next call no room: each of its repeated values was
        // a string of its own.
        var memory = new RequestMemory();
        var staging = new StagingBuffer(new StagingArea(Fields, 8192, 4096, memory));
        staging.Bind(0);
        string? seven = null;
        for (var key = 0; key < 4096; key++)
        {
            var value = staging.Intern(("unique-" + key).AsSpan());
            seven ??= key == 7 ? value : null;
            staging.SetString(0, key, value);
        }

        Assert.Same(seven, staging.Intern("unique-7".AsSpan()));
        staging.Bind(1);
        var first = staging.Intern("new-category".AsSpan());
        Assert.Same(first, staging.InternUtf8("new-category"u8));
        Assert.NotSame(seven, staging.Intern("unique-7".AsSpan())); // the first call's values are gone with it
        memory.Release();
    }

    [Fact]
    public void SetString_KeepsTheConnectorsInstance_AndRefusesNullAndAnotherFieldType()
    {
        var staging = new StagingBuffer(Fields, 4, new RequestMemory());
        var decodedAtLoad = new string("a title".AsSpan());

        staging.SetString(1, 0, decodedAtLoad);

        Assert.Same(decodedAtLoad, ((StringColumn)staging.Field(1)).Get(0));
        Assert.Throws<ArgumentNullException>(() => staging.SetString(0, 1, null!));
        var mismatch = Assert.Throws<InvalidOperationException>(() => staging.SetString(2, 1, "x"));
        Assert.Contains("list<string>", mismatch.Message, StringComparison.Ordinal);
        Assert.False(staging.HasValue(0, 1));
        Assert.False(staging.HasValue(2, 1));
    }

    [Fact]
    public void UTF8_IsDecodedAsEncodingUtf8Does_InvalidBytesAsReplacementCharacters_AndLongValuesToo()
    {
        var staging = new StagingBuffer(Fields, 4, new RequestMemory());
        var longValue = new string('x', 1000) + "é";

        staging.SetString(0, 0, staging.InternUtf8("café"u8));
        staging.SetString(0, 1, staging.InternUtf8([0x61, 0xFF, 0x62]));
        staging.SetString(1, 0, staging.InternUtf8(Encoding.UTF8.GetBytes(longValue)));
        staging.SetString(0, 2, staging.InternUtf8("café"u8));

        var category = (StringColumn)staging.Field(0);
        Assert.Equal("café", category.Get(0));
        Assert.Equal("a�b", category.Get(1));
        Assert.Same(category.Get(0), category.Get(2));
        Assert.Equal(longValue, ((StringColumn)staging.Field(1)).Get(0));
    }

    /// <summary>
    /// A connector writing a 3-value category and two tags of five per key through Intern: 50,000 keys allocate what 5,000
    /// do, so nothing is allocated per key (a string per key would be about 1.6 MB more). The difference, not an absolute
    /// count, is checked: the constant part (the writer's table, the tags' list buffer) is not what this test is about.
    /// </summary>
    [Fact]
    public void ACategoryAndTags_ThroughIntern_AllocateNothingPerKey()
    {
        // Measured on this thread, which runs the code under test alone; a first round warms the pools at each size.
        byte[][] categories = ["music"u8.ToArray(), "sports"u8.ToArray(), "news"u8.ToArray()];
        byte[][] tagValues = ["red"u8.ToArray(), "blue"u8.ToArray(), "shoe"u8.ToArray(), "hat"u8.ToArray(), "sale"u8.ToArray()];
        var pair = new string[2];
        long Written(int keys)
        {
            var memory = new RequestMemory();
            var staging = new StagingBuffer(Fields, keys, memory);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var key = 0; key < keys; key++)
            {
                staging.SetString(0, key, staging.InternUtf8(categories[key % categories.Length]));
                pair[0] = staging.InternUtf8(tagValues[key % tagValues.Length]);
                pair[1] = staging.InternUtf8(tagValues[(key / 7) % tagValues.Length]);
                staging.SetStringList(2, key, pair);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal("sports", staging.Get(0, keys - 1).AsString()); // 4,999 and 49,999: 1 mod 3
            memory.Release();
            return allocated;
        }

        // The best of three rounds: a pool rent can miss and allocate when tests running in parallel hold the shared
        // pool's arrays (a once-off 6.5 KB at 50,000 keys in review 75), which is not a cost of the keys.
        Written(5_000);
        Written(50_000);
        long few = long.MaxValue, many = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            few = Math.Min(few, Written(5_000));
            many = Math.Min(many, Written(50_000));
        }

        Assert.True(many - few < 1024, $"5,000 keys: {few} B; 50,000 keys: {many} B");
    }
}
