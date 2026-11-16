using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// Compaction changes where rows are, never what a request returns (design 4.5): a key a filter dropped stays out of a
/// later row source whether the filter's node compacts the table or not (Gemini review 2026-11-14, finding 1).
/// </summary>
public sealed class CompactionTests
{
    /// <summary>
    /// <c>recall</c> appends 1–4, <c>keep</c> keeps 2 only (a quarter live: below the default threshold, so <c>auto</c>
    /// compacts), <c>again</c> recalls 1 and 5: 1 was dropped and stays out, in every compaction mode.
    /// </summary>
    [Theory]
    [InlineData("int64", "never")]
    [InlineData("int64", "auto")]
    [InlineData("int64", "always")]
    [InlineData("string", "never")]
    [InlineData("string", "auto")]
    [InlineData("string", "always")]
    public async Task ALaterRowSource_NeverAppendsADroppedKey_WhateverTheCompaction(string keyType, string compact)
    {
        var plan = Compile(keyType, compact);
        var one = keyType == "string" ? EntityKey.FromString("i1") : EntityKey.FromInt64(1);

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = new RequestInputs(),
            Deadline = TimeSpan.FromSeconds(60),
            ExplainIds = new[] { one },
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new[] { Key(keyType, 2), Key(keyType, 5) }, result.Items.Select(i => i.Id));
        var explained = Assert.Single(result.Explanation!.Candidates);
        Assert.Equal((ExplainVerdict.Dropped, "keep"), (explained.Verdict, explained.DroppedBy));
        Assert.Equal((ExplainChange.Absent, true, false), (explained.Steps[^1].Change, explained.Steps[^1].Present, explained.Steps[^1].Alive));
    }

    private static EntityKey Key(string keyType, int n) => keyType == "string" ? EntityKey.FromString("i" + n) : EntityKey.FromInt64(n);

    private static ExecutionPlan Compile(string keyType, string compact)
    {
        string Id(int n) => keyType == "string" ? $"\"i{n}\"" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Ids(params int[] ns) => "[" + string.Join(", ", ns.Select(Id)) + "]";

        var root = Directory.CreateTempSubdirectory("stratarec-compaction-").FullName;
        Write(root, "catalog/c.jsonc", $$"""
            { "catalog_id": "c", "entities": { "item": { "key": "col.entity_id" } }, "features": {} }
            """);
        Write(root, "bindings/local.jsonc", $$"""
            { "environment": "local",
              "candidate_sources": {
                "main":  { "client": "memory_list@1", "items": {{Ids(1, 2, 3, 4)}} },
                "later": { "client": "memory_list@1", "items": {{Ids(1, 5)}} } } }
            """);
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "{{keyType}}",
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "later_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "main": 10 }, "later_quotas": { "later": 10 }, "k": 10 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "keep":   { "order": 200, "op": "ExpressionFilter@1", "compact": "{{compact}}", "config": { "predicate": "col.entity_id == {{Id(2).Replace('"', '\'')}}" } },
                "again":  { "order": 300, "op": "MultiSourceRecall@1", "config": { "quotas_param": "later_quotas" } } },
              "output": { "limit_param": "k", "columns": {} } }
            """);

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
