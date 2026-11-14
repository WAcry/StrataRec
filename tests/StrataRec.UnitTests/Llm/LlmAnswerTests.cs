using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Llm;

namespace StrataRec.UnitTests.Llm;

/// <summary>The one parser of generated answers (design 5.9 rule 3): fences, strict JSON, and what each element counts as.</summary>
public sealed class LlmAnswerTests
{
    [Theory]
    [InlineData("```json\n[1]\n```", "[1]")]
    [InlineData("```JSON \r\n [1] \r\n```", "[1]")]
    [InlineData("```\n[1]\n```", "[1]")]
    [InlineData("```\n```", "")]
    [InlineData("```js\n[1]\n```", "```js\n[1]\n```")] // another language: not unwrapped
    [InlineData("```[1]```", "```[1]```")] // no line for the opening fence
    [InlineData("```json\n[1]\n```\n```json\n[2]\n```", "[1]\n```\n```json\n[2]")] // two fences: the outer one only, the rest fails the parse
    [InlineData("```json\n[2,\"```\",1]\n```", "[2,\"```\",1]")] // backticks in a JSON string are content (review 29)
    [InlineData("[1]", "[1]")]
    [InlineData("```", "```")]
    public void OneFenceAroundTheWholeAnswer_IsRemoved(string text, string inside)
    {
        Assert.Equal(inside, LlmAnswer.Unfence(text.AsSpan().Trim()).ToString());
    }

    [Fact]
    public void Ranks_AreOneBasedWindowPositions_EachUsedOnce()
    {
        var (positions, counts, error) = Parse("[3, 1, 3, 0, 4, -1, 2.0, \"2\", 9223372036854775808, 2]", AnswerKind.Rank, 10, 20, 30);

        Assert.Null(error);
        Assert.Equal(new[] { 2, 0, 1 }, positions);
        Assert.Equal(new AnswerCounts(3, 6, 1), counts);
    }

    [Fact]
    public void Int64Ids_AreJsonIntegers_FoundInTheWindow()
    {
        var (positions, counts, error) = Parse("[30, \"10\", 40, 10, 30, 10.0]", AnswerKind.EntityId, 10, 20, 30);

        Assert.Null(error);
        Assert.Equal(new[] { 2, 0 }, positions);
        Assert.Equal(new AnswerCounts(2, 3, 1), counts);
    }

    [Fact]
    public void StringIds_AreJsonStrings_ComparedUnescaped()
    {
        var (positions, counts, error) = Parse("""["b-x", "a", 1, "A", "a"]""", AnswerKind.EntityId, "a", "b-x");

        Assert.Null(error);
        Assert.Equal(new[] { 1, 0 }, positions);
        Assert.Equal(new AnswerCounts(2, 2, 1), counts);
    }

    [Fact]
    public void BackticksInAFencedAnswer_AreContent_NotASecondFence()
    {
        // Review 29: the whole inside was scanned for another fence, so a string holding ``` failed a valid answer.
        var (ranks, rankCounts, rankError) = Parse("```json\n[2,\"```\",1]\n```", AnswerKind.Rank, 10, 20, 30);
        var (ids, idCounts, idError) = Parse("```json\n[\"code```id\"]\n```", AnswerKind.EntityId, "a", "code```id");
        var (_, _, twoBlocks) = Parse("```json\n[1]\n```\n```json\n[2]\n```", AnswerKind.Rank, 10, 20, 30);

        Assert.Null(rankError);
        Assert.Equal(new[] { 1, 0 }, ranks);
        Assert.Equal(new AnswerCounts(2, 1, 0), rankCounts);
        Assert.Null(idError);
        Assert.Equal(new[] { 1 }, ids);
        Assert.Equal(new AnswerCounts(1, 0, 0), idCounts);
        Assert.NotNull(twoBlocks); // a second code block is not one strict JSON array
    }

    [Fact]
    public void AStringThatIsNotValidUnicode_IsAnUnknownElement()
    {
        // Review 29: an unpaired surrogate escape is valid JSON but cannot equal an id; it threw instead.
        var (positions, counts, error) = Parse("""["\uD800", "b", "\uDC00x"]""", AnswerKind.EntityId, "a", "b");

        Assert.Null(error);
        Assert.Equal(new[] { 1 }, positions);
        Assert.Equal(new AnswerCounts(1, 2, 0), counts);
    }

    [Theory]
    [InlineData("[1, 2,]")] // RFC 8259: no trailing commas
    [InlineData("[1 /* first */]")] // nor comments
    [InlineData("[1] [2]")] // nor a second value
    [InlineData("[1, 2")]
    public void AnythingButOneStrictJsonArray_IsInvalid(string text)
    {
        var (_, _, error) = Parse(text, AnswerKind.Rank, 10, 20);

        Assert.StartsWith("it is not valid JSON (", error);
    }

    [Fact]
    public void NestingDeeperThan64_IsInvalid()
    {
        var (_, _, error) = Parse(new string('[', 65) + "1" + new string(']', 65), AnswerKind.Rank, 10, 20);

        Assert.StartsWith("it is not valid JSON (", error);
    }

    [Fact]
    public void NestedValues_AreSkippedWhole_AsUnknownElements()
    {
        var (positions, counts, error) = Parse("""[[1, [2]], {"a": [1, 2]}, 2]""", AnswerKind.Rank, 10, 20);

        Assert.Null(error);
        Assert.Equal(new[] { 1 }, positions);
        Assert.Equal(new AnswerCounts(1, 2, 0), counts);
    }

    [Fact]
    public void TheQuote_IsOneLine_EscapedAndCut()
    {
        Assert.Equal("\"a\\nb\\t\\\"c\\\\\"", LlmAnswer.Quote(" a\nb\t\"c\\ "));
        Assert.Equal("\"" + new string('x', 5) + "…\"", LlmAnswer.Quote(new string('x', 6), max: 5));
    }

    private static (int[] Positions, AnswerCounts Counts, string? Error) Parse(string text, AnswerKind kind, params long[] window) =>
        Parse(text, kind, new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64 }), table => table.AppendRows(window), window.Length);

    private static (int[] Positions, AnswerCounts Counts, string? Error) Parse(string text, AnswerKind kind, params string[] window) =>
        Parse(text, kind, new TableSchema(EntityKeyType.String, new[] { ColumnType.String }), table => table.AppendRows(window), window.Length);

    private static (int[] Positions, AnswerCounts Counts, string? Error) Parse(string text, AnswerKind kind, TableSchema schema, Func<CandidateTable, int> append, int n)
    {
        var memory = new RequestMemory();
        var table = new CandidateTable(schema, n + 4, memory);
        var first = append(table);
        var rows = Enumerable.Range(first, n).ToArray();
        var positions = new int[n];
        var listed = new bool[n];

        var error = LlmAnswer.Parse(text, kind, table, rows, memory, positions, listed, out var counts);

        if (error is null)
        {
            Assert.Equal(positions.Take(counts.Used).Order(), Enumerable.Range(0, n).Where(p => listed[p]));
        }

        return (positions[..counts.Used], counts, error);
    }
}
