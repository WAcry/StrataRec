using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;

namespace StrataRec.Core.Tables;

/// <summary>Static shape of a candidate table: entity key type and the type of every physical slot.</summary>
public sealed class TableSchema
{
    /// <summary>Physical slot of the primary key column <c>col.entity_id</c>.</summary>
    public const int EntityIdSlot = 0;

    public TableSchema(EntityKeyType keyType, IReadOnlyList<ColumnType> slotTypes)
    {
        if (slotTypes.Count == 0)
        {
            throw new ArgumentException("Slot 0 must be the entity id column.", nameof(slotTypes));
        }

        var expected = keyType == EntityKeyType.Int64 ? ColumnType.Int64 : ColumnType.String;
        if (slotTypes[0] != expected)
        {
            throw new ArgumentException($"Slot 0 must have type {expected}, got {slotTypes[0]}.", nameof(slotTypes));
        }

        KeyType = keyType;
        var types = new ColumnType[slotTypes.Count];
        for (var i = 0; i < types.Length; i++)
        {
            types[i] = slotTypes[i];
        }

        SlotTypes = types;
    }

    public EntityKeyType KeyType { get; }

    public IReadOnlyList<ColumnType> SlotTypes { get; }

    public int SlotCount => SlotTypes.Count;
}

/// <summary>
/// Columnar storage of every candidate in one request (design 4.4, 4.5).
/// </summary>
/// <remarks>
/// <para>Slots are allocated lazily on first write. Each slot has a fill bitmap marking which physical rows have been
/// populated (with a value or an explicit "missing"); rows needing a fetch are <c>alive &amp; ~filled</c>.</para>
/// <para>Not thread-safe: a table is owned by one request and mutated on the request's logical thread.</para>
/// </remarks>
public sealed class CandidateTable
{
    private readonly ColumnBuffer?[] _slots;
    private readonly ulong[]?[] _filled;
    private int _totalAppended;
    private EntityKeyIndex? _keyIndex; // key → first physical row, for rows [0, _indexedRows); built on first FindRow
    private int _indexedRows;

    public CandidateTable(TableSchema schema, int capacity, RequestMemory memory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Schema = schema;
        Capacity = capacity;
        Memory = memory;
        _slots = new ColumnBuffer?[schema.SlotCount];
        _filled = new ulong[]?[schema.SlotCount];
        Rows = new RowSelection(capacity, memory);
        _slots[TableSchema.EntityIdSlot] = ColumnBuffer.Create(schema.SlotTypes[0], capacity, memory);
    }

    public TableSchema Schema { get; }

    /// <summary>Maximum number of rows that may ever be appended in this request (sum of the plan's row-source limits).</summary>
    public int Capacity { get; }

    /// <summary>Number of physical rows currently stored (appended rows, minus rows released by Compact).</summary>
    public int PhysicalRowCount { get; private set; }

    /// <summary>
    /// Rows the slot buffers must be able to hold: the stored rows plus every row that may still be appended.
    /// It never grows; Compact shrinks it to survivors plus the remaining append budget.
    /// </summary>
    public int StorageCapacity => PhysicalRowCount + FreeRows;

    public RowSelection Rows { get; }

    public RequestMemory Memory { get; }

    public EntityKeyType KeyType => Schema.KeyType;

    /// <summary>The primary key column for int64-keyed tables.</summary>
    public ScalarColumn<long> Int64Ids => (ScalarColumn<long>)_slots[TableSchema.EntityIdSlot]!;

    /// <summary>The primary key column for string-keyed tables.</summary>
    public DictStringColumn StringIds => (DictStringColumn)_slots[TableSchema.EntityIdSlot]!;

    /// <summary>Ratio of live rows to physical rows (1.0 when empty).</summary>
    public double LiveRatio => PhysicalRowCount == 0 ? 1.0 : (double)Rows.Count / PhysicalRowCount;

    public EntityKey GetKey(int physicalRow) => KeyType == EntityKeyType.Int64
        ? EntityKey.FromInt64(Int64Ids[physicalRow])
        : EntityKey.FromString(StringIds.Get(physicalRow));

    /// <summary>Returns the slot's buffer, or null when nothing has been written to it yet.</summary>
    public ColumnBuffer? TryGetSlot(int slot) => _slots[slot];

    /// <summary>Returns the slot's buffer, creating it on first use.</summary>
    public ColumnBuffer GetOrCreateSlot(int slot) =>
        _slots[slot] ??= ColumnBuffer.Create(Schema.SlotTypes[slot], Math.Max(1, StorageCapacity), Memory);

    /// <summary>Discards whatever was written to a slot (used when a node fails before committing).</summary>
    public void ResetSlot(int slot)
    {
        if (slot == TableSchema.EntityIdSlot)
        {
            throw new InvalidOperationException("The entity id slot cannot be reset.");
        }

        _slots[slot] = null;
        _filled[slot] = null;
    }

    /// <summary>Fill bitmap of a slot (allocated, all-clear, on first use).</summary>
    public Span<ulong> GetFilled(int slot)
    {
        var words = Bits.WordCount(Math.Max(1, StorageCapacity));
        return (_filled[slot] ??= Memory.RentCleared<ulong>(words)).AsSpan(0, words);
    }

    /// <summary>True when every live row of the slot is filled.</summary>
    public bool IsFilledForLiveRows(int slot)
    {
        var filled = _filled[slot];
        if (filled is null)
        {
            return Rows.Count == 0;
        }

        var alive = Rows.AliveBits;
        var words = Math.Min(alive.Length, Bits.WordCount(Math.Max(1, StorageCapacity)));
        for (var i = 0; i < words; i++)
        {
            if ((alive[i] & ~filled[i]) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rows that may still be appended in this request.</summary>
    public int FreeRows => Capacity - _totalAppended;

    /// <summary>Appends rows with the given int64 keys at the end of the ranking. Returns the first new physical row.</summary>
    public int AppendRows(ReadOnlySpan<long> keys)
    {
        RequireKeyType(EntityKeyType.Int64);
        var first = ReserveRows(keys.Length);
        var ids = Int64Ids;
        for (var i = 0; i < keys.Length; i++)
        {
            ids[first + i] = keys[i];
        }

        PublishAppended(first, keys.Length);
        return first;
    }

    /// <summary>Appends rows with the given string keys at the end of the ranking. Returns the first new physical row.</summary>
    public int AppendRows(IReadOnlyList<string> keys)
    {
        RequireKeyType(EntityKeyType.String);
        var first = ReserveRows(keys.Count);
        var ids = StringIds;
        for (var i = 0; i < keys.Count; i++)
        {
            ids.Set(first + i, keys[i]);
        }

        PublishAppended(first, keys.Count);
        return first;
    }

    /// <summary>Finds the physical row of a key (live or dropped) or returns -1. The first row wins for a repeated key.</summary>
    /// <remarks>
    /// The index is built on the first call and rented from the request memory, sized to <see cref="StorageCapacity"/>
    /// so it never grows; rows appended later are indexed on the next call, so appends cost nothing when nobody looks
    /// keys up again. A key of the other key type is never found.
    /// </remarks>
    public int FindRow(in EntityKey key)
    {
        if (PhysicalRowCount == 0)
        {
            return -1;
        }

        if (_indexedRows < PhysicalRowCount)
        {
            IndexAppendedRows();
        }

        return _keyIndex!.TryGetValue(key, out var row) ? row : -1;
    }

    private void IndexAppendedRows()
    {
        var index = _keyIndex ??= new EntityKeyIndex(KeyType, StorageCapacity, Memory);
        if (KeyType == EntityKeyType.Int64)
        {
            var ids = Int64Ids;
            for (var r = _indexedRows; r < PhysicalRowCount; r++)
            {
                index.TryAdd(ids[r], r);
            }
        }
        else
        {
            var ids = StringIds;
            for (var r = _indexedRows; r < PhysicalRowCount; r++)
            {
                index.TryAdd(ids.Get(r), r);
            }
        }

        _indexedRows = PhysicalRowCount;
    }

    /// <summary>
    /// Rewrites every slot so that live rows occupy physical rows 0..n-1 in rank order and dropped rows are released
    /// (design 4.5). Fill state moves with the values. Old arrays are returned when the request ends.
    /// </summary>
    public void Compact()
    {
        var count = Rows.Count;
        var order = Memory.Rent<int>(Math.Max(1, count));
        Rows.Order.CopyTo(order);
        var rows = order.AsSpan(0, count);
        var newCapacity = Math.Max(1, count + FreeRows);

        for (var s = 0; s < _slots.Length; s++)
        {
            _slots[s]?.Gather(rows, newCapacity);
            var filled = _filled[s];
            if (filled is not null)
            {
                var next = Memory.RentCleared<ulong>(Bits.WordCount(newCapacity));
                for (var i = 0; i < rows.Length; i++)
                {
                    if (Bits.Get(filled, rows[i]))
                    {
                        Bits.Set(next, i);
                    }
                }

                _filled[s] = next;
            }
        }

        PhysicalRowCount = count;
        Rows.ResetIdentity(count);

        // Rows moved: re-index lazily. Compact never raises StorageCapacity, so the index's arrays still fit.
        _keyIndex?.Clear();
        _indexedRows = 0;
    }

    private int ReserveRows(int count)
    {
        if (count > FreeRows)
        {
            throw new InvalidOperationException(
                $"Appending {count} rows exceeds the table capacity ({Capacity}, {PhysicalRowCount} used). " +
                "Row sources must truncate to their declared limits.");
        }

        var first = PhysicalRowCount;
        PhysicalRowCount += count;
        _totalAppended += count;
        return first;
    }

    /// <summary>
    /// Makes appended rows live. The key column is filled; every other existing slot marks the new rows missing and
    /// unfilled, so values written before the append (or pooled garbage) are never read for them. A row source that
    /// writes those columns sets them afterwards.
    /// </summary>
    private void PublishAppended(int first, int count)
    {
        Bits.SetRange(GetFilled(TableSchema.EntityIdSlot), first, count);
        for (var s = 1; s < _slots.Length; s++)
        {
            var slot = _slots[s];
            if (slot is not null)
            {
                Bits.ClearRange(slot.EnsureValidity(), first, count);
            }

            var filled = _filled[s];
            if (filled is not null)
            {
                Bits.ClearRange(filled, first, count);
            }
        }

        Rows.AppendRange(first, count);
    }

    private void RequireKeyType(EntityKeyType expected)
    {
        if (KeyType != expected)
        {
            throw new InvalidOperationException($"Table keys are {KeyType}, not {expected}.");
        }
    }
}
