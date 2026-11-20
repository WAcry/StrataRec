using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Memory;
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

    /// <summary>
    /// Review 88: a tap of entered or dropped rows logs rows that were live when the node filled, none that its position
    /// appends. The rule refused it as it refuses a tap of survived rows there.
    /// </summary>
    [Theory]
    [InlineData("entered")]
    [InlineData("dropped")]
    public async Task ATapOfEnteredOrDroppedRows_AtAPositionThatAppends_LogsWhatTheNodeFilled(string scope)
    {
        var pipeline = Appending(Pipeline("""{ "after": "next" }""", "col.item.base", primary: "item.base", backup: "", output: "1.0"))
            .Replace("\"key\": { \"group\": \"all\",", $"\"key\": {{ \"group\": \"all\", \"rows\": {{ \"scope\": \"{scope}\" }},", StringComparison.Ordinal);
        if (scope == "dropped")
        {
            pipeline = pipeline.Replace("\"op\": \"AppendsOne@1\", \"config\": { \"output_col\": \"score.t\"",
                "\"op\": \"AppendsOne@1\", \"config\": { \"drop_entered\": true, \"output_col\": \"score.t\"", StringComparison.Ordinal);
        }

        var compilation = Compile(pipeline, OperatorRegistry.CreateDefault().Add(new FailsAfterReadingFactory()).Add(new AppendsOneFactory()));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));

        var result = await ExecuteAsync(compilation.Plan!, capture: true, write: false, pool: new DirtyPool());

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("backup", result.Outcomes.Single(o => o.NodeId == "next").ProducedBy);
        Assert.Contains(result.Items, item => item.Id.Int64Value == 2);
        var batch = Assert.Single(result.LogBatches, b => b.TapId == "key");
        Assert.Equal(new long[] { 1 }, batch.Int64EntityIds);
        Assert.Equal(new long[] { 10 }, (long[])Assert.Single(batch.Columns).Values);
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

        public long Id { get; init; } = 2;

        /// <summary>Drops every row before appending (for a tap of dropped rows).</summary>
        public bool DropEntered { get; init; }
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
        public OperatorDescriptor Descriptor { get; } = new("AppendsOne", 1, Effects.RowSource | Effects.RowFilter, typeof(AppendingConfig), "Appends candidate 2.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<AppendingConfig>()!;
            return context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output) ? new Appends(output, config.Id, config.DropEntered) : null;
        }

        private sealed class Appends(OutputColumn output, long id, bool dropEntered) : Operator
        {
            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => 1;

            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                if (dropEntered)
                {
                    context.Truncate(0);
                }

                var row = context.AppendRows(new long[] { id });
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

    /// <summary>
    /// Review 88: past the combination limit, the compiler checks each rule's scope. A tap's candidate feature was decided
    /// by the tap, its attach point and the feature's readers; a layer adding a row source (or a row-source fallback)
    /// between two readers was in no scope, so three layers together (skip the later reader, add the row source, log the
    /// feature at the output) compiled, and the tap logged the appended row's storage. Each row source is in a scope.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AScopedCombinationCheck_DecidesATapsCandidateFeatureWithEachRowSource(bool useFallback)
    {
        var baseline = Pipeline("""{ "output": true }""", "col.entity_id", primary: "double(col.item.base)",
                backup: "1.0", laterReader: true, output: "1.0")
            .Replace("\"k\": 1", "\"k\": 2", StringComparison.Ordinal)
            .Replace("\"score.l\": { \"type\": \"float64\", \"default\": 0.0 }",
                "\"score.l\": { \"type\": \"float64\", \"default\": 0.0 }, \"score.app\": { \"type\": \"float64?\" }", StringComparison.Ordinal);
        var placements = new[]
        {
            Layer("a", """{ "nodes": { "later": { "when": "false" } } }"""),
            Layer("b", useFallback
                ? """
                  { "nodes": {
                     "more": { "order": 250, "op": "FailsAfterReading@1", "on_failure": "fallback", "fallback": "more_backup",
                         "config": { "output_col": "score.app", "expr": "entity_id" } },
                     "more_backup": { "role": "fallback", "op": "AppendsOne@1", "config": { "output_col": "score.app" } } } }
                  """
                : """
                  { "nodes": {
                     "more": { "order": 250, "op": "AppendsOne@1", "config": { "output_col": "score.app" } } } }
                  """),
            Layer("c", """{ "log_taps": { "key": { "columns": { "v": "col.item.base" } } } }"""),
            Layer("d", """{ "nodes": { "recall": { "fetch_barrier": false } } }"""),
        };
        var operators = OperatorRegistry.CreateDefault().Add(new AppendsOneFactory()).Add(new FailsAfterReadingFactory());
        var (request, environment) = Inputs(baseline, operators);
        var scopedRequest = CombinationLimitTests.With(request, placements, 8);
        foreach (var pair in new[] { new[] { "a/t", "b/t" }, new[] { "a/t", "c/t" }, new[] { "b/t", "c/t" } })
        {
            var merged = PlanCompiler.CompileMerged(scopedRequest, pair, environment);
            Assert.True(merged.Succeeded, string.Join("\n", merged.Diagnostics));
        }

        var triple = new[] { "a/t", "b/t", "c/t" };
        var joint = PlanCompiler.CompileMerged(scopedRequest, triple, environment);
        Assert.False(joint.Succeeded);
        Assert.Contains(joint.Diagnostics, d => d.IsError && d.Code == "TAP_FEATURE_NOT_FETCHED");

        var exhaustive = PlanCompiler.Compile(CombinationLimitTests.With(request, placements, 16), environment);
        Assert.True(exhaustive.Report.CombinationCheckExhaustive);
        Assert.False(exhaustive.Succeeded);
        Assert.Contains(exhaustive.Diagnostics, d => d.IsError && d.Code == "TAP_FEATURE_NOT_FETCHED");

        var scoped = PlanCompiler.Compile(scopedRequest, environment);
        Assert.False(scoped.Report.CombinationCheckExhaustive);
        Assert.False(scoped.Succeeded);
        Assert.Contains(scoped.Diagnostics, d => d.IsError && d.Code == "TAP_FEATURE_NOT_FETCHED");
    }

    /// <summary>
    /// Review 88: the hint named the first later node that reads the feature, though rows its position appends (by it or
    /// its fallback) have no value for it: moved there, the tap was refused again. It names the first that keeps it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHint_PassesOverALaterReaderWhosePositionAppendsRows(bool useFallback)
    {
        var basePipeline = Pipeline("""{ "after": "recall" }""", "col.item.base", primary: "double(col.item.base)",
            backup: useFallback ? "" : "1.0", laterReader: true, output: "1.0");
        var pipeline = useFallback
            ? Appending(basePipeline.Replace("double(col.item.base)", "item.base", StringComparison.Ordinal))
            : basePipeline
                .Replace("\"score.t\": { \"type\": \"float64\", \"default\": 0.0 }", "\"score.t\": { \"type\": \"float64?\" }", StringComparison.Ordinal)
                .Replace("\"next\": { \"order\": 200, \"op\": \"ExpressionScorer@1\"",
                    "\"next\": { \"order\": 200, \"op\": \"ReadsAndAppends@1\"", StringComparison.Ordinal)
                .Replace("double(col.item.base)", "item.base", StringComparison.Ordinal);
        var operators = OperatorRegistry.CreateDefault().Add(new AppendsOneFactory())
            .Add(new FailsAfterReadingFactory()).Add(new ReadsAndAppendsFactory());

        var before = Assert.Single(Compile(pipeline, operators).Diagnostics, d => d.IsError);
        Assert.Equal("TAP_FEATURE_NOT_FETCHED", before.Code);
        Assert.Equal("attach the tap after 'later', the first node after this point that reads 'item.base'", before.Hint);

        var accepted = Compile(pipeline.Replace("""{ "after": "recall" }""", """{ "after": "later" }""", StringComparison.Ordinal), operators);
        Assert.True(accepted.Succeeded, string.Join("\n", accepted.Diagnostics));
    }

    /// <summary>Review 88: each of two row sources needs a later reader to fill its rows; context features are not affected.</summary>
    [Fact]
    public async Task TwoRowSources_EachNeedALaterReaderToFillTheirRows_AndContextFeaturesStay()
    {
        var baseline = Pipeline("""{ "after": "refill" }""", "col.item.base", primary: "double(col.item.base)",
                backup: "double(ctx.user.w)", laterReader: true, output: "1.0", context: "ctx.user.w")
            .Replace("\"k\": 1", "\"k\": 3", StringComparison.Ordinal)
            .Replace("\"score.l\": { \"type\": \"float64\", \"default\": 0.0 }",
                "\"score.l\": { \"type\": \"float64\", \"default\": 0.0 }, \"score.app\": { \"type\": \"float64?\" }, \"score.second\": { \"type\": \"float64\", \"default\": 0.0 }", StringComparison.Ordinal)
            .Replace("\"next\": {", """
                "more": { "order": 250, "op": "AppendsOne@1", "config": { "output_col": "score.app" } },
                "refill": { "order": 260, "op": "ExpressionScorer@1", "config": { "output_col": "score.second", "expr": "double(col.item.base)" } },
                "last_more": { "order": 275, "op": "AppendsOne@1", "config": { "output_col": "score.app", "id": 3 } },
                "next": {
                """, StringComparison.Ordinal);

        var operators = OperatorRegistry.CreateDefault().Add(new AppendsOneFactory());
        var middle = Compile(baseline, operators);
        Assert.True(middle.Succeeded, string.Join("\n", middle.Diagnostics));
        var middleResult = await ExecuteAsync(middle.Plan!, capture: true, write: false, pool: new DirtyPool());
        Assert.True(middleResult.Succeeded, middleResult.Error);
        var middleBatch = Assert.Single(middleResult.LogBatches, b => b.TapId == "key");
        Assert.Equal(new long[] { 1, 2 }, middleBatch.Int64EntityIds);
        Assert.Equal(new long[] { 10, 0 }, (long[])Assert.Single(middleBatch.Columns).Values);
        Assert.Equal(3L, Assert.Single(middleBatch.Context).Value.AsInt64());

        var moved = baseline.Replace("""{ "after": "refill" }""", """{ "after": "last_more" }""", StringComparison.Ordinal);
        var invalid = Compile(moved, operators);
        var error = Assert.Single(invalid.Diagnostics, d => d.IsError);
        Assert.Equal("TAP_FEATURE_NOT_FETCHED", error.Code);
        Assert.Equal("attach the tap after 'later', the first node after this point that reads 'item.base'", error.Hint);

        var last = Compile(baseline.Replace("""{ "after": "refill" }""", """{ "after": "later" }""", StringComparison.Ordinal), operators);
        Assert.True(last.Succeeded, string.Join("\n", last.Diagnostics));
        var lastResult = await ExecuteAsync(last.Plan!, capture: true, write: false, pool: new DirtyPool());
        Assert.True(lastResult.Succeeded, lastResult.Error);
        var lastBatch = Assert.Single(lastResult.LogBatches, b => b.TapId == "key");
        Assert.Equal(new long[] { 1, 2, 3 }, lastBatch.Int64EntityIds);
        Assert.Equal(new long[] { 10, 0, 0 }, (long[])Assert.Single(lastBatch.Columns).Values);
        Assert.Equal(3L, Assert.Single(lastBatch.Context).Value.AsInt64());

        var outputPipeline = moved
            .Replace("""{ "after": "last_more" }""", """{ "output": true }""", StringComparison.Ordinal)
            .Replace(""""score": "1.0"""", """"score": "double(col.item.base)"""", StringComparison.Ordinal)
            .Replace(""""later": { "order": 300, "op": "ExpressionScorer@1", "config": { "output_col": "score.l", "expr": "double(col.creator.pop)" } }"""",
                """"later": { "order": 300, "when": "false", "op": "ExpressionScorer@1", "config": { "output_col": "score.l", "expr": "double(col.creator.pop)" } }"""", StringComparison.Ordinal);
        var output = Compile(outputPipeline, operators);
        Assert.True(output.Succeeded, string.Join("\n", output.Diagnostics));
        var outputResult = await ExecuteAsync(output.Plan!, capture: true, write: false, pool: new DirtyPool());
        Assert.True(outputResult.Succeeded, outputResult.Error);
        Assert.Equal(NodeOutcomeKind.SkippedWhen, outputResult.Outcomes.Single(o => o.NodeId == "later").Kind);
        var outputBatch = Assert.Single(outputResult.LogBatches, b => b.TapId == "key");
        Assert.Equal(new long[] { 1, 2, 3 }, outputBatch.Int64EntityIds);
        Assert.Equal(new long[] { 10, 0, 0 }, (long[])Assert.Single(outputBatch.Columns).Values);
        Assert.Equal(3L, Assert.Single(outputBatch.Context).Value.AsInt64());
    }

    private sealed class ReadsAndAppendsFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("ReadsAndAppends", 1, Effects.RowSource, typeof(ReadingConfig), "Reads a column and appends a row.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<ReadingConfig>()!;
            return context.TryReadColumn(config.Expr, "/expr", out _) && context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output)
                ? new Appends(output) : null;
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

    private static ExperimentPlacement Layer(string id, string patch) => new(new ExperimentArtifact
    {
        ExperimentId = id,
        PipelineId = "p",
        Owner = "owner",
        Description = "probe",
        ExpiresAt = "2099-01-01",
        Arms = new Dictionary<string, JsonObject> { ["t"] = JsonNode.Parse(patch)!.AsObject() },
    }, "layer_" + id);

    private sealed class DirtyPool : IBufferPool
    {
        public T[] Rent<T>(int minimumLength)
        {
            var array = new T[Math.Max(1, minimumLength)];
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array)), array.Length * Unsafe.SizeOf<T>()).Fill(0xA5);
            }

            return array;
        }

        public void Return<T>(T[] array, bool clearArray)
        {
        }
    }

    private static Task<PipelineResult> Execute(ExecutionPlan plan, bool capture, bool write) =>
        ExecuteAsync(plan, capture, write).AsTask();

    private static async ValueTask<PipelineResult> ExecuteAsync(ExecutionPlan plan, bool capture, bool write,
        RequestArms arms = default, IBufferPool? pool = null)
    {
        await using var logs = new LogPipeline(new InMemoryLogSink());
        return await new PipelineExecutor(pool: pool, logs: logs).ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "tap-key", Inputs = new RequestInputs().Set("user_id", 5), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000,
            CaptureLogs = capture, WriteLogs = write, Arms = arms,
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
        var (request, environment) = Inputs(pipeline, operators);
        return PlanCompiler.Compile(request, environment);
    }

    private static (PlanCompileRequest, PlanEnvironment) Inputs(string pipeline, OperatorRegistry? operators = null)
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
        return (request!, new PlanEnvironment
        {
            BaseDirectory = root, Connectors = FeatureConnectorRegistry.CreateDefault(), Operators = operators ?? OperatorRegistry.CreateDefault(),
        });
    }
}
