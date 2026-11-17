using System.Numerics;
using System.Runtime.CompilerServices;
using StrataRec.Core.Memory;

namespace StrataRec.Core.Keys;

/// <summary>
/// <see cref="EntityKeyIndex"/> for keys of several parts (design 6.5): a request-scoped hash index from tuples of
/// <see cref="EntityKey"/>s to <c>int</c> values, every array rented from the request's <see cref="RequestMemory"/>, so
/// deduplicating thousands of tuples allocates nothing per tuple.
/// </summary>
/// <remarks>
/// <para>For the parts that vary per key only: the shared parts of a batch (a request input) are the same for every key,
/// so a caller leaves them out and indexes the rest. A key whose only varying part is one column is an
/// <see cref="EntityKeyIndex"/>, which compares and hashes one <c>long</c> or string; this index is for two or more.</para>
/// <para>Open addressing with linear probing over a power-of-two slot table kept at most half full, as
/// <see cref="EntityKeyIndex"/>. Each entry stores its tuple's hash, so a probe compares parts only when the hashes match
/// and growing never rehashes a string. Parts are stored part-major (<see cref="PartKeys"/>): part p of every entry is one
/// contiguous run, in insertion order, which is how a batch of keys lays them out.</para>
/// <para>Not thread-safe; used on the request's logical thread. Nothing may use it after its memory is released.</para>
/// </remarks>
public sealed class EntityKeyTupleIndex
{
    private const int MinSlots = 16;
    private const ulong Fibonacci = 0x9E3779B97F4A7C15UL;

    private readonly RequestMemory _memory;
    private int _partCount;
    private int[] _slots = Array.Empty<int>();
    private int _slotMask = -1; // no slots until the first Reset allocates them
    private int _shift;
    private int _maxCount;
    private EntityKey[] _parts = Array.Empty<EntityKey>(); // part p of entry e at p * _maxCount + e
    private int[] _hashes = Array.Empty<int>();
    private int[] _values = Array.Empty<int>();
    private int _count;

    /// <param name="partCount">Parts of every tuple, 1 to <see cref="EntityKeyLayout.MaxParts"/>.</param>
    /// <param name="capacity">Tuples the index holds without growing.</param>
    public EntityKeyTupleIndex(int partCount, int capacity, RequestMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _memory = memory;
        Reset(partCount, capacity);
    }

    /// <summary>Number of distinct tuples.</summary>
    public int Count => _count;

    public int PartCount => _partCount;

    /// <summary>Part <paramref name="part"/> of every tuple, in insertion order (entry <c>i</c> is the i-th tuple added).</summary>
    public ReadOnlySpan<EntityKey> PartKeys(int part)
    {
        if ((uint)part >= (uint)_partCount)
        {
            throw new ArgumentOutOfRangeException(nameof(part), part, $"Tuples have {_partCount} parts.");
        }

        return _parts.AsSpan(part * _maxCount, _count);
    }

    /// <summary>Part <paramref name="part"/> of entry <paramref name="entry"/>.</summary>
    public EntityKey Part(int entry, int part)
    {
        if ((uint)entry >= (uint)_count || (uint)part >= (uint)_partCount)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), entry, $"The index has {_count} tuples of {_partCount} parts.");
        }

        return _parts[(part * _maxCount) + entry];
    }

    /// <summary>
    /// Empties the index and readies it for <paramref name="capacity"/> tuples of <paramref name="partCount"/> parts without
    /// growing: a caller that indexes one batch after another keeps one index and costs no object per batch.
    /// </summary>
    public void Reset(int partCount, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partCount, EntityKeyLayout.MaxParts);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, EntityKeyIndex.MaxCapacity);
        Clear();
        var slotCount = capacity <= MinSlots / 2 ? MinSlots : (int)BitOperations.RoundUpToPowerOf2((uint)capacity * 2);
        if (slotCount > _slotMask + 1 || _parts.Length < partCount * _maxCount)
        {
            Allocate(slotCount, partCount);
        }

        _partCount = partCount;
    }

    /// <summary>Removes every tuple, keeping the rented arrays for reuse.</summary>
    public void Clear()
    {
        _slots.AsSpan(0, _slotMask + 1).Clear();
        for (var p = 0; p < _partCount; p++)
        {
            _parts.AsSpan(p * _maxCount, _count).Clear(); // drop the string references; the arrays live until the request ends
        }

        _count = 0;
    }

    public bool TryGetValue(ReadOnlySpan<EntityKey> tuple, out int value)
    {
        RequireParts(tuple);
        var hash = Hash(tuple);
        for (var s = Slot((uint)hash); ; s = (s + 1) & _slotMask)
        {
            var entry = _slots[s] - 1;
            if (entry < 0)
            {
                value = 0;
                return false;
            }

            if (_hashes[entry] == hash && Matches(entry, tuple))
            {
                value = _values[entry];
                return true;
            }
        }
    }

    /// <summary>
    /// Returns the value of <paramref name="tuple"/>, adding it with <paramref name="value"/> first when it is absent — one
    /// probe sequence for both.
    /// </summary>
    public int GetOrAdd(ReadOnlySpan<EntityKey> tuple, int value, out bool added)
    {
        RequireParts(tuple);
        var hash = Hash(tuple);
        var s = Slot((uint)hash);
        for (; ; s = (s + 1) & _slotMask)
        {
            var entry = _slots[s] - 1;
            if (entry < 0)
            {
                break;
            }

            if (_hashes[entry] == hash && Matches(entry, tuple))
            {
                added = false;
                return _values[entry];
            }
        }

        if (_count == _maxCount)
        {
            Grow();
            s = EmptySlot(Slot((uint)hash));
        }

        var e = _count++;
        for (var p = 0; p < tuple.Length; p++)
        {
            _parts[(p * _maxCount) + e] = tuple[p];
        }

        _hashes[e] = hash;
        _values[e] = value;
        _slots[s] = e + 1;
        added = true;
        return value;
    }

    /// <summary>The tuple's hash: each part's (a string's ordinal hash, an int64's), mixed in order.</summary>
    private static int Hash(ReadOnlySpan<EntityKey> tuple)
    {
        var hash = default(HashCode);
        foreach (ref readonly var part in tuple)
        {
            if (part.StringValue is { } s)
            {
                hash.Add(s.GetHashCode());
            }
            else
            {
                hash.Add(part.Int64Value);
            }
        }

        return hash.ToHashCode();
    }

    private bool Matches(int entry, ReadOnlySpan<EntityKey> tuple)
    {
        for (var p = 0; p < tuple.Length; p++)
        {
            ref readonly var stored = ref _parts[(p * _maxCount) + entry];
            if (stored.Int64Value != tuple[p].Int64Value || !string.Equals(stored.StringValue, tuple[p].StringValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void RequireParts(ReadOnlySpan<EntityKey> tuple)
    {
        if (tuple.Length != _partCount)
        {
            throw new ArgumentException($"A tuple of {tuple.Length} parts; this index holds tuples of {_partCount}.", nameof(tuple));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot(ulong hash) => (int)((hash * Fibonacci) >> _shift);

    private int EmptySlot(int start)
    {
        var s = start;
        while (_slots[s] != 0)
        {
            s = (s + 1) & _slotMask;
        }

        return s;
    }

    private void Allocate(int slotCount, int partCount)
    {
        _slots = _memory.Rent<int>(slotCount);
        _slots.AsSpan(0, slotCount).Clear();
        _slotMask = slotCount - 1;
        _shift = 64 - BitOperations.Log2((uint)slotCount);
        _maxCount = slotCount / 2;
        _values = _memory.Rent<int>(_maxCount);
        _hashes = _memory.Rent<int>(_maxCount);
        _parts = _memory.Rent<EntityKey>(partCount * _maxCount);
    }

    private void Grow()
    {
        if (_slotMask + 1 > EntityKeyIndex.MaxCapacity)
        {
            throw new InvalidOperationException($"An entity key index holds at most {EntityKeyIndex.MaxCapacity} keys.");
        }

        var values = _values;
        var hashes = _hashes;
        var parts = _parts;
        var oldMax = _maxCount;
        var count = _count;
        Allocate((_slotMask + 1) * 2, _partCount);
        values.AsSpan(0, count).CopyTo(_values);
        hashes.AsSpan(0, count).CopyTo(_hashes);
        for (var p = 0; p < _partCount; p++)
        {
            parts.AsSpan(p * oldMax, count).CopyTo(_parts.AsSpan(p * _maxCount));
        }

        for (var e = 0; e < count; e++)
        {
            _slots[EmptySlot(Slot((uint)_hashes[e]))] = e + 1;
        }
    }
}
