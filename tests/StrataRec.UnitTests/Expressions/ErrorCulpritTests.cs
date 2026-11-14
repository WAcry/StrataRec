using StrataRec.Expressions;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// A type error is reported at the part at fault: the operand or argument of the wrong type, or the operator when
/// neither side alone is the mistake. The offset is what the CLI turns into a column inside the expression string.
/// </summary>
public class ErrorCulpritTests
{
    [Theory]
    [InlineData("-'a'", 1, 3)]
    [InlineData("!1.0", 1, 3)]
    [InlineData("col.item.age_hours ?? 'x'", 22, 3)] // the fallback disagrees with the value
    [InlineData("true && 1.0", 8, 3)]
    [InlineData("1.0 || true", 0, 3)]
    [InlineData("col.score.final * 'a'", 18, 3)]
    [InlineData("'a' - col.score.final", 0, 3)]
    [InlineData("'a' * 'b'", 4, 1)] // both wrong: the operator
    [InlineData("5 % 2.0", 4, 3)]
    [InlineData("5.0 % 2", 0, 3)]
    [InlineData("1.0 < 'a'", 6, 3)]
    [InlineData("'a' >= 1.0", 0, 3)]
    [InlineData("1.0 == 'a'", 4, 2)] // each side is comparable, not with the other: the operator
    [InlineData("col.item.embedding == 1.0", 0, 18)] // a vector is not comparable at all
    [InlineData("true ? 1.0 : 'a'", 13, 3)] // the else branch disagrees with the then branch
    [InlineData("'a' in ctx.request.recent_item_ids", 0, 3)] // a list, of other elements: the value
    [InlineData("1 in col.score.final", 5, 15)] // not a list: the list
    [InlineData("overlap_count(ctx.request.recent_item_ids, col.score.final)", 43, 15)]
    [InlineData("overlap_count(1.0, ctx.request.recent_item_ids)", 14, 3)]
    [InlineData("ctx.request.surface.startsWith(1)", 31, 1)]
    [InlineData("[1, 'a']", 4, 3)] // the first element unlike the others
    public void TypeErrors_AreAtThePartAtFault(string source, int offset, int length)
    {
        var d = Assert.Single(CompilerTests.HomeFeed().Errors(source));

        Assert.Equal(ExpressionErrorCodes.TypeMismatch, d.Code);
        Assert.Equal((offset, length), (d.Offset, d.Length));
    }

    [Fact]
    public void AnUnusableName_FailsTheExpressionWithoutAnErrorOfItsOwn()
    {
        // A column declared with an unknown type is reported at its declaration; its readers must not repeat it.
        var scope = new UnusableScope(CompilerTests.HomeFeed().Scope, "col.score.quality");

        Assert.False(ExpressionCompiler.TryCompile("col.score.final * col.score.quality", scope, CompileOptions.Any, out _, out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void AnUnusableName_DoesNotHideAnotherError()
    {
        var scope = new UnusableScope(CompilerTests.HomeFeed().Scope, "col.score.quality");

        Assert.False(ExpressionCompiler.TryCompile("col.score.quality + col.score.finl", scope, CompileOptions.Any, out _, out var errors));
        var d = Assert.Single(errors);
        Assert.Equal(ExpressionErrorCodes.UnknownName, d.Code);
        Assert.Equal(20, d.Offset);
    }

    private sealed class UnusableScope(IExpressionScope inner, string unusable) : IExpressionScope
    {
        public IEnumerable<string> KnownNames => inner.KnownNames;

        public bool TryResolve(string name, out Symbol symbol) => inner.TryResolve(name, out symbol);

        public bool IsUnusable(string name) => name == unusable;

        public bool TryGetLookupTable(string name, out ILookupTable table) => inner.TryGetLookupTable(name, out table);
    }
}
