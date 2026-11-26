using StrataRec.Core.Memory;

namespace StrataRec.UnitTests.Core;

public sealed class BatchingTests
{
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0, int.MaxValue, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(5, 2, 3)]
    [InlineData(6, 2, 3)]
    [InlineData(6, 256, 1)]
    [InlineData(6, int.MaxValue, 1)] // (items + size - 1) / size overflowed to 0 here
    [InlineData(int.MaxValue, int.MaxValue, 1)]
    [InlineData(int.MaxValue, 1, int.MaxValue)]
    [InlineData(int.MaxValue, 2, 1073741824)]
    public void Count_IsTheCeilingOfItemsOverSize_WithoutOverflow(int items, int size, int expected) =>
        Assert.Equal(expected, Batching.Count(items, size));

    [Theory]
    [InlineData(9, 4, 3)] // 3, 3, 3 rather than 4, 4, 1
    [InlineData(1001, 1000, 501)] // 501 and 500 rather than 1,000 and 1
    [InlineData(10, 4, 4)] // 4, 4, 2: the largest batch an even split into three has
    [InlineData(5, 2, 2)]
    [InlineData(6, 256, 6)]
    [InlineData(7, 1, 1)]
    [InlineData(0, 4, 4)]
    [InlineData(6, int.MaxValue, 6)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue, 2, 2)]
    public void Size_IsTheEvenBatchOfTheFewestBatches(int items, int size, int expected) =>
        Assert.Equal(expected, Batching.Size(items, size));

    [Fact]
    public void Size_KeepsTheCount_NeverExceedsTheLimit_AndIsTheSmallestLargestBatch()
    {
        for (var items = 0; items <= 300; items++)
        {
            for (var size = 1; size <= 40; size++)
            {
                var even = Batching.Size(items, size);
                var count = Batching.Count(items, size);
                Assert.InRange(even, 1, size);
                Assert.Equal(count, Batching.Count(items, even)); // no call added
                if (count > 0)
                {
                    Assert.Equal((items + count - 1) / count, even); // no split into this many batches has a smaller largest one
                    Assert.True(items - ((count - 1) * even) is > 0 and var last && last <= even); // the last batch is not empty
                }
            }
        }
    }

    [Fact]
    public void Count_RejectsANegativeCountAndASizeBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Batching.Count(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Batching.Count(1, 0));
    }
}
