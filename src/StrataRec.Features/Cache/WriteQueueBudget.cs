namespace StrataRec.Features.Cache;

/// <remarks>
/// A batch larger than the whole limit still goes into an empty queue. A compare-and-swap, not an add rolled back: a
/// batch refused for its size never makes a concurrent one look refused too.
/// </remarks>
internal static class WriteQueueBudget
{
    public static bool TryTake(ref long queued, long bytes, long limit)
    {
        while (true)
        {
            var current = Interlocked.Read(ref queued);
            if (current > 0 && current + bytes > limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref queued, current + bytes, current) == current)
            {
                return true;
            }
        }
    }
}
