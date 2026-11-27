using StrataRec.Core.Columns;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>on_failure</c> is one of three words and the fallback node is its own field: each setting is
/// checked where it is written, a fallback setting on a node that does not fall back is an error, and the time a node
/// leaves to its fallback defaults to the fallback's <c>timeout_ms</c>.
/// </summary>
public sealed class NodeFallbackConfigTests
{
    private const string Sources = """ "r": { "client": "memory_list@1", "items": [1, 2] } """;

    [Theory]
    [InlineData("\"on_failure\": \"fallback\"", "NODE_FALLBACK_MISSING", "/nodes/primary/on_failure", "add \"fallback\": \"<node_id>\"")]
    [InlineData("\"on_failure\": \"fallback:backup\"", "NODE_BAD_ON_FAILURE", "/nodes/primary/on_failure", "fallback with \"fallback\": \"<node_id>\"")]
    [InlineData("\"fallback\": \"backup\"", "NODE_FALLBACK_UNUSED", "/nodes/primary/fallback", "set \"on_failure\": \"fallback\"")]
    [InlineData("\"on_failure\": \"skip\", \"fallback_reserve_ms\": 5", "NODE_FALLBACK_UNUSED", "/nodes/primary/fallback_reserve_ms", "set \"on_failure\": \"fallback\"")]
    [InlineData("\"on_failure\": \"fallback\", \"fallback\": \"nothing\"", "FALLBACK_UNKNOWN", "/nodes/primary/fallback", null)]
    public void FallbackSettings_AreCheckedWhereTheyAreWritten(string settings, string code, string pointer, string? hint)
    {
        var compilation = MiniWorkspace.TryCompile(Sources, Pipeline(settings), operators: Operators(new Budgets()));

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.Code == code);
        Assert.Equal(pointer, error.Pointer);
        if (hint is not null)
        {
            Assert.Contains(hint, error.Hint);
        }
    }

    /// <summary>
    /// A fallback runs whenever the node that names it fails: what decides whether a node on the timeline runs was
    /// accepted on a fallback and never looked at, so a fallback whose <c>when</c> was false served all the same.
    /// </summary>
    [Theory]
    [InlineData("\"when\": \"ctx.request.n == 1\"", "/nodes/backup/when")]
    [InlineData("\"min_remaining_ms\": 50", "/nodes/backup/min_remaining_ms")]
    public void WhatDecidesWhetherANodeRuns_IsAnErrorOnAFallback(string setting, string pointer)
    {
        var pipeline = Pipeline("\"on_failure\": \"fallback\", \"fallback\": \"backup\"")
            .Replace("\"pipeline_id\": \"p\",", "\"pipeline_id\": \"p\", \"request_schema\": { \"n\": { \"type\": \"int64\" } },", StringComparison.Ordinal)
            .Replace("\"role\": \"fallback\",", "\"role\": \"fallback\", " + setting + ",", StringComparison.Ordinal);

        var compilation = MiniWorkspace.TryCompile(Sources, pipeline, operators: Operators(new Budgets()));

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == StrataRec.Artifacts.Model.DiagnosticSeverity.Error);
        Assert.Equal(("NODE_FALLBACK_CONDITION", pointer), (error.Code, error.Pointer));
        Assert.Contains("put it on the node that falls back to this one", error.Hint);
    }

    [Theory]
    [InlineData("", 3000)] // the fallback's timeout_ms
    [InlineData(", \"fallback_reserve_ms\": 0", 0)]
    [InlineData(", \"fallback_reserve_ms\": 1000", 1000)]
    public async Task ANodeLeavesItsFallback_TheFallbacksTimeout_UnlessItSaysOtherwise(string reserve, int expectedMs)
    {
        var budgets = new Budgets();
        var plan = MiniWorkspace.Compile(Sources, Pipeline("\"on_failure\": \"fallback\", \"fallback\": \"backup\"" + reserve), operators: Operators(budgets));

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(10),
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var left = 10_000 - budgets.Primary!.Value.TotalMilliseconds;
        Assert.InRange(left, expectedMs, expectedMs + 1000); // the request's own time to get here is at most a second
        Assert.Equal(new[] { 1.0, 1.0 }, result.Items.Select(i => i.Columns["s"].AsFloat64()));
    }

    private static string Pipeline(string primarySettings) => $$"""
        { "pipeline_id": "p",
          "columns": { "score.s": { "type": "float64", "default": 0.0 } },
          "param_schema": { "q": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
          "params": { "q": { "r": 10 }, "k": 10 },
          "nodes": {
            "recall":  { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q" } },
            "primary": { "order": 200, "op": "Budget@1", {{primarySettings}}, "config": { "output_col": "score.s" } },
            "backup":  { "role": "fallback", "op": "ExpressionScorer@1", "timeout_ms": 3000, "config": { "output_col": "score.s", "expr": "1.0" } } },
          "output": { "limit_param": "k", "columns": { "s": "col.score.s" } } }
        """;

    private static OperatorRegistry Operators(Budgets budgets) => OperatorRegistry.CreateDefault().Add(new BudgetFactory(budgets));

    private sealed class Budgets
    {
        public TimeSpan? Primary { get; set; }
    }

    public sealed class BudgetConfig
    {
        public required string OutputCol { get; init; }
    }

    /// <summary><c>Budget@1</c>: notes the time budget it was given, then fails, so the fallback serves.</summary>
    private sealed class BudgetFactory(Budgets budgets) : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("Budget", 1, Effects.ColumnWriter, typeof(BudgetConfig), "Notes its budget, then fails.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<BudgetConfig>()!;
            return context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output) ? new Budget(budgets, output) : null;
        }

        private sealed class Budget(Budgets budgets, OutputColumn output) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                budgets.Primary = context.BudgetRemaining;
                _ = (ScalarColumn<double>)context.Output(output);
                context.Fail("noted its budget");
                return ValueTask.CompletedTask;
            }
        }
    }
}
