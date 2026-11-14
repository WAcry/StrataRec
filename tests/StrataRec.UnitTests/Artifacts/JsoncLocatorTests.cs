using System.Text;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// <see cref="JsoncLocator"/>: a pointer becomes the line and column an editor opens, through comments, escapes and
/// nested arrays; a pointer the text does not have falls back to its nearest ancestor and says so.
/// </summary>
public class JsoncLocatorTests
{
    private const string Pipeline = """
        // "order": 1 -- a comment that looks like the key
        {
          /* "nodes": { "a": { "order": 0 } } */
          "nodes": {
            "a": { "op": "TopK@1", // "order"
                   "order": 350 },
            "b/c": { "x~y": [10, [20, 30]] }
          },
          "list": [[1, 2], [3, [4, 5]]]
        }
        """;

    [Fact]
    public void AMember_IsAtItsKey_ThroughCommentsThatLookLikeKeys()
    {
        Assert.Equal(At(Pipeline, "\"order\": 350"), Locate(Pipeline, "/nodes/a/order"));
    }

    [Fact]
    public void AnArrayElement_IsAtItsValue_InNestedArrays()
    {
        Assert.Equal(At(Pipeline, "4, 5"), Locate(Pipeline, "/list/1/1/0"));
        Assert.Equal(At(Pipeline, "30]"), Locate(Pipeline, "/nodes/b~1c/x~0y/1/1"));
    }

    [Fact]
    public void TheRoot_IsAtItsValue()
    {
        Assert.Equal(At(Pipeline, "{\n  /*"), Locate(Pipeline, string.Empty));
    }

    [Fact]
    public void AnEscapedKey_IsMatchedByItsValue()
    {
        const string text = """{ "x\u002Fy": 1, "z": 2 }""";

        Assert.Equal(At(text, "\"x\\u"), Locate(text, "/x~1y"));
    }

    [Theory]
    [InlineData("/nodes/zzz/order", "\"nodes\": {\n")] // a node the file does not have (an arm adds it)
    [InlineData("/nodes/a/config/x", "\"a\": { \"op\"")] // a member the object does not have
    [InlineData("/nodes/a/op/x", "\"op\"")] // into a string
    [InlineData("/list/7", "\"list\"")] // past the end of an array
    [InlineData("/list/x", "\"list\"")] // not an index
    [InlineData("/list/01", "\"list\"")] // not an index either (RFC 6901 has no leading zeros)
    public void APointerTheTextDoesNotHave_IsAtItsNearestAncestor_NotExact(string pointer, string ancestor)
    {
        var expected = At(Pipeline, ancestor);

        Assert.Equal(expected with { Exact = false }, Locate(Pipeline, pointer));
    }

    [Fact]
    public void AByteOrderMark_IsNotAColumn()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{ "a": 1 }""")).ToArray();

        Assert.Equal(new JsoncLocation(1, 3, true), JsoncLocator.Locate(bytes, "/a"));
    }

    [Fact]
    public void CarriageReturns_AreNotColumns()
    {
        Assert.Equal(new JsoncLocation(2, 3, true), Locate("{\r\n  \"a\": 1\r\n}", "/a"));
    }

    [Fact]
    public void ColumnsCountUtf16Units()
    {
        const string text = """{ "été": 1, "😀": 2, "b": 3 }""";

        Assert.Equal(At(text, "\"b\""), Locate(text, "/b"));
        Assert.Equal(1 + text.IndexOf("\"b\"", StringComparison.Ordinal), Locate(text, "/b")!.Value.Column);
    }

    [Theory]
    [InlineData("""{ "expr": "a \"b\" + c" }""", "a \"b\" + c", "c")] // \" is two columns, one character
    [InlineData("""{ "expr": "\u00e9t\u00e9 + y" }""", "été + y", "y")] // \u00e9 is six columns, one character
    [InlineData("""{ "expr": "été + y" }""", "été + y", "y")] // two UTF-8 bytes, one character
    [InlineData("""{ "expr": "😀 + y" }""", "😀 + y", "y")] // four UTF-8 bytes, two UTF-16 units
    [InlineData("""{ "expr": "\ud83d\ude00 + y" }""", "😀 + y", "y")] // a surrogate pair written as two escapes
    [InlineData("""{ "expr": "a\n\tb" }""", "a\n\tb", "b")]
    public void AnOffsetIntoAString_IsTheColumnOfThatCharacterAsWritten(string text, string value, string character)
    {
        var offset = value.IndexOf(character, StringComparison.Ordinal);
        var written = text.LastIndexOf(character, StringComparison.Ordinal);

        Assert.Equal(new JsoncLocation(1, written + 1, true), Locate(text, "/expr", offset));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // the low surrogate: review 32 #8 returned the column of the next character
    [InlineData(2)]
    public void AnOffsetInARawNonBmpCharacter_IsTheColumnOfThatUtf16Unit(int offset)
    {
        const string text = "{ \"expr\": \"😀x\" }";
        var first = text.IndexOf("😀", StringComparison.Ordinal) + 1;

        Assert.Equal(new JsoncLocation(1, first + offset, true), Locate(text, "/expr", offset));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 6)] // the second escape of the pair
    [InlineData(2, 12)]
    public void AnOffsetInAnEscapedSurrogatePair_IsTheColumnOfThatEscape(int offset, int columns)
    {
        var backslash = (char)92;
        var text = "{ \"expr\": \"" + backslash + "ud83d" + backslash + "ude00x\" }";
        var first = text.IndexOf(backslash) + 1;

        Assert.Equal(new JsoncLocation(1, first + columns, true), Locate(text, "/expr", offset));
    }

    [Fact]
    public void AnOffsetIntoAString_OnALaterLine()
    {
        const string text = "{\n  // the scorer\n  \"expr\": \"col.item.smoothed_ctr * 'a'\"\n}";

        Assert.Equal(new JsoncLocation(3, 36, true), Locate(text, "/expr", 24));
    }

    [Fact]
    public void AnOffsetPastTheEndOfTheString_StopsAtTheClosingQuote()
    {
        const string text = """{ "expr": "ab" }""";

        Assert.Equal(At(text, "\" }"), Locate(text, "/expr", 10));
    }

    [Fact]
    public void AnOffsetForAPointerTheTextDoesNotHave_IsIgnored()
    {
        Assert.Equal(At(Pipeline, "\"a\": { \"op\"") with { Exact = false }, Locate(Pipeline, "/nodes/a/expr", 5));
    }

    [Fact]
    public void TextMalformedAfterTheMatch_KeepsTheMatch()
    {
        const string text = """{ "a": { "b": 1 }, "c": """;

        Assert.Equal(At(text, "\"b\""), Locate(text, "/a/b"));
    }

    [Fact]
    public void TextMalformedBeforeTheMatch_IsAtTheDeepestMatch()
    {
        const string text = """{ "a": [1, }, "z": 1 }""";

        Assert.Equal(At(text, "{") with { Exact = false }, Locate(text, "/z"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   // only a comment")]
    [InlineData("}")]
    public void TextWithoutAValue_HasNoLocation(string text)
    {
        Assert.Null(Locate(text, "/a"));
    }

    private static JsoncLocation? Locate(string text, string pointer, int? offset = null) =>
        JsoncLocator.Locate(Encoding.UTF8.GetBytes(text), pointer, offset);

    /// <summary>The position of the first occurrence of <paramref name="marker"/>, counted independently of the locator.</summary>
    private static JsoncLocation At(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, marker);
        var line = 1 + text[..index].Count(c => c == '\n');
        var column = index - (index == 0 ? -1 : text.LastIndexOf('\n', index - 1));
        return new JsoncLocation(line, column, true);
    }
}
