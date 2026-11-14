using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// <see cref="ConfigValues"/>: a default, parameter value, enum value, table entry or request input of the wrong kind is
/// told in the words of the JSON written, as the end of a sentence whose subject the caller names.
/// </summary>
public class ConfigValuesTests
{
    [Theory]
    [InlineData("\"1001\"", "int64", "must be an integer (int64), got the string \"1001\"")]
    [InlineData("1.5", "int64", "must be an integer (int64), got the number 1.5")]
    [InlineData("null", "int64", "must be an integer (int64), got null")]
    [InlineData("\"0.02\"", "float32", "must be a number (float32), got the string \"0.02\"")]
    [InlineData("1", "bool", "must be true or false (bool), got the number 1")]
    [InlineData("3", "string", "must be a string, got the number 3")]
    [InlineData("{}", "text", "must be a string (text), got an object")]
    [InlineData("\"1,2\"", "list<int64>", "must be an array of integers (list<int64>), got the string \"1,2\"")]
    [InlineData("[1, \"x\"]", "list<int64>", "must be an array of integers (list<int64>); element 1 is the string \"x\"")]
    [InlineData("[1.5]", "list<int32>", "must be an array of integers (list<int32>); element 0 is the number 1.5")]
    [InlineData("[\"a\", null]", "list<string>", "must be an array of strings (list<string>); element 1 is null")]
    [InlineData("[1, 2, 3]", "vec<float32,4>", "must be an array of 4 numbers (vec<float32,4>), got 3 elements")]
    [InlineData("[1, true, 3, 4]", "vec<float32,4>", "must be an array of 4 numbers (vec<float32,4>); element 1 is true")]
    public void AValueOfTheWrongKind_SaysWhatItMustBeAndWhatItIs(string json, string type, string error)
    {
        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out _, out var actual));

        Assert.Equal(error, actual);
    }

    [Theory]
    [InlineData("3.0", "int64", 3L)] // an integral number written with a fraction is an integer
    [InlineData("3e2", "int64", 300L)]
    public void AnIntegralNumber_IsAnInteger(string json, string type, long expected)
    {
        Assert.True(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out var value, out _));

        Assert.Equal(expected, value.AsInt64());
    }

    [Fact]
    public void NullIsMissing_ForANullableType()
    {
        Assert.True(ConfigValues.TryToScalar(null, ColumnType.Parse("int64?"), out var value, out _));

        Assert.True(value.IsMissing);
    }

    [Theory]
    [InlineData("9007199254740993", "int64", 9007199254740993L)] // above 2^53: exact, not rounded through double
    [InlineData("9007199254740993.0", "int64", 9007199254740993L)]
    [InlineData("90071992547409930e-1", "int64", 9007199254740993L)]
    [InlineData("-9223372036854775808", "int64", long.MinValue)]
    [InlineData("9.223372036854775807e18", "int64", long.MaxValue)]
    [InlineData("30e-1", "int64", 3L)]
    [InlineData("1.5e1", "int64", 15L)]
    [InlineData("-0", "int64", 0L)]
    [InlineData("0.000", "int32", 0L)]
    [InlineData("1e18", "int64", 1_000_000_000_000_000_000L)]
    public void AnInteger_IsReadFromItsDigits(string json, string type, long expected)
    {
        Assert.True(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out var value, out var error), error);

        Assert.Equal(expected, value.AsInt64());
    }

    [Theory]
    [InlineData("9007199254740992.5", "int64", "must be an integer (int64), got the number 9007199254740992.5")]
    [InlineData("1e-400", "int64", "must be an integer (int64), got the number 1e-400")]
    [InlineData("9223372036854775808", "int64", "must be within the int64 range [-9223372036854775808, 9223372036854775807], got the number 9223372036854775808")]
    [InlineData("1e19", "int64", "must be within the int64 range [-9223372036854775808, 9223372036854775807], got the number 1e19")]
    [InlineData("1e400", "int64", "must be within the int64 range [-9223372036854775808, 9223372036854775807], got the number 1e400")]
    [InlineData("[1, 1e400]", "list<int64>", "must be an array of integers within the int64 range [-9223372036854775808, 9223372036854775807]; element 1 is the number 1e400")]
    [InlineData("3000000000", "int32?", "must be within the int32 range [-2147483648, 2147483647], got the number 3000000000; declare the type as int64? for larger values")]
    [InlineData("1e400", "float64", "must be a number (float64) within the float64 range, got the number 1e400")]
    [InlineData("-1e400", "float32", "must be a number (float32) within the float64 range, got the number -1e400")]
    [InlineData("[1, 1e39, 3]", "vec<float32,3>", "must be an array of 3 numbers (vec<float32,3>); element 1 is the number 1e39")]
    [InlineData("\"\\uD800\"", "string", "must be a string, got a string that is not valid Unicode (an unpaired surrogate escape)")]
    public void AValueItsTypeCannotHold_IsAnError_NotAnException(string json, string type, string error)
    {
        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out _, out var actual));
        using var document = JsonDocument.Parse(json);
        Assert.False(ConfigValues.TryToScalar(document.RootElement, ColumnType.Parse(type), out _, out var fromElement));

        Assert.Equal(error, actual);
        Assert.Equal(error, fromElement);
    }

    [Fact]
    public void AnArrayHoldingAStringThatIsNotUnicode_IsAnError_NotAnException()
    {
        // An element names the item at fault; an array parsed into nodes cannot be written back as JSON to be read, so it
        // is told as a whole.
        using var document = JsonDocument.Parse("""["a", "\uDC00"]""");
        Assert.False(ConfigValues.TryToScalar(document.RootElement, ColumnType.Parse("list<string>"), out _, out var fromElement));
        Assert.Equal("must be an array of strings (list<string>); element 1 is a string that is not valid Unicode (an unpaired surrogate escape)", fromElement);

        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse("""["a", "\uDC00"]"""), ColumnType.Parse("list<string>"), out _, out var fromNode));
        Assert.Equal("must be an array of strings (list<string>), got a value holding a string that is not valid Unicode (an unpaired surrogate escape)", fromNode);
    }

    [Theory]
    [InlineData("1001", "int64")]
    [InlineData("\"x\"", "int64")]
    [InlineData("null", "int64?")]
    [InlineData("null", "int64")]
    [InlineData("[1, 2.5]", "list<int32>")]
    [InlineData("[0.25, 1, 2]", "vec<float32,3>")]
    [InlineData("[\"a\", 1]", "list<string>")]
    [InlineData("\"long text\"", "text")]
    [InlineData("1e400", "float64")]
    [InlineData("12345678901234567890", "int64")]
    public void AnElement_AndANode_ReadAlike(string json, string type)
    {
        using var document = JsonDocument.Parse(json);
        var columnType = ColumnType.Parse(type);

        var fromElement = ConfigValues.TryToScalar(document.RootElement, columnType, out var elementValue, out var elementError);
        var fromNode = ConfigValues.TryToScalar(JsonNode.Parse(json), columnType, out var nodeValue, out var nodeError);

        Assert.Equal((fromElement, elementError), (fromNode, nodeError));
        Assert.Equal(elementValue, nodeValue);
    }

    [Fact]
    public void AnAbsentElement_IsMissing()
    {
        Assert.True(ConfigValues.TryToScalar(default(JsonElement), ColumnType.Parse("string?"), out var value, out _));
        Assert.True(value.IsMissing);
        Assert.False(ConfigValues.TryToScalar(default(JsonElement), ColumnType.Parse("string"), out _, out var error));
        Assert.Equal("must be a string, got null", error);
    }

    [Fact]
    public void ATypedValue_IsToldInTheWordsOfTheJsonThatWouldCarryIt()
    {
        Assert.True(ConfigValues.TryConvert(ScalarValue.FromInt64(7), ColumnType.Parse("int32"), out var promoted, out _));
        Assert.Equal(7L, promoted.AsInt64());

        Assert.Equal("must be an integer (int64), got the string \"1001\"", Convert(ScalarValue.FromString("1001"), "int64"));
        Assert.Equal("must be an integer (int64), got the number 1.5", Convert(ScalarValue.FromFloat64(1.5), "int64"));
        Assert.Equal("must be a string, got true", Convert(ScalarValue.FromBool(true), "string"));
        Assert.Equal("must be true or false (bool), got an array", Convert(ScalarValue.FromInt64List(new long[] { 1 }), "bool"));
        Assert.Equal(
            "must be within the int32 range [-2147483648, 2147483647], got the number 3000000000; declare the type as int64? for larger values",
            Convert(ScalarValue.FromInt64(3_000_000_000), "int32?"));
        Assert.Equal(
            "must be an array of integers within the int32 range [-2147483648, 2147483647]; element 0 is the number 3000000000; declare the type as list<int64> for larger values",
            Convert(ScalarValue.FromInt64List(new long[] { 3_000_000_000 }), "list<int32>"));
        Assert.Equal("must be an array of 4 numbers (vec<float32,4>), got 1 element", Convert(ScalarValue.FromVector(new float[] { 1 }), "vec<float32,4>"));

        static string Convert(ScalarValue value, string type)
        {
            Assert.False(ConfigValues.TryConvert(value, ColumnType.Parse(type), out _, out var error));
            return error;
        }
    }

    [Fact]
    public void Describe_IsTheJsonShapeAndTheType()
    {
        Assert.Equal("an integer (int64)", ConfigValues.Describe(ColumnType.Parse("int64?")));
        Assert.Equal("an array of 4 numbers (vec<float32,4>)", ConfigValues.Describe(ColumnType.Parse("vec<float32,4>")));
    }
}
