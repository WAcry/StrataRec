using StrataRec.Expressions;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// ColumnExpr takes CEL's syntax, not all of CEL (design 7.1): what CEL writes differently or ColumnExpr leaves out is
/// one diagnostic with the ColumnExpr way, or why there is none, not an unknown function with a cascade of unknown names.
/// </summary>
public class CelIdiomTests
{
    [Theory]
    [InlineData("cel.bind(x, col.score.final, x * 2.0)", "let x = init; body")]
    [InlineData("ctx.request.recent_item_ids.exists(t, t == 1)", "'x in list'")]
    [InlineData("ctx.request.recent_item_ids.all(t, t > 0)", "overlap_count(a, b)")]
    [InlineData("size(ctx.request.recent_item_ids.filter(t, t > 0)) > 0", "'x in list'")]
    [InlineData("math.greatest(col.score.final, 1.0)", "max(a, b, ...)")]
    [InlineData("math.least(col.score.final, 1.0)", "min(a, b, ...)")]
    [InlineData("math.sqrt(col.score.final)", "write sqrt(...)")]
    [InlineData("math.ceil(col.score.final)", "design 7.6")]
    [InlineData("sets.intersects(ctx.request.recent_item_ids, ctx.request.recent_item_ids)", "overlap_count(a, b) > 0")]
    [InlineData("optional.of(col.score.final)", "x ?? fallback")]
    [InlineData("col.item.age_hours.orValue(720.0)", "x ?? fallback")]
    [InlineData("ctx.request.surface.matches('^ho')", "endsWith()")]
    [InlineData("ctx.request.surface.lowerAscii() == 'home'", "normalize strings")]
    [InlineData("string(col.item.creator_id) == '7'", "literal of its own type")]
    [InlineData("uint(col.item.creator_id) > 0", "int(x)")]
    [InlineData("ctx.request.seed > timestamp('2026-01-01T00:00:00Z')", "ctx.request.time_ms")]
    public void ACelIdiom_IsOneDiagnostic_ThatSaysHowColumnExprWritesIt(string source, string hint)
    {
        var d = Assert.Single(CompilerTests.HomeFeed().Errors(source));

        Assert.Equal(ExpressionErrorCodes.UnknownFunction, d.Code);
        Assert.Contains(hint, d.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AMapLiteral_PointsToTablesAndMapParameters()
    {
        var d = Assert.Single(CompilerTests.HomeFeed().Errors("{'a': 1.0}['a']"));

        Assert.Equal(ExpressionErrorCodes.Syntax, d.Code);
        Assert.Contains("lookup('table', key, default)", d.Hint, StringComparison.Ordinal);
    }

    /// <summary>A let binding named like a CEL namespace is a value: a method call on it is a call with it.</summary>
    [Fact]
    public void ALetNamedLikeANamespace_IsAValue()
    {
        Assert.True(CompilerTests.HomeFeed().Scalar("let math = ctx.request.surface; math.startsWith('ho')").AsBool());
    }
}
