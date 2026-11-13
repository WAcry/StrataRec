using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class JsoncTests
{
    private const string FileName = "config/pipelines/home_feed.jsonc";

    [Fact]
    public void Parse_AcceptsCommentsAndTrailingCommas()
    {
        const string text = """
            // leading comment
            {
              "a": 1, // trailing comment
              /* block
                 comment */
              "b": [1, 2, 3,],
              "c": { "d": "x", },
            }
            """;

        var node = Jsonc.Parse(text);

        Assert.Equal("""{"a":1,"b":[1,2,3],"c":{"d":"x"}}""", node!.ToJsonString());
    }

    [Fact]
    public void Parse_PreservesNumberTextAndSupportsTypedAccess()
    {
        var node = Jsonc.Parse("""{"price": 1.50, "count": 3}""")!;

        Assert.Equal("1.50", node["price"]!.ToJsonString());
        Assert.Equal(3, node["count"]!.GetValue<int>());
        Assert.Equal(1.5, node["price"]!.GetValue<double>());
    }

    [Fact]
    public void Parse_LiteralNull_ReturnsNull()
    {
        Assert.Null(Jsonc.Parse("null"));
    }

    [Fact]
    public void Parse_SkipsUtf8ByteOrderMark()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("""{"a":1}""")).ToArray();

        var node = Jsonc.Parse(bytes);

        Assert.Equal(1, node!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Parse_DuplicateKey_ReportsBothPositionsAndFileName()
    {
        const string text = "{\n  \"a\": 1,\n  \"b\": 2,\n  \"a\": 3\n}";

        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse(text, FileName));

        Assert.Equal(JsoncException.DuplicateKeyCode, ex.Code);
        Assert.Equal(FileName, ex.FileName);
        Assert.Equal(4, ex.Line);
        Assert.Equal(3, ex.Column);
        Assert.Contains("home_feed.jsonc(4,3)", ex.Message);
        Assert.Contains("line 2, column 3", ex.Message);
        Assert.NotNull(ex.Hint);
        Assert.IsAssignableFrom<JsonException>(ex);
    }

    [Fact]
    public void Parse_DuplicateKey_IsDetectedAfterUnescaping()
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse("""{"a": 1, "\u0061": 2}"""));

        Assert.Equal(JsoncException.DuplicateKeyCode, ex.Code);
    }

    [Fact]
    public void Parse_DuplicateKey_InNestedObjectAfterSiblingObject()
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse("""{"o": {"a": 1}, "p": {"a": 1, "a": 2}}"""));

        Assert.Equal(JsoncException.DuplicateKeyCode, ex.Code);
        Assert.Equal(1, ex.Line);
        Assert.Equal(31, ex.Column);
    }

    [Fact]
    public void Parse_SameKeyInDifferentObjects_IsAllowed()
    {
        var node = Jsonc.Parse("""{"a": {"x": 1}, "b": {"x": 2}, "x": 3, "list": [{"x": 1}, {"x": 2}]}""");

        Assert.Equal(3, node!["x"]!.GetValue<int>());
    }

    [Fact]
    public void Parse_DuplicateKey_ColumnCountsUtf16CodeUnitsNotBytes()
    {
        // "é" is two UTF-8 bytes but one UTF-16 code unit: the second key starts at byte 8 but column 8.
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse("{\"é\":1,\"é\":2}"));

        Assert.Equal(1, ex.Line);
        Assert.Equal(8, ex.Column);
        Assert.Equal(8, ex.BytePositionInLine);
    }

    [Fact]
    public void Parse_SyntaxError_ReportsLineColumnAndFileName()
    {
        const string text = "{\n  \"a\": 1,\n  x\n}";

        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse(text, FileName));

        Assert.Equal(JsoncException.SyntaxErrorCode, ex.Code);
        Assert.Equal(3, ex.Line);
        Assert.Equal(3, ex.Column);
        Assert.StartsWith("config/pipelines/home_feed.jsonc(3,3): JSONC_SYNTAX: ", ex.Message);
        Assert.DoesNotContain("LineNumber", ex.Message);
        Assert.Equal(2, ex.LineNumber); // System.Text.Json convention: 0-based
    }

    [Fact]
    public void Parse_WithoutFileName_UsesPlaceholder()
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse("{"));

        Assert.Null(ex.FileName);
        Assert.StartsWith("<jsonc>(", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("// only a comment")]
    [InlineData("{\"a\": NaN}")]
    [InlineData("{\"a\": Infinity}")]
    [InlineData("{\"a\": 01}")]
    [InlineData("{\"a\": +1}")]
    [InlineData("{\"a\": 0x10}")]
    [InlineData("{'a': 1}")]
    [InlineData("{a: 1}")]
    [InlineData("{} {}")]
    [InlineData("[1,,2]")]
    [InlineData("{\"a\": \"unterminated}")]
    public void Parse_InvalidDocument_ThrowsSyntaxError(string text)
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse(text));

        Assert.Equal(JsoncException.SyntaxErrorCode, ex.Code);
        Assert.NotNull(ex.Line);
        Assert.NotNull(ex.Column);
    }

    [Theory]
    [InlineData("{\"a\": \"\\ud800\"}")]
    [InlineData("{\"a\": \"x\\udc00\"}")]
    [InlineData("{\"\\ud83d\": 1}")]
    public void Parse_UnpairedSurrogateEscape_IsRejected(string text)
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse(text));

        Assert.Equal(JsoncException.InvalidUnicodeCode, ex.Code);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public void Parse_PairedSurrogateEscape_IsAccepted()
    {
        var node = Jsonc.Parse("""{"emoji": "\ud83d\ude00"}""");

        Assert.Equal("😀", node!["emoji"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_StringWithUnpairedSurrogate_IsRejected()
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Parse("{\"a\":\"\uD800\"}"));

        Assert.Equal(JsoncException.InvalidUnicodeCode, ex.Code);
        Assert.Equal(1, ex.Line);
        Assert.Equal(7, ex.Column);
    }

    [Fact]
    public void Parse_NestingBeyondMaxDepth_IsRejected()
    {
        var ok = new string('[', Jsonc.MaxDepth) + new string(']', Jsonc.MaxDepth);
        var tooDeep = new string('[', Jsonc.MaxDepth + 1) + new string(']', Jsonc.MaxDepth + 1);

        Assert.NotNull(Jsonc.Parse(ok));
        Assert.Equal(JsoncException.SyntaxErrorCode, Assert.Throws<JsoncException>(() => Jsonc.Parse(tooDeep)).Code);
    }

    [Fact]
    public void Parse_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Jsonc.Parse((string)null!));
    }

    [Fact]
    public void Deserialize_ReadsJsoncIntoModel()
    {
        const string text = """
            {
              // node configuration
              "op": "Filter@1",
              "order": 300,
              "weights": { "a": 0.5, "b": 1, },
            }
            """;

        var config = Jsonc.Deserialize<NodeConfig>(text, FileName);

        Assert.NotNull(config);
        Assert.Equal("Filter@1", config.Op);
        Assert.Equal(300, config.Order);
        Assert.Equal(0.5, config.Weights["a"]);
    }

    [Fact]
    public void Deserialize_TypeMismatch_ReportsPathAndPosition()
    {
        const string text = "{\n  \"op\": \"Filter@1\",\n  \"order\": \"late\"\n}";

        var ex = Assert.Throws<JsoncException>(() => Jsonc.Deserialize<NodeConfig>(text, FileName));

        Assert.Equal(JsoncException.DeserializationErrorCode, ex.Code);
        Assert.Equal("$.order", ex.Path);
        Assert.Equal(3, ex.Line);
        Assert.Contains("home_feed.jsonc(3,", ex.Message);
        Assert.Contains("(at $.order)", ex.Message);
        Assert.DoesNotContain("LineNumber", ex.Message);
    }

    [Fact]
    public void Deserialize_QuotedNumber_IsRejected()
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Deserialize<NodeConfig>("""{"op": "x", "order": "5"}"""));

        Assert.Equal(JsoncException.DeserializationErrorCode, ex.Code);
    }

    [Fact]
    public void Deserialize_MissingRequiredProperty_IsRejected()
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Deserialize<NodeConfig>("""{"order": 5}"""));

        Assert.Equal(JsoncException.DeserializationErrorCode, ex.Code);
        Assert.Contains("op", ex.Reason);
    }

    [Fact]
    public void Deserialize_DuplicateKey_IsRejectedBeforeBinding()
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Deserialize<NodeConfig>("""{"op": "a", "op": "b"}"""));

        Assert.Equal(JsoncException.DuplicateKeyCode, ex.Code);
    }

    [Fact]
    public void Deserialize_DuplicateDictionaryKey_IsRejected()
    {
        var ex = Assert.Throws<JsoncException>(() => Jsonc.Deserialize<Dictionary<string, int>>("""{"a": 1, "a": 2}"""));

        Assert.Equal(JsoncException.DuplicateKeyCode, ex.Code);
    }

    [Fact]
    public void SerializerOptions_AreSharedAndReadOnly()
    {
        var options = Jsonc.SerializerOptions;

        Assert.Same(options, Jsonc.SerializerOptions);
        Assert.True(options.IsReadOnly);
        Assert.Equal(JsonCommentHandling.Skip, options.ReadCommentHandling);
        Assert.True(options.AllowTrailingCommas);
        Assert.Equal(JsonNumberHandling.Strict, options.NumberHandling);
        Assert.False(options.AllowDuplicateProperties);
        Assert.Throws<InvalidOperationException>(() => options.AllowTrailingCommas = false);
    }

    private sealed class NodeConfig
    {
        [JsonPropertyName("op")]
        [JsonRequired]
        public string Op { get; set; } = string.Empty;

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("weights")]
        public Dictionary<string, double> Weights { get; set; } = new();
    }
}
