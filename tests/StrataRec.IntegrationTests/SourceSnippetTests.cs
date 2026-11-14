using System.Text;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The source line the CLI prints under a located diagnostic (<see cref="Output.Snippet"/>), with a caret under the
/// column as a terminal draws the line: tabs copied, wide characters in two cells, a long line cut around the column.
/// </summary>
public sealed class SourceSnippetTests
{
    [Fact]
    public void TheIndentation_IsDropped_AndTheCaretIsUnderTheColumn()
    {
        Assert.Equal("""
                         "k_param": "topk"
                                    ^
                     """.ReplaceLineEndings("\n"), Output.Snippet("""      "k_param": "topk"  """, 18));
    }

    [Theory]
    [InlineData("\t\t\"a\": x", 8, "    \"a\": x\n         ^")] // indentation of tabs, dropped
    [InlineData("\"a\":\t\tx", 7, "    \"a\":\t\tx\n        \t\t^")] // tabs inside the line, copied
    [InlineData("\"名称\": x", 7, "    \"名称\": x\n     \u3000\u3000   ^")] // CJK: two cells each
    [InlineData("\"😀\": x", 7, "    \"😀\": x\n     \u3000   ^")] // a surrogate pair: one character, two cells
    [InlineData("\"e\u0301\": x", 7, "    \"e\u0301\": x\n         ^")] // a combining accent: no cell
    [InlineData("\"a\": ", 7, "    \"a\": \n         ^")] // the column past the text: after it
    public void TheCaret_FollowsHowATerminalDrawsTheLine(string line, int column, string expected)
    {
        Assert.Equal(expected, Output.Snippet(line, column));
    }

    [Fact]
    public void ALongLine_IsCutToAWindowAroundTheColumn()
    {
        var line = "\"expr\": \"" + new string('a', 150) + "BAD" + new string('b', 150) + "\"";
        var column = line.IndexOf("BAD", StringComparison.Ordinal) + 1;

        var lines = Output.Snippet(line, column).Split('\n');

        Assert.StartsWith("    ...", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("...", lines[0], StringComparison.Ordinal);
        Assert.Equal(4 + 3 + 100 + 3, lines[0].Length);
        Assert.Equal("BAD", lines[0].Substring(lines[1].IndexOf('^'), 3));
    }

    [Theory]
    [InlineData(5, false, true)] // near the start: the window starts with the line
    [InlineData(300, true, false)] // near the end: a full window ending with the line
    public void ALongLine_NearAnEnd_ShowsAFullWindowFromThatEnd(int column, bool cutBefore, bool cutAfter)
    {
        var line = new string('x', 160) + "0123456789" + new string('y', 140);

        var lines = Output.Snippet(line, column).Split('\n');

        Assert.Equal(cutBefore, lines[0].StartsWith("    ...", StringComparison.Ordinal));
        Assert.Equal(cutAfter, lines[0].EndsWith("...", StringComparison.Ordinal));
        Assert.Equal(4 + 100 + 3, lines[0].Length);
        Assert.Equal(line[column - 1], lines[0][lines[1].IndexOf('^')]);
    }

    [Fact]
    public void AWindowsEdge_NeverSplitsASurrogatePair()
    {
        // The window would start on the low surrogate of the pair at 98–99: it starts after it instead.
        var line = new string('a', 98) + "😀" + new string('b', 200);

        var shown = Output.Snippet(line, 150).Split('\n')[0];

        Assert.DoesNotContain(shown, char.IsSurrogate);
        Assert.StartsWith("    ...b", shown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\nbc\r\nd", 1, "a")]
    [InlineData("a\nbc\r\nd", 2, "bc")]
    [InlineData("a\nbc\r\nd", 3, "d")]
    [InlineData("a\nbc\r\nd", 4, null)]
    [InlineData("a\n", 2, "")]
    public void LineOf_IsALineWithoutItsBreak(string text, int line, string? expected)
    {
        Assert.Equal(expected, DiagnosticLocator.LineOf(Encoding.UTF8.GetBytes(text), line));
    }
}
