using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// A column of fixed-size native values in one contiguous array (design 4.4). Used for bool, int32, int64,
/// float32 and float64.
/// </summary>
public sealed class ScalarColumn<T> : ColumnBuffer
    where T : unmanaged
{
    private T[] _values;

    public ScalarColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        _values = memory.Rent<T>(capacity);
    }

    /// <summary>The value array; indices are physical rows. Length may exceed <see cref="ColumnBuffer.Capacity"/>.</summary>
    public T[] Values => _values;

    public Span<T> Span => _values.AsSpan(0, Capacity);

    public T this[int row]
    {
        get => _values[row];
        set => _values[row] = value;
    }

    /// <summary>Writes a present value.</summary>
    public void Set(int row, T value)
    {
        _values[row] = value;
        SetValid(row);
    }

    public override void CopyFrom(ColumnBuffer source, int sourceRow, int row)
    {
        var s = (ScalarColumn<T>)source;
        _values[row] = s._values[sourceRow];
        CopyValidityFrom(source, sourceRow, row);
    }

    protected override void GatherValues(ReadOnlySpan<int> rows)
    {
        var next = Memory.Rent<T>(Capacity);
        for (var i = 0; i < rows.Length; i++)
        {
            next[i] = _values[rows[i]];
        }

        _values = next;
    }
}
