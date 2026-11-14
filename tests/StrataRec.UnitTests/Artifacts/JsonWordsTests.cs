using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

/// <summary><see cref="JsonWords"/>: values as the JSON a person wrote, quoted short.</summary>
public class JsonWordsTests
{
    [Theory]
    [InlineData("null", "null")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("3.5", "the number 3.5")]
    [InlineData("1e40", "the number 1e40")]
    [InlineData("\"350\"", "the string \"350\"")]
    [InlineData("[1, 2]", "an array")]
    [InlineData("{ \"a\": 1 }", "an object")]
    public void Value_SaysWhatTheJsonIs(string json, string words)
    {
        Assert.Equal(words, JsonWords.Value(JsonNode.Parse(json)));
    }

    [Fact]
    public void Quote_EscapesAsJsonDoes()
    {
        Assert.Equal("\"a\\\"b\\\\c\\n\\u0001\"", JsonWords.Quote("a\"b\\c\n\u0001"));
    }

    [Fact]
    public void Quote_CutsALongString_ButNeverASurrogatePair()
    {
        var exact = new string('x', JsonWords.MaxQuotedLength);
        var pairAtTheCut = new string('x', JsonWords.MaxQuotedLength - 1) + "😀";

        Assert.Equal("\"" + exact + "\"", JsonWords.Quote(exact));
        Assert.Equal("\"" + exact + "\"…", JsonWords.Quote(exact + "y"));
        Assert.Equal("\"" + new string('x', JsonWords.MaxQuotedLength - 1) + "\"…", JsonWords.Quote(pairAtTheCut));
    }

    [Fact]
    public void List_JoinsNamesAsASentenceDoes()
    {
        Assert.Equal(string.Empty, JsonWords.List(Array.Empty<string>()));
        Assert.Equal("'a'", JsonWords.List(new[] { "a" }));
        Assert.Equal("'a' and 'b'", JsonWords.List(new[] { "a", "b" }));
        Assert.Equal("'a', 'b' and 'c'", JsonWords.List(new[] { "a", "b", "c" }));
    }

    [Theory]
    [InlineData(JsonTokenType.Number, "a number")]
    [InlineData(JsonTokenType.String, "a string")]
    [InlineData(JsonTokenType.StartObject, "an object")]
    [InlineData(JsonTokenType.StartArray, "an array")]
    [InlineData(JsonTokenType.Null, "null")]
    public void Token_SaysWhatTheReaderSaw(JsonTokenType token, string words)
    {
        Assert.Equal(words, JsonWords.Token(token));
    }
}
