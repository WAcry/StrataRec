using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class JsonCanonicalizerTests
{
    // RFC 8785 Appendix B: IEEE 754 bit patterns and their ECMAScript serialization.
    [Theory]
    [InlineData("0000000000000000", "0")]
    [InlineData("8000000000000000", "0")]
    [InlineData("0000000000000001", "5e-324")]
    [InlineData("8000000000000001", "-5e-324")]
    [InlineData("7fefffffffffffff", "1.7976931348623157e+308")]
    [InlineData("ffefffffffffffff", "-1.7976931348623157e+308")]
    [InlineData("4340000000000000", "9007199254740992")]
    [InlineData("c340000000000000", "-9007199254740992")]
    [InlineData("4430000000000000", "295147905179352830000")]
    [InlineData("44b52d02c7e14af5", "9.999999999999997e+22")]
    [InlineData("44b52d02c7e14af6", "1e+23")]
    [InlineData("44b52d02c7e14af7", "1.0000000000000001e+23")]
    [InlineData("444b1ae4d6e2ef4e", "999999999999999700000")]
    [InlineData("444b1ae4d6e2ef4f", "999999999999999900000")]
    [InlineData("444b1ae4d6e2ef50", "1e+21")]
    [InlineData("3eb0c6f7a0b5ed8c", "9.999999999999997e-7")]
    [InlineData("3eb0c6f7a0b5ed8d", "0.000001")]
    [InlineData("41b3de4355555553", "333333333.3333332")]
    [InlineData("41b3de4355555554", "333333333.33333325")]
    [InlineData("41b3de4355555555", "333333333.3333333")]
    [InlineData("41b3de4355555556", "333333333.3333334")]
    [InlineData("41b3de4355555557", "333333333.33333343")]
    [InlineData("becbf647612f3696", "-0.0000033333333333333333")]
    [InlineData("43143ff3c1cb0959", "1424953923781206.2")]
    public void FormatNumber_MatchesRfc8785AppendixB(string ieeeHex, string expected)
    {
        var value = BitConverter.UInt64BitsToDouble(ulong.Parse(ieeeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        Assert.Equal(expected, JsonCanonicalizer.FormatNumber(value));
    }

    [Theory]
    [InlineData("7fffffffffffffff")] // NaN
    [InlineData("7ff0000000000000")] // Infinity
    [InlineData("fff0000000000000")] // -Infinity
    public void FormatNumber_RejectsNaNAndInfinity(string ieeeHex)
    {
        var value = BitConverter.UInt64BitsToDouble(ulong.Parse(ieeeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        Assert.Throws<ArgumentOutOfRangeException>(() => JsonCanonicalizer.FormatNumber(value));
    }

    [Theory]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1.5e-7, "1.5e-7")]
    [InlineData(1e-6, "0.000001")]
    [InlineData(1.25e-6, "0.00000125")]
    [InlineData(0.1, "0.1")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(100.0, "100")]
    [InlineData(1.5, "1.5")]
    [InlineData(123.456, "123.456")]
    [InlineData(1.23e-18, "1.23e-18")]
    [InlineData(4.35e100, "4.35e+100")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(1e16, "10000000000000000")]
    [InlineData(-1e-300, "-1e-300")]
    public void FormatNumber_FollowsEcmaScriptThresholds(double value, string expected)
    {
        Assert.Equal(expected, JsonCanonicalizer.FormatNumber(value));
    }

    [Fact]
    public void Canonicalize_Rfc8785Section324Sample()
    {
        const string input = """
            {
              "numbers": [333333333.33333329, 1E30, 4.50,
                          2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;
        const string expected =
            """{"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}""";

        var node = Jsonc.Parse(input);

        Assert.Equal(expected, JsonCanonicalizer.CanonicalizeToString(node));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), JsonCanonicalizer.Canonicalize(node));
    }

    [Fact]
    public void Canonicalize_SortsKeysByUtf16CodeUnits_Rfc8785Section323()
    {
        const string input = """
            {
              "\u20ac": "Euro Sign",
              "\r": "Carriage Return",
              "\ufb33": "Hebrew Letter Dalet With Dagesh",
              "1": "One",
              "\ud83d\ude00": "Emoji: Grinning Face",
              "\u0080": "Control",
              "\u00f6": "Latin Small Letter O With Diaeresis"
            }
            """;
        const string expected =
            "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\"," +
            "\"\u00f6\":\"Latin Small Letter O With Diaeresis\",\"\u20ac\":\"Euro Sign\"," +
            "\"\ud83d\ude00\":\"Emoji: Grinning Face\",\"\ufb33\":\"Hebrew Letter Dalet With Dagesh\"}";

        Assert.Equal(expected, JsonCanonicalizer.CanonicalizeToString(Jsonc.Parse(input)));
    }

    [Fact]
    public void Canonicalize_EscapesOnlyMandatoryCharacters()
    {
        var value = JsonValue.Create("\u0000\u0001\b\t\n\u000b\f\r\u001f\"\\/\u007f\u2028é😀<>&'");

        var text = JsonCanonicalizer.CanonicalizeToString(value);

        Assert.Equal("\"\\u0000\\u0001\\b\\t\\n\\u000b\\f\\r\\u001f\\\"\\\\/\u007f\u2028é😀<>&'\"", text);
    }

    [Fact]
    public void Canonicalize_ProducesUtf8()
    {
        Assert.Equal(new byte[] { 0x22, 0xE2, 0x82, 0xAC, 0x22 }, JsonCanonicalizer.Canonicalize(JsonValue.Create("€")));
    }

    [Fact]
    public void Canonicalize_NormalizesNumberSpellings()
    {
        var node = Jsonc.Parse("[1.0, 1e0, 10e-1, 0.1e1, -0, -0.0, 1E+2, 100, 0e-400, 1e22, 9007199254740992]");

        Assert.Equal("[1,1,1,1,0,0,100,100,0,1e+22,9007199254740992]", JsonCanonicalizer.CanonicalizeToString(node));
    }

    [Fact]
    public void Canonicalize_IsIndependentOfKeyOrderWhitespaceAndComments()
    {
        var a = Jsonc.Parse("""{"b": [1, {"y": 2, "x": 1}], "a": "s"}""");
        var b = Jsonc.Parse("""
            {
              "a": "s", // comment
              "b": [ 1.0, { "x": 1, "y": 2e0, }, ],
            }
            """);

        Assert.Equal(JsonCanonicalizer.Canonicalize(a), JsonCanonicalizer.Canonicalize(b));
        Assert.Equal("""{"a":"s","b":[1,{"x":1,"y":2}]}""", JsonCanonicalizer.CanonicalizeToString(a));
    }

    [Fact]
    public void Canonicalize_IsIdempotent()
    {
        var once = JsonCanonicalizer.CanonicalizeToString(Jsonc.Parse("""{"z": [0.5, "\u001f"], "a": {"c": null, "b": true}}"""));

        var twice = JsonCanonicalizer.CanonicalizeToString(Jsonc.Parse(once));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Canonicalize_PreservesArrayOrderAndEmptyContainers()
    {
        Assert.Equal("""[3,1,2,{},[],""]""", JsonCanonicalizer.CanonicalizeToString(Jsonc.Parse("""[3, 1, 2, {}, [], ""]""")));
    }

    [Fact]
    public void Canonicalize_HandlesClrBackedValues()
    {
        var node = new JsonObject
        {
            ["i"] = 1,
            ["l"] = 2L,
            ["d"] = 0.5,
            ["f"] = 0.1f,
            ["m"] = 1.50m,
            ["b"] = true,
            ["s"] = "x",
            ["g"] = Guid.Empty,
            ["c"] = 'q',
            ["n"] = null,
            ["big"] = 1L << 53,
        };

        Assert.Equal(
            """{"b":true,"big":9007199254740992,"c":"q","d":0.5,"f":0.1,"g":"00000000-0000-0000-0000-000000000000","i":1,"l":2,"m":1.5,"n":null,"s":"x"}""",
            JsonCanonicalizer.CanonicalizeToString(node));
    }

    [Fact]
    public void Canonicalize_NullNode_IsJsonNull()
    {
        Assert.Equal("null", JsonCanonicalizer.CanonicalizeToString(null));
    }

    [Fact]
    public void Canonicalize_DoesNotMutateInput()
    {
        var node = Jsonc.Parse("""{"b": 1, "a": [2.50]}""")!;
        var before = node.ToJsonString();

        _ = JsonCanonicalizer.Canonicalize(node);

        Assert.Equal(before, node.ToJsonString());
    }

    [Fact]
    public void Canonicalize_NaNInTree_ReportsPointer()
    {
        var node = new JsonObject { ["scores"] = new JsonArray(1.0, double.NaN) };

        var ex = Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(node));

        Assert.Equal("/scores/1", ex.Pointer.ToString());
        Assert.Contains("'/scores/1'", ex.Message);
    }

    [Fact]
    public void Canonicalize_InfiniteFloat_IsRejected()
    {
        var ex = Assert.Throws<JsonCanonicalizationException>(
            () => JsonCanonicalizer.Canonicalize(new JsonObject { ["f"] = float.PositiveInfinity }));

        Assert.Equal("/f", ex.Pointer.ToString());
    }

    [Theory]
    [InlineData("{\"id\": 9007199254740993}")]          // not a double
    [InlineData("{\"id\": -9007199254740993}")]
    [InlineData("{\"id\": 12345678901234567890}")]
    [InlineData("{\"id\": 1152921504606846976}")]       // 2^60 is a double, but JCS writes 1152921504606847000
    public void Canonicalize_LargeIntegerWithDifferentCanonicalValue_IsRejected(string text)
    {
        var ex = Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(Jsonc.Parse(text)));

        Assert.Equal("/id", ex.Pointer.ToString());
        Assert.NotNull(ex.Hint);
    }

    [Theory]
    [InlineData("[9007199254740992]", "[9007199254740992]")]
    [InlineData("[-9007199254740992]", "[-9007199254740992]")]
    [InlineData("[9007199254740994]", "[9007199254740994]")]
    [InlineData("[295147905179352830000]", "[295147905179352830000]")]
    [InlineData("[10000000000000000000000]", "[1e+22]")]
    [InlineData("[1152921504606846976.0]", "[1152921504606847000]")] // fraction syntax: ordinary double rounding
    [InlineData("[1.152921504606846976e18]", "[1152921504606847000]")]
    public void Canonicalize_LargeIntegerWithSameCanonicalValue_IsAccepted(string text, string expected)
    {
        Assert.Equal(expected, JsonCanonicalizer.CanonicalizeToString(Jsonc.Parse(text)));
    }

    [Fact]
    public void Canonicalize_LargeClrInteger_IsRejected()
    {
        Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(JsonValue.Create(long.MaxValue)));
        Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(JsonValue.Create(1L << 60)));
    }

    [Theory]
    [InlineData("[1e400]")]
    [InlineData("[-1e400]")]
    [InlineData("[1e-400]")]
    public void Canonicalize_NumberOutsideDoubleRange_IsRejected(string text)
    {
        var ex = Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(Jsonc.Parse(text)));

        Assert.Equal("/0", ex.Pointer.ToString());
    }

    [Fact]
    public void Canonicalize_FractionalNumbersRoundToNearestDouble()
    {
        Assert.Equal("[3.141592653589793]", JsonCanonicalizer.CanonicalizeToString(Jsonc.Parse("[3.14159265358979323846264338]")));
    }

    [Fact]
    public void Canonicalize_UnpairedSurrogate_IsRejected()
    {
        // Not [InlineData]: xunit replaces lone surrogates in serialized test data with U+FFFD.
        foreach (var value in new[] { "a\uD800b", "\uDC00", "end\uD83D", "\uDE00\uD83D" })
        {
            var node = new JsonObject { ["k"] = new JsonArray(value) };

            var ex = Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(node));

            Assert.Equal("/k/0", ex.Pointer.ToString());
        }
    }

    [Fact]
    public void Canonicalize_UnpairedSurrogateInKey_IsRejected()
    {
        var node = new JsonObject { ["\uD800"] = 1 };

        Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(node));
    }
}
