using System.Globalization;
using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;

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
        ListFloat,
        ListBool,
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
        ("col.t1", "string?", K.Text), // a string column of longer, non-ASCII values
        ("col.v1", "list<float32,4>?", K.Vec),
        ("col.v2", "list<float32,4>", K.Vec),
        ("col.v3", "list<float64,4>?", K.Vec),
        ("col.li", "list<int64>?", K.ListInt),
        ("col.li32", "list<int32>?", K.ListInt),
        ("col.lj32", "list<int32>", K.ListInt),
        ("col.ls", "list<string>", K.ListStr),
        ("col.lf", "list<float64>?", K.ListFloat),
        ("col.lf32", "list<float32>", K.ListFloat),
        ("col.lb", "list<bool>?", K.ListBool),
    };

    private static readonly (string Name, string Type, K Kind)[] RequestValues =
    {
        ("ctx.f", "float64?", K.Float),
        ("ctx.g", "float64", K.Float),
        ("ctx.i", "int64?", K.Int),
        ("ctx.j", "int64", K.Int),
        ("ctx.b", "bool?", K.Bool),
        ("ctx.s", "string?", K.Str),
        ("ctx.v", "list<float32,4>?", K.Vec),
        ("ctx.v64", "list<float64,4>", K.Vec),
        ("ctx.li", "list<int64>", K.ListInt),
        ("ctx.li32", "list<int32>?", K.ListInt),
        ("ctx.ls", "list<string>?", K.ListStr),
        ("ctx.lf", "list<float32>?", K.ListFloat),
        ("ctx.lb", "list<bool>", K.ListBool),
        ("ctx.t", "string?", K.Text),
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
                _ => random.Next(5) switch { 0 => K.Vec, 1 => K.ListInt, 2 => K.ListStr, 3 => K.ListFloat, _ => K.ListBool },
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
                // An int value broadcast into int32 storage must be missing on every row when it does not fit.
                var narrow = kind == K.Int && random.Next(2) == 0;
                var d = ColumnBuffer.Create(narrow ? ColumnType.Int32.AsNullable() : expression.ResultType.AsNullable(), f.Capacity, f.Memory);
                expression.Evaluate(f.Context, f.Table.Rows.Order, d);
                if (narrow && !ColumnValues.FitsStorage(expected, ColumnType.Int32))
                {
                    expected = ScalarValue.Missing;
                }

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

        // Int and int-list results sometimes go to int32 storage: values outside the int32 range (int results reach
        // ±2^63) must then be missing, never wrapped (design 4.3). Float lists sometimes go to float32 storage, which
        // rounds each element as a float32 column does.
        var resultType = expression.ResultType.AsNullable();
        var narrow = (resultType.Kind == StorageKind.Int64 || resultType == ColumnType.List(StorageKind.Int64, nullable: true)) && random.Next(3) == 0;
        var single = resultType == ColumnType.List(StorageKind.Float64, nullable: true) && random.Next(3) == 0;
        var destinationType = single ? ColumnType.List(StorageKind.Float32, nullable: true)
            : !narrow ? resultType : resultType.Kind == StorageKind.Int64 ? ColumnType.Int32.AsNullable() : ColumnType.List(StorageKind.Int32).AsNullable();
        var destination = ColumnBuffer.Create(destinationType, f.Capacity, f.Memory);
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

            if (narrow && !ColumnValues.FitsStorage(expected, destinationType))
            {
                expected = ScalarValue.Missing;
            }

            if (single && !expected.IsMissing)
            {
                expected = ScalarValue.FromFloat64List(expected.AsFloat64List().ToArray().Select(x => (double)(float)x).ToArray());
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

        if (expected.Kind == StorageKind.String)
        {
            return expected.AsString() == actual.AsString();
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
            case StringColumn c when kind == K.Text: c.Set(row, Texts[random.Next(Texts.Length)]); break;
            case StringColumn c: c.Set(row, Strings[random.Next(Strings.Length)]); break;
            // A fixed-length column holds rows of its length, as its writers keep it (design 4.3).
            case ListColumn<float> c when kind == K.Vec: c.Set(row, RandomVector(random, garbage)); break;
            case ListColumn<double> c when kind == K.Vec: c.Set(row, RandomVector(random, garbage).Select(v => (double)v).ToArray()); break;
            case ListColumn<long> c: c.Set(row, RandomLongs(random)); break;
            case ListColumn<int> c: c.Set(row, RandomLongs(random).Select(v => (int)v).ToArray()); break;
            case ListColumn<string> c: c.Set(row, RandomStrings(random)); break;
            case ListColumn<double> c: c.Set(row, RandomDoubles(random)); break;
            case ListColumn<float> c: c.Set(row, RandomDoubles(random).Select(v => (float)v).ToArray()); break;
            case ListColumn<bool> c: c.Set(row, RandomBools(random)); break;
        }

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

    /// <summary>Few distinct values, so membership and overlap find matches; integral ones meet int probes.</summary>
    private static double[] RandomDoubles(Random random)
    {
        var v = new double[random.Next(5)];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = random.Next(-2, 6) * 0.5;
        }

        return v;
    }

    private static bool[] RandomBools(Random random)
    {
        var v = new bool[random.Next(4)];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = random.Next(2) == 0;
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
        K.Text => ScalarValue.FromString(Texts[random.Next(Texts.Length)]),
        K.Vec => ScalarValue.FromFloat32List(RandomVector(random, garbage: false)),
        K.ListInt => ScalarValue.FromInt64List(RandomLongs(random)),
        K.ListFloat => ScalarValue.FromFloat64List(RandomDoubles(random)),
        K.ListBool => ScalarValue.FromBoolList(RandomBools(random)),
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
                    return random.Next(31) switch
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
                        20 => $"dot({VecArg(d)}, {VecArg(d)})",
                        21 => $"cosine({VecArg(d)}, {VecArg(d)})",
                        22 => $"l2_norm({VecArg(d)})",
                        23 => $"hash01({Gen(K.Int, d)}, {Gen(K.Int, d)})",
                        24 => $"hash01({Gen(K.Str, d)}, {Gen(K.Float, d)}, {Gen(K.Bool, d)}, {Gen(K.Int, d)})",
                        25 => $"({Gen(K.Bool, d)} ? {Num(d)} : {Num(d)})",
                        26 => $"({Gen(K.Float, d)} ?? {Num(d)})",
                        27 => $"lookup('tbl', {Gen(K.Int, d)}, ({Num(d)} ?? 0.5))",
                        28 => Let(K.Float, d),
                        29 => $"double({Num(d)})",
                        _ => $"({Gen(K.Float, d)} * 1.0 + {Gen(K.Int, d)} / 2)",
                    };
                case K.Int:
                    return random.Next(16) switch
                    {
                        13 => $"int({Num(d)})",
                        14 => $"(int({Gen(K.Float, d)}) ?? {Gen(K.Int, d)})",
                        0 => $"({Gen(K.Int, d)} + {Gen(K.Int, d)})",
                        1 => $"({Gen(K.Int, d)} - {Gen(K.Int, d)})",
                        2 => $"({Gen(K.Int, d)} * {Gen(K.Int, d)})",
                        3 => $"({Gen(K.Int, d)} % {Divisor(Gen(K.Int, d))})",
                        4 => $"-{Gen(K.Int, d)}",
                        5 => $"abs({Gen(K.Int, d)})",
                        6 => $"min({Gen(K.Int, d)}, {Gen(K.Int, d)})",
                        7 => $"max({Gen(K.Int, d)}, {Gen(K.Int, d)}, {Gen(K.Int, d)})",
                        8 => $"size({Gen(Pick(K.ListInt, K.ListStr, K.ListFloat, K.ListBool), d)})",
                        9 => $"size({Gen(K.Text, d)})",
                        10 => Overlap(d),
                        11 => $"({Gen(K.Bool, d)} ? {Gen(K.Int, d)} : {Gen(K.Int, d)})",
                        12 => $"({Gen(K.Int, d)} ?? {Gen(K.Int, d)})",
                        _ => Let(K.Int, d),
                    };
                case K.Bool:
                    return random.Next(22) switch
                    {
                        20 => $"({Num(d)} in {Gen(K.ListFloat, d)})",
                        21 => $"contains({Gen(K.ListBool, d)}, {Gen(K.Bool, d)})",
                        0 => $"({Num(d)} {Pick("<", "<=", ">", ">=", "==", "!=")} {Num(d)})",
                        1 => $"({Gen(K.Int, d)} {Pick("<", "<=", ">", ">=", "==", "!=")} {Gen(K.Int, d)})",
                        2 => $"({Gen(K.Str, d)} {Pick("==", "!=")} {Gen(K.Str, d)})",
                        3 => $"({Gen(K.Str, d)} in ['a', 'zz'])",
                        4 => $"{Gen(K.Str, d)}.{Pick("startsWith", "endsWith")}({Gen(K.Str, d)})",
                        5 => $"({Gen(K.Int, d)} in [1, 2, -1])",
                        6 => $"({Num(d)} in [0.5, 1.0, 0])",
                        7 => $"contains({Gen(K.ListInt, d)}, {Gen(K.Int, d)})",
                        8 => $"({Gen(K.Str, d)} in {Gen(K.ListStr, d)})",
                        9 => $"contains({Gen(K.Text, d)}, {Gen(K.Str, d)})",
                        10 => $"has({Gen((K)random.Next(10), d)})",
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
                case K.Vec or K.ListInt or K.ListStr or K.ListFloat or K.ListBool:
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

        private string Overlap(int depth)
        {
            var list = Pick(K.ListInt, K.ListStr, K.ListFloat, K.ListBool);
            return $"overlap_count({Gen(list, depth)}, {Gen(list, depth)})";
        }

        private string Num(int depth) => Gen(random.Next(3) == 0 ? K.Int : K.Float, depth);

        /// <summary>
        /// An argument of dot, cosine or l2_norm: mostly a <c>list&lt;float32,4&gt;</c> or <c>list&lt;float64,4&gt;</c>, mixed;
        /// sometimes a float list of any length (its rows of another length than 4 give missing, never NaN) or a literal
        /// of 4. Never a literal of another length: two declared lengths that differ do not compile.
        /// </summary>
        private string VecArg(int depth) => random.Next(6) switch
        {
            0 => rowLevel ? Pick("col.lf", "col.lf32", "ctx.lf") : "ctx.lf",
            1 => "[1.0, 0.5, -1.0, 2]",
            _ => Gen(K.Vec, depth),
        };

        /// <summary>
    /// A divisor the binder knows is 0 (a literal, <c>int(0)</c>, a let of one) is a compile error (design 7.5); a 0 that
    /// only folding or the data finds gives missing. So a divisor that is not a non-zero literal is written as
    /// <c>(d + 0)</c>, the same value but never a constant to the binder (review 66: <c>% int(0)</c> stopped seed 88), and
    /// a non-zero literal stays, for the non-nullable path.
    /// </summary>
    private static string Divisor(string divisor) =>
        long.TryParse(divisor.Trim('(', ')', ' '), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value != 0
            ? divisor
            : $"({divisor} + 0)";

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

        private K Pick(params K[] options) => options[random.Next(options.Length)];

        /// <summary>Both are strings, drawn from different value sets, so either stands where the other is wanted.</summary>
        private static bool Reads(K value, K wanted) => value == wanted || (value, wanted) is (K.Text, K.Str) or (K.Str, K.Text);

        private string Leaf(K kind)
        {
            var candidates = new List<string>();
            if (rowLevel)
            {
                candidates.AddRange(Columns.Where(c => Reads(c.Kind, kind)).Select(c => c.Name));
                if (kind == K.Int)
                {
                    candidates.Add("col.entity_id");
                }
            }

            candidates.AddRange(RequestValues.Where(c => Reads(c.Kind, kind)).Select(c => c.Name));
            var literal = kind switch
            {
                K.Float => Pick("0.0", "1.0", "-2.5", "0.5", "1e-3", "100.0", "3.0", "-0.0"),
                K.Int => Pick("0", "1", "-1", "7", "3", "9223372036854775807", "-9223372036854775808"),
                K.Bool => Pick("true", "false"),
                K.Str => Pick("'a'", "'b'", "''", "'zz'"),
                K.ListInt => "[1, 2, 7]",
                K.ListStr => "['a', 'zz']",
                // An integer element of a float list literal is a float; a list of integers only is list<int64> wherever it
                // stands (in the list of 'x in [2, 1]', say), so it is not a float list leaf (seeds 60, 96 of 100).
                K.ListFloat => Pick("[0.5, 1.0, -1.0]", "[2, 0.5]", "[2, 1.0]"),
                K.ListBool => "[true]",
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
