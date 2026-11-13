using System.Numerics;
using System.Runtime.CompilerServices;
using StrataRec.Core.Memory;

namespace StrataRec.Core.Keys;

/// <summary>
/// A request-scoped hash index from entity keys to <c>int</c> values (key indices, row numbers). Every array is rented
/// from the request's <see cref="RequestMemory"/>, so building, probing and growing it allocate nothing per key
/// (design 4.8); a <see cref="Dictionary{TKey, TValue}"/> over thousands of candidates allocates its entries and
/// buckets on every request, and above 85 KB on the large object heap.
/// </summary>
/// <remarks>
/// <para>Open addressing with linear probing over a power-of-two slot table that is kept at most half full, so probe
/// sequences are short on average and always reach an empty slot. A slot holds <c>entry + 1</c> (0 = empty). Entries are
/// stored densely in insertion order, so <see cref="Int64Keys"/> / <see cref="StringKeys"/> double as the list of
/// distinct keys in first-seen order.</para>
/// <para>One key type per index. The <c>int64</c> case is specialized: keys are compared as <c>long</c> and hashed with a
/// Fibonacci multiplier, which spreads sequential ids evenly. String keys compare ordinally; their hash codes are
/// stored per entry so growing never rehashes a string. Lookups of a key of the other type find nothing.</para>
/// <para>Growth rents twice the space and leaves the old arrays on the request's ledger until the request ends, like
/// Compact does; callers that know an upper bound on the number of keys pass it as the capacity and never grow.</para>
/// <para>Not thread-safe; used on the request's logical thread. Nothing may use it after its memory is released.</para>
/// </remarks>
public sealed class EntityKeyIndex
{
    /// <summary>Largest supported capacity: the slot table (twice the capacity) must stay within array pool limits.</summary>
    public const int MaxCapacity = 1 << 29;

    private const int MinSlots = 16;
    private const ulong Fibonacci = 0x9E3779B97F4A7C15UL;

    private readonly RequestMemory _memory;
    private readonly bool _stringKeys;
    private int[] _slots = Array.Empty<int>();
    private int _slotMask;
    private int _shift;
    private int _maxCount;
    private long[] _int64Keys = Array.Empty<long>();
    private string[] _strings = Array.Empty<string>();
    private int[] _hashes = Array.Empty<int>();
    private int[] _values = Array.Empty<int>();
    private int _count;

    /// <param name="keyType">The type of every key this index holds.</param>
    /// <param name="capacity">Number of keys the index holds without growing.</param>
    /// <param name="memory">The request memory the arrays are rented from.</param>
    public EntityKeyIndex(EntityKeyType keyType, int capacity, RequestMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaxCapacity);
        if (keyType is not (EntityKeyType.Int64 or EntityKeyType.String))
        {
            throw new ArgumentOutOfRangeException(nameof(keyType), keyType, "Entity keys are int64 or string.");
        }

        KeyType = keyType;
        _memory = memory;
        _stringKeys = keyType == EntityKeyType.String;
        Allocate(capacity <= MinSlots / 2 ? MinSlots : (int)BitOperations.RoundUpToPowerOf2((uint)capacity * 2));
    }

    public EntityKeyType KeyType { get; }

    /// <summary>Number of distinct keys.</summary>
    public int Count => _count;

    /// <summary>Distinct keys in insertion order (entry <c>i</c> is the i-th key added). Int64 indexes only.</summary>
    public ReadOnlySpan<long> Int64Keys
    {
        get
        {
            RequireKeyType(EntityKeyType.Int64);
            return _int64Keys.AsSpan(0, _count);
        }
    }

    /// <summary>Distinct keys in insertion order (entry <c>i</c> is the i-th key added). String indexes only.</summary>
    public ReadOnlySpan<string> StringKeys
    {
        get
        {
            RequireKeyType(EntityKeyType.String);
            return _strings.AsSpan(0, _count);
        }
    }

    /// <summary>The key of entry <paramref name="entry"/> (insertion order).</summary>
    public EntityKey KeyAt(int entry)
    {
        if ((uint)entry >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), entry, $"The index has {_count} keys.");
        }

        return _stringKeys ? EntityKey.FromString(_strings[entry]) : EntityKey.FromInt64(_int64Keys[entry]);
    }

    public bool TryGetValue(in EntityKey key, out int value) =>
        key.StringValue is { } s ? TryGetValue(s, out value) : TryGetValue(key.Int64Value, out value);

    public bool TryGetValue(long key, out int value)
    {
        if (!_stringKeys)
        {
            var slots = _slots;
            var keys = _int64Keys;
            for (var s = Slot((ulong)key); ; s = (s + 1) & _slotMask)
            {
                var entry = slots[s] - 1;
                if (entry < 0)
                {
                    break;
                }

                if (keys[entry] == key)
                {
                    value = _values[entry];
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }

    public bool TryGetValue(string key, out int value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_stringKeys)
        {
            var hash = key.GetHashCode();
            var slots = _slots;
            for (var s = Slot((uint)hash); ; s = (s + 1) & _slotMask)
            {
                var entry = slots[s] - 1;
                if (entry < 0)
                {
                    break;
                }

                if (_hashes[entry] == hash && string.Equals(_strings[entry], key))
                {
                    value = _values[entry];
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }

    /// <summary>Adds the key with <paramref name="value"/>; returns false (keeping the first value) if it is present.</summary>
    public bool TryAdd(in EntityKey key, int value)
    {
        GetOrAdd(key, value, out var added);
        return added;
    }

    /// <inheritdoc cref="TryAdd(in EntityKey, int)"/>
    public bool TryAdd(long key, int value)
    {
        GetOrAdd(key, value, out var added);
        return added;
    }

    /// <inheritdoc cref="TryAdd(in EntityKey, int)"/>
    public bool TryAdd(string key, int value)
    {
        GetOrAdd(key, value, out var added);
        return added;
    }

    /// <summary>
    /// Returns the value of <paramref name="key"/>, adding it with <paramref name="value"/> first when it is absent —
    /// one probe sequence for both. Throws when the key's type is not <see cref="KeyType"/>.
    /// </summary>
    public int GetOrAdd(in EntityKey key, int value, out bool added) =>
        key.StringValue is { } s ? GetOrAdd(s, value, out added) : GetOrAdd(key.Int64Value, value, out added);

    /// <inheritdoc cref="GetOrAdd(in EntityKey, int, out bool)"/>
    public int GetOrAdd(long key, int value, out bool added)
    {
        RequireKeyType(EntityKeyType.Int64);
        var s = Slot((ulong)key);
        for (; ; s = (s + 1) & _slotMask)
        {
            var entry = _slots[s] - 1;
            if (entry < 0)
            {
                break;
            }

            if (_int64Keys[entry] == key)
            {
                added = false;
                return _values[entry];
            }
        }

        if (_count == _maxCount)
        {
            Grow();
            s = EmptySlot(Slot((ulong)key));
        }

        var e = _count++;
        _int64Keys[e] = key;
        _values[e] = value;
        _slots[s] = e + 1;
        added = true;
        return value;
    }

    /// <inheritdoc cref="GetOrAdd(in EntityKey, int, out bool)"/>
    public int GetOrAdd(string key, int value, out bool added)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireKeyType(EntityKeyType.String);
        var hash = key.GetHashCode();
        var s = Slot((uint)hash);
        for (; ; s = (s + 1) & _slotMask)
        {
            var entry = _slots[s] - 1;
            if (entry < 0)
            {
                break;
            }

            if (_hashes[entry] == hash && string.Equals(_strings[entry], key))
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
        _strings[e] = key;
        _hashes[e] = hash;
        _values[e] = value;
        _slots[s] = e + 1;
        added = true;
        return value;
    }

    /// <summary>Removes every key, keeping the rented arrays for reuse.</summary>
    public void Clear()
    {
        _slots.AsSpan(0, _slotMask + 1).Clear();
        if (_stringKeys)
        {
            _strings.AsSpan(0, _count).Clear(); // drop the references; the arrays live until the request ends
        }

        _count = 0;
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

    private void Allocate(int slotCount)
    {
        // Entry arrays hold at most half the slot count: the load factor never exceeds 0.5.
        _slots = _memory.RentCleared<int>(slotCount);
        _slotMask = slotCount - 1;
        _shift = 64 - BitOperations.Log2((uint)slotCount);
        _maxCount = slotCount / 2;
        _values = _memory.Rent<int>(_maxCount);
        if (_stringKeys)
        {
            _strings = _memory.Rent<string>(_maxCount);
            _hashes = _memory.Rent<int>(_maxCount);
        }
        else
        {
            _int64Keys = _memory.Rent<long>(_maxCount);
        }
    }

    private void Grow()
    {
        if (_slotMask + 1 > MaxCapacity)
        {
            throw new InvalidOperationException($"An entity key index holds at most {MaxCapacity} keys.");
        }

        var values = _values;
        var int64Keys = _int64Keys;
        var strings = _strings;
        var hashes = _hashes;
        Allocate((_slotMask + 1) * 2);
        values.AsSpan(0, _count).CopyTo(_values);
        if (_stringKeys)
        {
            strings.AsSpan(0, _count).CopyTo(_strings);
            hashes.AsSpan(0, _count).CopyTo(_hashes);
            for (var e = 0; e < _count; e++)
            {
                _slots[EmptySlot(Slot((uint)_hashes[e]))] = e + 1;
            }
        }
        else
        {
            int64Keys.AsSpan(0, _count).CopyTo(_int64Keys);
            for (var e = 0; e < _count; e++)
            {
                _slots[EmptySlot(Slot((ulong)_int64Keys[e]))] = e + 1;
            }
        }
    }

    private void RequireKeyType(EntityKeyType expected)
    {
        if (KeyType != expected)
        {
            throw new InvalidOperationException($"This index holds {KeyType} keys, not {expected}.");
        }
    }
}
