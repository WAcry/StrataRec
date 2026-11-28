using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

public sealed class BaselineAloneTests
{
    [Fact]
    public void ABaselineThatReadsAColumnOnlyANodeOfAnArmDeclares_IsRejected()
    {
        // The count may be missing (its group key may be), so the column is nullable: a combination without its producer
        // reads it as missing, which the combination check allows.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["keep"] = new NodeSpec
        {
            Order = 650, Op = "ExpressionFilter@1", Config = new JsonObject { ["predicate"] = "(col.stat ?? 1) > 0" },
        };
        var request = CombinationLimitTests.With(host.Request, [CombinationLimitTests.Treatment("e", """
            { "nodes": { "agg": { "order": 600, "op": "GroupAggregate@1", "config": { "group_col": "item.author_id", "outputs": { "stat": { "fn": "count" } } } } } }
            """)]);
        Assert.Contains(PlanCompiler.CompileMerged(request, [], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");
        Assert.True(PlanCompiler.CompileMerged(request, ["e/t"], host.Environment).Succeeded);

        var unified = PlanCompiler.Compile(request, host.Environment);

        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("EXPR_UNKNOWN_NAME", "/nodes/keep/config/predicate"), (error.Code, error.Pointer));
    }

    [Fact]
    public void TheSample_CompilesWithoutAnySubsetOfItsExperiments()
    {
        var host = HomeFeedTestHost.Load();
        var experiments = host.Request.Experiments;
        for (var removed = 0; removed < 1 << experiments.Count; removed++)
        {
            var kept = new List<ExperimentPlacement>();
            for (var i = 0; i < experiments.Count; i++)
            {
                if ((removed & (1 << i)) == 0)
                {
                    kept.Add(experiments[i]);
                }
            }

            var compilation = PlanCompiler.Compile(CombinationLimitTests.With(host.Request, kept), host.Environment);
            Assert.True(compilation.Succeeded, $"without {removed:b}: " + string.Join("\n", compilation.Diagnostics));
        }
    }
}
