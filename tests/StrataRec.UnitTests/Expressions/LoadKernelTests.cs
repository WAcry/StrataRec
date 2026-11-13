using StrataRec.Expressions.Vectorized;

namespace StrataRec.UnitTests.Expressions;

public class LoadKernelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1000)]
    public void Widen_And_GatherWiden_MatchScalarConversion(int n)
    {
        var random = new Random(n);
        var floats = new float[n + 3];
        var ints = new int[n + 3];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = i % 11 == 0 ? float.NaN : (float)((random.NextDouble() - 0.5) * 1e6);
            ints[i] = random.Next(int.MinValue, int.MaxValue);
        }

        floats[0] = n > 0 ? -0.0f : floats[0];
        var map = Enumerable.Range(0, n).Select(_ => random.Next(floats.Length)).ToArray();

        var d = new double[n];
        var l = new long[n];
        LoadKernels.Widen(floats.AsSpan(0, n), d);
        LoadKernels.Widen(ints.AsSpan(0, n), l);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(floats[i]), BitConverter.DoubleToInt64Bits(d[i]));
            Assert.Equal(ints[i], l[i]);
        }

        LoadKernels.GatherWiden(floats, map, floats.Length, d);
        LoadKernels.GatherWiden(ints, map, ints.Length, l);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(floats[map[i]]), BitConverter.DoubleToInt64Bits(d[i]));
            Assert.Equal(ints[map[i]], l[i]);
        }
    }

    [Fact]
    public void AllInRange_RejectsNegativeAndOutOfRangeRowsInVectorAndTail()
    {
        var rows = Enumerable.Range(0, 21).ToArray();
        Assert.True(LoadKernels.AllInRange(rows, 21));
        Assert.False(LoadKernels.AllInRange(rows, 20));
        foreach (var at in new[] { 0, 5, 9, 16, 20 })
        {
            var copy = (int[])rows.Clone();
            copy[at] = -1;
            Assert.False(LoadKernels.AllInRange(copy, 21));
        }

        Assert.True(LoadKernels.AllInRange(ReadOnlySpan<int>.Empty, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(130)]
    [InlineData(1000)]
    public void GatherBits_MatchesNaiveGather_AndClearsTailBits(int n)
    {
        var random = new Random(n);
        const int physical = 1500;
        var source = new ulong[(physical + 63) / 64];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = ((ulong)random.NextInt64() << 1) ^ (ulong)random.Next();
        }

        var map = Enumerable.Range(0, n).Select(_ => random.Next(physical)).ToArray();
        var destination = Enumerable.Repeat(ulong.MaxValue, (n + 63) / 64).ToArray();
        LoadKernels.GatherBits(source, map, physical, destination);
        for (var i = 0; i < destination.Length * 64; i++)
        {
            var expected = i < n && ((source[map[i] >> 6] >> map[i]) & 1) != 0;
            Assert.Equal(expected, ((destination[i >> 6] >> i) & 1) != 0);
        }
    }

    [Fact]
    public void Gathers_RejectBuffersShorterThanThePhysicalRows()
    {
        var rows = new[] { 0, 1 };
        Assert.Throws<InvalidOperationException>(() => LoadKernels.Gather<double>(new double[4], rows, 5, new double[2]));
        Assert.Throws<InvalidOperationException>(() => LoadKernels.GatherBits(new ulong[1], rows, 65, new ulong[1]));
    }
}
