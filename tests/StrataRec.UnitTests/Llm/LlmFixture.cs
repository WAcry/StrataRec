using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Llm;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Llm;

internal static class LlmFixture
{
    public static readonly long[] Incoming = { 30, 10, 50, 20, 40, 70, 60 };

    public static readonly string[] StringKeys = { "sku-c", "sku-a", "sku-e", "sku-b", "sku-d" };

    private const string Items = """
        {
          "10": { "title": "Red \"shoes\"", "blurb": "Bright & bold", "price": 19.99, "score": 0.1, "stock": 3, "fresh": true, "tags": ["red", "shoe"], "codes": [7, -9007199254740993], "emb": [1, 2], "extra": "x10", "weights": [0.1, -2.5], "clicks": [true, false] },
          "20": { "title": "Blue hat", "price": 5, "score": 0.25, "stock": 0, "fresh": false, "tags": [], "codes": [], "rating": 4.5, "emb": [1, 2], "extra": "x20" },
          "30": { "title": "Green\nscarf", "blurb": "Warm", "price": 1e-7, "score": 3.4028235e38, "stock": -12, "fresh": true, "tags": ["green"], "codes": [1], "rating": 0.1, "emb": [1, 2], "extra": "x30" },
          "40": { "title": "Tea", "price": 2.5, "score": 1, "stock": 9007199254740993, "fresh": false, "tags": ["drink"], "codes": [2, 3], "rating": 3, "emb": [1, 2], "extra": "x40" },
          "50": { "title": "Mug", "blurb": "", "price": 0.30000000000000004, "score": -0.5, "stock": 1, "fresh": true, "codes": [4], "rating": -1.25, "emb": [1, 2], "extra": "x50" },
          "60": { "title": "Lamp", "blurb": "Glows", "price": 30, "score": 2, "stock": 2, "fresh": false, "tags": ["light"], "codes": [5], "rating": 2, "emb": [1, 2], "extra": "x60" },
          "70": { "title": "Desk", "blurb": "Oak", "price": 120, "score": 7, "stock": 1, "fresh": true, "tags": ["wood"], "codes": [6], "rating": 5, "emb": [1, 2], "extra": "x70" }
        }
        """;

    public const string AllowedVersions = "\"llm_version\": { \"type\": \"string\", \"enum\": [\"v1\", \"v2\"] }";

    public const string DefaultOutput = "\"title\": \"col.item.title\"";

    public const string NullableRecallColumns = """
        "recall.sources_mask": { "type": "int64?" }, "recall.best_rank": { "type": "int64?" },
        "recall.best_score": { "type": "float64?" }, "recall.primary_source": { "type": "string?" }
        """;

    public static RequestInputs Inputs() => new RequestInputs()
        .Set("user_id", 7L)
        .Set("query", "gifts for \"dad\"")
        .Set("budget", ScalarValue.FromFloat64(0.1f))
        .Set("vip", true)
        .Set("interests", ScalarValue.FromStringList(new[] { "tea", "lamps" }))
        .Set("ids", ScalarValue.FromInt64List(new[] { 3L, -4L }))
        .Set("weights", new[] { 0.1, 1e-7 })
        .Set("flags", new[] { false, true });

    public static PlanCompilation TryCompile(
        TextModels models, string config, string op = "LlmReranker@1", string onFailure = "fail_request", bool stringKeys = false,
        OperatorRegistry? operators = null, long[]? recall = null, string columns = "", string output = DefaultOutput, string nodes = "")
    {
        var root = Directory.CreateTempSubdirectory("stratarec-llm-").FullName;
        Write(root, "catalog/c.jsonc", $$"""
            { "catalog_id": "c",
              "entities": {
                "item": { "key": "col.entity_id" },
                "user": { "key": "ctx.request.user_id" } },
              "features": {
                "item.title":  { "type": "string",         "source": "items", "field": "title",  "default": "" },
                "item.blurb":  { "type": "string",         "source": "items", "field": "blurb" },
                "item.price":  { "type": "float64",        "source": "items", "field": "price",  "default": 0.0 },
                "item.score":  { "type": "float32",        "source": "items", "field": "score",  "default": 0.0 },
                "item.stock":  { "type": "int64",          "source": "items", "field": "stock",  "default": 0 },
                "item.fresh":  { "type": "bool",           "source": "items", "field": "fresh",  "default": false },
                "item.tags":   { "type": "list<string>",   "source": "items", "field": "tags" },
                "item.codes":  { "type": "list<int64>",    "source": "items", "field": "codes",  "default": [] },
                "item.rating": { "type": "float32",        "source": "items", "field": "rating" },
                "item.emb":    { "type": "list<float32,2>", "source": "items", "field": "emb",    "default": [0, 0] },
                "item.extra":  { "type": "string",         "source": "items", "field": "extra",  "default": "" },
                "item.weights": { "type": "list<float32>", "source": "items", "field": "weights" },
                "item.clicks": { "type": "list<bool>",     "source": "items", "field": "clicks" },
                "user.name":   { "type": "string",         "source": "users", "field": "name" },
                "user.tier":   { "type": "int64",          "source": "users", "field": "tier",   "default": 0 } } }
            """);
        Write(root, "bindings/local.jsonc", $$"""
            { "environment": "local",
              "sources": {
                "items": { "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": 30000 },
                "users": { "connector": "memory@1", "data_file": "fixtures/users.json", "timeout_ms": 30000 } },
              "services": { "llm": { "client": "{{TextModels.TypeName}}" }, "s": { "client": "memory_list@1", "items": [{{(stringKeys ? string.Join(", ", StringKeys.Select(k => $"\"{k}\"")) : string.Join(", ", recall ?? Incoming))}}] } } }
            """);
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "{{(stringKeys ? "string" : "int64")}}",
              "request_schema": {
                "user_id": { "type": "int64" }, "query": { "type": "string" }, "budget": { "type": "float32" }, "vip": { "type": "bool" },
                "interests": { "type": "list<string>" }, "ids": { "type": "list<int64>" }, "nickname": { "type": "string?" },
                "weights": { "type": "list<float32>?" }, "flags": { "type": "list<bool>?" } },
              "param_schema": {
                "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" }, "tone": { "type": "string" }, "boost": { "type": "float64" },
                {{AllowedVersions}} },
              "params": { "quotas": { "s": 100 }, "k": 100, "tone": "friendly", "boost": 1.5, "llm_version": "v1" },
              "columns": { {{columns}} },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "rank": { "order": 200, "op": "{{op}}", "timeout_ms": 30000, "on_failure": "{{onFailure}}", "config": {{config}} }{{(nodes.Length == 0 ? "" : ", " + nodes)}} },
              "output": { "limit_param": "k", "columns": { {{output}} } } }
            """);
        Write(root, "fixtures/items.json", stringKeys
            ? """{ "sku-a": { "title": "A" }, "sku-b": { "title": "B" }, "sku-c": { "title": "C" }, "sku-d": { "title": "D" }, "sku-e": { "title": "E" }, "sku-f": { "title": "F" } }"""
            : Items);
        Write(root, "fixtures/users.json", """{ "7": { "name": "Ada", "tier": 2 } }""");

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return PlanCompiler.Compile(request!, new PlanEnvironment
        {
            BaseDirectory = root,
            Bindings = PlanEnvironment.DefaultBindings().Add(models),
            Operators = operators ?? OperatorRegistry.CreateDefault().AddLlm(),
        });
    }

    public static ExecutionPlan Compile(TextModels models, string config, string onFailure = "fail_request", bool stringKeys = false, OperatorRegistry? operators = null,
        string op = "LlmReranker@1", long[]? recall = null, string columns = "", string output = DefaultOutput, string nodes = "")
    {
        var compilation = TryCompile(models, config, op, onFailure, stringKeys, operators, recall, columns, output, nodes);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

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

    /// <summary><paramref name="extra"/> adds keys (start it with a comma).</summary>
    public static string Rerank(string template, string answer = "rank", int window = 3, string extra = "") =>
        $$"""{ "model": "llm", "output": "answer", "answer": "{{answer}}", "window": {{window}}, "prompts": { "user": {{System.Text.Json.JsonSerializer.Serialize(template)}} }{{extra}} }""";

    /// <summary><paramref name="extra"/> adds keys (start it with a comma).</summary>
    public static string Annotate(string template, int window = 3, string extra = "") =>
        $$"""{ "model": "llm", "output": "answer", "column": "llm.note", "window": {{window}}, "prompts": { "user": {{System.Text.Json.JsonSerializer.Serialize(template)}} }{{extra}} }""";

    /// <summary><paramref name="extra"/> adds keys (start it with a comma).</summary>
    public static string Retrieve(string template, int maxItems = 3, string extra = "") =>
        $$"""{ "model": "llm", "output": "answer", "max_items": {{maxItems}}, "prompts": { "user": {{System.Text.Json.JsonSerializer.Serialize(template)}} }{{extra}} }""";

    public static async Task<(PipelineResult Result, RunReplay Replay)> Replay(ExecutionPlan plan, PipelineResult recorded, RequestInputs? inputs = null)
    {
        var replay = RunReplay.Create(recorded.Recording!, "/runs/0");
        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = inputs ?? Inputs(),
            Deadline = RunReplay.Deadline,
            Seed = 1234,
            Replay = replay,
        });
        return (result, replay);
    }

    public static Dictionary<long, object?> Values(PipelineResult result, string column) =>
        result.Items.ToDictionary(i => i.Id.Int64Value, i => Value(i.Columns[column]));

    public static object? Value(ScalarValue value) => value.Kind switch
    {
        0 => null,
        StorageKind.Bool => value.AsBool(),
        StorageKind.Int64 => value.AsInt64(),
        StorageKind.Float64 => value.AsFloat64(),
        _ => value.AsString(),
    };

    public static NodeOutcome Rank(PipelineResult result) => Assert.Single(result.Outcomes, o => o.NodeId == "rank");

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}

internal sealed record TextCall(string? Version, int BatchSize, long? Seed, int? MaxOutputTokens, IReadOnlyDictionary<string, string> Prompts);

internal sealed class TextModels(Func<TextCall, (string? Text, ServiceStatus Status)> answer, Func<string?, ServiceSignature>? signature = null)
    : IBindingFactory<IServiceClient>, IServiceClient
{
    public const string TypeName = "text_test@1";

    private readonly List<TextCall> _calls = new();

    public TextModels(string text)
        : this(_ => (text, ServiceStatus.Ok))
    {
    }

    public string Type => TypeName;

    public Type SettingsType => typeof(NoSettings);

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

    public static ServiceSignature Text(params string[] inputs) => new(
        inputs.Select(n => new TensorSpec(n, TensorElementType.Text, Array.Empty<int>())).ToArray(),
        new[] { new TensorSpec("answer", TensorElementType.Text, Array.Empty<int>()) });

    public IServiceClient Create(BindingContext context) => this;

    public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
        ValueTask.FromResult(signature?.Invoke(version) ?? Text("user"));

    public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse outputs, CancellationToken cancellationToken)
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
