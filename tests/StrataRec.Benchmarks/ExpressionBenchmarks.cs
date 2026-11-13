using BenchmarkDotNet.Attributes;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;

namespace StrataRec.Benchmarks;

/// <summary>
/// ColumnExpr vectorized backend over 5,000 live candidates (design 7.7 target: a ~20-node formula with two
/// transcendental calls in at most 50 μs, with no steady-state allocation).
/// </summary>
/// <remarks>
/// <para><c>FullRange</c> evaluates every row of a 5,000-row table (live ratio 1.0, zero-copy column reads).
/// <c>Gather</c> evaluates 5,000 live rows scattered over a 20,000-row table (live ratio 0.25), which takes the
/// gather/scatter path. Both write into a preallocated <c>float64</c> destination, except <c>NarrowInt32</c>, which
/// writes an int64 result into an <c>int32</c> column. The <c>List*</c> cases run the list kernels over 1–8 topic ids
/// per candidate, stored as <c>list&lt;int64&gt;</c> or (same values) <c>list&lt;int32&gt;</c>.</para>
/// <para>Run: <c>dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*ExpressionBenchmarks*'</c>.</para>
/// </remarks>
[MemoryDiagnoser]
public class ExpressionBenchmarks
{
    /// <summary>The fusion formula of design 7.3, times the log1p popularity factor of design 9.3: exp + log1p.</summary>
    public const string FusionFormula = """
        let utility    = param.w_click * col.score.p_click + param.w_like * col.score.p_like;
        let age_hours  = col.item.age_hours ?? 720.0;
        let freshness  = exp(-age_hours / param.half_life_hours);
        let cold_boost = (col.creator.follower_count < 1000.0 && ctx.user.tenure_days > 30.0) ? param.cold_creator_boost : 1.0;
        utility * (1.0 + param.w_fresh * freshness) * cold_boost * (1.0 + 0.05 * log1p(col.item.clicks_7d))
        """;

    /// <summary>Exploration noise of design 7.8 (all-int64 hash01 fast path).</summary>
    public const string ExplorationFormula = "param.explore_eps * hash01(ctx.request.seed, col.entity_id)";

    /// <summary>An int64 result written into an <c>int32</c> column: measures the narrowing write (design 4.3).</summary>
    public const string NarrowFormula = "col.entity_id - 1000000";

    /// <summary>The list kernels over a per-candidate topic list: <c>size</c>, <c>in</c> and <c>overlap_count</c>.</summary>
    public const string ListFormula = "overlap_count(col.item.topics64, ctx.user.topics) + size(col.item.topics64) + (col.entity_id % 64 in col.item.topics64 ? 1 : 0)";

    /// <summary><see cref="ListFormula"/> over the same topics stored as <c>list&lt;int32&gt;</c> (read in place, no widening).</summary>
    public const string List32Formula = "overlap_count(col.item.topics32, ctx.user.topics) + size(col.item.topics32) + (col.entity_id % 64 in col.item.topics32 ? 1 : 0)";

    private const int LiveRows = 5_000;

    private static readonly (string Name, ColumnType Type)[] Columns =
    {
        ("col.score.p_click", ColumnType.Parse("float64")),
        ("col.score.p_like", ColumnType.Parse("float64")),
        ("col.item.age_hours", ColumnType.Parse("float32?")),
        ("col.item.clicks_7d", ColumnType.Parse("float32")),
        ("col.creator.follower_count", ColumnType.Parse("float32")),
        ("col.item.topics64", ColumnType.Parse("list<int64>")),
        ("col.item.topics32", ColumnType.Parse("list<int32>")),
    };

    private Fixture _full = null!;
    private Fixture _sparse = null!;
    private CompiledExpression _fusion = null!;
    private CompiledExpression _exploration = null!;
    private CompiledExpression _narrow = null!;
    private CompiledExpression _list64 = null!;
    private CompiledExpression _list32 = null!;

    [GlobalSetup]
    public void Setup()
    {
        var scope = new DictionaryScope();
        scope.Add(new Symbol(SymbolKind.Column, "col.entity_id", ColumnType.Int64, 0, TableSchema.EntityIdSlot));
        for (var i = 0; i < Columns.Length; i++)
        {
            scope.Add(new Symbol(SymbolKind.Column, Columns[i].Name, Columns[i].Type, i + 1));
        }

        scope.Add(new Symbol(SymbolKind.Context, "ctx.user.tenure_days", ColumnType.Float32, 0));
        scope.Add(new Symbol(SymbolKind.Context, "ctx.request.seed", ColumnType.Int64, 1));
        scope.Add(new Symbol(SymbolKind.Context, "ctx.user.topics", ColumnType.ListInt64, 2));
        string[] parameters = { "param.w_click", "param.w_like", "param.w_fresh", "param.half_life_hours", "param.cold_creator_boost", "param.explore_eps" };
        for (var i = 0; i < parameters.Length; i++)
        {
            scope.Add(new Symbol(SymbolKind.Param, parameters[i], ColumnType.Float64, i));
        }

        _fusion = ExpressionCompiler.Compile(FusionFormula, scope, CompileOptions.Score);
        _exploration = ExpressionCompiler.Compile(ExplorationFormula, scope, CompileOptions.Score);
        _narrow = ExpressionCompiler.Compile(NarrowFormula, scope, new CompileOptions { ExpectedType = ColumnType.Int32 });
        _list64 = ExpressionCompiler.Compile(ListFormula, scope, CompileOptions.Score);
        _list32 = ExpressionCompiler.Compile(List32Formula, scope, CompileOptions.Score);

        _full = new Fixture(physicalRows: LiveRows, stride: 1);
        _sparse = new Fixture(physicalRows: LiveRows * 4, stride: 4);

        // Warm the pools so the measured iterations see the steady state.
        FusionFullRange();
        FusionGather();
        ExplorationFullRange();
        NarrowInt32FullRange();
        ListInt64FullRange();
        ListInt64Gather();
        ListInt32FullRange();
        ListInt32Gather();
    }

    [Benchmark(Baseline = true)]
    public void FusionFullRange() => _fusion.Evaluate(_full.Context, _full.Rows, _full.Destination);

    [Benchmark]
    public void FusionGather() => _fusion.Evaluate(_sparse.Context, _sparse.Rows, _sparse.Destination);

    [Benchmark]
    public void ExplorationFullRange() => _exploration.Evaluate(_full.Context, _full.Rows, _full.Destination);

    [Benchmark]
    public void NarrowInt32FullRange() => _narrow.Evaluate(_full.Context, _full.Rows, _full.Int32Destination);

    [Benchmark]
    public void ListInt64FullRange() => _list64.Evaluate(_full.Context, _full.Rows, _full.Destination);

    [Benchmark]
    public void ListInt64Gather() => _list64.Evaluate(_sparse.Context, _sparse.Rows, _sparse.Destination);

    [Benchmark]
    public void ListInt32FullRange() => _list32.Evaluate(_full.Context, _full.Rows, _full.Destination);

    [Benchmark]
    public void ListInt32Gather() => _list32.Evaluate(_sparse.Context, _sparse.Rows, _sparse.Destination);

    /// <summary>One request: a table whose live rows are every <c>stride</c>-th physical row.</summary>
    private sealed class Fixture
    {
        public Fixture(int physicalRows, int stride)
        {
            var memory = new RequestMemory();
            var slotTypes = new List<ColumnType> { ColumnType.Int64 };
            foreach (var (_, type) in Columns)
            {
                slotTypes.Add(type);
            }

            var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, slotTypes), physicalRows, memory);
            var bindings = new OutputBindings(Columns.Length + 1, memory);
            for (var i = 0; i < Columns.Length; i++)
            {
                bindings.Commit(i + 1, i + 1, producerNode: 0);
            }

            var ids = new long[physicalRows];
            for (var r = 0; r < physicalRows; r++)
            {
                ids[r] = 1_000_000 + r;
            }

            table.AppendRows(ids);
            var random = new Random(7);
            var pClick = (ScalarColumn<double>)table.GetOrCreateSlot(1);
            var pLike = (ScalarColumn<double>)table.GetOrCreateSlot(2);
            var age = (ScalarColumn<float>)table.GetOrCreateSlot(3);
            var clicks = (ScalarColumn<float>)table.GetOrCreateSlot(4);
            var followers = (ScalarColumn<float>)table.GetOrCreateSlot(5);
            var topics64 = (ListColumn<long>)table.GetOrCreateSlot(6);
            var topics32 = (ListColumn<int>)table.GetOrCreateSlot(7);
            Span<long> topics = stackalloc long[8];
            Span<int> narrowTopics = stackalloc int[8];
            for (var r = 0; r < physicalRows; r++)
            {
                pClick.Set(r, random.NextDouble() * 0.2);
                pLike.Set(r, random.NextDouble() * 0.05);
                if (random.Next(10) == 0)
                {
                    age.SetMissing(r);
                }
                else
                {
                    age.Set(r, (float)(random.NextDouble() * 400));
                }

                clicks.Set(r, random.Next(5_000));
                followers.Set(r, random.Next(10_000));
                var count = 1 + random.Next(topics.Length);
                for (var t = 0; t < count; t++)
                {
                    narrowTopics[t] = random.Next(64);
                    topics[t] = narrowTopics[t];
                }

                topics64.Set(r, topics[..count]);
                topics32.Set(r, narrowTopics[..count]);
            }

            Rows = new int[physicalRows / stride];
            for (var i = 0; i < Rows.Length; i++)
            {
                Rows[i] = i * stride;
            }

            Destination = ColumnBuffer.Create(ColumnType.Float64, physicalRows, memory);
            Int32Destination = ColumnBuffer.Create(ColumnType.Int32, physicalRows, memory);
            ScalarValue[] context =
            {
                ScalarValue.FromFloat64(45), ScalarValue.FromInt64(42),
                ScalarValue.FromInt64List(new long[] { 1, 3, 5, 8, 13, 21, 34, 55, 2, 4, 6, 10, 12, 20, 40, 60 }),
            };
            ScalarValue[] parameters =
            {
                ScalarValue.FromFloat64(1.0), ScalarValue.FromFloat64(0.6), ScalarValue.FromFloat64(0.3),
                ScalarValue.FromFloat64(48), ScalarValue.FromFloat64(1.2), ScalarValue.FromFloat64(0.05),
            };
            Context = new EvalContext(table, bindings, context, parameters, new ExpressionCounters());
        }

        public EvalContext Context { get; }

        public int[] Rows { get; }

        public ColumnBuffer Destination { get; }

        public ColumnBuffer Int32Destination { get; }
    }
}
