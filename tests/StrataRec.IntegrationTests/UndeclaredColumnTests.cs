using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A column written without a declaration exists only where a node writing it compiles before its reader (design 11.5
/// check 3). The unified plan compiles each node once on a schema every arm's writers declare, so one arm removing or
/// moving the writer and another, in another layer, adding a reader of the nullable column was accepted, while the merged
/// config of the two is refused (found by <see cref="GeneratedExperimentTests"/>).
/// </summary>
public sealed class UndeclaredColumnTests
{
    /// <summary>Arm b/t takes the sample's author rank away from where a/t reads it, each valid alone.</summary>
    [Theory]
    [InlineData("""{ "author_rank": null, "author_cap": null }""", "here no node writes it")]
    [InlineData("""{ "author_rank": { "order": 530 }, "author_cap": { "order": 535 } }""", "here 'author_rank' (order 530) writes it after")]
    public void AReaderWhoseWriterAnotherLayersArmTakesAway_IsRejected_ForThatCombination(string removal, string why)
    {
        var host = HomeFeedTestHost.Load();
        var request = CombinationLimitTests.With(host.Request,
        [
            CombinationLimitTests.Treatment("a", """
                { "nodes": { "read": { "order": 525, "op": "ExpressionFilter@1", "config": { "predicate": "(col.group.author_rank ?? 1) <= 3" } } } }
                """),
            CombinationLimitTests.Treatment("b", $$"""{ "nodes": {{removal}} }"""),
        ]);
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t"], host.Environment).Succeeded);
        Assert.True(PlanCompiler.CompileMerged(request, ["b/t"], host.Environment).Succeeded);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("COLUMN_NOT_DECLARED", "experiment:a", "/arms/t/nodes/read"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("'group.author_rank'", error.Message, StringComparison.Ordinal);
        Assert.Contains(why, error.Message, StringComparison.Ordinal);
        Assert.EndsWith("(together with b/t)", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"group.author_rank\": { \"type\": \"int64?\" }", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredColumn_ExistsInEveryCombination()
    {
        // The hint's fix: declared under /columns, the column exists wherever the reader does, missing where nothing writes it.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Columns["group.author_rank"] = new ColumnSpec { Type = "int64?" };
        var request = CombinationLimitTests.With(host.Request,
        [
            CombinationLimitTests.Treatment("a", """
                { "nodes": { "read": { "order": 525, "op": "ExpressionFilter@1", "config": { "predicate": "(col.group.author_rank ?? 1) <= 3" } } } }
                """),
            CombinationLimitTests.Treatment("b", """{ "nodes": { "author_rank": null, "author_cap": null } }"""),
        ]);

        Assert.True(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment).Succeeded);
        var unified = PlanCompiler.Compile(request, host.Environment);
        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
    }

    [Fact]
    public void TheOutputReadingAColumnAnArmTakesAway_IsRejected_EvenWithoutCheckingArmsAlone()
    {
        // The output compiles after every node, so any writer declares what it reads; an arm without one leaves it unknown.
        // Each arm alone would show it too, but that check can be turned off (a thousand arms): the combinations do not.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Output.Columns["author_rank"] = "col.group.author_rank";
        var request = CombinationLimitTests.With(host.Request,
            [CombinationLimitTests.Treatment("b", """{ "nodes": { "author_rank": null, "author_cap": null } }""")]);
        Assert.False(request.ValidateArmsIndependently);

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("COLUMN_NOT_DECLARED", "/output/columns/author_rank"), (error.Code, error.Pointer));
        Assert.Contains("b/t", error.Artifact, StringComparison.Ordinal);
    }
}
