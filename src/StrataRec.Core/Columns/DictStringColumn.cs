using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// Dictionary-encoded string column for low-cardinality values such as categories and sources (design 4.4).
/// Comparisons against constants translate the constant to a code once and compare integers in the loop.
/// </summary>
/// <remarks>Code 0 is always the empty string, so zero-initialized codes are structurally valid.</remarks>
public sealed class DictStringColumn : ColumnBuffer
{
    private readonly List<string> _dictionary = new() { string.Empty };
    private readonly Dictionary<string, int> _codesByValue = new(StringComparer.Ordinal) { [string.Empty] = 0 };
    private int[] _codes;

    public DictStringColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        _codes = memory.RentCleared<int>(capacity);
    }

    public int[] Codes => _codes;

    public IReadOnlyList<string> Dictionary => _dictionary;

    /// <summary>Returns the code of <paramref name="value"/>, or -1 when it does not occur in this column.</summary>
    public int FindCode(string value) => _codesByValue.TryGetValue(value, out var code) ? code : -1;

    public int GetOrAddCode(string value)
    {
        if (!_codesByValue.TryGetValue(value, out var code))
        {
            code = _dictionary.Count;
            _dictionary.Add(value);
            _codesByValue.Add(value, code);
        }

        return code;
    }

    /// <summary>
    /// Makes room for <paramref name="additional"/> more distinct values at once, for a writer that knows its values are
    /// mostly distinct (string entity ids): grown step by step, the dictionary allocated about three times what it keeps
    /// (583 KB for 5,000 ids, 204 KB reserved; performance audit). Not for low-cardinality columns, which need no room.
    /// </summary>
    public void ReserveDistinct(int additional)
    {
        _codesByValue.EnsureCapacity(_dictionary.Count + additional);
        _dictionary.EnsureCapacity(_dictionary.Count + additional);
    }

    public string Get(int row) => _dictionary[_codes[row]];

    public void Set(int row, string value)
    {
        _codes[row] = GetOrAddCode(value);
        SetValid(row);
    }

    public override void CopyFrom(ColumnBuffer source, int sourceRow, int row)
    {
        var s = (DictStringColumn)source;
        _codes[row] = GetOrAddCode(s.Get(sourceRow));
        CopyValidityFrom(source, sourceRow, row);
    }

    protected override void GatherValues(ReadOnlySpan<int> rows, int newCapacity)
    {
        var next = Memory.RentCleared<int>(newCapacity);
        for (var i = 0; i < rows.Length; i++)
        {
            next[i] = _codes[rows[i]];
        }

        _codes = next;
    }
}
