using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// A reader that knows a value's declared type (a result column, a log context field) narrows the promoted value to it:
/// <see cref="ScalarValue.AsInt32"/> is exact for every value of an int32 type and refuses one that would wrap;
/// <see cref="ScalarValue.AsFloat32"/> is exact for a value read from float32 storage.
/// </summary>
public sealed class NarrowValueTests
{
    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void AsInt32_IsTheInt32_ForEveryValueInRange(int value) =>
        Assert.Equal(value, ScalarValue.FromInt64(value).AsInt32());

    [Theory]
    [InlineData(int.MaxValue + 1L)]
    [InlineData(int.MinValue - 1L)]
    [InlineData(1L << 40)]
    public void AsInt32_RefusesAValueThatWouldWrap(long value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ScalarValue.FromInt64(value).AsInt32());

        Assert.Contains("outside the int32 range", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AsInt32_RefusesAnotherKind()
    {
        Assert.Throws<InvalidOperationException>(() => ScalarValue.FromFloat64(1).AsInt32());
        Assert.Throws<InvalidOperationException>(() => ScalarValue.Missing.AsInt32());
    }

    [Fact]
    public void AnInt32ColumnsRow_ReadsBackExactly()
    {
        var column = new ScalarColumn<int>(ColumnType.Int32, 1, new RequestMemory());
        column.Set(0, int.MinValue);

        Assert.Equal(int.MinValue, ColumnValues.Get(column, 0).AsInt32());
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(float.MaxValue)]
    [InlineData(float.Epsilon)] // the smallest subnormal
    [InlineData(-3.4028235e38f)]
    public void AsFloat32_IsExact_ForAValueReadFromFloat32Storage(float value)
    {
        var column = new ScalarColumn<float>(ColumnType.Float32, 1, new RequestMemory());
        column.Set(0, value);

        var read = ColumnValues.Get(column, 0);

        Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(read.AsFloat32()));
        Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(ScalarValue.FromFloat64(value).AsFloat32()));
    }

    [Fact]
    public void AsFloat32_RoundsADoubleToTheFloatStorageWouldHold()
    {
        // A float32 request input holds the double it was given; narrowed, it is what a float32 column would store.
        Assert.Equal(0.1f, ScalarValue.FromFloat64(0.1).AsFloat32());
        Assert.Equal(3f, ScalarValue.FromInt64(3).AsFloat32()); // an integer converts, as AsFloat64 converts it
        Assert.True(float.IsNaN(ScalarValue.FromFloat64(double.NaN).AsFloat32()));
        Assert.Throws<InvalidOperationException>(() => ScalarValue.FromString("x").AsFloat32());
    }
}
