using StrataRec.Core.Keys;
using StrataRec.Core.Memory;

namespace StrataRec.UnitTests.Core;

[Collection(AllocationMeasurement.Name)]
public class EntityKeyIndexTests
{
    // Mirrors EntityKeyIndex's home slot (Fibonacci hashing) so tests can build keys that collide on purpose. If the
    // hash changes these keys merely stop colliding; every assertion below still holds.
    private const ulong Fibonacci = 0x9E3779B97F4A7C15UL;

    private static int HomeSlot(ulong hash, int slotCount) => (int)((hash * Fibonacci) >> (64 - System.Numerics.BitOperations.Log2((uint)slotCount)));

    [Fact]
    public void Int64Keys_AddLookupAndAbsent()
    {
        var memory = new RequestMemory();
        var index = new EntityKeyIndex(EntityKeyType.Int64, 8, memory);
        long[] keys = { 0, -1, 1, long.MinValue, long.MaxValue, 10042, 1L << 40, -(1L << 62) };
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(index.TryAdd(keys[i], i * 10));
        }

        Assert.Equal(keys.Length, index.Count);
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(index.TryGetValue(keys[i], out var value));
            Assert.Equal(i * 10, value);
            Assert.True(index.TryGetValue(EntityKey.FromInt64(keys[i]), out value));
            Assert.Equal(i * 10, value);
            Assert.Equal(EntityKey.FromInt64(keys[i]), index.KeyAt(i));
        }

        Assert.Equal(keys, index.Int64Keys.ToArray()); // insertion order
        foreach (var absent in new long[] { 2, -2, 10043, long.MinValue + 1, long.MaxValue - 1 })
        {
            Assert.False(index.TryGetValue(absent, out _));
        }

        memory.Release();
    }

    [Fact]
    public void TryAdd_KeepsTheFirstValue_GetOrAddReportsWhetherItAdded()
    {
        var index = new EntityKeyIndex(EntityKeyType.Int64, 4, new RequestMemory());

        Assert.True(index.TryAdd(7, 1));
        Assert.False(index.TryAdd(7, 2));
        Assert.Equal(1, index.GetOrAdd(EntityKey.FromInt64(7), 3, out var added));
        Assert.False(added);
        Assert.Equal(4, index.GetOrAdd(EntityKey.FromInt64(8), 4, out added));
        Assert.True(added);
        Assert.Equal(2, index.Count);
        Assert.True(index.TryGetValue(7, out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void CollidingInt64Keys_AreAllFound_IncludingAcrossTheTableEnd()
    {
        // Capacity 8 → 16 slots, at most 8 keys. Eight keys whose home slot is the last slot fill the table to its load
        // limit with one probe chain that wraps around to slot 0.
        const int Slots = 16;
        var colliding = new List<long>();
        var absentColliding = new List<long>();
        for (long k = 0; absentColliding.Count < 4; k++)
        {
            if (HomeSlot((ulong)k, Slots) == Slots - 1)
            {
                (colliding.Count < 8 ? colliding : absentColliding).Add(k);
            }
        }

        var index = new EntityKeyIndex(EntityKeyType.Int64, 8, new RequestMemory());
        for (var i = 0; i < colliding.Count; i++)
        {
            Assert.True(index.TryAdd(colliding[i], i));
        }

        for (var i = 0; i < colliding.Count; i++)
        {
            Assert.True(index.TryGetValue(colliding[i], out var value), $"key {colliding[i]}");
            Assert.Equal(i, value);
            Assert.False(index.TryAdd(colliding[i], -1));
        }

        foreach (var absent in absentColliding)
        {
            Assert.False(index.TryGetValue(absent, out _)); // walks the whole chain and stops at an empty slot
        }

        // The ninth colliding key grows the table; everything stays reachable.
        Assert.True(index.TryAdd(absentColliding[0], 8));
        for (var i = 0; i < colliding.Count; i++)
        {
            Assert.True(index.TryGetValue(colliding[i], out var value));
            Assert.Equal(i, value);
        }

        Assert.True(index.TryGetValue(absentColliding[0], out var last));
        Assert.Equal(8, last);
    }

    [Fact]
    public void Growth_FromZeroCapacity_KeepsValuesAndInsertionOrder()
    {
        var memory = new RequestMemory();
        var index = new EntityKeyIndex(EntityKeyType.Int64, 0, memory);
        var random = new Random(7);
        var keys = Enumerable.Range(0, 20_000).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)).Distinct().ToArray();
        var rentedBefore = memory.RentedBytes;

        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(index.TryAdd(keys[i], i));
        }

        Assert.True(memory.RentedBytes > rentedBefore); // grew by renting, not allocating
        Assert.Equal(keys.Length, index.Count);
        Assert.Equal(keys, index.Int64Keys.ToArray());
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(index.TryGetValue(keys[i], out var value));
            Assert.Equal(i, value);
        }

        var present = keys.ToHashSet();
        foreach (var absent in Enumerable.Range(0, 1000).Select(_ => random.NextInt64()).Where(k => !present.Contains(k)))
        {
            Assert.False(index.TryGetValue(absent, out _));
        }

        memory.Release();
    }

    [Fact]
    public void SequentialAndStridedIds_DoNotDegrade()
    {
        // Typical catalog ids are sequential or share low bits; all must be found with one index of the right size.
        foreach (var stride in new long[] { 1, 16, 1024, 1L << 32 })
        {
            var index = new EntityKeyIndex(EntityKeyType.Int64, 5000, new RequestMemory());
            for (var i = 0; i < 5000; i++)
            {
                Assert.True(index.TryAdd(i * stride, i));
            }

            for (var i = 0; i < 5000; i++)
            {
                Assert.True(index.TryGetValue(i * stride, out var value));
                Assert.Equal(i, value);
            }

            Assert.False(index.TryGetValue(5000 * stride, out _));
        }
    }

    [Fact]
    public void StringKeys_UseOrdinalEquality()
    {
        var index = new EntityKeyIndex(EntityKeyType.String, 8, new RequestMemory());
        string[] keys = { "a", "A", "ß", "ss", "", "item-10042", "a\0", "é" };
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(index.TryAdd(keys[i], i));
        }

        for (var i = 0; i < keys.Length; i++)
        {
            // A different instance with the same characters is the same key.
            Assert.True(index.TryGetValue(new string(keys[i].AsSpan()), out var value));
            Assert.Equal(i, value);
            Assert.True(index.TryGetValue(EntityKey.FromString(keys[i]), out value));
            Assert.Equal(i, value);
        }

        Assert.Equal(keys, index.StringKeys.ToArray());
        Assert.Equal(EntityKey.FromString("ß"), index.KeyAt(2));
        foreach (var absent in new[] { "b", "SS", "é", "item-10043", " a" })
        {
            Assert.False(index.TryGetValue(absent, out _));
        }
    }

    [Fact]
    public void CollidingStringKeys_AndGrowth_AreAllFound()
    {
        // string.GetHashCode is randomized per process but stable within it, so colliding keys can be found here.
        const int Slots = 16;
        var colliding = new List<string>();
        for (var i = 0; colliding.Count < 12; i++)
        {
            var key = "k" + i;
            if (HomeSlot((uint)key.GetHashCode(), Slots) == Slots - 1)
            {
                colliding.Add(key);
            }
        }

        var index = new EntityKeyIndex(EntityKeyType.String, 8, new RequestMemory());
        for (var i = 0; i < colliding.Count; i++)
        {
            Assert.True(index.TryAdd(colliding[i], i)); // the ninth grows the table
        }

        for (var i = 0; i < 5000; i++)
        {
            index.TryAdd("bulk-" + i, 100 + i);
        }

        for (var i = 0; i < colliding.Count; i++)
        {
            Assert.True(index.TryGetValue(colliding[i], out var value));
            Assert.Equal(i, value);
        }

        for (var i = 0; i < 5000; i++)
        {
            Assert.True(index.TryGetValue("bulk-" + i, out var value));
            Assert.Equal(100 + i, value);
        }

        Assert.Equal(colliding.Count + 5000, index.Count);
        Assert.False(index.TryGetValue("bulk-5000", out _));
    }

    [Fact]
    public void KeysOfTheOtherType_AreNeverFound_AndCannotBeAdded()
    {
        var ints = new EntityKeyIndex(EntityKeyType.Int64, 4, new RequestMemory());
        ints.TryAdd(0, 1);
        ints.TryAdd(5, 2);
        Assert.False(ints.TryGetValue(EntityKey.FromString("5"), out _));
        Assert.False(ints.TryGetValue("0", out _));
        Assert.Throws<InvalidOperationException>(() => ints.TryAdd(EntityKey.FromString("x"), 3));
        Assert.Throws<InvalidOperationException>(() => ints.StringKeys.ToArray());

        var strings = new EntityKeyIndex(EntityKeyType.String, 4, new RequestMemory());
        strings.TryAdd("5", 1);
        Assert.False(strings.TryGetValue(EntityKey.FromInt64(5), out _));
        Assert.False(strings.TryGetValue(0L, out _)); // EntityKey default: int64 0
        Assert.Throws<InvalidOperationException>(() => strings.TryAdd(5L, 3));
        Assert.Throws<InvalidOperationException>(() => strings.Int64Keys.ToArray());
    }

    [Fact]
    public void Clear_RemovesEveryKey_AndTheIndexIsReusable()
    {
        var index = new EntityKeyIndex(EntityKeyType.String, 16, new RequestMemory());
        for (var i = 0; i < 16; i++)
        {
            index.TryAdd("x" + i, i);
        }

        index.Clear();

        Assert.Equal(0, index.Count);
        Assert.False(index.TryGetValue("x3", out _));
        Assert.True(index.TryAdd("x3", 42));
        Assert.True(index.TryGetValue("x3", out var value));
        Assert.Equal(42, value);
        Assert.Equal(new[] { "x3" }, index.StringKeys.ToArray());
    }

    [Fact]
    public void Reset_EmptiesTheIndex_ForEitherKeyType_AndAnyCapacity()
    {
        var index = new EntityKeyIndex(EntityKeyType.Int64, 4, new RequestMemory());
        index.TryAdd(7L, 0);

        // To strings, larger than the slot table: every string lands without growing past the asked capacity.
        index.Reset(EntityKeyType.String, 100);
        Assert.Equal(EntityKeyType.String, index.KeyType);
        Assert.Equal(0, index.Count);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(index.TryAdd("k" + i, i));
        }

        Assert.Equal(Enumerable.Range(0, 100).Select(i => "k" + i), index.StringKeys.ToArray());

        // Back to int64 within the same slot table: the int64 arrays were rented for 4 keys only, so they are rented again.
        index.Reset(EntityKeyType.Int64, 50);
        Assert.False(index.TryGetValue(7L, out _));
        for (var i = 0; i < 50; i++)
        {
            Assert.True(index.TryAdd(i * 1000L, i));
        }

        Assert.True(index.TryGetValue(49_000L, out var value));
        Assert.Equal(49, value);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => i * 1000L), index.Int64Keys.ToArray());

        // Smaller again: the larger table is kept, and it is empty.
        index.Reset(EntityKeyType.String, 1);
        Assert.False(index.TryGetValue("k3", out _));
        Assert.True(index.TryAdd("k3", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Reset(EntityKeyType.Int64, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Reset((EntityKeyType)0, 1));
    }

    [Fact]
    public void Reset_BackToStrings_ChecksEveryStringArray_WhenThePoolRoundsThemDifferently()
    {
        // A pool may hand out more than asked, differently per element type: here string arrays come back with at least
        // 64 entries while the hash array (int) is exact. After the index grew for int64 keys, switching back to strings
        // must not take the long string array as proof that the hash array fits too (review 14).
        var index = new EntityKeyIndex(EntityKeyType.String, 0, new RequestMemory(new StringRoundingPool(64)));
        Assert.True(index.TryAdd("first", 0));

        index.Reset(EntityKeyType.Int64, 32);
        Assert.True(index.TryAdd(1L, 1));

        index.Reset(EntityKeyType.String, 32);
        for (var i = 0; i < 32; i++)
        {
            Assert.True(index.TryAdd("k" + i, i));
        }

        for (var i = 0; i < 32; i++)
        {
            Assert.True(index.TryGetValue("k" + i, out var value));
            Assert.Equal(i, value);
        }

        Assert.Equal(32, index.Count);
    }

    /// <summary>Rents string arrays of at least <c>stringLength</c> entries and every other array at the exact length.</summary>
    private sealed class StringRoundingPool(int stringLength) : IBufferPool
    {
        public T[] Rent<T>(int minimumLength) =>
            new T[typeof(T) == typeof(string) ? Math.Max(minimumLength, stringLength) : Math.Max(1, minimumLength)];

        public void Return<T>(T[] array, bool clearArray)
        {
        }
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        var memory = new RequestMemory();
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntityKeyIndex(EntityKeyType.Int64, -1, memory));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntityKeyIndex(EntityKeyType.Int64, EntityKeyIndex.MaxCapacity + 1, memory));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntityKeyIndex((EntityKeyType)0, 4, memory));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntityKeyIndex(EntityKeyType.Int64, 4, memory).KeyAt(0));
        Assert.Throws<ArgumentNullException>(() => new EntityKeyIndex(EntityKeyType.String, 4, memory).TryAdd((string)null!, 0));
    }

    [Fact]
    public void BuildingAndProbing_DoNotAllocatePerKey()
    {
        const int Keys = 10_000;
        var strings = Enumerable.Range(0, Keys).Select(i => "item-" + i).ToArray();
        var allocated = long.MaxValue;

        // Best of three rounds: the first rents fill the array pool; a per-key allocation shows up in every round.
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var memory = new RequestMemory();
            var ints = new EntityKeyIndex(EntityKeyType.Int64, Keys, memory);
            var text = new EntityKeyIndex(EntityKeyType.String, 0, memory); // grows several times
            for (var i = 0; i < Keys; i++)
            {
                ints.TryAdd(i * 7919L, i);
                text.TryAdd(strings[i], i);
            }

            for (var i = 0; i < Keys; i++)
            {
                ints.TryGetValue(i * 7919L, out _);
                ints.TryGetValue(-i - 1, out _);
                text.TryGetValue(strings[i], out _);
            }

            memory.Release();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Only the request memory and the two index objects: a few hundred bytes, independent of the key count.
        Assert.True(allocated < 1024, $"building and probing {Keys} keys allocated {allocated} bytes");
    }
}
