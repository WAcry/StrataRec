using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// A <c>list&lt;E&gt;</c> or <c>list&lt;E,N&gt;</c> column: the rows' extents, whatever the element type. What only needs a
/// row's length (<c>size()</c>, a template's section, a copy's budget) reads it here without knowing the element type.
/// </summary>
/// <remarks>
/// <para>Each row stores <c>(start, length)</c> into an append-only arena. Unlike strict CSR offsets, rows may be written in
/// any order (backfill writes rows in arbitrary physical order), and zero-initialized rows are valid empty lists.</para>
/// <para>A fixed-length list (an embedding) is stored the same way: a row is a contiguous span either way. The column
/// does not check lengths; the writers that apply the type's rules do (<see cref="ColumnValues.TrySet"/>), as for int32
/// narrowing. The type only sizes the first arena: N per row, so filling every row never grows it.</para>
/// </remarks>
public abstract class ListColumn : ColumnBuffer
{
    private protected int[] _starts;
    private protected int[] _lengths;

    private protected ListColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        if (!type.IsList)
        {
            throw new ArgumentException($"A list column needs a list type, got {type}.", nameof(type));
        }

        _starts = memory.RentCleared<int>(capacity);
        _lengths = memory.RentCleared<int>(capacity);
    }

    /// <summary>Number of elements in the row's list (<c>size()</c> without touching the arena).</summary>
    public int LengthOf(int row) => _lengths[row];

    /// <summary>
    /// Makes the row's list the elements of a row of <paramref name="source"/> at <paramref name="positions"/>, in that
    /// order: a list reordered, filtered or cut. Lists that go together element by element (a row's ids, their scores,
    /// their labels) stay aligned when each takes the same positions, whatever its element type.
    /// </summary>
    /// <param name="source">A list column of the same element storage; it may be this column, and the same row.</param>
    /// <param name="positions">Indexes into the source row's list; one may be taken more than once.</param>
    /// <exception cref="ArgumentException">The source stores another element type.</exception>
    /// <exception cref="IndexOutOfRangeException">A position is outside the source row's list; the row is then left as it was.</exception>
    public abstract void SetTaken(int row, ListColumn source, int sourceRow, ReadOnlySpan<int> positions);

    protected override void GatherValues(ReadOnlySpan<int> rows, int newCapacity)
    {
        var starts = Memory.RentCleared<int>(newCapacity);
        var lengths = Memory.RentCleared<int>(newCapacity);
        for (var i = 0; i < rows.Length; i++)
        {
            starts[i] = _starts[rows[i]];
            lengths[i] = _lengths[rows[i]];
        }

        _starts = starts;
        _lengths = lengths;
    }

    internal static ListColumn CreateList(ColumnType type, int capacity, RequestMemory memory) => type.Element switch
    {
        StorageKind.Bool => new ListColumn<bool>(type, capacity, memory),
        StorageKind.Int32 => new ListColumn<int>(type, capacity, memory),
        StorageKind.Int64 => new ListColumn<long>(type, capacity, memory),
        StorageKind.Float32 => new ListColumn<float>(type, capacity, memory),
        StorageKind.Float64 => new ListColumn<double>(type, capacity, memory),
        _ => new ListColumn<string>(type, capacity, memory),
    };
}

/// <summary>
/// A list column whose elements are stored as <typeparamref name="T"/>: <c>bool</c>, <c>int</c> (<c>list&lt;int32&gt;</c>),
/// <c>long</c>, <c>float</c> (<c>list&lt;float32&gt;</c>), <c>double</c> or <c>string</c>; created by
/// <see cref="ColumnBuffer.Create"/> from the type.
/// </summary>
public sealed class ListColumn<T> : ListColumn
{
    private T[] _arena;
    private int _arenaUsed;

    public ListColumn(ColumnType type, int capacity, RequestMemory memory)
        : base(type, capacity, memory)
    {
        if (!Stores(type.Element))
        {
            throw new ArgumentException($"{type} is not stored as {typeof(T).Name} elements.", nameof(type));
        }

        _arena = memory.Rent<T>(Math.Max(16, checked(capacity * (type.IsFixedLength ? type.FixedLength : 4))));
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

    /// <summary>
    /// Makes the row's value a fresh extent of <paramref name="length"/> elements, marks the row present and returns the
    /// extent to fill. Lets a writer convert while copying (int64 or float64 elements into int32 or float32 storage and
    /// back) without a temporary array. The caller must write every element: the arena is rented and holds stale data.
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

    public override void SetTaken(int row, ListColumn source, int sourceRow, ReadOnlySpan<int> positions)
    {
        if (source is not ListColumn<T> from)
        {
            throw new ArgumentException($"A {Type} column takes elements of a column that stores the same, not of a {source?.Type} one.", nameof(source));
        }

        var values = from.Get(sourceRow);
        foreach (var position in positions)
        {
            if ((uint)position >= (uint)values.Length)
            {
                throw new IndexOutOfRangeException($"Position {position} is outside the source row's list of {values.Length} elements.");
            }
        }

        var extent = SetUninitialized(row, positions.Length);
        for (var i = 0; i < positions.Length; i++)
        {
            extent[i] = values[positions[i]];
        }
    }

    public override void CopyFrom(ColumnBuffer source, int sourceRow, int row)
    {
        var s = (ListColumn<T>)source;
        var valid = s.IsValid(sourceRow);
        Set(row, s.Get(sourceRow));
        SetValidity(row, valid);
    }

    private static bool Stores(StorageKind element) => element switch
    {
        StorageKind.Bool => typeof(T) == typeof(bool),
        StorageKind.Int32 => typeof(T) == typeof(int),
        StorageKind.Int64 => typeof(T) == typeof(long),
        StorageKind.Float32 => typeof(T) == typeof(float),
        StorageKind.Float64 => typeof(T) == typeof(double),
        StorageKind.String => typeof(T) == typeof(string),
        _ => false,
    };

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
