using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>MultiSourceRecall@1</c> merge semantics (design 5.7): sources merge in the declared order, the first occurrence
/// of a key fixes its position and primary source, later ones add their source bit, the best rank and the best score;
/// a source is truncated at its quota.
/// </summary>
public sealed class RecallMergeTests
{
    // source_order b, a, c. "a" repeats 5 and is cut at its quota of 3, so its 7 never arrives.
    private const string Sources = """
        "b": { "client": "memory_list@1", "items": [ { "id": K3, "score": 0.95 }, { "id": K9, "score": 0.2 }, { "id": K7, "score": 0.6 } ] },
        "a": { "client": "memory_list@1", "items": [ { "id": K5, "score": 0.9 }, { "id": K3, "score": 0.8 }, { "id": K5, "score": 0.1 }, { "id": K7, "score": 0.5 } ] },
        "c": { "client": "memory_list@1", "items": [ { "id": K9, "score": 0.1 }, { "id": K11, "score": 0.3 } ] }
        """;

    public static TheoryData<string> KeyTypes => new() { "int64", "string" };

    [Theory]
    [MemberData(nameof(KeyTypes))]
    public async Task MergesInSourceOrder_WithMaskBestRankBestScoreAndPrimary(string keyType)
    {
        var plan = Compile(keyType);

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5),
        });

        Assert.True(result.Succeeded, result.Error);
        var expected = new (int Id, long Mask, long Rank, double Score, string Primary)[]
        {
            (3, 0b011, 1, 0.95, "b"),
            (9, 0b101, 1, 0.2, "b"),
            (7, 0b001, 3, 0.6, "b"),
            (5, 0b010, 1, 0.9, "a"),
            (11, 0b100, 2, 0.3, "c"),
        };
        Assert.Equal(expected.Length, result.Items.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var item = result.Items[i];
            var e = expected[i];
            Assert.Equal(keyType == "int64" ? EntityKey.FromInt64(e.Id) : EntityKey.FromString("s" + e.Id), item.Id);
            Assert.Equal(e.Mask, item.Columns["mask"].AsInt64());
            Assert.Equal(e.Rank, item.Columns["rank"].AsInt64());
            Assert.Equal(e.Score, item.Columns["score"].AsFloat64());
            Assert.Equal(e.Primary, item.Columns["primary"].AsString());
        }
    }

    private static Pipeline.Planning.ExecutionPlan Compile(string keyType)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-recall-merge-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        var sources = Sources;
        foreach (var id in new[] { 11, 9, 7, 5, 3 })
        {
            sources = sources.Replace("K" + id, keyType == "int64" ? id.ToString() : $"\"s{id}\"", StringComparison.Ordinal);
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), $$"""{ "environment": "local", "candidate_sources": { {{sources}} } }""");
        File.WriteAllText(Path.Combine(root, "pipelines", "recall.jsonc"), $$"""
            { "pipeline_id": "recall", "entity_key_type": "{{keyType}}",
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "a": 3, "b": 10, "c": 10 }, "k": 100 },
              "nodes": { "recall": { "order": 100, "op": "MultiSourceRecall@1",
                                     "config": { "quotas_param": "recall_quotas", "source_order": ["b", "a", "c"] } } },
              "output": { "limit_param": "k", "columns": {
                "mask": "col.recall.sources_mask", "rank": "col.recall.best_rank",
                "score": "col.recall.best_score", "primary": "col.recall.primary_source" } } }
            """);

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("recall", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var compilation = PlanCompiler.Compile(request!, new Pipeline.Planning.PlanEnvironment { BaseDirectory = root });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }
}
