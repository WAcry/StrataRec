using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;

namespace StrataRec.IntegrationTests;

public sealed class TopKInt64Tests
{
    [Theory]
    [InlineData(false, new[] { 9007199254740993L, 9007199254740992L })]
    [InlineData(true, new[] { 9007199254740992L, 9007199254740993L })]
    public async Task EntityIdsAbove2To53_RankByTheirValue(bool ascending, long[] expected)
    {
        // Artifacts are RFC 8785 JSON, which cannot hold such integers; the ids come from the source's data, as in
        // production.
        var files = new Dictionary<string, string> { ["fixtures/ids.json"] = """{ "items": [9007199254740992, 9007199254740993] }""" };
        var plan = MiniWorkspace.Compile("\"s\": { \"client\": \"memory_list@1\", \"data_file\": \"fixtures/ids.json\" }", $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "s": 10 }, "k": 10 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "top": { "order": 200, "op": "TopK@1", "config": { "score_col": "entity_id", "k": 2, "ascending": {{(ascending ? "true" : "false")}} } } },
              "output": { "limit_param": "k", "columns": { "source": "col.recall.primary_source" } } }
            """, files: files);

        var result = await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest { RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5) },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expected, result.Items.Select(i => i.Id.Int64Value));
    }
}
