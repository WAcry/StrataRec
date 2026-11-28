using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Experiments.Compilation;
using StrataRec.Expressions;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using static StrataRec.IntegrationTests.CombinationLimitTests;

namespace StrataRec.IntegrationTests;

public sealed class DeclarationTests
{
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
        // GroupAggregate@1 reads its value columns before it writes its outputs, so a value column naming its own
        // output is unknown when the arm's config is compiled alone. Each arm alone is not compiled here (With).
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
    public void ANodeReadingAColumnOnlyItWrites_IsRefused_AndInTwoNodesItCompiles()
    {
        var host = HomeFeedTestHost.Load();
        var one = With(host.Request, [Treatment("a", """
            { "nodes": { "selfagg": { "order": 600, "op": "GroupAggregate@1", "config": { "group_col": "item.author_id",
              "outputs": { "a_self": { "fn": "count" }, "b_self": { "fn": "sum", "value_col": "a_self" } } } } } }
            """)]);

        var merged = Assert.Single(PlanCompiler.CompileMerged(one, ["a/t"], host.Environment).Diagnostics, d => d.IsError);
        Assert.Equal(("COLUMN_OWN_OUTPUT", "/nodes/selfagg/config/outputs/b_self/value_col"), (merged.Code, merged.Pointer));
        Assert.Contains("a node after this one", merged.Hint, StringComparison.Ordinal);
        var unified = Assert.Single(PlanCompiler.Compile(one, host.Environment).Diagnostics, d => d.IsError);
        Assert.Equal(("COLUMN_OWN_OUTPUT", "experiment:a", "/arms/t/nodes/selfagg/config/outputs/b_self/value_col"),
            (unified.Code, unified.Artifact, unified.Pointer));

        var two = With(host.Request, [Treatment("a", """
            { "nodes": {
                "count": { "order": 600, "op": "GroupAggregate@1", "config": { "group_col": "item.author_id", "outputs": { "a_self": { "fn": "count" } } } },
                "sum": { "order": 601, "op": "GroupAggregate@1", "config": { "group_col": "item.author_id",
                  "outputs": { "b_self": { "fn": "sum", "value_col": "a_self" } } } } } }
            """)]);
        Assert.True(PlanCompiler.CompileMerged(two, ["a/t"], host.Environment).Succeeded);
        var both = PlanCompiler.Compile(two, host.Environment);
        Assert.True(both.Succeeded, string.Join("\n", both.Diagnostics));
    }

    [Fact]
    public void ANodeReadingAColumnOnlyItWrites_WhereAnotherCombinationDeclaresIt_IsRefusedInItsOwn()
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Nodes["count"] = new NodeSpec
        {
            Order = 600, Op = "GroupAggregate@1",
            Config = JsonNode.Parse("""{ "group_col": "item.author_id", "outputs": { "x": { "fn": "count" } } }""")!.AsObject(),
        };
        var request = With(host.Request, [Treatment("a", """
            { "nodes": { "count": null, "selfagg": { "order": 601, "op": "GroupAggregate@1", "config": { "group_col": "item.author_id",
              "outputs": { "x": { "fn": "count" }, "y": { "fn": "sum", "value_col": "x" } } } } } }
            """)]);
        Assert.Contains(PlanCompiler.CompileMerged(request, ["a/t"], host.Environment).Diagnostics, d => d.IsError && d.Code == "COLUMN_OWN_OUTPUT");

        var unified = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("NAME_NOT_DECLARED", "experiment:a", "/arms/t/nodes/selfagg"), (error.Code, error.Artifact, error.Pointer));
    }

    [Fact]
    public void ADeclaredColumn_ExistsInEveryCombination()
    {
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

    /// <summary>The second is the optimizer drops the branch, the name must still be declared to bind.</summary>
    [Theory]
    [InlineData("col.group.author_rank")]
    [InlineData("true ? 7 : (col.group.author_rank ?? 0)")]
    public void TheOutputReadingAColumnAnArmTakesAway_IsRejected_EvenWithoutCheckingArmsAlone(string expression)
    {
        // The output compiles after every node, so any writer declares what it reads; an arm without one leaves it
        // unknown.
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
    public void ADiagnosticAtATapOrAnOutputColumn_EscapesItsNameInThePointer()
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Output.Columns["rank/of~author"] = "col.group.author_rank";
        var request = With(host.Request,
        [
            Treatment("a", """{ "nodes": { "author_rank": null, "author_cap": null } }"""),
            Treatment("b", """
                { "log_taps": { "rank/of~tap": { "group": "all", "attach": { "after": "diversity_cap" }, "columns": { "v": "col.group.author_rank" } } } }
                """),
        ]);

        var unified = PlanCompiler.Compile(request, host.Environment);

        // The tap's column has no producer before it either, which the data-flow check says: the name rule does not repeat it.
        var errors = unified.Diagnostics.Where(d => d.IsError).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, d => (d.Code, d.Pointer) == ("NAME_NOT_DECLARED", "/output/columns/rank~1of~0author"));
        Assert.Contains(errors, d => (d.Code, d.Artifact, d.Pointer) == ("TAP_COLUMN_NOT_PRODUCED", "experiment:b", "/arms/t/log_taps/rank~1of~0tap"));
    }

    [Fact]
    public void AnErrorInATapsContextOrAMapKey_IsAtItsEscapedPointer()
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Params["recall_quotas"]!["a/b~c"] = "many";
        var request = With(host.Request, [Treatment("a", """
            { "log_taps": { "tap/of~x": { "group": "all", "attach": { "output": true }, "context": { "slash/name~x": "ctx.request.wrong_input" } } } }
            """)]);

        var unified = PlanCompiler.Compile(request, host.Environment);

        var errors = unified.Diagnostics.Where(d => d.IsError).ToList();
        Assert.Contains(errors, d => (d.Code, d.Artifact, d.Pointer) == ("EXPR_UNKNOWN_NAME", "experiment:a", "/arms/t/log_taps/tap~1of~0x/context/slash~1name~0x"));
        Assert.Contains(errors, d => d.Pointer == "/params/recall_quotas/a~1b~0c");
    }

    [Fact]
    public void AnErrorInAColumnOrAFusionTerm_IsAtItsEscapedPointer()
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Columns["col/of~x"] = new ColumnSpec { Type = "int46" };
        var request = With(host.Request, [Treatment("a", """
            { "nodes": { "fusion": { "config": { "multipliers": { "term/of~x": "col.wrong_column" }, "addends": { "add/of~x": "col.wrong_column" } } } } }
            """)]);

        var unified = PlanCompiler.Compile(request, host.Environment);

        var errors = unified.Diagnostics.Where(d => d.IsError).ToList();
        Assert.Contains(errors, d => (d.Code, d.Pointer) == ("COLUMN_BAD_TYPE", "/columns/col~1of~0x/type"));
        Assert.Contains(errors, d => (d.Artifact, d.Pointer) == ("experiment:a", "/arms/t/nodes/fusion/config/multipliers/term~1of~0x"));
        Assert.Contains(errors, d => (d.Artifact, d.Pointer) == ("experiment:a", "/arms/t/nodes/fusion/config/addends/add~1of~0x"));
    }

    [Fact]
    public void AColumnDeclaredOnlyByAnArmNotAssignedWithItsReader_IsNotDeclaredThere()
    {
        // A declares the column the baseline's writer declares too, b reads it, c deletes the writer; each arm is valid
        // alone and checked so. With b and c but not a, the name is unknown.
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

    /// <summary>the binder resolves every name; the optimizer then drops the branch, and the reads with it.</summary>
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
        // An extension writes its column with a type of its own unless both options are on; then it writes it as
        // declared (TryWriteColumn without an implicit type).
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
        // Joint reads y only when a and b both switch it on; c deletes y's writer. Every arm and every pair is legal;
        // four more layers make 128 combinations, past the limit of 8, which the three deciding layers fit.
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
