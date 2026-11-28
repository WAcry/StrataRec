namespace StrataRec.Core.Memory;

/// <summary>
/// The one place that splits work into batches (model calls, feature fetch chunks): how many batches of at most a given
/// size cover a number of items, and how large to make them so they are even.
/// </summary>
/// <remarks>
/// A configured size may be as large as <see cref="int.MaxValue"/>, which means "everything in one batch".
/// <see cref="Count"/> cannot overflow.
/// </remarks>
public static class Batching
{
    /// <summary>The number of batches of at most <paramref name="size"/> items that cover <paramref name="items"/>; 0 for none.</summary>
    /// <param name="items">Non-negative.</param>
    /// <param name="size">Positive.</param>
    public static int Count(int items, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(items);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        return items == 0 ? 0 : ((items - 1) / size) + 1;
    }

    /// <summary>
    /// The batch size that splits <paramref name="items"/> into the fewest batches of at most <paramref name="size"/>
    /// (<see cref="Count"/>) as evenly as batches of one size can: each batch but the last holds this many, the last the
    /// rest. 9 items at most 4 a batch go as 3, 3 and 3, not 4, 4 and 1.
    /// </summary>
    /// <remarks>
    /// <para>The count stays the fewest, so no call is added, and the size never exceeds <paramref name="size"/>: with
    /// <c>k</c> batches it is the ceiling of items / <c>k</c>, the smallest largest batch any split into <c>k</c>
    /// has.</para>
    /// </remarks>
    /// <param name="items">Non-negative.</param>
    /// <param name="size">Positive: the configured largest batch.</param>
    /// <returns>Between 1 and <paramref name="size"/>; <paramref name="size"/> when there are no items.</returns>
    public static int Size(int items, int size)
    {
        var count = Count(items, size);
        return count == 0 ? size : ((items - 1) / count) + 1;
    }
}
