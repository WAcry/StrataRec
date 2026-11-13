using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Expressions;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The compile-time bounds on arm combinations (design 11.5, 12.3): a space too large to compile or check is a located
/// error, never an exception and never a plan that serves unchecked combinations.
/// </summary>
public sealed class CombinationLimitTests
{
    [Fact]
    public void ANodeSpaceFarBeyondTheLimit_IsReportedAtTheNode_NotAnArithmeticOverflow()
    {
        // Review 18 #5: ten layers of 100 arms each modify fusion, 101^10 combinations at the default limit of 1024.
        var host = HomeFeedTestHost.Load();
        var placements = new List<ExperimentPlacement>();
        for (var layer = 0; layer < 10; layer++)
        {
            var arms = new JsonObject();
            for (var arm = 0; arm < 100; arm++)
            {
                arms["a" + arm] = JsonNode.Parse($$"""{ "nodes": { "fusion": { "config": { "multipliers": { "x{{layer}}": "1.{{arm}}" } } } } }""");
            }

            placements.Add(new ExperimentPlacement(Experiment("e" + layer, arms), "layer" + layer));
        }

        var request = With(host.Request, placements);

        var compilation = PlanCompiler.Compile(request, host.Environment);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.Code == "NODE_TOO_MANY_COMBINATIONS");
        Assert.Equal("/nodes/fusion", error.Pointer);
        Assert.Contains("1024", error.Message);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void PastTheLimit_ANullableColumnsSecondProducer_ThatOnlyThreeArmsTogetherCreate_IsFound()
    {
        // Review 18 #1: joint writes the nullable, non-flow column x only when arms of layers a and b both switch it on;
        // c adds another writer of x. Every arm and every pair is legal, the three together give x two producers. Ten
        // more layers, each adding a harmless filter, make 2^13 combinations: past the default limit of 4096.
        var (request, environment) = JointWriterRequest(extraLayers: 10);
        Assert.Equal(4096, request.MaxCombinationsChecked);

        // The control: each combination compiled on its own.
        foreach (var pair in new[] { new[] { "a/t", "b/t" }, new[] { "a/t", "c/t" }, new[] { "b/t", "c/t" } })
        {
            var merged = PlanCompiler.CompileMerged(request, pair, environment);
            Assert.True(merged.Succeeded, string.Join(" + ", pair) + ": " + string.Join("\n", merged.Diagnostics));
        }

        var triple = PlanCompiler.CompileMerged(request, new[] { "a/t", "b/t", "c/t" }, environment);
        Assert.Contains(triple.Diagnostics, d => d.IsError && d.Code == "COLUMN_MULTIPLE_PRODUCERS");

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Report.CombinationCheckExhaustive);
        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal("COLUMN_MULTIPLE_PRODUCERS", error.Code);
        Assert.Contains("'x'", error.Message);
    }

    [Fact]
    public void PastTheLimit_ARuleWhoseNodesThreeLayersDecide_CannotBeCheckedInTheLimit_IsAnErrorAtItsSubject()
    {
        // The same space with a limit of 4: the three layers that decide x's producers have 8 combinations.
        var (request, environment) = JointWriterRequest(extraLayers: 0, maxCombinationsChecked: 4);

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.Code == "COMBINATION_CHECK_LIMIT" && d.Pointer == "/columns/x");
        Assert.True(error.IsError);
        Assert.Contains("8 combinations", error.Message);
        Assert.Contains("layer", error.Hint);
    }

    [Fact]
    public void PastTheLimit_AnOrderDependencyThatOnlyThreeArmsTogetherCreate_IsFound()
    {
        // Not a column rule: q writes y at order 870; a moves p there, and p reads y only when b and c both switch it on.
        // Only the three together put two dependent nodes at one order. y and z are nullable: no producer is needed.
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.Columns["y"] = new ColumnSpec { Type = "float64?" };
        pipeline.Columns["z"] = new ColumnSpec { Type = "float64?" };
        pipeline.Nodes["q"] = new NodeSpec { Order = 870, Op = "ExpressionScorer@1", Config = new JsonObject { ["output_col"] = "y", ["expr"] = "2.0" } };
        pipeline.Nodes["p"] = new NodeSpec
        {
            Order = 860, Op = "Joint@1", Config = new JsonObject { ["left"] = false, ["right"] = false, ["output"] = "z", ["input"] = "y" },
        };
        var placements = new List<ExperimentPlacement>
        {
            Treatment("a", """{ "nodes": { "p": { "order": 870 } } }"""),
            Treatment("b", """{ "nodes": { "p": { "config": { "left": true } } } }"""),
            Treatment("c", """{ "nodes": { "p": { "config": { "right": true } } } }"""),
        };
        placements.AddRange(Filters(10));
        var request = With(host.Request, placements);
        var environment = JointEnvironment(host);

        foreach (var pair in new[] { new[] { "a/t", "b/t" }, new[] { "a/t", "c/t" }, new[] { "b/t", "c/t" } })
        {
            var merged = PlanCompiler.CompileMerged(request, pair, environment);
            Assert.True(merged.Succeeded, string.Join(" + ", pair) + ": " + string.Join("\n", merged.Diagnostics));
        }

        var triple = PlanCompiler.CompileMerged(request, new[] { "a/t", "b/t", "c/t" }, environment);
        Assert.Contains(triple.Diagnostics, d => d.IsError && d.Code == "ORDER_DEPENDENCY");

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Report.CombinationCheckExhaustive);
        Assert.False(unified.Succeeded);
        Assert.Equal("ORDER_DEPENDENCY", Assert.Single(unified.Diagnostics, d => d.IsError).Code);
    }

    /// <summary>The joint-writer space of review 18 #1, with <paramref name="extraLayers"/> independent filter layers.</summary>
    private static (PlanCompileRequest Request, PlanEnvironment Environment) JointWriterRequest(int extraLayers, int? maxCombinationsChecked = null)
    {
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.Columns["x"] = new ColumnSpec { Type = "float64?" };
        pipeline.Nodes["joint"] = new NodeSpec { Order = 850, Op = "Joint@1", Config = new JsonObject { ["left"] = false, ["right"] = false, ["output"] = "x" } };
        var placements = new List<ExperimentPlacement>
        {
            Treatment("a", """{ "nodes": { "joint": { "config": { "left": true } } } }"""),
            Treatment("b", """{ "nodes": { "joint": { "config": { "right": true } } } }"""),
            Treatment("c", """{ "nodes": { "other": { "order": 900, "op": "ExpressionScorer@1", "config": { "output_col": "x", "expr": "2.0" } } } }"""),
        };
        placements.AddRange(Filters(extraLayers));
        return (With(host.Request, placements, maxCombinationsChecked), JointEnvironment(host));
    }

    private static PlanEnvironment JointEnvironment(HomeFeedTestHost host) =>
        new() { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new JointFactory()) };

    /// <summary>Layers whose one arm adds a filter that keeps every row, each at its own order.</summary>
    private static IEnumerable<ExperimentPlacement> Filters(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return Treatment("f" + i, $$"""{ "nodes": { "filter{{i}}": { "order": {{1000 + (i * 10)}}, "op": "ExpressionFilter@1", "config": { "predicate": "true" } } } }""");
        }
    }

    /// <summary>An experiment in its own layer (named after it) with one arm, <c>t</c>.</summary>
    private static ExperimentPlacement Treatment(string id, string patch) =>
        new(Experiment(id, new JsonObject { ["t"] = JsonNode.Parse(patch) }), "layer_" + id);

    public sealed class JointConfig
    {
        public bool Left { get; init; }

        public bool Right { get; init; }

        public required string Output { get; init; }

        public string? Input { get; init; }
    }

    /// <summary>
    /// A node whose data flow two options decide together, so it depends on two layers at once. Without an input it
    /// writes its output only when both are on; with one it always writes its output, reading the input only then.
    /// </summary>
    private sealed class JointFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("Joint", 1, Effects.ColumnWriter, typeof(JointConfig), "Writes or reads when both options are on.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<JointConfig>();
            if (config is null)
            {
                return null;
            }

            var both = config.Left && config.Right;
            if (config.Input is null && !both)
            {
                return new Joint(null, default);
            }

            if (!context.TryWriteColumn(config.Output, "/output", null, out var output))
            {
                return null;
            }

            var source = both && config.Input is { } input ? $"col.{input} ?? 0.0" : "1.0";
            var expression = context.CompileExpression(source, "/input", CompileOptions.Score);
            return expression is null ? null : new Joint(expression, output);
        }

        private sealed class Joint(CompiledExpression? expression, OutputColumn output) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                expression?.Evaluate(context.EvalContext, context.LiveRows, context.Output(output));
                return ValueTask.CompletedTask;
            }
        }
    }

    internal static ExperimentArtifact Experiment(string id, JsonObject arms) => ArtifactSerializer.Deserialize<ExperimentArtifact>(new JsonObject
    {
        ["experiment_id"] = id,
        ["pipeline_id"] = "home_feed",
        ["owner"] = "t",
        ["description"] = "d",
        ["expires_at"] = "2099-01-01",
        ["arms"] = arms,
    }.ToJsonString());

    /// <summary>The sample's request with <paramref name="placements"/> instead of its experiments.</summary>
    private static PlanCompileRequest With(PlanCompileRequest r, IReadOnlyList<ExperimentPlacement> placements, int? maxCombinationsChecked = null) => new()
    {
        Pipeline = r.Pipeline,
        Catalog = r.Catalog,
        Bindings = r.Bindings,
        Tables = r.Tables,
        Policy = r.Policy,
        Experiments = placements,
        ManifestId = r.ManifestId,
        ValidateArmsIndependently = false, // a thousand arms: the bound under test is the node's, not each arm's compile
        MaxCombinationsChecked = maxCombinationsChecked ?? r.MaxCombinationsChecked,
    };
}
