using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// <see cref="JsonIntegers"/>: an integer is read from the number's text exactly, whatever form JSON writes it in; a
/// number with a fraction, or one a long cannot hold, is not one. Review 32 #1: through a double,
/// <c>9007199254740993.0</c> and <c>9007199254740992.5</c> both became 9007199254740992.
/// </summary>
public class JsonIntegersTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-0.0", 0L)]
    [InlineData("0.0e999999999999", 0L)]
    [InlineData("3", 3L)]
    [InlineData("3.0", 3L)]
    [InlineData("3e2", 300L)]
    [InlineData("3E+2", 300L)]
    [InlineData("-250E-1", -25L)]
    [InlineData("100e-2", 1L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("9007199254740993.0", 9007199254740993L)]
    [InlineData("9.007199254740993e15", 9007199254740993L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("-9.223372036854775808e18", long.MinValue)]
    [InlineData("1.0000000000000000000000000000000000000", 1L)] // more digits than a decimal holds, all zeros
    public void AnIntegerInAnyForm_IsReadExactly(string text, long expected)
    {
        Assert.True(JsonIntegers.TryParse(text, out var value));
        Assert.Equal(expected, value);
        Assert.True(JsonIntegers.IsIntegral(text));
    }

    [Theory]
    [InlineData("9007199254740992.5")]
    [InlineData("1.5")]
    [InlineData("-25E-1")]
    [InlineData("1.00000000000000000000000000001")] // a decimal would round this to 1
    [InlineData("1e-999999999999")]
    public void ANumberWithAFraction_IsNotAnInteger(string text)
    {
        Assert.False(JsonIntegers.TryParse(text, out _));
        Assert.False(JsonIntegers.IsIntegral(text));
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("9223372036854775807.1e1")]
    [InlineData("1e19")]
    [InlineData("1e999999999999")]
    [InlineData("100000000000000000000000000000000000000000")]
    public void AnIntegerALongCannotHold_IsOutOfRange(string text)
    {
        Assert.False(JsonIntegers.TryParse(text, out _));
        Assert.True(JsonIntegers.IsIntegral(text)); // an integer, just not a long
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1e")]
    [InlineData(".5")]
    [InlineData("abc")]
    public void TextThatIsNotANumber_IsNeither(string text)
    {
        Assert.False(JsonIntegers.TryParse(text, out _));
        Assert.False(JsonIntegers.IsIntegral(text));
    }

    [Fact]
    public void ANode_IsReadFromItsText_WhateverHoldsIt()
    {
        Assert.True(JsonIntegers.TryGetInt64(JsonNode.Parse("9007199254740993.0"), out var parsed));
        Assert.Equal(9007199254740993L, parsed);
        Assert.True(JsonIntegers.TryGetInt64(JsonValue.Create(3.0), out var fromDouble));
        Assert.Equal(3L, fromDouble);
        Assert.False(JsonIntegers.TryGetInt64(JsonValue.Create(2.5), out _));
        Assert.False(JsonIntegers.TryGetInt64(JsonNode.Parse("\"3\""), out _));
        Assert.False(JsonIntegers.TryGetInt64(null, out _));
    }
}
