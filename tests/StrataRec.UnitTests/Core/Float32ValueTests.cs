using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.UnitTests.Core;

public sealed class Float32ValueTests
{
    [Fact]
    public void AFloat32Value_PrintsAsTheFloat_AndIsTheDoubleItWidensTo()
    {
        var single = ScalarValue.FromFloat32(0.1f);
        var widened = ScalarValue.FromFloat64(0.1f);

        Assert.Equal(("0.1", "0.10000000149011612"), (single.ToString(), widened.ToString()));
        Assert.Equal((double)0.1f, single.AsFloat64());
        Assert.Equal(widened, single);
        Assert.Equal(widened.GetHashCode(), single.GetHashCode());
        Assert.Equal((true, false), (single.IsFloat32, widened.IsFloat32));
        Assert.False(ScalarValue.FromInt64(1).IsFloat32);
        Assert.False(ScalarValue.Missing.IsFloat32);
    }

    [Fact]
    public void AFloat32ColumnsRow_IsAFloat32Value_AndAFloat64ColumnsRowIsNot()
    {
        var memory = new RequestMemory();
        var floats = new ScalarColumn<float>(ColumnType.Float32, 1, memory);
        var doubles = new ScalarColumn<double>(ColumnType.Float64, 1, memory);
        floats.Set(0, 0.3291833f);
        doubles.Set(0, 0.3291833f);

        Assert.Equal("0.3291833", ColumnValues.Get(floats, 0).ToString());
        Assert.Equal("0.32918331027030945", ColumnValues.Get(doubles, 0).ToString());
    }

    [Fact]
    public void ConvertedToFloat64_ItPrintsAsTheDouble_ConvertedToFloat32ItStaysAFloat()
    {
        var single = ScalarValue.FromFloat32(0.1f);

        Assert.True(single.TryConvert(ColumnType.Float64, out var asDouble));
        Assert.True(single.TryConvert(ColumnType.Parse("float32?"), out var asSingle));

        Assert.Equal((false, "0.10000000149011612"), (asDouble.IsFloat32, asDouble.ToString()));
        Assert.Equal((true, "0.1"), (asSingle.IsFloat32, asSingle.ToString()));
        Assert.Equal(single, asDouble);
    }
}
