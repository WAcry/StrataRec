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
/// What <c>ModelScorer@1</c> sends (design 6.9): each input as the type of its column or context value gives it, gathered
/// from the column's storage (int64 values exact, beyond 2^53 too) or broadcast from the request, and sliced into batches;
/// and which inputs it refuses to compile.
/// </summary>
public sealed class ModelScorerTests
{
    /// <summary>2^53 + 1: the first integer a double (and a float32) cannot hold.</summary>
    private const long Big = 9007199254740993L;

    private static readonly int[] Rows = { -1 };

    private static readonly int[] RowsOf3 = { -1, 3 };

    /// <summary>The fixture's items by id: an int64 above 2^53, an int32, a float64, a bool and a 3-vector each.</summary>
    private static readonly Dictionary<long, (long Code, int Rank, double Ctr, bool Safe, float[] Emb)> Items = new()
    {
        [Big] = (Big + 2, 7, 0.1, true, new[] { 1f, 2f, 3f }),
        [Big - 1] = (-Big, -7, 1e-40, false, new[] { 4f, 5f, 6f }),
        [3] = (long.MaxValue, int.MaxValue, 0.30000000000000004, true, new[] { 7f, 8f, 9f }),
        [4] = (long.MinValue, int.MinValue, -2.5, false, new[] { -1f, float.Epsilon, 1e30f }),
        [5] = (0, 0, 0, true, new[] { 0.1f, 0.2f, 0.3f }),
    };

    /// <summary>The scorer's inputs in the execution tests, and what the model declares for them.</summary>
    private const string AllKinds = """
        { "id": "col.entity_id", "code": "col.item.code", "rank": "col.item.rank", "ctr": "col.item.ctr", "safe": "col.item.safe",
          "emb": "col.item.emb", "user_emb": "ctx.request.user_emb", "user_id": "ctx.request.user_id", "weight": "param.weight" }
        """;

    private static readonly ModelSignature AllKindsSignature = new(
        new[]
        {
            new TensorSpec("id", TensorElementType.Int64, Rows), new TensorSpec("code", TensorElementType.Int64, Rows),
            new TensorSpec("rank", TensorElementType.Int64, Rows), new TensorSpec("ctr", TensorElementType.Float32, Rows),
            new TensorSpec("safe", TensorElementType.Float32, Rows), new TensorSpec("emb", TensorElementType.Float32, RowsOf3),
            new TensorSpec("user_emb", TensorElementType.Float32, RowsOf3), new TensorSpec("user_id", TensorElementType.Int64, Rows),
            new TensorSpec("weight", TensorElementType.Float32, Rows),
        },
        new[] { new TensorSpec("p", TensorElementType.Float32, Rows) });

    private static readonly float[] UserEmb = { 0.5f, -0.25f, 8f };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(256)]
    [InlineData(int.MaxValue)] // one batch: the batch count must not overflow (review 27)
    public async Task EachInput_IsSentAsItsTypeGivesIt_WithEveryRowsValue_InBatchesOfBatchSize(int batchSize)
    {
        var models = new CapturingModels(AllKindsSignature);
        var plan = Compile(models, AllKinds, batchSize);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(5, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(0.5, item.Columns["p"].AsFloat64())); // every batch's output was written back
        var calls = models.Calls;
        var expectedSizes = Enumerable.Range(0, Batching.Count(5, batchSize)).Select(b => Math.Min(batchSize, 5 - (b * batchSize)));
        Assert.Equal(expectedSizes.Order(), calls.Select(c => c.BatchSize).Order());
        var seen = new List<long>();
        foreach (var call in calls)
        {
            var n = call.BatchSize;
            Assert.Equal(AllKindsSignature.Inputs.Select(s => s.Name).Order(StringComparer.Ordinal), call.Inputs.Select(i => i.Name)); // in ordinal order
            AssertTensor(call["id"], TensorElementType.Int64, n);
            AssertTensor(call["code"], TensorElementType.Int64, n);
            AssertTensor(call["rank"], TensorElementType.Int64, n);
            AssertTensor(call["ctr"], TensorElementType.Float32, n);
            AssertTensor(call["safe"], TensorElementType.Float32, n);
            AssertTensor(call["emb"], TensorElementType.Float32, n, 3);
            AssertTensor(call["user_emb"], TensorElementType.Float32, n, 3);
            AssertTensor(call["user_id"], TensorElementType.Int64, n);
            AssertTensor(call["weight"], TensorElementType.Float32, n);
            for (var r = 0; r < n; r++)
            {
                var id = call["id"].Int64[r];
                var item = Items[id];
                seen.Add(id);
                Assert.Equal(item.Code, call["code"].Int64[r]); // exact: no detour through double
                Assert.Equal(item.Rank, call["rank"].Int64[r]);
                Assert.Equal((float)item.Ctr, call["ctr"].Float32[r]);
                Assert.Equal(item.Safe ? 1f : 0f, call["safe"].Float32[r]);
                Assert.Equal(item.Emb, call["emb"].Float32.AsSpan(r * 3, 3).ToArray()); // row-major
                Assert.Equal(UserEmb, call["user_emb"].Float32.AsSpan(r * 3, 3).ToArray()); // the request's vector in every row
                Assert.Equal(Big, call["user_id"].Int64[r]);
                Assert.Equal(0.75f, call["weight"].Float32[r]);
            }
        }

        Assert.Equal(Items.Keys.Order(), seen.Order()); // each row once
        Assert.Contains(Big, seen);
    }

    [Theory]
    [InlineData("col.item.maybe", "MODEL_INPUT_TYPE", "Model input 'x' maps col.item.maybe, which is nullable (float32?): a model needs a value in every row.")]
    [InlineData("ctx.request.maybe_id", "MODEL_INPUT_TYPE", "Model input 'x' maps ctx.request.maybe_id, which is nullable (int64?): a model needs a value in every row.")]
    [InlineData("col.item.maybe_emb", "MODEL_INPUT_TYPE", "Model input 'x' maps col.item.maybe_emb, which is nullable (vec<float32,3>?): a model needs a value in every row.")]
    [InlineData("col.item.tag", "MODEL_INPUT_TYPE", "Model input 'x' maps col.item.tag (string), which ModelScorer@1 cannot send as a tensor.")]
    [InlineData("ctx.request.surface", "MODEL_INPUT_TYPE", "Model input 'x' maps ctx.request.surface (string), which ModelScorer@1 cannot send as a tensor.")]
    [InlineData("col.item.tags", "MODEL_INPUT_TYPE", "Model input 'x' maps col.item.tags (list<int64>), which ModelScorer@1 cannot send as a tensor.")]
    [InlineData("col.item.codes", "MODEL_INPUT_TYPE", "Model input 'x' maps col.item.codes (list<int32>), which ModelScorer@1 cannot send as a tensor.")]
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

    private static void AssertTensor(CapturedInput input, TensorElementType type, int rows, int width = 0)
    {
        Assert.Equal(type, input.ElementType);
        Assert.Equal(width == 0 ? new[] { rows } : new[] { rows, width }, input.Shape);
        Assert.Equal(type == TensorElementType.Float32 ? rows * Math.Max(1, width) : 0, input.Float32.Length);
        Assert.Equal(type == TensorElementType.Int64 ? rows : 0, input.Int64.Length);
    }

    private static ValueTask<PipelineResult> Run(ExecutionPlan plan) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r",
        Inputs = new RequestInputs().Set("user_emb", ScalarValue.FromVector(UserEmb)).Set("user_id", Big).Set("surface", "home"),
        Deadline = TimeSpan.FromSeconds(30),
    });

    private static ExecutionPlan Compile(CapturingModels models, string inputs, int batchSize)
    {
        var compilation = TryCompile(models, inputs, batchSize);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>
    /// A workspace of one pipeline (recall of the five items, then the scorer with <paramref name="inputs"/>) in a temp
    /// directory. The ids and int64 values above 2^53 live in data files: artifacts are RFC 8785 JSON, which cannot hold them.
    /// </summary>
    private static PlanCompilation TryCompile(CapturingModels models, string inputs, int batchSize)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-model-scorer-").FullName;
        Write(root, "catalog/c.jsonc", """
            { "catalog_id": "c",
              "entities": { "item": { "scope": "candidate", "key": "col.entity_id", "key_type": "int64" } },
              "features": {
                "item.code":      { "type": "int64",          "source": "items", "field": "code", "default": 0 },
                "item.rank":      { "type": "int32",          "source": "items", "field": "rank", "default": 0 },
                "item.ctr":       { "type": "float64",        "source": "items", "field": "ctr",  "default": 0.0 },
                "item.safe":      { "type": "bool",           "source": "items", "field": "safe", "default": false },
                "item.emb":       { "type": "vec<float32,3>", "source": "items", "field": "emb",  "default": [0, 0, 0] },
                "item.maybe":     { "type": "float32",        "source": "items", "field": "maybe" },
                "item.maybe_emb": { "type": "vec<float32,3>", "source": "items", "field": "maybe_emb" },
                "item.tag":       { "type": "string",         "source": "items", "field": "tag",   "default": "" },
                "item.tags":      { "type": "list<int64>",    "source": "items", "field": "tags",  "default": [] },
                "item.codes":     { "type": "list<int32>",    "source": "items", "field": "codes", "default": [] } } }
            """);
        Write(root, "bindings/local.jsonc", $$"""
            { "environment": "local",
              "sources": { "items": { "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": 30000 } },
              "models": { "m": { "client": "{{CapturingModels.Type}}" } },
              "candidate_sources": { "s": { "client": "memory_list@1", "data_file": "fixtures/ids.json" } } }
            """);
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "request_schema": {
                "user_emb": { "type": "vec<float32,3>" }, "user_id": { "type": "int64" }, "maybe_id": { "type": "int64?" }, "surface": { "type": "string" } },
              "columns": { "score.p": { "type": "float32", "default": 0.0 } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" }, "weight": { "type": "float64" } },
              "params": { "quotas": { "s": 10 }, "k": 10, "weight": 0.75 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "scorer": { "order": 200, "op": "ModelScorer@1", "timeout_ms": 30000,
                            "config": { "model": "m", "inputs": {{inputs}}, "outputs": { "p": "score.p" }, "batch_size": {{batchSize}} } } },
              "output": { "limit_param": "k", "columns": { "p": "col.score.p" } } }
            """);
        Write(root, "fixtures/ids.json", "{ \"items\": [" + string.Join(", ", Items.Keys) + "] }");
        Write(root, "fixtures/items.json", "{ " + string.Join(", ", Items.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"\"{p.Key}\": {{ \"code\": {p.Value.Code}, \"rank\": {p.Value.Rank}, \"ctr\": {p.Value.Ctr:R}, \"safe\": {(p.Value.Safe ? "true" : "false")}, "
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

    /// <summary>One input as the client received it, copied: the scorer's buffers go back to the pool after the request.</summary>
    private sealed record CapturedInput(string Name, TensorElementType ElementType, int[] Shape, float[] Float32, long[] Int64);

    private sealed record CapturedCall(int BatchSize, IReadOnlyList<CapturedInput> Inputs)
    {
        public CapturedInput this[string name] => Inputs.Single(i => i.Name == name);
    }

    /// <summary><c>capture@1</c>: declares a fixed signature, keeps a copy of every call and scores every row 0.5.</summary>
    private sealed class CapturingModels(ModelSignature signature) : IModelClientFactory, IModelClient
    {
        public const string Type = "capture@1";

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

        public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) => ValueTask.FromResult(signature);

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
        {
            var inputs = new List<CapturedInput>();
            foreach (var input in call.Inputs.Span)
            {
                inputs.Add(new CapturedInput(input.Name, input.ElementType, (int[])input.Shape.Clone(), input.Float32.ToArray(), input.Int64.ToArray()));
            }

            lock (_calls)
            {
                _calls.Add(new CapturedCall(call.BatchSize, inputs));
            }

            outputs.GetFloat32("p", call.BatchSize).Span.Fill(0.5f);
            return ValueTask.FromResult(ModelStatus.Ok);
        }
    }
}
