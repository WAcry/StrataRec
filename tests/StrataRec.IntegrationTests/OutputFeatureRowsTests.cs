using System.Collections.Concurrent;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The features an output column reads are filled for the returned rows only: the first <c>limit</c> rows of
/// the final order. A row that is not returned is read by nobody, so nothing is fetched or computed for it, and a value of
/// it that cannot be served does not fail the request (external assessment: with candidates 1 and 2 and a limit of 1, item
/// 2's derived value failed the request although only item 1 was returned).
/// </summary>
public class OutputFeatureRowsTests
{
    [Fact]
    public async Task AValueOfARowNotReturned_ThatTheTypeCannotHold_DoesNotFailTheRequest_AndItsKeyIsNotFetched()
    {
        var (plan, calls) = Compile(limit: 1, baseOf2: 2); // item 1: 1.5e9 fits int32; item 2: 3e9 does not

        var result = await Execute(plan);

        Assert.True(result.Succeeded, result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(1, item.Id.Int64Value);
        Assert.Equal(1_500_000_000, item.Columns["narrow"].AsInt64());
        Assert.Equal(0, result.OutOfRangeValues);
        Assert.Equal(new long[] { 1 }, Assert.Single(calls));
    }

    [Fact]
    public async Task ALimitOfZero_ReturnsNoRows_AndFetchesNothingForTheOutput()
    {
        var (plan, calls) = Compile(limit: 0, baseOf2: 2);

        var result = await Execute(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Items);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task AReturnedRowsValue_ThatTheTypeCannotHold_StillFailsTheRequest()
    {
        var (plan, _) = Compile(limit: 1, baseOf1: 2, baseOf2: 1); // item 1 (returned): 3e9

        var result = await Execute(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("output reads a feature that failed", result.Error);
        Assert.Contains("item.narrow", result.Error);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task EveryRowReturned_IsFilled_AsBefore()
    {
        var (plan, calls) = Compile(limit: 5, baseOf2: 1);

        var result = await Execute(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 1, 2 }, result.Items.Select(i => i.Id.Int64Value));
        Assert.All(result.Items, i => Assert.Equal(1_500_000_000, i.Columns["narrow"].AsInt64()));
        Assert.Equal(new long[] { 1, 2 }, Assert.Single(calls));
    }

    /// <summary>
    /// The first recall appends item 1 and a scorer fills its <c>item.base</c>; a second recall appends item 2,
    /// and the next node's fallback declares <c>item.base</c>, so a prefetch of key 2 starts, which fails. The node itself
    /// succeeds and the fallback never runs. The output returns item 1 alone: its features were filled, and the prefetch
    /// for item 2 is not waited for (waiting set <c>item.base</c> failed and failed the request). When item 2 is returned
    /// too, the prefetch is waited for, its failure fails the request, and key 2 is not fetched again.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task APrefetchForARowNotReturned_ThatFails_DoesNotFailTheOutput(int limit)
    {
        var (plan, calls) = CompileWithFallback(limit);

        var result = await Execute(plan);

        Assert.Equal(new long[][] { [1], [2] }, calls);
        if (limit == 1)
        {
            Assert.True(result.Succeeded, result.Error);
            var item = Assert.Single(result.Items);
            Assert.Equal(1, item.Id.Int64Value);
            Assert.Equal(1_500_000_000, item.Columns["narrow"].AsInt64());
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Contains("output reads a feature that failed", result.Error);
            Assert.Contains("key 2 failed", result.Error);
        }
    }

    /// <summary>
    /// The first recall appends item 1; the second (no fetch barrier) appends item 2 after the prefetch of the
    /// next node's fallback's <c>item.base</c> started, for key 1, which fails. The node succeeds, the fallback never runs,
    /// and a filter keeps item 2 alone. The output fills item 2, which the prefetch never asked for: it is not waited for
    /// (waiting failed the request with key 1's failure, the connector never asked for key 2), and key 2 is fetched.
    /// </summary>
    [Fact]
    public async Task APrefetchOfOtherKeys_ThatFails_DoesNotFailTheOutput_WhoseRowsItNeverAskedFor()
    {
        var (plan, calls) = Compile(
            1,
            1,
            """{ "first": { "client": "memory_list@1", "items": [1] }, "second": { "client": "memory_list@1", "items": [2] } }""",
            """
            { "pipeline_id": "p",
              "columns": { "score.t": { "type": "float64", "default": 0.0 } },
              "param_schema": { "q1": { "type": "map<string,int64>" }, "q2": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "q1": { "first": 10 }, "q2": { "second": 10 }, "k": 1 },
              "nodes": {
                "recall_first": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q1" } },
                "recall_second": { "order": 200, "op": "MultiSourceRecall@1", "fetch_barrier": false, "config": { "quotas_param": "q2" } },
                "next": { "order": 300, "op": "ExpressionScorer@1", "on_failure": "fallback", "fallback": "backup",
                  "config": { "output_col": "score.t", "expr": "1.0" } },
                "keep": { "order": 400, "op": "ExpressionFilter@1", "config": { "predicate": "col.entity_id == 2" } },
                "backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.t", "expr": "double(col.item.base)" } } },
              "output": { "limit_param": "k", "columns": { "narrow": "col.item.narrow" } } }
            """,
            failing: 1);

        var result = await Execute(plan);

        Assert.True(result.Succeeded, result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(2, item.Id.Int64Value);
        Assert.Equal(1_500_000_000, item.Columns["narrow"].AsInt64());
        Assert.Equal(new long[][] { [1], [2] }, calls);
    }

    private static Task<PipelineResult> Execute(ExecutionPlan plan) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000,
    }, TestContext.Current.CancellationToken).AsTask();

    /// <summary>
    /// Candidates 1 and 2, in that order, and an output of <paramref name="limit"/> rows whose one column is
    /// <c>item.narrow</c>: <c>item.base * 1500000000</c>, a non-nullable int32 with no default, so a value that does not fit
    /// fails it. <c>item.base</c> is fetched; the keys of each call are returned.
    /// </summary>
    private static (ExecutionPlan Plan, ConcurrentQueue<long[]> Calls) Compile(int limit, long baseOf2, long baseOf1 = 1) =>
        Compile(
            baseOf1,
            baseOf2,
            """{ "pool": { "client": "memory_list@1", "items": [1, 2] } }""",
            $$"""
            { "pipeline_id": "p",
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "pool": 10 }, "k": {{limit}} },
              "nodes": { "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } } },
              "output": { "limit_param": "k", "columns": { "narrow": "col.item.narrow" } } }
            """);

    /// <summary>
    /// The pipeline: recall of item 1, a scorer reading <c>item.base</c>, recall of item 2, then a node whose
    /// fallback reads <c>item.base</c>; the output of <paramref name="limit"/> rows reads <c>item.narrow</c>. The source
    /// fails every call for key 2.
    /// </summary>
    private static (ExecutionPlan Plan, ConcurrentQueue<long[]> Calls) CompileWithFallback(int limit) =>
        Compile(
            1,
            1,
            """{ "first": { "client": "memory_list@1", "items": [1] }, "second": { "client": "memory_list@1", "items": [2] } }""",
            $$"""
            { "pipeline_id": "p",
              "columns": { "score.s": { "type": "float64", "default": 0.0 }, "score.t": { "type": "float64", "default": 0.0 } },
              "param_schema": { "q1": { "type": "map<string,int64>" }, "q2": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "q1": { "first": 10 }, "q2": { "second": 10 }, "k": {{limit}} },
              "nodes": {
                "recall_first": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q1" } },
                "score": { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "score.s", "expr": "double(col.item.base)" } },
                "recall_second": { "order": 300, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q2" } },
                "next": { "order": 400, "op": "ExpressionScorer@1", "on_failure": "fallback", "fallback": "backup",
                  "config": { "output_col": "score.t", "expr": "1.0" } },
                "backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.t", "expr": "double(col.item.base)" } } },
              "output": { "limit_param": "k", "columns": { "narrow": "col.item.narrow" } } }
            """,
            failing: 2);

    private static (ExecutionPlan Plan, ConcurrentQueue<long[]> Calls) Compile(
        long baseOf1, long baseOf2, string candidateSources, string pipeline, long? failing = null)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-output-rows-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """
            { "catalog_id": "c",
              "entities": { "item": { "key": "col.entity_id" } },
              "features": { "item.base": { "type": "int64", "source": "item_store", "field": "base", "default": 0 } },
              "derived_features": { "item.narrow": { "type": "int32", "expr": "col.item.base * 1500000000" } } }
            """);
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), $$"""
            { "environment": "local",
              "sources": {
                "item_store": { "connector": "{{CountingConnectors.TypeName}}", "data": { "1": { "base": {{baseOf1}} }, "2": { "base": {{baseOf2}} } }, "timeout_ms": 60000 } },
              "services": {{candidateSources}} }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), pipeline);

        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var connectors = new CountingConnectors(failing);
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment
        {
            BaseDirectory = root,
            Bindings = PlanEnvironment.DefaultBindings().Add(connectors),
        });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return (compilation.Plan!, connectors.Calls);
    }

    /// <summary>
    /// <c>counting_memory@1</c>: <c>memory@1</c>, recording the keys of every call, and failing a call that asks for
    /// <paramref name="failing"/>.
    /// </summary>
    private sealed class CountingConnectors(long? failing = null) : IBindingFactory<IFeatureSourceConnector>
    {
        public const string TypeName = "counting_memory@1";

        private readonly MemoryFeatureConnectorFactory _memory = new();

        public ConcurrentQueue<long[]> Calls { get; } = new();

        public string Type => TypeName;

        public Type SettingsType => _memory.SettingsType;

        public IFeatureSourceConnector Create(BindingContext context) => new Connector(_memory.Create(context), Calls, failing);

        private sealed class Connector(IFeatureSourceConnector inner, ConcurrentQueue<long[]> calls, long? failing) : IFeatureSourceConnector
        {
            public string ConnectorType => TypeName;

            public bool Supports(EntityKeyLayout layout) => inner.Supports(layout);

            public ValueTask<FetchStatus> FetchAsync(
                EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
            {
                var asked = Enumerable.Range(0, keys.Count).Select(k => keys.Key(k, 0).Int64Value).ToArray();
                calls.Enqueue(asked);
                return asked.Contains(failing ?? long.MinValue)
                    ? ValueTask.FromResult(FetchStatus.Failed($"key {failing} failed"))
                    : inner.FetchAsync(keys, fields, writer, cancellationToken);
            }
        }
    }
}
