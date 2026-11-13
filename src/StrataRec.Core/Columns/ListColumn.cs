using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// Variable-length list column (<c>list&lt;int32&gt;</c> as <c>ListColumn&lt;int&gt;</c>, <c>list&lt;int64&gt;</c>,
/// <c>list&lt;string&gt;</c>).
/// </summary>
/// <remarks>
/// Each row stores <c>(start, length)</c> into an append-only arena. Unlike strict CSR offsets, rows may be written in
/// any order (backfill writes rows in arbitrary physical order), and zero-initialized rows are valid empty lists.
/// </remarks>
public sealed class ListColumn<T> : ColumnBuffer
{
    private int[] _starts;
    private int[] _lengths;
    private T[] _arena;
    private int _arenaUsed;

    public ListColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        _starts = memory.RentCleared<int>(capacity);
        _lengths = memory.RentCleared<int>(capacity);
        _arena = memory.Rent<T>(Math.Max(16, capacity * 4));
    }

    public ReadOnlySpan<T> Get(int row) => _arena.AsSpan(_starts[row], _lengths[row]);

    /// <summary>Number of elements in the row's list (<c>size()</c> without touching the arena).</summary>
    public int LengthOf(int row) => _lengths[row];

    public void Set(int row, ReadOnlySpan<T> value)
    {
        EnsureArena(value.Length);
        value.CopyTo(_arena.AsSpan(_arenaUsed));
        _starts[row] = _arenaUsed;
        _lengths[row] = value.Length;
        _arenaUsed += value.Length;
        SetValid(row);
    }

    /// <summary>
    /// Makes the row's value a fresh extent of <paramref name="length"/> elements, marks the row present and returns the
    /// extent to fill. Lets a writer convert while copying (an int64 list into <c>list&lt;int32&gt;</c> storage and back)
    /// without a temporary array. The caller must write every element: the arena is rented and holds stale data.
    /// </summary>
    /// <remarks>A span read from this column before the call stays valid: a growing arena is copied into a new
    /// array and the old one stays rented until the request ends, so in-place updates may read their source while
    /// filling the result.</remarks>
    public Span<T> SetUninitialized(int row, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        EnsureArena(length);
        var extent = _arena.AsSpan(_arenaUsed, length);
        _starts[row] = _arenaUsed;
        _lengths[row] = length;
        _arenaUsed += length;
        SetValid(row);
        return extent;
    }

    public override void CopyFrom(ColumnBuffer source, int sourceRow, int row)
    {
        var s = (ListColumn<T>)source;
        var valid = s.IsValid(sourceRow);
        Set(row, s.Get(sourceRow));
        SetValidity(row, valid);
    }

    protected override void GatherValues(ReadOnlySpan<int> rows, int newCapacity)
    {
        var starts = Memory.RentCleared<int>(newCapacity);
        var lengths = Memory.RentCleared<int>(newCapacity);
        for (var i = 0; i < rows.Length; i++)
        {
            starts[i] = _starts[rows[i]];
            lengths[i] = _lengths[rows[i]];
        }

        // The arena is shared: gathered rows keep pointing at their existing extents.
        _starts = starts;
        _lengths = lengths;
    }

    private void EnsureArena(int extra)
    {
        if (_arenaUsed + extra <= _arena.Length)
        {
            return;
        }

        var next = Memory.Rent<T>(Math.Max(_arena.Length * 2, _arenaUsed + extra));
        _arena.AsSpan(0, _arenaUsed).CopyTo(next);
        _arena = next;
    }
}
