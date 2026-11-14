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
    [InlineData(6, int.MaxValue, 1)] // (items + size - 1) / size overflowed to 0 here (review 27)
    [InlineData(int.MaxValue, int.MaxValue, 1)]
    [InlineData(int.MaxValue, 1, int.MaxValue)]
    [InlineData(int.MaxValue, 2, 1073741824)]
    public void Count_IsTheCeilingOfItemsOverSize_WithoutOverflow(int items, int size, int expected) =>
        Assert.Equal(expected, Batching.Count(items, size));

    [Fact]
    public void Count_RejectsANegativeCountAndASizeBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Batching.Count(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Batching.Count(1, 0));
    }
}
