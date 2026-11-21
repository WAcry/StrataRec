using System.Globalization;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Memory;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// What <c>ModelScorer@1</c> sends (design 6.9): each input as a tensor of the declared type of its column or context
/// value, nothing converted (int64 values exact beyond 2^53, an int32 as an int32, a float64 as a float64, a bool as a
/// bool, a string as a string), gathered from the column's storage or broadcast from the request, and sliced into
/// batches; and which inputs it refuses to compile.
/// </summary>
public sealed class ModelScorerTests
{
    /// <summary>2^53 + 1: the first integer a double (and a float32) cannot hold.</summary>
    private const long Big = 9007199254740993L;

    private static readonly int[] Rows = { -1 };

    private static readonly int[] RowsOf3 = { -1, 3 };

    private static readonly int[] RowsOf2 = { -1, 2 };

    /// <summary>The fixture's items by id: an int64 above 2^53, an int32, a float64, a bool, a list&lt;float32,3&gt;
    /// (an embedding), a list&lt;bool,2&gt;, a list&lt;int32,2&gt; and a list&lt;float64,2&gt; each (and a string tag
    /// and a list&lt;string,2&gt; derived from the id).</summary>
    private static readonly Dictionary<long, (long Code, int Rank, double Ctr, bool Safe, float[] Emb, bool[] Flags, int[] Ids, double[] Weights)> Items = new()
    {
        [Big] = (Big + 2, 7, 0.1, true, new[] { 1f, 2f, 3f }, new[] { true, false }, new[] { int.MaxValue, -1 }, new[] { 0.1, 2 }),
        [Big - 1] = (-Big, -7, 1e-40, false, new[] { 4f, 5f, 6f }, new[] { false, false }, new[] { int.MinValue, 0 }, new[] { -0.5, 1e300 }),
        [3] = (long.MaxValue, int.MaxValue, 0.30000000000000004, true, new[] { 7f, 8f, 9f }, new[] { true, true }, new[] { 3, 4 }, new[] { 0.0, 0.0 }),
        [4] = (long.MinValue, int.MinValue, -2.5, false, new[] { -1f, float.Epsilon, 1e30f }, new[] { false, true }, new[] { 5, 6 }, new[] { 1.5, -1.5 }),
        [5] = (0, 0, 0, true, new[] { 0.1f, 0.2f, 0.3f }, new[] { true, false }, new[] { 7, 8 }, new[] { 3.0, 4.0 }),
    };

    /// <summary>The scorer's inputs in the execution tests, and what the model declares for them.</summary>
    private const string AllKinds = """
        { "id": "col.entity_id", "code": "col.item.code", "rank": "col.item.rank", "ctr": "col.item.ctr", "safe": "col.item.safe",
          "emb": "col.item.emb", "flags": "col.item.flags", "ids": "col.item.ids", "weights": "col.item.weights",
          "user_emb": "ctx.request.user_emb", "user_id": "ctx.request.user_id", "member": "ctx.request.member",
          "recent": "ctx.request.recent", "weight": "param.weight", "tag": "col.item.tag", "names": "col.item.names",
          "surface": "ctx.request.surface", "boost": "ctx.request.boost", "slot": "ctx.request.slot", "affinity": "ctx.user.affinity",
          "slots": "ctx.request.slots", "user_emb2": "ctx.request.user_emb" }
        """;

    /// <summary>The mapping: each reference's declared type, a list&lt;E,N&gt; with a trailing N.</summary>
    private static readonly ModelSignature AllKindsSignature = new(
        new[]
        {
            new TensorSpec("id", TensorElementType.Int64, Rows), new TensorSpec("code", TensorElementType.Int64, Rows),
            new TensorSpec("rank", TensorElementType.Int32, Rows), new TensorSpec("ctr", TensorElementType.Float64, Rows),
            new TensorSpec("safe", TensorElementType.Bool, Rows), new TensorSpec("emb", TensorElementType.Float32, RowsOf3),
            new TensorSpec("flags", TensorElementType.Bool, RowsOf2), new TensorSpec("ids", TensorElementType.Int32, RowsOf2),
            new TensorSpec("weights", TensorElementType.Float64, RowsOf2),
            new TensorSpec("user_emb", TensorElementType.Float32, RowsOf3), new TensorSpec("user_id", TensorElementType.Int64, Rows),
            new TensorSpec("member", TensorElementType.Bool, Rows), new TensorSpec("recent", TensorElementType.Int64, RowsOf2),
            new TensorSpec("weight", TensorElementType.Float64, Rows), new TensorSpec("tag", TensorElementType.String, Rows),
            new TensorSpec("names", TensorElementType.String, RowsOf2), new TensorSpec("surface", TensorElementType.String, Rows),
            new TensorSpec("boost", TensorElementType.Float32, Rows), new TensorSpec("slot", TensorElementType.Int32, Rows),
            new TensorSpec("affinity", TensorElementType.Float32, Rows), new TensorSpec("slots", TensorElementType.Int32, RowsOf2),
            new TensorSpec("user_emb2", TensorElementType.Float32, RowsOf3),
        },
        new[] { new TensorSpec("p", TensorElementType.Float32, Rows) });

    private static readonly long[] Recent = { Big, -3 };

    private static readonly float[] UserEmb = { 0.5f, -0.25f, 8f };

    [Theory]
    [InlineData(2, new[] { 2, 2, 1 })]
    [InlineData(3, new[] { 3, 2 })]
    [InlineData(4, new[] { 3, 2 })] // the fewest batches, even: not 4 and 1
    [InlineData(256, new[] { 5 })]
    [InlineData(int.MaxValue, new[] { 5 })] // one batch: the batch count must not overflow (review 27)
    public async Task EachInput_IsSentAsItsTypeGivesIt_WithEveryRowsValue_InBatchesOfBatchSize(int batchSize, int[] sizes)
    {
        var models = new CapturingModels(AllKindsSignature);
        var plan = Compile(models, AllKinds, batchSize);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(5, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(0.5, item.Columns["p"].AsFloat64())); // every batch's output was written back
        var calls = models.Calls;
        Assert.Equal(sizes.Order(), calls.Select(c => c.BatchSize).Order());
        var seen = new List<long>();
        foreach (var call in calls)
        {
            var n = call.BatchSize;
            Assert.Equal(AllKindsSignature.Inputs.Select(s => s.Name).Order(StringComparer.Ordinal), call.Inputs.Select(i => i.Name)); // in ordinal order
            AssertTensor(call["id"], TensorElementType.Int64, n);
            AssertTensor(call["code"], TensorElementType.Int64, n);
            AssertTensor(call["rank"], TensorElementType.Int32, n);
            AssertTensor(call["ctr"], TensorElementType.Float64, n);
            AssertTensor(call["safe"], TensorElementType.Bool, n);
            AssertTensor(call["emb"], TensorElementType.Float32, n, 3);
            AssertTensor(call["flags"], TensorElementType.Bool, n, 2);
            AssertTensor(call["ids"], TensorElementType.Int32, n, 2);
            AssertTensor(call["weights"], TensorElementType.Float64, n, 2);
            AssertTensor(call["user_emb"], TensorElementType.Float32, n, 3);
            AssertTensor(call["user_id"], TensorElementType.Int64, n);
            AssertTensor(call["member"], TensorElementType.Bool, n);
            AssertTensor(call["recent"], TensorElementType.Int64, n, 2);
            AssertTensor(call["weight"], TensorElementType.Float64, n);
            AssertTensor(call["tag"], TensorElementType.String, n);
            AssertTensor(call["names"], TensorElementType.String, n, 2);
            AssertTensor(call["surface"], TensorElementType.String, n);
            AssertTensor(call["boost"], TensorElementType.Float32, n);
            AssertTensor(call["slot"], TensorElementType.Int32, n);
            AssertTensor(call["affinity"], TensorElementType.Float32, n); // a context feature's declared type, not the promoted one its symbol has
            AssertTensor(call["slots"], TensorElementType.Int32, n, 2);
            AssertTensor(call["user_emb2"], TensorElementType.Float32, n, 3);
            for (var r = 0; r < n; r++)
            {
                var id = call["id"].Of<long>()[r];
                var item = Items[id];
                seen.Add(id);
                Assert.Equal(item.Code, call["code"].Of<long>()[r]); // exact: no detour through double
                Assert.Equal(item.Rank, call["rank"].Of<int>()[r]); // at its width: not widened
                Assert.Equal(item.Ctr, call["ctr"].Of<double>()[r]); // a float64 as is: not rounded to float32
                Assert.Equal(item.Safe, call["safe"].Of<bool>()[r]); // a bool as a bool
                Assert.Equal(item.Emb, call["emb"].Of<float>().AsSpan(r * 3, 3).ToArray()); // row-major
                Assert.Equal(item.Flags, call["flags"].Of<bool>().AsSpan(r * 2, 2).ToArray());
                Assert.Equal(item.Ids, call["ids"].Of<int>().AsSpan(r * 2, 2).ToArray());
                Assert.Equal(item.Weights, call["weights"].Of<double>().AsSpan(r * 2, 2).ToArray());
                Assert.Equal(UserEmb, call["user_emb"].Of<float>().AsSpan(r * 3, 3).ToArray()); // the request's vector in every row
                Assert.Equal(Big, call["user_id"].Of<long>()[r]);
                Assert.True(call["member"].Of<bool>()[r]);
                Assert.Equal(Recent, call["recent"].Of<long>().AsSpan(r * 2, 2).ToArray());
                Assert.Equal(0.75, call["weight"].Of<double>()[r]);
                Assert.Equal("t" + id.ToString(CultureInfo.InvariantCulture), call["tag"].Of<string>()[r]);
                Assert.Equal(new[] { "a" + id.ToString(CultureInfo.InvariantCulture), "b" }, call["names"].Of<string>().AsSpan(r * 2, 2).ToArray());
                Assert.Equal("home", call["surface"].Of<string>()[r]);
                Assert.Equal(0.1f, call["boost"].Of<float>()[r]); // a float32 request input, held promoted, narrowed back exactly
                Assert.Equal(int.MinValue, call["slot"].Of<int>()[r]);
                Assert.Equal(0.1f, call["affinity"].Of<float>()[r]);
                Assert.Equal(new[] { int.MaxValue, int.MinValue }, call["slots"].Of<int>().AsSpan(r * 2, 2).ToArray()); // an int32 list in every row
                Assert.Equal(UserEmb, call["user_emb2"].Of<float>().AsSpan(r * 3, 3).ToArray()); // the same value broadcast to two inputs
            }
        }

        Assert.Equal(Items.Keys.Order(), seen.Order()); // each row once
        Assert.Contains(Big, seen);
    }

    [Theory]
    [InlineData("col.item.maybe", "MODEL_INPUT_TYPE", "Model input 'x' maps col.item.maybe, which is nullable (float32?): a model needs a value in every row.")]
    [InlineData("ctx.request.maybe_id", "MODEL_INPUT_TYPE", "Model input 'x' maps ctx.request.maybe_id, which is nullable (int64?): a model needs a value in every row.")]
    [InlineData("col.item.maybe_emb", "MODEL_INPUT_TYPE", "Model input 'x' maps col.item.maybe_emb, which is nullable (list<float32,3>?): a model needs a value in every row.")]
    [InlineData("col.item.tags", "MODEL_INPUT_TYPE",
        "Model input 'x' maps col.item.tags (list<int64>), a list of any length: a tensor's trailing dimension is the same in every row.")]
    [InlineData("col.item.codes", "MODEL_INPUT_TYPE",
        "Model input 'x' maps col.item.codes (list<int32>), a list of any length: a tensor's trailing dimension is the same in every row.")]
    [InlineData("col.item.ctr * 2.0", "MODEL_INPUT_NOT_REFERENCE", "Model input 'x' must be a plain column or context reference, not an expression.")]
    public void AnInputItCannotSend_IsACompileError_AtTheInput(string source, string code, string message)
    {
        var compilation = TryCompile(new CapturingModels(AllKindsSignature), $$"""{ "x": "{{source}}" }""", batchSize: 2);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("pipeline:p", "/nodes/scorer/config/inputs/x", code, message), (error.Artifact, error.Pointer, error.Code, error.Message));
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    [Fact]
    public void AnIntColumnMappedToAFloat32Input_IsAMismatch_WithTheFix()
    {
        var models = new CapturingModels(new ModelSignature(new[] { new TensorSpec("id", TensorElementType.Float32, Rows) }, AllKindsSignature.Outputs));

        var compilation = TryCompile(models, """{ "id": "col.entity_id" }""", batchSize: 2);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("/nodes/scorer/config/inputs/id", "MODEL_TENSOR_MISMATCH"), (error.Pointer, error.Code));
        Assert.Equal("Model 'm' version 'default' declares input 'id' as float32 [-1], but it maps col.entity_id (int64), which ModelScorer@1 sends as int64 [-1].",
            error.Message);
        Assert.Equal("declare input 'id' as int64 [-1] in the model's signature, or map a float32 derived feature of col.entity_id instead "
            + "(one definition for training and serving, design 6.9)", error.Hint);
    }

    [Fact]
    public void AVariableLengthListInput_IsAnError_WhoseHintDeclaresTheLength()
    {
        var compilation = TryCompile(new CapturingModels(AllKindsSignature), """{ "x": "col.item.tags" }""", batchSize: 2);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal("/nodes/scorer/config/inputs/x", error.Pointer);
        Assert.StartsWith("declare the length of col.item.tags, e.g. list<int64,64>", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoolColumnMappedToAFloat32Input_IsAMismatch_WithTheFix()
    {
        // A bool goes to the model as a bool: not converted to a number of either kind.
        var models = new CapturingModels(new ModelSignature(new[] { new TensorSpec("safe", TensorElementType.Float32, Rows) }, AllKindsSignature.Outputs));

        var compilation = TryCompile(models, """{ "safe": "col.item.safe" }""", batchSize: 2);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("/nodes/scorer/config/inputs/safe", "MODEL_TENSOR_MISMATCH"), (error.Pointer, error.Code));
        Assert.Equal("Model 'm' version 'default' declares input 'safe' as float32 [-1], but it maps col.item.safe (bool), which ModelScorer@1 sends as bool [-1].",
            error.Message);
        Assert.Equal("declare input 'safe' as bool [-1] in the model's signature, or map a float32 derived feature of col.item.safe instead "
            + "(one definition for training and serving, design 6.9)", error.Hint);
    }

    [Fact]
    public async Task EachOutput_IsReadAsTheTypeOfItsColumn_AndWrittenAsReturned()
    {
        var signature = new ModelSignature(new[] { new TensorSpec("id", TensorElementType.Int64, Rows) }, new[]
        {
            new TensorSpec("p", TensorElementType.Float32, Rows), new TensorSpec("n", TensorElementType.Int64, Rows),
            new TensorSpec("label", TensorElementType.String, Rows), new TensorSpec("v", TensorElementType.Float32, RowsOf2),
            new TensorSpec("d", TensorElementType.Float64, Rows), new TensorSpec("odd", TensorElementType.Bool, Rows),
            new TensorSpec("r", TensorElementType.Int32, Rows),
        });
        var models = new CapturingModels(signature, (call, outputs) =>
        {
            var ids = call.Inputs.Span[0].Int64.Span;
            var n = call.BatchSize;
            outputs.GetValues<float>("p", n).Span.Fill(0.5f);
            var codes = outputs.GetValues<long>("n", n).Span;
            var labels = outputs.GetValues<string>("label", n).Span;
            var vectors = outputs.GetValues<float>("v", 2 * n).Span;
            var doubles = outputs.GetValues<double>("d", n).Span;
            var odd = outputs.GetValues<bool>("odd", n).Span;
            var ranks = outputs.GetValues<int>("r", n).Span;
            for (var r = 0; r < n; r++)
            {
                odd[r] = ids[r] % 2 == 1;
                ranks[r] = int.MinValue + (int)(ids[r] % 7);
                codes[r] = ids[r] + 1;
                labels[r] = "l" + ids[r].ToString(CultureInfo.InvariantCulture);
                vectors[2 * r] = ids[r] % 7;
                vectors[(2 * r) + 1] = -1;
                doubles[r] = 0.1 * (ids[r] % 3);
            }
        });
        var plan = Compile(models, """{ "id": "col.entity_id" }""", batchSize: 2,
            outputs: """{ "p": "score.p", "n": "score.n", "label": "score.label", "v": "score.v", "d": "score.d", "odd": "score.odd", "r": "score.r" }""",
            resultColumns: """{ "p": "col.score.p", "n": "col.score.n", "label": "col.score.label", "v": "col.score.v", "d": "col.score.d", "odd": "col.score.odd", "r": "col.score.r" }""");

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(5, result.Items.Count);
        foreach (var item in result.Items)
        {
            var id = item.Id.Int64Value;
            Assert.Equal(id + 1, item.Columns["n"].AsInt64()); // exact beyond 2^53: never through a float
            Assert.Equal("l" + id.ToString(CultureInfo.InvariantCulture), item.Columns["label"].AsString());
            Assert.Equal(new double[] { id % 7, -1 }, item.Columns["v"].AsFloat64List().ToArray());
            Assert.Equal(0.1 * (id % 3), item.Columns["d"].AsFloat64()); // a float64 output, not rounded through float32
            Assert.Equal(id % 2 == 1, item.Columns["odd"].AsBool());
            Assert.Equal(int.MinValue + (int)(id % 7), item.Columns["r"].AsInt32());
        }
    }

    /// <summary>
    /// An output with more elements than its shape holds fails the node: sliced by the column's width, its rows would start
    /// elsewhere than the client wrote them (a list&lt;float32,2&gt; column written 3 per row took the next row's values).
    /// </summary>
    [Theory]
    [InlineData("p", 1)]
    [InlineData("v", 2)]
    public async Task AnOutputLongerThanItsShape_FailsTheNode_NotSlicedIntoOtherRows(string output, int width)
    {
        var signature = new ModelSignature(new[] { new TensorSpec("id", TensorElementType.Int64, Rows) },
            new[] { new TensorSpec("p", TensorElementType.Float32, Rows), new TensorSpec("v", TensorElementType.Float32, RowsOf2) });
        var models = new CapturingModels(signature, (call, outputs) =>
        {
            outputs.GetValues<float>("p", call.BatchSize * (output == "p" ? 2 : 1)).Span.Fill(0.5f);
            outputs.GetValues<float>("v", call.BatchSize * (output == "v" ? 3 : 2)).Span.Fill(1f);
        });
        var plan = Compile(models, """{ "id": "col.entity_id" }""", batchSize: 8, outputs: """{ "p": "score.p", "v": "score.v" }""");

        var result = await Run(plan);

        Assert.False(result.Succeeded);
        var expected = 5 * width;
        Assert.Contains($"model 'm' returned {5 * (output == "p" ? 2 : 3)} values of output '{output}', but its shape holds {expected}", result.Error);
    }

    [Fact]
    public async Task AnOutputOfAnotherTypeThanItsSignatureDeclares_FailsTheNode_NamingBoth()
    {
        var models = new CapturingModels(AllKindsSignature, (call, outputs) => outputs.GetValues<double>("p", call.BatchSize).Span.Fill(0.5));
        var plan = Compile(models, AllKinds, batchSize: 8);

        var result = await Run(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("model 'm' returned output 'p' as float64, but it is written to 'score.p' (float32), which takes float32 as the signature declares", result.Error);
    }

    [Fact]
    public async Task AStringOutputWithAnElementLeftUnwritten_FailsTheNode()
    {
        var signature = new ModelSignature(new[] { new TensorSpec("id", TensorElementType.Int64, Rows) },
            new[] { new TensorSpec("p", TensorElementType.Float32, Rows), new TensorSpec("label", TensorElementType.String, Rows) });
        var models = new CapturingModels(signature, (call, outputs) =>
        {
            outputs.GetValues<float>("p", call.BatchSize).Span.Fill(0.5f);
            outputs.GetValues<string>("label", call.BatchSize).Span[..^1].Fill("x"); // the last row's label never written
        });
        var plan = Compile(models, """{ "id": "col.entity_id" }""", batchSize: 8,
            outputs: """{ "p": "score.p", "label": "score.label" }""", resultColumns: """{ "label": "col.score.label" }""");

        var result = await Run(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("model 'm' returned no value for some row of output 'label'", result.Error);
    }

    private static void AssertTensor(CapturedInput input, TensorElementType type, int rows, int width = 0)
    {
        Assert.Equal(type, input.ElementType);
        Assert.Equal(width == 0 ? new[] { rows } : new[] { rows, width }, input.Shape);
        Assert.Equal(rows * Math.Max(1, width), input.Values.Length);
    }

    private static ValueTask<PipelineResult> Run(ExecutionPlan plan) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r",
        Inputs = new RequestInputs().Set("user_emb", ScalarValue.FromFloat32List(UserEmb)).Set("user_id", Big).Set("surface", "home")
            .Set("member", ScalarValue.FromBool(true)).Set("recent", ScalarValue.FromInt64List(Recent)).Set("boost", ScalarValue.FromFloat32(0.1f))
            .Set("slot", int.MinValue).Set("slots", new long[] { int.MaxValue, int.MinValue }),
        Deadline = TimeSpan.FromSeconds(30),
    });

    private static ExecutionPlan Compile(CapturingModels models, string inputs, int batchSize, string? outputs = null, string? resultColumns = null)
    {
        var compilation = TryCompile(models, inputs, batchSize, outputs, resultColumns);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>
    /// A workspace of one pipeline (recall of the five items, then the scorer with <paramref name="inputs"/>) in a temp
    /// directory. The ids and int64 values above 2^53 live in data files: artifacts are RFC 8785 JSON, which cannot hold them.
    /// </summary>
    private static PlanCompilation TryCompile(CapturingModels models, string inputs, int batchSize, string? outputs = null, string? resultColumns = null)
    {
        outputs ??= """{ "p": "score.p" }""";
        resultColumns ??= """{ "p": "col.score.p" }""";
        var root = Directory.CreateTempSubdirectory("stratarec-model-scorer-").FullName;
        Write(root, "catalog/c.jsonc", """
            { "catalog_id": "c",
              "entities": { "item": { "key": "col.entity_id" }, "user": { "key": "ctx.request.user_id" } },
              "features": {
                "user.affinity":  { "type": "float32",        "source": "users", "field": "affinity", "default": 0.0 },
                "item.code":      { "type": "int64",          "source": "items", "field": "code", "default": 0 },
                "item.rank":      { "type": "int32",          "source": "items", "field": "rank", "default": 0 },
                "item.ctr":       { "type": "float64",        "source": "items", "field": "ctr",  "default": 0.0 },
                "item.safe":      { "type": "bool",           "source": "items", "field": "safe", "default": false },
                "item.emb":       { "type": "list<float32,3>", "source": "items", "field": "emb",  "default": [0, 0, 0] },
                "item.flags":     { "type": "list<bool,2>",    "source": "items", "field": "flags", "default": [false, false] },
                "item.ids":       { "type": "list<int32,2>",   "source": "items", "field": "ids", "default": [0, 0] },
                "item.weights":   { "type": "list<float64,2>", "source": "items", "field": "weights", "default": [0, 0] },
                "item.names":     { "type": "list<string,2>",  "source": "items", "field": "names", "default": ["", ""] },
                "item.maybe":     { "type": "float32",        "source": "items", "field": "maybe" },
                "item.maybe_emb": { "type": "list<float32,3>", "source": "items", "field": "maybe_emb" },
                "item.tag":       { "type": "string",         "source": "items", "field": "tag",   "default": "" },
                "item.tags":      { "type": "list<int64>",    "source": "items", "field": "tags",  "default": [] },
                "item.codes":     { "type": "list<int32>",    "source": "items", "field": "codes", "default": [] } } }
            """);
        Write(root, "bindings/local.jsonc", $$"""
            { "environment": "local",
              "sources": { "items": { "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": 30000 },
                           "users": { "connector": "memory@1", "data_file": "fixtures/users.json", "timeout_ms": 30000 } },
              "models": { "m": { "client": "{{CapturingModels.Type}}" } },
              "candidate_sources": { "s": { "client": "memory_list@1", "data_file": "fixtures/ids.json" } } }
            """);
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "request_schema": {
                "user_emb": { "type": "list<float32,3>" }, "user_id": { "type": "int64" }, "maybe_id": { "type": "int64?" }, "surface": { "type": "string" },
                "member": { "type": "bool" }, "recent": { "type": "list<int64,2>" }, "boost": { "type": "float32" }, "slot": { "type": "int32" },
                "slots": { "type": "list<int32,2>" } },
              "columns": { "score.p": { "type": "float32", "default": 0.0 }, "score.n": { "type": "int64", "default": 0 },
                           "score.label": { "type": "string", "default": "" }, "score.v": { "type": "list<float32,2>", "default": [0, 0] },
                           "score.d": { "type": "float64", "default": 0.0 }, "score.odd": { "type": "bool", "default": false },
                           "score.r": { "type": "int32", "default": 0 } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" }, "weight": { "type": "float64" } },
              "params": { "quotas": { "s": 10 }, "k": 10, "weight": 0.75 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "scorer": { "order": 200, "op": "ModelScorer@1", "timeout_ms": 30000,
                            "config": { "model": "m", "inputs": {{inputs}}, "outputs": {{outputs}}, "batch_size": {{batchSize}} } } },
              "output": { "limit_param": "k", "columns": {{resultColumns}} } }
            """);
        Write(root, "fixtures/ids.json", "{ \"items\": [" + string.Join(", ", Items.Keys) + "] }");
        Write(root, "fixtures/users.json", "{ \"" + Big.ToString(CultureInfo.InvariantCulture) + "\": { \"affinity\": 0.1 } }");
        Write(root, "fixtures/items.json", "{ " + string.Join(", ", Items.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"\"{p.Key}\": {{ \"code\": {p.Value.Code}, \"rank\": {p.Value.Rank}, \"ctr\": {p.Value.Ctr:R}, \"safe\": {(p.Value.Safe ? "true" : "false")}, "
            + $"\"emb\": [{string.Join(", ", p.Value.Emb.Select(f => f.ToString("R", CultureInfo.InvariantCulture)))}], "
            + $"\"flags\": [{string.Join(", ", p.Value.Flags.Select(f => f ? "true" : "false"))}], \"ids\": [{string.Join(", ", p.Value.Ids)}], "
            + $"\"weights\": [{string.Join(", ", p.Value.Weights.Select(w => w.ToString("R", CultureInfo.InvariantCulture)))}], "
            + $"\"tag\": \"t{p.Key}\", \"names\": [\"a{p.Key}\", \"b\"] }}"))) + " }");

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

    /// <summary>One input as the client received it, copied: the scorer's buffers go back to the pool after the request.</summary>
    private sealed record CapturedInput(string Name, TensorElementType ElementType, int[] Shape, Array Values)
    {
        public T[] Of<T>() => (T[])Values;
    }

    private sealed record CapturedCall(int BatchSize, IReadOnlyList<CapturedInput> Inputs)
    {
        public CapturedInput this[string name] => Inputs.Single(i => i.Name == name);
    }

    /// <summary>
    /// <c>capture@1</c>: declares a fixed signature, keeps a copy of every call, and scores every row 0.5 (or writes what
    /// <paramref name="write"/> writes).
    /// </summary>
    private sealed class CapturingModels(ModelSignature signature, Action<ModelCall, IModelOutputWriter>? write = null) : IModelClientFactory, IModelClient
    {
        public const string Type = "capture@1";

        private readonly List<CapturedCall> _calls = new();

        public string ClientType => Type;

        public Type SettingsType => typeof(NoSettings);

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

        public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) => ValueTask.FromResult(signature);

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
        {
            var inputs = new List<CapturedInput>();
            foreach (var input in call.Inputs.Span)
            {
                var values = input.Values;
                Array copy = values.ElementType switch
                {
                    TensorElementType.Bool => values.Bool.ToArray(),
                    TensorElementType.Int32 => values.Int32.ToArray(),
                    TensorElementType.Int64 => values.Int64.ToArray(),
                    TensorElementType.Float32 => values.Float32.ToArray(),
                    TensorElementType.Float64 => values.Float64.ToArray(),
                    _ => values.String.ToArray(),
                };
                inputs.Add(new CapturedInput(input.Name, input.ElementType, (int[])input.Shape.Clone(), copy));
            }

            lock (_calls)
            {
                _calls.Add(new CapturedCall(call.BatchSize, inputs));
            }

            if (write is null)
            {
                outputs.GetValues<float>("p", call.BatchSize).Span.Fill(0.5f);
            }
            else
            {
                write(call, outputs);
            }

            return ValueTask.FromResult(ModelStatus.Ok);
        }
    }
}
