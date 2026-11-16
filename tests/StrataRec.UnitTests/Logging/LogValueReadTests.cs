using System.Text.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Logging;

namespace StrataRec.UnitTests.Logging;

/// <summary><see cref="LogBatchJson.TryReadValue"/> is the inverse of <see cref="LogBatchJson.WriteValue"/> given the type.</summary>
public sealed class LogValueReadTests
{
    public static TheoryData<string, ScalarValue> Values() => new()
    {
        { "bool", ScalarValue.FromBool(true) },
        { "bool", ScalarValue.FromBool(false) },
        { "int32", ScalarValue.FromInt64(-7) },
        { "int64", ScalarValue.FromInt64(long.MaxValue) },
        { "float32", ScalarValue.FromFloat64(0.1f) }, // the promoted float32 survives exactly
        { "float64", ScalarValue.FromFloat64(Math.PI) },
        { "string", ScalarValue.FromString("ü \"quoted\"") },
        { "text", ScalarValue.FromText("long text") },
        { "vec<float32,3>", ScalarValue.FromVector(new[] { 0.5f, -1f, 0.1f }) },
        { "list<int64>", ScalarValue.FromInt64List(new long[] { 1, -2, long.MinValue }) },
        { "list<int32>", ScalarValue.FromInt64List(new long[] { int.MaxValue, 0 }) },
        { "list<string>", ScalarValue.FromStringList(new[] { "a", string.Empty }) },
        { "list<bool>", ScalarValue.FromBoolList(new[] { true, false }) },
        { "list<float64>", ScalarValue.FromFloat64List(new[] { 0.1, -1e300 }) },
        { "list<float32>", ScalarValue.FromFloat32List(new[] { 0.1f, 2.5f }) }, // promoted float32 elements survive exactly
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
    [InlineData("float32", "0.10000000149011612", true, "0.1")] // the exact double of float32 0.1, as logs write it
    [InlineData("float32", "0.1", false, "0.1")] // not a float32: kept as the double, printed as one
    [InlineData("float64", "0.10000000149011612", false, "0.10000000149011612")]
    public void AFloat32ColumnsValue_ReadsBackAsTheSameDouble_PrintedAsTheFloat(string type, string json, bool float32, string text)
    {
        using var document = JsonDocument.Parse(json);

        Assert.True(LogBatchJson.TryReadValue(document.RootElement, ColumnType.Parse(type), out var value, out _));
        Assert.Equal(double.Parse(json, System.Globalization.CultureInfo.InvariantCulture), value.AsFloat64()); // the value is not narrowed
        Assert.Equal((float32, text), (value.IsFloat32, value.ToString()));
    }

    [Fact]
    public void ListElements_AreWrittenAsScalarsOfTheirType()
    {
        Assert.Equal("[1,0]", Write(ScalarValue.FromBoolList(new[] { true, false }), nonFiniteAsStrings: false)); // as a bool column is
        Assert.Equal("[0.10000000149011612]", Write(ScalarValue.FromFloat32List(new[] { 0.1f }), nonFiniteAsStrings: false));

        var read = Read("[0.10000000149011612, 2]", ColumnType.Parse("list<float32>"), nonFiniteAsStrings: false);
        Assert.True(read.IsFloat32List); // printed as the floats it holds
        Assert.Equal("[0.1, 2]", read.ToString());
        Assert.False(Read("[0.1]", ColumnType.Parse("list<float32>"), nonFiniteAsStrings: false).IsFloat32List); // not a float32
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
        Assert.True(RoundTrip(ScalarValue.FromVector(new[] { float.PositiveInfinity }), ColumnType.Vector(1)).IsMissing);
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
        Assert.Equal("null", Write(ScalarValue.FromFloat64(value), nonFiniteAsStrings: false)); // logs are unchanged
    }

    [Fact]
    public void BundleEncoding_KeepsNonFiniteVectorComponents()
    {
        var vector = ScalarValue.FromVector(new[] { float.PositiveInfinity, 0.5f, float.NaN, float.NegativeInfinity });

        var written = Write(vector, nonFiniteAsStrings: true);

        Assert.Equal("[\"Infinity\",0.5,\"NaN\",\"-Infinity\"]", written);
        Assert.Equal(vector, Read(written, ColumnType.Vector(4), nonFiniteAsStrings: true));
        Assert.Equal("null", Write(vector, nonFiniteAsStrings: false));
    }

    [Theory]
    [InlineData("float64", "\"NaN\"")]
    [InlineData("vec<float32,2>", "[\"Infinity\", 1]")]
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
    [InlineData("vec<float32,2>", "[1, 2, 3]")]
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
