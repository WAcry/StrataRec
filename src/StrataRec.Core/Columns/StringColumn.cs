using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// A <c>string</c> column: one string reference per row, in an array rented from the request.
/// </summary>
/// <remarks>
/// <para>A row never written holds null, read as the empty string, so every row is structurally readable, and validity
/// says whether the row has a value.</para>
/// </remarks>
public sealed class StringColumn : ColumnBuffer
{
    private string?[] _values;

    public StringColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        _values = memory.RentCleared<string?>(capacity);
    }

    /// <summary>The rented array, indexed by physical row (at least <see cref="ColumnBuffer.Capacity"/> long); a writer
    /// marks the rows it wrote valid. Replaced by <see cref="ColumnBuffer.Gather"/>.</summary>
    public string?[] Values => _values;

    /// <summary>Returns the string at <paramref name="row"/>; empty when the row was never written.</summary>
    public string Get(int row) => _values[row] ?? string.Empty;

    public void Set(int row, string value)
    {
        _values[row] = value;
        SetValid(row);
    }

    public override void CopyFrom(ColumnBuffer source, int sourceRow, int row)
    {
        var s = (StringColumn)source;
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
