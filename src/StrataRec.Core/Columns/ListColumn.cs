using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// Variable-length list column (<c>list&lt;int64&gt;</c>, <c>list&lt;string&gt;</c>).
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

    public void Set(int row, ReadOnlySpan<T> value)
    {
        EnsureArena(value.Length);
        value.CopyTo(_arena.AsSpan(_arenaUsed));
        _starts[row] = _arenaUsed;
        _lengths[row] = value.Length;
        _arenaUsed += value.Length;
        SetValid(row);
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
