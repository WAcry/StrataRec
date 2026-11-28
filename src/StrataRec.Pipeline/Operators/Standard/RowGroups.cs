using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>
/// Rows grouped by a key column (int64, int32 or string), for the operators that work per group
/// (<c>GroupAggregate@1</c>, <c>SlidingWindowQuota@1</c>): dense group ids, so per-group state is an array indexed by
/// group, rented from the request's memory, and nothing is allocated per row.
/// </summary>
internal static class RowGroups
{
    /// <summary>
    /// The group of each row of <paramref name="rows"/> into <paramref name="groupOf"/> (-1 for a row whose key is
    /// missing, or every row when <paramref name="keys"/> is null), and the number of groups. Strings are numbered in
    /// the order they first appear, (equal strings group together whether or not they are the same instance).
    /// </summary>
    public static int Assign(ColumnBuffer? keys, ReadOnlySpan<int> rows, Span<int> groupOf, RequestMemory memory)
    {
        var n = rows.Length;
        if (keys is StringColumn strings)
        {
            var seen = new EntityKeyIndex(EntityKeyType.String, n, memory, scratch: true);
            for (var i = 0; i < n; i++)
            {
                var row = rows[i];
                groupOf[i] = strings.IsValid(row) ? seen.GetOrAdd(strings.Get(row), seen.Count, out _) : -1;
            }

            var distinct = seen.Count;
            seen.ReturnArrays();
            return distinct;
        }

        var sorted = memory.RentScratch<long>(Math.Max(1, n));
        var index = memory.RentScratch<int>(Math.Max(1, n));
        var present = 0;
        for (var i = 0; i < n; i++)
        {
            var row = rows[i];
            groupOf[i] = -1;
            if (keys is not null && keys.IsValid(row))
            {
                sorted[present] = keys is ScalarColumn<long> l ? l[row] : ((ScalarColumn<int>)keys)[row];
                index[present++] = i;
            }
        }

        Array.Sort(sorted, index, 0, present);
        var groups = 0;
        for (var j = 0; j < present; j++)
        {
            if (j == 0 || sorted[j] != sorted[j - 1])
            {
                groups++;
            }

            groupOf[index[j]] = groups - 1;
        }

        memory.ReturnScratch(sorted);
        memory.ReturnScratch(index);
        return groups;
    }
}
