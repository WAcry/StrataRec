using System.Net;
using System.Text;
using System.Text.Json;
using StrataRec.Llm.Templates;

namespace StrataRec.UnitTests.Llm;

/// <summary>
/// The Mustache specification's own tests (vendored under <c>MustacheSpec/</c>, MIT) for what prompt templates support:
/// interpolation, sections, inverted sections and comments, with the standalone-line rules. The generic renderer is
/// driven with the tests' JSON data through a context stack, as the specification describes name resolution.
/// </summary>
public sealed class MustacheSpecTests
{
    private static readonly string[] Files = { "interpolation", "sections", "inverted", "comments" };

    public static TheoryData<string, string> Cases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var file in Files)
        {
            foreach (var test in Load(file).RootElement.GetProperty("tests").EnumerateArray())
            {
                cases.Add(file, test.GetProperty("name").GetString()!);
            }
        }

        return cases;
    }

    [Fact]
    public void EveryFileHasItsTests_NoneNeedsPartials()
    {
        var counts = Files.ToDictionary(f => f, f => Load(f).RootElement.GetProperty("tests").GetArrayLength());

        Assert.Equal(new Dictionary<string, int> { ["interpolation"] = 42, ["sections"] = 34, ["inverted"] = 22, ["comments"] = 12 }, counts);
        Assert.All(Files, f => Assert.All(Load(f).RootElement.GetProperty("tests").EnumerateArray(), t => Assert.False(t.TryGetProperty("partials", out _))));
    }

    /// <summary>
    /// Each case renders as the specification expects, except that nothing is HTML-escaped: a prompt is not HTML, so the
    /// escaping cases expect the characters themselves (their expected output, HTML-decoded).
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void TheSpecification(string file, string name)
    {
        var test = Load(file).RootElement.GetProperty("tests").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);
        var source = test.GetProperty("template").GetString()!;
        var expected = test.GetProperty("expected").GetString()!;
        if (name.Contains("HTML Escaping", StringComparison.Ordinal))
        {
            expected = WebUtility.HtmlDecode(expected);
        }

        Assert.True(MustacheTemplate.TryParse(source, out var template, out var error), $"{error.Code} at {error.Offset}: {error.Message}");
        var data = new JsonData(test.GetProperty("data"));
        var output = new StringBuilder();
        template.Render(ref data, output);

        Assert.Equal(expected, output.ToString());
    }

    private static JsonDocument Load(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Llm", "MustacheSpec", file + ".json")));

    /// <summary>
    /// The specification's data model: a context stack of JSON values; a dotted name resolves its first part against the
    /// stack, top first, and the rest against the result; a miss renders nothing and is falsey.
    /// </summary>
    private sealed class JsonData(JsonElement root) : IMustacheData
    {
        private readonly List<JsonElement> _stack = new() { root };

        public void Write(MustacheTag variable, StringBuilder output)
        {
            if (Resolve(variable.Name) is not { } value)
            {
                return;
            }

            output.Append(value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null => string.Empty,
                _ => value.GetRawText(),
            });
        }

        public int Count(MustacheTag section) => Resolve(section.Name) is not { } value ? 0 : value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength(),
            JsonValueKind.False or JsonValueKind.Null => 0,
            _ => 1,
        };

        public void Enter(MustacheTag section, int index)
        {
            var value = Resolve(section.Name)!.Value;
            _stack.Add(value.ValueKind == JsonValueKind.Array ? value[index] : value);
        }

        public void Leave(MustacheTag section) => _stack.RemoveAt(_stack.Count - 1);

        private JsonElement? Resolve(string name)
        {
            if (name == ".")
            {
                return _stack[^1];
            }

            var parts = name.Split('.');
            JsonElement? current = null;
            for (var i = _stack.Count - 1; i >= 0 && current is null; i--)
            {
                if (_stack[i].ValueKind == JsonValueKind.Object && _stack[i].TryGetProperty(parts[0], out var found))
                {
                    current = found;
                }
            }

            foreach (var part in parts.Skip(1))
            {
                if (current is not { ValueKind: JsonValueKind.Object } scope || !scope.TryGetProperty(part, out var next))
                {
                    return null;
                }

                current = next;
            }

            return current;
        }
    }
}

/// <summary>The parser: the tree it builds, the standalone-line rule on its edges, and every error with its offset.</summary>
public sealed class MustacheParserTests
{
    [Fact]
    public void Tags_GetIdsInTheOrderTheyOpen_SectionsHoldTheirContent()
    {
        Assert.True(MustacheTemplate.TryParse("a{{x}}{{#s}}b{{{y}}}{{& z}}{{/s}}{{^t}}{{.}}{{/t}}{{! c }}", out var template, out _));

        Assert.Equal(6, template.TagCount);
        var tags = Flatten(template.Nodes).OfType<MustacheTag>().ToList();
        Assert.Equal(new[] { ("x", 0), ("s", 1), ("y", 2), ("z", 3), ("t", 4), (".", 5) }, tags.Select(t => (t.Name, t.Id)));
        Assert.Equal(new[] { false, false, true, true, false, false }, tags.Select(t => t.Unescaped));
        var section = Assert.IsType<MustacheTag>(template.Nodes[2]);
        Assert.Equal((MustacheTagKind.Section, 6, 6), (section.Kind, section.Offset, section.Length));
        Assert.Equal(3, section.Children.Count);
        Assert.Equal(MustacheTagKind.InvertedSection, ((MustacheTag)template.Nodes[3]).Kind);
    }

    [Theory]
    [InlineData("A\n  {{#s}}  \r\nB\n\t{{/s}}\nC", "A\nB\nC")] // indented, \r\n and \n line ends
    [InlineData("{{#s}}\nB\n{{/s}}", "B\n")] // the first line and the last, without a newline
    [InlineData("A {{#s}}\nB\n{{/s}}", "A \nB\n")] // text before the tag on its line: not standalone
    [InlineData("{{#s}}{{/s}}\nB", "\nB")] // two tags on a line: neither is standalone
    [InlineData("A\n{{x}}\nB", "A\nX\nB")] // a variable never is
    [InlineData("A\n  {{!\n multi-line\n }}  \nB", "A\nB")] // a comment spanning lines
    [InlineData("A\n{{#s}}\r{{/s}}", "A\n\r")] // a lone \r is not a line end
    public void TheStandaloneRule_RemovesALineHoldingOnlyABlockTag(string source, string expected)
    {
        Assert.True(MustacheTemplate.TryParse(source, out var template, out var error), error.Message);
        var data = new FlagData();
        var output = new StringBuilder();
        template.Render(ref data, output);

        Assert.Equal(expected, output.ToString());
    }

    [Theory]
    [InlineData("Hi {{> header}} there", 3, "MUSTACHE_PARTIAL", "{{> header}}")]
    [InlineData("Hi {{=<% %>=}} there", 3, "MUSTACHE_DELIMITER", "{{=<% %>=}}")]
    [InlineData("ab {{x", 3, "MUSTACHE_UNCLOSED_TAG", "'}}'")]
    [InlineData("ab {{{x}} c", 3, "MUSTACHE_UNCLOSED_TAG", "'}}}'")]
    [InlineData("a {{  }} b", 2, "MUSTACHE_EMPTY_TAG", "{{  }}")]
    [InlineData("a {{#}}b{{/}}", 2, "MUSTACHE_EMPTY_TAG", "{{#}}")]
    [InlineData("a {{col.a b}}", 2, "MUSTACHE_TAG_NAME", "'col.a b'")]
    [InlineData("a {{/s}}", 2, "MUSTACHE_UNEXPECTED_CLOSE", "{{/s}}")]
    [InlineData("{{#a}}{{#b}}x{{/a}}{{/b}}", 13, "MUSTACHE_SECTION_MISMATCH", "'b' (opened at offset 6)")]
    [InlineData("x {{#a}} {{^b}} {{/b}}", 2, "MUSTACHE_UNCLOSED_SECTION", "{{#a}}")]
    public void AnUnsupportedOrMalformedTemplate_IsAnError_AtTheTagsOffset_WithAHint(string source, int offset, string code, string quoted)
    {
        Assert.False(MustacheTemplate.TryParse(source, out var template, out var error));

        Assert.Null(template);
        Assert.Equal((offset, code), (error.Offset, error.Code));
        Assert.Contains(quoted, error.Message);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
    }

    [Fact]
    public void SectionsNestedDeeperThanMaxDepth_AreAnError_AtTheFirstTooDeepTag()
    {
        // Review 29: binding and rendering recurse per level, and 15,000 levels overflowed the stack at load.
        static string Nested(int depth) => string.Concat(Enumerable.Repeat("{{#s}}", depth)) + "x" + string.Concat(Enumerable.Repeat("{{/s}}", depth));

        Assert.True(MustacheTemplate.TryParse(Nested(MustacheTemplate.MaxDepth), out _, out _));
        Assert.False(MustacheTemplate.TryParse(Nested(MustacheTemplate.MaxDepth + 1), out _, out var error));
        Assert.Equal(("MUSTACHE_TOO_DEEP", MustacheTemplate.MaxDepth * 6), (error.Code, error.Offset));
        Assert.False(MustacheTemplate.TryParse(Nested(15_000), out _, out var deep));
        Assert.Equal("MUSTACHE_TOO_DEEP", deep.Code);
    }

    private static IEnumerable<MustacheNode> Flatten(IEnumerable<MustacheNode> nodes) =>
        nodes.SelectMany(n => n is MustacheTag tag ? new[] { n }.Concat(Flatten(tag.Children)) : new[] { n });

    /// <summary>Every section renders once; every variable renders its name in capitals.</summary>
    private struct FlagData : IMustacheData
    {
        public readonly void Write(MustacheTag variable, StringBuilder output) => output.Append(variable.Name.ToUpperInvariant());

        public readonly int Count(MustacheTag section) => 1;

        public readonly void Enter(MustacheTag section, int index)
        {
        }

        public readonly void Leave(MustacheTag section)
        {
        }
    }
}
