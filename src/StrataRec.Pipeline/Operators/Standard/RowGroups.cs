using StrataRec.Core.Columns;
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
    /// The group of each row of <paramref name="rows"/> into <paramref name="groupOf"/> (−1 for a row whose key is
    /// missing, or every row when <paramref name="keys"/> is null), and the number of groups. A string column's
    /// dictionary codes number its values already, in the order they first appear; integers are numbered after a sort.
    /// </summary>
    public static int Assign(ColumnBuffer? keys, ReadOnlySpan<int> rows, Span<int> groupOf, RequestMemory memory)
    {
        var n = rows.Length;
        if (keys is DictStringColumn strings)
        {
            var ids = memory.RentScratch<int>(Math.Max(1, strings.Dictionary.Count));
            ids.AsSpan(0, Math.Max(1, strings.Dictionary.Count)).Clear();
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                var row = rows[i];
                if (!strings.IsValid(row))
                {
                    groupOf[i] = -1;
                    continue;
                }

                ref var id = ref ids[strings.Codes[row]];
                if (id == 0)
                {
                    id = ++count;
                }

                groupOf[i] = id - 1;
            }

            memory.ReturnScratch(ids);
            return count;
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
