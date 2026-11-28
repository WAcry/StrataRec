using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public sealed class StrictJsonTests
{
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{ "a": 1, "b": { "a": 2 }, "c": [{ "a": 3 }, { "a": 4 }] }""")]
    [InlineData("""{ "é": "\u00e9", "smile": "\ud83d\ude00", "\ud83d\ude00": 1 }""")]
    [InlineData("""{ "a": "\\uD800" }""")]
    [InlineData("""{ "A": 1, "a": 2 }""")]
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
    [InlineData("""{ "a": 1, "\u0061": 2 }""", "/a", "a")]
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
    /// A node tree a host parsed (<c>JsonNode.Parse</c> keeps a repeated member; the object throws when it is first
    /// enumerated) is reported at the repeated member, also when an outer object was enumerated already and the
    /// repeated member is in one of its values, not yet read.
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
            Assert.Single(node);
        }

        var error = StrictJson.Check(node, Jsonc.MaxDepth, out _);

        Assert.Equal(pointer, error?.Pointer);
        Assert.Equal("give each member once: which of two values would count is not defined", error?.Hint);
        var clean = new JsonObject { ["params"] = new JsonObject { ["k"] = 3 } };
        Assert.Null(StrictJson.Check(clean, Jsonc.MaxDepth, out _));
        Assert.Null(StrictJson.Check(null, Jsonc.MaxDepth, out _));
        var parsed = JsonNode.Parse("""{ "params": { "k": [1, { "a": 1 }] } }""");
        Assert.Null(StrictJson.Check(parsed, Jsonc.MaxDepth, out _));
        Assert.Equal("""{"params":{"k":[1,{"a":1}]}}""", JsonCanonicalizer.CanonicalizeToString(parsed));
    }

    [Fact]
    public void ANodeTreeParsedIgnoringCase_IsReportedAtTheNameThatRepeatsAnotherIgnoringCase()
    {
        const string Json = """{ "params": { "final_top_k": 3, "FINAL_TOP_K": 4 } }""";
        var insensitive = JsonNode.Parse(Json, new JsonNodeOptions { PropertyNameCaseInsensitive = true });

        var error = StrictJson.Check(insensitive, Jsonc.MaxDepth, out _);

        Assert.NotNull(error);
        Assert.Equal("/params/FINAL_TOP_K", error.Pointer);
        Assert.Equal("Member 'FINAL_TOP_K' is given more than once: the object compares names ignoring case (JsonNodeOptions.PropertyNameCaseInsensitive).",
            error.Message);
        Assert.Null(StrictJson.Check(JsonNode.Parse(Json), Jsonc.MaxDepth, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARepeatedMember_IsReportedBesideAValueJsonCannotWrite(bool nanFirst)
    {
        var nan = new JsonObject { ["final_top_k"] = double.NaN };
        var repeated = JsonNode.Parse("""{ "coarse_top_k": { "order": 1 }, "coarse_top_k": { "order": 2 } }""");
        var tree = nanFirst ? new JsonObject { ["params"] = nan, ["nodes"] = repeated } : new JsonObject { ["nodes"] = repeated, ["params"] = nan };

        Assert.Equal("/nodes/coarse_top_k", StrictJson.Check(tree, Jsonc.MaxDepth, out _)?.Pointer);
        Assert.Null(StrictJson.Check(new JsonObject { ["params"] = new JsonObject { ["final_top_k"] = double.NaN } }, Jsonc.MaxDepth, out _));
    }

    [Fact]
    public void ANameThatIsNotUnicode_InANodeTree_IsReportedAtItsObject()
    {
        var error = StrictJson.Check(JsonNode.Parse("""{ "x": { "ok": 1, "\uD800": 2 } }"""), Jsonc.MaxDepth, out _);

        Assert.NotNull(error);
        Assert.Equal("/x", error.Pointer);
        Assert.StartsWith("The members of this object cannot be read: ", error.Message, StringComparison.Ordinal);
        Assert.Equal("give each member once, under a name that is valid Unicode (a complete surrogate pair such as \\ud83d\\ude00, or the character directly)",
            error.Hint);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(10_000, 5)]
    [InlineData(64, 64)]
    [InlineData(65, 64)]
    [InlineData(70, 64)]
    public void ATreeNestedPastTheLimit_IsReportedAtTheFirstLevelTooDeep(int levels, int maxDepth)
    {
        var error = StrictJson.Check(Nested(levels), maxDepth, out _);

        if (levels <= maxDepth)
        {
            Assert.Null(error);
            return;
        }

        Assert.NotNull(error);
        Assert.Equal("/a" + string.Concat(Enumerable.Repeat("/0", maxDepth - 1)), error.Pointer);
        Assert.Equal($"Objects and arrays nest deeper than {maxDepth} levels here: this one is level {maxDepth + 1}.", error.Message);
        Assert.Equal($"nest objects and arrays at most {maxDepth} levels deep, counting the outermost", error.Hint);
    }

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
    public void AWrappedClrObject_IsCheckedAsTheJsonItWrites()
    {
        var error = StrictJson.Check(new JsonObject { ["x"] = JsonValue.Create(new RepeatedName()) }, Jsonc.MaxDepth, out var copy);

        Assert.NotNull(error);
        Assert.Equal(("/x/A", "Member 'A' is given more than once."), (error.Pointer, error.Message));
        Assert.Null(copy);
        var cased = new JsonObject { ["x"] = JsonValue.Create(new Dictionary<string, int> { ["a"] = 1, ["A"] = 2 }) };
        Assert.Null(StrictJson.Check(cased, Jsonc.MaxDepth, out copy));
        Assert.Equal("""{"x":{"A":2,"a":1}}""", JsonCanonicalizer.CanonicalizeToString(copy));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(62, 62)]
    [InlineData(63, 62)]
    public void AWrappedClrArray_NestedPastTheLimit_IsReportedWhereAJsonArrayIs(int levels, int maxDepth)
    {
        object value = 1;
        for (var i = 1; i < levels; i++)
        {
            value = new object[] { value };
        }

        var error = StrictJson.Check(new JsonObject { ["a"] = JsonValue.Create(value) }, maxDepth, out _);

        Assert.Equal(StrictJson.Check(Nested(levels), maxDepth, out _), error);
        Assert.Equal(levels > maxDepth, error is not null);
    }

    [Fact]
    public void AWrappedClrValueThatCannotBeWritten_IsReportedAtItsPointer()
    {
        var cycle = new Cycle();
        cycle.Next = cycle;
        var nan = new Dictionary<string, double> { ["k"] = double.NaN };

        foreach (var (value, words) in new (JsonNode?, string)[]
                 {
                     (JsonValue.Create(cycle), "A possible object cycle was detected"),
                     (JsonValue.Create(nan), ".NET number values such as positive and negative infinity cannot be written as valid JSON"),
                 })
        {
            var error = StrictJson.Check(new JsonObject { ["x"] = new JsonArray(1, value) }, Jsonc.MaxDepth, out var copy);

            Assert.NotNull(error);
            Assert.Equal("/x/1", error.Pointer);
            Assert.StartsWith("This value cannot be written as JSON: " + words, error.Message, StringComparison.Ordinal);
            Assert.Equal("give a value System.Text.Json can write (no cycle, no NaN or infinity), or build it from JsonObject and JsonArray", error.Hint);
            Assert.Null(copy);
        }

        Assert.Null(StrictJson.Check(new JsonObject { ["x"] = double.NaN }, Jsonc.MaxDepth, out var lone));
        Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.CanonicalizeToString(lone));
    }

    [Theory]
    [InlineData("double")]
    [InlineData("half")]
    [InlineData("half_infinity")]
    [InlineData("boxed_double")]
    [InlineData("boxed_half")]
    public void ALoneNumberJsonCannotWrite_IsTheCanonicalFormsWhateverItsType(string type)
    {
        var value = type switch
        {
            "double" => JsonValue.Create(double.NaN),
            "half" => JsonValue.Create(Half.NaN),
            "half_infinity" => JsonValue.Create(Half.NegativeInfinity),
            "boxed_double" => JsonValue.Create<object>(double.PositiveInfinity),
            _ => JsonValue.Create<object>(Half.NaN),
        };

        Assert.Null(StrictJson.Check(new JsonObject { ["x"] = new JsonArray(1, value) }, Jsonc.MaxDepth, out var copy));

        var refused = Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.CanonicalizeToString(copy));
        Assert.Equal("/x/1", refused.Pointer.ToString());
        Assert.Equal("NaN and Infinity are not JSON numbers (RFC 8785 section 3.2.2.3).", refused.Reason);
    }

    [Theory]
    [InlineData("writes")]
    [InlineData("throws")]
    public void AWrappedElement_IsTheJsonOfTheElement_NeverWhatItsConverterWrites(string converter)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new ElementConverter(converter == "throws"));
        var info = (JsonTypeInfo<JsonElement>)options.GetTypeInfo(typeof(JsonElement));
        using var number = JsonDocument.Parse("7");
        using var nested = JsonDocument.Parse("""{ "a": [[1]] }""");

        Assert.Null(StrictJson.Check(new JsonObject { ["x"] = JsonValue.Create(number.RootElement, info) }, Jsonc.MaxDepth, out var copy));
        Assert.Equal("""{"x":7}""", copy!.ToJsonString());
        Assert.Null(StrictJson.Check(new JsonObject { ["x"] = JsonValue.Create(nested.RootElement, info) }, Jsonc.MaxDepth, out copy));
        Assert.Equal("""{"x":{"a":[[1]]}}""", copy!.ToJsonString());
        var deep = StrictJson.Check(new JsonObject { ["x"] = JsonValue.Create(nested.RootElement, info) }, 3, out _);
        Assert.Equal("/x/a/0", deep?.Pointer);
    }

    [Fact]
    public void AWrappedElement_IsWalked_NotReadBackFromItsText()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new ElementConverter(throws: true)); // wraps an object or array element; never run
        var info = (JsonTypeInfo<JsonElement>)options.GetTypeInfo(typeof(JsonElement));
        JsonBindingError? Checked(JsonElement element, int maxDepth, out JsonNode? copy) =>
            StrictJson.Check(new JsonObject { ["p"] = JsonValue.Create(element, info) }, maxDepth, out copy);

        using var badValue = JsonDocument.Parse(WithBytes("""{ "x": ["#AD"] }""", [0xFF]));
        Assert.Equal("/p/x/0", Checked(badValue.RootElement, Jsonc.MaxDepth, out _)?.Pointer);
        using var badName = JsonDocument.Parse(WithBytes("""[{ "#AD": 1 }]""", [0xFF]));
        Assert.Equal("/p/0", Checked(badName.RootElement, Jsonc.MaxDepth, out _)?.Pointer);

        Assert.Null(Checked(Jsonc.ParseElement("""{ "x": /* kept */ [1, 2,], }"""u8), Jsonc.MaxDepth, out var copy));
        Assert.Equal("""{"p":{"x":[1,2]}}""", copy!.ToJsonString());

        using var deep = JsonDocument.Parse(new string('[', 1001) + new string(']', 1001), new JsonDocumentOptions { MaxDepth = 2000 });
        Assert.Equal("/p/0/0/0/0", Checked(deep.RootElement, 5, out _)?.Pointer);

        var undefined = Checked(default, Jsonc.MaxDepth, out _);
        Assert.Equal("/p", undefined?.Pointer);
        Assert.Contains("default JsonElement", undefined!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrappedElementTooDeep_IsRefusedAtTheFirstLevelPastTheLimit_WithoutWalkingFurther()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new ElementConverter(throws: true)); // wraps an array element; never run
        var info = (JsonTypeInfo<JsonElement>)options.GetTypeInfo(typeof(JsonElement));
        const int levels = 10_000;
        using var deep = JsonDocument.Parse(new string('[', levels) + new string(']', levels), new JsonDocumentOptions { MaxDepth = levels + 1 });
        var tree = new JsonObject { ["p"] = JsonValue.Create(deep.RootElement, info) };
        JsonBindingError? error = null;
        var check = new Thread(() => error = StrictJson.Check(tree, 62, out _), maxStackSize: 256 * 1024);
        check.Start();
        check.Join();

        Assert.Equal("/p" + string.Concat(Enumerable.Repeat("/0", 61)), error?.Pointer);
    }

    private sealed class ElementConverter(bool throws) : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
        {
            if (throws)
            {
                throw new InvalidOperationException("the host's converter failed");
            }

            for (var i = 0; i < 59; i++)
            {
                writer.WriteStartArray();
            }

            for (var i = 0; i < 59; i++)
            {
                writer.WriteEndArray();
            }
        }
    }

    [Fact]
    public void TheCopy_IsTheTreeAsChecked()
    {
        var counting = new Counting();
        var tree = JsonNode.Parse("""{ "params": { "k": 1 } }""", new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();
        tree["params"]!["n"] = JsonValue.Create(counting);

        Assert.Null(StrictJson.Check(tree, Jsonc.MaxDepth, out var copy));

        var writes = counting.Writes;
        var checkedText = "{\"params\":{\"k\":1,\"n\":{\"writes\":" + writes + "}}}";
        Assert.Equal(checkedText, JsonCanonicalizer.CanonicalizeToString(copy));
        Assert.Equal(checkedText, JsonCanonicalizer.CanonicalizeToString(copy!.DeepClone()));
        Assert.Equal(writes, counting.Writes);
        Assert.IsType<JsonObject>(copy["params"]!["n"]);
        Assert.NotSame(tree["params"], copy["params"]);
        Assert.False(copy["params"]!.AsObject().ContainsKey("K"));
        Assert.True(tree["params"]!.AsObject().ContainsKey("K"));
    }

    /// <summary>Writes <c>{"A":1,"A":2}</c>: a property, and extension data holding its name.</summary>
    public sealed class RepeatedName
    {
        public int A { get; } = 1;

        [JsonExtensionData]
        public Dictionary<string, object> More { get; } = new() { ["A"] = 2 };
    }

    public sealed class Cycle
    {
        public Cycle? Next { get; set; }
    }

    /// <summary>Writes <c>{"writes":n}</c>, n counting its writes: what it writes the second time is not what it wrote the first.</summary>
    [JsonConverter(typeof(CountingConverter))]
    public sealed class Counting
    {
        public int Writes { get; set; }
    }

    public sealed class CountingConverter : JsonConverter<Counting>
    {
        public override Counting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Counting value, JsonSerializerOptions options)
        {
            value.Writes++;
            writer.WriteStartObject();
            writer.WriteNumber("writes", value.Writes);
            writer.WriteEndObject();
        }
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
    [InlineData("""{ "x": "\\\uD800" }""", "/x", "The string \"\\\\\\uD800\" is not valid Unicode: an escape is an unpaired surrogate.")]
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
    /// Bytes that are not UTF-8: <see cref="JsonDocument"/> parses them and throws only when the text is read; a string
    /// at its pointer, a member name at its object.
    /// </summary>
    [Theory]
    [InlineData("""{ "x": "a#b" }""", new byte[] { 0xFF }, "/x", "The string is not valid UTF-8 text.")]
    [InlineData("""{ "x": ["ok", "#"] }""", new byte[] { 0x80 }, "/x/1", "The string is not valid UTF-8 text.")] // a lone continuation byte
    [InlineData("""{ "x": "#" }""", new byte[] { 0xC0, 0xAF }, "/x", "The string is not valid UTF-8 text.")] // an overlong form of '/'
    [InlineData("""{ "x": "#" }""", new byte[] { 0xE2, 0x82 }, "/x", "The string is not valid UTF-8 text.")] // a truncated sequence
    [InlineData("""{ "x": "#" }""", new byte[] { 0xED, 0xA0, 0x80 }, "/x", "The string is not valid UTF-8 text.")] // a surrogate, encoded
    [InlineData("""{ "x": "\n#" }""", new byte[] { 0xFF }, "/x", "The string is not valid UTF-8 text.")]
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
        Assert.Null(StrictJson.Check(document.RootElement));

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
        Assert.Null(StrictJson.Check(document.RootElement));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = StrictJson.Check(document.RootElement);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(error);
        Assert.Equal(0, allocated);
    }

    internal static byte[] WithBytes(string json, byte[] bytes)
    {
        var text = Encoding.UTF8.GetBytes(json);
        var at = Array.IndexOf(text, (byte)'#');
        Assert.True(at >= 0 && Array.IndexOf(text, (byte)'#', at + 1) < 0, "one # in the JSON");
        return [.. text[..at], .. bytes, .. text[(at + 1)..]];
    }
}
