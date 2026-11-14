using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Review 44: an undeclared output takes its type from the first variant that writes it, and every arm combination runs on
/// that one schema. An arm that writes it as another type (count to mean, or a group key that may be missing) was compiled
/// against the first type: the mean was written as an integer, a missing count failed the request. It is an error at
/// compile time now, and a declaration under /columns that both can write is the fix.
/// </summary>
public sealed class ImplicitColumnVariantTests
{
    [Fact]
    public void AnArmThatChangesAnImplicitOutputsKind_IsAnError()
    {
        var (request, environment) = Request("""{ "nodes": { "agg": { "config": { "outputs": { "stat": { "fn": "mean", "value_col": "score.final" } } } } } }""");

        var compilation = PlanCompiler.Compile(request, environment);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("COLUMN_TYPE_MISMATCH", "/arms/t/nodes/agg/config/outputs/stat"), (error.Code, error.Pointer)); // at the arm
        Assert.Contains("one type in every arm combination", error.Message);
        Assert.Equal("write one of them to another column", error.Hint);
    }

    [Fact]
    public void AnArmThatMakesAnImplicitOutputNullable_IsAnError_AndADeclarationBothCanWrite_IsTheFix()
    {
        // The arm groups by item.author_id, which may be missing: its count may be missing, the baseline's may not.
        var (request, environment) = Request("""{ "nodes": { "agg": { "config": { "group_col": "item.author_id" } } } }""");

        var error = Assert.Single(PlanCompiler.Compile(request, environment).Diagnostics, d => d.IsError);
        Assert.Equal("COLUMN_TYPE_MISMATCH", error.Code);
        Assert.Equal("declare it under /columns as int64?, which both can write", error.Hint);

        request.Pipeline.Columns["stat"] = new ColumnSpec { Type = "int64?" };
        var unified = PlanCompiler.Compile(request, environment);
        var merged = PlanCompiler.CompileMerged(request, ["e/t"], environment);

        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
        Assert.True(merged.Succeeded, string.Join("\n", merged.Diagnostics));
    }

    /// <summary>home_feed with a count of every row into the undeclared <c>stat</c>, and one experiment, e/t, patching it.</summary>
    private static (PlanCompileRequest, PlanEnvironment) Request(string patch)
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["agg"] = new NodeSpec
        {
            Order = 600, Op = "GroupAggregate@1", Config = new JsonObject { ["outputs"] = new JsonObject { ["stat"] = new JsonObject { ["fn"] = "count" } } },
        };
        return (CombinationLimitTests.With(host.Request, [CombinationLimitTests.Treatment("e", patch)]), host.Environment);
    }
}
