using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// <see cref="StrictJson"/>: what System.Text.Json parses but a request must not hold (review 32): a member given twice,
/// and an escape that is not Unicode, each at its pointer, nothing thrown.
/// </summary>
public sealed class StrictJsonTests
{
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{ "a": 1, "b": { "a": 2 }, "c": [{ "a": 3 }, { "a": 4 }] }""")] // one name in different objects
    [InlineData("""{ "é": "\u00e9", "smile": "\ud83d\ude00", "\ud83d\ude00": 1 }""")] // escapes that are Unicode
    [InlineData("""{ "a": "\\uD800" }""")] // an escaped backslash, then the text uD800
    [InlineData("""{ "A": 1, "a": 2 }""")] // names are case-sensitive
    [InlineData("""1""")]
    public void AValueARequestMayHold_HasNoProblem(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(StrictJson.Check(document.RootElement));
    }

    [Theory]
    [InlineData("""{ "inputs": {}, "inputs": {} }""", "/inputs", "inputs")]
    [InlineData("""{ "inputs": { "user_id": 1001, "user_id": 1002 } }""", "/inputs/user_id", "user_id")]
    [InlineData("""{ "x": { "y": [0, { "a": 1, "b": 2, "a": 3 }] } }""", "/x/y/1/a", "a")]
    [InlineData("""{ "a/b": 1, "a~b": 2, "a/b": 3 }""", "/a~1b", "a/b")]
    [InlineData("""{ "a": 1, "\u0061": 2 }""", "/a", "a")] // one name after unescaping
    [InlineData("""{ "": 1, "": 2 }""", "/", "")]
    public void AMemberGivenTwice_IsAtItsSecondOccurrence(string json, string pointer, string name)
    {
        using var document = JsonDocument.Parse(json);

        var error = StrictJson.Check(document.RootElement);

        Assert.NotNull(error);
        Assert.Equal((pointer, $"Member '{name}' is given more than once."), (error.Pointer, error.Message));
        Assert.Equal("give each member once: which of two values would count is not defined", error.Hint);
    }

    [Fact]
    public void ALargeObject_IsCheckedWithASet()
    {
        var members = new StringBuilder("{");
        for (var i = 0; i < 40; i++)
        {
            members.Append($"\"m{i}\": {i}, ");
        }

        using var clean = JsonDocument.Parse(members.ToString() + "\"last\": 0 }");
        using var repeated = JsonDocument.Parse(members.ToString() + "\"m7\": 0 }");

        Assert.Null(StrictJson.Check(clean.RootElement));
        Assert.Equal("/m7", StrictJson.Check(repeated.RootElement)?.Pointer);
    }

    [Theory]
    [InlineData("""{ "x": ["ok", "\uD800"] }""", "/x/1", "The string \"\\uD800\" is not valid Unicode: an escape is an unpaired surrogate.")]
    [InlineData("""{ "x": "a\uDC00b" }""", "/x", "The string \"a\\uDC00b\" is not valid Unicode: an escape is an unpaired surrogate.")]
    [InlineData("""{ "x": "\\\uD800" }""", "/x", "The string \"\\\\\\uD800\" is not valid Unicode: an escape is an unpaired surrogate.")] // after an escaped backslash
    [InlineData("""{ "x": { "\uD800": 1 } }""", "/x", "The member name \"\\uD800\" is not valid Unicode: an escape is an unpaired surrogate.")] // the object: the name has no pointer
    [InlineData("""["\uD83D"]""", "/0", "The string \"\\uD83D\" is not valid Unicode: an escape is an unpaired surrogate.")]
    public void AnEscapeThatIsNotUnicode_IsAtItsValue(string json, string pointer, string message)
    {
        using var document = JsonDocument.Parse(json);

        var error = StrictJson.Check(document.RootElement);

        Assert.NotNull(error);
        Assert.Equal((pointer, message), (error.Pointer, error.Message));
        Assert.Equal("use a complete surrogate pair such as \\ud83d\\ude00, or write the character directly", error.Hint);
    }

    [Fact]
    public void AValidObject_OfPlainNames_AllocatesNothing()
    {
        using var document = JsonDocument.Parse("""{ "inputs": { "user_id": 1001, "region": "us", "tags": [1, 2] }, "request_id": "r" }""");
        Assert.Null(StrictJson.Check(document.RootElement)); // warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = StrictJson.Check(document.RootElement);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(error);
        Assert.Equal(0, allocated);
    }
}
