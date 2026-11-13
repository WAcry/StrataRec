using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class JsonPointerTests
{
    // RFC 6901 section 5 document.
    private const string Rfc6901Document = """
        {
          "foo": ["bar", "baz"],
          "": 0,
          "a/b": 1,
          "c%d": 2,
          "e^f": 3,
          "g|h": 4,
          "i\\j": 5,
          "k\"l": 6,
          " ": 7,
          "m~n": 8
        }
        """;

    [Theory]
    [InlineData("", null)]
    [InlineData("/foo", """["bar","baz"]""")]
    [InlineData("/foo/0", "\"bar\"")]
    [InlineData("/", "0")]
    [InlineData("/a~1b", "1")]
    [InlineData("/c%d", "2")]
    [InlineData("/e^f", "3")]
    [InlineData("/g|h", "4")]
    [InlineData("/i\\j", "5")]
    [InlineData("/k\"l", "6")]
    [InlineData("/ ", "7")]
    [InlineData("/m~0n", "8")]
    public void TryEvaluate_Rfc6901Section5Examples(string pointer, string? expected)
    {
        var document = Jsonc.Parse(Rfc6901Document);

        Assert.True(JsonPointer.Parse(pointer).TryEvaluate(document, out var value));

        Assert.Equal(expected ?? document!.ToJsonString(), value!.ToJsonString());
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("/", new[] { "" })]
    [InlineData("//", new[] { "", "" })]
    [InlineData("/a/", new[] { "a", "" })]
    [InlineData("/a~1b", new[] { "a/b" })]
    [InlineData("/m~0n", new[] { "m~n" })]
    [InlineData("/~01", new[] { "~1" })]
    [InlineData("/~10", new[] { "/0" })]
    [InlineData("/~0~1~0", new[] { "~/~" })]
    [InlineData("/nodes/fusion/config", new[] { "nodes", "fusion", "config" })]
    [InlineData("/é/😀", new[] { "é", "😀" })]
    public void Parse_UnescapesTokensAndRoundTrips(string text, string[] tokens)
    {
        var pointer = JsonPointer.Parse(text);

        Assert.Equal(tokens, pointer.Tokens.ToArray());
        Assert.Equal(text, pointer.ToString());
        Assert.Equal(text, JsonPointer.Create(tokens).ToString());
        Assert.Equal(pointer, JsonPointer.Create(tokens));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("nodes/x")]
    [InlineData("/~")]
    [InlineData("/~2")]
    [InlineData("/a~")]
    [InlineData("/a/b~x")]
    public void Parse_InvalidPointer_Throws(string text)
    {
        var ex = Assert.Throws<FormatException>(() => JsonPointer.Parse(text));

        Assert.Contains("RFC 6901", ex.Message);
        Assert.False(JsonPointer.TryParse(text, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(JsonPointer.TryParse(null, out _));
        Assert.Throws<ArgumentNullException>(() => JsonPointer.Parse(null!));
    }

    [Fact]
    public void Escape_EscapesTildeBeforeSlash()
    {
        Assert.Equal("a~1b~0c~01", JsonPointer.Escape("a/b~c~1"));
        Assert.Equal("plain", JsonPointer.Escape("plain"));
    }

    [Fact]
    public void Append_AddsEscapedToken()
    {
        var pointer = JsonPointer.Parse("/nodes").Append("a/b").Append("m~n").Append(3);

        Assert.Equal("/nodes/a~1b/m~0n/3", pointer.ToString());
        Assert.Equal(new[] { "nodes", "a/b", "m~n", "3" }, pointer.Tokens.ToArray());
        Assert.Equal("/x", JsonPointer.Root.Append("x").ToString());
        Assert.Equal("/x", JsonPointer.Create().Append("x").ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => pointer.Append(-1));
        Assert.Throws<ArgumentNullException>(() => pointer.Append(null!));
    }

    [Fact]
    public void Parent_WalksUpToRoot()
    {
        var pointer = JsonPointer.Parse("/a/b");

        Assert.Equal("/a", pointer.Parent!.ToString());
        Assert.Same(JsonPointer.Root, pointer.Parent!.Parent);
        Assert.Null(JsonPointer.Root.Parent);
        Assert.Equal("b", pointer.LastToken);
        Assert.Null(JsonPointer.Root.LastToken);
        Assert.Equal(2, pointer.Depth);
        Assert.True(JsonPointer.Root.IsRoot);
        Assert.False(pointer.IsRoot);
    }

    [Theory]
    [InlineData("/a", "/a/b", true)]
    [InlineData("/a", "/a", true)]
    [InlineData("/a", "/ab", false)]
    [InlineData("/a/b", "/a", false)]
    [InlineData("", "/a/b", true)]
    [InlineData("", "", true)]
    [InlineData("/a~1b", "/a/b", false)]
    [InlineData("/a/b", "/a~1b/c", false)]
    [InlineData("/", "//x", true)]
    [InlineData("/", "/x", false)]
    public void IsPrefixOf_ComparesWholeTokens(string prefix, string other, bool expected)
    {
        Assert.Equal(expected, JsonPointer.Parse(prefix).IsPrefixOf(JsonPointer.Parse(other)));
    }

    [Fact]
    public void IsProperPrefixOf_ExcludesEquality()
    {
        Assert.True(JsonPointer.Parse("/a").IsProperPrefixOf(JsonPointer.Parse("/a/b")));
        Assert.False(JsonPointer.Parse("/a").IsProperPrefixOf(JsonPointer.Parse("/a")));
        Assert.False(JsonPointer.Parse("/a").IsProperPrefixOf(JsonPointer.Parse("/ab")));
    }

    [Fact]
    public void Equality_IsTokenWise()
    {
        var a = JsonPointer.Parse("/a~1b/c");
        var b = JsonPointer.Create("a/b", "c");
        var c = JsonPointer.Parse("/a/b/c");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.True(a != c);
        Assert.False(a.Equals(null));
        Assert.False(a == null);
        Assert.NotEqual(JsonPointer.Parse("/a"), JsonPointer.Parse("/A"));
        Assert.NotEqual(JsonPointer.Parse(""), JsonPointer.Parse("/"));

        var set = new HashSet<JsonPointer> { a, b, c };
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void CompareTo_SortsPrefixesBeforeTheirDescendants()
    {
        var pointers = new[] { "/b", "/ab", "/a/b", "", "/a", "/a/a", "/" }.Select(JsonPointer.Parse).ToList();

        pointers.Sort();

        Assert.Equal(new[] { "", "/", "/a", "/a/a", "/a/b", "/ab", "/b" }, pointers.Select(p => p.ToString()));
        Assert.True(JsonPointer.Parse("/a") < JsonPointer.Parse("/a/b"));
        Assert.True(JsonPointer.Parse("/a/b") < JsonPointer.Parse("/ab"));
        Assert.True(JsonPointer.Parse("/a") <= JsonPointer.Parse("/a"));
        Assert.True(JsonPointer.Parse("/b") > JsonPointer.Parse("/a/z"));
        Assert.Equal(1, JsonPointer.Root.CompareTo(null));
    }

    [Fact]
    public void TryEvaluate_DistinguishesAbsentFromNull()
    {
        var document = Jsonc.Parse("""{"a": null, "b": {"c": 1}, "arr": [10, 20, null]}""");

        Assert.True(JsonPointer.Parse("/a").TryEvaluate(document, out var value));
        Assert.Null(value);

        Assert.True(JsonPointer.Parse("/arr/2").TryEvaluate(document, out value));
        Assert.Null(value);

        Assert.False(JsonPointer.Parse("/missing").TryEvaluate(document, out value));
        Assert.Null(value);

        Assert.True(JsonPointer.Parse("/b/c").TryEvaluate(document, out value));
        Assert.Equal(1, value!.GetValue<int>());

        Assert.True(JsonPointer.Parse("/arr/1").TryEvaluate(document, out value));
        Assert.Equal(20, value!.GetValue<int>());
    }

    [Theory]
    [InlineData("/b/c/d")]   // into a scalar
    [InlineData("/a/x")]     // into null
    [InlineData("/arr/3")]   // past the end
    [InlineData("/arr/-")]   // the element after the last never exists
    [InlineData("/arr/01")]  // leading zero
    [InlineData("/arr/-1")]
    [InlineData("/arr/x")]
    [InlineData("/arr/")]
    [InlineData("/arr/99999999999")]
    [InlineData("/B")]       // names are case-sensitive
    public void TryEvaluate_InvalidOrMissingPath_IsAbsent(string pointer)
    {
        var document = Jsonc.Parse("""{"a": null, "b": {"c": 1}, "arr": [10, 20, null]}""");

        Assert.False(JsonPointer.Parse(pointer).TryEvaluate(document, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryEvaluate_NullDocument()
    {
        Assert.True(JsonPointer.Root.TryEvaluate(null, out var value));
        Assert.Null(value);
        Assert.False(JsonPointer.Parse("/a").TryEvaluate(null, out _));
    }

    [Fact]
    public void FromNode_ComputesPathFromRoot()
    {
        var document = Jsonc.Parse("""{"nodes": {"a/b": {"list": [1, {"x": true}]}}}""")!;
        var node = document["nodes"]!["a/b"]!["list"]![1]!["x"]!;

        Assert.Equal("/nodes/a~1b/list/1/x", JsonPointer.FromNode(node).ToString());
        Assert.Same(JsonPointer.Root, JsonPointer.FromNode(document));
    }

    [Fact]
    public void Create_RejectsNullTokens()
    {
        Assert.Throws<ArgumentException>(() => JsonPointer.Create("a", null!));
        Assert.Same(JsonPointer.Root, JsonPointer.Create(Array.Empty<string>()));
    }

    [Fact]
    public void JsonConverter_ReadsAndWritesPointersAsValuesAndKeys()
    {
        const string text = """
            {
              "experiment_patchable": ["/params", "/nodes", "/a~1b"],
              "owners": { "/nodes/safety_filter": ["trust-safety"], "/features": ["feature-platform"] }
            }
            """;

        var policy = Jsonc.Deserialize<PolicyModel>(text)!;

        Assert.Equal(JsonPointer.Create("a/b"), policy.Patchable[2]);
        Assert.Equal(new[] { "trust-safety" }, policy.Owners[JsonPointer.Parse("/nodes/safety_filter")]);

        var json = JsonSerializer.Serialize(policy, Jsonc.SerializerOptions);
        Assert.Contains("\"/a~1b\"", json);
        Assert.Contains("\"/nodes/safety_filter\":", json);
    }

    [Theory]
    [InlineData("""{"experiment_patchable": ["params"], "owners": {}}""")]
    [InlineData("""{"experiment_patchable": [1], "owners": {}}""")]
    [InlineData("""{"experiment_patchable": [], "owners": {"nodes": []}}""")]
    public void JsonConverter_RejectsInvalidPointers(string text)
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Deserialize<PolicyModel>(text, "policy.jsonc"));

        Assert.Equal(JsoncException.DeserializationErrorCode, ex.Code);
        Assert.NotNull(ex.Line);
    }

    private sealed class PolicyModel
    {
        [System.Text.Json.Serialization.JsonPropertyName("experiment_patchable")]
        public List<JsonPointer> Patchable { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("owners")]
        public Dictionary<JsonPointer, string[]> Owners { get; set; } = new();
    }
}
