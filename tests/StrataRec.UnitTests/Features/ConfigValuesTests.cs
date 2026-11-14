using System.Text.Json.Nodes;
using StrataRec.Core.Types;
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

    // ---- integers, exactly (review 32 #1: through a double, 9007199254740993.0 became 9007199254740992) ----------------

    [Theory]
    [InlineData("9007199254740993.0", "int64", 9007199254740993L)]
    [InlineData("9.007199254740993e15", "int64", 9007199254740993L)]
    [InlineData("-9223372036854775808.0", "int64", long.MinValue)]
    [InlineData("2147483647.0", "int32", 2147483647L)]
    [InlineData("-2.147483648e9", "int32", -2147483648L)]
    public void ABigIntegerWrittenWithAFractionOrAnExponent_IsExact(string json, string type, long expected)
    {
        Assert.True(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out var value, out var error), error);

        Assert.Equal(expected, value.AsInt64());
    }

    [Theory]
    [InlineData("[9007199254740993, 9007199254740993.0, 9.007199254740993e15]", "list<int64>", 9007199254740993L)]
    [InlineData("[2147483647, 2147483647.0, 2.147483647e9]", "list<int32>", 2147483647L)]
    public void ABigIntegerElementWrittenWithAFractionOrAnExponent_IsExact(string json, string type, long expected)
    {
        Assert.True(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out var value, out var error), error);

        Assert.Equal(new[] { expected, expected, expected }, value.AsInt64List());
    }

    [Theory]
    [InlineData("9007199254740992.5", "int64", "must be an integer (int64), got the number 9007199254740992.5")]
    [InlineData("2147483647.5", "int32", "must be an integer (int32), got the number 2147483647.5")]
    [InlineData("[1, 9007199254740992.5]", "list<int64>", "must be an array of integers (list<int64>); element 1 is the number 9007199254740992.5")]
    [InlineData("[2147483646.5]", "list<int32>", "must be an array of integers (list<int32>); element 0 is the number 2147483646.5")]
    public void AnIntegerWithANonZeroFraction_IsRejected_HoweverBig(string json, string type, string error)
    {
        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out _, out var actual));

        Assert.Equal(error, actual);
    }

    [Theory]
    [InlineData("9223372036854775808", "int64",
        "must be within the int64 range [-9223372036854775808, 9223372036854775807], got the number 9223372036854775808")]
    [InlineData("1e19", "int64", "must be within the int64 range [-9223372036854775808, 9223372036854775807], got the number 1e19")]
    [InlineData("[1, 9223372036854775808.0]", "list<int64>",
        "must be an array of integers within the int64 range [-9223372036854775808, 9223372036854775807]; element 1 is the number 9223372036854775808.0")]
    [InlineData("2147483648.0", "int32",
        "must be within the int32 range [-2147483648, 2147483647], got the number 2147483648.0; declare the type as int64 for larger values")]
    [InlineData("1e19", "int32", "must be within the int32 range [-2147483648, 2147483647], got the number 1e19")] // int64 would not hold it either
    [InlineData("[2.147483648e9]", "list<int32>",
        "must be an array of integers within the int32 range [-2147483648, 2147483647]; element 0 is the number 2.147483648e9; declare the type as list<int64> for larger values")]
    public void AnIntegerOutOfRange_SaysTheRange(string json, string type, string error)
    {
        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out _, out var actual));

        Assert.Equal(error, actual);
    }

    [Fact]
    public void AStringThatIsNotValidUnicode_IsRejected_NotThrown()
    {
        var unpaired = "\"" + (char)92 + "uD800\"";

        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse(unpaired), ColumnType.Parse("string"), out _, out var scalar));
        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse("[\"a\", " + unpaired + "]"), ColumnType.Parse("list<string>"), out _, out var list));

        Assert.Equal("must be a string, got a string that is not valid Unicode", scalar);
        Assert.Equal("must be an array of strings (list<string>); element 1 is a string that is not valid Unicode", list);
    }

    [Fact]
    public void NullIsMissing_ForANullableType()
    {
        Assert.True(ConfigValues.TryToScalar(null, ColumnType.Parse("int64?"), out var value, out _));

        Assert.True(value.IsMissing);
    }

    [Fact]
    public void Describe_IsTheJsonShapeAndTheType()
    {
        Assert.Equal("an integer (int64)", ConfigValues.Describe(ColumnType.Parse("int64?")));
        Assert.Equal("an array of 4 numbers (vec<float32,4>)", ConfigValues.Describe(ColumnType.Parse("vec<float32,4>")));
    }
}
