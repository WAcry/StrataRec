using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// String values a connector writes as characters or UTF-8 (audit B5): a <c>string</c> field keeps one string per distinct
/// value, a <c>text</c> value is its own string.
/// </summary>
public sealed class StagingStringTests
{
    private static readonly CompiledFieldBinding[] Fields =
    [
        new("category", 0, ColumnType.String),
        new("title", 1, ColumnType.Text),
    ];

    [Fact]
    public void AStringFieldWrittenAsCharacters_KeepsOneStringPerDistinctValue()
    {
        var staging = new StagingBuffer(Fields, 100, new RequestMemory());
        for (var key = 0; key < 100; key++)
        {
            staging.SetString(0, key, (key % 2 == 0 ? "music" : "sports").AsSpan());
            staging.SetString(1, key, "same title".AsSpan());
        }

        var category = (TextColumn)staging.Field(0);
        var title = (TextColumn)staging.Field(1);
        Assert.Equal("music", category.Get(0));
        Assert.Equal("sports", category.Get(1));
        Assert.Same(category.Get(0), category.Get(98));
        Assert.Same(category.Get(1), category.Get(99));
        Assert.Equal("same title", title.Get(0));
        Assert.NotSame(title.Get(0), title.Get(1)); // text is high-cardinality: no table for it
        Assert.True(staging.HasValue(0, 99) && staging.HasValue(1, 99));
    }

    [Fact]
    public void Utf8_IsDecoded_InvalidBytesAsReplacementCharacters_AndLongValuesAsWell()
    {
        var staging = new StagingBuffer(Fields, 20, new RequestMemory());
        staging.SetStringUtf8(0, 0, "café"u8);
        staging.SetStringUtf8(0, 1, [0x61, 0xFF, 0x62]);
        var longText = new string('x', 1000) + "é";
        staging.SetStringUtf8(1, 0, Encoding.UTF8.GetBytes(longText));
        staging.SetStringUtf8(0, 2, "café"u8);

        var category = (TextColumn)staging.Field(0);
        Assert.Equal("café", category.Get(0));
        Assert.Equal("a�b", category.Get(1));
        Assert.Same(category.Get(0), category.Get(2));
        Assert.Equal(longText, ((TextColumn)staging.Field(1)).Get(0));
    }

    [Fact]
    public void ACallOfFewKeys_DoesNotBuildATable_AndTheTableStopsGrowingAtItsCap()
    {
        var single = new StagingBuffer(Fields, 1, new RequestMemory()); // a context fetch: one key
        single.SetString(0, 0, "music".AsSpan());
        Assert.Equal("music", ((TextColumn)single.Field(0)).Get(0));

        var keys = StagingBuffer.MaxInterned + 10;
        var staging = new StagingBuffer(Fields, keys, new RequestMemory());
        for (var key = 0; key < keys; key++)
        {
            staging.SetString(0, key, ("v" + key).AsSpan());
        }

        staging.SetString(0, 0, "v1".AsSpan()); // interned before the cap
        staging.SetString(0, keys - 1, ("v" + (keys - 5)).AsSpan()); // past the cap: a string of its own, the same text
        var category = (TextColumn)staging.Field(0);
        Assert.Same(category.Get(1), category.Get(0));
        Assert.Equal("v" + (keys - 5), category.Get(keys - 1));
        Assert.NotSame(category.Get(keys - 5), category.Get(keys - 1));
    }

    [Fact]
    public void RepeatedCategories_AllocateAboutTheirDistinctValues_NotAStringPerKey()
    {
        // Measured on this thread, which runs the code under test alone; the first round warms the pools.
        byte[][] values = ["music"u8.ToArray(), "sports"u8.ToArray(), "news"u8.ToArray()];
        long Written(int keys)
        {
            var memory = new RequestMemory();
            var staging = new StagingBuffer(Fields, keys, memory);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var key = 0; key < keys; key++)
            {
                staging.SetStringUtf8(0, key, values[key % values.Length]);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            memory.Release();
            return allocated;
        }

        Written(5000);
        var allocated = Written(5000);

        Assert.True(allocated < 2048, $"{allocated} B for 5,000 keys of 3 categories (a string per key is about 160 KB)");
    }
}
