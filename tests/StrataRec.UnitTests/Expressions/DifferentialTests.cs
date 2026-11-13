using System.Globalization;
using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using Xunit.Abstractions;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// Differential tests (design 7.7): random well-typed expressions over random tables — missing values, NaN, ±∞,
/// extreme integers, garbage in dead rows, varying live ratios — must give the same result on the vectorized backend
/// (optimized IR) as on the row-at-a-time reference interpreter (unoptimized IR) for every listed row.
/// </summary>
public class DifferentialTests(ITestOutputHelper output)
{
    private enum K
    {
        Float,
        Int,
        Bool,
        Str,
        Text,
        Vec,
        ListInt,
        ListStr,
    }

    private static readonly (string Name, string Type, K Kind)[] Columns =
    {
        ("col.f1", "float64", K.Float),
        ("col.f2", "float64?", K.Float),
        ("col.f3", "float32?", K.Float),
        ("col.f4", "float32", K.Float),
        ("col.i1", "int64", K.Int),
        ("col.i2", "int64?", K.Int),
        ("col.i3", "int32?", K.Int),
        ("col.i4", "int32", K.Int),
        ("col.b1", "bool", K.Bool),
        ("col.b2", "bool?", K.Bool),
        ("col.s1", "string", K.Str),
        ("col.s2", "string?", K.Str),
        ("col.t1", "text?", K.Text),
        ("col.v1", "vec<float32,4>?", K.Vec),
        ("col.v2", "vec<float32,4>", K.Vec),
        ("col.li", "list<int64>?", K.ListInt),
        ("col.ls", "list<string>", K.ListStr),
    };

    private static readonly (string Name, string Type, K Kind)[] RequestValues =
    {
        ("ctx.f", "float64?", K.Float),
        ("ctx.g", "float64", K.Float),
        ("ctx.i", "int64?", K.Int),
        ("ctx.j", "int64", K.Int),
        ("ctx.b", "bool?", K.Bool),
        ("ctx.s", "string?", K.Str),
        ("ctx.v", "vec<float32,4>?", K.Vec),
        ("ctx.li", "list<int64>", K.ListInt),
        ("ctx.ls", "list<string>?", K.ListStr),
        ("ctx.t", "text?", K.Text),
        ("param.w", "float64", K.Float),
        ("param.k", "int64", K.Int),
    };

    private static readonly double[] SpecialFloats = { 0.0, -0.0, 1.0, -1.0, 0.5, 2.0, 1e-300, 1e300, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
    private static readonly long[] SpecialInts = { 0, 1, -1, 2, 7, long.MaxValue, long.MinValue };
    private static readonly string[] Strings = { "a", "b", "zz", string.Empty, "ab" };
    private static readonly string[] Texts = { "hello", "ab", string.Empty, "😀a", "xxabyy" };

    /// <summary>Seeds 1..4 by default; set <c>STRATAREC_DIFF_SEEDS=n</c> to soak-test seeds 1..n.</summary>
    public static TheoryData<int> Seeds()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("STRATAREC_DIFF_SEEDS"), out var n) ? n : 4;
        var data = new TheoryData<int>();
        for (var seed = 1; seed <= count; seed++)
        {
            data.Add(seed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Vectorized_Equals_Reference_OnRandomExpressions(int seed)
    {
        var random = new Random(seed);
        var failures = new List<string>();
        var compiled = 0;
        for (var e = 0; e < 100; e++)
        {
            var kind = random.Next(10) switch
            {
                < 4 => K.Float,
                < 6 => K.Int,
                < 8 => K.Bool,
                8 => K.Str,
                _ => random.Next(3) switch { 0 => K.Vec, 1 => K.ListInt, _ => K.ListStr },
            };
            var source = new Generator(random, rowLevel: true).Gen(kind, 5);
            for (var t = 0; t < 2; t++)
            {
                var f = RandomTable(random);
                CompiledExpression expression;
                try
                {
                    expression = f.Compile(source);
                }
                catch (ExpressionCompileException ex)
                {
                    failures.Add($"[compile] {source}: {ex.Message}");
                    break;
                }

                compiled++;
                if (t == 0 && e % 10 == 0)
                {
                    var program = ((StrataRec.Expressions.Vectorized.VectorizedExpression)expression).Program;
                    output.WriteLine($"{source}\n    => {expression.Describe().Replace("\n", " ", StringComparison.Ordinal)}  [{program?.Instructions.Count ?? 0} instr, {program?.HoistedCount ?? 0} hoisted]");
                }

                Compare(f, expression, random, failures);
            }
        }

        output.WriteLine($"seed {seed}: {compiled} evaluations, {failures.Count} failures");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EvaluateScalar_Equals_Reference_OnRandomRequestLevelExpressions(int seed)
    {
        var random = new Random(1000 + seed);
        var failures = new List<string>();
        for (var e = 0; e < 100; e++)
        {
            var kind = (K)random.Next(4);
            var source = new Generator(random, rowLevel: false).Gen(kind, 5);
            var f = RandomTable(random);
            var expression = f.Compile(source);
            Assert.False(expression.IsRowDependent, source);
            var actual = expression.EvaluateScalar(f.Context);
            var expected = f.Reference(expression, 0);
            if (!Same(expected, actual))
            {
                failures.Add($"{source}: expected {expected}, got {actual}");
            }

            // Broadcast through Evaluate as well.
            if (kind is K.Float or K.Int or K.Bool)
            {
                var d = ColumnBuffer.Create(expression.ResultType.AsNullable(), f.Capacity, f.Memory);
                expression.Evaluate(f.Context, f.Table.Rows.Order, d);
                foreach (var r in f.Table.Rows.Order)
                {
                    if (!Same(expected, ExprFixture.Read(d, r)))
                    {
                        failures.Add($"{source} broadcast @row {r}: expected {expected}, got {ExprFixture.Read(d, r)}");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    private static void Compare(ExprFixture f, CompiledExpression expression, Random random, List<string> failures)
    {
        var live = f.Table.Rows.Order.ToArray();
        var rows = random.Next(4) == 0 ? live.Where(_ => random.Next(3) == 0).ToArray() : live;
        if (rows.Length == 0)
        {
            rows = live.Take(1).ToArray();
        }

        var destination = ColumnBuffer.Create(expression.ResultType.AsNullable(), f.Capacity, f.Memory);
        try
        {
            expression.Evaluate(f.Context, rows, destination);
        }
        catch (Exception ex)
        {
            failures.Add($"[evaluate] {expression.Source}: {ex}");
            return;
        }

        foreach (var r in rows)
        {
            ScalarValue expected;
            try
            {
                expected = f.Reference(expression, r);
            }
            catch (Exception ex)
            {
                failures.Add($"[reference] {expression.Source} @row {r}: {ex.Message}");
                return;
            }

            var actual = ExprFixture.Read(destination, r);
            if (!Same(expected, actual))
            {
                failures.Add($"{expression.Source}\n  @row {r} of {f.Table.PhysicalRowCount} ({rows.Length} evaluated): expected {Show(expected)}, got {Show(actual)}\n  optimized: {expression.Describe().Replace("\n", " ", StringComparison.Ordinal)}");
                return;
            }
        }
    }

    private static string Show(ScalarValue v) =>
        v.Kind == StorageKind.Float64 ? v.AsFloat64().ToString("R", CultureInfo.InvariantCulture) : v.ToString();

    private static bool Same(ScalarValue expected, ScalarValue actual)
    {
        if (expected.IsMissing || actual.IsMissing)
        {
            return expected.IsMissing && actual.IsMissing;
        }

        if (expected.Kind == StorageKind.Float64)
        {
            double e = expected.AsFloat64(), a = actual.AsFloat64();
            return e.Equals(a) || Math.Abs(e - a) <= 1e-12 * Math.Max(Math.Abs(e), Math.Abs(a));
        }

        if (expected.Kind is StorageKind.String or StorageKind.Text)
        {
            return expected.AsString() == actual.AsString();
        }

        if (expected.Kind == StorageKind.Vector)
        {
            return expected.AsVector().AsSpan().SequenceEqual(actual.AsVector());
        }

        return expected.Equals(actual);
    }

    private static ExprFixture RandomTable(Random random)
    {
        var rows = random.Next(10) switch
        {
            0 => random.Next(3, 9),
            1 => 64,
            2 => 65,
            _ => random.Next(3, 160),
        };
        var f = new ExprFixture(capacity: rows + random.Next(0, 9));
        foreach (var (name, type, _) in Columns)
        {
            f.Col(name, type);
        }

        foreach (var (name, type, kind) in RequestValues)
        {
            var nullable = type.EndsWith('?');
            var value = nullable && random.Next(4) == 0 ? ScalarValue.Missing : RandomValue(random, kind);
            if (name.StartsWith("ctx.", StringComparison.Ordinal))
            {
                f.Ctx(name, type, value);
            }
            else
            {
                f.Param(name, type, value);
            }
        }

        f.Lookup(new TestLookupTable("tbl", ColumnType.Int64, ColumnType.Float64, new()
        {
            [ScalarValue.FromInt64(0)] = ScalarValue.FromFloat64(0.25),
            [ScalarValue.FromInt64(1)] = ScalarValue.FromFloat64(-4),
            [ScalarValue.FromInt64(7)] = ScalarValue.FromFloat64(double.NaN),
        }));
        f.Build(rows);

        var ratio = random.Next(5) switch { 0 => 1.0, 1 => 0.8, 2 => 0.5, 3 => 0.3, _ => 0.05 };
        var live = Enumerable.Range(0, rows).Where(_ => random.NextDouble() < ratio).OrderBy(_ => random.Next()).ToArray();
        if (live.Length == 0)
        {
            live = new[] { random.Next(rows) };
        }

        var alive = live.ToHashSet();
        foreach (var (name, type, kind) in Columns)
        {
            var nullable = type.EndsWith('?');
            if (nullable && random.Next(15) == 0)
            {
                continue; // never written: the whole column reads as missing
            }

            var buffer = f.Table.GetOrCreateSlot(f.SlotOf(name));
            for (var r = 0; r < rows; r++)
            {
                var dead = !alive.Contains(r);
                var missing = (nullable && random.Next(5) == 0) || (dead && random.Next(3) == 0);
                Write(buffer, r, random, kind, garbage: dead);
                if (missing)
                {
                    buffer.SetMissing(r);
                }
            }
        }

        f.Table.Rows.Replace(live, allowDrop: true);
        return f;
    }

    private static void Write(ColumnBuffer buffer, int row, Random random, K kind, bool garbage)
    {
        switch (buffer)
        {
            case ScalarColumn<double> c: c.Set(row, garbage ? Garbage(random) : RandomFloat(random)); break;
            case ScalarColumn<float> c: c.Set(row, (float)(garbage ? Garbage(random) : RandomFloat(random))); break;
            case ScalarColumn<long> c: c.Set(row, garbage ? (random.Next(2) == 0 ? long.MinValue : 0) : RandomInt(random)); break;
            case ScalarColumn<int> c: c.Set(row, garbage ? int.MinValue : (int)Math.Clamp(RandomInt(random), int.MinValue, int.MaxValue)); break;
            case ScalarColumn<bool> c: c.Set(row, random.Next(2) == 0); break;
            case DictStringColumn c: c.Set(row, Strings[random.Next(Strings.Length)]); break;
            case TextColumn c: c.Set(row, Texts[random.Next(Texts.Length)]); break;
            case VectorColumn c: c.Set(row, RandomVector(random, garbage)); break;
            case ListColumn<long> c: c.Set(row, RandomLongs(random)); break;
            case ListColumn<string> c: c.Set(row, RandomStrings(random)); break;
        }

        _ = kind;
    }

    private static double Garbage(Random random) => random.Next(4) switch
    {
        0 => double.NaN,
        1 => double.PositiveInfinity,
        2 => double.MaxValue,
        _ => BitConverter.Int64BitsToDouble(random.NextInt64()),
    };

    private static double RandomFloat(Random random) =>
        random.Next(8) == 0 ? SpecialFloats[random.Next(SpecialFloats.Length)] : Math.Round((random.NextDouble() * 20) - 10, 3);

    private static long RandomInt(Random random) =>
        random.Next(6) == 0 ? SpecialInts[random.Next(SpecialInts.Length)] : random.Next(-20, 20);

    private static float[] RandomVector(Random random, bool garbage)
    {
        var v = new float[4];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = garbage && random.Next(3) == 0 ? float.NaN : random.Next(5) - 2;
        }

        return v;
    }

    private static long[] RandomLongs(Random random)
    {
        var v = new long[random.Next(5)];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = random.Next(-2, 8);
        }

        return v;
    }

    private static string[] RandomStrings(Random random)
    {
        var v = new string[random.Next(4)];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = Strings[random.Next(Strings.Length)];
        }

        return v;
    }

    private static ScalarValue RandomValue(Random random, K kind) => kind switch
    {
        K.Float => ScalarValue.FromFloat64(RandomFloat(random)),
        K.Int => ScalarValue.FromInt64(RandomInt(random)),
        K.Bool => ScalarValue.FromBool(random.Next(2) == 0),
        K.Str => ScalarValue.FromString(Strings[random.Next(Strings.Length)]),
        K.Text => ScalarValue.FromText(Texts[random.Next(Texts.Length)]),
        K.Vec => ScalarValue.FromVector(RandomVector(random, garbage: false)),
        K.ListInt => ScalarValue.FromInt64List(RandomLongs(random)),
        _ => ScalarValue.FromStringList(RandomStrings(random)),
    };

    /// <summary>Generates well-typed ColumnExpr source of a requested kind.</summary>
    private sealed class Generator(Random random, bool rowLevel)
    {
        private int _lets;

        public string Gen(K kind, int depth)
        {
            if (depth <= 0 || random.Next(5) == 0)
            {
                return Leaf(kind);
            }

            var d = depth - 1;
            switch (kind)
            {
                case K.Float:
                    return random.Next(30) switch
                    {
                        0 => $"({Num(d)} + {Gen(K.Float, d)})",
                        1 => $"({Gen(K.Float, d)} - {Num(d)})",
                        2 => $"({Num(d)} * {Num(d)})",
                        3 => $"({Num(d)} / {Num(d)})",
                        4 => $"-{Gen(K.Float, d)}",
                        5 => $"abs({Gen(K.Float, d)})",
                        6 => $"sqrt({Num(d)})",
                        7 => $"exp({Num(d)})",
                        8 => $"log({Num(d)})",
                        9 => $"log1p({Num(d)})",
                        10 => $"sigmoid({Num(d)})",
                        11 => $"logit({Num(d)})",
                        12 => $"pow({Num(d)}, {Num(d)})",
                        13 => $"min({Gen(K.Float, d)}, {Num(d)}, {Num(d)})",
                        14 => $"max({Num(d)}, {Gen(K.Float, d)})",
                        15 => $"clamp({Num(d)}, {Num(d)}, {Num(d)})",
                        16 => $"soft_floor({Num(d)}, {Num(d)})",
                        17 => $"soft_floor({Num(d)}, {Num(d)}, {Num(d)})",
                        18 => $"linear_decay({Num(d)}, {Num(d)}, {Num(d)})",
                        19 => $"piecewise_linear({Num(d)}, [-1.0, 0, 2.5], [3, -1.0, 0.5])",
                        20 => $"dot({Gen(K.Vec, d)}, {Gen(K.Vec, d)})",
                        21 => $"cosine({Gen(K.Vec, d)}, {Gen(K.Vec, d)})",
                        22 => $"l2_norm({Gen(K.Vec, d)})",
                        23 => $"hash01({Gen(K.Int, d)}, {Gen(K.Int, d)})",
                        24 => $"hash01({Gen(K.Str, d)}, {Gen(K.Float, d)}, {Gen(K.Bool, d)}, {Gen(K.Int, d)})",
                        25 => $"({Gen(K.Bool, d)} ? {Num(d)} : {Num(d)})",
                        26 => $"({Gen(K.Float, d)} ?? {Num(d)})",
                        27 => $"lookup('tbl', {Gen(K.Int, d)}, ({Num(d)} ?? 0.5))",
                        28 => Let(K.Float, d),
                        _ => $"({Gen(K.Float, d)} * 1.0 + {Gen(K.Int, d)} / 2)",
                    };
                case K.Int:
                    return random.Next(14) switch
                    {
                        0 => $"({Gen(K.Int, d)} + {Gen(K.Int, d)})",
                        1 => $"({Gen(K.Int, d)} - {Gen(K.Int, d)})",
                        2 => $"({Gen(K.Int, d)} * {Gen(K.Int, d)})",
                        3 => $"({Gen(K.Int, d)} % {Gen(K.Int, d)})",
                        4 => $"-{Gen(K.Int, d)}",
                        5 => $"abs({Gen(K.Int, d)})",
                        6 => $"min({Gen(K.Int, d)}, {Gen(K.Int, d)})",
                        7 => $"max({Gen(K.Int, d)}, {Gen(K.Int, d)}, {Gen(K.Int, d)})",
                        8 => $"size({Gen(random.Next(2) == 0 ? K.ListInt : K.ListStr, d)})",
                        9 => $"size({Gen(K.Text, d)})",
                        10 => random.Next(2) == 0 ? $"overlap_count({Gen(K.ListInt, d)}, {Gen(K.ListInt, d)})" : $"overlap_count({Gen(K.ListStr, d)}, {Gen(K.ListStr, d)})",
                        11 => $"({Gen(K.Bool, d)} ? {Gen(K.Int, d)} : {Gen(K.Int, d)})",
                        12 => $"({Gen(K.Int, d)} ?? {Gen(K.Int, d)})",
                        _ => Let(K.Int, d),
                    };
                case K.Bool:
                    return random.Next(20) switch
                    {
                        0 => $"({Num(d)} {Pick("<", "<=", ">", ">=", "==", "!=")} {Num(d)})",
                        1 => $"({Gen(K.Int, d)} {Pick("<", "<=", ">", ">=", "==", "!=")} {Gen(K.Int, d)})",
                        2 => $"({Gen(K.Str, d)} {Pick("==", "!=")} {Gen(K.Str, d)})",
                        3 => $"({Gen(K.Str, d)} in ['a', 'zz'])",
                        4 => $"{Gen(K.Str, d)}.startsWith({Gen(K.Str, d)})",
                        5 => $"({Gen(K.Int, d)} in [1, 2, -1])",
                        6 => $"({Num(d)} in [0.5, 1.0, 0])",
                        7 => $"contains({Gen(K.ListInt, d)}, {Gen(K.Int, d)})",
                        8 => $"({Gen(K.Str, d)} in {Gen(K.ListStr, d)})",
                        9 => $"contains({Gen(K.Text, d)}, {Gen(K.Str, d)})",
                        10 => $"has({Gen((K)random.Next(8), d)})",
                        11 => $"!{Gen(K.Bool, d)}",
                        12 => $"({Gen(K.Bool, d)} && {Gen(K.Bool, d)})",
                        13 => $"({Gen(K.Bool, d)} || {Gen(K.Bool, d)})",
                        14 => $"({Gen(K.Bool, d)} ? {Gen(K.Bool, d)} : {Gen(K.Bool, d)})",
                        15 => $"({Gen(K.Bool, d)} ?? {Gen(K.Bool, d)})",
                        16 => $"({Gen(K.Bool, d)} == {Gen(K.Bool, d)})",
                        17 => $"({Gen(K.Str, d)} == 'a')",
                        18 => $"({Gen(K.Int, d)} in {Gen(K.ListInt, d)})",
                        _ => Let(K.Bool, d),
                    };
                case K.Str:
                    return random.Next(3) switch
                    {
                        0 => $"({Gen(K.Bool, d)} ? {Gen(K.Str, d)} : {Gen(K.Str, d)})",
                        1 => $"({Gen(K.Str, d)} ?? {Gen(K.Str, d)})",
                        _ => Leaf(K.Str),
                    };
                case K.Vec or K.ListInt or K.ListStr:
                    return random.Next(3) switch
                    {
                        0 => $"({Gen(K.Bool, d)} ? {Gen(kind, d)} : {Gen(kind, d)})",
                        1 => $"({Gen(kind, d)} ?? {Gen(kind, d)})",
                        _ => Leaf(kind),
                    };
                default:
                    return Leaf(kind);
            }
        }

        private string Num(int depth) => Gen(random.Next(3) == 0 ? K.Int : K.Float, depth);

        private string Let(K kind, int depth)
        {
            var name = "v" + (_lets++).ToString(CultureInfo.InvariantCulture);
            var value = Gen(kind, depth);
            return kind switch
            {
                K.Float => $"(let {name} = {value}; {name} * {Num(depth)} + {name})",
                K.Int => $"(let {name} = {value}; {name} - {name} * {Gen(K.Int, depth)})",
                _ => $"(let {name} = {value}; {name} && !{name} || {Gen(K.Bool, depth)})",
            };
        }

        private string Pick(params string[] options) => options[random.Next(options.Length)];

        private string Leaf(K kind)
        {
            var candidates = new List<string>();
            if (rowLevel)
            {
                candidates.AddRange(Columns.Where(c => c.Kind == kind).Select(c => c.Name));
                if (kind == K.Int)
                {
                    candidates.Add("col.entity_id");
                }
            }

            candidates.AddRange(RequestValues.Where(c => c.Kind == kind).Select(c => c.Name));
            var literal = kind switch
            {
                K.Float => Pick("0.0", "1.0", "-2.5", "0.5", "1e-3", "100.0", "3.0", "-0.0"),
                K.Int => Pick("0", "1", "-1", "7", "3", "9223372036854775807", "-9223372036854775808"),
                K.Bool => Pick("true", "false"),
                K.Str => Pick("'a'", "'b'", "''", "'zz'"),
                K.ListInt => "[1, 2, 7]",
                K.ListStr => "['a', 'zz']",
                _ => null,
            };
            if (literal is not null && (candidates.Count == 0 || random.Next(4) == 0))
            {
                return literal;
            }

            var sb = new StringBuilder(candidates[random.Next(candidates.Count)]);
            return sb.ToString();
        }
    }
}
