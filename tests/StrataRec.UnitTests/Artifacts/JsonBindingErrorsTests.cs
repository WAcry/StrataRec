using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// <see cref="JsonBindingErrors"/>: every way a document fails to bind is told in the words of the JSON written, at the
/// pointer of the value at fault, without a .NET type name or the serializer's own path syntax.
/// </summary>
public class JsonBindingErrorsTests
{
    public sealed class Sample
    {
        [Description("Logical model name, bound per environment.")]
        public required string Model { get; init; }

        public int? Order { get; init; }

        public int Window { get; init; }

        public double Weight { get; init; }

        public bool Enabled { get; init; }

        public string? Label { get; init; }

        public List<Item> Items { get; init; } = new();

        public Dictionary<string, Item> Named { get; init; } = new();

        public Dictionary<string, List<int>> Lists { get; init; } = new();

        public JsonObject? Config { get; init; }

        public JsonPointer? At { get; init; }
    }

    public sealed class Item
    {
        public required string Name { get; init; }

        public int Count { get; init; }
    }

    public sealed class Empty;

    // ---- unknown keys ------------------------------------------------------------------------------------------------

    [Fact]
    public void UnknownKey_NamesTheKeyAndSuggestsTheClosest()
    {
        var error = Error<Sample>("""{ "model": "m", "ordr": 1 }""");

        Assert.Equal("/ordr", error.Pointer);
        Assert.Equal("Unknown key 'ordr' in the sample.", error.Message);
        Assert.Equal("did you mean 'order'?", error.Hint);
    }

    [Fact]
    public void UnknownKey_WithNothingClose_ListsTheAllowedKeys()
    {
        var error = Error<Sample>("""{ "model": "m", "zzzzzzzz": 1 }""");

        Assert.Equal("allowed keys: model, order, window, weight, enabled, label, items, named, lists, config, at", error.Hint);
    }

    [Fact]
    public void UnknownKey_OfATypeWithoutKeys_SaysSo()
    {
        var error = Error<Empty>("""{ "x": 1 }""", "the config of Noop@1");

        Assert.Equal("Unknown key 'x' in the config of Noop@1.", error.Message);
        Assert.Equal("it takes no keys", error.Hint);
    }

    // ---- values of the wrong kind ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "model": "m", "order": "350" }""", "/order", """'order' must be an integer or null, got the string "350".""")]
    [InlineData("""{ "model": "m", "window": "20" }""", "/window", """'window' must be an integer, got the string "20".""")]
    [InlineData("""{ "model": "m", "window": null }""", "/window", "'window' must be an integer, got null.")]
    [InlineData("""{ "model": 3 }""", "/model", "'model' must be a string, got the number 3.")]
    [InlineData("""{ "model": "m", "label": 3 }""", "/label", "'label' must be a string or null, got the number 3.")]
    [InlineData("""{ "model": "m", "enabled": "yes" }""", "/enabled", """'enabled' must be true or false, got the string "yes".""")]
    [InlineData("""{ "model": "m", "weight": [1] }""", "/weight", "'weight' must be a number, got an array.")]
    [InlineData("""{ "model": "m", "items": {} }""", "/items", "'items' must be an array, got an object.")]
    [InlineData("""{ "model": "m", "config": [1] }""", "/config", "'config' must be an object or null, got an array.")]
    [InlineData("""{ "model": "m", "window": 2.5 }""", "/window", "'window' must be an integer, got the number 2.5.")]
    [InlineData("""{ "model": "m", "window": 3000000000 }""", "/window", "'window' must be an integer from -2147483648 to 2147483647, got the number 3000000000.")]
    [InlineData("""{ "model": "m", "window": 1e40 }""", "/window", "'window' must be an integer from -2147483648 to 2147483647, got the number 1e40.")]
    public void WrongKind_SaysWhatItMustBeAndWhatItIs(string json, string pointer, string message)
    {
        var error = Error<Sample>(json);

        Assert.Equal(pointer, error.Pointer);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void ALongString_IsQuotedShort()
    {
        var error = Error<Sample>($$"""{ "model": "m", "window": "{{new string('x', 100)}}" }""");

        Assert.Equal($"'window' must be an integer, got the string \"{new string('x', JsonWords.MaxQuotedLength)}\"….", error.Message);
    }

    [Fact]
    public void OurOwnConverter_WordsItsMessage()
    {
        var error = Error<Sample>("""{ "model": "m", "at": 3 }""");

        Assert.Equal("/at", error.Pointer);
        Assert.Equal("""'at': Expected an RFC 6901 JSON pointer string such as "/nodes/fusion", got a number.""", error.Message);
    }

    [Fact]
    public void NullDocument_SaysWhatItMustBe()
    {
        Assert.False(ArtifactSerializer.TryBind<Sample>(null, "the config of TopK@1", out _, out var error));

        Assert.Equal(string.Empty, error.Pointer);
        Assert.Equal("The config of TopK@1 must be an object, got null.", error.Message);
    }

    // ---- missing required keys ---------------------------------------------------------------------------------------

    [Fact]
    public void MissingRequiredKey_PointsAtTheObject_AndTheHintSaysWhatTheKeyMeans()
    {
        var error = Error<Sample>("""{ "order": 1 }""", "the config of ModelScorer@1");

        Assert.Equal(string.Empty, error.Pointer);
        Assert.Equal("'model' is required in the config of ModelScorer@1.", error.Message);
        Assert.Equal("'model': Logical model name, bound per environment.", error.Hint);
    }

    [Fact]
    public void MissingRequiredKey_TheMeaningComesFromTheXmlDocumentation()
    {
        // NodeSpec.Op is documented with a summary and no [Description].
        Assert.False(ArtifactSerializer.TryBind<NodeSpec>(JsonNode.Parse("""{ "order": 1 }"""), "node 'x'", out _, out var error));

        Assert.Equal("'op' is required in node 'x'.", error.Message);
        Assert.Equal("'op': Operator type and version, e.g. TopK@1.", error.Hint);
    }

    [Fact]
    public void Documentation_ReadsAPropertyReferenceAsItsJsonName_AndDropsATypeReference()
    {
        Assert.Equal("Semantic position; required unless role is fallback.",
            MemberDocs.Flatten("""Semantic position; required unless <see cref="P:StrataRec.Artifacts.Model.NodeSpec.Role"/> is <c>fallback</c>.""",
                JsonNamingPolicy.SnakeCaseLower));
        Assert.Equal("The time_ms of the request.", MemberDocs.Flatten("The <see cref=\"P:A.B.TimeMs\"/>   of the\n            request.", JsonNamingPolicy.SnakeCaseLower));
        Assert.Null(MemberDocs.Flatten("""A <see cref="T:StrataRec.Core.Types.ColumnType"/>.""", JsonNamingPolicy.SnakeCaseLower));
    }

    // ---- nested paths ------------------------------------------------------------------------------------------------

    [Fact]
    public void IntoAList_ThePointerHasTheIndex_AndTheMessageTheElement()
    {
        var wrong = Error<Sample>("""{ "model": "m", "items": [{ "name": "a" }, { "name": "b", "count": "x" }] }""");
        var missing = Error<Sample>("""{ "model": "m", "items": [{ "name": "a" }, { "count": 1 }] }""");

        Assert.Equal("/items/1/count", wrong.Pointer);
        Assert.Equal("""'count' must be an integer, got the string "x".""", wrong.Message);
        Assert.Equal("/items/1", missing.Pointer);
        Assert.Equal("'name' is required in element 1 of 'items'.", missing.Message);
    }

    [Fact]
    public void IntoADictionary_TheKeyIsEscapedInThePointer_AndQuotedInTheMessage()
    {
        var error = Error<Sample>("""{ "model": "m", "named": { "a/b~c": { "name": "n", "cout": 1 } } }""");

        Assert.Equal("/named/a~1b~0c/cout", error.Pointer);
        Assert.Equal("Unknown key 'cout' in entry 'a/b~c' of 'named'.", error.Message);
        Assert.Equal("did you mean 'count'?", error.Hint);
    }

    [Fact]
    public void IntoAListInADictionary_EveryLevelIsNamed()
    {
        var error = Error<Sample>("""{ "model": "m", "lists": { "k": [1, "x"] } }""");

        Assert.Equal("/lists/k/1", error.Pointer);
        Assert.Equal("""Element 1 of entry 'k' of 'lists' must be an integer, got the string "x".""", error.Message);
    }

    [Theory]
    [InlineData("it's']")]
    [InlineData("a'].b")]
    [InlineData("a.b[0]")]
    public void AKeyThatLooksLikePathSyntax_IsReadFromTheDocument(string key)
    {
        var document = new JsonObject
        {
            ["model"] = "m",
            ["named"] = new JsonObject { [key] = new JsonObject { ["name"] = 1 } },
        };

        Assert.False(ArtifactSerializer.TryBind<Sample>(document, "the sample", out _, out var error));

        Assert.Equal("/named/" + JsonPointer.Escape(key) + "/name", error.Pointer);
        Assert.Equal("'name' must be a string, got the number 1.", error.Message);
    }

    [Fact]
    public void PathSegments_ReadsDotsBracketsAndIndexes()
    {
        var segments = JsonBindingErrors.PathSegments("$.nodes['a.b'].config.keys[2]", null);

        Assert.Equal(new[] { ("nodes", false), ("a.b", false), ("config", false), ("keys", false), ("2", true) },
            segments.Select(s => (s.Token, s.IsIndex)));
    }

    // ---- artifacts -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AnArtifact_ReportsThePointerAndReasonInJsonWords()
    {
        var json = """
            { "pipeline_id": "p", "entity_key_type": "int64",
              "nodes": { "top": { "order": "350", "op": "TopK@1" } } }
            """;

        var ex = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<PipelineArtifact>(json, "pipelines/p.jsonc"));

        Assert.Equal("/nodes/top/order", ex.Pointer);
        Assert.Equal("""'order' must be an integer or null, got the string "350".""", ex.Reason);
        Assert.Equal("""pipelines/p.jsonc: /nodes/top/order: 'order' must be an integer or null, got the string "350".""", ex.Message);
        NoDotNetNames(ex.Reason, ex.Hint);
    }

    [Fact]
    public void AnArtifactThatIsNotJson_SaysWhereTheSyntaxBreaks()
    {
        var ex = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<PipelineArtifact>("{\n  \"pipeline_id\": \"p\"\n  \"x\": 1 }", "p.jsonc"));

        Assert.Equal(string.Empty, ex.Pointer);
        Assert.Contains("(line 3, column 3)", ex.Reason, StringComparison.Ordinal);
        NoDotNetNames(ex.Reason, ex.Hint);
    }

    [Theory]
    [InlineData("""{ "model": "m", "order": "350" }""")]
    [InlineData("""{ "model": "m", "order": 1.5 }""")]
    [InlineData("""{ "model": "m", "window": 1e40 }""")]
    [InlineData("""{ "model": "m", "weight": "1" }""")]
    [InlineData("""{ "model": "m", "items": [{ "name": "a", "count": true }] }""")]
    [InlineData("""{ "model": "m", "named": { "k": 1 } }""")]
    [InlineData("""{ "model": "m", "lists": { "k": [[1]] } }""")]
    [InlineData("""{ "model": "m", "config": "x" }""")]
    [InlineData("""{ "model": "m", "at": "no-slash" }""")]
    [InlineData("""{ "model": "m", "huh": 1 }""")]
    [InlineData("""{ "items": [] }""")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void NoMessageNamesADotNetType(string json)
    {
        Error<Sample>(json);
    }

    private static JsonBindingError Error<T>(string json, string subject = "the sample")
        where T : class
    {
        Assert.False(ArtifactSerializer.TryBind<T>(JsonNode.Parse(json), subject, out _, out var error));
        NoDotNetNames(error.Message, error.Hint);
        Assert.True(error.Pointer.Length == 0 || error.Pointer[0] == '/', error.Pointer);
        return error;
    }

    private static void NoDotNetNames(string message, string? hint)
    {
        foreach (var text in new[] { message, hint ?? string.Empty })
        {
            foreach (var leak in new[] { "System.", "Int32", "Int64", "Double", "Nullable", "String", "Boolean", "JsonObject", "JsonNode", "$.", "Path:", "LineNumber", "BytePosition", ".NET", "could not be converted", "StrataRec." })
            {
                Assert.DoesNotContain(leak, text, StringComparison.Ordinal);
            }
        }
    }
}
