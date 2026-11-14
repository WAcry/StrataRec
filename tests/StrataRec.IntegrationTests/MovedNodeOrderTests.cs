using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Each definition of a node is compiled at its own place on the timeline. All of a node's definitions were compiled at
/// the lowest order any of them has: an arm that moves a node after an implicit writer and reads its column was compiled
/// before the writer had declared the column, and the unified plan rejected what the arm's own compile accepts (external
/// review, P2).
/// </summary>
public sealed class MovedNodeOrderTests
{
    [Fact]
    public void AnArmThatMovesANodeAfterAnImplicitWriterAndReadsItsColumn_IsAccepted_AsItsOwnCompileIs()
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["agg"] = new NodeSpec
        {
            Order = 600, Op = "GroupAggregate@1", Config = new JsonObject { ["outputs"] = new JsonObject { ["stat"] = new JsonObject { ["fn"] = "count" } } },
        };
        host.Request.Pipeline.Nodes["keep"] = new NodeSpec
        {
            Order = 550, Op = "ExpressionFilter@1", Config = new JsonObject { ["predicate"] = "true" },
        };
        var request = CombinationLimitTests.With(host.Request, [CombinationLimitTests.Treatment("e", """
            { "nodes": { "keep": { "order": 650, "config": { "predicate": "col.stat > 0" } } } }
            """)]);
        Assert.True(PlanCompiler.CompileMerged(request, ["e/t"], host.Environment).Succeeded);

        var unified = PlanCompiler.Compile(request, host.Environment);

        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
    }

    /// <summary>
    /// The order is the JSON number, however the tree holds it: one built in code holds a CLR int, not the parsed tree's
    /// JSON element, and only a long was read, so the moved writer was compiled last, after the reader (review 49).
    /// </summary>
    [Theory]
    [InlineData("parsed")]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("double")]
    public void AWriterMovedBeforeItsReader_IsCompiledThere_HoweverTheTreeHoldsTheOrder(string order)
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["agg"] = new NodeSpec
        {
            Order = 600, Op = "GroupAggregate@1", Config = new JsonObject { ["outputs"] = new JsonObject { ["stat"] = new JsonObject { ["fn"] = "count" } } },
        };
        host.Request.Pipeline.Nodes["keep"] = new NodeSpec
        {
            Order = 700, Op = "ExpressionFilter@1", Config = new JsonObject { ["predicate"] = "true" },
        };
        var patch = JsonNode.Parse("""
            { "nodes": {
              "agg": { "order": 650, "config": { "outputs": { "other": { "fn": "count" } } } },
              "keep": { "config": { "predicate": "col.other > 0" } }
            } }
            """)!.AsObject();
        patch["nodes"]!["agg"]!["order"] = order switch
        {
            "int" => JsonValue.Create(650),
            "long" => JsonValue.Create(650L),
            "double" => JsonValue.Create(650.0),
            _ => JsonNode.Parse("650"),
        };
        var experiment = new ExperimentArtifact // as a host builds it: not through JSON text, which would parse the number
        {
            ExperimentId = "e", PipelineId = "home_feed", Owner = "t", Description = "d", ExpiresAt = "2099-01-01",
            Arms = new Dictionary<string, JsonObject> { ["t"] = patch },
        };
        var request = CombinationLimitTests.With(host.Request, [new ExperimentPlacement(experiment, "layer_e")]);

        Assert.True(PlanCompiler.CompileMerged(request, ["e/t"], host.Environment).Succeeded);
        var unified = PlanCompiler.Compile(request, host.Environment);
        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
    }
}
