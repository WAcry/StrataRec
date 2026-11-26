using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// What leaves the engine has the declared type: a log tap's column or context field that is a plain reference (a
/// feature, a node's output column, a request input, a context feature) is logged at the width it is declared with, a
/// candidate column copied straight from its storage, and a result column carries the declared type. Computed expressions
/// keep their promoted type; a derived feature gives one a narrow type. Taps logged every column at the expression's
/// promoted type (an int32 feature as int64, a float32 embedding as float64), and results carried no types at all.
/// </summary>
public class DeclaredWidthEndToEndTests
{
    [Fact]
    public async Task ATapLogsEachReference_AtItsDeclaredType_AndAComputedExpression_AtItsPromotedOne()
    {
        var result = await Run(Compile(Workspace()));

        var batch = Assert.Single(result.LogBatches, b => b.TapId == "t");
        Assert.Equal(
            ["clicks:int32", "clicks1:int64", "computed:float64?", "ctr:float32?", "ctr2:float32", "dctr:float64?", "emb:list<float32,2>?", "id:int64",
             "maxc:int64", "n32:int32", "w:float32?"],
            batch.Schema.Columns.Select(f => $"{f.Name}:{f.Type}"));
        Assert.Equal(["age:int32?", "sum:float64", "uctr:float32", "w:float32?"], batch.Schema.Context.Select(f => $"{f.Name}:{f.Type}"));
        Assert.All(batch.Columns, c => Assert.Equal(batch.Schema.Columns.Single(f => f.Name == c.Name).Type, c.Type));

        var order = batch.Int64EntityIds!.ToList();
        var (first, second) = (order.IndexOf(1), order.IndexOf(2));
        var clicks = Assert.IsType<int[]>(Column(batch, "clicks").Values);
        Assert.Equal((int.MaxValue, -5), (clicks[first], clicks[second]));
        Assert.Equal(-2, Assert.IsType<int[]>(Column(batch, "n32").Values)[second]); // a node's int32 output
        var ctr = Assert.IsType<float[]>(Column(batch, "ctr").Values);
        Assert.Equal((0.1f, 0.3f), (ctr[first], ctr[second]));
        Assert.Equal(0.6f, Assert.IsType<float[]>(Column(batch, "ctr2").Values)[second]); // a derived float32 feature
        Assert.IsType<double[]>(Column(batch, "computed").Values); // computed: promoted
        Assert.IsType<long[]>(Column(batch, "id").Values);
        Assert.IsType<long[]>(Column(batch, "clicks1").Values); // computed as written, though the optimizer reduces it to the column
        Assert.IsType<double[]>(Column(batch, "dctr").Values); // computed as written, though the binder reduces it to the column
        Assert.IsType<long[]>(Column(batch, "maxc").Values);
        Assert.Equal([0.25f, 0.25f], Assert.IsType<float[]>(Column(batch, "w").Values)); // a request input broadcast, narrowed
        var emb = Column(batch, "emb");
        Assert.Equal(4, Assert.IsType<float[]>(emb.Values).Length); // a float32 embedding: 4 bytes an element
        Assert.Equal(ScalarValue.FromFloat32List([0.1f, 0.2f]), emb.Get(first));

        // Context values stay promoted; a sink narrows them by the declared type.
        var context = batch.Context.ToDictionary(c => c.Name, c => c.Value);
        Assert.Equal(30, context["age"].AsInt32());
        Assert.Equal(0.25f, context["w"].AsFloat32());
        Assert.Equal(0.1f, context["uctr"].AsFloat32());

        using var json = JsonDocument.Parse(LogBatchJson.Encode(batch, indented: false));
        var written = json.RootElement.GetProperty("context");
        Assert.Equal(("0.25", "0.1"), (written.GetProperty("w").GetRawText(), written.GetProperty("uctr").GetRawText())); // as floats
        Assert.Equal(((double)0.1f + 1.0).ToString(System.Globalization.CultureInfo.InvariantCulture), written.GetProperty("sum").GetRawText()); // a computed float64: the double
        Assert.Equal("[0.1,0.3]", Reorder(json.RootElement.GetProperty("float_cols").GetProperty("ctr"), first, second));
    }

    [Fact]
    public async Task AResultColumn_CarriesItsDeclaredType_AndAHostReadsItAtThatWidth()
    {
        var result = await Run(Compile(Workspace()));

        Assert.True(result.Succeeded, result.Error);
        var columns = result.Columns;
        Assert.Equal(
            ["clicks:int32", "ctr2:float32", "ctr:float32?", "double:float64?", "emb:list<float32,2>?", "maxc:int64", "uctr:float32", "w2:float64", "w:float32?"],
            columns.Names.Select((n, c) => $"{n}:{columns.Types[c]}").Order(StringComparer.Ordinal));
        var row = result.Items.Single(i => i.Id.Int64Value == 1).Columns;
        Assert.Equal(int.MaxValue, row["clicks"].AsInt32());
        Assert.Equal(0.1f, row["ctr"].AsFloat32());
        Assert.Equal(0.25f, row["w"].AsFloat32());
        Assert.True(row["uctr"].IsFloat32); // a float32 context feature prints as the float it is, as a float32 column's value does
    }

    [Fact]
    public async Task ATapColumn_ReadingAColumnNoNodeProduced_IsMissing()
    {
        // The only scorer of out.n32n is skipped by its when: a tap reading the column logs it missing, typed as declared.
        var root = Workspace();
        var pipeline = Path.Combine(root, "pipelines", "p.jsonc");
        File.WriteAllText(pipeline, File.ReadAllText(pipeline)
            .Replace("\"order\": 210, \"op\": \"ExpressionScorer@1\",", "\"order\": 210, \"op\": \"ExpressionScorer@1\", \"when\": \"(ctx.request.age ?? 0) == 1\",", StringComparison.Ordinal)
            .Replace("\"clicks1\": \"col.item.clicks * 1\" } } },", "\"clicks1\": \"col.item.clicks * 1\" } },\n\"nullable\": { \"group\": \"all\", \"attach\": { \"output\": true }, \"columns\": { \"n\": \"col.out.n32n\" } } },", StringComparison.Ordinal));

        var result = await Run(Compile(root));

        Assert.True(result.Succeeded, result.Error);
        var column = Assert.Single(Assert.Single(result.LogBatches, b => b.TapId == "nullable").Columns);
        Assert.Equal("int32?", column.Type.ToString());
        Assert.IsType<int[]>(column.Values);
        Assert.All(column.Rows(), v => Assert.True(v.IsMissing));
    }

    /// <summary>
    /// A tap expression reading a non-nullable column no node produced logs missing, as a plain reference to the column
    /// does; it does not fail the request (it threw once such an expression stopped counting as a reference). One reading a
    /// nullable such column reads it as missing, and keeps what it makes of that.
    /// </summary>
    [Fact]
    public async Task ATapExpression_ReadingAColumnNoNodeProduced_DoesNotFailTheRequest()
    {
        var root = Workspace();
        var pipeline = Path.Combine(root, "pipelines", "p.jsonc");
        const string Skipped = "\"when\": \"(ctx.request.age ?? 0) == 1\",";
        File.WriteAllText(pipeline, File.ReadAllText(pipeline)
            .Replace("\"order\": 200, \"op\": \"ExpressionScorer@1\",", "\"order\": 200, \"op\": \"ExpressionScorer@1\", " + Skipped, StringComparison.Ordinal)
            .Replace("\"order\": 210, \"op\": \"ExpressionScorer@1\",", "\"order\": 210, \"op\": \"ExpressionScorer@1\", " + Skipped, StringComparison.Ordinal)
            .Replace("\"clicks1\": \"col.item.clicks * 1\" } } },", "\"clicks1\": \"col.item.clicks * 1\" } },\n\"computed\": { \"group\": \"all\", \"attach\": { \"output\": true }, \"columns\": { \"n\": \"col.out.n32 * 1\", \"nn\": \"col.out.n32n ?? 7\" } } },", StringComparison.Ordinal));

        var result = await Run(Compile(root));

        Assert.True(result.Succeeded, result.Error);
        var batch = Assert.Single(result.LogBatches, b => b.TapId == "computed");
        Assert.All(Column(batch, "n").Rows(), v => Assert.True(v.IsMissing));
        Assert.All(Column(batch, "nn").Rows(), v => Assert.Equal(7, v.AsInt64()));
        Assert.All(Column(Assert.Single(result.LogBatches, b => b.TapId == "t"), "n32").Rows(), v => Assert.True(v.IsMissing));
    }

    /// <summary>
    /// A tap with <c>columns_from</c> logs a model node's inputs at the types the model was sent: an int32 feature and a
    /// float32 context feature as int32 and float32, as random@1 (declaring them so) received them.
    /// </summary>
    [Fact]
    public async Task ATapOfAModelsInputs_LogsThemAtTheTypesTheModelWasSent()
    {
        var root = Workspace();
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        File.WriteAllText(bindings, File.ReadAllText(bindings).Replace("\"services\": {", """
            "services": { "m": { "client": "random@1", "seed": 1,
                               "inputs": { "clicks": { "type": "int32", "shape": [-1] }, "uctr": { "type": "float32", "shape": [-1] } }, "outputs": ["p"] },
            """, StringComparison.Ordinal));
        var pipeline = Path.Combine(root, "pipelines", "p.jsonc");
        File.WriteAllText(pipeline, File.ReadAllText(pipeline)
            .Replace("\"columns\": { \"out.n32\"", "\"columns\": { \"score.p\": { \"type\": \"float32\", \"default\": 0.0 }, \"out.n32\"", StringComparison.Ordinal)
            .Replace("\"n32n\": {", """
                "scorer": { "order": 300, "op": "ModelScorer@1", "config": { "model": "m", "inputs": { "clicks": "col.item.clicks", "uctr": "ctx.user.ctr" }, "outputs": { "p": "score.p" } } },
                "n32n": {
                """, StringComparison.Ordinal)
            .Replace("\"log_taps\": {", "\"log_taps\": { \"fed\": { \"group\": \"all\", \"attach\": { \"output\": true }, \"columns_from\": \"scorer.inputs\" },", StringComparison.Ordinal));

        var result = await Run(Compile(root));

        Assert.True(result.Succeeded, result.Error);
        var batch = Assert.Single(result.LogBatches, b => b.TapId == "fed");
        Assert.Equal(["clicks:int32", "uctr:float32"], batch.Schema.Columns.Select(f => $"{f.Name}:{f.Type}").Order(StringComparer.Ordinal));
        Assert.IsType<int[]>(Column(batch, "clicks").Values);
        Assert.Equal([0.1f, 0.1f], Assert.IsType<float[]>(Column(batch, "uctr").Values)); // a context feature broadcast, at its width
    }

    /// <summary>
    /// A float32 request input given 0.1 is 0.1f everywhere: an expression computes with the float a result or log of the
    /// field gets, not the double it was given.
    /// </summary>
    [Fact]
    public async Task AFloat32RequestInput_IsTheSameFloat_InExpressionsAndWhereItLeavesTheEngine()
    {
        var plan = Compile(Workspace());

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs().Set("user_id", 7L).Set("w", 0.1), Deadline = TimeSpan.FromSeconds(10), CaptureLogs = true,
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var row = result.Items[0].Columns;
        Assert.Equal(0.1f, row["w"].AsFloat32());
        Assert.Equal((double)0.1f, row["w2"].AsFloat64()); // computed in float64 from the float: (double)0.1f, not 0.1
        var batch = Assert.Single(result.LogBatches, b => b.TapId == "t");
        Assert.Equal((double)0.1f, batch.Context.Single(c => c.Name == "w").Value.AsFloat64());
    }

    /// <summary>
    /// The primary declares input <c>w</c> float32, its fallback int64. A replay gives the fallback the integer the host
    /// gave, as the request served it: the recording keeps the host's value, not the primary's float32 of it, which lost
    /// its last digits.
    /// </summary>
    [Fact]
    public async Task AReplayedFallback_BindsTheHostsInput_NotThePrimarysDeclaredTypeOfIt()
    {
        const long W = 9_007_199_791_611_905;
        var root = ExperimentRegressionTests.FallbackWorkspace(brokenLatencyMs: 0);
        foreach (var (id, type) in new[] { ("primary", "float32"), ("backup", "int64") })
        {
            var file = Path.Combine(root, "pipelines", id + ".jsonc");
            File.WriteAllText(file, File.ReadAllText(file)
                .Replace("\"param_schema\":", $"\"request_schema\": {{ \"w\": {{ \"type\": \"{type}\" }} }}, \"param_schema\":", StringComparison.Ordinal)
                .Replace("\"source\": \"col.recall.primary_source\"", "\"source\": \"col.recall.primary_source\", \"w\": \"ctx.request.w\"", StringComparison.Ordinal));
        }

        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
        ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", new[] { "primary" }).ThrowIfErrors("load");
        var served = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "primary", RequestId = "r", Inputs = new RequestInputs().Set("w", W), Deadline = TimeSpan.FromSeconds(10),
            Debug = new DebugOptions { Record = true },
        }, TestContext.Current.CancellationToken);
        var replayed = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(served.Recording!)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(("backup", W), (served.ServedBy, served.Items[0].Columns["w"].AsInt64()));
        Assert.True(replayed.Result.Succeeded, replayed.Result.Error);
        Assert.Equal(("backup", W), (replayed.Result.ServedBy, replayed.Result.Items[0].Columns["w"].AsInt64()));
    }

    private static LogColumn Column(LogBatch batch, string name) => Assert.Single(batch.Columns, c => c.Name == name);

    /// <summary>A two-row JSON array in entity order (1, 2), whatever the rows' order.</summary>
    private static string Reorder(JsonElement array, int first, int second) => $"[{array[first].GetRawText()},{array[second].GetRawText()}]";

    private static Task<PipelineResult> Run(ExecutionPlan plan) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r",
        Inputs = new RequestInputs().Set("user_id", 7L).Set("age", 30L).Set("w", 0.25),
        Deadline = TimeSpan.FromSeconds(10),
        TimeMs = 1_767_225_600_000,
        CaptureLogs = true,
    }, TestContext.Current.CancellationToken).AsTask();

    private static ExecutionPlan Compile(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static string Workspace()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-widths-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """
            { "catalog_id": "c",
              "entities": { "item": { "key": "col.entity_id" }, "user": { "key": "ctx.request.user_id" } },
              "features": {
                "item.clicks": { "type": "int32", "source": "items", "field": "clicks", "default": 0 },
                "item.ctr":    { "type": "float32", "source": "items", "field": "ctr" },
                "item.emb":    { "type": "list<float32,2>", "source": "items", "field": "emb" },
                "user.ctr":    { "type": "float32", "source": "users", "field": "ctr", "default": 0.0 }
              },
              "derived_features": { "item.ctr2": { "type": "float32", "expr": "(col.item.ctr ?? 0.0) * 2.0" } } }
            """);
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), """
            { "environment": "local",
              "sources": {
                "items": { "connector": "memory@1", "timeout_ms": 10000,
                           "data": { "1": { "clicks": 2147483647, "ctr": 0.1, "emb": [0.1, 0.2] }, "2": { "clicks": -5, "ctr": 0.3, "emb": [0.3, 0.4] } } },
                "users": { "connector": "memory@1", "timeout_ms": 10000, "data": { "7": { "ctr": 0.1 } } } },
              "services": { "pool": { "client": "memory_list@1", "items": [1, 2] } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), """
            { "pipeline_id": "p",
              "request_schema": { "user_id": { "type": "int64?" }, "age": { "type": "int32?" }, "w": { "type": "float32?" } },
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "pool": 10 }, "k": 5 },
              "columns": { "out.n32": { "type": "int32", "default": 0 }, "out.n32n": { "type": "int32?" } },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
                "n32":  { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "out.n32", "expr": "-col.entity_id" } },
                "n32n": { "order": 210, "op": "ExpressionScorer@1", "config": { "output_col": "out.n32n", "expr": "-col.entity_id" } }
              },
              "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
              "log_taps": {
                "t": { "group": "all", "attach": { "output": true },
                       "context": { "age": "ctx.request.age", "w": "ctx.request.w", "uctr": "ctx.user.ctr", "sum": "ctx.user.ctr + 1.0" },
                       "columns": { "clicks": "col.item.clicks", "ctr": "col.item.ctr", "ctr2": "col.item.ctr2", "emb": "col.item.emb",
                                    "computed": "col.item.ctr * 2.0", "dctr": "double(col.item.ctr)", "maxc": "max(col.item.clicks)",
                                    "id": "col.entity_id", "w": "ctx.request.w", "n32": "col.out.n32",
                                    "clicks1": "col.item.clicks * 1" } } },
              "output": { "limit_param": "k",
                          "columns": { "clicks": "col.item.clicks", "ctr": "col.item.ctr", "emb": "col.item.emb", "w": "ctx.request.w",
                                       "uctr": "ctx.user.ctr", "double": "col.item.ctr * 2.0", "ctr2": "col.item.ctr2", "w2": "(ctx.request.w ?? 0.0) + 0.0",
                                       "maxc": "max(col.item.clicks)" } } }
            """);
        return root;
    }
}
