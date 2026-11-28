using StrataRec.Core.Keys;

namespace StrataRec.Core.Tables;

/// <summary>
/// What an operator reads of the request's candidate table: entity keys, the live rows in rank order, key lookups and
/// how many rows still fit. It cannot change the table.
/// </summary>
/// <remarks>
/// <para>An operator changes the table only through its context, which checks each change against the effects the node
/// declared: the ranking (<c>Retain</c>, <c>Truncate</c>, <c>SetOrder</c>), new rows (<c>AppendRows</c>) and the node's
/// own output columns (<c>Output</c>). Compaction, checkpoints and rollback belong to the executor.</para>
/// <para>A struct over the table: every member reads the table as it is now.</para>
/// </remarks>
public readonly struct CandidateTableView
{
    private readonly CandidateTable _table;

    public CandidateTableView(CandidateTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <summary>The read-only view of a table, like <see cref="Span{T}"/> to <see cref="ReadOnlySpan{T}"/>.</summary>
    public static implicit operator CandidateTableView(CandidateTable table) => new(table);

    public EntityKeyType KeyType => _table.KeyType;

    /// <inheritdoc cref="CandidateTable.Capacity"/>
    public int Capacity => _table.Capacity;

    /// <inheritdoc cref="CandidateTable.PhysicalRowCount"/>
    public int PhysicalRowCount => _table.PhysicalRowCount;

    /// <inheritdoc cref="CandidateTable.StorageCapacity"/>
    /// <remarks>Size a buffer indexed by physical row to this.</remarks>
    public int StorageCapacity => _table.StorageCapacity;

    /// <inheritdoc cref="CandidateTable.FreeRows"/>
    public int FreeRows => _table.FreeRows;

    /// <summary>Live physical rows in rank order (best first).</summary>
    public ReadOnlySpan<int> LiveRows => _table.Rows.Order;

    public int LiveCount => _table.Rows.Count;

    /// <summary>True when the physical row is live (in the ranking); a dropped row stays in the table until compaction.</summary>
    public bool IsAlive(int physicalRow) => _table.Rows.IsAlive(physicalRow);

    /// <summary>The entity key of a physical row, live or dropped.</summary>
    public EntityKey GetKey(int physicalRow) => _table.GetKey(physicalRow);

    /// <summary>
    /// The int64 entity id of each physical row, indexed by row; empty when the table's keys are strings.
    /// </summary>
    public ReadOnlySpan<long> Int64Ids => _table.KeyType == EntityKeyType.Int64 ? _table.Int64Ids.Span : default;

    /// <inheritdoc cref="CandidateTable.FindRow"/>
    public int FindRow(in EntityKey key) => _table.FindRow(key);

    /// <inheritdoc cref="CandidateTable.ContainsKey"/>
    public bool ContainsKey(in EntityKey key) => _table.ContainsKey(key);

    public override string ToString() => $"{LiveCount} live of {PhysicalRowCount} rows ({KeyType} keys, {FreeRows} free)";
}
