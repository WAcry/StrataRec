using System.Globalization;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// <c>docs/guide/expressions.md</c> says only what the compiler does: every <c>colexpr</c> block of the page is run. A
/// line ending in <c>// =&gt; value</c> evaluates to that value (<c>missing</c> for no value), a line ending in <c>//
/// error</c> does not compile, and everything else (up to a blank line or the end of the block) compiles.
/// </summary>
public sealed class ExpressionGuideTests
{
    private static readonly string Page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guide", "expressions.md"));

    public static TheoryData<string, string?> Cases()
    {
        var cases = new TheoryData<string, string?>();
        var inBlock = false;
        var pending = new List<string>();
        void Flush(string? expected)
        {
            if (pending.Count > 0)
            {
                cases.Add(string.Join('\n', pending), expected);
                pending.Clear();
            }
        }

        foreach (var raw in Page.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                Flush(null);
                inBlock = line == "```colexpr";
            }
            else if (inBlock && line.Length == 0)
            {
                Flush(null);
            }
            else if (inBlock)
            {
                var marker = line.LastIndexOf("// ", StringComparison.Ordinal);
                var note = marker < 0 ? string.Empty : line[(marker + 3)..].Trim();
                if (note == "error" || note.StartsWith("=> ", StringComparison.Ordinal))
                {
                    pending.Add(line[..marker].TrimEnd());
                    Flush(note == "error" ? "error" : note[3..].Trim());
                }
                else
                {
                    pending.Add(line);
                }
            }
        }

        return cases;
    }

    [Fact]
    public void ThePageHasExamplesOfEveryKind()
    {
        var cases = Cases().Select(c => c.Data.Item2).ToList();
        Assert.Contains(null, cases);
        Assert.Contains("error", cases);
        Assert.True(cases.Count(c => c is not null and not "error") >= 20, "the page lost its examples with values");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AnExampleOfThePage_DoesWhatThePageSays(string source, string? expected)
    {
        var f = Fixture();
        switch (expected)
        {
            case null:
                f.Compile(source);
                break;
            case "error":
                Assert.NotEmpty(f.Errors(source));
                break;
            default:
                ExprAssert.Scalar(f, source, expected switch
                {
                    "missing" => null,
                    "true" => true,
                    "false" => false,
                    _ when long.TryParse(expected, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) => (int)whole,
                    _ => double.Parse(expected, CultureInfo.InvariantCulture),
                });
                break;
        }
    }

    [Fact]
    public void ThePageListsEveryFunction_AsTheCompilerSpellsIt()
    {
        foreach (var name in Builtins.FunctionNames)
        {
            Assert.True(Builtins.TryGetArity(name, out _, out _, out var usage));
            Assert.True(Page.Contains($"`{usage}`", StringComparison.Ordinal), $"docs/guide/expressions.md does not list `{usage}` in its table of functions");
        }
    }

    private static ExprFixture Fixture() => new ExprFixture()
        .Col("col.score.p_click", "float64")
        .Col("col.score.p_like", "float64")
        .Col("col.item.age_hours", "float64?")
        .Col("col.item.is_policy_safe", "bool?")
        .Col("col.creator.follower_count", "float64")
        .Ctx("ctx.user.tenure_days", "float64", ScalarValue.FromFloat64(90))
        .Ctx("ctx.request.seed", "int64", ScalarValue.FromInt64(42))
        .Ctx("ctx.request.recent_item_ids", "list<int64>", ScalarValue.FromInt64List([1000]))
        .Param("param.w_click", "float64", ScalarValue.FromFloat64(1))
        .Param("param.w_like", "float64", ScalarValue.FromFloat64(1))
        .Param("param.w_fresh", "float64", ScalarValue.FromFloat64(1))
        .Param("param.half_life_hours", "float64", ScalarValue.FromFloat64(24))
        .Param("param.cold_creator_boost", "float64", ScalarValue.FromFloat64(1.2))
        .Param("param.missing_weight", "float64?")
        .Param("param.zero", "int64", ScalarValue.FromInt64(0))
        .Build(3);
}
