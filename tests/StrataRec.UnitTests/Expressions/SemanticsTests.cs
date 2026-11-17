using System.Globalization;
using System.Numerics.Tensors;
using StrataRec.Core.Columns;
using StrataRec.Core.Hashing;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;

namespace StrataRec.UnitTests.Expressions;

/// <summary>Language semantics (design 7.4–7.6), checked on the vectorized paths and the reference interpreter.</summary>
public class SemanticsTests
{
    private static readonly bool?[] As = { true, true, true, false, false, false, null, null, null };
    private static readonly bool?[] Bs = { true, false, null, true, false, null, true, false, null };

    private static ExprFixture KleeneTable()
    {
        var f = new ExprFixture().Col("col.a", "bool?").Col("col.b", "bool?").Ctx("ctx.a", "bool?").Ctx("ctx.b", "bool?").Build(9);
        f.Fill("col.a", As);
        f.Fill("col.b", Bs);
        return f;
    }

    [Fact]
    public void KleeneLogic_And_Or_Not()
    {
        var f = KleeneTable();
        ExprAssert.Rows(f, "col.a && col.b", true, false, null, false, false, false, null, false, null);
        ExprAssert.Rows(f, "col.a || col.b", true, true, true, true, false, null, true, null, null);
        ExprAssert.Rows(f, "!col.a", false, false, false, true, true, true, null, null, null);
        ExprAssert.Rows(f, "col.a && true", true, true, true, false, false, false, null, null, null);
        ExprAssert.Rows(f, "has(col.a) && col.a", true, true, true, false, false, false, false, false, false);
        ExprAssert.Rows(f, "col.a == col.b", true, false, null, false, true, null, null, null, null);
    }

    [Fact]
    public void KleeneLogic_RequestLevel()
    {
        var f = KleeneTable();
        for (var i = 0; i < As.Length; i++)
        {
            f.SetCtx("ctx.a", As[i] is { } a ? ScalarValue.FromBool(a) : ScalarValue.Missing);
            f.SetCtx("ctx.b", Bs[i] is { } b ? ScalarValue.FromBool(b) : ScalarValue.Missing);
            bool? and = As[i] == false || Bs[i] == false ? false : As[i] is null || Bs[i] is null ? null : true;
            bool? or = As[i] == true || Bs[i] == true ? true : As[i] is null || Bs[i] is null ? null : false;
            ExprAssert.Scalar(f, "ctx.a && ctx.b", and);
            ExprAssert.Scalar(f, "ctx.a || ctx.b", or);

            // Mixed request-level and row-level operands.
            ExprAssert.Rows(f, "ctx.a && col.b", Enumerable.Range(0, 9).Select(r => Kleene(As[i], Bs[r], and: true)).Cast<object?>().ToArray());
            ExprAssert.Rows(f, "col.a || ctx.b", Enumerable.Range(0, 9).Select(r => Kleene(As[r], Bs[i], and: false)).Cast<object?>().ToArray());
        }
    }

    private static bool? Kleene(bool? a, bool? b, bool and) => and
        ? a == false || b == false ? false : a is null || b is null ? null : true
        : a == true || b == true ? true : a is null || b is null ? null : false;

    [Fact]
    public void Conditional_Coalesce_Has()
    {
        var f = new ExprFixture().Col("col.c", "bool?").Col("col.x", "float64?").Col("col.y", "float64").Build(4);
        f.Fill<bool>("col.c", true, false, null, true);
        f.Fill<double>("col.x", 1.5, null, 3.0, null);
        f.Fill<double>("col.y", 10, 20, 30, 40);

        ExprAssert.Rows(f, "col.c ? col.y : -col.y", 10.0, -20.0, null, 40.0);
        ExprAssert.Rows(f, "col.c ? col.x : 0.0", 1.5, 0.0, null, null);
        ExprAssert.Rows(f, "col.x ?? col.y", 1.5, 20.0, 3.0, 40.0);
        ExprAssert.Rows(f, "col.x ?? -1", 1.5, -1.0, 3.0, -1.0);
        ExprAssert.Rows(f, "has(col.x)", true, false, true, false);
        ExprAssert.Rows(f, "has(col.x / 0.0)", true, false, true, false);
        ExprAssert.Rows(f, "has(col.y)", true, true, true, true);
        ExprAssert.Rows(f, "col.x + col.y", 11.5, null, 33.0, null);
        ExprAssert.Rows(f, "(col.c ?? false) ? 'yes' : 'no'", "yes", "no", "no", "yes");
        ExprAssert.Rows(f, "col.c ? 1 : 2", 1L, 2L, null, 1L);
        ExprAssert.Rows(f, "col.c ? true : col.c", true, false, null, true);
        ExprAssert.Rows(f, "col.c ?? col.y > 15.0", true, false, true, true);
    }

    [Fact]
    public void TotalSemantics_Division_Logs_Sqrt()
    {
        var f = new ExprFixture().Col("col.x", "float64").Col("col.i", "int64").Build(4);
        f.Fill<double>("col.x", 1.0, -1.0, 0.0, double.NaN);
        f.Fill<long>("col.i", 1, -1, 0, 5);

        ExprAssert.Rows(f, "col.x / 0.0", double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.NaN);
        ExprAssert.Rows(f, "col.i / 0", double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.PositiveInfinity);
        ExprAssert.Rows(f, "log(col.x)", 0.0, double.NaN, double.NegativeInfinity, double.NaN);
        ExprAssert.Rows(f, "sqrt(col.x)", 1.0, double.NaN, 0.0, double.NaN);
        ExprAssert.Rows(f, "col.i / 2", 0.5, -0.5, 0.0, 2.5);

        ExprAssert.Scalar(f, "1.0 / 0.0", double.PositiveInfinity);
        ExprAssert.Scalar(f, "-1 / 0", double.NegativeInfinity);
        ExprAssert.Scalar(f, "0.0 / 0.0", double.NaN);
        ExprAssert.Scalar(f, "log(0.0)", double.NegativeInfinity);
        ExprAssert.Scalar(f, "log(-1.0)", double.NaN);
        ExprAssert.Scalar(f, "sqrt(-1.0)", double.NaN);
        ExprAssert.Scalar(f, "exp(710.0)", double.PositiveInfinity);
        ExprAssert.Scalar(f, "pow(-8.0, 1.0 / 3.0)", double.NaN);
    }

    [Fact]
    public void TotalSemantics_IntegerWraparound()
    {
        var f = new ExprFixture().Col("col.i", "int64").Col("col.j", "int32?").Build(4);
        f.Fill<long>("col.i", long.MaxValue, long.MinValue, 4611686018427387904L, -5);
        f.Fill<int>("col.j", 1, 1, 2, null);

        ExprAssert.Rows(f, "col.i + 1", long.MinValue, long.MinValue + 1, 4611686018427387905L, -4L);
        ExprAssert.Rows(f, "col.i - 1", long.MaxValue - 1, long.MaxValue, 4611686018427387903L, -6L);
        ExprAssert.Rows(f, "col.i * 2", -2L, 0L, long.MinValue, -10L);
        ExprAssert.Rows(f, "-col.i", long.MinValue + 1, long.MinValue, -4611686018427387904L, 5L);
        ExprAssert.Rows(f, "abs(col.i)", long.MaxValue, long.MinValue, 4611686018427387904L, 5L);
        ExprAssert.Rows(f, "col.i + col.j", long.MinValue, long.MinValue + 1, 4611686018427387906L, null);
        ExprAssert.Rows(f, "min(col.i, 0)", 0L, long.MinValue, 0L, -5L);

        ExprAssert.Scalar(f, "9223372036854775807 + 1", long.MinValue);
        ExprAssert.Scalar(f, "-9223372036854775808 - 1", long.MaxValue);
        ExprAssert.Scalar(f, "abs(-9223372036854775808)", long.MinValue);
    }

    [Fact]
    public void IntConversion_TruncatesTowardZero_AndIsMissingWithoutAnInt64Value()
    {
        // 2^63 and -2^63 are exact doubles: the first is out of range, the second is int64's minimum. The double
        // below 2^63 is 2^63 - 1024.
        var f = new ExprFixture().Col("col.x", "float64").Col("col.y", "float64?").Col("col.i", "int64?").Build(10);
        f.Fill<double>("col.x", 2.9, -2.9, -0.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            9223372036854775808.0, -9223372036854775808.0, 9223372036854774784.0, 1e300);
        f.Fill<double>("col.y", 7.5, null, 1e19, -1e19, 3.0, null, 0.0, -0.0, 41.99, 2.0);
        f.Fill<long>("col.i", 5, null, long.MaxValue, long.MinValue, 0, 1, 2, 3, 4, 9007199254740993);

        ExprAssert.Rows(f, "int(col.x)", 2L, -2L, 0L, null, null, null, null, long.MinValue, 9223372036854774784L, null);
        ExprAssert.Rows(f, "int(col.y)", 7L, null, null, null, 3L, null, 0L, 0L, 41L, 2L);
        ExprAssert.Rows(f, "int(col.i)", 5L, null, long.MaxValue, long.MinValue, 0L, 1L, 2L, 3L, 4L, 9007199254740993L);
        ExprAssert.Rows(f, "int(col.x) ?? -1", 2L, -2L, 0L, -1L, -1L, -1L, -1L, long.MinValue, 9223372036854774784L, -1L);
        ExprAssert.Rows(f, "has(int(col.x * 0.5))", true, true, true, false, false, false, true, true, true, false);
        ExprAssert.Rows(f, "int(col.x) % 2", 0L, 0L, 0L, null, null, null, null, 0L, 0L, null);

        // double() of an int64 rounds to the nearest double, like every int64 operand of float64 arithmetic.
        ExprAssert.Rows(f, "double(col.i)", 5.0, null, 9223372036854775807.0, -9223372036854775808.0, 0.0, 1.0, 2.0, 3.0, 4.0, 9007199254740992.0);
        ExprAssert.Rows(f, "double(col.x)", 2.9, -2.9, -0.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            9223372036854775808.0, -9223372036854775808.0, 9223372036854774784.0, 1e300);
        ExprAssert.Rows(f, "int(double(col.i))", 5L, null, null, long.MinValue, 0L, 1L, 2L, 3L, 4L, 9007199254740992L);

        ExprAssert.Scalar(f, "int(-7.9)", -7L);
        ExprAssert.Scalar(f, "int(1e19)", null);
        ExprAssert.Scalar(f, "int(1.0 / 0.0)", null);
        ExprAssert.Scalar(f, "int(42)", 42L);
        ExprAssert.Scalar(f, "double(3) / 2.0", 1.5);
    }

    [Fact]
    public void IntConversion_Types()
    {
        var f = new ExprFixture().Col("col.x", "float64").Col("col.i", "int64").Col("col.b", "bool").Build(3);

        // int() of a float64 may be missing even when its argument is not; int() of an int64 is that int64.
        Assert.Equal(ColumnType.Parse("int64?"), f.Compile("int(col.x)").ResultType);
        Assert.Equal(ColumnType.Parse("int64"), f.Compile("int(col.i)").ResultType);
        Assert.Equal(ColumnType.Parse("float64"), f.Compile("double(col.i)").ResultType);
        Assert.Equal(ColumnType.Parse("int64"), f.Compile("int(col.x) ?? 0").ResultType);

        var bad = Assert.Single(f.Errors("int(col.b)"));
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, bad.Code);
        Assert.Equal(4, bad.Offset);
        Assert.Equal(ExpressionErrorCodes.ArgCount, Assert.Single(f.Errors("double(col.x, 1)")).Code);
    }

    /// <summary>
    /// <c>x % 0</c> has no value, so it is missing (design 7.5), never a silent 0: the result is nullable unless the
    /// divisor is a non-zero constant, and a constant 0 divisor is a compile error. It used to be 0, counted in a metric
    /// that was never exported.
    /// </summary>
    [Fact]
    public void Modulo_ByZero_IsMissing_AndAConstantZeroDivisor_IsAnError()
    {
        var f = new ExprFixture().Col("col.i", "int64").Col("col.d", "int64").Build(6);
        f.Fill<long>("col.i", 7, 7, long.MinValue, -7, 9, 9);
        f.Fill<long>("col.d", 0, 3, -1, 3, 0, 4);

        ExprAssert.Rows(f, "col.i % col.d", null, 1L, 0L, -1L, null, 1L); // vectorized (both paths) and the reference
        ExprAssert.Rows(f, "(col.i % col.d) ?? -1", -1L, 1L, 0L, -1L, -1L, 1L);
        Assert.True(f.Compile("col.i % col.d", new CompileOptions { AllowNullableResult = true }).ResultType.Nullable);
        Assert.False(f.Compile("col.i % 4").ResultType.Nullable); // a non-zero constant divisor is never 0

        var error = Assert.Single(f.Errors("col.i % 0"));
        Assert.Equal((ExpressionErrorCodes.InvalidArgument, 8, 1), (error.Code, error.Offset, error.Length));
        Assert.Equal("use a non-zero divisor", error.Hint);

        ExprAssert.Scalar(f, "7 % (1 - 1)", null); // 0 only after folding: missing, not 0
        ExprAssert.Scalar(f, "-9223372036854775808 % -1", 0L);
        ExprAssert.Scalar(f, "-7 % 3", -1L);
        ExprAssert.Scalar(f, "7 % -3", 1L);
    }

    [Fact]
    public void Strings_Equality_In_StartsWith()
    {
        var f = new ExprFixture()
            .Col("col.s", "string?")
            .Col("col.s2", "string")
            .Col("col.flag", "bool")
            .Ctx("ctx.src", "string", ScalarValue.FromString("cf"))
            .Ctx("ctx.allowed", "list<string>", ScalarValue.FromStringList(new[] { "two_tower", "nope" }))
            .Ctx("ctx.missing", "string?")
            .Build(4);
        f.FillStrings("col.s", "two_tower", "cf", null, "two_tower_v2");
        f.FillStrings("col.s2", "two_tower", "x", "y", "two_tower");
        f.Fill<bool>("col.flag", true, false, true, false);

        ExprAssert.Rows(f, "col.s == 'two_tower'", true, false, null, false);
        ExprAssert.Rows(f, "'two_tower' != col.s", false, true, null, true);
        ExprAssert.Rows(f, "col.s == 'absent'", false, false, null, false);
        ExprAssert.Rows(f, "col.s != 'absent'", true, true, null, true);
        ExprAssert.Rows(f, "col.s == ctx.src", false, true, null, false);
        ExprAssert.Rows(f, "col.s == ctx.missing", null, null, null, null);
        ExprAssert.Rows(f, "col.s in ['cf', 'x']", false, true, null, false);
        ExprAssert.Rows(f, "col.s in ctx.allowed", true, false, null, false);
        ExprAssert.Rows(f, "col.s.startsWith('two')", true, false, null, true);
        ExprAssert.Rows(f, "startsWith(col.s, ctx.src)", false, true, null, false);
        ExprAssert.Rows(f, "col.s.endsWith('_v2')", false, false, null, true); // a dictionary column, a constant suffix: once per distinct value
        ExprAssert.Rows(f, "endsWith(col.s, col.s2)", true, false, null, false); // per row
        ExprAssert.Rows(f, "col.s.endsWith(ctx.missing)", null, null, null, null);
        ExprAssert.Rows(f, "col.s == col.s2", true, false, null, false);
        ExprAssert.Rows(f, "col.s ?? 'none'", "two_tower", "cf", "none", "two_tower_v2");
        ExprAssert.Rows(f, "col.flag ? col.s2 : 'other'", "two_tower", "other", "y", "other");
        ExprAssert.Rows(f, "(col.flag ? col.s2 : ctx.src).startsWith('t')", true, false, false, false);
        ExprAssert.Rows(f, "col.s2 in ['x', 'y']", false, true, true, false);
        ExprAssert.Rows(f, "col.s2 == 'x'", false, true, false, false);
    }

    [Fact]
    public void Lists_Size_Contains_Overlap()
    {
        var f = new ExprFixture()
            .Col("col.tags", "list<int64>?")
            .Col("col.cats", "list<string>")
            .Ctx("ctx.liked", "list<int64>", ScalarValue.FromInt64List(new long[] { 4, 3 }))
            .Ctx("ctx.recent", "list<int64>", ScalarValue.FromInt64List(new long[] { 1003, 1001 }))
            .Ctx("ctx.liked_cats", "list<string>", ScalarValue.FromStringList(new[] { "b", "z" }))
            .Build(4);
        var tags = f.Buffer<ListColumn<long>>("col.tags");
        tags.Set(0, new long[] { 1, 2, 3 });
        tags.Set(1, Array.Empty<long>());
        tags.SetMissing(2);
        tags.Set(3, new long[] { 3, 3 });
        var cats = f.Buffer<ListColumn<string>>("col.cats");
        cats.Set(0, new[] { "a", "b" });
        cats.Set(1, new[] { "b" });
        cats.Set(2, Array.Empty<string>());
        cats.Set(3, new[] { "c", "b", "b" });

        ExprAssert.Rows(f, "size(col.tags)", 3L, 0L, null, 2L);
        ExprAssert.Rows(f, "contains(col.tags, 3)", true, false, null, true);
        ExprAssert.Rows(f, "3 in col.tags", true, false, null, true);
        ExprAssert.Rows(f, "overlap_count(col.tags, ctx.liked)", 1L, 0L, null, 2L);
        ExprAssert.Rows(f, "overlap_count(ctx.liked, col.tags)", 1L, 0L, null, 1L);
        ExprAssert.Rows(f, "overlap_count(col.tags, col.tags)", 3L, 0L, null, 2L);
        ExprAssert.Rows(f, "col.entity_id in ctx.recent", false, true, false, true);
        ExprAssert.Rows(f, "!contains(ctx.recent, col.entity_id)", true, false, true, false);
        ExprAssert.Rows(f, "col.entity_id in [1000, 1002]", true, false, true, false);
        ExprAssert.Rows(f, "col.entity_id in [1000.0, 1002.5]", true, false, false, false);
        ExprAssert.Rows(f, "size(col.cats)", 2L, 1L, 0L, 3L);
        ExprAssert.Rows(f, "'b' in col.cats", true, true, false, true);
        ExprAssert.Rows(f, "overlap_count(col.cats, ctx.liked_cats)", 1L, 1L, 0L, 2L);
        ExprAssert.Rows(f, "overlap_count(col.cats, ['a', 'c'])", 1L, 0L, 0L, 1L);
        ExprAssert.Rows(f, "col.tags ?? ctx.liked", new long[] { 1, 2, 3 }, Array.Empty<long>(), new long[] { 4, 3 }, new long[] { 3, 3 });
        ExprAssert.Rows(f, "size(col.entity_id > 1001 ? col.cats : ctx.liked_cats)", 2L, 2L, 0L, 3L);
        ExprAssert.Scalar(f, "size([1, 2, 3])", 3L);
        ExprAssert.Scalar(f, "4 in ctx.liked", true);
    }

    /// <summary>
    /// A string column holds one reference per row (no dictionary codes): every string operation reads it the same way in
    /// both backends, short values and long ones, shared instances and equal copies alike. ==, != and 'in' compare by
    /// value (an equal string that is another instance matches); 'in' scans a small constant set and hashes a large one.
    /// </summary>
    [Fact]
    public void Strings_EveryOperation_OnThePlainColumn()
    {
        var copy = new string("abc".AsSpan()); // equal to the literal, another instance
        var large = string.Join(", ", Enumerable.Range(0, 20).Select(i => $"'v{i}'")) + ", 'abc'";
        var f = new ExprFixture().Col("col.t", "string?").Col("col.s", "string").Ctx("ctx.q", "string", ScalarValue.FromString("lo")).Build(4);
        f.FillStrings("col.t", "héllo 😀", string.Empty, null, copy);
        f.FillStrings("col.s", "héllo", "x", "y", "abc");
        ExprAssert.Rows(f, "size(col.t)", 7L, 0L, null, 3L);
        ExprAssert.Rows(f, "contains(col.t, 'll')", true, false, null, false);
        ExprAssert.Rows(f, "col.t.contains(ctx.q)", true, false, null, false);
        ExprAssert.Rows(f, "col.t ?? ctx.q", "héllo 😀", string.Empty, "lo", "abc");
        ExprAssert.Rows(f, "col.t == ''", false, true, null, false);
        ExprAssert.Rows(f, "col.t == 'abc'", false, false, null, true);
        ExprAssert.Rows(f, "'abc' != col.t", true, true, null, false);
        ExprAssert.Rows(f, "col.t == col.s", false, false, null, true);
        ExprAssert.Rows(f, "col.s != 'x'", true, false, true, true);
        ExprAssert.Rows(f, "col.t in ['abc', 'x']", false, false, null, true);
        ExprAssert.Rows(f, $"col.t in [{large}]", false, false, null, true);
        ExprAssert.Rows(f, $"col.s in [{large}, 'y']", false, false, true, true);
        ExprAssert.Rows(f, "col.s in ['nope']", false, false, false, false);
        ExprAssert.Rows(f, "col.t.startsWith(col.s)", true, false, null, true);
        ExprAssert.Rows(f, "col.s.startsWith('hé')", true, false, false, false);
        ExprAssert.Rows(f, "col.t.endsWith('😀')", true, false, null, false);
        ExprAssert.Rows(f, "size(col.s)", 5L, 1L, 1L, 3L);
        ExprAssert.Rows(f, "col.s.contains('ll')", true, false, false, false);
        Assert.Equal("string?", f.Compile("col.t", new CompileOptions { AllowNullableResult = true }).ResultType.ToString());

        var error = Assert.Single(f.Errors("'b' in col.s"));
        Assert.Equal("'in' tests list membership; use contains(s, part) for a substring", error.Hint);
    }

    /// <summary>An 'in' over constants allocates nothing per row, whether its set is scanned (small) or hashed (large).</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(40)]
    public void InConstants_OnAStringColumn_DoesNotAllocatePerRow(int constants)
    {
        var set = string.Join(", ", Enumerable.Range(0, constants).Select(i => $"'c{i}'"));
        long Allocated(int rows)
        {
            var f = new ExprFixture(rows).Col("col.s", "string").Build(rows);
            var values = Enumerable.Range(0, rows).Select(i => "c" + (i % (2 * constants))).ToArray();
            f.FillStrings("col.s", values);
            var compiled = f.Compile($"col.s in [{set}]");
            var destination = ColumnBuffer.Create(ColumnType.Bool, f.Capacity, f.Memory);
            compiled.Evaluate(f.Context, f.Table.Rows.Order, destination);
            var best = long.MaxValue;
            for (var round = 0; round < 3; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                compiled.Evaluate(f.Context, f.Table.Rows.Order, destination);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            var bools = (ScalarColumn<bool>)destination;
            Assert.True(bools[0] && !bools[constants]); // c0 is in the set, c{constants} is not
            return best;
        }

        Assert.Equal(0, Allocated(50_000) - Allocated(500));
    }

    [Fact]
    public void Vectors_Dot_Cosine_Norm()
    {
        var f = new ExprFixture()
            .Col("col.v", "list<float32,3>?")
            .Ctx("ctx.u", "list<float32,3>", ScalarValue.FromFloat32List(new float[] { 1, 0, 0 }))
            .Ctx("ctx.none", "list<float32,3>?")
            .Build(4);
        var v = f.Buffer<ListColumn<float>>("col.v");
        v.Set(0, new float[] { 1, 2, 3 });
        v.Set(1, new float[] { 0, 0, 0 });
        v.SetMissing(2);
        v.Set(3, new float[] { 3, 4, 0 });

        // Declared float32, computed in float32: the norm is the float32 square root; the cosine is formed in float64.
        ExprAssert.Rows(f, "dot(col.v, ctx.u)", 1.0, 0.0, null, 3.0);
        ExprAssert.Rows(f, "dot(col.v, col.v)", 14.0, 0.0, null, 25.0);
        ExprAssert.Rows(f, "l2_norm(col.v)", (double)MathF.Sqrt(14), 0.0, null, 5.0);
        ExprAssert.Rows(f, "cosine(col.v, ctx.u)", 1.0 / MathF.Sqrt(14), double.NaN, null, 0.6);
        ExprAssert.Rows(f, "dot(col.v, ctx.u) ?? 0.0", 1.0, 0.0, 0.0, 3.0);
        ExprAssert.Rows(f, "dot(col.v, ctx.none) ?? -1.0", -1.0, -1.0, -1.0, -1.0);
        ExprAssert.Rows(f, "has(col.v)", true, true, false, true);
        ExprAssert.Rows(f, "col.v ?? ctx.u", new float[] { 1, 2, 3 }, new float[] { 0, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 3, 4, 0 });
        ExprAssert.Rows(f, "l2_norm(col.entity_id > 1001 ? ctx.u : col.v)", (double)MathF.Sqrt(14), 0.0, 1.0, 1.0);
    }

    [Fact]
    public void VectorFunctions_TakeFloat32AndFloat64Lists_Mixed_AndMissRowsOfAnotherLength()
    {
        var f = new ExprFixture()
            .Col("col.a", "list<float32>?")
            .Col("col.d", "list<float64,3>")
            .Ctx("ctx.u", "list<float64,3>", ScalarValue.FromFloat64List(new double[] { 1, 0, 0 }))
            .Build(4);
        var a = f.Buffer<ListColumn<float>>("col.a");
        a.Set(0, new float[] { 1, 2, 3 });
        a.Set(1, new float[] { 1, 2 }); // of another length than col.d's rows
        a.SetMissing(2);
        a.Set(3, Array.Empty<float>());
        var d = f.Buffer<ListColumn<double>>("col.d");
        d.Set(0, new double[] { 0.5, 0.25, 2 });
        d.Set(1, new double[] { 3, 4, 0 });
        d.Set(2, new double[] { 0, 0, 1 });
        d.Set(3, new double[] { 1, 1, 1 });

        // float32 × float64 is computed in float64: the float32 elements widen, exactly.
        ExprAssert.Rows(f, "dot(col.a, col.d)", 7.0, null, null, null);
        ExprAssert.Rows(f, "cosine(col.a, col.d)", 7.0 / (Math.Sqrt(14) * Math.Sqrt(4.3125)), null, null, null);
        ExprAssert.Rows(f, "dot(col.d, ctx.u)", 0.5, 3.0, 0.0, 1.0);
        ExprAssert.Rows(f, "l2_norm(col.d)", Math.Sqrt(4.3125), 5.0, 1.0, Math.Sqrt(3));
        ExprAssert.Rows(f, "l2_norm(col.a)", (double)MathF.Sqrt(14), (double)MathF.Sqrt(5), null, 0.0); // float32 alone: in float32
        ExprAssert.Rows(f, "dot(col.a, [1.0, 1.0])", null, 3.0, null, null);
        ExprAssert.Rows(f, "dot(col.a, [1, 1])", null, 3.0, null, null); // an int list literal is a float list here

        // Two declared lengths make the result non-nullable; a list of any length on either side makes it nullable.
        Assert.Equal("float64", f.Compile("dot(col.d, ctx.u)").ResultType.ToString());
        Assert.Equal("float64?", f.Compile("dot(col.a, col.d)", new CompileOptions { AllowNullableResult = true }).ResultType.ToString());

        // Two declared lengths that differ can never pair.
        var mismatch = Assert.Single(f.Errors("dot(col.d, [1.0, 2.0])"));
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, mismatch.Code);
        Assert.Contains("list<float64,3> and list<float64,2>", mismatch.Message);
        Assert.NotNull(mismatch.Hint);
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, Assert.Single(f.Errors("cosine(ctx.u, [1.0])")).Code);

        // Not a float list.
        var ints = Assert.Single(f.Errors("l2_norm(['a'])"));
        Assert.Contains("float list", ints.Message);
    }

    /// <summary>
    /// dot, cosine and l2_norm compute in the precision the lists are declared in: float32 when every operand is a
    /// <c>list&lt;float32&gt;</c> (a column, a context value, a choice between them), as TensorPrimitives' float32 kernels
    /// compute it; float64 when any is a <c>list&lt;float64&gt;</c> or a literal. The vectorized backend, the reference
    /// interpreter and the request-level program give the same bits, a float32 list held as float64 included.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(300)] // longer than the scalar backend's stack buffer (256), and many SIMD blocks
    public void VectorFunctions_ComputeInTheDeclaredPrecision_WithTheSameBitsInEveryBackend(int length)
    {
        var random = new Random(length);
        float[] Floats() => Enumerable.Range(0, length).Select(_ => (float)((random.NextDouble() * 2) - 1)).ToArray();
        static double[] Widen(float[] values) => values.Select(v => (double)v).ToArray();
        const int rows = 4;
        float[][] a = [Floats(), Floats(), Floats(), Floats()];
        float[][] b = [Floats(), Floats(), Floats(), Floats()];
        double[][] d = [Widen(Floats()), Widen(Floats()), Widen(Floats()), Widen(Floats())];
        var u = Floats();
        var w = Widen(Floats());
        var f = new ExprFixture()
            .Col("col.a", $"list<float32,{length}>")
            .Col("col.b", $"list<float32,{length}>")
            .Col("col.d", $"list<float64,{length}>")
            .Ctx("ctx.u", $"list<float32,{length}>", ScalarValue.FromFloat32List(u))
            .Ctx("ctx.w", $"list<float64,{length}>", ScalarValue.FromFloat64List(w))
            .Build(rows);
        for (var r = 0; r < rows; r++)
        {
            f.Buffer<ListColumn<float>>("col.a").Set(r, a[r]);
            f.Buffer<ListColumn<float>>("col.b").Set(r, b[r]);
            f.Buffer<ListColumn<double>>("col.d").Set(r, d[r]);
        }

        static double Cosine32(float[] x, float[] y) =>
            (double)TensorPrimitives.Dot(x, y) / ((double)TensorPrimitives.Norm(x) * (double)TensorPrimitives.Norm(y));
        static double Cosine64(double[] x, double[] y) => TensorPrimitives.Dot(x, y) / (TensorPrimitives.Norm(x) * TensorPrimitives.Norm(y));

        void Check(string source, Func<int, double> expected)
        {
            var compiled = f.Compile(source);
            var vectorized = f.Rows(source);
            for (var r = 0; r < rows; r++)
            {
                var bits = BitConverter.DoubleToInt64Bits(expected(r));
                Assert.True(bits == BitConverter.DoubleToInt64Bits(vectorized[r].AsFloat64()), $"{source} @row {r}: expected {expected(r):R}, got {vectorized[r]}");
                Assert.True(bits == BitConverter.DoubleToInt64Bits(f.Reference(compiled, r).AsFloat64()), $"{source} @row {r} (reference)");
            }
        }

        // float32: columns, a context value (held as float64), a choice between them.
        Check("dot(col.a, col.b)", r => TensorPrimitives.Dot(a[r], b[r]));
        Check("dot(ctx.u, col.a)", r => TensorPrimitives.Dot(u, a[r]));
        Check("cosine(ctx.u, col.a)", r => Cosine32(u, a[r]));
        Check("l2_norm(col.a)", r => TensorPrimitives.Norm(a[r]));
        Check("dot(col.entity_id % 2 == 0 ? ctx.u : col.b, col.a)", r => TensorPrimitives.Dot(r % 2 == 0 ? u : b[r], a[r]));
        Check("let e = col.a ?? ctx.u; cosine(e, col.b)", r => Cosine32(a[r], b[r]));
        Assert.Equal((double)TensorPrimitives.Dot(u, u), f.Scalar("dot(ctx.u, ctx.u)").AsFloat64());
        Assert.Equal((double)TensorPrimitives.Norm(u), f.Scalar("l2_norm(ctx.u)").AsFloat64());

        // float64: a float64 list, a literal, or a choice that may give one; a float32 list beside one widens.
        Check("dot(col.d, ctx.w)", r => TensorPrimitives.Dot(d[r], w));
        Check("cosine(col.a, col.d)", r => Cosine64(Widen(a[r]), d[r]));
        Check("dot(col.a, ctx.w)", r => TensorPrimitives.Dot(Widen(a[r]), w));
        Check("l2_norm(col.entity_id % 2 == 0 ? col.a : col.d)", r => TensorPrimitives.Norm(r % 2 == 0 ? Widen(a[r]) : d[r]));
        var literal = "[" + string.Join(", ", u.Select(v => ((double)v).ToString("R", CultureInfo.InvariantCulture))) + "]";
        Check($"dot(col.a, {literal})", r => TensorPrimitives.Dot(Widen(a[r]), Widen(u)));
        Assert.Equal(TensorPrimitives.Dot(Widen(u), w), f.Scalar("dot(ctx.u, ctx.w)").AsFloat64());

        if (length >= 100)
        {
            // The two precisions differ here, so the checks above tell them apart.
            Assert.NotEqual((double)TensorPrimitives.Dot(u, a[0]), TensorPrimitives.Dot(Widen(u), Widen(a[0])));
        }
    }

    [Fact]
    public void PiecewiseLinear_InterpolatesAndClamps()
    {
        var f = new ExprFixture().Col("col.x", "float64").Build(8);
        f.Fill<double>("col.x", -1, 0, 0.5, 1, 2, 3, 4, double.NaN);
        ExprAssert.Rows(f, "piecewise_linear(col.x, [0.0, 1.0, 3.0], [0.0, 10.0, 20.0])", 0.0, 0.0, 5.0, 10.0, 15.0, 20.0, 20.0, double.NaN);
        ExprAssert.Rows(f, "piecewise_linear(col.x, [0, 4], [1, -1])", 1.0, 1.0, 0.75, 0.5, 0.0, -0.5, -1.0, double.NaN);
        ExprAssert.Scalar(f, "piecewise_linear(2, [0, 4], [0, 1])", 0.5);
    }

    [Fact]
    public void RankingTransforms()
    {
        var f = new ExprFixture().Col("col.x", "float64").Build(5);
        f.Fill<double>("col.x", 0, 1, 2, 3, 4);
        ExprAssert.Rows(f, "linear_decay(col.x, 1, 3)", 1.0, 1.0, 0.5, 0.0, 0.0);
        ExprAssert.Rows(f, "linear_decay(col.x, 3, 1)", 1.0, 1.0, 1.0, 1.0, 0.0);
        ExprAssert.Rows(f, "sigmoid(col.x - 2.0)", 1 / (1 + Math.Exp(2)), 1 / (1 + Math.Exp(1)), 0.5, 1 / (1 + Math.Exp(-1)), 1 / (1 + Math.Exp(-2)));
        ExprAssert.Rows(f, "logit(col.x / 4.0)", double.NegativeInfinity, Math.Log(1.0 / 3.0), 0.0, Math.Log(3.0), double.PositiveInfinity);
        ExprAssert.Rows(f, "soft_floor(col.x, 2.0)", 2 + Math.Log(1 + Math.Exp(-2)), 2 + Math.Log(1 + Math.Exp(-1)), 2 + Math.Log(2), 3 + Math.Log(1 + Math.Exp(-1)), 4 + Math.Log(1 + Math.Exp(-2)));
        ExprAssert.Rows(f, "soft_floor(col.x, 2.0, 2.0)", 2 + (Math.Log(1 + Math.Exp(-4)) / 2), 2 + (Math.Log(1 + Math.Exp(-2)) / 2), 2 + (Math.Log(2) / 2), 3 + (Math.Log(1 + Math.Exp(-2)) / 2), 4 + (Math.Log(1 + Math.Exp(-4)) / 2));

        ExprAssert.Scalar(f, "sigmoid(-1000.0)", 0.0);
        ExprAssert.Scalar(f, "sigmoid(1000.0)", 1.0);
        ExprAssert.Scalar(f, "logit(2.0)", double.NaN);
        ExprAssert.Scalar(f, "soft_floor(1000.0, 0.0)", 1000.0);
        ExprAssert.Scalar(f, "soft_floor(-50.0, 0.0)", Math.Exp(-50));
        ExprAssert.Scalar(f, "log1p(1e-20)", 1e-20);
        ExprAssert.Scalar(f, "log1p(-1.0)", double.NegativeInfinity);
        ExprAssert.Scalar(f, "log1p(-2.0)", double.NaN);
        ExprAssert.Scalar(f, "log1p(1e300)", Math.Log(1e300));
        ExprAssert.Scalar(f, "max(0.0 / 0.0, 1.0)", double.NaN);
        ExprAssert.Scalar(f, "min(-0.0, 0.0) == 0.0", true);
    }

    [Fact]
    public void Hash01_MatchesStableHash()
    {
        var f = new ExprFixture()
            .Col("col.s", "string")
            .Ctx("ctx.request.seed", "int64", ScalarValue.FromInt64(42))
            .Build(5);
        f.FillStrings("col.s", "a", "b", "é", string.Empty, new string('x', 600));

        ExprAssert.Rows(f, "hash01(ctx.request.seed, col.entity_id)", Enumerable.Range(0, 5).Select(r => (object?)StableHash.Hash01(42, 1000 + r)).ToArray());
        ExprAssert.Rows(f, "hash01(col.entity_id, ctx.request.seed)", Enumerable.Range(0, 5).Select(r => (object?)StableHash.Hash01(1000 + r, 42)).ToArray());
        ExprAssert.Rows(f, "hash01(1, 2, col.entity_id)", Enumerable.Range(0, 5).Select(r => (object?)StableHash.Hash01(1, 2, 1000 + r)).ToArray());
        ExprAssert.Rows(f, "hash01(col.s, col.entity_id, -0.0, col.entity_id > 1002)", Enumerable.Range(0, 5).Select(r =>
        {
            var b = new StableHash.Builder();
            b.Add(r switch { 0 => "a", 1 => "b", 2 => "é", 3 => string.Empty, _ => new string('x', 600) });
            b.Add((long)(1000 + r));
            b.Add(0.0);
            b.Add(r > 2);
            return (object?)StableHash.ToUnit(b.Finish());
        }).ToArray());

        var builder = new StableHash.Builder();
        builder.Add(7L);
        builder.Add(2.5);
        builder.Add(true);
        builder.Add("abc");
        ExprAssert.Scalar(f, "hash01(7, 2.5, true, 'abc')", StableHash.ToUnit(builder.Finish()));
        ExprAssert.Scalar(f, "hash01(ctx.request.seed, 5)", StableHash.Hash01(42, 5));
        ExprAssert.Scalar(f, "hash01(-0.0) == hash01(0.0)", true);

        var single = new StableHash.Builder();
        single.Add(9L);
        ExprAssert.Scalar(f, "hash01(9)", StableHash.ToUnit(single.Finish()));
        Assert.All(f.Rows("hash01(col.entity_id)"), v => Assert.InRange(v.AsFloat64(), 0.0, 0.9999999999999999));
    }

    [Fact]
    public void Lookup_HitsMissesAndMissingKeys()
    {
        var calibration = new TestLookupTable("calib", ColumnType.Int64, ColumnType.Float64, new()
        {
            [ScalarValue.FromInt64(1)] = ScalarValue.FromFloat64(0.5),
            [ScalarValue.FromInt64(2)] = ScalarValue.FromInt64(4), // int stored for a float table: converted
        });
        var weights = new TestLookupTable("country_weight", ColumnType.String, ColumnType.Bool, new()
        {
            [ScalarValue.FromString("NZ")] = ScalarValue.FromBool(true),
        });
        var f = new ExprFixture().Col("col.k", "int32?").Col("col.c", "string").Lookup(calibration).Lookup(weights)
            .Ctx("ctx.country", "string", ScalarValue.FromString("NZ")).Build(4);
        f.Fill<int>("col.k", 1, 2, 3, null);
        f.FillStrings("col.c", "NZ", "AU", "NZ", "US");

        ExprAssert.Rows(f, "lookup('calib', col.k, 1)", 0.5, 4.0, 1.0, 1.0);
        ExprAssert.Rows(f, "lookup('calib', col.k, col.entity_id * 1.0)", 0.5, 4.0, 1002.0, 1003.0);
        ExprAssert.Rows(f, "lookup('country_weight', col.c, false)", true, false, true, false);
        ExprAssert.Scalar(f, "lookup('country_weight', ctx.country, false)", true);
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, f.Errors("lookup('calib', col.c, 1.0)")[0].Code);
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, f.Errors("lookup('calib', col.k, col.k * 1.0)")[0].Code);
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, f.Errors("lookup('calib', col.k, true)")[0].Code);
    }

    [Fact]
    public void NumericPromotion_InComparisonsAndArithmetic()
    {
        var f = new ExprFixture().Col("col.i", "int32").Col("col.x", "float32").Build(3);
        f.Fill<int>("col.i", 1, 2, 3);
        f.Fill<float>("col.x", 0.5f, 2f, 3.25f);
        ExprAssert.Rows(f, "col.i == 2.0", false, true, false);
        ExprAssert.Rows(f, "col.i < col.x", false, false, true);
        ExprAssert.Rows(f, "col.i + col.x", 1.5, 4.0, 6.25);
        ExprAssert.Rows(f, "col.i * 3", 3L, 6L, 9L);
        ExprAssert.Rows(f, "max(col.i, col.x)", 1.0, 2.0, 3.25);
        ExprAssert.Rows(f, "clamp(col.i, 2, 2)", 2L, 2L, 2L);
        ExprAssert.Rows(f, "col.i >= 2 != (col.x > 1.0)", false, false, false);
    }
}
