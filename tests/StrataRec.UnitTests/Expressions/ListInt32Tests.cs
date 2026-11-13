using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// <c>list&lt;int32&gt;</c> columns in ColumnExpr (design 4.3): read in place with int64 element semantics, and the
/// out-of-range rule for every write into int32 storage (<c>int32</c> and <c>list&lt;int32&gt;</c>).
/// </summary>
/// <remarks><see cref="ExprAssert.Rows"/> checks each expected value three ways: full-range (zero-copy column reads),
/// gather (one row at a time) and the reference interpreter.</remarks>
public class ListInt32Tests
{
    private const long Above = 2147483648;   // int.MaxValue + 1: an unchecked narrowing gives int.MinValue
    private const long Wrap3 = 4294967299;   // 2^32 + 3: an unchecked narrowing gives 3
    private const long Below = -2147483649;  // int.MinValue - 1: an unchecked narrowing gives int.MaxValue

    /// <summary>
    /// <code>
    /// row | t32 (list&lt;int32&gt;?)  | u32 (list&lt;int32&gt;) | t64 (list&lt;int64&gt;?) | big (list&lt;int64&gt;?)    | k        | n (int64?)
    /// 0   | [1, 2, 3]          | [3, 3, 9]       | [3, 4]           | [1, 2]              | 3        | 5
    /// 1   | []                 | [3]             | [5]              | [Above]             | Wrap3    | missing (5e9 underneath)
    /// 2   | [Min, 7, 7]        | [7]             | [7, Min]         | [3, Wrap3]          | Above    | Below
    /// 3   | missing            | [1]             | [1, 3]           | missing             | 7        | missing (5e9 underneath)
    /// 4   | [Max, -5]          | [-5, -5]        | missing          | [Min, Max]          | Max      | Min
    /// 5-7 | [0]                | [0]             | [0]              | [0]                 | 0        | 0
    /// </code>
    /// </summary>
    private static ExprFixture Lists()
    {
        var f = new ExprFixture(capacity: 16)
            .Col("col.t32", "list<int32>?")
            .Col("col.u32", "list<int32>")
            .Col("col.t64", "list<int64>?")
            .Col("col.big", "list<int64>?")
            .Col("col.k", "int64")
            .Col("col.n", "int64?")
            .Ctx("ctx.l", "list<int64>", ScalarValue.FromInt64List(new long[] { 7, Wrap3, int.MinValue }))
            .Ctx("ctx.l32", "list<int32>", ScalarValue.FromInt64List(new long[] { 1, 9 }))
            .Ctx("ctx.big", "int64", ScalarValue.FromInt64(5_000_000_000))
            .Ctx("ctx.small", "int64", ScalarValue.FromInt64(12))
            .Build(8);

        var t32 = f.Buffer<ListColumn<int>>("col.t32");
        var u32 = f.Buffer<ListColumn<int>>("col.u32");
        var t64 = f.Buffer<ListColumn<long>>("col.t64");
        var big = f.Buffer<ListColumn<long>>("col.big");
        t32.Set(0, new[] { 1, 2, 3 });
        t32.Set(1, Array.Empty<int>());
        t32.Set(2, new[] { int.MinValue, 7, 7 });
        t32.SetMissing(3);
        t32.Set(4, new[] { int.MaxValue, -5 });
        u32.Set(0, new[] { 3, 3, 9 });
        u32.Set(1, new[] { 3 });
        u32.Set(2, new[] { 7 });
        u32.Set(3, new[] { 1 });
        u32.Set(4, new[] { -5, -5 });
        t64.Set(0, new long[] { 3, 4 });
        t64.Set(1, new long[] { 5 });
        t64.Set(2, new long[] { 7, int.MinValue });
        t64.Set(3, new long[] { 1, 3 });
        t64.SetMissing(4);
        big.Set(0, new long[] { 1, 2 });
        big.Set(1, new[] { Above });
        big.Set(2, new[] { 3, Wrap3 });
        big.SetMissing(3);
        big.Set(4, new long[] { int.MinValue, int.MaxValue });
        for (var r = 5; r < 8; r++)
        {
            t32.Set(r, new[] { 0 });
            u32.Set(r, new[] { 0 });
            t64.Set(r, new long[] { 0 });
            big.Set(r, new long[] { 0 });
        }

        f.Fill<long>("col.k", 3, Wrap3, Above, 7, int.MaxValue, 0, 0, 0);
        var n = f.Scalar<long>("col.n");
        f.Fill<long>("col.n", 5, null, Below, null, int.MinValue, 0, 0, 0);
        n[1] = 5_000_000_000; // values under missing rows are arbitrary; they must not count as out of range
        n[3] = 5_000_000_000;
        return f;
    }

    // ---- Reading: every list function, both row domains, the reference interpreter -------------------------------------

    [Fact]
    public void Size_CountsElements()
    {
        var f = Lists();
        ExprAssert.Rows(f, "size(col.t32)", 3, 0, 3, null, 2);
        ExprAssert.Rows(f, "size(col.u32)", 3, 1, 1, 1, 2);
        ExprAssert.Rows(f, "size(col.t32 ?? col.t64)", 3, 0, 3, 2, 2); // lazy select of mixed widths
        ExprAssert.Rows(f, "has(col.t32)", true, true, true, false, true);
    }

    [Fact]
    public void In_And_Contains_UseInt64Semantics()
    {
        var f = Lists();
        ExprAssert.Rows(f, "col.k in col.t32", true, false, false, null, true);
        ExprAssert.Rows(f, "contains(col.u32, col.k)", true, false, false, false, false);
        ExprAssert.Rows(f, "col.k in (col.t32 ?? col.t64)", true, false, false, false, true);
        ExprAssert.Rows(f, "-2147483648 in col.t32", false, false, true, null, false);
    }

    [Fact]
    public void In_ProbeOutsideTheInt32Range_IsAbsent_NotTruncated()
    {
        var f = Lists();

        // Row 1: 2^32 + 3 against [3] would match after truncation to int32; row 2: 2^31 against [Min, 7, 7] likewise.
        ExprAssert.Rows(f, "col.k in col.u32", true, false, false, false, false);
        ExprAssert.Rows(f, "col.k in col.t32", true, false, false, null, true);
        ExprAssert.Rows(f, "2147483648 in col.t32", false, false, false, null, false);
        ExprAssert.Rows(f, "4294967299 in col.u32", false, false, false, false, false);
        ExprAssert.Scalar(f, "4294967305 in ctx.l32", false); // 2^32 + 9
        ExprAssert.Scalar(f, "9 in ctx.l32", true);
    }

    [Fact]
    public void OverlapCount_AcrossWidths()
    {
        var f = Lists();
        ExprAssert.Rows(f, "overlap_count(col.t32, col.t64)", 1, 0, 3, null, null);
        ExprAssert.Rows(f, "overlap_count(col.t64, col.t32)", 1, 0, 2, null, null);
        ExprAssert.Rows(f, "overlap_count(col.t32, col.u32)", 1, 0, 2, null, 1);
        ExprAssert.Rows(f, "overlap_count(col.t32 ?? col.u32, col.t64 ?? col.u32)", 1, 0, 3, 1, 1);

        // Request-level lists: sorted once per call (int64 keys; 2^32 + 3 must not match 3) or probed per row.
        ExprAssert.Rows(f, "overlap_count(col.u32, ctx.l)", 0, 0, 1, 0, 0);
        ExprAssert.Rows(f, "overlap_count(ctx.l, col.t32)", 0, 0, 2, null, 0);
        ExprAssert.Rows(f, "overlap_count(col.u32, ctx.l32)", 1, 0, 0, 1, 0);
        ExprAssert.Scalar(f, "size(ctx.l32)", 2);
    }

    [Fact]
    public void ListResults_ReadAsInt64Lists()
    {
        var f = Lists();
        ExprAssert.Rows(f, "col.t32 ?? col.t64",
            new long[] { 1, 2, 3 }, Array.Empty<long>(), new long[] { int.MinValue, 7, 7 }, new long[] { 1, 3 }, new long[] { int.MaxValue, -5 });
        ExprAssert.Rows(f, "col.k == 3 ? col.t64 : col.u32",
            new long[] { 3, 4 }, new long[] { 3 }, new long[] { 7 }, new long[] { 1 }, new long[] { -5, -5 });
        ExprAssert.Rows(f, "col.t32 ?? [9]",
            new long[] { 1, 2, 3 }, Array.Empty<long>(), new long[] { int.MinValue, 7, 7 }, new long[] { 9 }, new long[] { int.MaxValue, -5 });
    }

    [Fact]
    public void ListInt32_IsTheSameExpressionTypeAsListInt64()
    {
        var f = Lists();
        Assert.Equal(ColumnType.ListInt64.AsNullable(), f.Compile("col.t32").ResultType);
        Assert.Equal(ColumnType.ListInt64, f.Compile("col.u32").ResultType);

        // Both widths are accepted where an int list is expected, in either direction.
        f.Compile("col.u32", new CompileOptions { ExpectedType = ColumnType.ListInt64 });
        f.Compile("col.t64 ?? [1]", new CompileOptions { ExpectedType = ColumnType.ListInt32 });
        var error = Assert.Single(f.Errors("['a']", new CompileOptions { ExpectedType = ColumnType.ListInt32 }));
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, error.Code);
    }

    // ---- Writing into list<int32>: narrowing and the out-of-range rule ---------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntListResult_IntoListInt32Column_IsNarrowed_OrReplacedAsAWhole(bool withDefault)
    {
        var f = Lists();
        var compiled = f.Compile("col.big");
        var fallback = withDefault ? ScalarValue.FromInt64List(new long[] { 42 }) : ScalarValue.Missing;
        long[]? replaced = withDefault ? new long[] { 42 } : null;
        foreach (var gather in new[] { false, true })
        {
            var before = f.Counters.OutOfRange;
            var destination = Evaluate(f, compiled, "list<int32>?", gather, fallback);

            Assert.IsType<ListColumn<int>>(destination);
            var what = gather ? "gather" : "full range";
            ExprAssert.Same(new long[] { 1, 2 }, ExprFixture.Read(destination, 0), what);
            ExprAssert.Same(replaced, ExprFixture.Read(destination, 1), what);  // [2^31]: not [Min]
            ExprAssert.Same(replaced, ExprFixture.Read(destination, 2), what);  // [3, 2^32 + 3]: not [3, 3], not [3]
            ExprAssert.Same(null, ExprFixture.Read(destination, 3), what);      // a missing source is not out of range
            ExprAssert.Same(new long[] { int.MinValue, int.MaxValue }, ExprFixture.Read(destination, 4), what);
            Assert.Equal(2, f.Counters.OutOfRange - before);
        }
    }

    [Fact]
    public void Int32Lists_CopyAndWiden_WithoutAnyOutOfRange()
    {
        var f = Lists();
        foreach (var gather in new[] { false, true })
        {
            var before = f.Counters.OutOfRange;
            var narrow = Evaluate(f, f.Compile("col.t32 ?? col.u32"), "list<int32>", gather);
            var wide = Evaluate(f, f.Compile("col.t32 ?? col.u32"), "list<int64>", gather);
            var mixed = Evaluate(f, f.Compile("col.k == 3 ? col.t64 : col.t32"), "list<int32>?", gather);

            for (var r = 0; r < 5; r++)
            {
                Assert.Equal(ExprFixture.Read(narrow, r), ExprFixture.Read(wide, r));
            }

            ExprAssert.Same(new long[] { int.MaxValue, -5 }, ExprFixture.Read(narrow, 4), "narrow");
            ExprAssert.Same(new long[] { 1 }, ExprFixture.Read(narrow, 3), "narrow");
            ExprAssert.Same(new long[] { 3, 4 }, ExprFixture.Read(mixed, 0), "mixed");
            ExprAssert.Same(new long[] { int.MinValue, 7, 7 }, ExprFixture.Read(mixed, 2), "mixed");
            ExprAssert.Same(null, ExprFixture.Read(mixed, 3), "mixed");
            Assert.Equal(before, f.Counters.OutOfRange);
        }
    }

    [Fact]
    public void ConstantLists_IntoListInt32Column_GoThroughTheSameNarrowing()
    {
        var f = Lists();
        var rows = new[] { 0, 1, 2 };

        // Request-level: one value broadcast to every listed row.
        var fits = ColumnBuffer.Create(ColumnType.ListInt32, f.Capacity, f.Memory);
        f.Compile("[1, -2147483648]").Evaluate(f.Context, rows, fits);
        Assert.All(rows, r => ExprAssert.Same(new long[] { 1, int.MinValue }, ExprFixture.Read(fits, r), $"row {r}"));
        Assert.Equal(0, f.Counters.OutOfRange);

        var wide = ColumnBuffer.Create(ColumnType.ListInt32, f.Capacity, f.Memory);
        f.Compile("[1, 5000000000]").Evaluate(f.Context, rows, wide, ScalarValue.FromInt64List(new long[] { 0 }));
        Assert.All(rows, r => ExprAssert.Same(new long[] { 0 }, ExprFixture.Read(wide, r), $"row {r}"));
        Assert.Equal(3, f.Counters.OutOfRange); // once per row

        var missing = ColumnBuffer.Create(ColumnType.ListInt32.AsNullable(), f.Capacity, f.Memory);
        f.Compile("ctx.small > 0 ? [5000000000] : [1]").Evaluate(f.Context, rows, missing);
        Assert.All(rows, r => Assert.False(missing.IsValid(r)));
        Assert.Equal(6, f.Counters.OutOfRange);

        // Row-level: a literal in one branch.
        foreach (var gather in new[] { false, true })
        {
            var before = f.Counters.OutOfRange;
            var branch = Evaluate(f, f.Compile("col.k == 3 ? [5000000000] : [1]"), "list<int32>?", gather);
            ExprAssert.Same(null, ExprFixture.Read(branch, 0), "branch");
            ExprAssert.Same(new long[] { 1 }, ExprFixture.Read(branch, 1), "branch");
            Assert.Equal(1, f.Counters.OutOfRange - before);
        }
    }

    [Fact]
    public void ConstantOutOfRange_ForAnInt32Site_IsACompileError_WithAFix()
    {
        var f = Lists();
        var list = Assert.Single(f.Errors("[1, 5000000000]", new CompileOptions { ExpectedType = ColumnType.ListInt32 }));
        Assert.Equal(ExpressionErrorCodes.InvalidArgument, list.Code);
        Assert.Equal(0, list.Offset);
        Assert.Contains("list<int32>", list.Message);
        Assert.Contains("list<int64>", list.Hint);

        var scalar = Assert.Single(f.Errors("3000000000", new CompileOptions { ExpectedType = ColumnType.Int32 }));
        Assert.Equal(ExpressionErrorCodes.InvalidArgument, scalar.Code);
        Assert.Contains("int64", scalar.Hint);

        f.Compile("[-2147483648, 2147483647]", new CompileOptions { ExpectedType = ColumnType.ListInt32 });
        f.Compile("2147483647", new CompileOptions { ExpectedType = ColumnType.Int32 });
    }

    // ---- Writing into int32: the narrowing bug (it used to wrap) --------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Int64Result_IntoInt32Column_OutOfRange_TakesTheDefaultOrMissing_AndIsCounted(bool withDefault)
    {
        var f = Lists();
        var compiled = f.Compile("col.k");
        var fallback = withDefault ? ScalarValue.FromInt64(-1) : ScalarValue.Missing;
        object? replaced = withDefault ? -1 : null;
        foreach (var gather in new[] { false, true })
        {
            var before = f.Counters.OutOfRange;
            var destination = Evaluate(f, compiled, "int32", gather, fallback);

            var what = gather ? "gather" : "full range";
            ExprAssert.Same(3, ExprFixture.Read(destination, 0), what);
            ExprAssert.Same(replaced, ExprFixture.Read(destination, 1), what); // 2^32 + 3 used to be written as 3
            ExprAssert.Same(replaced, ExprFixture.Read(destination, 2), what); // 2^31 used to be written as int.MinValue
            ExprAssert.Same(7, ExprFixture.Read(destination, 3), what);
            ExprAssert.Same(int.MaxValue, ExprFixture.Read(destination, 4), what);
            Assert.Equal(2, f.Counters.OutOfRange - before);
        }
    }

    [Fact]
    public void MissingLanes_HoldingLargeValues_AreNotCountedAsOutOfRange()
    {
        var f = Lists();
        foreach (var gather in new[] { false, true })
        {
            var before = f.Counters.OutOfRange;
            var destination = Evaluate(f, f.Compile("col.n"), "int32?", gather);

            ExprAssert.Same(5, ExprFixture.Read(destination, 0), "n");
            ExprAssert.Same(null, ExprFixture.Read(destination, 1), "n"); // missing (5e9 underneath)
            ExprAssert.Same(null, ExprFixture.Read(destination, 2), "n"); // -2^31 - 1: out of range
            ExprAssert.Same(null, ExprFixture.Read(destination, 3), "n"); // missing (5e9 underneath)
            ExprAssert.Same(int.MinValue, ExprFixture.Read(destination, 4), "n");
            Assert.Equal(1, f.Counters.OutOfRange - before);
        }
    }

    [Fact]
    public void ArithmeticResults_AreCheckedToo()
    {
        var f = Lists();
        foreach (var gather in new[] { false, true })
        {
            var before = f.Counters.OutOfRange;
            var destination = Evaluate(f, f.Compile("col.k - 1"), "int32", gather, ScalarValue.FromInt64(0));
            ExprAssert.Same(2, ExprFixture.Read(destination, 0), "k - 1");
            ExprAssert.Same(0, ExprFixture.Read(destination, 1), "k - 1");
            ExprAssert.Same(int.MaxValue, ExprFixture.Read(destination, 2), "k - 1"); // 2^31 - 1 fits
            ExprAssert.Same(int.MaxValue - 1, ExprFixture.Read(destination, 4), "k - 1");
            Assert.Equal(1, f.Counters.OutOfRange - before);
        }
    }

    [Fact]
    public void RequestLevelValue_OutOfRange_IsReplacedOnEveryListedRow()
    {
        var f = Lists();
        var rows = new[] { 0, 2, 4 };
        var destination = ColumnBuffer.Create(ColumnType.Int32, f.Capacity, f.Memory);

        f.Compile("ctx.big").Evaluate(f.Context, rows, destination, ScalarValue.FromInt64(9));
        Assert.All(rows, r => ExprAssert.Same(9, ExprFixture.Read(destination, r), $"row {r}"));
        Assert.Equal(3, f.Counters.OutOfRange);

        f.Compile("ctx.big").Evaluate(f.Context, rows, destination);
        Assert.All(rows, r => Assert.False(destination.IsValid(r)));
        Assert.Equal(6, f.Counters.OutOfRange);

        f.Compile("ctx.big - 4999999990 + ctx.small").Evaluate(f.Context, rows, destination);
        Assert.All(rows, r => ExprAssert.Same(22, ExprFixture.Read(destination, r), $"row {r}"));
        Assert.Equal(6, f.Counters.OutOfRange);
    }

    [Fact]
    public void Replacement_MustFitTheDestinationItself()
    {
        var f = Lists();
        var rows = new[] { 0, 1 };
        var int32 = ColumnBuffer.Create(ColumnType.Int32, f.Capacity, f.Memory);
        var list32 = ColumnBuffer.Create(ColumnType.ListInt32, f.Capacity, f.Memory);

        Assert.Throws<ArgumentException>(() => f.Compile("col.k").Evaluate(f.Context, rows, int32, ScalarValue.FromInt64(Above)));
        Assert.Throws<ArgumentException>(() => f.Compile("col.k").Evaluate(f.Context, rows, int32, ScalarValue.FromFloat64(1)));
        Assert.Throws<ArgumentException>(() => f.Compile("col.u32").Evaluate(f.Context, rows, list32, ScalarValue.FromInt64List(new[] { Below })));

        // Destinations without int32 storage never need it.
        var int64 = ColumnBuffer.Create(ColumnType.Int64, f.Capacity, f.Memory);
        f.Compile("col.k").Evaluate(f.Context, rows, int64, ScalarValue.FromInt64(Above));
        ExprAssert.Same(Wrap3, ExprFixture.Read(int64, 1), "int64");
    }

    // ---- Allocation -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ReadingListInt32_DoesNotAllocate_InSteadyState()
    {
        var f = Lists();
        var expressions = new[]
        {
            ("size(col.t32)", "int64?"),
            ("size(col.u32 ?? col.t64)", "int64"),
            ("col.k in col.t32", "bool?"),
            ("contains(col.u32, col.k) ? 1.0 : 0.0", "float64"),
            ("col.k in (col.t32 ?? col.u32)", "bool"),
            ("overlap_count(col.u32, ctx.l)", "int64"),
            ("overlap_count(col.t32, col.t64 ?? col.u32)", "int64?"),
            ("overlap_count(col.u32, col.u32)", "int64"),
            ("col.k", "int32"), // narrowing write, in and out of range
        };

        foreach (var (source, type) in expressions)
        {
            var compiled = f.Compile(source);
            var destination = ColumnBuffer.Create(ColumnType.Parse(type), f.Capacity, f.Memory);
            var some = new[] { 1, 4 };
            for (var i = 0; i < 20; i++)
            {
                compiled.Evaluate(f.Context, f.Table.Rows.Order, destination);
                compiled.Evaluate(f.Context, some, destination);
            }

            // Best of three rounds: a one-time lazy initialization or tier-up can land in a round, but a steady-state
            // allocation shows up in every round.
            var allocated = long.MaxValue;
            for (var round = 0; round < 3 && allocated != 0; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 100; i++)
                {
                    compiled.Evaluate(f.Context, f.Table.Rows.Order, destination);
                    compiled.Evaluate(f.Context, some, destination);
                }

                allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            Assert.True(allocated == 0, $"'{source}' allocated {allocated} bytes per 100 evaluations");
        }
    }

    /// <summary>Evaluates rows 0–4 (full range: 5 of 8 physical rows) or each row alone (gather) into a fresh column.</summary>
    private static ColumnBuffer Evaluate(ExprFixture f, CompiledExpression compiled, string type, bool gather, ScalarValue outOfRange = default)
    {
        var destination = ColumnBuffer.Create(ColumnType.Parse(type), f.Capacity, f.Memory);
        if (gather)
        {
            for (var r = 0; r < 5; r++)
            {
                compiled.Evaluate(f.Context, new[] { r }, destination, outOfRange);
            }
        }
        else
        {
            compiled.Evaluate(f.Context, new[] { 0, 1, 2, 3, 4 }, destination, outOfRange);
        }

        return destination;
    }
}
