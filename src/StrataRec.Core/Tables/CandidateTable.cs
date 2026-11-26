using System.Numerics;
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
/// The state of a <see cref="CandidateTable"/> before a node attempt (<see cref="CandidateTable.Checkpoint"/>), to return
/// it to if the attempt fails (<see cref="CandidateTable.Rollback"/>).
/// </summary>
public readonly struct TableCheckpoint
{
    internal TableCheckpoint(int[] order, int count, int physicalRows, int appended, int compactions, int slots)
    {
        Order = order;
        Count = count;
        PhysicalRows = physicalRows;
        Appended = appended;
        Compactions = compactions;
        Slots = slots;
    }

    /// <summary>The ranking, in an array rented from the request memory; <see cref="Count"/> rows.</summary>
    internal int[] Order { get; }

    internal int Count { get; }

    internal int PhysicalRows { get; }

    /// <summary>Rows appended in the request so far, which bounds what may still be appended.</summary>
    internal int Appended { get; }

    /// <summary>How many times the table had been compacted: a rollback cannot cross a Compact.</summary>
    internal int Compactions { get; }

    /// <summary>How many slots had been created: the ones created after it are dropped by a rollback.</summary>
    internal int Slots { get; }
}

/// <summary>
/// Columnar storage of every candidate in one request (design 4.4, 4.5).
/// </summary>
/// <remarks>
/// <para>Slots are allocated lazily on first write. Each slot has a fill bitmap marking which physical rows have been
/// populated (with a value or an explicit "missing"); rows needing a fetch are <c>alive &amp; ~filled</c>.</para>
/// <para>A row has no value in a slot until something writes it, and the table alone applies that rule: every row of a
/// new slot is missing and unfilled, including rows appended before the slot existed (a row source's column that an
/// earlier row source did not write); a row is missing and unfilled in every existing slot when it is appended; and
/// <see cref="Rollback"/> makes the rows it releases so again. Writers mark what they write (the <c>Set</c> methods of
/// the column buffers, or one of the bulk <c>SetValid</c> overloads after a bulk write), so a value nobody wrote
/// (pooled data, a rolled-back attempt's, the <c>''</c> or <c>0</c> of a never-written row) is never read as valid,
/// and a writer that forgets to mark leaves its rows missing rather than wrong.</para>
/// <para>Data no successful attempt wrote is never visible either: <see cref="Rollback"/> drops every slot created since
/// its checkpoint (a failed node's own outputs, or a shared row-source slot it started) and releases the rows the attempt
/// appended in the slots that existed before, which keep everything else. A dropped slot reads as never written, through
/// any read path (the current version or a pinned <c>col.x@node</c>), exactly as in a replay that skips the attempt.</para>
/// <para>A slot that has a buffer is not written again by a later attempt of the node it belongs to (a fallback node
/// that runs for several nodes): the attempt fills a buffer of its own (<see cref="CreateDetached"/>), which becomes the
/// slot's when the attempt commits (<see cref="ReplaceSlot"/>), so a failed one leaves the slot untouched too.</para>
/// <para>Rows at or past <see cref="PhysicalRowCount"/> carry nothing that is read: a row's state in every slot is reset
/// when it is appended, and <see cref="Rollback"/> releases the rows a failed attempt appended, so the next append
/// reuses them.</para>
/// <para>Not thread-safe: a table is owned by one request and mutated on the request's logical thread.</para>
/// </remarks>
public sealed class CandidateTable
{
    private readonly ColumnBuffer?[] _slots;
    private readonly ulong[]?[] _filled;
    private readonly int[] _created; // [0, _createdCount): every existing slot but the key column, in creation order
    private int _createdCount;
    private int _totalAppended;
    private EntityKeyIndex? _keyIndex; // key → last physical row appended with it, of rows [0, _indexedRows); built on first FindRow
    private int _indexedRows;
    private int _compactions;
    private long[]? _releasedInt64; // keys of the rows Compact released, in release order (int64 keys); rented on the first release
    private string[]? _releasedStrings; // the same for string keys
    private int _released;
    private EntityKeyIndex? _releasedIndex; // the first _releasedIndexed released keys; built on the first ContainsKey that needs it
    private int _releasedIndexed;

    public CandidateTable(TableSchema schema, int capacity, RequestMemory memory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Schema = schema;
        Capacity = capacity;
        Memory = memory;
        _slots = new ColumnBuffer?[schema.SlotCount];
        _filled = new ulong[]?[schema.SlotCount];
        _created = memory.Rent<int>(schema.SlotCount);
        Rows = new RowSelection(capacity, memory);
        _slots[TableSchema.EntityIdSlot] = ColumnBuffer.Create(schema.SlotTypes[0], capacity, memory);
    }

    public TableSchema Schema { get; }

    /// <summary>
    /// Maximum number of rows that may ever be appended in this request: the limits of its row sources, less what
    /// finished row sources did not use (<see cref="ReleaseRowBudget"/>). Rows a failed attempt appended are given back by
    /// <see cref="Rollback"/>.
    /// </summary>
    public int Capacity { get; private set; }

    /// <summary>Rows appended in this request so far, less those a <see cref="Rollback"/> gave back.</summary>
    public int AppendedRows => _totalAppended;

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
    public StringColumn StringIds => (StringColumn)_slots[TableSchema.EntityIdSlot]!;

    /// <summary>Ratio of live rows to physical rows (1.0 when empty).</summary>
    public double LiveRatio => PhysicalRowCount == 0 ? 1.0 : (double)Rows.Count / PhysicalRowCount;

    public EntityKey GetKey(int physicalRow) => KeyType == EntityKeyType.Int64
        ? EntityKey.FromInt64(Int64Ids[physicalRow])
        : EntityKey.FromString(StringIds.Get(physicalRow));

    /// <summary>Returns the slot's buffer, or null when nothing has been written to it yet.</summary>
    public ColumnBuffer? TryGetSlot(int slot) => _slots[slot];

    /// <summary>Returns the slot's buffer, creating it on first use with every row missing (see the remarks).</summary>
    public ColumnBuffer GetOrCreateSlot(int slot)
    {
        if (_slots[slot] is { } existing)
        {
            return existing;
        }

        var created = CreateDetached(slot);
        _slots[slot] = created;
        _created[_createdCount++] = slot;
        return created;
    }

    /// <summary>
    /// A buffer for another version of a slot that has one, every row missing as in a new slot, and not the slot's until
    /// <see cref="ReplaceSlot"/>: a node that runs again in a request (a fallback node shared by several nodes) writes
    /// here, so what it wrote before stays what readers see, the node itself included, until the new version is published,
    /// and stays for good when the attempt fails.
    /// </summary>
    public ColumnBuffer CreateDetached(int slot)
    {
        var created = ColumnBuffer.Create(Schema.SlotTypes[slot], Math.Max(1, StorageCapacity), Memory);
        created.SetAllMissing();
        return created;
    }

    /// <summary>Makes <paramref name="buffer"/> (<see cref="CreateDetached"/>) the slot's. The buffer it replaces stays rented until the request ends.</summary>
    public void ReplaceSlot(int slot, ColumnBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_slots[slot] is null)
        {
            throw new InvalidOperationException("Only a slot that has a buffer is replaced; a new one is created (GetOrCreateSlot).");
        }

        _slots[slot] = buffer;
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

    /// <summary>
    /// True when each of <paramref name="rows"/> (physical rows) is filled in the slot: the check of a fill that covers
    /// some rows only (the output's returned rows), one bit per row rather than a word of every live row.
    /// </summary>
    public bool IsFilled(int slot, ReadOnlySpan<int> rows)
    {
        var filled = _filled[slot];
        if (filled is null)
        {
            return rows.IsEmpty;
        }

        foreach (var row in rows)
        {
            if (!Bits.Get(filled, row))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Number of live rows that have no value in the slot: all of them when the slot was never created. Checked a word of
    /// rows at a time, so a slot with a value on every live row costs no per-row work.
    /// </summary>
    public int CountMissingLiveRows(int slot) => _slots[slot] is { } buffer ? CountMissingLiveRows(buffer) : Rows.Count;

    /// <summary><see cref="CountMissingLiveRows(int)"/> of a buffer made for this table that is not one of its slots yet.</summary>
    public int CountMissingLiveRows(ColumnBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var validity = buffer.Validity;
        if (validity is null)
        {
            return 0; // the key column: written for every row when it is appended
        }

        var alive = Rows.AliveBits;
        var words = Math.Min(alive.Length, Bits.WordCount(Math.Max(1, StorageCapacity)));
        var missing = 0;
        for (var i = 0; i < words; i++)
        {
            missing += BitOperations.PopCount(alive[i] & ~validity[i]);
        }

        return missing;
    }

    /// <summary>Rows that may still be appended in this request.</summary>
    public int FreeRows => Capacity - _totalAppended;

    /// <summary>
    /// Gives back <paramref name="rows"/> of the append budget (at most <see cref="FreeRows"/>): a row source that has
    /// finished below its limit. Slots created afterwards, and a <see cref="Compact"/>, are sized without them, so the
    /// columns written after a recall that found a quarter of its quota are a quarter of the size.
    /// </summary>
    public void ReleaseRowBudget(int rows)
    {
        if (rows > 0)
        {
            Capacity -= Math.Min(rows, FreeRows);
        }
    }

    /// <summary>
    /// Appends rows with the given int64 keys at the end of the ranking. Returns the first new physical row. A key is
    /// appended only while it is not live, once (design 4.5): a row source that may bring back a dropped key skips the live
    /// ones (<see cref="FindRow"/> and <see cref="RowSelection.IsAlive"/>), and the built-in ones skip every key the request
    /// has seen (<see cref="ContainsKey"/>). The table does not check it, which would hash every appended key.
    /// </summary>
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

    /// <summary>Appends rows with the given string keys at the end of the ranking. Returns the first new physical row. A key is appended only while it is not live (see the int64 overload).</summary>
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

    /// <summary>
    /// Finds the physical row of a key (live or dropped) or returns -1: the last row appended with it, which is its live row
    /// when it has one, since a key is appended only while it is not live (<see cref="AppendRows(ReadOnlySpan{long})"/>; a
    /// dropped key a row source brought back is found at its new row, not at the dropped one). A key whose row a
    /// <see cref="Compact"/> released has no row any more; <see cref="ContainsKey"/> still knows it.
    /// </summary>
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

    /// <summary>
    /// Whether the key was appended in this request: a live or a dropped row, or a row a <see cref="Compact"/> released
    /// (rows a <see cref="Rollback"/> released were never appended). The built-in row sources test this, not
    /// <see cref="FindRow"/>, so that they never append a key a filter dropped, whether or not the table was compacted
    /// since (design 4.5, invariant 2): compaction changes where rows are, never which entities the request has seen.
    /// </summary>
    /// <remarks>
    /// Compact copies the released keys aside; they are indexed on the first call that does not find the key in a row, so
    /// a request whose row sources all run before its compactions never hashes them.
    /// </remarks>
    public bool ContainsKey(in EntityKey key)
    {
        if (FindRow(key) >= 0)
        {
            return true;
        }

        if (_released == 0)
        {
            return false;
        }

        if (_releasedIndexed < _released)
        {
            IndexReleasedKeys();
        }

        return _releasedIndex!.TryGetValue(key, out _);
    }

    private void IndexReleasedKeys()
    {
        var index = _releasedIndex ??= new EntityKeyIndex(KeyType, _released, Memory);
        for (var i = _releasedIndexed; i < _released; i++)
        {
            if (KeyType == EntityKeyType.Int64)
            {
                index.TryAdd(_releasedInt64![i], 0);
            }
            else
            {
                index.TryAdd(_releasedStrings![i], 0);
            }
        }

        _releasedIndexed = _released;
    }

    /// <summary>Copies aside the keys of the rows the coming Compact releases (every physical row not alive).</summary>
    private void RecordReleasedKeys()
    {
        // Every released row was appended once and never rolled back, so all of them together fit in Capacity (a key
        // brought back has a row for each time it was appended, and is copied once for each).
        var rows = Rows;
        if (KeyType == EntityKeyType.Int64)
        {
            var released = _releasedInt64 ??= Memory.Rent<long>(Capacity);
            var ids = Int64Ids;
            for (var r = 0; r < PhysicalRowCount; r++)
            {
                if (!rows.IsAlive(r))
                {
                    released[_released++] = ids[r];
                }
            }
        }
        else
        {
            var released = _releasedStrings ??= Memory.Rent<string>(Capacity);
            var ids = StringIds;
            for (var r = 0; r < PhysicalRowCount; r++)
            {
                if (!rows.IsAlive(r))
                {
                    released[_released++] = ids.Get(r);
                }
            }
        }
    }

    private void IndexAppendedRows()
    {
        var index = _keyIndex ??= new EntityKeyIndex(KeyType, StorageCapacity, Memory);
        if (KeyType == EntityKeyType.Int64)
        {
            var ids = Int64Ids;
            for (var r = _indexedRows; r < PhysicalRowCount; r++)
            {
                if (!index.TryAdd(ids[r], r))
                {
                    index.TryReplace(ids[r], r); // a dropped key brought back: found at its new row
                }
            }
        }
        else
        {
            var ids = StringIds;
            for (var r = _indexedRows; r < PhysicalRowCount; r++)
            {
                var key = ids.Get(r);
                if (!index.TryAdd(key, r))
                {
                    index.TryReplace(key, r);
                }
            }
        }

        _indexedRows = PhysicalRowCount;
    }

    /// <summary>
    /// Rewrites every slot so that live rows occupy physical rows 0..n-1 in rank order and dropped rows are released
    /// (design 4.5). Fill state moves with the values. Old arrays are returned when the request ends. The released keys
    /// are kept: <see cref="ContainsKey"/> still finds them, so the built-in row sources do not append them again.
    /// </summary>
    public void Compact()
    {
        var count = Rows.Count;
        if (count < PhysicalRowCount)
        {
            RecordReleasedKeys();
        }

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
        _compactions++;

        // Rows moved: re-index lazily. Compact never raises StorageCapacity, so the index's arrays still fit.
        _keyIndex?.Clear();
        _indexedRows = 0;
    }

    /// <summary>
    /// Remembers the table as it is before a node attempt, for <see cref="Rollback"/> if the attempt fails (design 4.6).
    /// The ranking is copied into a scratch array of the request memory, given back by <see cref="Discard"/> once the
    /// attempt is settled.
    /// </summary>
    public TableCheckpoint Checkpoint()
    {
        var order = Memory.RentScratch<int>(Math.Max(1, Rows.Count));
        var count = Rows.Capture(order);
        return new TableCheckpoint(order, count, PhysicalRowCount, _totalAppended, _compactions, _createdCount);
    }

    /// <summary>
    /// Gives back the ranking copy of <paramref name="checkpoint"/>, which cannot be rolled back to afterwards. One never
    /// discarded is only lost to the pool.
    /// </summary>
    public void Discard(in TableCheckpoint checkpoint)
    {
        if (checkpoint.Order is not null)
        {
            Memory.ReturnScratch(checkpoint.Order);
        }
    }

    /// <summary>
    /// Leaves the table as it was at <paramref name="checkpoint"/>, which a failed node attempt must (design 4.6). Slots
    /// created since are dropped: they read as never written again, whoever reads them, and the next write creates them
    /// anew. The ranking is restored, and the rows appended since are released as if never appended. They are no longer
    /// found by <see cref="FindRow"/>, so a later row source may append the same keys; they hold nothing in any remaining
    /// slot (missing and unfilled); and their capacity may be appended again, reusing the same physical rows. Rows that
    /// existed at the checkpoint keep their state in the remaining slots, dropped rows included, so
    /// <see cref="ContainsKey"/> still knows a filtered key, and the rows earlier row sources wrote into a shared slot keep
    /// their values.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The table was compacted since the checkpoint (physical rows moved), or the checkpoint was never taken.
    /// </exception>
    public void Rollback(in TableCheckpoint checkpoint)
    {
        if (checkpoint.Order is null || checkpoint.Compactions != _compactions || checkpoint.PhysicalRows > PhysicalRowCount)
        {
            throw new InvalidOperationException("A table can only be rolled back to a checkpoint taken from it since its last Compact.");
        }

        for (var i = checkpoint.Slots; i < _createdCount; i++)
        {
            // Its arrays stay rented until the request ends, like everything a Compact replaces.
            _slots[_created[i]] = null;
            _filled[_created[i]] = null;
        }

        _createdCount = checkpoint.Slots;
        Rows.Restore(checkpoint.Order.AsSpan(0, checkpoint.Count));
        var first = checkpoint.PhysicalRows;
        var appended = PhysicalRowCount - first;
        if (appended == 0)
        {
            return;
        }

        ClearRowState(first, appended);
        if (_indexedRows > first)
        {
            // Keys of released rows must not be found. Rebuilt lazily from row 0 in append order, as on first use, so a key
            // brought back by the attempt is found at its row from before it again.
            _keyIndex!.Clear();
            _indexedRows = 0;
        }

        PhysicalRowCount = first;
        _totalAppended = checkpoint.Appended;
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
    /// Makes appended rows live. The key column is filled; every other existing slot has the new rows missing and
    /// unfilled, so values written before the append (pooled data, or a rolled-back attempt's) are never read for them.
    /// A row source that writes those columns sets them afterwards.
    /// </summary>
    private void PublishAppended(int first, int count)
    {
        ClearRowState(first, count);
        Bits.SetRange(GetFilled(TableSchema.EntityIdSlot), first, count);
        Rows.AppendRange(first, count);
    }

    /// <summary>Rows <c>[first, first + count)</c> hold nothing: missing in every slot but the key column, unfilled in every slot.</summary>
    private void ClearRowState(int first, int count)
    {
        for (var s = 0; s < _slots.Length; s++)
        {
            if (s != TableSchema.EntityIdSlot && _slots[s] is { } slot)
            {
                Bits.ClearRange(slot.EnsureValidity(), first, count);
            }

            if (_filled[s] is { } filled)
            {
                Bits.ClearRange(filled, first, count);
            }
        }
    }

    private void RequireKeyType(EntityKeyType expected)
    {
        if (KeyType != expected)
        {
            throw new InvalidOperationException($"Table keys are {KeyType}, not {expected}.");
        }
    }
}
