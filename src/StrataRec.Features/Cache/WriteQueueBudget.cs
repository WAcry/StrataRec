namespace StrataRec.Features.Cache;

/// <summary>
/// The byte budget of a write-back queue (the table's writer, the distributed tier's loop): the one rule both tiers take
/// a batch's bytes by, so they cannot drift apart.
/// </summary>
/// <remarks>
/// A batch larger than the whole limit still goes into an empty queue, as a log batch does (<c>LogPipeline</c>): one
/// request's misses make one batch, whatever <c>max_batch</c> split its calls into, so a request with many candidates
/// would otherwise have its write-back dropped by an idle queue on every request, and its keys fetched again every time.
/// The queue then holds that one batch over its limit and takes nothing more until it drained. A compare-and-swap, not
/// an add rolled back: a batch refused for its size never makes a concurrent one look refused too.
/// </remarks>
internal static class WriteQueueBudget
{
    /// <summary>Takes <paramref name="bytes"/> of the budget, or nothing (false) when they do not fit a queue that holds some.</summary>
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
