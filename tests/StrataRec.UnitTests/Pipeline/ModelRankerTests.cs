using System.Diagnostics.Metrics;
using System.Globalization;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.UnitTests.Features.Cache;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// <c>PairwiseRanker@1</c> and <c>ListwiseReranker@1</c> (design 5.9): the pair and list tensors they send for each kind
/// of input, how they turn the model's answers into a new order of the window, what fails the node (leaving the order as
/// it was), and the load-time checks of their config and of every model version's signature.
/// </summary>
public sealed class ModelRankerTests
{
    private const long Big = 9007199254740993L;

    private static readonly float[] UserEmb = { 0.5f, -0.25f, 8f };

    /// <summary>The fixture's items: an int64 (some beyond 2^53), a float64 and a 3-vector each.</summary>
    internal static readonly Dictionary<long, (long Code, double Ctr, float[] Emb)> Items = new()
    {
        [10] = (Big, 0.1, new[] { 1f, 2f, 3f }),
        [20] = (-5, 1e-40, new[] { 4f, 5f, 6f }),
        [30] = (long.MaxValue, 0.30000000000000004, new[] { 7f, 8f, 9f }),
        [40] = (long.MinValue, -2.5, new[] { -1f, float.Epsilon, 1e30f }),
        [50] = (0, 0.5, new[] { 0.1f, 0.2f, 0.3f }),
        [60] = (42, 0.6, new[] { 2f, 2f, 2f }),
        [70] = (7, 0.7, new[] { 3f, 3f, 3f }),
    };

    /// <summary>The order the recall leaves the rows in: what the rankers see.</summary>
    private static readonly long[] Incoming = { 30, 10, 50, 20, 40, 70, 60 };

    /// <summary>The pairs of the first four rows, in the order they are sent: i ascending, then j, the higher-ranked first.</summary>
    private static readonly (long, long)[] PairsOf4 = { (30, 10), (30, 50), (30, 20), (10, 50), (10, 20), (50, 20) };

    private const string AllKinds = """
        { "id": "col.entity_id", "code": "col.item.code", "ctr": "col.item.ctr", "emb": "col.item.emb",
          "user_emb": "ctx.request.user_emb", "user_id": "ctx.request.user_id", "weight": "param.weight" }
        """;

    private const string Ids = """{ "id": "col.entity_id" }""";

    private static readonly ModelSignature PairAll = Signature(
        ("id", TensorElementType.Int64, [-1, 2]), ("code", TensorElementType.Int64, [-1, 2]), ("ctr", TensorElementType.Float32, [-1, 2]),
        ("emb", TensorElementType.Float32, [-1, 2, 3]), ("user_emb", TensorElementType.Float32, [-1, 3]), ("user_id", TensorElementType.Int64, [-1]),
        ("weight", TensorElementType.Float32, [-1]), ("p", TensorElementType.Float32, [-1]));

    private static readonly ModelSignature PairIds = Signature(("id", TensorElementType.Int64, [-1, 2]), ("p", TensorElementType.Float32, [-1]));

    private static readonly ModelSignature ListAll = Signature(
        ("id", TensorElementType.Int64, [1, -1]), ("code", TensorElementType.Int64, [1, -1]), ("ctr", TensorElementType.Float32, [1, -1]),
        ("emb", TensorElementType.Float32, [1, -1, 3]), ("user_emb", TensorElementType.Float32, [1, 3]), ("user_id", TensorElementType.Int64, [1]),
        ("weight", TensorElementType.Float32, [1]), ("s", TensorElementType.Float32, [1, -1]));

    private static readonly ModelSignature ListIds = Signature(("id", TensorElementType.Int64, [1, -1]), ("s", TensorElementType.Float32, [1, -1]));

    /// <summary>Strengths giving the window of five a known result: 10 first, 20 and 50 tied, 30 and 40 tied.</summary>
    private static readonly Dictionary<long, int> Strengths = new() { [30] = 0, [10] = 3, [50] = 1, [20] = 1, [40] = 0, [60] = 0, [70] = 0 };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(256)]
    public async Task Pairwise_SendsEachPairOfTheWindowOnce_ColumnsAsTwoRows_ContextOncePerPair_InBatchesOfBatchSize(int batchSize)
    {
        var models = new TestModels(_ => PairAll, Prefer(Strengths));
        var plan = Compile(models, "PairwiseRanker@1", $$"""{ "model": "m", "inputs": {{AllKinds}}, "output": "p", "window": 4, "batch_size": {{batchSize}} }""");

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        var calls = models.Calls;
        var expectedSizes = Enumerable.Range(0, (6 + batchSize - 1) / batchSize).Select(b => Math.Min(batchSize, 6 - (b * batchSize)));
        Assert.Equal(expectedSizes.Order(), calls.Select(c => c.BatchSize).Order());
        var sent = new List<int>();
        foreach (var call in calls)
        {
            var n = call.BatchSize;
            Assert.Equal(PairAll.Inputs.Select(s => s.Name).Order(StringComparer.Ordinal), call.Inputs.Select(i => i.Name)); // in ordinal order
            AssertTensor(call["id"], TensorElementType.Int64, n, 2);
            AssertTensor(call["code"], TensorElementType.Int64, n, 2);
            AssertTensor(call["ctr"], TensorElementType.Float32, n, 2);
            AssertTensor(call["emb"], TensorElementType.Float32, n, 2, 3);
            AssertTensor(call["user_emb"], TensorElementType.Float32, n, 3);
            AssertTensor(call["user_id"], TensorElementType.Int64, n);
            AssertTensor(call["weight"], TensorElementType.Float32, n);
            var first = Array.IndexOf(PairsOf4, (call["id"].Int64[0], call["id"].Int64[1]));
            Assert.True(first >= 0 && first % batchSize == 0, $"a batch starts at pair {first}");
            for (var p = 0; p < n; p++)
            {
                sent.Add(first + p);
                var (a, b) = PairsOf4[first + p];
                Assert.Equal(new[] { a, b }, call["id"].Int64.AsSpan(2 * p, 2).ToArray()); // the batch is a contiguous run of the pairs
                Assert.Equal(new[] { Items[a].Code, Items[b].Code }, call["code"].Int64.AsSpan(2 * p, 2).ToArray()); // exact
                Assert.Equal(new[] { (float)Items[a].Ctr, (float)Items[b].Ctr }, call["ctr"].Float32.AsSpan(2 * p, 2).ToArray());
                Assert.Equal(Items[a].Emb.Concat(Items[b].Emb), call["emb"].Float32.AsSpan(6 * p, 6).ToArray()); // row-major [pair, row, N]
                Assert.Equal(UserEmb, call["user_emb"].Float32.AsSpan(3 * p, 3).ToArray()); // the request's vector in every pair
                Assert.Equal(Big, call["user_id"].Int64[p]);
                Assert.Equal(0.75f, call["weight"].Float32[p]);
            }
        }

        Assert.Equal(Enumerable.Range(0, 6), sent.Order()); // every pair once
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(50, 7)]
    public async Task Listwise_SendsTheWindowAsOneList_InOneCall_ColumnsAlongTheList_ContextOnce(int window, int n)
    {
        var models = new TestModels(_ => ListAll, Score(new Dictionary<long, float>()));
        var plan = Compile(models, "ListwiseReranker@1", $$"""{ "model": "m", "inputs": {{AllKinds}}, "output": "s", "window": {{window}} }""");

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(models.Calls);
        Assert.Equal(1, call.BatchSize);
        AssertTensor(call["id"], TensorElementType.Int64, 1, n);
        AssertTensor(call["code"], TensorElementType.Int64, 1, n);
        AssertTensor(call["ctr"], TensorElementType.Float32, 1, n);
        AssertTensor(call["emb"], TensorElementType.Float32, 1, n, 3);
        AssertTensor(call["user_emb"], TensorElementType.Float32, 1, 3);
        AssertTensor(call["user_id"], TensorElementType.Int64, 1);
        AssertTensor(call["weight"], TensorElementType.Float32, 1);
        var list = Incoming[..n];
        Assert.Equal(list, call["id"].Int64); // in rank order
        Assert.Equal(list.Select(id => Items[id].Code), call["code"].Int64);
        Assert.Equal(list.Select(id => (float)Items[id].Ctr), call["ctr"].Float32);
        Assert.Equal(list.SelectMany(id => Items[id].Emb), call["emb"].Float32);
        Assert.Equal(UserEmb, call["user_emb"].Float32);
        Assert.Equal(new[] { Big }, call["user_id"].Int64);
        Assert.Equal(new[] { 0.75f }, call["weight"].Float32);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(256)]
    [InlineData(int.MaxValue)] // one call: the batch count overflowed and no call was sent (review 27)
    public async Task Pairwise_RanksTheWindowByExpectedWins_TiesByEntityId_RowsAfterTheWindowKeepTheirOrder(int batchSize)
    {
        var models = new TestModels(_ => PairIds, Prefer(Strengths));
        var plan = Compile(models, "PairwiseRanker@1", $$"""{ "model": "m", "inputs": {{Ids}}, "output": "p", "window": 5, "batch_size": {{batchSize}}, "score_col": "score.rank" }""",
            outputColumns: """ "score": "col.score.rank" """);

        var result = await Run(plan);

        // Wins of a row: Σ over the other four of 0.5 + (s_row - s_other) / 8. 50 came before 20, but they tie: by id.
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 10, 20, 50, 30, 40, 70, 60 }, IdsOf(result));
        Assert.Equal(new double?[] { 3.25, 2, 2, 1.375, 1.375, null, null }, ScoresOf(result));
        Assert.Equal(10, models.Calls.Sum(c => c.BatchSize));
    }

    [Fact]
    public async Task Listwise_RanksTheWindowByScore_NaNLast_TiesByEntityId_RowsAfterTheWindowKeepTheirOrder()
    {
        var scores = new Dictionary<long, float> { [30] = 1, [10] = float.NaN, [50] = 2, [20] = 2, [40] = float.NegativeInfinity };
        var models = new TestModels(_ => ListIds, Score(scores));
        var plan = Compile(models, "ListwiseReranker@1", $$"""{ "model": "m", "inputs": {{Ids}}, "output": "s", "window": 5, "score_col": "score.rank" }""",
            outputColumns: """ "score": "col.score.rank" """);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 20, 50, 30, 40, 10, 70, 60 }, IdsOf(result));
        Assert.Equal(new double?[] { 2, 2, 1, null, null, null, null }, ScoresOf(result)); // non-finite scores are missing in the column (design 4.3)
        Assert.Equal(2, result.NonFiniteValues);
    }

    [Fact]
    public async Task Pairwise_WithFewerThanTwoRows_CallsNothing_AndSucceeds()
    {
        var models = new TestModels(_ => PairIds, Prefer(Strengths));
        var config = $$"""{ "model": "m", "inputs": {{Ids}}, "output": "p", "score_col": "score.rank" }""";

        var one = await Run(Compile(models, "PairwiseRanker@1", config, recall: [30], outputColumns: """ "score": "col.score.rank" """));
        var none = await Run(Compile(models, "PairwiseRanker@1", config, recall: [], outputColumns: """ "score": "col.score.rank" """));

        Assert.True(one.Succeeded, one.Error);
        Assert.Equal(new long[] { 30 }, IdsOf(one));
        Assert.Equal(new double?[] { 0 }, ScoresOf(one)); // no other row to win against: the empty sum
        Assert.True(none.Succeeded, none.Error);
        Assert.Empty(none.Items);
        Assert.Empty(models.Calls);
        Assert.All(one.Outcomes.Concat(none.Outcomes), o => Assert.Equal(NodeOutcomeKind.Succeeded, o.Kind));
    }

    [Fact]
    public async Task Listwise_WithNoRows_CallsNothing_AndWithOneRow_SendsAListOfOne()
    {
        var models = new TestModels(_ => ListIds, Score(new Dictionary<long, float>()));
        var config = $$"""{ "model": "m", "inputs": {{Ids}}, "output": "s" }""";

        var none = await Run(Compile(models, "ListwiseReranker@1", config, recall: []));
        Assert.True(none.Succeeded, none.Error);
        Assert.Empty(models.Calls);

        var one = await Run(Compile(models, "ListwiseReranker@1", config, recall: [30]));
        Assert.True(one.Succeeded, one.Error);
        var call = Assert.Single(models.Calls);
        AssertTensor(call["id"], TensorElementType.Int64, 1, 1);
        Assert.Equal(new long[] { 30 }, call["id"].Int64);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Pairwise_AFailedCall_InAnyBatch_FailsTheNode_AndLeavesTheOrderAsItWas(int failing)
    {
        // Window 5: ten pairs in five batches of two; batch `failing` starts at pair 2 * failing.
        var pairs = PairsOfIncoming(5);
        var prefer = Prefer(Strengths);
        var models = new TestModels(_ => PairIds, call => (call["id"].Int64[0], call["id"].Int64[1]) == pairs[2 * failing] ? null : prefer(call));
        var plan = Compile(models, "PairwiseRanker@1", $$"""{ "model": "m", "inputs": {{Ids}}, "output": "p", "window": 5, "batch_size": 2, "score_col": "score.rank" }""",
            onFailure: "skip", outputColumns: """ "score": "col.score.rank" """);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Incoming, IdsOf(result));
        Assert.All(ScoresOf(result), s => Assert.Null(s)); // a failed node commits nothing
        var outcome = Assert.Single(result.Outcomes, o => o.NodeId == "rank");
        Assert.Equal((NodeOutcomeKind.Failed, "model 'm' failed: refused (skipped)"), (outcome.Kind, outcome.Detail));
        Assert.Equal(5, models.Calls.Count); // every batch was sent
    }

    [Theory]
    [InlineData(float.NaN, "NaN")]
    [InlineData(-0.25f, "-0.25")]
    [InlineData(1.5f, "1.5")]
    [InlineData(float.PositiveInfinity, "Infinity")]
    public async Task Pairwise_APreferenceThatIsNotAProbability_FailsTheNode_NamingThePairAndTheValue(float value, string shown)
    {
        var prefer = Prefer(Strengths);
        var models = new TestModels(_ => PairIds, call =>
        {
            var answer = prefer(call)!;
            for (var p = 0; p < call.BatchSize; p++)
            {
                if ((call["id"].Int64[2 * p], call["id"].Int64[(2 * p) + 1]) == (10, 50))
                {
                    answer[p] = value;
                }
            }

            return answer;
        });
        var plan = Compile(models, "PairwiseRanker@1", $$"""{ "model": "m", "inputs": {{Ids}}, "output": "p", "window": 5, "batch_size": 3 }""", onFailure: "skip");

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Incoming, IdsOf(result));
        var outcome = Assert.Single(result.Outcomes, o => o.NodeId == "rank");
        Assert.Equal(NodeOutcomeKind.Failed, outcome.Kind);
        Assert.Equal($"model 'm' returned {shown} as the preference of 10 over 50, which is not a probability in [0, 1]; output 'p' must be the probability "
            + "that the pair's first row is preferred (apply the sigmoid inside the model) (skipped)", outcome.Detail);
    }

    [Fact]
    public async Task Pairwise_CertainPreferences_ZeroAndOne_AreProbabilities()
    {
        var models = new TestModels(_ => PairIds, call => Enumerable.Range(0, call.BatchSize).Select(p => call["id"].Int64[2 * p] < call["id"].Int64[(2 * p) + 1] ? 1f : 0f).ToArray());
        var plan = Compile(models, "PairwiseRanker@1", $$"""{ "model": "m", "inputs": {{Ids}}, "output": "p", "window": 7 }""");

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Incoming.Order(), IdsOf(result)); // the lower id always wins
    }

    [Fact]
    public async Task Listwise_AFailedCall_OrAShortOutput_FailsTheNode_AndLeavesTheOrderAsItWas()
    {
        var refused = new TestModels(_ => ListIds, _ => null);
        var shortOutput = new TestModels(_ => ListIds, _ => new[] { 1f, 2f });
        const string Config = """{ "model": "m", "inputs": { "id": "col.entity_id" }, "output": "s", "window": 5, "score_col": "score.rank" }""";

        var failed = await Run(Compile(refused, "ListwiseReranker@1", Config, onFailure: "skip", outputColumns: """ "score": "col.score.rank" """));
        var cut = await Run(Compile(shortOutput, "ListwiseReranker@1", Config, onFailure: "skip", outputColumns: """ "score": "col.score.rank" """));

        Assert.Equal(Incoming, IdsOf(failed));
        Assert.Equal(Incoming, IdsOf(cut));
        Assert.All(ScoresOf(failed).Concat(ScoresOf(cut)), s => Assert.Null(s));
        Assert.Equal("model 'm' failed: refused (skipped)", Assert.Single(failed.Outcomes, o => o.NodeId == "rank").Detail);
        Assert.Equal("model 'm' did not return output 's' for every row of the list (skipped)", Assert.Single(cut.Outcomes, o => o.NodeId == "rank").Detail);
    }

    [Theory]
    [InlineData(51, null, "A window of 51 rows makes 1275 pairs, more than max_pairs (1225).")]
    [InlineData(50, null, null)]
    [InlineData(10, 44, "A window of 10 rows makes 45 pairs, more than max_pairs (44).")]
    [InlineData(10, 45, null)]
    [InlineData(100000, 1225, "A window of 100000 rows makes 4999950000 pairs, more than max_pairs (1225).")]
    public void Pairwise_AWindowWithMorePairsThanMaxPairs_IsALoadError_AtTheWindow(int window, int? maxPairs, string? message)
    {
        var limit = maxPairs is { } m ? $", \"max_pairs\": {m}" : string.Empty;

        var compilation = TryCompile(new TestModels(_ => PairIds, Prefer(Strengths)), "PairwiseRanker@1",
            $$"""{ "model": "m", "inputs": {{Ids}}, "output": "p", "window": {{window}}{{limit}} }""");

        if (message is null)
        {
            Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
            return;
        }

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("/nodes/rank/config/window", "RANKER_MAX_PAIRS", message), (error.Pointer, error.Code, error.Message));
        Assert.Contains("lower 'window', or raise 'max_pairs'", error.Hint);
        Assert.Contains("none is dropped at run time", error.Hint);
    }

    [Theory]
    [InlineData("PairwiseRanker@1", """ "window": 1 """, "/window", "RANKER_WINDOW")]
    [InlineData("PairwiseRanker@1", """ "max_pairs": 0 """, "/max_pairs", "RANKER_MAX_PAIRS")]
    [InlineData("PairwiseRanker@1", """ "max_pairs": 2147483647, "window": 46342 """, "/max_pairs", "RANKER_MAX_PAIRS")] // 2 · pairs overflowed at run time (review 28)
    [InlineData("PairwiseRanker@1", """ "batch_size": 0 """, "/batch_size", "MODEL_BAD_BATCH")]
    [InlineData("ListwiseReranker@1", """ "window": 0 """, "/window", "RANKER_WINDOW")]
    [InlineData("ListwiseReranker@1", """ "output": "" """, "/output", "MODEL_OUTPUT")]
    [InlineData("ListwiseReranker@1", """ "batch_size": 8 """, "/batch_size", "NODE_BAD_CONFIG")]
    public void ABadSetting_IsALoadError_AtItsKey(string op, string setting, string key, string code)
    {
        var output = op == "PairwiseRanker@1" ? "p" : "s";
        var signature = op == "PairwiseRanker@1" ? PairIds : ListIds;
        var config = setting.Contains("\"output\"", StringComparison.Ordinal)
            ? $$"""{ "model": "m", "inputs": {{Ids}}, {{setting}} }"""
            : $$"""{ "model": "m", "inputs": {{Ids}}, "output": "{{output}}", {{setting}} }""";

        var compilation = TryCompile(new TestModels(_ => signature, _ => null), op, config);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("/nodes/rank/config" + key, code), (error.Pointer, error.Code));
    }

    /// <summary>
    /// Every version the node may use is checked against the operator's layout, in both directions of the dimension rule:
    /// the dynamic dimension only as -1, the fixed ones as their size or -1.
    /// </summary>
    [Theory]
    [InlineData("PairwiseRanker@1", new[] { -1, 2 }, null)]
    [InlineData("PairwiseRanker@1", new[] { -1, -1 }, null)]
    [InlineData("PairwiseRanker@1", new[] { 2, 2 }, "float32 [-1, 2]")]
    [InlineData("PairwiseRanker@1", new[] { -1, 3 }, "float32 [-1, 2]")]
    [InlineData("PairwiseRanker@1", new[] { -1 }, "float32 [-1, 2]")]
    [InlineData("ListwiseReranker@1", new[] { 1, -1 }, null)]
    [InlineData("ListwiseReranker@1", new[] { -1, -1 }, null)]
    [InlineData("ListwiseReranker@1", new[] { 1, 5 }, "float32 [1, -1]")]
    [InlineData("ListwiseReranker@1", new[] { 2, -1 }, "float32 [1, -1]")]
    [InlineData("ListwiseReranker@1", new[] { -1 }, "float32 [1, -1]")]
    public void EveryVersionsSignature_IsCheckedAgainstTheLayout(string op, int[] v2Shape, string? sent)
    {
        var pairwise = op == "PairwiseRanker@1";
        var good = pairwise ? new[] { -1, 2 } : new[] { 1, -1 };
        ModelSignature Version(string? version) => Signature(
            ("id", TensorElementType.Int64, good), ("ctr", TensorElementType.Float32, version == "v2" ? v2Shape : good),
            (pairwise ? "p" : "s", TensorElementType.Float32, pairwise ? [-1] : [1, -1]));
        var config = $$"""{ "model": "m", "version_param": "m_version", "inputs": { "id": "col.entity_id", "ctr": "col.item.ctr" }, "output": "{{(pairwise ? "p" : "s")}}" }""";

        var compilation = TryCompile(new TestModels(Version, _ => null), op, config);

        if (sent is null)
        {
            Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
            return;
        }

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("/nodes/rank/config/inputs/ctr", "MODEL_TENSOR_MISMATCH"), (error.Pointer, error.Code));
        Assert.Equal($"Model 'm' version 'v2' declares input 'ctr' as float32 [{string.Join(", ", v2Shape)}], but it maps col.item.ctr (float64), which {op} sends as {sent}.",
            error.Message);
    }

    [Theory]
    [InlineData("PairwiseRanker@1", new[] { -1, -1 }, "MODEL_TENSOR_UNSUPPORTED")]
    [InlineData("PairwiseRanker@1", new[] { 1 }, "MODEL_TENSOR_UNSUPPORTED")]
    [InlineData("ListwiseReranker@1", new[] { -1 }, "MODEL_TENSOR_UNSUPPORTED")]
    [InlineData("ListwiseReranker@1", new[] { 1, 50 }, "MODEL_TENSOR_UNSUPPORTED")]
    public void AnOutputOfAnotherShape_IsALoadError_AtTheOutput(string op, int[] shape, string code)
    {
        var pairwise = op == "PairwiseRanker@1";
        var signature = Signature(("id", TensorElementType.Int64, pairwise ? [-1, 2] : [1, -1]), (pairwise ? "p" : "s", TensorElementType.Float32, shape));

        var compilation = TryCompile(new TestModels(_ => signature, _ => null), op, $$"""{ "model": "m", "inputs": {{Ids}}, "output": "{{(pairwise ? "p" : "s")}}" }""");

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("/nodes/rank/config/output", code), (error.Pointer, error.Code));
        Assert.StartsWith(op, error.Hint);
    }

    [Theory]
    [InlineData("float32", false)]
    [InlineData("int64?", false)]
    [InlineData("float64?", true)]
    [InlineData("float32?", true)]
    public async Task TheScoreColumn_MustBeANullableFloat(string type, bool accepted)
    {
        var models = new TestModels(_ => PairIds, Prefer(Strengths));
        var config = $$"""{ "model": "m", "inputs": {{Ids}}, "output": "p", "window": 5, "score_col": "score.rank" }""";
        var declared = $$""" "score.rank": { "type": "{{type}}"{{(type.EndsWith('?') ? string.Empty : ", \"default\": 0")}} } """;

        var compilation = TryCompile(models, "PairwiseRanker@1", config, columns: declared, outputColumns: """ "score": "col.score.rank" """);

        if (!accepted)
        {
            var error = Assert.Single(compilation.Diagnostics, d => d.IsError && d.Pointer == "/nodes/rank/config/score_col");
            Assert.Equal("COLUMN_TYPE_MISMATCH", error.Code);
            Assert.Contains("float32? or float64?", error.Message);
            return;
        }

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var result = await Run(compilation.Plan!);
        Assert.Equal(new double?[] { 3.25, 2, 2, 1.375, 1.375, null, null }, ScoresOf(result));
    }

    /// <summary>The pairs of the first <paramref name="n"/> incoming rows, in the order they are sent.</summary>
    internal static (long, long)[] PairsOfIncoming(int n) =>
        (from i in Enumerable.Range(0, n) from j in Enumerable.Range(i + 1, n - i - 1) select (Incoming[i], Incoming[j])).ToArray();

    /// <summary>A pairwise model: the first row of a pair is preferred with probability 0.5 + (s_first − s_second) / 8, exact in float32.</summary>
    internal static Func<CapturedCall, float[]?> Prefer(IReadOnlyDictionary<long, int> strengths) => call =>
    {
        var ids = call["id"].Int64;
        return Enumerable.Range(0, call.BatchSize).Select(p => 0.5f + ((strengths[ids[2 * p]] - strengths[ids[(2 * p) + 1]]) / 8f)).ToArray();
    };

    /// <summary>A listwise model: each row's score from the table (0 for the others).</summary>
    private static Func<CapturedCall, float[]?> Score(IReadOnlyDictionary<long, float> scores) =>
        call => call["id"].Int64.Select(id => scores.GetValueOrDefault(id)).ToArray();

    private static long[] IdsOf(PipelineResult result) => result.Items.Select(i => i.Id.Int64Value).ToArray();

    private static double?[] ScoresOf(PipelineResult result) =>
        result.Items.Select(i => i.Columns["score"] is { IsMissing: false } v ? v.AsFloat64() : (double?)null).ToArray();

    private static void AssertTensor(CapturedInput input, TensorElementType type, params int[] shape)
    {
        Assert.Equal(type, input.ElementType);
        Assert.Equal(shape, input.Shape);
        var elements = shape.Aggregate(1, (a, d) => a * d);
        Assert.Equal(type == TensorElementType.Float32 ? elements : 0, input.Float32.Length);
        Assert.Equal(type == TensorElementType.Int64 ? elements : 0, input.Int64.Length);
    }

    private static ModelSignature Signature(params (string Name, TensorElementType Type, int[] Shape)[] specs) => new(
        specs[..^1].Select(s => new TensorSpec(s.Name, s.Type, s.Shape)).ToArray(),
        new[] { new TensorSpec(specs[^1].Name, specs[^1].Type, specs[^1].Shape) });

    internal static ValueTask<PipelineResult> Run(ExecutionPlan plan) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r",
        Inputs = new RequestInputs().Set("user_emb", ScalarValue.FromVector(UserEmb)).Set("user_id", Big),
        Deadline = TimeSpan.FromSeconds(30),
    });

    internal static ExecutionPlan Compile(
        TestModels models, string op, string config, long[]? recall = null, string onFailure = "fail_request", string outputColumns = "")
    {
        var compilation = TryCompile(models, op, config, recall, onFailure, outputColumns: outputColumns);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>
    /// A workspace of one pipeline in a temp directory: the recall of <paramref name="recall"/> (the incoming order by
    /// default), then the node <c>rank</c> running <paramref name="op"/>. Values beyond 2^53 live in the data file:
    /// artifacts are RFC 8785 JSON, which cannot hold them.
    /// </summary>
    private static PlanCompilation TryCompile(
        TestModels models, string op, string config, long[]? recall = null, string onFailure = "fail_request", string columns = "", string outputColumns = "")
    {
        var root = Directory.CreateTempSubdirectory("stratarec-model-ranker-").FullName;
        Write(root, "catalog/c.jsonc", """
            { "catalog_id": "c",
              "entities": { "item": { "scope": "candidate", "key": "col.entity_id", "key_type": "int64" } },
              "features": {
                "item.code": { "type": "int64",          "source": "items", "field": "code", "default": 0 },
                "item.ctr":  { "type": "float64",        "source": "items", "field": "ctr",  "default": 0.0 },
                "item.emb":  { "type": "vec<float32,3>", "source": "items", "field": "emb",  "default": [0, 0, 0] } } }
            """);
        Write(root, "bindings/local.jsonc", $$"""
            { "environment": "local",
              "sources": { "items": { "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": 30000 } },
              "models": { "m": { "client": "{{TestModels.Type}}" } },
              "candidate_sources": { "s": { "client": "memory_list@1", "items": [{{string.Join(", ", recall ?? Incoming)}}] } } }
            """);
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "request_schema": { "user_emb": { "type": "vec<float32,3>" }, "user_id": { "type": "int64" } },
              "columns": { {{columns}} },
              "param_schema": {
                "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" }, "weight": { "type": "float64" },
                "m_version": { "type": "string", "enum": ["v1", "v2"] } },
              "params": { "quotas": { "s": 100 }, "k": 100, "weight": 0.75, "m_version": "v1" },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "rank": { "order": 200, "op": "{{op}}", "timeout_ms": 30000, "on_failure": "{{onFailure}}", "config": {{config}} } },
              "output": { "limit_param": "k", "columns": { {{outputColumns}} } } }
            """);
        Write(root, "fixtures/items.json", "{ " + string.Join(", ", Items.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"\"{p.Key}\": {{ \"code\": {p.Value.Code}, \"ctr\": {p.Value.Ctr:R}, "
            + $"\"emb\": [{string.Join(", ", p.Value.Emb.Select(f => f.ToString("R", CultureInfo.InvariantCulture)))}] }}"))) + " }");

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root, Models = new ModelClientRegistry().Add(models) });
    }

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>One input as the client received it, copied: the operator's buffers go back to the pool after the request.</summary>
    internal sealed record CapturedInput(string Name, TensorElementType ElementType, int[] Shape, float[] Float32, long[] Int64);

    internal sealed record CapturedCall(int BatchSize, string? Version, IReadOnlyList<CapturedInput> Inputs)
    {
        public CapturedInput this[string name] => Inputs.Single(i => i.Name == name);
    }

    /// <summary>
    /// <c>ranker_test@1</c>: declares a signature per version, keeps a copy of every call, and answers its output (the
    /// signature's only one) with what <c>answer</c> computes from the call, or refuses the call when it returns null.
    /// </summary>
    internal sealed class TestModels(Func<string?, ModelSignature> signature, Func<CapturedCall, float[]?> answer) : IModelClientFactory, IModelClient
    {
        public const string Type = "ranker_test@1";

        private readonly List<CapturedCall> _calls = new();

        public string ClientType => Type;

        public List<CapturedCall> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToList();
                }
            }
        }

        public IModelClient Create(BindingContext context) => this;

        public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromResult(signature(version));

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
        {
            var inputs = new List<CapturedInput>();
            foreach (var input in call.Inputs.Span)
            {
                inputs.Add(new CapturedInput(input.Name, input.ElementType, (int[])input.Shape.Clone(), input.Float32.ToArray(), input.Int64.ToArray()));
            }

            var captured = new CapturedCall(call.BatchSize, call.Version, inputs);
            lock (_calls)
            {
                _calls.Add(captured);
            }

            if (answer(captured) is not { } values)
            {
                return ValueTask.FromResult(ModelStatus.Failed("refused"));
            }

            values.CopyTo(outputs.GetFloat32(signature(call.Version).Outputs[0].Name, values.Length).Span);
            return ValueTask.FromResult(ModelStatus.Ok);
        }
    }
}

/// <summary><c>stratarec.model.pairs</c>: the pairs a pairwise node sends, tagged with the model.</summary>
[Collection(MetricMeasurement.Name)]
public sealed class ModelPairsMetricTests
{
    [Fact]
    public async Task ThePairsSent_AreCounted_OncePerRun_TaggedWithTheModel()
    {
        var plan = ModelRankerTests.Compile(new ModelRankerTests.TestModels(
                _ => new ModelSignature(new[] { new TensorSpec("id", TensorElementType.Int64, new[] { -1, 2 }) }, new[] { new TensorSpec("p", TensorElementType.Float32, new[] { -1 }) }),
                _ => null),
            "PairwiseRanker@1", """{ "model": "m", "inputs": { "id": "col.entity_id" }, "output": "p", "window": 5, "batch_size": 3 }""", onFailure: "skip");
        var measured = new List<(long Value, string? Model)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name == StrataRecMetrics.Names.ModelPairs)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? model = null;
            foreach (var tag in tags)
            {
                if (tag.Key == StrataRecTags.Model)
                {
                    model = (string?)tag.Value;
                }
            }

            lock (measured)
            {
                measured.Add((value, model));
            }
        });
        listener.Start();

        await ModelRankerTests.Run(plan); // the model refuses: the pairs were sent all the same

        lock (measured)
        {
            Assert.Equal(new[] { (10L, (string?)"m") }, measured);
        }
    }
}
