using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Columns;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Expressions;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using static StrataRec.IntegrationTests.CombinationLimitTests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Pinned versions (<c>col.x@node</c>, design 4.6) are judged per arm combination, against the nodes that run together,
/// so the unified plan accepts exactly the combinations a compile of each alone accepts (review 20 #4). They used to be
/// judged once over the union of every node's variants: an arm that can never meet the reader could reject the plan, and
/// a combination that removes the version could pass.
/// </summary>
public sealed class VersionReferenceTests
{
    [Fact]
    public async Task APinOnlyInAnArmThatExcludesTheRowSourceVariant_IsAccepted_AndEachArmServesItsOwnPlan()
    {
        // Codex's case: in one layer, 'rows' makes producer a row source and 'pin' adds a reader of its version. They never
        // run together, and each configuration alone is legal; the union of producer's variants rejected the plan.
        var (request, environment) = Exclusive(sameLayer: true);
        foreach (var arms in new[] { Array.Empty<string>(), new[] { "choice/rows" }, new[] { "choice/pin" } })
        {
            var plain = PlanCompiler.CompileMerged(request, arms, environment);
            Assert.True(plain.Succeeded, string.Join(" + ", arms) + ": " + string.Join("\n", plain.Diagnostics));
        }

        var unified = PlanCompiler.Compile(request, environment);

        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
        var plan = unified.Plan!;
        Assert.Equal(new[] { (1L, 1.0, -1.0) }, await Serve(plan));
        Assert.Equal(new[] { (1L, -1.0, -1.0), (2L, 1.0, -1.0) }, await Serve(plan, "choice/rows"));
        Assert.Equal(new[] { (1L, 1.0, 1.0) }, await Serve(plan, "choice/pin")); // the reader sees producer's version
    }

    [Fact]
    public void TheSameArmsInTwoLayers_CanRunTogether_AndThePinOfTheRowSourceIsRejected()
    {
        // The control: once the arms can meet, the combination that has both is what a plain compile rejects.
        var (request, environment) = Exclusive(sameLayer: false);

        var both = PlanCompiler.CompileMerged(request, new[] { "pin/t", "rows/t" }, environment);
        var unified = PlanCompiler.Compile(request, environment);

        Assert.Contains(both.Diagnostics, d => d.IsError && d.Code == "VERSION_REF_ROW_SOURCE");
        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal("VERSION_REF_ROW_SOURCE", error.Code);
        Assert.EndsWith("/nodes/reader/config", error.Pointer, StringComparison.Ordinal);
    }

    [Fact]
    public void PastTheLimit_AVersionThatOnlyThreeArmsTogetherRemove_IsRejected_AsTheirCompileRejectsIt()
    {
        // Codex's triple: producer stops writing x only when a, b and c are all on, and a baseline node reads its version.
        // With 13 layers the unified compile checks scope by scope; it used to accept and serve this plan.
        var (request, environment) = Fading(stopAt: 3, readerArm: null, extraLayers: 10);
        foreach (var pair in new[] { new[] { "a/t", "b/t" }, new[] { "a/t", "c/t" }, new[] { "b/t", "c/t" } })
        {
            var merged = PlanCompiler.CompileMerged(request, pair, environment);
            Assert.True(merged.Succeeded, string.Join(" + ", pair) + ": " + string.Join("\n", merged.Diagnostics));
        }

        var triple = PlanCompiler.CompileMerged(request, new[] { "a/t", "b/t", "c/t" }, environment);
        Assert.Contains(triple.Diagnostics, d => d.IsError && d.Code == "VERSION_REF_UNKNOWN");

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Report.CombinationCheckExhaustive);
        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal("VERSION_REF_UNKNOWN", error.Code);
        Assert.Equal("/nodes/reader/config", error.Pointer);
        Assert.Contains("'producer' does not write 'x'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PastTheLimit_AReaderOneArmAdds_PinningAVersionTwoOtherArmsRemove_IsRejected()
    {
        // No rule about the column alone spans three layers (producer depends on two); only the pin joins the reader's
        // layer to them, so the pin's own scope is what finds the combination of all three.
        var (request, environment) = Fading(stopAt: 2, readerArm: "d", extraLayers: 10);
        foreach (var pair in new[] { new[] { "a/t", "b/t" }, new[] { "a/t", "d/t" }, new[] { "b/t", "d/t" } })
        {
            var merged = PlanCompiler.CompileMerged(request, pair, environment);
            Assert.True(merged.Succeeded, string.Join(" + ", pair) + ": " + string.Join("\n", merged.Diagnostics));
        }

        var triple = PlanCompiler.CompileMerged(request, new[] { "a/t", "b/t", "d/t" }, environment);
        Assert.Contains(triple.Diagnostics, d => d.IsError && d.Code == "VERSION_REF_UNKNOWN");

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Report.CombinationCheckExhaustive);
        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal("VERSION_REF_UNKNOWN", error.Code);
        Assert.EndsWith("/nodes/reader/config", error.Pointer, StringComparison.Ordinal);
    }

    [Fact]
    public void APinWhoseLayersHaveMoreCombinationsThanTheLimit_IsAnErrorAtTheReader()
    {
        var (request, environment) = Fading(stopAt: 2, readerArm: "d", extraLayers: 0, maxCombinationsChecked: 4);

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal("COMBINATION_CHECK_LIMIT", error.Code);
        Assert.Equal("/nodes/reader/config", error.Pointer);
        Assert.Contains("8 combinations", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinOfANodeThatIsNotThere_IsAnErrorAtTheReader()
    {
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.Columns["z"] = new ColumnSpec { Type = "float64?" };
        pipeline.Nodes["reader"] = new NodeSpec
        {
            Order = 900, Op = "ExpressionScorer@1", Config = new JsonObject { ["output_col"] = "z", ["expr"] = "col.score.final@nowhere ?? 0.0" },
        };

        var compilation = host.Compile();

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("VERSION_REF_UNKNOWN", "/nodes/reader/config"), (error.Code, error.Pointer));
        Assert.Contains("there is no node 'nowhere'", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
    }

    /// <summary>
    /// A pipeline of its own: seed appends row 1, producer writes x = 1. Arm <c>rows</c> makes producer a row source that
    /// appends row 2 with x = 1; arm <c>pin</c> adds reader, writing z from producer's version of x. The output shows both.
    /// </summary>
    private static (PlanCompileRequest Request, PlanEnvironment Environment) Exclusive(bool sameLayer)
    {
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.Nodes.Clear();
        pipeline.Columns.Clear();
        pipeline.LogTaps.Clear();
        pipeline.Output.Columns.Clear();
        pipeline.Columns["x"] = new ColumnSpec { Type = "float64?" };
        pipeline.Columns["z"] = new ColumnSpec { Type = "float64?" };
        pipeline.Nodes["seed"] = new NodeSpec { Order = 100, Op = "Append@1", Config = new JsonObject() };
        pipeline.Nodes["producer"] = new NodeSpec { Order = 200, Op = "ExpressionScorer@1", Config = new JsonObject { ["output_col"] = "x", ["expr"] = "1.0" } };
        pipeline.Output.Columns["x"] = "col.x ?? -1.0";
        pipeline.Output.Columns["z"] = "col.z ?? -1.0";
        const string Rows = """{ "nodes": { "producer": { "op": "Append@1" } } }""";
        const string Pin = """{ "nodes": { "reader": { "order": 300, "op": "ExpressionScorer@1", "config": { "output_col": "z", "expr": "col.x@producer ?? 0.0" } } } }""";
        var placements = sameLayer
            ? new List<ExperimentPlacement>
            {
                new(Experiment("choice", new JsonObject { ["rows"] = JsonNode.Parse(Rows), ["pin"] = JsonNode.Parse(Pin) }), "layer"),
            }
            : new List<ExperimentPlacement> { Treatment("rows", Rows), Treatment("pin", Pin) };
        var environment = new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new AppendFactory()) };
        return (With(host.Request, placements), environment);
    }

    /// <summary>
    /// Producer writes x until <paramref name="stopAt"/> of the options a, b (and c) turn on, each in its own layer; reader
    /// reads its version, in the baseline or added by <paramref name="readerArm"/>; and independent filter layers.
    /// </summary>
    private static (PlanCompileRequest Request, PlanEnvironment Environment) Fading(int stopAt, string? readerArm, int extraLayers, int? maxCombinationsChecked = null)
    {
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.Columns["x"] = new ColumnSpec { Type = "float64?" };
        pipeline.Columns["z"] = new ColumnSpec { Type = "float64?" };
        pipeline.Nodes["producer"] = new NodeSpec { Order = 850, Op = "Fading@1", Config = new JsonObject { ["output"] = "x", ["stop_at"] = stopAt } };
        const string Reader = """{ "order": 900, "op": "ExpressionScorer@1", "config": { "output_col": "z", "expr": "col.x@producer ?? 0.0" } }""";
        var placements = new List<ExperimentPlacement>
        {
            Treatment("a", """{ "nodes": { "producer": { "config": { "left": true } } } }"""),
            Treatment("b", """{ "nodes": { "producer": { "config": { "right": true } } } }"""),
        };
        if (stopAt > 2)
        {
            placements.Add(Treatment("c", """{ "nodes": { "producer": { "config": { "third": true } } } }"""));
        }

        if (readerArm is null)
        {
            pipeline.Nodes["reader"] = ArtifactSerializer.Deserialize<NodeSpec>(Reader);
        }
        else
        {
            placements.Add(Treatment(readerArm, $$"""{ "nodes": { "reader": {{Reader}} } }"""));
        }

        placements.AddRange(Filters(extraLayers));
        var environment = new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new FadingFactory()) };
        return (With(host.Request, placements, maxCombinationsChecked), environment);
    }

    /// <summary>Runs the plan with <paramref name="arms"/>: each item's id, x and z, by id.</summary>
    private static async Task<(long, double, double)[]> Serve(ExecutionPlan plan, params string[] arms)
    {
        var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(arms)));
        Assert.True(result.Succeeded, result.Error);
        return result.Items
            .Select(i => (i.Id.Int64Value, i.Columns["x"].AsFloat64(), i.Columns["z"].AsFloat64()))
            .OrderBy(i => i.Item1)
            .ToArray();
    }

    public sealed class AppendConfig
    {
        public string? OutputCol { get; init; }

        public string? Expr { get; init; }
    }

    /// <summary>
    /// <c>Append@1</c>, a row source: appends row 1; with <c>output_col</c> (it replaces an ExpressionScorer, whose
    /// <c>expr</c> it ignores), appends row 2 and writes 1 into that column for it.
    /// </summary>
    private sealed class AppendFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("Append", 1, Effects.RowSource, typeof(AppendConfig), "Appends one row.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<AppendConfig>();
            if (config is null)
            {
                return null;
            }

            if (config.OutputCol is null)
            {
                return new Append(null);
            }

            return context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output) ? new Append(output) : null;
        }

        private sealed class Append(OutputColumn? output) : Operator
        {
            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => 1;

            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                var row = context.AppendRows(new[] { output is null ? 1L : 2L });
                if (output is { } column)
                {
                    ColumnValues.Set(context.Output(column), row, ScalarValue.FromFloat64(1.0));
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    public sealed class FadingConfig
    {
        public required string Output { get; init; }

        public required int StopAt { get; init; }

        public bool Left { get; init; }

        public bool Right { get; init; }

        public bool Third { get; init; }
    }

    /// <summary>
    /// <c>Fading@1</c>: writes 1 into its output until <c>stop_at</c> of its options are on, then writes nothing. Arms in
    /// different layers that each turn one on remove the node's version only together.
    /// </summary>
    private sealed class FadingFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("Fading", 1, Effects.ColumnWriter, typeof(FadingConfig), "Writes until enough options are on.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<FadingConfig>();
            if (config is null)
            {
                return null;
            }

            var on = (config.Left ? 1 : 0) + (config.Right ? 1 : 0) + (config.Third ? 1 : 0);
            if (on >= config.StopAt)
            {
                return new Fading(null, default);
            }

            if (!context.TryWriteColumn(config.Output, "/output", null, out var output))
            {
                return null;
            }

            var expression = context.CompileExpression("1.0", "/output", CompileOptions.Score);
            return expression is null ? null : new Fading(expression, output);
        }

        private sealed class Fading(CompiledExpression? expression, OutputColumn output) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                expression?.Evaluate(context.EvalContext, context.LiveRows, context.Output(output));
                return ValueTask.CompletedTask;
            }
        }
    }
}
