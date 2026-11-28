using System.Text.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Logging;

namespace StrataRec.UnitTests.Logging;

public sealed class LogValueReadTests
{
    public static TheoryData<string, ScalarValue> Values() => new()
    {
        { "bool", ScalarValue.FromBool(true) },
        { "bool", ScalarValue.FromBool(false) },
        { "int32", ScalarValue.FromInt64(-7) },
        { "int64", ScalarValue.FromInt64(long.MaxValue) },
        { "float32", ScalarValue.FromFloat64(0.1f) },
        { "float64", ScalarValue.FromFloat64(Math.PI) },
        { "string", ScalarValue.FromString("ü \"quoted\"") },
        { "string", ScalarValue.FromString(new string('x', 10_000)) },
        { "list<float32,3>", ScalarValue.FromFloat32List(new[] { 0.5f, -1f, 0.1f }) },
        { "list<int32,2>", ScalarValue.FromInt64List(new long[] { 7, -1 }) },
        { "list<bool,1>?", ScalarValue.FromBoolList(new[] { true }) },
        { "list<int64>", ScalarValue.FromInt64List(new long[] { 1, -2, long.MinValue }) },
        { "list<int32>", ScalarValue.FromInt64List(new long[] { int.MaxValue, 0 }) },
        { "list<string>", ScalarValue.FromStringList(new[] { "a", string.Empty }) },
        { "list<bool>", ScalarValue.FromBoolList(new[] { true, false }) },
        { "list<float64>", ScalarValue.FromFloat64List(new[] { 0.1, -1e300 }) },
        { "list<float32>", ScalarValue.FromFloat32List(new[] { 0.1f, 2.5f }) },
        { "list<float64>", ScalarValue.FromFloat64List(Array.Empty<double>()) },
        { "float64?", ScalarValue.Missing },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void WrittenValues_ReadBackEqual(string type, ScalarValue value)
    {
        var read = RoundTrip(value, ColumnType.Parse(type));

        Assert.Equal(value, read);
    }

    [Theory]
    [InlineData("0.10000000149011612", false)] // the exact double of float32 0.1, as WriteValue writes it
    [InlineData("0.1", false)] // the shortest text of float32 0.1, as a batch writes a float32 field
    [InlineData("0.10000000149011612", true)]
    public void AFloat32Value_ReadsBackAsTheFloat_PrintedAsTheFloat(string json, bool bundle)
    {
        var value = Read(json, ColumnType.Float32, nonFiniteAsStrings: bundle);

        Assert.Equal((double)0.1f, value.AsFloat64());
        Assert.Equal((true, "0.1"), (value.IsFloat32, value.ToString()));
    }

    [Theory]
    [InlineData("float32", "0.1", true)] // a bundle keeps what the request saw: a float32 request input is the double it was given
    [InlineData("float64", "0.10000000149011612", true)]
    [InlineData("float64", "0.10000000149011612", false)]
    public void ADoublesText_ReadsBackAsThatDouble_WhereTheEncodingHoldsDoubles(string type, string json, bool bundle)
    {
        var value = Read(json, ColumnType.Parse(type), nonFiniteAsStrings: bundle);

        Assert.Equal(double.Parse(json, System.Globalization.CultureInfo.InvariantCulture), value.AsFloat64());
        Assert.Equal((false, json), (value.IsFloat32, value.ToString()));
    }

    /// <summary>
    /// A decimal just below the midpoint above float.MaxValue names float.MaxValue; through the double it rounds to the
    /// midpoint, which as a float is +∞: a log's float32 is judged on the float it reads as.
    /// </summary>
    [Fact]
    public void ALogsFloat32Text_JustBelowTheOverflowMidpoint_IsFloatMaxValue()
    {
        var value = Read("340282356779733661637539395458142568447", ColumnType.Float32, nonFiniteAsStrings: false);

        Assert.Equal(float.MaxValue, value.AsFloat32());
    }

    [Theory]
    [InlineData(16777217.0)] // halfway between two floats: the text rounds to even
    [InlineData(1.00000005960464477539)] // 1 + 2^-24, halfway too
    [InlineData(3.4028234663852886E+38)] // float.MaxValue
    [InlineData(1.401298464324817E-45)] // float.Epsilon, the smallest subnormal
    public void ALogsFloat32Text_ReadsAsTheFloatItNames(double number)
    {
        var text = number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        var value = Read(text, ColumnType.Float32, nonFiniteAsStrings: false);

        Assert.Equal(float.Parse(text, System.Globalization.CultureInfo.InvariantCulture), value.AsFloat32());
        Assert.True(value.IsFloat32);
    }

    [Fact]
    public void ListElements_AreWrittenAsScalarsOfTheirType()
    {
        Assert.Equal("[1,0]", Write(ScalarValue.FromBoolList(new[] { true, false }), nonFiniteAsStrings: false));
        Assert.Equal("[0.10000000149011612]", Write(ScalarValue.FromFloat32List(new[] { 0.1f }), nonFiniteAsStrings: false));

        var read = Read("[0.10000000149011612, 2]", ColumnType.Parse("list<float32>"), nonFiniteAsStrings: false);
        Assert.True(read.IsFloat32List);
        Assert.Equal("[0.1, 2]", read.ToString());
        Assert.True(Read("[0.1]", ColumnType.Parse("list<float32>"), nonFiniteAsStrings: false).IsFloat32List);
        Assert.False(Read("[0.1]", ColumnType.Parse("list<float32>"), nonFiniteAsStrings: true).IsFloat32List);
    }

    [Fact]
    public void AFloatListHoldingNonFiniteElements_IsNullInLogs_AndKeepsTheNamesInBundles()
    {
        var list = ScalarValue.FromFloat64List(new[] { 1, double.NaN, double.NegativeInfinity });

        Assert.Equal("null", Write(list, nonFiniteAsStrings: false));
        var written = Write(list, nonFiniteAsStrings: true);
        Assert.Equal("[1,\"NaN\",\"-Infinity\"]", written);
        Assert.Equal(list, Read(written, ColumnType.Parse("list<float64>"), nonFiniteAsStrings: true));
    }

    [Fact]
    public void NonFiniteValues_WereWrittenAsNull_AndReadBackMissing()
    {
        Assert.True(RoundTrip(ScalarValue.FromFloat64(double.NaN), ColumnType.Float64).IsMissing);
        Assert.True(RoundTrip(ScalarValue.FromFloat32List(new[] { float.PositiveInfinity }), ColumnType.Parse("list<float32,1>")).IsMissing);
    }

    [Fact]
    public void AFixedLengthList_ReadsBackWhateverLengthWasWritten()
    {
        // A recording holds what a connector wrote, before the type's length was applied where it was stored; a replay
        // reads it back as written and applies the same rule again.
        var read = Read("[1, 2]", ColumnType.Parse("list<float32,4>"), nonFiniteAsStrings: true);

        Assert.Equal(ScalarValue.FromFloat32List(new[] { 1f, 2f }), read);
    }

    [Theory]
    [InlineData(double.NaN, "\"NaN\"")]
    [InlineData(double.PositiveInfinity, "\"Infinity\"")]
    [InlineData(double.NegativeInfinity, "\"-Infinity\"")]
    public void BundleEncoding_KeepsNonFiniteFloats_AsTheirNames(double value, string json)
    {
        var written = Write(ScalarValue.FromFloat64(value), nonFiniteAsStrings: true);

        Assert.Equal(json, written);
        Assert.Equal(ScalarValue.FromFloat64(value), Read(written, ColumnType.Float64, nonFiniteAsStrings: true));
        Assert.Equal(ScalarValue.FromFloat64(value), Read(written, ColumnType.Float32, nonFiniteAsStrings: true));
        Assert.Equal("null", Write(ScalarValue.FromFloat64(value), nonFiniteAsStrings: false));
    }

    [Fact]
    public void BundleEncoding_KeepsNonFiniteEmbeddingElements()
    {
        var vector = ScalarValue.FromFloat32List(new[] { float.PositiveInfinity, 0.5f, float.NaN, float.NegativeInfinity });

        var written = Write(vector, nonFiniteAsStrings: true);

        Assert.Equal("[\"Infinity\",0.5,\"NaN\",\"-Infinity\"]", written);
        Assert.Equal(vector, Read(written, ColumnType.Parse("list<float32,4>"), nonFiniteAsStrings: true));
        Assert.Equal("null", Write(vector, nonFiniteAsStrings: false));
    }

    [Theory]
    [InlineData("float64", "\"NaN\"")]
    [InlineData("list<float32,2>", "[\"Infinity\", 1]")]
    public void LogEncoding_DoesNotAcceptNonFiniteNames(string type, string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(LogBatchJson.TryReadValue(document.RootElement, ColumnType.Parse(type), out _, out _));
        Assert.False(LogBatchJson.TryReadValue(JsonDocument.Parse("\"nan\"").RootElement, ColumnType.Float64, out _, out _, nonFiniteAsStrings: true));
    }

    [Theory]
    [InlineData("int32", "4294967296")]
    [InlineData("int64", "1.5")]
    [InlineData("bool", "2")]
    [InlineData("string", "3")]
    [InlineData("list<float32,2>", "[1, true]")]
    [InlineData("list<int32>", "[1, 4294967296]")]
    [InlineData("list<string>", "[\"a\", 1]")]
    [InlineData("list<bool>", "[1, 2]")]
    [InlineData("list<float64>", "[1, \"x\"]")]
    [InlineData("list<float64>", "[1, \"NaN\"]")]
    public void ValuesThatDoNotFitTheType_AreErrors(string type, string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(LogBatchJson.TryReadValue(document.RootElement, ColumnType.Parse(type), out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    private static string Write(ScalarValue value, bool nonFiniteAsStrings)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            LogBatchJson.WriteValue(writer, value, nonFiniteAsStrings);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static ScalarValue Read(string json, ColumnType type, bool nonFiniteAsStrings)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(LogBatchJson.TryReadValue(document.RootElement, type, out var read, out var error, nonFiniteAsStrings), error);
        return read;
    }

    private static ScalarValue RoundTrip(ScalarValue value, ColumnType type)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            LogBatchJson.WriteValue(writer, value);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        Assert.True(LogBatchJson.TryReadValue(document.RootElement, type, out var read, out var error), error);
        return read;
    }
}
