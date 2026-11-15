using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Experiments.Compilation;
using StrataRec.Expressions;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using static StrataRec.IntegrationTests.CombinationLimitTests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Every name a node binds must be declared in each arm combination before the node compiles, as a compile of that
/// combination alone requires (design 11.5 check 3, 16.2): by the baseline, by an arm assigned with it, or, for a column
/// without a declaration, by a node compiled before it that writes it. The unified plan compiles each node once on a
/// schema holding what any arm declares, so each case here was accepted while the combination's merged config is refused:
/// a writer another layer's arm takes away (found by <see cref="GeneratedExperimentTests"/>), a node reading its own output
/// before writing it (review 50), a declaration by an arm not assigned with the reader, a name in a dead branch, a write
/// that needs a declaration, a parameter another arm adds (review 51).
/// </summary>
public sealed class DeclarationTests
{
    /// <summary>Arm b/t takes the sample's author rank away from where a/t reads it, each valid alone.</summary>
    [Theory]
    [InlineData("""{ "author_rank": null, "author_cap": null }""", "here no node writes it")]
    [InlineData("""{ "author_rank": { "order": 530 }, "author_cap": { "order": 535 } }""", "here 'author_rank' (order 530) writes it after")]
    public void AReaderWhoseWriterAnotherLayersArmTakesAway_IsRejected_ForThatCombination(string removal, string why)
    {
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request,
        [
            Treatment("a", """
                { "nodes": { "read": { "order": 525, "op": "ExpressionFilter@1", "config": { "predicate": "(col.group.author_rank ?? 1) <= 3" } } } }
                """),
            Treatment("b", $$"""{ "nodes": {{removal}} }"""),
        ]);
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t"], host.Environment).Succeeded);
        Assert.True(PlanCompiler.CompileMerged(request, ["b/t"], host.Environment).Succeeded);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "experiment:a", "/arms/t/nodes/read"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("'col.group.author_rank'", error.Message, StringComparison.Ordinal);
        Assert.Contains(why, error.Message, StringComparison.Ordinal);
        Assert.EndsWith("(together with b/t)", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"group.author_rank\": { \"type\": \"int64?\" }", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ANodeReadingItsOwnOutputBeforeWritingIt_IsRejected_ThoughAnotherVariantDeclaredIt()
    {
        // Review 50: GroupAggregate@1 reads its value columns before it writes its outputs, so a value column naming its own
        // output is unknown when the arm's config is compiled alone; the baseline's variant had declared it in the unified
        // schema, and a read of a column the node writes was let through. Each arm alone is not compiled here (With).
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["selfagg"] = new NodeSpec
        {
            Order = 600, Op = "GroupAggregate@1",
            Config = JsonNode.Parse("""{ "group_col": "item.author_id", "outputs": { "x": { "fn": "sum", "value_col": "score.final" } } }""")!.AsObject(),
        };
        var request = With(host.Request,
            [Treatment("a", """{ "nodes": { "selfagg": { "config": { "outputs": { "x": { "value_col": "x" } } } } } }""")]);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "COLUMN_UNKNOWN");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "experiment:a", "/arms/t/nodes/selfagg"), (error.Code, error.Artifact, error.Pointer));
    }

    [Fact]
    public void ANodeReadingWhatItWroteEarlierInItsCompile_IsAccepted()
    {
        // Its outputs compile in order: 'a_self' is declared by the node itself when 'b_self' reads it.
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request, [Treatment("a", """
            { "nodes": { "selfagg": { "order": 600, "op": "GroupAggregate@1", "config": { "group_col": "item.author_id",
              "outputs": { "a_self": { "fn": "count" }, "b_self": { "fn": "sum", "value_col": "a_self" } } } } } }
            """)]);
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t"], host.Environment).Succeeded);

        var unified = PlanCompiler.Compile(request, host.Environment);

        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
    }

    [Fact]
    public void ADeclaredColumn_ExistsInEveryCombination()
    {
        // The hint's fix: declared under /columns, the column exists wherever the reader does, missing where nothing writes it.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Columns["group.author_rank"] = new ColumnSpec { Type = "int64?" };
        var request = With(host.Request,
        [
            Treatment("a", """
                { "nodes": { "read": { "order": 525, "op": "ExpressionFilter@1", "config": { "predicate": "(col.group.author_rank ?? 1) <= 3" } } } }
                """),
            Treatment("b", """{ "nodes": { "author_rank": null, "author_cap": null } }"""),
        ]);

        Assert.True(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment).Succeeded);
        var unified = PlanCompiler.Compile(request, host.Environment);
        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
    }

    /// <summary>The second is review 51's: the optimizer drops the branch, the name must still be declared to bind.</summary>
    [Theory]
    [InlineData("col.group.author_rank")]
    [InlineData("true ? 7 : (col.group.author_rank ?? 0)")]
    public void TheOutputReadingAColumnAnArmTakesAway_IsRejected_EvenWithoutCheckingArmsAlone(string expression)
    {
        // The output compiles after every node, so any writer declares what it reads; an arm without one leaves it unknown.
        // Each arm alone would show it too, but that check can be turned off (a thousand arms): the combinations do not.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Output.Columns["author_rank"] = expression;
        var request = With(host.Request, [Treatment("b", """{ "nodes": { "author_rank": null, "author_cap": null } }""")]);
        Assert.False(request.ValidateArmsIndependently);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["b/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "/output/columns/author_rank"), (error.Code, error.Pointer));
        Assert.Contains("b/t", error.Artifact, StringComparison.Ordinal);
    }

    [Fact]
    public void AColumnDeclaredOnlyByAnArmNotAssignedWithItsReader_IsNotDeclaredThere()
    {
        // Review 51 P1: a declares the column the baseline's writer declares too, b reads it, c deletes the writer; each arm
        // is valid alone and checked so. With b and c but not a, the name is unknown. The unified schema held a's
        // declaration for every combination, so the column did not count as one needing a writer: the plan was accepted,
        // and revoking a published a build no host could load.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["expwriter"] = new NodeSpec
        {
            Order = 600, Op = "GroupAggregate@1",
            Config = JsonNode.Parse("""{ "group_col": "item.author_id", "outputs": { "explicit_probe": { "fn": "count" } } }""")!.AsObject(),
        };
        var request = With(host.Request,
        [
            Treatment("a", """{ "columns": { "explicit_probe": { "type": "int64?" } } }"""),
            Treatment("b", """
                { "nodes": { "expreader": { "order": 650, "op": "ExpressionFilter@1", "config": { "predicate": "(col.explicit_probe ?? 0) > 0" } } } }
                """),
            Treatment("c", """{ "nodes": { "expwriter": null } }"""),
        ], validateArmsIndependently: true);
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t", "b/t", "c/t"], host.Environment).Succeeded);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["b/t", "c/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "experiment:b", "/arms/t/nodes/expreader"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("only 'a/t' declares it, and it is not assigned with it here; no node writes it before", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("(together with c/t)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Review 51: the binder resolves every name; the optimizer then drops the branch, and the reads with it.</summary>
    [Theory]
    [InlineData("true || ((col.group.author_rank ?? 1) <= 3)")]
    [InlineData("false ? ((col.group.author_rank ?? 1) <= 3) : true")]
    [InlineData("let unused = col.group.author_rank; true")]
    public void ANameInABranchTheOptimizerDrops_MustBeDeclaredToo(string predicate)
    {
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request,
        [
            Treatment("a", $$"""{ "nodes": { "read": { "order": 525, "op": "ExpressionFilter@1", "config": { "predicate": "{{predicate}}" } } } }"""),
            Treatment("b", """{ "nodes": { "author_rank": null, "author_cap": null } }"""),
        ], validateArmsIndependently: true);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "experiment:a", "/arms/t/nodes/read"), (error.Code, error.Artifact, error.Pointer));
    }

    [Fact]
    public void AFallbackReadingAColumnAnArmTakesAway_IsRejected_InABranchTheOptimizerDrops()
    {
        // Fallbacks compile after the timeline, so any writer there declares what they read.
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request,
        [
            Treatment("a", """
                { "nodes": { "ctr_backup": { "config": { "expr": "true || ((col.group.author_rank ?? 0) > 0) ? col.item.smoothed_ctr : 0.0" } } } }
                """),
            Treatment("b", """{ "nodes": { "author_rank": null, "author_cap": null } }"""),
        ], validateArmsIndependently: true);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal("NAME_NOT_DECLARED", error.Code);
        Assert.StartsWith("Fallback 'ctr_backup' reads 'col.group.author_rank'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteThatNeedsADeclaration_DoesNotDeclareTheColumnItWrites()
    {
        // Review 51: an extension writes its column with a type of its own unless both options are on; then it writes it
        // as declared (TryWriteColumn without an implicit type). Each arm alone declares it; a and b together have no
        // declaration, though the unified schema had one from the other variants.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["selfdecl"] = new NodeSpec { Order = 600, Op = "SelfDeclaring@1", Config = new JsonObject() };
        var request = With(host.Request,
        [
            Treatment("a", """{ "nodes": { "selfdecl": { "config": { "left": true } } } }"""),
            Treatment("b", """{ "nodes": { "selfdecl": { "config": { "right": true } } } }"""),
        ], validateArmsIndependently: true);
        var environment = new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new SelfDeclaringFactory()) };
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t"], environment).Succeeded);
        Assert.True(PlanCompiler.CompileMerged(request, ["b/t"], environment).Succeeded);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t", "b/t"], environment).Diagnostics, d => d.IsError && d.Code == "COLUMN_UNDECLARED");

        var unified = PlanCompiler.Compile(request, environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal("NAME_NOT_DECLARED", error.Code);
        Assert.StartsWith("Node 'selfdecl' reads 'col.needs_decl'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AParameterAnotherArmAdds_IsNotDeclaredForAReaderWithoutIt()
    {
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request,
        [
            Treatment("a", """{ "param_schema": { "probe_p": { "type": "int64", "min": 0, "max": 10 } }, "params": { "probe_p": 3 } }"""),
            Treatment("b", """{ "nodes": { "pread": { "order": 525, "op": "ExpressionFilter@1", "config": { "predicate": "param.probe_p > 0" } } } }"""),
        ]);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["b/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "experiment:b", "/arms/t/nodes/pread"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("'param.probe_p'", error.Message, StringComparison.Ordinal);
        Assert.Contains("only 'a/t' declares it", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PastTheLimit_ANameOnlyThreeLayersTogetherLeaveUndeclared_IsFoundInItsScope()
    {
        // Joint reads y only when a and b both switch it on; c deletes y's writer. Every arm and every pair is legal; four
        // more layers make 128 combinations, past the limit of 8, which the three deciding layers fit (review 51: the
        // generated space never checked a scope it could enumerate).
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.Columns["z"] = new ColumnSpec { Type = "float64?" };
        pipeline.Nodes["ywriter"] = new NodeSpec
        {
            Order = 840, Op = "GroupAggregate@1",
            Config = JsonNode.Parse("""{ "group_col": "item.author_id", "outputs": { "y": { "fn": "mean", "value_col": "score.final" } } }""")!.AsObject(),
        };
        pipeline.Nodes["joint"] = new NodeSpec
        {
            Order = 850, Op = "Joint@1", Config = new JsonObject { ["left"] = false, ["right"] = false, ["output"] = "z", ["input"] = "y" },
        };
        var placements = new List<ExperimentPlacement>
        {
            Treatment("a", """{ "nodes": { "joint": { "config": { "left": true } } } }"""),
            Treatment("b", """{ "nodes": { "joint": { "config": { "right": true } } } }"""),
            Treatment("c", """{ "nodes": { "ywriter": null } }"""),
        };
        placements.AddRange(Filters(4));
        var request = With(host.Request, placements, maxCombinationsChecked: 8);
        var environment = JointEnvironment(host);
        foreach (var pair in new[] { new[] { "a/t", "b/t" }, new[] { "a/t", "c/t" }, new[] { "b/t", "c/t" } })
        {
            var merged = PlanCompiler.CompileMerged(request, pair, environment);
            Assert.True(merged.Succeeded, string.Join(" + ", pair) + ": " + string.Join("\n", merged.Diagnostics));
        }

        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t", "b/t", "c/t"], environment).Diagnostics, d => d.IsError && d.Code == "EXPR_UNKNOWN_NAME");

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Report.CombinationCheckExhaustive);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal("NAME_NOT_DECLARED", error.Code);
        Assert.StartsWith("Node 'joint' reads 'col.y'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteDeclaringAColumnAnotherArmDeclaresOtherwise_IsATypeMismatch_WhereThatArmIsNotAssigned()
    {
        // Generated (seed 113): t0 declares stat.b as int64?; t1, exclusive with it, writes it with a count of its own
        // (int64) and reads it without a default. t1's own compile has an int64 column, the unified plan's schema t0's
        // declaration: the reader was refused for a missing value t1 never has, with no word of why.
        var host = HomeFeedTestHost.Load();
        var arms = new JsonObject
        {
            ["t0"] = JsonNode.Parse("""{ "columns": { "stat.b": { "type": "int64?" } } }"""),
            ["t1"] = JsonNode.Parse("""
                { "nodes": {
                  "count_b": { "order": 505, "op": "GroupAggregate@1", "config": { "outputs": { "stat.b": { "fn": "count" } } } },
                  "boost": { "order": 508, "op": "ExpressionScorer@1",
                    "config": { "output_col": "score.final", "expr": "(col.stat.b > 0) ? col.score.final * 1.5 : col.score.final" } } } }
                """),
        };
        var request = With(host.Request, [new ExperimentPlacement(Experiment("g", arms), "layer_g")], validateArmsIndependently: true);
        Assert.True(PlanCompiler.CompileMerged(request, ["g/t0"], host.Environment).Succeeded);
        Assert.True(PlanCompiler.CompileMerged(request, ["g/t1"], host.Environment).Succeeded);

        var unified = PlanCompiler.Compile(request, host.Environment);

        var mismatch = Assert.Single(unified.Diagnostics, d => d.IsError && d.Code == "COLUMN_TYPE_MISMATCH");
        Assert.Equal(("experiment:g", "/arms/t1/nodes/count_b"), (mismatch.Artifact, mismatch.Pointer));
        Assert.Contains("writes 'col.stat.b' as int64, which declares it here, but 'g/t0' declares it as int64?", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("declare it in the baseline", mismatch.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ABaselineWriteDeclaringAColumnAnArmDeclaresOtherwise_IsATypeMismatch()
    {
        // The bare baseline has no declaration: its column is the write's int64, the unified plan's the arm's int64?.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["count_b"] = new NodeSpec
        {
            Order = 600, Op = "GroupAggregate@1", Config = JsonNode.Parse("""{ "outputs": { "stat.b": { "fn": "count" } } }""")!.AsObject(),
        };
        var request = With(host.Request, [Treatment("a", """{ "columns": { "stat.b": { "type": "int64?" } } }""")], validateArmsIndependently: true);
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t"], host.Environment).Succeeded);

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("COLUMN_TYPE_MISMATCH", "/nodes/count_b"), (error.Code, error.Pointer));
    }

    [Fact]
    public void AnArmDeclaringAColumnItWritesWithAnotherType_IsAccepted()
    {
        // The declaration holds wherever the arm's write does: a nullable int64 holds the count.
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request, [Treatment("a", """
            { "columns": { "stat.b": { "type": "int64?" } }, "nodes": {
              "count_b": { "order": 505, "op": "GroupAggregate@1", "config": { "outputs": { "stat.b": { "fn": "count" } } } },
              "read": { "order": 525, "op": "ExpressionFilter@1", "config": { "predicate": "(col.stat.b ?? 0) > 0" } } } }
            """)], validateArmsIndependently: true);
        Assert.True(PlanCompiler.CompileMerged(request, ["a/t"], host.Environment).Succeeded);

        var unified = PlanCompiler.Compile(request, host.Environment);

        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
    }

    public sealed class SelfDeclaringConfig
    {
        public bool Left { get; init; }

        public bool Right { get; init; }
    }

    /// <summary>Writes <c>needs_decl</c> with a type of its own, or, with both options on, as a declared column.</summary>
    private sealed class SelfDeclaringFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("SelfDeclaring", 1, Effects.ColumnWriter, typeof(SelfDeclaringConfig), "Declares its output unless both options are on.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<SelfDeclaringConfig>();
            if (config is null || !context.TryWriteColumn("needs_decl", "/output", config.Left && config.Right ? null : ColumnType.Float64.AsNullable(), out var output))
            {
                return null;
            }

            var expression = context.CompileExpression("1.0", "/value", CompileOptions.Score);
            return expression is null ? null : new Writer(expression, output);
        }

        private sealed class Writer(CompiledExpression expression, OutputColumn output) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                expression.Evaluate(context.EvalContext, context.LiveRows, context.Output(output));
                return ValueTask.CompletedTask;
            }
        }
    }
}
