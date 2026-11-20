using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// <see cref="StrictJson"/>: what System.Text.Json parses but a request must not hold (reviews 32 and 33): a member given
/// twice, an escape that is not Unicode, and bytes that are not UTF-8, each at its pointer, nothing thrown.
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

    /// <summary>
    /// Review 79: a node tree a host parsed (<c>JsonNode.Parse</c> keeps a repeated member; the object throws when it is
    /// first enumerated) is reported at the repeated member, also when an outer object was enumerated already and the
    /// repeated member is in one of its values, not yet read. A tree built in code has no repeated member. Once checked,
    /// a tree reads without throwing.
    /// </summary>
    [Theory]
    [InlineData("""{ "params": { "k": 3 }, "params": { "k": 4 } }""", "/params", false)]
    [InlineData("""{ "params": { "k": 3, "k": 4 } }""", "/params/k", false)]
    [InlineData("""{ "params": { "k": 3, "k": 4 } }""", "/params/k", true)]
    [InlineData("""{ "params": { "k": [1, { "a": 1, "a": 2 }] } }""", "/params/k/1/a", false)]
    public void ANodeTreeAHostParsed_IsReportedAtTheRepeatedMember(string json, string pointer, bool outerEnumerated)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        if (outerEnumerated)
        {
            Assert.Single(node); // reads the outer object, not params
        }

        var error = StrictJson.Check(node, Jsonc.MaxDepth);

        Assert.Equal(pointer, error?.Pointer);
        Assert.Equal("give each member once: which of two values would count is not defined", error?.Hint);
        var clean = new JsonObject { ["params"] = new JsonObject { ["k"] = 3 } };
        Assert.Null(StrictJson.Check(clean, Jsonc.MaxDepth));
        Assert.Null(StrictJson.Check(null, Jsonc.MaxDepth));
        var parsed = JsonNode.Parse("""{ "params": { "k": [1, { "a": 1 }] } }""");
        Assert.Null(StrictJson.Check(parsed, Jsonc.MaxDepth));
        Assert.Equal("""{"params":{"k":[1,{"a":1}]}}""", JsonCanonicalizer.CanonicalizeToString(parsed));
    }

    /// <summary>
    /// Review 81: the check wrote the tree and read the text back by ordinal names, so two names that differ only in case
    /// passed, and the object, parsed with <see cref="JsonNodeOptions.PropertyNameCaseInsensitive"/>, threw when first
    /// enumerated. The object's own comparison of names decides.
    /// </summary>
    [Fact]
    public void ANodeTreeParsedIgnoringCase_IsReportedAtTheNameThatRepeatsAnotherIgnoringCase()
    {
        const string Json = """{ "params": { "final_top_k": 3, "FINAL_TOP_K": 4 } }""";
        var insensitive = JsonNode.Parse(Json, new JsonNodeOptions { PropertyNameCaseInsensitive = true });

        var error = StrictJson.Check(insensitive, Jsonc.MaxDepth);

        Assert.NotNull(error);
        Assert.Equal("/params/FINAL_TOP_K", error.Pointer);
        Assert.Equal("Member 'FINAL_TOP_K' is given more than once: the object compares names ignoring case (JsonNodeOptions.PropertyNameCaseInsensitive).",
            error.Message);
        Assert.Null(StrictJson.Check(JsonNode.Parse(Json), Jsonc.MaxDepth)); // two names, by ordinal comparison
    }

    /// <summary>
    /// Review 81: a tree JSON cannot write (a NaN) was not checked at all, since the check wrote the tree; an object it
    /// held that repeats a member then threw when it was first enumerated. The NaN is not this check's (the canonical
    /// form reports it); the repeated member is, wherever it is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARepeatedMember_IsReportedBesideAValueJsonCannotWrite(bool nanFirst)
    {
        var nan = new JsonObject { ["final_top_k"] = double.NaN };
        var repeated = JsonNode.Parse("""{ "coarse_top_k": { "order": 1 }, "coarse_top_k": { "order": 2 } }""");
        var tree = nanFirst ? new JsonObject { ["params"] = nan, ["nodes"] = repeated } : new JsonObject { ["nodes"] = repeated, ["params"] = nan };

        Assert.Equal("/nodes/coarse_top_k", StrictJson.Check(tree, Jsonc.MaxDepth)?.Pointer);
        Assert.Null(StrictJson.Check(new JsonObject { ["params"] = new JsonObject { ["final_top_k"] = double.NaN } }, Jsonc.MaxDepth));
    }

    /// <summary>
    /// A name that is not Unicode stops an object read from text from being enumerated, and from being written: reported at
    /// the object, as for a request, in the words of what threw. It threw out of the canonical form before.
    /// </summary>
    [Fact]
    public void ANameThatIsNotUnicode_InANodeTree_IsReportedAtItsObject()
    {
        var error = StrictJson.Check(JsonNode.Parse("""{ "x": { "ok": 1, "\uD800": 2 } }"""), Jsonc.MaxDepth);

        Assert.NotNull(error);
        Assert.Equal("/x", error.Pointer);
        Assert.StartsWith("The members of this object cannot be read: ", error.Message, StringComparison.Ordinal);
        Assert.Equal("give each member once, under a name that is valid Unicode (a complete surrogate pair such as \\ud83d\\ude00, or the character directly)",
            error.Hint);
    }

    /// <summary>
    /// Review 81: the check wrote the tree and read it back at the reader's default depth, which threw for a deeper tree.
    /// A tree as deep as the limit passes; one level more is reported at the first object or array past it, nothing
    /// deeper read.
    /// </summary>
    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(10_000, 5)]
    [InlineData(64, 64)]
    [InlineData(65, 64)]
    [InlineData(70, 64)] // the review's: 70 levels at /params/final_top_k
    public void ATreeNestedPastTheLimit_IsReportedAtTheFirstLevelTooDeep(int levels, int maxDepth)
    {
        var error = StrictJson.Check(Nested(levels), maxDepth);

        if (levels <= maxDepth)
        {
            Assert.Null(error);
            return;
        }

        Assert.NotNull(error);
        Assert.Equal("/a" + string.Concat(Enumerable.Repeat("/0", maxDepth - 1)), error.Pointer); // "/a" is level 2
        Assert.Equal($"Objects and arrays nest deeper than {maxDepth} levels here: this one is level {maxDepth + 1}.", error.Message);
        Assert.Equal($"nest objects and arrays at most {maxDepth} levels deep, counting the outermost", error.Hint);
    }

    /// <summary>An object holding <c>"a"</c>, an array of an array … of <c>1</c>: <paramref name="levels"/> objects and arrays in all.</summary>
    internal static JsonObject Nested(int levels)
    {
        JsonNode value = 1;
        for (var i = 1; i < levels; i++)
        {
            value = new JsonArray(value);
        }

        return new JsonObject { ["a"] = value };
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

    /// <summary>
    /// Bytes that are not UTF-8 (review 33): <see cref="JsonDocument"/> parses them and throws only when the text is read;
    /// a string at its pointer, a member name at its object. <c>#</c> stands for the bytes.
    /// </summary>
    [Theory]
    [InlineData("""{ "x": "a#b" }""", new byte[] { 0xFF }, "/x", "The string is not valid UTF-8 text.")]
    [InlineData("""{ "x": ["ok", "#"] }""", new byte[] { 0x80 }, "/x/1", "The string is not valid UTF-8 text.")] // a lone continuation byte
    [InlineData("""{ "x": "#" }""", new byte[] { 0xC0, 0xAF }, "/x", "The string is not valid UTF-8 text.")] // an overlong form of '/'
    [InlineData("""{ "x": "#" }""", new byte[] { 0xE2, 0x82 }, "/x", "The string is not valid UTF-8 text.")] // a truncated sequence
    [InlineData("""{ "x": "#" }""", new byte[] { 0xED, 0xA0, 0x80 }, "/x", "The string is not valid UTF-8 text.")] // a surrogate, encoded
    [InlineData("""{ "x": "\n#" }""", new byte[] { 0xFF }, "/x", "The string is not valid UTF-8 text.")] // escaped as well
    [InlineData("""{ "ok": 1, "x": { "a": 1, "#": 2 } }""", new byte[] { 0xFF }, "/x", "The name of member 2 of this object is not valid UTF-8 text.")]
    public void BytesThatAreNotUtf8_AreAtTheirValue_OrTheObjectOfTheirName(string json, byte[] bytes, string pointer, string message)
    {
        using var document = JsonDocument.Parse(WithBytes(json, bytes));

        var error = StrictJson.Check(document.RootElement);

        Assert.NotNull(error);
        Assert.Equal((pointer, message), (error.Pointer, error.Message));
        Assert.Equal("send the body as UTF-8 (Content-Type: application/json; charset=utf-8)", error.Hint);
    }

    [Fact]
    public void TextInEveryUtf8Length_HasNoProblem()
    {
        using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes("""{ "aé€😀": "aé€😀" }"""));

        Assert.Null(StrictJson.Check(document.RootElement));
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

    [Fact]
    public void AValidObject_OfTextBeyondAscii_AllocatesNothing()
    {
        using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes("""{ "inputs": { "région": "île-de-France", "标签": ["新闻", "😀"] } }"""));
        Assert.Null(StrictJson.Check(document.RootElement)); // warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = StrictJson.Check(document.RootElement);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(error);
        Assert.Equal(0, allocated);
    }

    /// <summary>The UTF-8 of <paramref name="json"/> with its one <c>#</c> replaced by <paramref name="bytes"/>.</summary>
    internal static byte[] WithBytes(string json, byte[] bytes)
    {
        var text = Encoding.UTF8.GetBytes(json);
        var at = Array.IndexOf(text, (byte)'#');
        Assert.True(at >= 0 && Array.IndexOf(text, (byte)'#', at + 1) < 0, "one # in the JSON");
        return [.. text[..at], .. bytes, .. text[(at + 1)..]];
    }
}
