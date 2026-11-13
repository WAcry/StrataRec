using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using StrataRec.Core.Columns;
using StrataRec.Core.Hashing;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// Regression tests for the external review of the ColumnExpr engine (findings #11, #12, #13, #15, #16). Every test
/// asserts the expected value, not only agreement between the backends.
/// </summary>
public class ExpressionReviewRegressionTests
{
    // ------------------------------------------------------------------------------------------------------------
    // #11: desugared built-ins must lift every argument (design 7.4 rule 2).

    private static readonly double?[] Grid = { null, -1.0, 0.0, 0.5, 1.0, 2.0, double.NaN };

    [Fact]
    public void LinearDecay_MissingEnd_IsMissing_SoCoalesceTakesTheDefault()
    {
        // The reviewer's repro: x <= start used to return the constant 1 without looking at end.
        var f = new ExprFixture().Ctx("ctx.end", "float64?").Build(3);
        ExprAssert.Scalar(f, "linear_decay(0.0, 1.0, ctx.end) ?? 0.0", 0.0);
        ExprAssert.Scalar(f, "linear_decay(0.0, 1.0, ctx.end)", null);
        var compiled = f.Compile("linear_decay(0.0, 1.0, ctx.end) ?? 0.0");
        ExprAssert.Same(0.0, f.Reference(compiled, 0), "reference interpreter");

        f.SetCtx("ctx.end", ScalarValue.FromFloat64(2.0));
        ExprAssert.Scalar(f, "linear_decay(0.0, 1.0, ctx.end) ?? 0.0", 1.0);
        ExprAssert.Scalar(f, "linear_decay(1.5, 1.0, ctx.end) ?? 0.0", 0.5);
    }

    [Fact]
    public void LinearDecay_AnyMissingArgument_IsMissing_OtherwiseMatchesDefinition()
    {
        // Every combination of missing / present (including NaN) arguments, row by row.
        var f = new ExprFixture(capacity: Grid.Length * Grid.Length * Grid.Length)
            .Col("col.x", "float64?").Col("col.s", "float64?").Col("col.e", "float64?")
            .Build(Grid.Length * Grid.Length * Grid.Length);
        var expected = new List<object?>();
        var row = 0;
        foreach (var x in Grid)
        {
            foreach (var s in Grid)
            {
                foreach (var e in Grid)
                {
                    SetOrMissing(f, "col.x", row, x);
                    SetOrMissing(f, "col.s", row, s);
                    SetOrMissing(f, "col.e", row, e);
                    expected.Add(x is { } xv && s is { } sv && e is { } ev ? LinearDecay(xv, sv, ev) : null);
                    row++;
                }
            }
        }

        ExprAssert.Rows(f, "linear_decay(col.x, col.s, col.e)", expected.ToArray());
        ExprAssert.Rows(f, "linear_decay(col.x, col.s, col.e) ?? -7.0", expected.Select(v => v ?? -7.0).ToArray());
    }

    /// <summary>Every desugared built-in (and the primitive numeric ones) with one argument position missing.</summary>
    public static TheoryData<string, int> NumericBuiltins() => new()
    {
        { "min({0}, {1})", 2 },
        { "min({0}, {1}, {2})", 3 },
        { "max({0}, {1}, {2})", 3 },
        { "clamp({0}, {1}, {2})", 3 },
        { "logit({0})", 1 },
        { "soft_floor({0}, {1})", 2 },
        { "soft_floor({0}, {1}, {2})", 3 },
        { "linear_decay({0}, {1}, {2})", 3 },
        { "({0} in [0.5, 1.0, 2])", 1 },
        { "abs({0})", 1 },
        { "sqrt({0})", 1 },
        { "exp({0})", 1 },
        { "log({0})", 1 },
        { "log1p({0})", 1 },
        { "sigmoid({0})", 1 },
        { "pow({0}, {1})", 2 },
        { "piecewise_linear({0}, [0.0, 1.0], [1.0, 0.0])", 1 },
        { "hash01({0}, {1})", 2 },
    };

    [Theory]
    [MemberData(nameof(NumericBuiltins))]
    public void Builtins_LiftAMissingArgument_InEveryPosition(string template, int arity)
    {
        var present = new[] { -1.0, 0.0, 0.5, 1.0, 2.0, double.NaN };
        var others = 1;
        for (var i = 1; i < arity; i++)
        {
            others *= present.Length;
        }

        for (var missing = 0; missing < arity; missing++)
        {
            // Row level: the missing argument is a column whose rows hold values but are marked missing.
            var f = new ExprFixture(capacity: Math.Max(3, others))
                .Col("col.m", "float64?").Col("col.a0", "float64").Col("col.a1", "float64")
                .Ctx("ctx.m", "float64?").Ctx("ctx.a0", "float64").Ctx("ctx.a1", "float64")
                .Build(Math.Max(3, others));
            for (var r = 0; r < Math.Max(3, others); r++)
            {
                f.Scalar<double>("col.m").Set(r, 0.25);
                f.Scalar<double>("col.m").SetMissing(r);
                f.Scalar<double>("col.a0").Set(r, present[r % present.Length]);
                f.Scalar<double>("col.a1").Set(r, present[r / present.Length % present.Length]);
            }

            var rowSource = Instantiate(template, arity, missing, "col.m", "col.a");
            ExprAssert.Rows(f, $"has({rowSource})", Enumerable.Repeat((object?)false, Math.Max(3, others)).ToArray());

            // Request level: the same through the scalar program (optimized) and the reference interpreter.
            var scalarSource = Instantiate(template, arity, missing, "ctx.m", "ctx.a");
            var compiled = f.Compile($"has({scalarSource})");
            for (var k = 0; k < others; k++)
            {
                f.SetCtx("ctx.a0", ScalarValue.FromFloat64(present[k % present.Length]));
                f.SetCtx("ctx.a1", ScalarValue.FromFloat64(present[k / present.Length % present.Length]));
                ExprAssert.Same(false, compiled.EvaluateScalar(f.Context), $"{scalarSource} (scalar program, k={k})");
                ExprAssert.Same(false, f.Reference(compiled, 0), $"{scalarSource} (reference, k={k})");
            }
        }
    }

    private static string Instantiate(string template, int arity, int missing, string missingName, string otherPrefix)
    {
        var args = new object[arity];
        var next = 0;
        for (var i = 0; i < arity; i++)
        {
            args[i] = i == missing ? missingName : otherPrefix + (next++).ToString(CultureInfo.InvariantCulture);
        }

        return string.Format(CultureInfo.InvariantCulture, template, args);
    }

    private static double LinearDecay(double x, double start, double end) =>
        x <= start ? 1.0 : x >= end ? 0.0 : (end - x) / (end - start);

    private static void SetOrMissing(ExprFixture f, string column, int row, double? value)
    {
        var c = f.Scalar<double>(column);
        c.Set(row, value ?? 123.0);
        if (value is null)
        {
            c.SetMissing(row);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // #12: every NaN hashes as the canonical quiet NaN, so rewrites that change NaN sign/payload cannot change hash01.

    private const long CanonicalNaNBits = 0x7FF8000000000000;

    private static readonly double[] NaNs =
    {
        double.NaN,
        -double.NaN,
        BitConverter.Int64BitsToDouble(CanonicalNaNBits),
        BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000)),
        BitConverter.Int64BitsToDouble(0x7FF8000000000001),
        BitConverter.Int64BitsToDouble(0x7FF0000000000001), // signaling payload
        BitConverter.Int64BitsToDouble(unchecked((long)0xFFFFFFFFFFFFFFFF)),
    };

    /// <summary>hash01 of a single float64 NaN, computed from the documented canonical encoding.</summary>
    private static double CanonicalNaNHash01()
    {
        Span<byte> bytes = stackalloc byte[9];
        bytes[0] = 0x02;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[1..], CanonicalNaNBits);
        return StableHash.ToUnit(XxHash3.HashToUInt64(bytes));
    }

    [Fact]
    public void StableHashBuilder_HashesEveryNaN_AsTheCanonicalQuietNaN()
    {
        var expected = CanonicalNaNHash01();
        foreach (var nan in NaNs)
        {
            using var b = new StableHash.Builder();
            b.Add(nan);
            Assert.Equal(expected, StableHash.ToUnit(b.Finish()));

            using var v = new StableHash.Builder();
            v.Add(ScalarValue.FromFloat64(nan));
            Assert.Equal(expected, StableHash.ToUnit(v.Finish()));
        }

        // -0.0 still hashes as 0.0, and ordinary values keep their IEEE bytes.
        Span<byte> bytes = stackalloc byte[9];
        bytes[0] = 0x02;
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[1..], 0.0);
        using (var zero = new StableHash.Builder())
        {
            zero.Add(-0.0);
            Assert.Equal(XxHash3.HashToUInt64(bytes), zero.Finish());
        }

        BinaryPrimitives.WriteDoubleLittleEndian(bytes[1..], 2.5);
        using (var plain = new StableHash.Builder())
        {
            plain.Add(2.5);
            Assert.Equal(XxHash3.HashToUInt64(bytes), plain.Finish());
        }

        Assert.Equal(CanonicalNaNBits, StableHash.CanonicalFloat64Bits(-double.NaN));
        Assert.Equal(0L, StableHash.CanonicalFloat64Bits(-0.0));
    }

    [Fact]
    public void Hash01_DomainErrorIntermediates_HashAsCanonicalNaN_OnEveryPath()
    {
        var nan = CanonicalNaNHash01();
        var f = new ExprFixture()
            .Col("col.x", "float64").Col("col.z", "float64").Col("col.p", "float64?")
            .Ctx("ctx.neg", "float64", ScalarValue.FromFloat64(-1.0))
            .Ctx("ctx.nan", "float64", ScalarValue.FromFloat64(BitConverter.Int64BitsToDouble(0x7FF8000000000001)))
            .Param("param.w", "float64", ScalarValue.FromFloat64(3.0))
            .Build(4);
        for (var r = 0; r < 4; r++)
        {
            f.Scalar<double>("col.x").Set(r, -1.0 - r);
            f.Scalar<double>("col.z").Set(r, 0.0);
            f.Scalar<double>("col.p").Set(r, BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000002)));
        }

        // Row level: optimized vectorized program (full range and gather) and the unoptimized reference interpreter.
        string[] rowLevel =
        {
            "hash01((-sqrt(col.x)) * 2.0)",            // the reviewer's repro: Neg moved into the invariant
            "hash01((-sqrt(col.x)) / param.w)",
            "hash01(2.0 * (-log(col.x)))",
            "hash01(col.z / col.z)",
            "hash01(-(col.z / col.z))",
            "hash01(log(col.x))",
            "hash01(min(ctx.nan, sqrt(col.x)))",       // scalar-left min/max lowering swaps the operands
            "hash01(max(ctx.nan, col.p))",
            "hash01(min(col.p, ctx.nan))",
            "hash01(max(0.0 / 0.0, col.x / 0.0 * 0.0))",
        };
        foreach (var source in rowLevel)
        {
            ExprAssert.Rows(f, source, nan, nan, nan, nan);
        }

        // Request level: optimized scalar program and constant folding vs. the unoptimized reference interpreter.
        string[] requestLevel =
        {
            "hash01((-sqrt(ctx.neg)) * 2.0)",
            "hash01((-sqrt(ctx.neg)) / param.w)",
            "hash01((-sqrt(-1.0)) * 2.0)",
            "hash01(0.0 / 0.0)",
            "hash01(-(0.0 / 0.0))",
            "hash01(log(-1.0))",
            "hash01(sqrt(ctx.neg))",
            "hash01(min(ctx.nan, sqrt(ctx.neg)))",
        };
        foreach (var source in requestLevel)
        {
            var compiled = f.Compile(source);
            ExprAssert.Same(nan, compiled.EvaluateScalar(f.Context), source + " (scalar program)");
            ExprAssert.Same(nan, f.Reference(compiled, 0), source + " (reference)");
        }
    }

    [Fact]
    public void Hash01_MixedArguments_WithNaN_AgreeWithStableHashBuilder()
    {
        var f = new ExprFixture().Col("col.x", "float64").Ctx("ctx.s", "string", ScalarValue.FromString("salt")).Build(3);
        for (var r = 0; r < 3; r++)
        {
            f.Scalar<double>("col.x").Set(r, -4.0);
        }

        var expected = new object?[3];
        for (var r = 0; r < 3; r++)
        {
            using var b = new StableHash.Builder();
            b.Add("salt");
            b.Add(NaNs[r]); // any NaN encodes the same
            b.Add(1000L + r);
            b.Add(true);
            expected[r] = StableHash.ToUnit(b.Finish());
        }

        ExprAssert.Rows(f, "hash01(ctx.s, sqrt(col.x), col.entity_id, true)", expected);
        ExprAssert.Rows(f, "hash01(ctx.s, -sqrt(col.x) * 2.0, col.entity_id, true)", expected);
    }

    // ------------------------------------------------------------------------------------------------------------
    // #13: malformed escapes are syntax diagnostics (offset + hint), never exceptions out of TryCompile.

    [Theory]
    [InlineData(@"'\UFFFFFFFF'", 1)]
    [InlineData(@"'\U80000000'", 1)]
    [InlineData(@"'\U00110000'", 1)]
    [InlineData(@"'\U0000D800'", 1)]
    [InlineData(@"'\uD800'", 1)]
    [InlineData(@"'\uDFFF'", 1)]
    [InlineData(@"'ab\uDC00cd'", 3)]
    [InlineData(@"'\uD83D\uDE00'", 1)] // a surrogate pair is two invalid escapes; write \U0001F600
    [InlineData(@"'\x'", 1)]
    [InlineData(@"'\x4'", 1)]
    [InlineData(@"'\xG1'", 1)]
    [InlineData(@"'\u12'", 1)]
    [InlineData(@"'\U1234567'", 1)]
    [InlineData(@"'\4'", 1)]
    [InlineData(@"'\08'", 1)]
    [InlineData(@"'\0", 1)]
    [InlineData(@"'\", 1)]
    [InlineData(@"'abc\", 4)]
    [InlineData(@"""\z""", 1)]
    public void MalformedEscapes_AreSyntaxDiagnostics_WithOffsetAndHint(string source, int offset)
    {
        var scope = new DictionaryScope();
        var ok = ExpressionCompiler.TryCompile(source, scope, CompileOptions.Any, out _, out var diagnostics);
        Assert.False(ok);
        var d = Assert.Single(diagnostics);
        Assert.Equal(ExpressionErrorCodes.Syntax, d.Code);
        Assert.Equal(offset, d.Offset);
        Assert.InRange(d.Offset + d.Length, d.Offset + 1, source.Length + 1);
        Assert.False(string.IsNullOrEmpty(d.Hint), $"'{source}': {d.Message} has no hint");
    }

    [Fact]
    public void UnicodeEscapes_DecodeEveryScalarValue_AndRejectEverythingElse()
    {
        var f = new ExprFixture().Build(3);
        Assert.Equal("\U0001F600", f.Compile(@"'\U0001F600'").EvaluateScalar(f.Context).AsString());
        Assert.Equal("\U0010FFFF", f.Compile(@"'\U0010FFFF'").EvaluateScalar(f.Context).AsString());
        Assert.Equal("\u00E9\uFFFF\u0000", f.Compile(@"'\u00e9\uFFFF\x00'").EvaluateScalar(f.Context).AsString());
        Assert.Equal("\u00FF", f.Compile(@"'\377'").EvaluateScalar(f.Context).AsString());

        var random = new Random(13);
        uint[] edges = { 0, 0xD7FF, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xE000, 0xFFFF, 0x10000, 0x10FFFF, 0x110000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF };
        for (var i = 0; i < 2000 + edges.Length; i++)
        {
            var value = i < edges.Length ? edges[i] : (i % 2 == 0 ? (uint)random.Next(0x120000) : (uint)random.NextInt64(0x1_0000_0000));
            var source = $@"'\U{value:X8}'";
            var valid = value <= 0x10FFFF && value is not (>= 0xD800 and <= 0xDFFF);
            var ok = ExpressionCompiler.TryCompile(source, f.Scope, CompileOptions.Any, out var compiled, out var diagnostics);
            Assert.True(ok == valid, $"{source}: compiled={ok}");
            if (valid)
            {
                Assert.Equal(char.ConvertFromUtf32((int)value), compiled!.EvaluateScalar(f.Context).AsString());
            }
            else
            {
                var d = Assert.Single(diagnostics);
                Assert.Equal(ExpressionErrorCodes.Syntax, d.Code);
                Assert.Equal(1, d.Offset);
                Assert.Equal(10, d.Length);
                Assert.Contains("U+10FFFF", d.Hint);
            }
        }
    }

    [Fact]
    public void Lexer_Fuzz_NeverThrows_AndReportsInRangeDiagnostics()
    {
        const string alphabet = "\\\\\\'\"xXuUrRb0123789aAfFdD +()[],.?:;\n";
        var f = new ExprFixture().Col("col.x", "float64").Build(3);
        var random = new Random(1300);
        for (var i = 0; i < 20_000; i++)
        {
            var length = random.Next(1, 14);
            var chars = new char[length];
            for (var k = 0; k < length; k++)
            {
                chars[k] = alphabet[random.Next(alphabet.Length)];
            }

            var body = new string(chars);
            var source = random.Next(3) switch
            {
                0 => body,
                1 => "'" + body,
                _ => "'" + body + "'",
            };

            bool ok;
            IReadOnlyList<ExpressionDiagnostic> diagnostics;
            try
            {
                ok = ExpressionCompiler.TryCompile(source, f.Scope, CompileOptions.Any, out _, out diagnostics);
            }
            catch (Exception ex)
            {
                Assert.Fail($"TryCompile threw for {Show(source)}: {ex}");
                return;
            }

            if (!ok)
            {
                Assert.NotEmpty(diagnostics);
                foreach (var d in diagnostics)
                {
                    Assert.True(d.Offset >= 0 && d.Length >= 1 && d.Offset <= source.Length, $"{Show(source)}: {d.Code} at {d.Offset}+{d.Length}");
                }
            }
        }

        static string Show(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    // ------------------------------------------------------------------------------------------------------------
    // #15: evaluating into a column the expression reads (in-place update) uses each row's values from before the call.

    [Fact]
    public void Coalesce_IntoTheListColumnItReads_TakesTheFallback_NotStaleData()
    {
        foreach (var gather in new[] { false, true })
        {
            var f = new ExprFixture(capacity: 8).Col("col.a", "list<int64>?").Build(8);
            var a = f.Buffer<ListColumn<long>>("col.a");
            a.Set(0, new long[] { 1 });
            a.Set(1, new long[] { 99 });
            a.SetMissing(1); // stale data behind a missing row
            a.Set(2, new long[] { 3, 4 });
            a.SetMissing(3);
            for (var r = 4; r < 8; r++)
            {
                a.Set(r, new long[] { r });
            }

            int[] rows = gather ? new[] { 1, 2 } : new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
            f.Compile("col.a ?? [7]").Evaluate(f.Context, rows, a);

            var what = gather ? "gather" : "full range";
            ExprAssert.Same(new long[] { 7 }, ExprFixture.Read(a, 1), "row 1 " + what);
            ExprAssert.Same(new long[] { 3, 4 }, ExprFixture.Read(a, 2), "row 2 " + what);
            if (!gather)
            {
                ExprAssert.Same(new long[] { 1 }, ExprFixture.Read(a, 0), "row 0");
                ExprAssert.Same(new long[] { 7 }, ExprFixture.Read(a, 3), "row 3");
                ExprAssert.Same(new long[] { 5 }, ExprFixture.Read(a, 5), "row 5");
            }
            else
            {
                ExprAssert.Same(null, ExprFixture.Read(a, 3), "unlisted row 3 stays missing");
            }
        }
    }

    [Fact]
    public void InPlaceUpdates_OfListsVectorsAndScalars_ReadTheOldValues()
    {
        var f = new ExprFixture(capacity: 4)
            .Col("col.a", "list<int64>?").Col("col.b", "list<int64>?").Col("col.c", "bool")
            .Col("col.s", "list<string>?").Col("col.v", "vec<float32,2>?").Col("col.x", "float64?")
            .Ctx("ctx.l", "list<int64>?").Ctx("ctx.u", "vec<float32,2>", ScalarValue.FromVector(new[] { 7f, 7f }))
            .Build(4);
        var a = f.Buffer<ListColumn<long>>("col.a");
        var b = f.Buffer<ListColumn<long>>("col.b");
        var s = f.Buffer<ListColumn<string>>("col.s");
        var v = f.Buffer<VectorColumn>("col.v");
        for (var r = 0; r < 4; r++)
        {
            a.Set(r, new long[] { 10 + r });
            b.Set(r, new long[] { 20 + r });
            s.Set(r, new[] { "old" + r });
            v.Set(r, new[] { 9f, 9f });
            f.Scalar<double>("col.x").Set(r, 99.0);
        }

        // Rows 1 and 3 of every nullable column hold data but are missing.
        foreach (var column in new ColumnBuffer[] { a, b, s, v, f.Scalar<double>("col.x") })
        {
            column.SetMissing(1);
            column.SetMissing(3);
        }

        f.Fill<bool>("col.c", true, false, true, false);
        var rows = new[] { 0, 1, 2, 3 };

        // Nullable result: the fallback is missing, so missing rows stay missing.
        f.Compile("col.a ?? ctx.l").Evaluate(f.Context, rows, a);
        ExprAssert.Same(null, ExprFixture.Read(a, 1), "a ?? missing");
        f.SetCtx("ctx.l", ScalarValue.FromInt64List(new long[] { 5, 6 }));
        f.Compile("col.a ?? ctx.l").Evaluate(f.Context, rows, a);
        Assert.Equal(new object?[] { new long[] { 10 }, new long[] { 5, 6 }, new long[] { 12 }, new long[] { 5, 6 } }, ReadAll(a, rows));

        // Nested lazy selections, written into the inner coalesce's column.
        b.SetMissing(0);
        f.Compile("col.b ?? (col.a ?? [7])").Evaluate(f.Context, rows, b);
        Assert.Equal(new object?[] { new long[] { 10 }, new long[] { 5, 6 }, new long[] { 22 }, new long[] { 5, 6 } }, ReadAll(b, rows));

        // A conditional whose branches read the destination.
        f.Compile("col.c ? [8] : col.b").Evaluate(f.Context, rows, b);
        Assert.Equal(new object?[] { new long[] { 8 }, new long[] { 5, 6 }, new long[] { 8 }, new long[] { 5, 6 } }, ReadAll(b, rows));

        f.Compile("col.s ?? ['z']").Evaluate(f.Context, rows, s);
        Assert.Equal(new object?[] { new[] { "old0" }, new[] { "z" }, new[] { "old2" }, new[] { "z" } }, ReadAll(s, rows));

        f.Compile("col.v ?? ctx.u").Evaluate(f.Context, rows, v);
        Assert.Equal(new object?[] { new[] { 9f, 9f }, new[] { 7f, 7f }, new[] { 9f, 9f }, new[] { 7f, 7f } }, ReadAll(v, rows));

        var x = f.Scalar<double>("col.x");
        f.Compile("col.x ?? 7.0").Evaluate(f.Context, rows, x);
        Assert.Equal(new object?[] { 99.0, 7.0, 99.0, 7.0 }, ReadAll(x, rows));
    }

    private static object?[] ReadAll(ColumnBuffer column, int[] rows)
    {
        var values = new object?[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var value = ExprFixture.Read(column, rows[i]);
            values[i] = value.Kind switch
            {
                0 => null,
                StorageKind.ListInt64 => value.AsInt64List(),
                StorageKind.ListString => value.AsStringList(),
                StorageKind.Vector => value.AsVector(),
                _ => value.AsFloat64(),
            };
        }

        return values;
    }

    // ------------------------------------------------------------------------------------------------------------
    // #16: request-level results never hand out the compiled expression's own constant arrays.

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("ctx.c ? [1, 2] : [3]")]
    [InlineData("ctx.l ?? [1, 2]")]
    [InlineData("let l = [1, 2]; l")]
    public void EvaluateScalar_IntListConstant_IsACopy(string source)
    {
        var f = new ExprFixture().Ctx("ctx.c", "bool", ScalarValue.FromBool(true)).Ctx("ctx.l", "list<int64>?").Build(3);
        var compiled = f.Compile(source);
        var first = compiled.EvaluateScalar(f.Context);
        Assert.Equal(new long[] { 1, 2 }, first.AsInt64List());
        first.AsInt64List()[0] = 99;
        Assert.Equal(new long[] { 1, 2 }, compiled.EvaluateScalar(f.Context).AsInt64List());

        // The row-level broadcast writes the original constant, too.
        var destination = ColumnBuffer.Create(ColumnType.ListInt64, f.Capacity, f.Memory);
        compiled.Evaluate(f.Context, f.Table.Rows.Order, destination);
        ExprAssert.Same(new long[] { 1, 2 }, ExprFixture.Read(destination, 0), "broadcast");
    }

    [Fact]
    public void EvaluateScalar_StringListConstant_IsACopy_AndCallerValuesPassThrough()
    {
        var f = new ExprFixture().Ctx("ctx.l", "list<string>?").Build(3);
        var compiled = f.Compile("ctx.l ?? ['a', 'b']");
        var first = compiled.EvaluateScalar(f.Context);
        first.AsStringList()[1] = "mutated";
        Assert.Equal(new[] { "a", "b" }, compiled.EvaluateScalar(f.Context).AsStringList());

        // A value the caller supplied is the caller's own array: returned as is, without a copy.
        var supplied = new[] { "x" };
        f.SetCtx("ctx.l", ScalarValue.FromStringList(supplied));
        Assert.Same(supplied, compiled.EvaluateScalar(f.Context).AsStringList());
    }
}
