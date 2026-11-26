using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using static StrataRec.IntegrationTests.CombinationLimitTests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A recall source an arm adds with its quota alone (<c>params.recall_quotas.aaa</c>): the unified plan's
/// recall knows every source any arm gives a quota, and a request without the arm has no quota for it. It must serve each
/// combination as a compile of that combination does, which has never heard of the source (review 52).
/// </summary>
public sealed class ArmAddedSourceTests
{
    /// <summary>With the sample's source_order, which does not list the new source, and without one (sources by name).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASourceOnlyAnArmAdds_TakesNoBitWhereThatArmIsNotAssigned(bool sourceOrder)
    {
        // Review 52: 'aaa' comes first by name, so it took bit 1 in every request and moved popular's and two_tower's
        // bits; a baseline filter on the mask kept other rows than the baseline's own compile does.
        var host = Host();
        var pipeline = host.Request.Pipeline;
        if (!sourceOrder)
        {
            pipeline.Nodes["recall"].Config!.Remove("source_order");
        }

        pipeline.Nodes["mask_filter"] = new NodeSpec
        {
            Order = 150, Op = "ExpressionFilter@1", Config = new JsonObject { ["predicate"] = sourceOrder ? "col.recall.sources_mask == 1" : "col.recall.sources_mask == 2" },
        };
        pipeline.Output.Columns["mask"] = "col.recall.sources_mask";
        pipeline.Output.Columns["primary"] = "col.recall.primary_source";
        var request = With(host.Request, [Treatment("a", """{ "params": { "recall_quotas": { "aaa": 10 } } }""")], validateArmsIndependently: true);

        var unified = Loaded(host, bindings => PlanCompiler.Compile(request, host.Environment, bindings));

        foreach (var arms in new[] { Array.Empty<string>(), ["a/t"] })
        {
            var merged = Loaded(host, bindings => PlanCompiler.CompileMerged(request, arms, host.Environment, bindings));
            var actual = await Run(unified, unified.ArmsFromIds(arms));
            var expected = await Run(merged, default);
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ASourceListedInSourceOrder_MustHaveAQuotaWhereTheListingArmIs()
    {
        // b lists a source only a adds; a request with b and not a has no such source, which b's own compile refuses.
        var host = Host();
        var request = With(host.Request,
        [
            Treatment("a", """{ "params": { "recall_quotas": { "aaa": 10 } } }"""),
            Treatment("b", """{ "nodes": { "recall": { "config": { "source_order": ["aaa", "two_tower", "popular"] } } } }"""),
        ]);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["b/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "RECALL_UNKNOWN_SOURCE");
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment).Succeeded);

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "experiment:b", "/arms/t/nodes/recall"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("'param.recall_quotas.aaa'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceListedTwice_IsAnError()
    {
        var host = Host();
        host.Request.Pipeline.Nodes["recall"].Config!["source_order"] = new JsonArray("two_tower", "popular", "two_tower");

        var compilation = PlanCompiler.Compile(host.Request, host.Environment);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("RECALL_SOURCE_ORDER", "/nodes/recall/config/source_order/2"), (error.Code, error.Pointer));
    }

    /// <summary>The sample with a third recall service, <c>aaa</c>, bound in the environment.</summary>
    private static HomeFeedTestHost Host() => HomeFeedTestHost.LoadModified(root =>
    {
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        var text = File.ReadAllText(bindings);
        const string anchor = "\"services\": {";
        Assert.Contains(anchor, text, StringComparison.Ordinal);
        File.WriteAllText(bindings, text.Replace(anchor, anchor + """
             "aaa": { "client": "memory_list@1", "data_file": "fixtures/recall_popular.json" },
            """, StringComparison.Ordinal));
    });

    private static ExecutionPlan Loaded(HomeFeedTestHost host, Func<IPlanBindings, PlanCompilation> compile)
    {
        var lease = host.Bindings.OpenLease();
        var compilation = lease.CompileAsync(compile).GetAwaiter().GetResult();
        Assert.Empty(lease.WaitLoadedAsync().GetAwaiter().GetResult());
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static async Task<List<string>> Run(ExecutionPlan plan, RequestArms arms)
    {
        var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: arms), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        return result.Items.Select(i => $"{i.Id}:{i.Columns["mask"]}:{i.Columns["primary"]}").ToList();
    }
}
