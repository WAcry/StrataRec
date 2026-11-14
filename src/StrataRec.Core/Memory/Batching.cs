namespace StrataRec.Core.Memory;

/// <summary>
/// The one place that splits work into batches (model calls, feature fetch chunks): how many batches of at most a given
/// size cover a number of items.
/// </summary>
/// <remarks>
/// A configured size may be as large as <see cref="int.MaxValue"/>, which means "everything in one batch". The usual
/// <c>(items + size - 1) / size</c> overflows there and gave no batch at all, so nothing was sent and the work was read as
/// done (review 27); <see cref="Count"/> cannot overflow.
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
}
