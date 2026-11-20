using StrataRec.Artifacts.Model;
using StrataRec.Core.Columns;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Runtime;
using StrataRec.Hosting.Workspace;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Review 83: a tap logs a candidate feature only where its column is written whatever runs. A node's fill writes the
/// features it reads and the key features they are fetched by; a fallback's fill writes its candidate features only when
/// it runs, and the prefetch for it never backfills the table. The tap checks counted a fallback's reads, and the key
/// <c>col.item.base</c> of the <c>col.creator.pop</c> only the fallback reads, as fetched: the request succeeded without log
/// consumers and threw once a sampled tap read the column nobody had written, captured or written to a sink. A fallback's
/// context features count: the context prefetch stores them for every request.
/// </summary>
public sealed class TapCandidateKeyTests
{
    private const string Catalog = """
        { "catalog_id": "c",
          "entities": { "item": { "key": "col.entity_id" }, "creator": { "key": "col.item.base" }, "user": { "key": "ctx.request.user_id" } },
          "features": {
            "item.base": { "type": "int64", "source": "item_store", "field": "base", "default": 0 },
            "creator.pop": { "type": "int64", "source": "item_store", "field": "base", "default": 0 },
            "user.w": { "type": "int64", "source": "user_store", "field": "w", "default": 0 } } }
        """;

    /// <summary>
    /// The same compiled plan serves the request whether the fallback runs or not, and whether the tap is sampled: the
    /// check is the only place to stop it. The hint says where the feature can be logged.
    /// </summary>
    [Theory]
    [InlineData("col.creator.pop", """{ "after": "next" }""", null)]
    [InlineData("col.item.base", """{ "after": "next" }""", null)]
    [InlineData("col.item.base", """{ "output": true }""", null)]
    [InlineData("col.creator.pop", """{ "after": "next" }""", "later")]
    [InlineData("col.item.base", """{ "after": "next" }""", "later")]
    public void ATapLoggingACandidateFeatureOnlyAFallbackReads_IsToldItIsNotFetched_AndWhereItIs(string logged, string attach, string? laterReader)
    {
        var compilation = Compile(Pipeline(attach, logged, primary: "1.0", laterReader: laterReader is not null));

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("TAP_FEATURE_NOT_FETCHED", "/log_taps/key"), (error.Code, error.Pointer));
        var name = logged["col.".Length..];
        Assert.Contains($"'{name}'", error.Message);
        Assert.Equal(
            laterReader is not null
                ? $"attach the tap after '{laterReader}', the first node after this point that reads '{name}'"
                : $"attach the tap after a node that reads '{name}': fallback 'backup' reads it, but a fallback's candidate features are written only when it runs",
            error.Hint);
    }

    /// <summary>
    /// A node that reads <c>col.creator.pop</c> runs before the tap: its fill wrote the feature and its key, which the tap
    /// logs with no log consumers, captured, and written to a sink (rejected by the first fix of this review, which followed
    /// only context keys).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ATapLoggingTheKeyOfAFeatureANodeBeforeItReads_LogsIt(bool capture, bool write)
    {
        var compilation = Compile(Pipeline("""{ "after": "next" }""", "col.item.base", primary: "double(col.creator.pop)"));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));

        var result = await Execute(compilation.Plan!, capture, write);

        Assert.True(result.Succeeded, result.Error);
        if (capture)
        {
            var column = Assert.Single(Assert.Single(result.LogBatches, b => b.TapId == "key").Columns);
            Assert.Equal(10L, ((long[])column.Values)[0]); // item 1's base, the key of its creator
        }
    }

    /// <summary>
    /// Review 85: a node its <c>when</c> or <c>min_remaining_ms</c> skips fills nothing, and its fallback does not run
    /// either. The checks counted what it reads as fetched at every later point: the plan compiled, and the request threw
    /// once the output's tap was sampled after a skip. The hint names the node and why a later point cannot count on it.
    /// </summary>
    [Theory]
    [InlineData("col.creator.pop", "\"when\": \"false\",")]
    [InlineData("col.item.base", "\"when\": \"false\",")]
    [InlineData("col.item.base", "\"min_remaining_ms\": 60000,")]
    public void ATapAfterANodeThatMayBeSkipped_IsToldTheCandidateFeaturesItReadsAreNotFetched(string logged, string skip)
    {
        var compilation = Compile(Pipeline("""{ "output": true }""", logged, primary: "double(col.creator.pop)", backup: "1.0", next: skip, output: "1.0"));

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("TAP_FEATURE_NOT_FETCHED", "/log_taps/key"), (error.Code, error.Pointer));
        var name = logged["col.".Length..];
        Assert.Equal(
            $"attach the tap after 'next', which reads '{name}': its 'when' or 'min_remaining_ms' may skip it, and then nothing writes the feature for a later point",
            error.Hint);
    }

    /// <summary>
    /// A tap attached to a node that may be skipped is captured only when the node ran: it logs what the node reads then,
    /// and nothing when the node is skipped.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATapAttachedToANodeThatMayBeSkipped_LogsWhatItReads_WhenItRuns(bool runs)
    {
        var compilation = Compile(Pipeline("""{ "after": "next" }""", "col.item.base", primary: "double(col.creator.pop)", backup: "1.0",
            next: $"\"when\": \"{(runs ? "true" : "false")}\",", output: "1.0"));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));

        var result = await Execute(compilation.Plan!, capture: true, write: false);

        Assert.True(result.Succeeded, result.Error);
        var batches = result.LogBatches.Where(b => b.TapId == "key").ToList();
        if (runs)
        {
            Assert.Equal(10L, ((long[])Assert.Single(Assert.Single(batches).Columns).Values)[0]);
        }
        else
        {
            Assert.Empty(batches);
        }
    }

    /// <summary>A node that may be skipped still counts for the context features it reads: the prefetch stores them for every request.</summary>
    [Fact]
    public async Task ATapAfterANodeThatMayBeSkipped_LogsTheContextFeaturesItReads()
    {
        var compilation = Compile(Pipeline("""{ "output": true }""", "col.entity_id", primary: "double(ctx.user.w)", backup: "1.0",
            context: "ctx.user.w", next: "\"when\": \"false\",", output: "1.0"));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));

        var result = await Execute(compilation.Plan!, capture: true, write: false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3L, Assert.Single(Assert.Single(result.LogBatches, b => b.TapId == "key").Context, c => c.Name == "w").Value.AsInt64());
    }

    /// <summary>
    /// Review 85: the output reads the features of its columns for every row it returns, so a tap there can log them; the
    /// hint named only nodes.
    /// </summary>
    [Fact]
    public async Task ATapLoggingACandidateFeatureOnlyTheOutputReads_IsToldToAttachThere_WhereItLogsIt()
    {
        var after = Compile(Pipeline("""{ "after": "next" }""", "col.item.base", primary: "1.0", backup: "1.0", output: "double(col.creator.pop)"));
        var error = Assert.Single(after.Diagnostics, d => d.IsError);
        Assert.Equal("TAP_FEATURE_NOT_FETCHED", error.Code);
        Assert.Equal("attach the tap to the output (\"attach\": { \"output\": true }), which reads 'item.base'", error.Hint);

        var compilation = Compile(Pipeline("""{ "output": true }""", "col.item.base", primary: "1.0", backup: "1.0", output: "double(col.creator.pop)"));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var result = await Execute(compilation.Plan!, capture: true, write: false);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(10L, ((long[])Assert.Single(Assert.Single(result.LogBatches, b => b.TapId == "key").Columns).Values)[0]);
    }

    /// <summary>
    /// Review 87: a fill writes the rows live when it runs, and a tap never fills. A node read <c>col.item.base</c> and
    /// failed; its fallback, a row source, appended candidate 2, and a tap after the node logged candidate 2's value from
    /// storage nobody wrote, as a valid value. After a position that may append rows, a candidate feature filled before is
    /// fetched again only by a later node that reads it, which fills every live row.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\"when\": \"true\",")]
    public async Task RowsAFallbackAppends_HaveNoValueForWhatTheNodeFilled_UntilALaterNodeFillsIt(string next)
    {
        var operators = OperatorRegistry.CreateDefault().Add(new FailsAfterReadingFactory()).Add(new AppendsOneFactory());
        var appended = Appending(Pipeline("""{ "after": "next" }""", "col.item.base", primary: "item.base", backup: "", next: next, output: "1.0"));

        var error = Assert.Single(Compile(appended, operators).Diagnostics, d => d.IsError);
        Assert.Equal(("TAP_FEATURE_NOT_FETCHED", "/log_taps/key"), (error.Code, error.Pointer));
        Assert.Equal("attach the tap after a later node that reads 'item.base': the rows 'next' or its fallback appends have no value for it", error.Hint);

        var refilled = Compile(Appending(Pipeline("""{ "after": "later" }""", "col.item.base", primary: "item.base", backup: "", next: next,
            laterReader: true, output: "1.0")), operators);
        Assert.True(refilled.Succeeded, string.Join("\n", refilled.Diagnostics));
        var result = await Execute(refilled.Plan!, capture: true, write: false);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("backup", result.Outcomes.Single(o => o.NodeId == "next").ProducedBy);
        var batch = Assert.Single(result.LogBatches, b => b.TapId == "key");
        var column = Assert.Single(batch.Columns);
        Assert.Equal(new long[] { 1, 2 }, batch.Int64EntityIds);
        Assert.Equal(new long[] { 10, 0 }, (long[])column.Values); // candidate 2 has no record: the default, as filled by 'later'
    }

    /// <summary>A row source after the node that read the feature: the hint names both.</summary>
    [Fact]
    public void RowsALaterRowSourceAppends_HaveNoValueForWhatAnEarlierNodeFilled()
    {
        var pipeline = Pipeline("""{ "output": true }""", "col.item.base", primary: "double(col.item.base)", backup: "1.0", output: "1.0")
            .Replace("\"backup\": {", "\"more\": { \"order\": 250, \"op\": \"AppendsOne@1\", \"config\": { \"output_col\": \"score.l\" } }, \"backup\": {", StringComparison.Ordinal)
            .Replace("\"score.l\": { \"type\": \"float64\", \"default\": 0.0 }", "\"score.l\": { \"type\": \"float64?\" }", StringComparison.Ordinal);

        var error = Assert.Single(Compile(pipeline, OperatorRegistry.CreateDefault().Add(new AppendsOneFactory())).Diagnostics, d => d.IsError);

        Assert.Equal("TAP_FEATURE_NOT_FETCHED", error.Code);
        Assert.Equal("attach the tap after 'next', which reads 'item.base': the rows 'more' or its fallback appends after it have no value for it", error.Hint);
    }

    /// <summary>Review 87: the output is suggested only to a tap of survived rows, the only scope a tap there may have.</summary>
    [Theory]
    [InlineData("entered")]
    [InlineData("dropped")]
    public void ATapOfEnteredOrDroppedRows_IsNotToldToAttachToTheOutput(string scope)
    {
        var pipeline = Pipeline("""{ "after": "next" }""", "col.item.base", primary: "1.0", backup: "1.0", output: "double(col.creator.pop)")
            .Replace("\"key\": { \"group\": \"all\",", $"\"key\": {{ \"group\": \"all\", \"rows\": {{ \"scope\": \"{scope}\" }},", StringComparison.Ordinal);

        var error = Assert.Single(Compile(pipeline).Diagnostics, d => d.IsError);

        Assert.Equal("TAP_FEATURE_NOT_FETCHED", error.Code);
        Assert.Equal("attach the tap after a node that reads 'item.base'", error.Hint);
    }

    /// <summary>
    /// <c>next</c> runs <c>FailsAfterReading@1</c>, whose fallback <c>backup</c> is <c>AppendsOne@1</c>, a row source;
    /// <c>score.t</c> is nullable, as the appended row is the only one the fallback writes.
    /// </summary>
    private static string Appending(string pipeline) => pipeline
        .Replace("\"k\": 1", "\"k\": 2", StringComparison.Ordinal)
        .Replace("\"score.t\": { \"type\": \"float64\", \"default\": 0.0 }", "\"score.t\": { \"type\": \"float64?\" }", StringComparison.Ordinal)
        .Replace("\"next\": { \"order\": 200, \"op\": \"ExpressionScorer@1\"", "\"next\": { \"order\": 200, \"op\": \"FailsAfterReading@1\"", StringComparison.Ordinal)
        .Replace("\"backup\": { \"role\": \"fallback\", \"op\": \"ExpressionScorer@1\", \"config\": { \"output_col\": \"score.t\", \"expr\": \"\" } }",
            "\"backup\": { \"role\": \"fallback\", \"op\": \"AppendsOne@1\", \"config\": { \"output_col\": \"score.t\" } }", StringComparison.Ordinal);

    public sealed class ReadingConfig
    {
        public required string OutputCol { get; init; }

        public required string Expr { get; init; }
    }

    public sealed class AppendingConfig
    {
        public required string OutputCol { get; init; }
    }

    /// <summary>Reads a column (its node fills the feature), then fails.</summary>
    private sealed class FailsAfterReadingFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("FailsAfterReading", 1, Effects.ColumnWriter, typeof(ReadingConfig), "Reads a column, then fails.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<ReadingConfig>()!;
            return context.TryReadColumn(config.Expr, "/expr", out _) && context.TryWriteColumn(config.OutputCol, "/output_col", null, out _)
                ? new Fails()
                : null;
        }

        private sealed class Fails : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                context.Fail("the primary does not serve");
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Appends candidate 2 and writes its column for that row only.</summary>
    private sealed class AppendsOneFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("AppendsOne", 1, Effects.RowSource, typeof(AppendingConfig), "Appends candidate 2.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<AppendingConfig>()!;
            return context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output) ? new Appends(output) : null;
        }

        private sealed class Appends(OutputColumn output) : Operator
        {
            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => 1;

            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                var row = context.AppendRows(new long[] { 2 });
                ((ScalarColumn<double>)context.Output(output)).Set(row, 2.0);
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A context feature only the fallback reads is prefetched for every request: the tap logs it.</summary>
    [Fact]
    public async Task ATapLoggingAContextFeatureOnlyAFallbackReads_LogsIt()
    {
        var compilation = Compile(Pipeline("""{ "after": "next" }""", "col.entity_id", primary: "1.0", backup: "double(ctx.user.w)", context: "ctx.user.w"));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));

        var result = await Execute(compilation.Plan!, capture: true, write: false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3L, Assert.Single(Assert.Single(result.LogBatches, b => b.TapId == "key").Context, c => c.Name == "w").Value.AsInt64());
    }

    private static Task<PipelineResult> Execute(ExecutionPlan plan, bool capture, bool write) =>
        ExecuteAsync(plan, capture, write).AsTask();

    private static async ValueTask<PipelineResult> ExecuteAsync(ExecutionPlan plan, bool capture, bool write)
    {
        await using var logs = new LogPipeline(new InMemoryLogSink());
        return await new PipelineExecutor(logs: logs).ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "tap-key", Inputs = new RequestInputs().Set("user_id", 5), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000,
            CaptureLogs = capture, WriteLogs = write,
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Candidate 1, then <c>next</c> (a constant score unless <paramref name="primary"/> reads a feature) whose fallback
    /// reads <paramref name="backup"/>, by default <c>col.creator.pop</c>, keyed by <c>col.item.base</c>; with
    /// <paramref name="laterReader"/>, a node <c>later</c> after it reads <c>col.creator.pop</c>. The tap <c>key</c> logs
    /// <paramref name="logged"/>, and <paramref name="context"/> as a context field. <paramref name="next"/> adds members to
    /// <c>next</c>; <paramref name="output"/> is the output's column.
    /// </summary>
    private static string Pipeline(
        string attach, string logged, string primary, string backup = "double(col.creator.pop)", bool laterReader = false, string? context = null,
        string next = "", string output = "col.score.t") => $$"""
        { "pipeline_id": "p",
          "request_schema": { "user_id": { "type": "int64" } },
          "columns": { "score.t": { "type": "float64", "default": 0.0 }, "score.l": { "type": "float64", "default": 0.0 } },
          "param_schema": { "q": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
          "params": { "q": { "first": 1 }, "k": 1 },
          "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
          "log_taps": { "key": { "group": "all", "attach": {{attach}}, "columns": { "v": "{{logged}}" }{{(context is null ? "" : $$""", "context": { "w": "{{context}}" }""")}} } },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q" } },
            "next": { "order": 200, "op": "ExpressionScorer@1", {{next}} "on_failure": "fallback", "fallback": "backup",
              "config": { "output_col": "score.t", "expr": "{{primary}}" } },
            {{(laterReader ? """ "later": { "order": 300, "op": "ExpressionScorer@1", "config": { "output_col": "score.l", "expr": "double(col.creator.pop)" } }, """ : "")}}
            "backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.t", "expr": "{{backup}}" } } },
          "output": { "limit_param": "k", "columns": { "score": "{{output}}" } } }
        """;

    private static PlanCompilation Compile(string pipeline, OperatorRegistry? operators = null)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-tap-key-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), Catalog);
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), """
            { "environment": "local",
              "sources": {
                "item_store": { "connector": "memory@1", "data": { "1": { "base": 10 }, "10": { "base": 7 } }, "timeout_ms": 60000 },
                "user_store": { "connector": "memory@1", "data": { "5": { "w": 3 } }, "timeout_ms": 60000 } },
              "candidate_sources": { "first": { "client": "memory_list@1", "items": [1] } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), pipeline);

        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return PlanCompiler.Compile(request!, new PlanEnvironment
        {
            BaseDirectory = root, Connectors = FeatureConnectorRegistry.CreateDefault(), Operators = operators ?? OperatorRegistry.CreateDefault(),
        });
    }
}
