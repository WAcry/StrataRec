using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Llm;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Llm;

/// <summary>
/// A workspace of one pipeline in a temp directory for the generative operators: a recall of <see cref="Incoming"/>
/// (int64 keys) or of string keys, then the node <c>rank</c> with the operator under test, bound to a capturing text
/// model (<see cref="TextModels"/>). The catalog has a feature of every kind a template may name, nullable ones included.
/// </summary>
internal static class LlmFixture
{
    /// <summary>The order the recall leaves the rows in: what the reranker sees.</summary>
    public static readonly long[] Incoming = { 30, 10, 50, 20, 40, 70, 60 };

    public static readonly string[] StringKeys = { "sku-c", "sku-a", "sku-e", "sku-b", "sku-d" };

    /// <summary>
    /// The items: title (string), blurb (text?, missing for 20 and 40), price (float64), score (float32), stock (int64),
    /// fresh (bool), tags (list&lt;string&gt;?, missing for 50), codes (list&lt;int64&gt;), rating (float32?, missing for
    /// 10), emb (vec&lt;float32,2&gt;), extra (string, named only by some templates).
    /// </summary>
    private const string Items = """
        {
          "10": { "title": "Red \"shoes\"", "blurb": "Bright & bold", "price": 19.99, "score": 0.1, "stock": 3, "fresh": true, "tags": ["red", "shoe"], "codes": [7, -9007199254740993], "emb": [1, 2], "extra": "x10" },
          "20": { "title": "Blue hat", "price": 5, "score": 0.25, "stock": 0, "fresh": false, "tags": [], "codes": [], "rating": 4.5, "emb": [1, 2], "extra": "x20" },
          "30": { "title": "Green\nscarf", "blurb": "Warm", "price": 1e-7, "score": 3.4028235e38, "stock": -12, "fresh": true, "tags": ["green"], "codes": [1], "rating": 0.1, "emb": [1, 2], "extra": "x30" },
          "40": { "title": "Tea", "price": 2.5, "score": 1, "stock": 9007199254740993, "fresh": false, "tags": ["drink"], "codes": [2, 3], "rating": 3, "emb": [1, 2], "extra": "x40" },
          "50": { "title": "Mug", "blurb": "", "price": 0.30000000000000004, "score": -0.5, "stock": 1, "fresh": true, "codes": [4], "rating": -1.25, "emb": [1, 2], "extra": "x50" },
          "60": { "title": "Lamp", "blurb": "Glows", "price": 30, "score": 2, "stock": 2, "fresh": false, "tags": ["light"], "codes": [5], "rating": 2, "emb": [1, 2], "extra": "x60" },
          "70": { "title": "Desk", "blurb": "Oak", "price": 120, "score": 7, "stock": 1, "fresh": true, "tags": ["wood"], "codes": [6], "rating": 5, "emb": [1, 2], "extra": "x70" }
        }
        """;

    public const string AllowedVersions = "\"llm_version\": { \"type\": \"string\", \"enum\": [\"v1\", \"v2\"] }";

    /// <summary>The default request: a user with a name, a query, a budget (float32), vip, interests and a nullable nickname missing.</summary>
    public static RequestInputs Inputs() => new RequestInputs()
        .Set("user_id", 7L)
        .Set("query", "gifts for \"dad\"")
        .Set("budget", ScalarValue.FromFloat64(0.1f))
        .Set("vip", true)
        .Set("interests", ScalarValue.FromStringList(new[] { "tea", "lamps" }))
        .Set("ids", ScalarValue.FromInt64List(new[] { 3L, -4L }));

    /// <summary>
    /// Compiles the workspace with node <c>rank</c> running <paramref name="op"/> with <paramref name="config"/>; returns
    /// the compilation (check <see cref="PlanCompilation.Succeeded"/>).
    /// </summary>
    public static PlanCompilation TryCompile(
        TextModels models, string config, string op = "LlmReranker@1", string onFailure = "fail_request", bool stringKeys = false,
        OperatorRegistry? operators = null, long[]? recall = null)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-llm-").FullName;
        Write(root, "catalog/c.jsonc", $$"""
            { "catalog_id": "c",
              "entities": {
                "item": { "scope": "candidate", "key": "col.entity_id", "key_type": "{{(stringKeys ? "string" : "int64")}}" },
                "user": { "scope": "context", "key": "ctx.request.user_id", "key_type": "int64" } },
              "features": {
                "item.title":  { "type": "string",         "source": "items", "field": "title",  "default": "" },
                "item.blurb":  { "type": "text",           "source": "items", "field": "blurb" },
                "item.price":  { "type": "float64",        "source": "items", "field": "price",  "default": 0.0 },
                "item.score":  { "type": "float32",        "source": "items", "field": "score",  "default": 0.0 },
                "item.stock":  { "type": "int64",          "source": "items", "field": "stock",  "default": 0 },
                "item.fresh":  { "type": "bool",           "source": "items", "field": "fresh",  "default": false },
                "item.tags":   { "type": "list<string>",   "source": "items", "field": "tags" },
                "item.codes":  { "type": "list<int64>",    "source": "items", "field": "codes",  "default": [] },
                "item.rating": { "type": "float32",        "source": "items", "field": "rating" },
                "item.emb":    { "type": "vec<float32,2>", "source": "items", "field": "emb",    "default": [0, 0] },
                "item.extra":  { "type": "string",         "source": "items", "field": "extra",  "default": "" },
                "user.name":   { "type": "string",         "source": "users", "field": "name" },
                "user.tier":   { "type": "int64",          "source": "users", "field": "tier",   "default": 0 } } }
            """);
        Write(root, "bindings/local.jsonc", $$"""
            { "environment": "local",
              "sources": {
                "items": { "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": 30000 },
                "users": { "connector": "memory@1", "data_file": "fixtures/users.json", "timeout_ms": 30000 } },
              "models": { "llm": { "client": "{{TextModels.Type}}" } },
              "candidate_sources": { "s": { "client": "memory_list@1", "items": [{{(stringKeys ? string.Join(", ", StringKeys.Select(k => $"\"{k}\"")) : string.Join(", ", recall ?? Incoming))}}] } } }
            """);
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "{{(stringKeys ? "string" : "int64")}}",
              "request_schema": {
                "user_id": { "type": "int64" }, "query": { "type": "string" }, "budget": { "type": "float32" }, "vip": { "type": "bool" },
                "interests": { "type": "list<string>" }, "ids": { "type": "list<int64>" }, "nickname": { "type": "string?" } },
              "param_schema": {
                "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" }, "tone": { "type": "string" }, "boost": { "type": "float64" },
                {{AllowedVersions}} },
              "params": { "quotas": { "s": 100 }, "k": 100, "tone": "friendly", "boost": 1.5, "llm_version": "v1" },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "rank": { "order": 200, "op": "{{op}}", "timeout_ms": 30000, "on_failure": "{{onFailure}}", "config": {{config}} } },
              "output": { "limit_param": "k", "columns": { "title": "col.item.title" } } }
            """);
        Write(root, "fixtures/items.json", stringKeys
            ? """{ "sku-a": { "title": "A" }, "sku-b": { "title": "B" }, "sku-c": { "title": "C" }, "sku-d": { "title": "D" }, "sku-e": { "title": "E" } }"""
            : Items);
        Write(root, "fixtures/users.json", """{ "7": { "name": "Ada", "tier": 2 } }""");

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return PlanCompiler.Compile(request!, new PlanEnvironment
        {
            BaseDirectory = root,
            Models = new ModelClientRegistry().Add(models),
            Operators = operators ?? OperatorRegistry.CreateDefault().AddLlm(),
        });
    }

    public static ExecutionPlan Compile(TextModels models, string config, string onFailure = "fail_request", bool stringKeys = false, OperatorRegistry? operators = null,
        string op = "LlmReranker@1", long[]? recall = null)
    {
        var compilation = TryCompile(models, config, op, onFailure, stringKeys, operators, recall);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>The single error of a compilation that must fail.</summary>
    public static ConfigDiagnostic Error(PlanCompilation compilation)
    {
        Assert.False(compilation.Succeeded);
        return Assert.Single(compilation.Diagnostics, d => d.IsError);
    }

    public static ValueTask<PipelineResult> Run(ExecutionPlan plan, RequestInputs? inputs = null, bool record = false, long seed = 1234) =>
        new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = inputs ?? Inputs(),
            Deadline = TimeSpan.FromSeconds(30),
            Seed = seed,
            Record = record,
        });

    public static long[] IdsOf(PipelineResult result) => result.Items.Select(i => i.Id.Int64Value).ToArray();

    /// <summary>An <c>LlmReranker@1</c> config with one prompt, <c>user</c>; <paramref name="extra"/> adds keys (start it with a comma).</summary>
    public static string Rerank(string template, string answer = "rank", int window = 3, string extra = "") =>
        $$"""{ "model": "llm", "output": "answer", "answer": "{{answer}}", "window": {{window}}, "prompts": { "user": {{System.Text.Json.JsonSerializer.Serialize(template)}} }{{extra}} }""";

    /// <summary>The outcome of node <c>rank</c>.</summary>
    public static NodeOutcome Rank(PipelineResult result) => Assert.Single(result.Outcomes, o => o.NodeId == "rank");

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}

/// <summary>A call as <see cref="TextModels"/> received it.</summary>
internal sealed record TextCall(string? Version, int BatchSize, long? Seed, int? MaxOutputTokens, IReadOnlyDictionary<string, string> Prompts);

/// <summary>
/// <c>text_test@1</c>: declares a signature per version (text inputs and output by default), keeps every call, and
/// answers with what <c>answer</c> returns: the text of output <c>answer</c> (none when null) and the status.
/// </summary>
internal sealed class TextModels(Func<TextCall, (string? Text, ModelStatus Status)> answer, Func<string?, ModelSignature>? signature = null)
    : IModelClientFactory, IModelClient
{
    public const string Type = "text_test@1";

    private readonly List<TextCall> _calls = new();

    public TextModels(string text)
        : this(_ => (text, ModelStatus.Ok))
    {
    }

    public string ClientType => Type;

    public List<TextCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToList();
            }
        }
    }

    public static ModelSignature Text(params string[] inputs) => new(
        inputs.Select(n => new TensorSpec(n, TensorElementType.Text, Array.Empty<int>())).ToArray(),
        new[] { new TensorSpec("answer", TensorElementType.Text, Array.Empty<int>()) });

    public IModelClient Create(BindingContext context) => this;

    public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
        ValueTask.FromResult(signature?.Invoke(version) ?? Text("user"));

    public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
    {
        var prompts = new Dictionary<string, string>();
        foreach (var input in call.Inputs.Span)
        {
            prompts[input.Name] = input.ElementType == TensorElementType.Text ? input.Text! : $"<{input.ElementType}>";
        }

        var captured = new TextCall(call.Version, call.BatchSize, call.Options.Seed, call.Options.MaxOutputTokens, prompts);
        lock (_calls)
        {
            _calls.Add(captured);
        }

        var (text, status) = answer(captured);
        if (text is not null)
        {
            outputs.SetText("answer", text);
        }

        return ValueTask.FromResult(status);
    }
}
