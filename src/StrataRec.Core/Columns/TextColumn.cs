using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// Long text column (prompts, generated reasons).
/// </summary>
/// <remarks>
/// The initial implementation stores one string reference per row; the design's contiguous UTF-8 layout
/// (design 4.4) is a later optimization that does not change this API.
/// </remarks>
public sealed class TextColumn : ColumnBuffer
{
    private string?[] _values;

    public TextColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        _values = memory.RentCleared<string?>(capacity);
    }

    /// <summary>Returns the text at <paramref name="row"/>; empty when the row was never written.</summary>
    public string Get(int row) => _values[row] ?? string.Empty;

    public void Set(int row, string value)
    {
        _values[row] = value;
        SetValid(row);
    }

    public override void CopyFrom(ColumnBuffer source, int sourceRow, int row)
    {
        var s = (TextColumn)source;
        _values[row] = s._values[sourceRow];
        CopyValidityFrom(source, sourceRow, row);
    }

    protected override void GatherValues(ReadOnlySpan<int> rows, int newCapacity)
    {
        var next = Memory.RentCleared<string?>(newCapacity);
        for (var i = 0; i < rows.Length; i++)
        {
            next[i] = _values[rows[i]];
        }

        _values = next;
    }
}
