using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Expressions;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using static StrataRec.IntegrationTests.CombinationLimitTests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A tap's attach point and the context features it logs are judged per arm combination, against that combination's
/// nodes, as a compile of the combination alone judges them (review 20, the union checks left after #4). Both used to be
/// judged against every variant at once: a baseline tap could depend on a node or a feature reader that only an arm adds,
/// and the baseline then broke when that experiment ended (a tap after a missing node never fires; a feature nobody reads
/// is fetched for the tap alone).
/// </summary>
public sealed class TapCombinationTests
{
    private const string Reader = """{ "order": 950, "op": "ExpressionScorer@1", "config": { "output_col": "score.t", "expr": "ctx.user.tenure_days * 1.0" } }""";

    private const string Extra = """{ "order": 1234, "op": "ExpressionFilter@1", "config": { "predicate": "true" } }""";

    [Fact]
    public void ABaselineTapLoggingAContextFeatureOnlyAnArmReads_IsRejected_AsTheBaselineAloneIs()
    {
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.LogTaps["served"].Context!["tenure"] = "ctx.user.tenure_days";
        host.Request.Pipeline.Columns["score.t"] = new ColumnSpec { Type = "float64?" };
        var request = With(host.Request, new[] { Treatment("e", $$"""{ "nodes": { "uses": {{Reader}} } }""") });

        Assert.Contains(PlanCompiler.CompileMerged(request, Array.Empty<string>(), host.Environment).Diagnostics,
            d => d.IsError && d.Code == "TAP_FEATURE_NOT_FETCHED");
        var withArm = PlanCompiler.CompileMerged(request, new[] { "e/t" }, host.Environment);
        Assert.True(withArm.Succeeded, string.Join("\n", withArm.Diagnostics));

        var unified = PlanCompiler.Compile(request, host.Environment);

        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("TAP_FEATURE_NOT_FETCHED", "/log_taps/served"), (error.Code, error.Pointer));
        Assert.Contains("user.tenure_days", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public async Task AnArmThatAddsTheReaderAndTheTapsField_IsAccepted_AndLogsItOnlyWhereItRuns()
    {
        // The control: the field and its reader come and go together.
        var host = HomeFeedTestHost.Load();
        host.Request.Pipeline.Columns["score.t"] = new ColumnSpec { Type = "float64?" };
        var request = With(host.Request, new[]
        {
            Treatment("e", $$"""{ "nodes": { "uses": {{Reader}} }, "log_taps": { "served": { "context": { "tenure": "ctx.user.tenure_days" } } } }"""),
        });

        var plan = Plan(host, request);

        Assert.Equal(420.0, Single(await Serve(plan, "e/t"), "served").Context.Single(c => c.Name == "tenure").Value.AsFloat64());
        Assert.DoesNotContain(Single(await Serve(plan), "served").Context, c => c.Name == "tenure");
    }

    [Fact]
    public void PastTheLimit_AContextFeatureThatOnlyThreeArmsTogetherStopReading_IsRejected()
    {
        // ctx_reader reads the feature unless a, b and c are all on; it writes nothing, orders alone and has no fallback,
        // so only the tap's scope for the feature joins its three layers.
        var (request, environment) = ContextReader(extraLayers: 10);
        foreach (var pair in new[] { new[] { "a/t", "b/t" }, new[] { "a/t", "c/t" }, new[] { "b/t", "c/t" } })
        {
            var merged = PlanCompiler.CompileMerged(request, pair, environment);
            Assert.True(merged.Succeeded, string.Join(" + ", pair) + ": " + string.Join("\n", merged.Diagnostics));
        }

        Assert.Contains(PlanCompiler.CompileMerged(request, new[] { "a/t", "b/t", "c/t" }, environment).Diagnostics,
            d => d.IsError && d.Code == "TAP_FEATURE_NOT_FETCHED");

        var unified = PlanCompiler.Compile(request, environment);

        Assert.False(unified.Report.CombinationCheckExhaustive);
        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("TAP_FEATURE_NOT_FETCHED", "/log_taps/served"), (error.Code, error.Pointer));
    }

    [Fact]
    public void AContextFeatureScopeOverTheLimit_IsAnErrorAtTheTap()
    {
        var (request, environment) = ContextReader(extraLayers: 0, maxCombinationsChecked: 4);

        var unified = PlanCompiler.Compile(request, environment);

        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("COMBINATION_CHECK_LIMIT", "/log_taps/served"), (error.Code, error.Pointer));
        Assert.Contains("context feature 'user.tenure_days'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABaselineTapAttachedAfterANodeOnlyAnArmAdds_IsRejected_AsTheBaselineAloneIs()
    {
        var host = HomeFeedTestHost.Load();
        var tap = host.Request.Pipeline.LogTaps["coarse_dropped"];
        host.Request.Pipeline.LogTaps["coarse_dropped"] = new LogTapSpec
        {
            Group = tap.Group, Attach = new LogTapAttachSpec { After = "extra" }, Rows = tap.Rows, Columns = tap.Columns,
        };
        var request = With(host.Request, new[] { Treatment("e", $$"""{ "nodes": { "extra": {{Extra}} } }""") });

        Assert.Contains(PlanCompiler.CompileMerged(request, Array.Empty<string>(), host.Environment).Diagnostics,
            d => d.IsError && d.Code == "TAP_UNKNOWN_NODE");
        var withArm = PlanCompiler.CompileMerged(request, new[] { "e/t" }, host.Environment);
        Assert.True(withArm.Succeeded, string.Join("\n", withArm.Diagnostics));

        var unified = PlanCompiler.Compile(request, host.Environment);

        Assert.False(unified.Succeeded);
        var error = Assert.Single(unified.Diagnostics, d => d.IsError);
        Assert.Equal(("TAP_UNKNOWN_NODE", "/log_taps/coarse_dropped/attach/after"), (error.Code, error.Pointer));
        Assert.Contains("'extra'", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public async Task AnArmThatAddsANodeAndATapAfterIt_IsAccepted_AndTheTapFiresWhereTheNodeRuns()
    {
        var host = HomeFeedTestHost.Load();
        var request = With(host.Request, new[]
        {
            Treatment("e", $$"""
                { "nodes": { "extra": {{Extra}} },
                  "log_taps": { "after_extra": { "group": "all", "attach": { "after": "extra" }, "columns": { "coarse": "col.score.coarse" } } } }
                """),
        });

        var plan = Plan(host, request);

        Assert.NotEmpty(Single(await Serve(plan, "e/t"), "after_extra").Columns);
        Assert.DoesNotContain(await Serve(plan), b => b.TapId == "after_extra");
    }

    [Fact]
    public void ATapAttachedAfterAFallback_IsRejected_ItWouldNeverFire()
    {
        // A fallback runs in its node's place, and the taps after that node capture its output; none fire after it.
        var host = HomeFeedTestHost.Load();
        var tap = host.Request.Pipeline.LogTaps["ranker_inputs"];
        host.Request.Pipeline.LogTaps["ranker_inputs"] = new LogTapSpec
        {
            Group = tap.Group, Delivery = tap.Delivery, Attach = new LogTapAttachSpec { After = "ctr_backup" }, Columns = tap.Columns,
        };

        var compilation = PlanCompiler.Compile(host.Request, host.Environment);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("TAP_UNKNOWN_NODE", "/log_taps/ranker_inputs/attach/after"), (error.Code, error.Pointer));
        Assert.Contains("fallback 'ctr_backup'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The served tap logs <c>user.tenure_days</c>, which only ctx_reader reads, until a, b and c (each in its own layer)
    /// are all on; with independent filter layers.
    /// </summary>
    private static (PlanCompileRequest Request, PlanEnvironment Environment) ContextReader(int extraLayers, int? maxCombinationsChecked = null)
    {
        var host = HomeFeedTestHost.Load();
        var pipeline = host.Request.Pipeline;
        pipeline.LogTaps["served"].Context!["tenure"] = "ctx.user.tenure_days";
        pipeline.Nodes["ctx_reader"] = new NodeSpec { Order = 960, Op = "ContextReader@1", Config = new JsonObject { ["feature"] = "user.tenure_days" } };
        var placements = new List<ExperimentPlacement>
        {
            Treatment("a", """{ "nodes": { "ctx_reader": { "config": { "left": true } } } }"""),
            Treatment("b", """{ "nodes": { "ctx_reader": { "config": { "right": true } } } }"""),
            Treatment("c", """{ "nodes": { "ctx_reader": { "config": { "third": true } } } }"""),
        };
        placements.AddRange(Filters(extraLayers));
        var environment = new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new ContextReaderFactory()) };
        return (With(host.Request, placements, maxCombinationsChecked), environment);
    }

    /// <summary>Compiles <paramref name="request"/> through the host's bindings and waits for them, as the engine does before serving.</summary>
    private static ExecutionPlan Plan(HomeFeedTestHost host, PlanCompileRequest request)
    {
        var lease = host.Bindings.OpenLease();
        var compilation = lease.CompileAsync(bindings => PlanCompiler.Compile(request, host.Environment, bindings)).GetAwaiter().GetResult();
        Assert.Empty(lease.WaitLoadedAsync().GetAwaiter().GetResult());
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static async Task<IReadOnlyList<LogBatch>> Serve(ExecutionPlan plan, params string[] arms)
    {
        var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(arms), captureLogs: true));
        Assert.True(result.Succeeded, result.Error);
        return result.LogBatches;
    }

    private static LogBatch Single(IReadOnlyList<LogBatch> batches, string tapId) => Assert.Single(batches, b => b.TapId == tapId);

    public sealed class ContextReaderConfig
    {
        public required string Feature { get; init; }

        public bool Left { get; init; }

        public bool Right { get; init; }

        public bool Third { get; init; }
    }

    /// <summary><c>ContextReader@1</c>: reads a context feature unless all three options are on; writes nothing.</summary>
    private sealed class ContextReaderFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("ContextReader", 1, Effects.ColumnWriter, typeof(ContextReaderConfig), "Reads a context feature.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<ContextReaderConfig>();
            if (config is null)
            {
                return null;
            }

            if (!(config.Left && config.Right && config.Third) && context.CompileExpression($"ctx.{config.Feature} * 1.0", "/feature", CompileOptions.Score) is null)
            {
                return null;
            }

            return new Nothing();
        }

        private sealed class Nothing : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context) => ValueTask.CompletedTask;
        }
    }
}
