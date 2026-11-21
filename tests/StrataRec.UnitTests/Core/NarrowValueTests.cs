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
        // A double, narrowed, is what a float32 column would store.
        Assert.Equal(0.1f, ScalarValue.FromFloat64(0.1).AsFloat32());
        Assert.Equal(3f, ScalarValue.FromInt64(3).AsFloat32()); // an integer converts, as AsFloat64 converts it
        Assert.True(float.IsNaN(ScalarValue.FromFloat64(double.NaN).AsFloat32()));
        Assert.Throws<InvalidOperationException>(() => ScalarValue.FromString("x").AsFloat32());
    }

    /// <summary>
    /// A value converted to a float32 type (a float32 request input, a float32 default or parameter) is a float32 value: the
    /// nearest float, so an expression computes with the same number a model input, a log or a result of the field gets.
    /// It was the double given (0.1 stayed 0.1 for expressions, and became 0.1f only where it left the engine).
    /// </summary>
    [Fact]
    public void ConvertingToFloat32_RoundsToTheNearestFloat_ScalarsListsAndIntegers()
    {
        Assert.True(ScalarValue.FromFloat64(0.1).TryConvert(ColumnType.Float32, out var scalar));
        Assert.True(scalar.IsFloat32);
        Assert.Equal((double)0.1f, scalar.AsFloat64());

        Assert.True(ScalarValue.FromInt64(16_777_217).TryConvert(ColumnType.Float32, out var integer)); // 2^24 + 1: no float
        Assert.Equal((16_777_216.0, true), (integer.AsFloat64(), integer.IsFloat32));

        Assert.True(ScalarValue.FromFloat64List([0.1, 0.2]).TryConvert(ColumnType.List(StorageKind.Float32, fixedLength: 2), out var list));
        Assert.True(list.IsFloat32List);
        Assert.Equal([(double)0.1f, (double)0.2f], list.AsFloat64List().ToArray());

        Assert.True(ScalarValue.FromInt64List([16_777_217]).TryConvert(ColumnType.List(StorageKind.Float32), out var integers));
        Assert.Equal([16_777_216.0], integers.AsFloat64List().ToArray());

        Assert.True(ScalarValue.FromFloat64(0.1).TryConvert(ColumnType.Float64, out var wide)); // float64: the double as given
        Assert.Equal((0.1, false), (wide.AsFloat64(), wide.IsFloat32));
        Assert.False(ScalarValue.FromFloat64(1e39).TryConvert(ColumnType.Float32, out _)); // beyond the float32 range
    }

    /// <summary>An integer list converted to list&lt;float64&gt; keeps every integer a double holds: not rounded through float.</summary>
    [Fact]
    public void AnIntegerListConvertedToFloat64_KeepsItsDoubles()
    {
        Assert.True(ScalarValue.FromInt64List([16_777_217, -16_777_217]).TryConvert(ColumnType.List(StorageKind.Float64), out var list));

        Assert.Equal([16_777_217.0, -16_777_217.0], list.AsFloat64List().ToArray());
        Assert.False(list.IsFloat32List);
    }

    /// <summary>
    /// An integer converted to float32 goes through its double, as one written to a float32 column does and as a replay
    /// reads the recorded integer back: a direct conversion can round to the other float.
    /// </summary>
    [Fact]
    public void AnIntegerConvertedToFloat32_RoundsThroughItsDouble()
    {
        const long Value = 9_007_199_791_611_905; // the direct conversion and the one through the double differ
        Assert.NotEqual((float)Value, (float)(double)Value);

        Assert.True(ScalarValue.FromInt64(Value).TryConvert(ColumnType.Float32, out var scalar));
        Assert.True(ScalarValue.FromInt64List([Value]).TryConvert(ColumnType.List(StorageKind.Float32), out var list));

        Assert.Equal((double)(float)(double)Value, scalar.AsFloat64());
        Assert.Equal([(double)(float)(double)Value], list.AsFloat64List().ToArray());
        Assert.True(ScalarValue.FromFloat64((double)Value).TryConvert(ColumnType.Float32, out var replayed)); // the recorded integer, read as a double
        Assert.Equal(scalar, replayed);
    }

    /// <summary>A list whose elements are floats already converts to list&lt;float32&gt; without copying them.</summary>
    [Fact]
    public void AListOfFloatsAlready_ConvertsToFloat32_WithoutACopy()
    {
        var doubles = new[] { 0.5, (double)0.1f };

        Assert.True(ScalarValue.FromFloat64List(doubles).TryConvert(ColumnType.List(StorageKind.Float32), out var list));

        Assert.True(list.IsFloat32List);
        Assert.True(System.Runtime.CompilerServices.Unsafe.AreSame(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(list.AsFloat64List()), ref doubles[0]));
    }
}
