using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// A <c>string</c> column: one string reference per row, in an array rented from the request.
/// </summary>
/// <remarks>
/// <para>Plain references rather than dictionary codes. A dictionary per column and request was allocated (a map and a
/// list, from hundreds of bytes for a category to hundreds of kilobytes for a high-cardinality column) and saved nothing:
/// a value is a string before it is looked up, so the strings exist either way, and where they come from decides how many
/// there are. A local table hands out its own dictionary's instances, and a connector deduplicates what it decodes (see
/// <c>IFeatureBatchWriter.Intern</c>), so a category repeated across rows is one shared instance whatever the column does.
/// What codes bought was comparing integers instead of strings, and <see cref="string.Equals(string, string)"/> on shared
/// instances stops at the reference check.</para>
/// <para>A row never written holds null, read as the empty string: the array is rented, so every row is structurally
/// readable, and validity says whether the row has a value.</para>
/// </remarks>
public sealed class StringColumn : ColumnBuffer
{
    private string?[] _values;

    public StringColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        _values = memory.RentCleared<string?>(capacity);
    }

    /// <summary>The rented array, indexed by physical row (at least <see cref="ColumnBuffer.Capacity"/> long), for loops
    /// that write many rows; a writer marks the rows it wrote valid. Replaced by <see cref="ColumnBuffer.Gather"/>.</summary>
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
