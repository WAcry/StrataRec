using StrataRec.Core.Memory;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// Test runs poison returned buffers (design 4.8), in Release as in Debug: the guard against one request reading another's
/// data must run where CI runs, which is the Release build.
/// </summary>
public sealed class BufferPoisoningTests
{
    [Fact]
    public void TestRuns_TurnPoisoningOn()
    {
        Assert.True(RequestMemory.PoisonReturnedBuffers, "every test project sets the StrataRec.PoisonReturnedBuffers runtime switch");
    }

    [Fact]
    public void AReturnedBuffer_IsFilledWithThePoisonByte_AndOneOfReferences_IsLeftToThePool()
    {
        var pool = new KeepingPool();
        var memory = new RequestMemory(pool);
        var numbers = memory.Rent<long>(4);
        numbers.AsSpan().Fill(42);
        var strings = memory.Rent<string>(2);
        strings[0] = "kept";

        memory.Release();

        Assert.All(numbers, n => Assert.Equal(unchecked((long)0xCDCDCDCDCDCDCDCD), n));
        Assert.Equal(2, pool.Returned.Count);
        Assert.Contains((Array)strings, pool.Returned); // references are cleared by the pool, not poisoned
    }

    /// <summary>
    /// Audit A6: a scratch buffer goes back when the computation that rented it is done, poisoned like the rest, and is
    /// not on the ledger: the request's end does not give it back again, and it is not in the rented bytes.
    /// </summary>
    [Fact]
    public void AScratchBuffer_GoesBackAtOnce_Poisoned_AndNotAgainWithTheRequest()
    {
        var pool = new KeepingPool();
        var memory = new RequestMemory(pool);
        var held = memory.Rent<int>(8);
        var scratch = memory.RentScratch<long>(4);
        scratch.AsSpan().Fill(42);

        Assert.Equal(8 * sizeof(int), memory.RentedBytes);
        memory.ReturnScratch(scratch);
        Assert.All(scratch, n => Assert.Equal(unchecked((long)0xCDCDCDCDCDCDCDCD), n));
        Assert.Equal(new Array[] { scratch }, pool.Returned);

        memory.Release();
        Assert.Equal(new Array[] { scratch, held }, pool.Returned);
    }

    /// <summary>
    /// What the pool allocates for a request's rents is counted (metric stratarec.request.memory.allocated): an array it
    /// had to create, ledger or scratch, and nothing for one it had.
    /// </summary>
    [Fact]
    public void WhatThePoolAllocates_IsCounted_AndAnArrayItHadIsNot()
    {
        var kept = new long[1024];
        var reusing = new RequestMemory(new ReusingPool(kept));
        reusing.Rent<long>(1024);
        reusing.RentScratch<long>(1024);
        Assert.Equal(0, reusing.PoolAllocatedBytes);

        var creating = new RequestMemory(new KeepingPool());
        creating.Rent<long>(1024);
        creating.RentScratch<int>(1024);
        Assert.InRange(creating.PoolAllocatedBytes, (1024 * 8) + (1024 * 4), (1024 * 8) + (1024 * 4) + 256); // the arrays and their headers
    }

    private sealed class ReusingPool(long[] array) : IBufferPool
    {
        public T[] Rent<T>(int minimumLength) => (T[])(object)array;

        public void Return<T>(T[] array, bool clearArray)
        {
        }
    }

    private sealed class KeepingPool : IBufferPool
    {
        public List<Array> Returned { get; } = new();

        public T[] Rent<T>(int minimumLength) => new T[minimumLength];

        public void Return<T>(T[] array, bool clearArray) => Returned.Add(array);
    }
}
