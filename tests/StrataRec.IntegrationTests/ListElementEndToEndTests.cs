using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>list&lt;float32&gt;</c>, <c>list&lt;float64&gt;</c> and <c>list&lt;bool&gt;</c> end to end (design 4.3): request
/// inputs, node outputs read by later expressions, a float list that does not fit float32 replaced as a whole and
/// counted in <see cref="PipelineResult.NonFiniteValues"/>, results and log taps.
/// </summary>
public class ListElementEndToEndTests
{
    [Fact]
    public async Task FloatAndBoolLists_FlowFromInputsThroughNodesToResultsAndLogs()
    {
        var plan = Compile(Workspace());

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = new RequestInputs().Set("weights", new[] { 0.1, 2.5 }).Set("big", new[] { 1e300 }).Set("flags", new[] { true, false }),
            Deadline = TimeSpan.FromSeconds(5),
            TimeMs = 1_767_225_600_000,
            CaptureLogs = true,
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var byId = result.Items.ToDictionary(i => i.Id.Int64Value, i => i.Columns);

        // Stored as float32: the values read back are the floats, printed as such.
        Assert.Equal(ScalarValue.FromFloat32List(new[] { 0.1f, 2.5f }), byId[1]["w"]);
        Assert.Equal("[0.1, 2.5]", byId[1]["w"].ToString());
        Assert.Equal(ScalarValue.FromFloat32List(new[] { 0.1f, 2.5f }), byId[2]["w"]);

        // 1e300 becomes +∞ in float32: the list takes the column's default, or is missing, and both are counted.
        Assert.Equal(ScalarValue.FromFloat64List(new[] { 0.0 }), byId[3]["w"]);
        Assert.True(byId[3]["wn"].IsMissing);
        Assert.Equal(2, result.NonFiniteValues);

        // A later node reads the float32 list column: size, and membership compared as float64.
        Assert.Equal(12L, byId[1]["n"].AsInt64()); // 2 elements, 2.5 among them
        Assert.Equal(1L, byId[3]["n"].AsInt64()); // the default [0.0]

        Assert.Equal(new[] { true, false }, byId[1]["f"].AsBoolList().ToArray());
        Assert.Equal(new[] { false }, byId[3]["f"].AsBoolList().ToArray());

        var tap = Assert.Single(result.LogBatches, b => b.TapId == "lists");
        Assert.Equal(3, tap.RowCount);
        Assert.Contains(ScalarValue.FromFloat32List(new[] { 0.1f, 2.5f }), Assert.Single(tap.Columns, c => c.Name == "w").Rows());
        Assert.Contains(ScalarValue.FromBoolList(new[] { false }), Assert.Single(tap.Columns, c => c.Name == "f").Rows());
    }

    [Fact]
    public async Task RequestInputsOfAnotherElementType_AreRejected_OrConvertedAsJsonWouldBe()
    {
        var plan = Compile(Workspace());

        var rejected = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs().Set("flags", new long[] { 1, 0 }), Deadline = TimeSpan.FromSeconds(5),
        }, TestContext.Current.CancellationToken);
        var converted = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs().Set("weights", new long[] { 2, 3 }), Deadline = TimeSpan.FromSeconds(5),
        }, TestContext.Current.CancellationToken);

        Assert.False(rejected.Succeeded);
        Assert.Contains("Input 'flags' must be an array of true or false (list<bool>)", rejected.Error);
        Assert.True(converted.Succeeded, converted.Error); // integers into a float list, as [2, 3] in JSON
        Assert.Equal(ScalarValue.FromFloat64List(new double[] { 2, 3 }), converted.Items[0].Columns["w"]);
    }

    [Theory]
    [InlineData("list<float32>", "a list with a NaN/±∞ element", "\"default\": []")]
    [InlineData("list<int32>", "a list with an element outside the int32 range", "\"default\": []")]
    [InlineData("float32", "NaN/±∞ writes", "\"default\": 0.0")]
    [InlineData("int32", "writes outside the int32 range", "\"default\": 0")]
    public void ANonNullableColumnWhoseWritesCanBeReplaced_NeedsADefault(string type, string replaced, string fix)
    {
        var root = Workspace();
        var pipeline = Path.Combine(root, "pipelines", "p.jsonc");
        File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace(
            "\"out.n\":  { \"type\": \"int64\" },", $"\"out.n\":  {{ \"type\": \"int64\" }}, \"out.x\": {{ \"type\": \"{type}\" }},", StringComparison.Ordinal));
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);

        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });

        // out.n is a non-nullable int64 without a default: nothing written to it is ever replaced, so it needs none.
        var error = Assert.Single(compilation.Diagnostics, d => d.Code == "COLUMN_NEEDS_DEFAULT");
        Assert.Equal("/columns/out.x", error.Pointer);
        Assert.Contains(replaced, error.Message, StringComparison.Ordinal);
        Assert.Contains(fix, error.Hint, StringComparison.Ordinal);
    }

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
        var root = Directory.CreateTempSubdirectory("stratarec-lists-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), """
            { "environment": "local",
              "candidate_sources": { "pool": { "client": "memory_list@1", "items": [1, 2, 3] } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), $$"""
            { "pipeline_id": "p",
              "request_schema": { "weights": { "type": "list<float32>?" }, "big": { "type": "list<float64>?" }, "flags": { "type": "list<bool>?" } },
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "pool": 10 }, "k": 5 },
              "columns": {
                "out.w":  { "type": "list<float32>?", "default": [0] },
                "out.wn": { "type": "list<float32>?" },
                "out.n":  { "type": "int64" },
                "out.f":  { "type": "list<bool>?" }
              },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
                "w":  { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "out.w", "expr": "col.entity_id == 3 ? ctx.request.big : (ctx.request.weights ?? [0.5])" } },
                "wn": { "order": 210, "op": "ExpressionScorer@1", "config": { "output_col": "out.wn", "expr": "col.entity_id == 3 ? ctx.request.big : (ctx.request.weights ?? [1])" } },
                "n":  { "order": 220, "op": "ExpressionScorer@1", "config": { "output_col": "out.n", "expr": "size(col.out.w ?? [0]) + (2.5 in (col.out.w ?? [0]) ? 10 : 0)" } },
                "f":  { "order": 230, "op": "ExpressionScorer@1", "config": { "output_col": "out.f", "expr": "col.entity_id < 3 ? (ctx.request.flags ?? [true]) : [false]" } }
              },
              "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
              "log_taps": { "lists": { "group": "all", "attach": { "output": true }, "columns": { "w": "col.out.w", "f": "col.out.f" } } },
              "output": { "limit_param": "k",
                          "columns": { "w": "col.out.w", "wn": "col.out.wn", "n": "col.out.n", "f": "col.out.f" } } }
            """);
        return root;
    }
}
