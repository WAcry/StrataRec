using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Columns;

/// <summary>
/// Base class of every column buffer in a <see cref="Tables.CandidateTable"/> (design 4.4).
/// </summary>
/// <remarks>
/// <para>Rows are physical row indices in <c>[0, Capacity)</c>. A row's value is meaningful only when the row is
/// valid (<see cref="IsValid"/>) and, for feature slots, filled. Other rows may hold arbitrary data, but every
/// buffer guarantees <em>structural</em> validity for all rows: dictionary codes are in range, list extents are
/// inside the arena. Vectorized code may therefore scan whole physical ranges without bounds failures.</para>
/// <para>All arrays are rented from the owning <see cref="RequestMemory"/> and returned when the request ends.</para>
/// </remarks>
public abstract class ColumnBuffer
{
    private ulong[]? _validity;

    protected ColumnBuffer(ColumnType type, int capacity, RequestMemory memory)
    {
        Type = type;
        Capacity = capacity;
        Memory = memory;
    }

    public ColumnType Type { get; }

    /// <summary>Number of physical rows the buffers can hold.</summary>
    public int Capacity { get; private set; }

    protected RequestMemory Memory { get; }

    /// <summary>Validity bitmap (bit set = value present). <c>null</c> means every row is valid.</summary>
    public ulong[]? Validity => _validity;

    public bool IsValid(int row) => _validity is null || Bits.Get(_validity, row);

    /// <summary>Marks a row as missing (allocating the validity bitmap on first use).</summary>
    public void SetMissing(int row) => Bits.Clear(EnsureValidity(), row);

    /// <summary>Marks a row as present. A no-op while the column has no validity bitmap.</summary>
    public void SetValid(int row)
    {
        if (_validity is not null)
        {
            Bits.Set(_validity, row);
        }
    }

    public void SetValidity(int row, bool valid)
    {
        if (valid)
        {
            SetValid(row);
        }
        else
        {
            SetMissing(row);
        }
    }

    /// <summary>Returns the validity bitmap, allocating an all-valid one if necessary.</summary>
    public ulong[] EnsureValidity()
    {
        if (_validity is null)
        {
            var words = Bits.WordCount(Capacity);
            _validity = Memory.Rent<ulong>(words);
            _validity.AsSpan(0, words).Fill(ulong.MaxValue);
        }

        return _validity;
    }

    /// <summary>
    /// Rewrites the column into new buffers of <paramref name="newCapacity"/> rows so that new row <c>i</c> holds old
    /// row <c>rows[i]</c> (used by Compact, design 4.5). Old arrays stay rented until the request ends.
    /// </summary>
    public void Gather(ReadOnlySpan<int> rows, int newCapacity)
    {
        if (newCapacity < rows.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(newCapacity), "Capacity must hold every gathered row.");
        }

        if (_validity is not null)
        {
            var newValidity = Memory.RentCleared<ulong>(Bits.WordCount(newCapacity));
            for (var i = 0; i < rows.Length; i++)
            {
                if (Bits.Get(_validity, rows[i]))
                {
                    Bits.Set(newValidity, i);
                }
            }

            _validity = newValidity;
        }

        GatherValues(rows, newCapacity);
        Capacity = newCapacity;
    }

    /// <summary>Copies the value (and validity) of <paramref name="sourceRow"/> in <paramref name="source"/> into <paramref name="row"/>.</summary>
    public abstract void CopyFrom(ColumnBuffer source, int sourceRow, int row);

    /// <summary>Moves values into new buffers of <paramref name="newCapacity"/> rows; <see cref="Capacity"/> is updated afterwards.</summary>
    protected abstract void GatherValues(ReadOnlySpan<int> rows, int newCapacity);

    /// <summary>Creates an empty column of the given type.</summary>
    public static ColumnBuffer Create(ColumnType type, int capacity, RequestMemory memory) => type.Kind switch
    {
        StorageKind.Bool => new ScalarColumn<bool>(type, capacity, memory),
        StorageKind.Int32 => new ScalarColumn<int>(type, capacity, memory),
        StorageKind.Int64 => new ScalarColumn<long>(type, capacity, memory),
        StorageKind.Float32 => new ScalarColumn<float>(type, capacity, memory),
        StorageKind.Float64 => new ScalarColumn<double>(type, capacity, memory),
        StorageKind.String => new DictStringColumn(type, capacity, memory),
        StorageKind.Text => new TextColumn(type, capacity, memory),
        StorageKind.Vector => new VectorColumn(type, capacity, memory),
        StorageKind.ListInt32 => new ListColumn<int>(type, capacity, memory),
        StorageKind.ListInt64 => new ListColumn<long>(type, capacity, memory),
        StorageKind.ListString => new ListColumn<string>(type, capacity, memory),
        _ => throw new ArgumentException($"Unsupported column type {type}.", nameof(type)),
    };

    protected void CopyValidityFrom(ColumnBuffer source, int sourceRow, int row) => SetValidity(row, source.IsValid(sourceRow));
}
