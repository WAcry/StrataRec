using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>int32</c> and <c>list&lt;int32&gt;</c> node outputs end to end (design 4.3, 4.6): a value that does not fit int32
/// storage takes the column's declared default, or becomes missing, and is counted in
/// <see cref="PipelineResult.OutOfRangeValues"/>; it is never wrapped. In the listener collection: one test reads the
/// out-of-range metric.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public class ListInt32EndToEndTests
{
    [Fact]
    public async Task ExpressionScorer_OutOfRangeWrites_TakeTheColumnDefault_OrBecomeMissing_AndAreCounted()
    {
        var plan = Compile(Workspace());
        using var capture = new MetricCapture();

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000,
        });

        Assert.True(result.Succeeded, result.Error);
        var byId = result.Items.ToDictionary(i => i.Id.Int64Value, i => i.Columns);
        Assert.Equal(new long[] { 1, 2, 3 }, byId.Keys.Order());

        // 1 × 1.5e9 fits; 3e9 and 4.5e9 do not (an unchecked narrowing would store -1294967296 and 205032704).
        Assert.Equal(1_500_000_000, byId[1]["id32"].AsInt64());
        Assert.Equal(-1, byId[2]["id32"].AsInt64());
        Assert.Equal(-1, byId[3]["id32"].AsInt64());
        Assert.Equal(1_500_000_000, byId[1]["id32n"].AsInt64());
        Assert.True(byId[2]["id32n"].IsMissing);
        Assert.True(byId[3]["id32n"].IsMissing);

        // A list with one element outside the int32 range is replaced as a whole.
        Assert.Equal(new long[] { 1, 2 }, byId[1]["tags"].AsInt64List());
        Assert.Equal(new long[] { 0 }, byId[2]["tags"].AsInt64List());
        Assert.Equal(new long[] { 0 }, byId[3]["tags"].AsInt64List());
        Assert.Equal(new long[] { 1, 2 }, byId[1]["tags_n"].AsInt64List());
        Assert.True(byId[2]["tags_n"].IsMissing);
        Assert.True(byId[3]["tags_n"].IsMissing);

        Assert.Equal(8, result.OutOfRangeValues);
        Assert.Equal(0, result.NonFiniteValues);
        var metric = Assert.Single(capture.Of(StrataRecMetrics.Names.ColumnOutOfRange));
        Assert.Equal(8, metric.Value);
        Assert.Equal(plan.PipelineId, Assert.Single(metric.Tags, t => t.Key == StrataRecTags.Pipeline).Value);
    }

    [Theory]
    [InlineData("int32", "col.entity_id * 1500000000")]
    [InlineData("list<int32>", "col.entity_id > 1 ? [3000000000] : [7]")]
    public async Task OutputReadingAFailedDerivedFeature_FailsTheRequest(string type, string expr)
    {
        // Review 6 #1 (verification): a non-nullable derived feature whose value did not fit fails; an output expression
        // reading it directly must not serve the invalid storage as a present value.
        var root = Workspace();
        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), $$"""
            { "catalog_id": "c",
              "entities": { "item": { "scope": "candidate", "key": "col.entity_id", "key_type": "int64" } },
              "derived_features": { "item.narrow": { "type": "{{type}}", "expr": "{{expr}}" } } }
            """);
        var pipeline = Path.Combine(root, "pipelines", "p.jsonc");
        File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace("\"tags_n\": \"col.out.tags_n\" }", "\"tags_n\": \"col.out.tags_n\", \"narrow\": \"col.item.narrow\" }", StringComparison.Ordinal));
        var plan = Compile(root);

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000,
        });

        Assert.False(result.Succeeded);
        Assert.Contains("output reads a feature that failed", result.Error);
        Assert.Contains("item.narrow", result.Error);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task TapReadingAFailedDerivedFeature_LogsItMissing_AndAFilterOnItKeepsNothing()
    {
        // A tap never fails the request; it must not log invalid storage as values either.
        var root = Workspace();
        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """
            { "catalog_id": "c",
              "entities": { "item": { "scope": "candidate", "key": "col.entity_id", "key_type": "int64" } },
              "derived_features": { "item.narrow": { "type": "int32", "expr": "col.entity_id * 1500000000" } } }
            """);
        var pipeline = Path.Combine(root, "pipelines", "p.jsonc");
        var text = File.ReadAllText(pipeline)
            .Replace("\"out.tags_n\": { \"type\": \"list<int32>?\" }",
                "\"out.tags_n\": { \"type\": \"list<int32>?\" }, \"out.narrow\": { \"type\": \"int32?\" }", StringComparison.Ordinal)
            .Replace("\"tags_n\": { \"order\": 230,",
                "\"narrow\": { \"order\": 240, \"op\": \"ExpressionScorer@1\", \"on_failure\": \"skip\", \"config\": { \"output_col\": \"out.narrow\", \"expr\": \"col.item.narrow\" } },\n\"tags_n\": { \"order\": 230,", StringComparison.Ordinal)
            .Replace("\"output\": {",
                "\"funnel_groups\": { \"all\": { \"key\": [\"ctx.request.id\"], \"rate\": 1.0 } },\n" +
                "\"log_taps\": {\n" +
                "  \"values\":   { \"group\": \"all\", \"attach\": { \"output\": true }, \"columns\": { \"narrow\": \"col.item.narrow\", \"id32\": \"col.out.id32\" } },\n" +
                "  \"filtered\": { \"group\": \"all\", \"attach\": { \"output\": true }, \"rows\": { \"filter\": \"col.item.narrow > 0\" }, \"columns\": { \"id32\": \"col.out.id32\" } } },\n" +
                "\"output\": {", StringComparison.Ordinal);
        File.WriteAllText(pipeline, text);
        var plan = Compile(root);

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000, CaptureLogs = true,
        });

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Outcomes, t => t.NodeId == "narrow" && t.Kind == NodeOutcomeKind.Failed);
        var values = Assert.Single(result.LogBatches, b => b.TapId == "values");
        Assert.Equal(3, values.RowCount);
        Assert.All(Assert.Single(values.Columns, c => c.Name == "narrow").Values, v => Assert.True(v.IsMissing, v.ToString()));
        Assert.Contains(Assert.Single(values.Columns, c => c.Name == "id32").Values, v => !v.IsMissing); // other columns unaffected
        Assert.DoesNotContain(result.LogBatches, b => b.TapId == "filtered" && b.RowCount > 0);
    }

    [Theory]
    [InlineData("n32", 3_000_000_000L, null)]
    [InlineData("tags32", null, 3_000_000_000L)]
    public async Task RequestInputOutsideItsDeclaredInt32Type_IsRejected(string input, long? scalar, long? element)
    {
        // Review 6 #2: inputs are held promoted (int64) but validated against the declared int32 width.
        var plan = Compile(Workspace());
        var value = scalar is { } s ? ScalarValue.FromInt64(s) : ScalarValue.FromInt64List(new long[] { 7, element!.Value });

        var rejected = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs().Set(input, value), Deadline = TimeSpan.FromSeconds(5),
        });
        var accepted = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs().Set(input, scalar is null ? ScalarValue.FromInt64List(new long[] { 7, 8 }) : ScalarValue.FromInt64(7)),
            Deadline = TimeSpan.FromSeconds(5),
        });

        Assert.False(rejected.Succeeded);
        Assert.Contains($"request input '{input}' must be {(scalar is null ? "list<int32>?" : "int32?")}", rejected.Error);
        Assert.True(accepted.Succeeded, accepted.Error);
    }

    [Fact]
    public void ConstantOutOfRange_ForAnInt32Column_IsAConfigurationError_AtItsPointer()
    {
        var root = Workspace(tagsExpr: "[1, 3000000000]");
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));

        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.ToString().Contains("EXPR_INVALID_ARGUMENT", StringComparison.Ordinal));
        Assert.Contains("/nodes/tags/config/expr", error.ToString());
        Assert.Contains("list<int64>", error.ToString());
    }

    [Fact]
    public void DefaultOutsideTheInt32Range_IsAConfigurationError()
    {
        var root = Workspace(tagsDefault: "[0, 2147483648]");
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        var compilation = request is null ? null : PlanCompiler.Compile(request, new PlanEnvironment { BaseDirectory = root });

        var all = string.Join("\n", diagnostics.Items) + "\n" + string.Join("\n", compilation?.Diagnostics ?? Array.Empty<ConfigDiagnostic>());
        Assert.Contains("COLUMN_BAD_DEFAULT", all);
        Assert.Contains("/columns/out.tags/default", all);
        Assert.Contains("element 1", all);
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

    private static string Workspace(string tagsExpr = "col.entity_id > 1 ? [1, 3000000000] : [1, 2]", string tagsDefault = "[0]")
    {
        var root = Directory.CreateTempSubdirectory("stratarec-int32-").FullName;
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
              "request_schema": { "n32": { "type": "int32?" }, "tags32": { "type": "list<int32>?" } },
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "pool": 10 }, "k": 5 },
              "columns": {
                "out.id32":   { "type": "int32", "default": -1 },
                "out.id32n":  { "type": "int32?" },
                "out.tags":   { "type": "list<int32>?", "default": {{tagsDefault}} },
                "out.tags_n": { "type": "list<int32>?" }
              },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
                "id32":   { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "out.id32", "expr": "col.entity_id * 1500000000" } },
                "id32n":  { "order": 210, "op": "ExpressionScorer@1", "config": { "output_col": "out.id32n", "expr": "col.entity_id * 1500000000" } },
                "tags":   { "order": 220, "op": "ExpressionScorer@1", "config": { "output_col": "out.tags", "expr": "{{tagsExpr}}" } },
                "tags_n": { "order": 230, "op": "ExpressionScorer@1", "config": { "output_col": "out.tags_n", "expr": "col.entity_id > 1 ? [1, 3000000000] : [1, 2]" } }
              },
              "output": { "limit_param": "k",
                          "columns": { "id32": "col.out.id32", "id32n": "col.out.id32n", "tags": "col.out.tags", "tags_n": "col.out.tags_n" } } }
            """);
        return root;
    }
}
