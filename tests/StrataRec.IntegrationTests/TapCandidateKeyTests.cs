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
/// Review 83: a tap may log the key of a chain only when that key is available wherever the tap runs. A context key is:
/// the context prefetch stores it whatever reads it (review 80's region key, <see cref="TapCombinationTests"/>). A
/// candidate key is written into the table only by a fill that consumes the feature keyed by it, never by a prefetch: the
/// key <c>col.item.base</c> of the <c>col.creator.pop</c> that only a node's fallback reads is not there when the fallback
/// does not run. The tap checks took it as fetched; the request then succeeded without log consumers and threw once a
/// sampled tap read the column nobody had written, captured or written to a sink.
/// </summary>
public sealed class TapCandidateKeyTests
{
    private const string Catalog = """
        { "catalog_id": "c",
          "entities": { "item": { "key": "col.entity_id" }, "creator": { "key": "col.item.base" } },
          "features": {
            "item.base": { "type": "int64", "source": "item_store", "field": "base", "default": 0 },
            "creator.pop": { "type": "int64", "source": "item_store", "field": "base", "default": 0 } } }
        """;

    /// <summary>
    /// The same compiled plan serves the request whether the fallback runs or not, and whether the tap is sampled: the
    /// check is the only place to stop it, after the node or at the output.
    /// </summary>
    [Theory]
    [InlineData("""{ "after": "next" }""")]
    [InlineData("""{ "output": true }""")]
    public void ATapLoggingTheCandidateKeyOfAFeatureOnlyAFallbackReads_IsToldItIsNotFetched(string attach)
    {
        var compilation = Compile(Pipeline(attach, logged: "col.item.base", primary: "1.0"));

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("TAP_FEATURE_NOT_FETCHED", "/log_taps/key"), (error.Code, error.Pointer));
        Assert.Contains("'item.base'", error.Message);
    }

    /// <summary>
    /// The neighbour that stays accepted: a node before the tap reads the key itself, so its fill wrote it. Logged as
    /// fetched without log consumers, captured, and written to a sink.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ATapLoggingAKeyANodeBeforeItReads_LogsIt(bool capture, bool write)
    {
        var compilation = Compile(Pipeline("""{ "after": "next" }""", logged: "col.item.base", primary: "double(col.item.base)"));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));

        await using var logs = new LogPipeline(new InMemoryLogSink());
        var result = await new PipelineExecutor(logs: logs).ExecuteAsync(compilation.Plan!, new PipelineRequest
        {
            RequestId = "tap-key", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(5), TimeMs = 1_767_225_600_000,
            CaptureLogs = capture, WriteLogs = write,
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        if (capture)
        {
            var column = Assert.Single(Assert.Single(result.LogBatches, b => b.TapId == "key").Columns);
            Assert.Equal(10L, ((long[])column.Values)[0]); // item 1's base
        }
    }

    /// <summary>
    /// Candidate 1, then <c>next</c> (a constant score unless <paramref name="primary"/> reads a feature) whose fallback
    /// reads <c>col.creator.pop</c>, keyed by <c>col.item.base</c>; the tap <c>key</c> logs <paramref name="logged"/>.
    /// </summary>
    private static string Pipeline(string attach, string logged, string primary) => $$"""
        { "pipeline_id": "p",
          "columns": { "score.t": { "type": "float64", "default": 0.0 } },
          "param_schema": { "q": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
          "params": { "q": { "first": 1 }, "k": 1 },
          "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
          "log_taps": { "key": { "group": "all", "attach": {{attach}}, "columns": { "v": "{{logged}}" } } },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q" } },
            "next": { "order": 200, "op": "ExpressionScorer@1", "on_failure": "fallback", "fallback": "backup",
              "config": { "output_col": "score.t", "expr": "{{primary}}" } },
            "backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.t", "expr": "double(col.creator.pop)" } } },
          "output": { "limit_param": "k", "columns": { "score": "col.score.t" } } }
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
              "sources": { "item_store": { "connector": "memory@1", "data": { "1": { "base": 10 }, "10": { "base": 7 } }, "timeout_ms": 60000 } },
              "candidate_sources": { "first": { "client": "memory_list@1", "items": [1] } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), pipeline);

        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root, Connectors = FeatureConnectorRegistry.CreateDefault() });
    }
}
