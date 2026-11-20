using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Runtime;
using StrataRec.Hosting.Workspace;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
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

    private static PlanCompilation Compile(string pipeline)
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
        return PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root, Connectors = FeatureConnectorRegistry.CreateDefault() });
    }
}
