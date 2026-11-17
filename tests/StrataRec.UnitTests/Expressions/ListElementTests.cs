using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// Lists of every element type in ColumnExpr (design 4.3, 7.6): <c>list&lt;float32&gt;</c> and <c>list&lt;float64&gt;</c>
/// read as <c>list&lt;float64&gt;</c> (float32 storage in place), <c>list&lt;bool&gt;</c>, and the list functions on each.
/// </summary>
/// <remarks><see cref="ExprAssert.Rows"/> checks each expected value three ways: full-range (zero-copy column reads),
/// gather (one row at a time) and the reference interpreter.</remarks>
public class ListElementTests
{
    /// <summary>
    /// <code>
    /// row | w32 (list&lt;float32&gt;?) | w64 (list&lt;float64&gt;) | b (list&lt;bool&gt;?) | x (float64) | i (int64)
    /// 0   | [0.5, 1.5]           | [1.5, 2]           | [true, false]  | 1.5         | 2
    /// 1   | []                   | [0.25]             | []             | 0.25        | 1
    /// 2   | missing              | [-1, -1, 3]        | missing        | 3           | 3
    /// 3   | [2, 2, 2]            | []                 | [false]        | 2           | 2
    /// </code>
    /// </summary>
    private static ExprFixture Lists()
    {
        var f = new ExprFixture(capacity: 8)
            .Col("col.w32", "list<float32>?")
            .Col("col.w64", "list<float64>")
            .Col("col.b", "list<bool>?")
            .Col("col.x", "float64")
            .Col("col.i", "int64")
            .Ctx("ctx.weights", "list<float64>", ScalarValue.FromFloat64List(new[] { 0.25, 2, 9 }))
            .Ctx("ctx.flags", "list<bool>", ScalarValue.FromBoolList(new[] { true }))
            .Ctx("ctx.none", "list<float32>?")
            .Build(4);

        var w32 = f.Buffer<ListColumn<float>>("col.w32");
        var w64 = f.Buffer<ListColumn<double>>("col.w64");
        var b = f.Buffer<ListColumn<bool>>("col.b");
        w32.Set(0, new[] { 0.5f, 1.5f });
        w32.Set(1, Array.Empty<float>());
        w32.SetMissing(2);
        w32.Set(3, new[] { 2f, 2f, 2f });
        w64.Set(0, new[] { 1.5, 2 });
        w64.Set(1, new[] { 0.25 });
        w64.Set(2, new double[] { -1, -1, 3 });
        w64.Set(3, Array.Empty<double>());
        b.Set(0, new[] { true, false });
        b.Set(1, Array.Empty<bool>());
        b.SetMissing(2);
        b.Set(3, new[] { false });
        f.Fill<double>("col.x", 1.5, 0.25, 3, 2);
        f.Fill<long>("col.i", 2, 1, 3, 2);
        return f;
    }

    [Fact]
    public void Types_PromoteFloat32Elements_AndKeepBool()
    {
        var f = Lists();
        Assert.Equal(ColumnType.List(StorageKind.Float64).AsNullable(), f.Compile("col.w32").ResultType);
        Assert.Equal(ColumnType.List(StorageKind.Float64), f.Compile("col.w64").ResultType);
        Assert.Equal(ColumnType.List(StorageKind.Bool).AsNullable(), f.Compile("col.b").ResultType);
        // A literal's length is known: it is a fixed-length list, which stands wherever a list of any length does.
        Assert.Equal(ColumnType.List(StorageKind.Float64, fixedLength: 2), f.Compile("[0.5, 1]").ResultType);
        Assert.Equal(ColumnType.List(StorageKind.Bool, fixedLength: 2), f.Compile("[true, false]").ResultType);
        Assert.Equal(ColumnType.List(StorageKind.Float64), f.Compile("col.i > 2 ? [0.5, 1] : [1.5]").ResultType); // lengths differ
    }

    [Fact]
    public void Size_CountsElements_OfEveryElementType()
    {
        var f = Lists();
        ExprAssert.Rows(f, "size(col.w32)", 2, 0, null, 3);
        ExprAssert.Rows(f, "size(col.w64)", 2, 1, 3, 0);
        ExprAssert.Rows(f, "size(col.b)", 2, 0, null, 1);
        ExprAssert.Rows(f, "size(col.w32 ?? col.w64)", 2, 0, 3, 3); // lazy select of mixed widths
        ExprAssert.Scalar(f, "size(ctx.weights)", 3);
        ExprAssert.Scalar(f, "size([0.5, 1.5]) + size([true])", 3);
    }

    [Fact]
    public void In_And_Contains_CompareFloatsAsFloat64_AndIntsAsFloats()
    {
        var f = Lists();
        ExprAssert.Rows(f, "col.x in col.w32", true, false, null, true);
        ExprAssert.Rows(f, "contains(col.w64, col.x)", true, true, true, false);
        ExprAssert.Rows(f, "col.i in col.w64", true, false, true, false); // an int64 compares as a float64, as in ==
        ExprAssert.Rows(f, "col.x in (col.w32 ?? col.w64)", true, false, true, true);
        ExprAssert.Rows(f, "col.x in ctx.weights", false, true, false, true);
        ExprAssert.Rows(f, "col.x in [1.5, 2]", true, false, false, true);
        ExprAssert.Rows(f, "true in col.b", true, false, null, false);
        ExprAssert.Rows(f, "false in (col.b ?? ctx.flags)", true, false, false, true);
        ExprAssert.Scalar(f, "true in ctx.flags", true);
    }

    [Fact]
    public void OverlapCount_ForFloatAndBoolLists()
    {
        var f = Lists();
        ExprAssert.Rows(f, "overlap_count(col.w32, col.w64)", 1, 0, null, 0);
        ExprAssert.Rows(f, "overlap_count(col.w64, ctx.weights)", 1, 1, 0, 0);
        ExprAssert.Rows(f, "overlap_count(col.w32, col.w32)", 2, 0, null, 3); // with multiplicity
        ExprAssert.Rows(f, "overlap_count(col.b, ctx.flags)", 1, 0, null, 0);
    }

    [Fact]
    public void ListResults_ReadAsPromotedLists()
    {
        var f = Lists();
        ExprAssert.Rows(f, "col.w32 ?? col.w64", new[] { 0.5, 1.5 }, Array.Empty<double>(), new double[] { -1, -1, 3 }, new double[] { 2, 2, 2 });
        ExprAssert.Rows(f, "col.w32 ?? [9.5]", new[] { 0.5, 1.5 }, Array.Empty<double>(), new[] { 9.5 }, new double[] { 2, 2, 2 });
        ExprAssert.Rows(f, "col.b ?? [true]", new[] { true, false }, Array.Empty<bool>(), new[] { true }, new[] { false });
        ExprAssert.Rows(f, "col.i > 1 ? ctx.weights : col.w64", new[] { 0.25, 2, 9 }, new[] { 0.25 }, new[] { 0.25, 2, 9 }, new[] { 0.25, 2, 9 });
    }

    [Theory]
    [InlineData("list<float32>")]
    [InlineData("list<float64>")]
    public void FloatListResults_AreWrittenIntoEitherWidth(string type)
    {
        var f = Lists();
        var expression = f.Compile("col.w32 ?? col.w64", new CompileOptions { ExpectedType = ColumnType.Parse(type) });
        var destination = ColumnBuffer.Create(ColumnType.Parse(type), f.Capacity, f.Memory);
        expression.Evaluate(f.Context, f.Table.Rows.Order, destination);

        Assert.Equal(new[] { 0.5, 1.5 }, ColumnValues.Get(destination, 0).AsFloat64List().ToArray());
        Assert.True(ColumnValues.Get(destination, 1).AsFloat64List().IsEmpty);
        Assert.Equal(new double[] { -1, -1, 3 }, ColumnValues.Get(destination, 2).AsFloat64List().ToArray());

        var broadcast = ColumnBuffer.Create(ColumnType.Parse(type), f.Capacity, f.Memory);
        f.Compile("ctx.weights", new CompileOptions { ExpectedType = ColumnType.Parse(type) }).Evaluate(f.Context, f.Table.Rows.Order, broadcast);
        Assert.Equal(new[] { 0.25, 2, 9 }, ColumnValues.Get(broadcast, 3).AsFloat64List().ToArray());
    }

    [Fact]
    public void Float32ListValues_PrintAsTheFloatsTheyAre()
    {
        var f = Lists();
        var memory = f.Memory;
        var column = ColumnBuffer.Create(ColumnType.List(StorageKind.Float32), 1, memory);
        ColumnValues.Set(column, 0, ScalarValue.FromFloat64List(new[] { 0.1, 2.5 }));

        var value = ColumnValues.Get(column, 0);
        Assert.True(value.IsFloat32List);
        Assert.Equal("[0.1, 2.5]", value.ToString());
        Assert.Equal(ScalarValue.FromFloat64List(new[] { (double)0.1f, 2.5 }), value); // equality is the doubles'
    }

    [Theory]
    [InlineData("'a' in col.w64", "'in' needs a value and a list of the same element type")]
    [InlineData("1.5 in col.b", "'in' needs a value and a list of the same element type")]
    [InlineData("overlap_count(col.w64, col.b)", "'overlap_count' needs two lists of the same element type")]
    [InlineData("[1.0, true]", "List literal elements must all be numbers, all strings or all bools")]
    public void Mismatches_AreTypeErrors(string source, string message)
    {
        var error = Assert.Single(Lists().Errors(source));
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegerListLiterals_AreFloatLists_WhereAFloatListIsExpected()
    {
        var f = Lists();

        // As a float list site's value, in '??' and '?:' with a float list, and in overlap_count with one.
        var expression = f.Compile("[1, 2]", new CompileOptions { ExpectedType = ColumnType.List(StorageKind.Float32) });
        Assert.Equal(ColumnType.List(StorageKind.Float64, fixedLength: 2), expression.ResultType);
        ExprAssert.Rows(f, "col.w32 ?? [0]", new[] { 0.5, 1.5 }, Array.Empty<double>(), new[] { 0.0 }, new double[] { 2, 2, 2 });
        ExprAssert.Rows(f, "col.i > 2 ? [7, 8] : col.w64", new[] { 1.5, 2 }, new[] { 0.25 }, new double[] { 7, 8 }, Array.Empty<double>());
        ExprAssert.Rows(f, "overlap_count(col.w64, [2, 3])", 1, 0, 1, 0);
        ExprAssert.Rows(f, "size([3] ?? col.w64)", 1, 1, 1, 1);

        // So is a choice between integer list literals, let-bound or not.
        var choice = f.Compile("col.i > 2 ? [1, 2] : [3]", new CompileOptions { ExpectedType = ColumnType.List(StorageKind.Float32) });
        Assert.Equal(ColumnType.List(StorageKind.Float64), choice.ResultType);
        ExprAssert.Rows(f, "let d = [0]; col.w32 ?? (col.i > 2 ? [1, 2] : d)", new[] { 0.5, 1.5 }, Array.Empty<double>(), new double[] { 1, 2 }, new double[] { 2, 2, 2 });
        ExprAssert.Rows(f, "let xs = [2]; overlap_count(col.w64, xs) + overlap_count([2, 7], xs)", 2, 1, 1, 1); // xs stays int64 where no float list is
    }

    [Fact]
    public void LiteralListConversion_VisitsEachNodeOfASharedLetOnce()
    {
        // Review 62: each let doubles the paths to the literal, not the nodes; walking paths took 190 MB at 18 levels.
        var f = new ExprFixture(capacity: 4).Ctx("ctx.b", "bool", ScalarValue.FromBool(true)).Build(1);
        var source = new System.Text.StringBuilder("let a0 = [1]; ");
        for (var i = 1; i <= 20; i++)
        {
            source.Append($"let a{i} = ctx.b ? a{i - 1} : a{i - 1}; ");
        }

        source.Append("a20");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var expression = f.Compile(source.ToString(), new CompileOptions { ExpectedType = ColumnType.List(StorageKind.Float32) });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(ColumnType.List(StorageKind.Float64, fixedLength: 1), expression.ResultType);
        Assert.True(allocated < 2_000_000, $"compiling allocated {allocated:N0} bytes");
        Assert.Equal(ScalarValue.FromFloat64List(new[] { 1.0 }), expression.EvaluateScalar(f.Context));
    }

    [Theory]
    [InlineData("[1, 1e300]", "list<float32>")]
    [InlineData("1e39", "float32")]
    public void AConstantBeyondFloat32_ForAFloat32Site_IsAnError(string source, string type)
    {
        var error = Assert.Single(Lists().Errors(source, new CompileOptions { ExpectedType = ColumnType.Parse(type) }));
        Assert.Equal(ExpressionErrorCodes.InvalidArgument, error.Code);
        Assert.Contains("[-3.4028235E+38, 3.4028235E+38]", error.Message, StringComparison.Ordinal);
        Assert.Contains("declare the column as " + ColumnType.Parse(type).Promoted(), error.Hint, StringComparison.Ordinal);
        Lists().Compile(source.Replace("e300", "e30", StringComparison.Ordinal).Replace("e39", "e38", StringComparison.Ordinal),
            new CompileOptions { ExpectedType = ColumnType.Parse(type) }); // within the range: compiles
    }

    [Fact]
    public void TypedIntLists_AreNotConvertedToFloatLists()
    {
        var f = new ExprFixture(capacity: 4).Col("col.ids", "list<int64>").Col("col.w", "list<float64>").Build(1);

        var site = Assert.Single(f.Errors("col.ids", new CompileOptions { ExpectedType = ColumnType.List(StorageKind.Float32) }));
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, site.Code);
        Assert.Contains("an int64 list is not converted to a float list", site.Hint, StringComparison.Ordinal);
        Assert.Contains("'??' needs both sides of the same type", Assert.Single(f.Errors("col.w ?? col.ids")).Message, StringComparison.Ordinal);
        Assert.Contains("'overlap_count' needs two lists of the same element type", Assert.Single(f.Errors("overlap_count(col.w, col.ids)")).Message, StringComparison.Ordinal);
    }
}
