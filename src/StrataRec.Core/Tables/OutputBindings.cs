using StrataRec.Core.Memory;

namespace StrataRec.Core.Tables;

/// <summary>
/// Per-request map from a logical column to its currently committed physical slot (design 4.6).
/// </summary>
/// <remarks>
/// A node writes into its own slots; only when it succeeds does the engine <see cref="Commit"/> them, which makes the
/// new version visible to later readers. Failed or skipped nodes never commit, so half-written data is invisible.
/// The bindings also record which node produced each committed version, for log lineage (design 8.7).
/// </remarks>
public sealed class OutputBindings
{
    public const int NotProduced = -1;

    private readonly int[] _slots;
    private readonly int[] _producers;

    public OutputBindings(int logicalColumnCount, RequestMemory memory)
    {
        LogicalColumnCount = logicalColumnCount;
        _slots = memory.Rent<int>(Math.Max(1, logicalColumnCount));
        _producers = memory.Rent<int>(Math.Max(1, logicalColumnCount));
        _slots.AsSpan(0, logicalColumnCount).Fill(NotProduced);
        _producers.AsSpan(0, logicalColumnCount).Fill(NotProduced);
    }

    public int LogicalColumnCount { get; }

    /// <summary>The committed physical slot of a logical column, or <see cref="NotProduced"/>.</summary>
    public int Resolve(int logicalId) => _slots[logicalId];

    /// <summary>Index of the node that committed the current version, or <see cref="NotProduced"/>.</summary>
    public int Producer(int logicalId) => _producers[logicalId];

    public bool IsProduced(int logicalId) => _slots[logicalId] != NotProduced;

    public void Commit(int logicalId, int physicalSlot, int producerNode)
    {
        _slots[logicalId] = physicalSlot;
        _producers[logicalId] = producerNode;
    }
}
