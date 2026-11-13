using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Tables;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>
/// Deterministic ranking of rows by a numeric column (design 5.7): higher scores first (or lower when ascending),
/// missing and NaN last, ties broken by <c>entity_id</c> ascending.
/// </summary>
internal static class RowRanking
{
    /// <summary>Reads a numeric column as double; returns false when the row is missing or NaN.</summary>
    public static bool TryGetScore(ColumnBuffer column, int row, out double value)
    {
        value = 0;
        if (!column.IsValid(row))
        {
            return false;
        }

        value = column switch
        {
            ScalarColumn<double> d => d[row],
            ScalarColumn<float> f => f[row],
            ScalarColumn<long> l => l[row],
            ScalarColumn<int> i => i[row],
            _ => throw new InvalidOperationException($"Column of type {column.Type} is not numeric."),
        };
        return !double.IsNaN(value);
    }

    /// <summary>
    /// Orders <paramref name="rows"/> in place so that its first <paramref name="k"/> entries are the best k rows in
    /// rank order. Entries after k are in unspecified order. Runs in O(n + k log k) expected time.
    /// </summary>
    public static void SelectTop(Span<int> rows, ColumnBuffer? score, CandidateTable table, int k, bool ascending, Core.Memory.RequestMemory memory)
    {
        var n = rows.Length;
        k = Math.Clamp(k, 0, n);
        if (n <= 1 || k == 0)
        {
            return;
        }

        var entries = memory.Rent<Entry>(n);
        var intIds = table.KeyType == EntityKeyType.Int64 ? table.Int64Ids : null;
        for (var i = 0; i < n; i++)
        {
            var row = rows[i];
            var v = 0.0;
            var present = score is not null && TryGetScore(score, row, out v);
            entries[i] = new Entry(present ? (ascending ? v : -v) : 0, intIds is null ? 0 : intIds[row], row, present ? (byte)0 : (byte)1);
        }

        var comparer = new EntryComparer(intIds is null ? table.StringIds : null);
        var span = entries.AsSpan(0, n);
        if (k < n)
        {
            Select(span, k, comparer);
            span[..k].Sort(comparer);
        }
        else
        {
            span.Sort(comparer);
        }

        for (var i = 0; i < n; i++)
        {
            rows[i] = span[i].Row;
        }
    }

    /// <summary>Quickselect: afterwards the k smallest entries (by comparer) occupy [0, k).</summary>
    private static void Select(Span<Entry> span, int k, EntryComparer comparer)
    {
        var lo = 0;
        var hi = span.Length - 1;
        while (hi > lo)
        {
            var mid = lo + ((hi - lo) / 2);
            // Median of three as pivot.
            if (comparer.Compare(span[mid], span[lo]) < 0)
            {
                (span[mid], span[lo]) = (span[lo], span[mid]);
            }

            if (comparer.Compare(span[hi], span[lo]) < 0)
            {
                (span[hi], span[lo]) = (span[lo], span[hi]);
            }

            if (comparer.Compare(span[hi], span[mid]) < 0)
            {
                (span[hi], span[mid]) = (span[mid], span[hi]);
            }

            var pivot = span[mid];
            var i = lo;
            var j = hi;
            while (i <= j)
            {
                while (comparer.Compare(span[i], pivot) < 0)
                {
                    i++;
                }

                while (comparer.Compare(span[j], pivot) > 0)
                {
                    j--;
                }

                if (i <= j)
                {
                    (span[i], span[j]) = (span[j], span[i]);
                    i++;
                    j--;
                }
            }

            if (k - 1 <= j)
            {
                hi = j;
            }
            else if (k - 1 >= i)
            {
                lo = i;
            }
            else
            {
                return;
            }
        }
    }

    internal readonly record struct Entry(double Key, long Id, int Row, byte Missing);

    private readonly struct EntryComparer(DictStringColumn? stringIds) : IComparer<Entry>
    {
        public int Compare(Entry x, Entry y)
        {
            if (x.Missing != y.Missing)
            {
                return x.Missing - y.Missing;
            }

            if (x.Missing == 0)
            {
                var c = x.Key.CompareTo(y.Key);
                if (c != 0)
                {
                    return c;
                }
            }

            return stringIds is null
                ? x.Id.CompareTo(y.Id)
                : string.CompareOrdinal(stringIds.Get(x.Row), stringIds.Get(y.Row));
        }
    }
}
