using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A row source's fallback gets table capacity of its own (design 4.8, 5.6): the request reserves the primary's row limit
/// and the fallback's, because a primary that failed may already have appended physical rows, and a failure restores only
/// the selection.
/// </summary>
public sealed class RowSourceFallbackTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AFallbackRecall_ReturnsItsWholeQuota_WhenThePrimarysQuotaIsSmaller()
    {
        // The reported case: the primary (quota 1) fails, the fallback (quota 3) answers [11, 22, 33]. With room for the
        // primary's single row only, the fallback was cut to [11].
        var plan = MiniWorkspace.Compile(Sources("\"primary\": { \"client\": \"memory_list@1\", \"fail\": true, \"items\": [7] }"), Pipeline(primaryTimeoutMs: null));

        var result = await Execute(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 11, 22, 33 }, result.Items.Select(i => i.Id.Int64Value));
        Assert.Equal("recall_backup", result.Outcomes.Single(o => o.NodeId == "recall").ProducedBy);
    }

    private static async Task<PipelineResult> Execute(ExecutionPlan plan) =>
        await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest { RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(600) })
            .AsTask().WaitAsync(Patience);

    private static string Sources(string primary) =>
        primary + """, "backup": { "client": "memory_list@1", "items": [11, 22, 33] }""";

    private static string Pipeline(int? primaryTimeoutMs) => $$"""
        { "pipeline_id": "p", "entity_key_type": "int64",
          "param_schema": { "primary_quotas": { "type": "map<string,int64>" }, "backup_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
          "params": { "primary_quotas": { "primary": 1 }, "backup_quotas": { "backup": 3 }, "k": 3 },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "on_failure": "fallback:recall_backup"{{(primaryTimeoutMs is { } t ? $", \"timeout_ms\": {t}" : "")}},
                        "config": { "quotas_param": "primary_quotas" } },
            "recall_backup": { "role": "fallback", "op": "MultiSourceRecall@1", "config": { "quotas_param": "backup_quotas" } } },
          "output": { "limit_param": "k", "columns": { "primary": "col.recall.primary_source" } } }
        """;

}
