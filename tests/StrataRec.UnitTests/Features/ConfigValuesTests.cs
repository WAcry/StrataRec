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
