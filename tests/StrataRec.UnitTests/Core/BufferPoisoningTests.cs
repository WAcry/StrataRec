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

    private sealed class KeepingPool : IBufferPool
    {
        public List<Array> Returned { get; } = new();

        public T[] Rent<T>(int minimumLength) => new T[minimumLength];

        public void Return<T>(T[] array, bool clearArray) => Returned.Add(array);
    }
}
