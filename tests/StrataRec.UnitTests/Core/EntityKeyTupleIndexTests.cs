using StrataRec.Core.Keys;
using StrataRec.Core.Memory;

namespace StrataRec.UnitTests.Core;

[Collection(AllocationMeasurement.Name)]
public class EntityKeyTupleIndexTests
{
    private static EntityKey[] Tuple(long id, string country) => [EntityKey.FromInt64(id), EntityKey.FromString(country)];

    [Fact]
    public void Tuples_AreDeduplicated_InFirstSeenOrder_AndStoredPartMajor()
    {
        var memory = new RequestMemory();
        var index = new EntityKeyTupleIndex(2, 4, memory);

        Assert.Equal(0, index.GetOrAdd(Tuple(7, "US"), 0, out var added));
        Assert.True(added);
        Assert.Equal(1, index.GetOrAdd(Tuple(7, "DE"), 1, out added));
        Assert.Equal(2, index.GetOrAdd(Tuple(8, "US"), 2, out added));
        Assert.Equal(0, index.GetOrAdd(Tuple(7, "US"), 3, out added));
        Assert.False(added);

        Assert.Equal(3, index.Count);
        Assert.Equal(new[] { EntityKey.FromInt64(7), EntityKey.FromInt64(7), EntityKey.FromInt64(8) }, index.PartKeys(0).ToArray());
        Assert.Equal(new[] { EntityKey.FromString("US"), EntityKey.FromString("DE"), EntityKey.FromString("US") }, index.PartKeys(1).ToArray());
        Assert.Equal(EntityKey.FromString("DE"), index.Part(1, 1));
        Assert.True(index.TryGetValue(Tuple(8, "US"), out var value));
        Assert.Equal(2, value);

        Assert.False(index.TryGetValue(Tuple(8, "DE"), out _));
        Assert.False(index.TryGetValue([EntityKey.FromString("7"), EntityKey.FromString("US")], out _));
        Assert.False(index.TryGetValue([EntityKey.FromInt64(0), EntityKey.FromString("US")], out _));
        Assert.Throws<ArgumentException>(() => index.TryGetValue([EntityKey.FromInt64(7)], out _));
        memory.Release();
    }

    [Fact]
    public void Growth_And_Reset_KeepEveryTuple_AndReuseTheArrays()
    {
        var memory = new RequestMemory();
        var index = new EntityKeyTupleIndex(3, 0, memory);
        for (var i = 0; i < 1_000; i++)
        {
            index.GetOrAdd([EntityKey.FromString("s" + (i % 7)), EntityKey.FromInt64(i), EntityKey.FromString(i % 2 == 0 ? "US" : "DE")], i, out _);
        }

        Assert.Equal(1_000, index.Count);
        for (var i = 0; i < 1_000; i++)
        {
            Assert.True(index.TryGetValue([EntityKey.FromString("s" + (i % 7)), EntityKey.FromInt64(i), EntityKey.FromString(i % 2 == 0 ? "US" : "DE")], out var value));
            Assert.Equal(i, value);
            Assert.Equal(EntityKey.FromInt64(i), index.PartKeys(1)[i]);
        }

        index.Reset(2, 100);
        Assert.Equal(0, index.Count);
        Assert.False(index.TryGetValue(Tuple(1, "US"), out _));
        index.GetOrAdd(Tuple(1, "US"), 5, out _);
        index.Reset(4, 10);
        Assert.Equal(4, index.PartCount);
        for (var i = 0; i < 10; i++)
        {
            index.GetOrAdd([EntityKey.FromInt64(i), EntityKey.FromInt64(-i), EntityKey.FromString("a"), EntityKey.FromString("b" + i)], i, out _);
        }

        Assert.Equal(10, index.Count);
        Assert.Equal(EntityKey.FromString("b9"), index.Part(9, 3));
        memory.Release();
    }

    [Fact]
    public void AddingAndLookingUp_AllocatesNothing()
    {
        var memory = new RequestMemory();
        var keys = new EntityKey[2_000][];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = Tuple(i % 1_000, i % 3 == 0 ? "US" : "DE");
        }

        var index = new EntityKeyTupleIndex(2, keys.Length, memory);
        foreach (var key in keys)
        {
            index.GetOrAdd(key, index.Count, out _);
        }

        index.Reset(2, keys.Length);
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var key in keys)
        {
            index.GetOrAdd(key, index.Count, out _);
        }

        foreach (var key in keys)
        {
            index.TryGetValue(key, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        memory.Release();
    }
}
