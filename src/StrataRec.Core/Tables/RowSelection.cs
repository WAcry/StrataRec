using StrataRec.Core.Columns;
using StrataRec.Core.Memory;

namespace StrataRec.Core.Tables;

/// <summary>
/// The live rows of a <see cref="CandidateTable"/> and their current ranking (design 4.5).
/// </summary>
/// <remarks>
/// Invariants: physical rows are only appended; dropped rows never come back; ordering never moves column data.
/// </remarks>
public sealed class RowSelection
{
    private readonly int _capacity;
    private int[] _order;
    private int[] _scratch;
    private ulong[] _alive;
    private ulong[] _seen;

    internal RowSelection(int capacity, RequestMemory memory)
    {
        _capacity = capacity;
        _order = memory.Rent<int>(Math.Max(1, capacity));
        _scratch = memory.Rent<int>(Math.Max(1, capacity));
        _alive = memory.RentCleared<ulong>(Bits.WordCount(capacity));
        _seen = memory.Rent<ulong>(Bits.WordCount(capacity));
    }

    /// <summary>Number of live rows.</summary>
    public int Count { get; private set; }

    /// <summary>Live physical rows in rank order (rank 0 first).</summary>
    public ReadOnlySpan<int> Order => _order.AsSpan(0, Count);

    /// <summary>Liveness bitmap indexed by physical row.</summary>
    public ReadOnlySpan<ulong> AliveBits => _alive.AsSpan(0, Bits.WordCount(_capacity));

    public bool IsAlive(int physicalRow) => Bits.Get(_alive, physicalRow);

    /// <summary>Appends newly created physical rows at the end of the ranking.</summary>
    internal void AppendRange(int firstPhysicalRow, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var row = firstPhysicalRow + i;
            _order[Count + i] = row;
            Bits.Set(_alive, row);
        }

        Count += count;
    }

    /// <summary>
    /// Replaces the ranking with <paramref name="newOrder"/>, which must contain distinct live rows. Live rows that are
    /// not listed are dropped. When <paramref name="allowDrop"/> is false, <paramref name="newOrder"/> must be a
    /// permutation of the current live rows.
    /// </summary>
    public void Replace(ReadOnlySpan<int> newOrder, bool allowDrop)
    {
        if (newOrder.Length > Count || (!allowDrop && newOrder.Length != Count))
        {
            throw new InvalidOperationException(
                allowDrop
                    ? $"New order has {newOrder.Length} rows but only {Count} are live."
                    : $"A pure reorder must keep all {Count} live rows, got {newOrder.Length}.");
        }

        // Validate: every row is live and listed once. The scratch bitmap becomes the new liveness bitmap.
        var words = Bits.WordCount(_capacity);
        var seen = _seen;
        seen.AsSpan(0, words).Clear();
        foreach (var row in newOrder)
        {
            if ((uint)row >= (uint)_capacity || !Bits.Get(_alive, row) || Bits.Get(seen, row))
            {
                throw new InvalidOperationException($"Row {row} is not live or appears more than once in the new order.");
            }

            Bits.Set(seen, row);
        }

        newOrder.CopyTo(_scratch);
        (_order, _scratch) = (_scratch, _order);
        Count = newOrder.Length;
        (_alive, _seen) = (seen, _alive);
    }

    /// <summary>Keeps the rows whose rank position is flagged in <paramref name="keepByRank"/>; relative order is preserved.</summary>
    public void Retain(ReadOnlySpan<bool> keepByRank)
    {
        if (keepByRank.Length != Count)
        {
            throw new ArgumentException($"Expected {Count} flags, got {keepByRank.Length}.", nameof(keepByRank));
        }

        var n = 0;
        for (var i = 0; i < Count; i++)
        {
            var row = _order[i];
            if (keepByRank[i])
            {
                _order[n++] = row;
            }
            else
            {
                Bits.Clear(_alive, row);
            }
        }

        Count = n;
    }

    /// <summary>Keeps only the first <paramref name="count"/> rows of the ranking.</summary>
    public void Truncate(int count)
    {
        if (count >= Count)
        {
            return;
        }

        for (var i = Math.Max(0, count); i < Count; i++)
        {
            Bits.Clear(_alive, _order[i]);
        }

        Count = Math.Max(0, count);
    }

    /// <summary>
    /// Copies the current ranking into <paramref name="buffer"/> (length ≥ <see cref="Count"/>) and returns the count.
    /// With <see cref="Restore"/>, lets the engine undo the row effects of a node that fails (design 4.6).
    /// </summary>
    public int Capture(Span<int> buffer)
    {
        Order.CopyTo(buffer);
        return Count;
    }

    /// <summary>Restores a ranking captured by <see cref="Capture"/> with no Compact in between.</summary>
    public void Restore(ReadOnlySpan<int> captured)
    {
        foreach (var row in Order)
        {
            Bits.Clear(_alive, row);
        }

        captured.CopyTo(_order);
        Count = captured.Length;
        foreach (var row in captured)
        {
            Bits.Set(_alive, row);
        }
    }

    /// <summary>After Compact: live rows are physical rows 0..count-1 in rank order.</summary>
    internal void ResetIdentity(int count)
    {
        var words = Bits.WordCount(_capacity);
        _alive.AsSpan(0, words).Clear();
        for (var i = 0; i < count; i++)
        {
            _order[i] = i;
        }

        Bits.SetRange(_alive, 0, count);
        Count = count;
    }
}
