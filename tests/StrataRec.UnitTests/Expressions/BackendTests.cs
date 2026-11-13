using System.Numerics.Tensors;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Expressions.Vectorized;

namespace StrataRec.UnitTests.Expressions;

/// <summary>The vectorized backend: row domains, reads, destinations, allocation and thread safety (design 7.7).</summary>
public class BackendTests
{
    private const double Sentinel = 12345.0;

    private static ExprFixture Scored(int rows, int seed = 1)
    {
        var f = new ExprFixture(capacity: rows + 8)
            .Col("col.x", "float32?")
            .Col("col.y", "float64")
            .Col("col.k", "int32")
            .Col("col.s", "string")
            .Param("param.w", "float64", ScalarValue.FromFloat64(0.75))
            .Build(rows);
        var random = new Random(seed);
        var x = f.Scalar<float>("col.x");
        var y = f.Scalar<double>("col.y");
        var k = f.Scalar<int>("col.k");
        var s = f.Buffer<DictStringColumn>("col.s");
        for (var r = 0; r < rows; r++)
        {
            if (random.Next(5) == 0)
            {
                x.SetMissing(r);
            }
            else
            {
                x.Set(r, (float)((random.NextDouble() * 20) - 10));
            }

            y.Set(r, random.NextDouble() * 100);
            k.Set(r, random.Next(-50, 50));
            s.Set(r, random.Next(3) switch { 0 => "a", 1 => "b", _ => "c" });
        }

        return f;
    }

    private const string Formula = "let v = (col.x ?? -1.0) * param.w + log1p(abs(col.y)); col.s == 'b' ? v * exp(-col.y / 50.0) : v + col.k % 7";

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.9)]
    [InlineData(0.5)]
    [InlineData(0.49)]
    [InlineData(0.1)]
    [InlineData(0.01)]
    public void FullRangeAndGather_WriteOnlyListedRows(double liveRatio)
    {
        var f = Scored(200);
        var random = new Random(7);
        var live = Enumerable.Range(0, 200).Where(_ => random.NextDouble() < liveRatio).OrderBy(_ => random.Next()).ToArray();
        if (live.Length == 0)
        {
            live = new[] { 3 };
        }

        // Dead rows hold garbage: NaN, infinities, extreme ints, missing.
        foreach (var dead in Enumerable.Range(0, 200).Except(live))
        {
            f.Scalar<float>("col.x").Set(dead, float.NaN);
            f.Scalar<double>("col.y").Set(dead, dead % 2 == 0 ? double.NegativeInfinity : double.MaxValue);
            f.Scalar<int>("col.k").Set(dead, int.MinValue);
        }

        f.Table.Rows.Replace(live, allowDrop: true);
        var compiled = f.Compile(Formula);
        var destination = (ScalarColumn<double>)ColumnBuffer.Create(ColumnType.Parse("float64?"), f.Capacity, f.Memory);
        destination.Span.Fill(Sentinel);
        destination.EnsureValidity();

        compiled.Evaluate(f.Context, f.Table.Rows.Order, destination);

        var listed = live.ToHashSet();
        for (var r = 0; r < 200; r++)
        {
            if (listed.Contains(r))
            {
                ExprAssert.Same(ExprFixture.Read(destination, r).AsFloat64(), f.Reference(compiled, r), $"row {r}");
                Assert.True(destination.IsValid(r));
            }
            else
            {
                Assert.Equal(Sentinel, destination[r]);
            }
        }
    }

    [Fact]
    public void ResultsAreIdentical_AcrossRowDomains()
    {
        var f = Scored(300, seed: 3);
        var compiled = f.Compile(Formula);
        var all = ColumnBuffer.Create(ColumnType.Float64, f.Capacity, f.Memory);
        compiled.Evaluate(f.Context, f.Table.Rows.Order, all);
        var one = ColumnBuffer.Create(ColumnType.Float64, f.Capacity, f.Memory);
        for (var r = 0; r < 300; r += 7)
        {
            compiled.Evaluate(f.Context, new[] { r, (r + 150) % 300 }, one);
            Assert.Equal(((ScalarColumn<double>)all)[r], ((ScalarColumn<double>)one)[r]);
            Assert.Equal(((ScalarColumn<double>)all)[(r + 150) % 300], ((ScalarColumn<double>)one)[(r + 150) % 300]);
        }
    }

    [Fact]
    public void Destinations_NarrowAndSetValidity()
    {
        var f = new ExprFixture().Col("col.x", "float64?").Col("col.i", "int64").Build(4);
        f.Fill<double>("col.x", 0.1, null, 1e300, -2.5);
        f.Fill<long>("col.i", 1, (long)int.MaxValue + 2, -3, 4);

        var f32 = (ScalarColumn<float>)f.EvaluateAll(f.Compile("col.x"), "float32?");
        Assert.Equal(0.1f, f32[0]);
        Assert.False(f32.IsValid(1));
        Assert.Equal(float.PositiveInfinity, f32[2]);

        var i32 = (ScalarColumn<int>)f.EvaluateAll(f.Compile("col.i"), "int32");
        Assert.Equal(new[] { 1, int.MinValue + 1, -3, 4 }, i32.Span[..4].ToArray());

        var asFloat = (ScalarColumn<double>)f.EvaluateAll(f.Compile("col.i * 2"), "float64");
        Assert.Equal(new[] { 2.0, 2.0 * ((long)int.MaxValue + 2), -6.0, 8.0 }, asFloat.Span[..4].ToArray());

        var asFloat32 = (ScalarColumn<float>)f.EvaluateAll(f.Compile("col.i"), "float32");
        Assert.Equal(-3f, asFloat32[2]);

        var b = (ScalarColumn<bool>)f.EvaluateAll(f.Compile("col.x > 0.0"), "bool?");
        Assert.True(b[0]);
        Assert.False(b.IsValid(1));
        Assert.False(b[3]);

        var i64 = (ScalarColumn<long>)f.EvaluateAll(f.Compile("col.i % 3"), "int64");
        Assert.Equal(new long[] { 1, ((long)int.MaxValue + 2) % 3, 0, 1 }, i64.Span[..4].ToArray());

        // A previously missing row becomes present again when re-evaluated with a present result.
        var reuse = (ScalarColumn<double>)ColumnBuffer.Create(ColumnType.Parse("float64?"), f.Capacity, f.Memory);
        reuse.SetMissing(0);
        f.Compile("col.x ?? 0.0").Evaluate(f.Context, f.Table.Rows.Order, reuse);
        Assert.True(reuse.IsValid(0));
        Assert.True(reuse.IsValid(1));
        Assert.Equal(0.0, reuse[1]);
    }

    [Fact]
    public void Destination_KindMismatch_And_BadRows_Throw()
    {
        var f = new ExprFixture().Col("col.x", "float64").Build(4);
        var compiled = f.Compile("col.x");
        Assert.Throws<ArgumentException>(() => f.EvaluateAll(compiled, "int64"));
        Assert.Throws<ArgumentException>(() => f.EvaluateAll(compiled, "bool"));
        Assert.Throws<ArgumentException>(() => f.EvaluateAll(f.Compile("col.x > 1.0"), "float64"));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.EvaluateAll(compiled, "float64", new[] { 0, 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.EvaluateAll(compiled, "float64", new[] { -1 }));
        var noTable = new EvalContext(null, null, Array.Empty<ScalarValue>(), Array.Empty<ScalarValue>());
        Assert.Throws<InvalidOperationException>(() => compiled.Evaluate(noTable, new[] { 0 }, ColumnBuffer.Create(ColumnType.Float64, 4, f.Memory)));
    }

    [Fact]
    public void RequestLevelExpressions_Broadcast()
    {
        var f = new ExprFixture().Param("param.w", "float64", ScalarValue.FromFloat64(1.5)).Ctx("ctx.m", "float64?").Ctx("ctx.s", "string", ScalarValue.FromString("x")).Build(4);
        var compiled = f.Compile("param.w * 2.0");
        Assert.False(compiled.IsRowDependent);
        var d = (ScalarColumn<float>)ColumnBuffer.Create(ColumnType.Float32, f.Capacity, f.Memory);
        compiled.Evaluate(f.Context, new[] { 1, 3 }, d);
        Assert.Equal(new[] { 0f, 3f, 0f, 3f }, d.Span[..4].ToArray());

        var missing = (ScalarColumn<double>)ColumnBuffer.Create(ColumnType.Parse("float64?"), f.Capacity, f.Memory);
        f.Compile("ctx.m + 1.0").Evaluate(f.Context, new[] { 0, 2 }, missing);
        Assert.False(missing.IsValid(0));
        Assert.True(missing.IsValid(1));
        Assert.False(missing.IsValid(2));

        var s = (DictStringColumn)ColumnBuffer.Create(ColumnType.String, f.Capacity, f.Memory);
        f.Compile("ctx.s").Evaluate(f.Context, new[] { 2 }, s);
        Assert.Equal("x", s.Get(2));

        // Works without a table, too.
        Assert.Equal(3.0, compiled.EvaluateScalar(f.RequestContext).AsFloat64());
    }

    [Fact]
    public void VersionReference_ReadsTheFixedSlot()
    {
        var f = new ExprFixture().Col("col.score.final", "float64").Version("col.score.final@fusion", "float64").Build(3);
        f.Fill<double>("col.score.final", 10, 20, 30);
        f.Fill<double>("col.score.final@fusion", 1, 2, 3);
        ExprAssert.Rows(f, "col.score.final - col.score.final@fusion", 9.0, 18.0, 27.0);
        Assert.Equal(2, f.Compile("col.score.final@fusion + col.score.final").Reads.Count);
        Assert.Equal(f.SlotOf("col.score.final@fusion"), f.Compile("col.score.final@fusion").Reads[0].FixedSlot);
    }

    [Fact]
    public void MissingColumns_ReadAsMissing_OrThrowWhenDeclaredNonNullable()
    {
        var f = new ExprFixture().Col("col.opt", "float64?").Col("col.req", "float64").Col("col.text", "text?").Col("col.list", "list<int64>?").Build(4);

        // Never written (no buffer).
        ExprAssert.Rows(f, "col.opt + 1.0", null, null, null, null);
        ExprAssert.Rows(f, "(col.opt ?? 2.0) + 1.0", 3.0, 3.0, 3.0, 3.0);
        ExprAssert.Rows(f, "size(col.text) ?? -1", -1L, -1L, -1L, -1L);
        ExprAssert.Rows(f, "size(col.list ?? [7])", 1L, 1L, 1L, 1L);
        var e = Assert.Throws<InvalidOperationException>(() => f.EvaluateAll(f.Compile("col.req * 2.0"), "float64"));
        Assert.Contains("col.req", e.Message);
        Assert.Throws<InvalidOperationException>(() => f.Reference(f.Compile("col.req"), 0));

        // Unresolved through the bindings (node skipped / not produced).
        f.Fill<double>("col.opt", 1, 2, 3, 4);
        ExprAssert.Rows(f, "col.opt", 1.0, 2.0, 3.0, 4.0);
        f.Bindings.Commit(1, -1, -1);
        ExprAssert.Rows(f, "col.opt", null, null, null, null);
    }

    [Fact]
    public void RequestValues_AreConverted_OrRejectedWhenMissing()
    {
        var f = new ExprFixture().Param("param.f", "float64", ScalarValue.FromInt64(3)).Param("param.req", "float64").Build(3);
        ExprAssert.Scalar(f, "param.f / 2.0", 1.5);
        var e = Assert.Throws<InvalidOperationException>(() => f.Scalar("param.req + 1.0"));
        Assert.Contains("param.req", e.Message);
    }

    [Fact]
    public void Int32AndFloat32_ArePromoted()
    {
        var f = new ExprFixture().Col("col.i", "int32").Col("col.x", "float32").Build(3);
        f.Fill<int>("col.i", int.MaxValue, int.MinValue, 7);
        f.Fill<float>("col.x", 0.1f, -0f, float.NaN);
        ExprAssert.Rows(f, "col.i + 1", (long)int.MaxValue + 1, (long)int.MinValue + 1, 8L);
        ExprAssert.Rows(f, "col.i * 3000000000", (long)int.MaxValue * 3000000000, (long)int.MinValue * 3000000000, 21000000000L);
        ExprAssert.Rows(f, "col.x * 1.0", (double)0.1f, -0.0, double.NaN);
        ExprAssert.Rows(f, "col.x == 0.1", false, false, false);
        ExprAssert.Rows(f, "col.x == col.x", true, true, false);
    }

    [Fact]
    public void Kernels_HandleRowCountsAroundWordAndVectorBoundaries()
    {
        foreach (var rows in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 63, 64, 65, 127, 128, 129, 200 })
        {
            var f = Scored(rows, seed: rows);
            var compiled = f.Compile("(col.x ?? 0.0) > 0.0 && col.k < 10 ? exp(col.y / 100.0) : (col.s in ['a', 'c'] ? 1.0 : -1.0)");
            var d = f.EvaluateAll(compiled, "float64");
            for (var r = 0; r < rows; r++)
            {
                ExprAssert.Same(ExprFixture.Read(d, r).AsFloat64(), f.Reference(compiled, r), $"{rows} rows, row {r}");
            }
        }
    }

    [Fact]
    public void Evaluate_DoesNotAllocate_InSteadyState()
    {
        var f = CompilerTests.HomeFeed();
        var rows = 8;
        for (var r = 0; r < rows; r++)
        {
            f.Scalar<double>("col.score.p_click").Set(r, 0.1 * r);
            f.Scalar<double>("col.score.p_like").Set(r, 0.2 * r);
            f.Scalar<float>("col.creator.follower_count").Set(r, 500 * r);
            f.Scalar<float>("col.item.age_hours").Set(r, 10 * r);
            f.Scalar<float>("col.item.report_rate").Set(r, 0.01f * r);
            f.Scalar<bool>("col.item.is_policy_safe").Set(r, r % 2 == 0);
            f.Buffer<DictStringColumn>("col.recall.primary_source").Set(r, r % 3 == 0 ? "two_tower" : "cf");
        }

        var expressions = new[]
        {
            CompilerTests.FusionFormula,
            "param.explore_eps * hash01(ctx.request.seed, col.entity_id)",
            "!contains(ctx.request.recent_item_ids, col.entity_id) && (col.item.is_policy_safe ?? false)",
            "col.recall.primary_source == 'two_tower' || col.recall.primary_source in ['x', 'cf']",
            "col.item.report_rate > 0.05 ? 0.2 : 1.0",
            "piecewise_linear(col.item.age_hours ?? 0.0, [0.0, 24.0], [1.0, 0.5])",
        };

        foreach (var source in expressions)
        {
            var compiled = f.Compile(source);
            var destination = ColumnBuffer.Create(compiled.ResultType.AsNullable().Kind == StorageKind.Bool ? ColumnType.Bool : ColumnType.Float64, f.Capacity, f.Memory);
            var some = new[] { 1, 6 };
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

    [Fact]
    public void CompiledExpressions_AreThreadSafe()
    {
        var compiled = Scored(10).Compile(Formula);
        var expected = new double[64][];
        var actual = new double[64][];
        Parallel.For(0, 64, i =>
        {
            var f = Scored(500, seed: i);
            var rows = i % 2 == 0 ? f.Table.Rows.Order.ToArray() : Enumerable.Range(0, 500).Where(r => r % 5 == 0).ToArray();
            var d = (ScalarColumn<double>)ColumnBuffer.Create(ColumnType.Float64, f.Capacity, f.Memory);
            for (var round = 0; round < 20; round++)
            {
                compiled.Evaluate(f.Context, rows, d);
            }

            actual[i] = rows.Select(r => d[r]).ToArray();
            expected[i] = rows.Select(r => f.Reference(compiled, r).AsFloat64()).ToArray();
        });

        for (var i = 0; i < 64; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void Registers_AreReused()
    {
        var f = Scored(4);
        var program = ((VectorizedExpression)f.Compile(Formula)).Program!;
        Assert.True(program.RegisterCount < program.Instructions.Count, $"{program.RegisterCount} registers for {program.Instructions.Count} instructions:\n{program}");
    }

    [Fact]
    public void TensorPrimitives_MinMax_MatchMath_OnSpecialValues()
    {
        // The vectorized backend uses TensorPrimitives.Min/Max; constant folding and the reference use Math.Min/Max.
        double[] specials = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -0.0, 1.0, -1.0, double.Epsilon };
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var a in specials)
        {
            foreach (var b in specials)
            {
                xs.Add(a);
                ys.Add(b);
            }
        }

        var min = new double[xs.Count];
        var max = new double[xs.Count];
        TensorPrimitives.Min<double>(xs.ToArray(), ys.ToArray(), min);
        TensorPrimitives.Max<double>(xs.ToArray(), ys.ToArray(), max);
        for (var i = 0; i < xs.Count; i++)
        {
            Assert.True(double.IsNaN(Math.Min(xs[i], ys[i])) == double.IsNaN(min[i]) && (double.IsNaN(min[i]) || BitConverter.DoubleToInt64Bits(Math.Min(xs[i], ys[i])) == BitConverter.DoubleToInt64Bits(min[i])), $"min({xs[i]}, {ys[i]})");
            Assert.True(double.IsNaN(Math.Max(xs[i], ys[i])) == double.IsNaN(max[i]) && (double.IsNaN(max[i]) || BitConverter.DoubleToInt64Bits(Math.Max(xs[i], ys[i])) == BitConverter.DoubleToInt64Bits(max[i])), $"max({xs[i]}, {ys[i]})");
        }
    }
}
