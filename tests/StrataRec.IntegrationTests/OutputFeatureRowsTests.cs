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
/// The features an output column reads are filled for the returned rows only (design 3.4): the first <c>limit</c> rows of
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

    private static Task<PipelineResult> Execute(ExecutionPlan plan) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000,
    }, TestContext.Current.CancellationToken).AsTask();

    /// <summary>
    /// Candidates 1 and 2, in that order, and an output of <paramref name="limit"/> rows whose one column is
    /// <c>item.narrow</c>: <c>item.base * 1500000000</c>, a non-nullable int32 with no default, so a value that does not fit
    /// fails it. <c>item.base</c> is fetched; the keys of each call are returned.
    /// </summary>
    private static (ExecutionPlan Plan, ConcurrentQueue<long[]> Calls) Compile(int limit, long baseOf2, long baseOf1 = 1)
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
                "item_store": { "connector": "{{CountingConnectors.Type}}", "data": { "1": { "base": {{baseOf1}} }, "2": { "base": {{baseOf2}} } }, "timeout_ms": 60000 } },
              "candidate_sources": { "pool": { "client": "memory_list@1", "items": [1, 2] } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), $$"""
            { "pipeline_id": "p",
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "pool": 10 }, "k": {{limit}} },
              "nodes": { "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } } },
              "output": { "limit_param": "k", "columns": { "narrow": "col.item.narrow" } } }
            """);

        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var connectors = new CountingConnectors();
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment
        {
            BaseDirectory = root,
            Connectors = FeatureConnectorRegistry.CreateDefault().Add(connectors),
        });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return (compilation.Plan!, connectors.Calls);
    }

    /// <summary><c>counting_memory@1</c>: <c>memory@1</c>, recording the keys of every call.</summary>
    private sealed class CountingConnectors : IFeatureConnectorFactory
    {
        public const string Type = "counting_memory@1";

        private readonly MemoryFeatureConnectorFactory _memory = new();

        public ConcurrentQueue<long[]> Calls { get; } = new();

        public string ConnectorType => Type;

        public Type SettingsType => _memory.SettingsType;

        public IFeatureSourceConnector Create(BindingContext context) => new Connector(_memory.Create(context), Calls);

        private sealed class Connector(IFeatureSourceConnector inner, ConcurrentQueue<long[]> calls) : IFeatureSourceConnector
        {
            public string ConnectorType => Type;

            public bool Supports(EntityKeyLayout layout) => inner.Supports(layout);

            public ValueTask<FetchStatus> FetchAsync(
                EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
            {
                calls.Enqueue(Enumerable.Range(0, keys.Count).Select(k => keys.Key(k, 0).Int64Value).ToArray());
                return inner.FetchAsync(keys, fields, writer, cancellationToken);
            }
        }
    }
}
