using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// In the listener collection: one test reads the out-of-range metric.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public class FixedLengthListEndToEndTests
{
    private const string Mixed = "col.entity_id > 1 ? [1.0, 2.0, 3.0] : [0.5, 0.25]";

    [Fact]
    public async Task WritesOfAnotherLength_TakeTheDefault_OrBecomeMissing_AndAreCounted()
    {
        var plan = Compile(Workspace());
        using var capture = new MetricCapture();

        var result = await Execute(plan);

        Assert.True(result.Succeeded, result.Error);
        var byId = result.Items.ToDictionary(i => i.Id.Int64Value, i => i.Columns);
        Assert.Equal(new long[] { 1, 2, 3 }, byId.Keys.Order());

        Assert.Equal(new[] { 0.5, 0.25 }, byId[1]["feature"].AsFloat64List().ToArray());
        Assert.Equal(new[] { 9.0, 9.0 }, byId[2]["feature"].AsFloat64List().ToArray());
        Assert.Equal(new[] { 9.0, 9.0 }, byId[3]["feature"].AsFloat64List().ToArray());

        Assert.Equal(new[] { 0.5, 0.25 }, byId[1]["vec"].AsFloat64List().ToArray());
        Assert.Equal(new[] { 7.0, 7.0 }, byId[2]["vec"].AsFloat64List().ToArray());
        Assert.Equal(new[] { 7.0, 7.0 }, byId[3]["vec"].AsFloat64List().ToArray());
        Assert.Equal(new[] { 0.5, 0.25 }, byId[1]["vec_n"].AsFloat64List().ToArray());
        Assert.True(byId[2]["vec_n"].IsMissing);
        Assert.True(byId[3]["vec_n"].IsMissing);

        Assert.Equal(6, result.OutOfRangeValues);
        var metric = Assert.Single(capture.Of(StrataRecMetrics.Names.ColumnOutOfRange));
        Assert.Equal(6, metric.Value);
    }

    [Fact]
    public async Task ANonNullableDerivedFeatureWithoutADefault_OfAnotherLength_Fails()
    {
        var root = Workspace(featureDefault: null);
        var plan = Compile(root);

        var result = await Execute(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("output reads a feature that failed", result.Error);
        Assert.Contains("item.emb", result.Error);
    }

    [Theory]
    [InlineData("[7, 7, 7]", "[9, 9]", "COLUMN_BAD_DEFAULT", "/columns/out.vec/default", "must be an array of 2 numbers (list<float32,2>), got 3 elements")]
    [InlineData("[7, 7]", "[9]", "FEATURE_BAD_DEFAULT", "/derived_features/item.emb/default", "must be an array of 2 numbers (list<float32,2>), got 1 element")]
    public void ADefaultOfAnotherLength_IsAConfigurationError_AtItsPointer(string columnDefault, string featureDefault, string code, string pointer, string message)
    {
        var root = Workspace(columnDefault: columnDefault, featureDefault: featureDefault);
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        var compilation = request is null ? null : PlanCompiler.Compile(request, new PlanEnvironment { BaseDirectory = root });

        var all = string.Join("\n", diagnostics.Items) + "\n" + string.Join("\n", compilation?.Diagnostics ?? Array.Empty<ConfigDiagnostic>());
        Assert.Contains(code, all);
        Assert.Contains(pointer, all);
        Assert.Contains(message, all);
    }

    [Theory]
    [InlineData("[1.0, 2.0, 3.0]", "EXPR_TYPE_MISMATCH", "declare the site as list<float32,3>, or write a list of 2")]
    [InlineData("let v = [1, 2, 3]; col.entity_id > 0 ? v : v", "EXPR_TYPE_MISMATCH", "declare the site as list<float32,3>, or write a list of 2")]
    public void AConstantOfAnotherLength_ForAFixedLengthColumn_IsAConfigurationError_AtItsPointer(string expr, string code, string hint)
    {
        var root = Workspace(vecExpr: expr);
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));

        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.ToString().Contains(code, StringComparison.Ordinal));
        Assert.Contains("/nodes/vec/config/expr", error.ToString());
        Assert.Contains("list<float32,2>", error.ToString());
        Assert.Contains(hint, error.ToString());
    }

    private static ValueTask<PipelineResult> Execute(ExecutionPlan plan) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000,
    }, TestContext.Current.CancellationToken);

    private static ExecutionPlan Compile(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static string Workspace(string vecExpr = Mixed, string columnDefault = "[7, 7]", string? featureDefault = "[9, 9]")
    {
        var root = Directory.CreateTempSubdirectory("stratarec-fixed-length-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        var defaultMember = featureDefault is null ? string.Empty : $", \"default\": {featureDefault}";
        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), $$"""
            { "catalog_id": "c",
              "entities": { "item": { "key": "col.entity_id" } },
              "derived_features": { "item.emb": { "type": "list<float32,2>", "expr": "{{Mixed}}"{{defaultMember}} } } }
            """);
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), """
            { "environment": "local",
              "services": { "pool": { "client": "memory_list@1", "items": [1, 2, 3] } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), $$"""
            { "pipeline_id": "p",
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "pool": 10 }, "k": 5 },
              "columns": {
                "out.vec":   { "type": "list<float32,2>?", "default": {{columnDefault}} },
                "out.vec_n": { "type": "list<float32,2>?" }
              },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
                "vec":    { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "out.vec", "expr": "{{vecExpr}}" } },
                "vec_n":  { "order": 210, "op": "ExpressionScorer@1", "config": { "output_col": "out.vec_n", "expr": "{{Mixed}}" } }
              },
              "output": { "limit_param": "k",
                          "columns": { "feature": "col.item.emb", "vec": "col.out.vec", "vec_n": "col.out.vec_n" } } }
            """);
        return root;
    }
}
