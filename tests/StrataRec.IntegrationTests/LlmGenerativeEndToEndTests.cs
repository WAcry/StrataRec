using System.Text.Json;
using StrataRec.CommandLine.Commands;
using StrataRec.Core.Diagnostics;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Llm;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The sample with a <c>GenerativeRetriever@1</c> after the recall and an <c>LlmAnnotator@1</c> at the end, their models
/// bound to <c>fixed_text@1</c> by the bindings alone: the generated rows are appended, get their features from the fetch
/// planner before the safety filter and the scorers read them, and the leading rows get the annotation; a recording
/// replays identically, the annotator per entity when its window changes, and the retriever's call only for the same
/// prompt. Runs alone: it counts the model answer metrics.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class LlmGenerativeEndToEndTests
{
    private const string Pipeline = "pipelines/home_feed.jsonc";

    /// <summary>
    /// For user 1001 the recall has 1–40 and every third id after it: 58 and 57 are new (and score high), 3 is present, "x" is
    /// of the wrong type, the second 58 repeats, and 60, the third new id, is the last of max_items 3.
    /// </summary>
    private const string Ids = """[58, 3, 57, "x", 58, 60, 54]""";

    private const string Reason = "A good match for your interests.";

    [Fact]
    public async Task TheGeneratedRowsGetTheirFeatures_TheLeadingRowsTheirAnnotation_AndTheRecording_ReplaysIdentically()
    {
        var host = HomeFeedTestHost.LoadModified(root => AddLlm(root, Ids, Reason));
        using var metrics = new MetricCapture();

        var recorded = await NewEngine(host).ExecuteAsync(ReplayTests.Request("req-llm-gen", 1001, "home", ReplayTests.ControlArms));

        Assert.True(recorded.Succeeded, recorded.Error);
        var items = recorded.Items.ToDictionary(i => i.Id.Int64Value);
        Assert.Equal((1L, "food"), (items[58].Columns["gen_rank"].AsInt64(), items[58].Columns["category"].AsString())); // a catalog feature of the new row
        Assert.Equal((3L, "tech"), (items[57].Columns["gen_rank"].AsInt64(), items[57].Columns["category"].AsString()));
        Assert.All(recorded.Items.Where(i => i.Id.Int64Value is not (58 or 57 or 60)), i => Assert.True(i.Columns["gen_rank"].IsMissing));
        Assert.Equal(new[] { Reason, Reason, Reason }, recorded.Items.Take(3).Select(i => i.Columns["reason"].AsString()));
        Assert.All(recorded.Items.Skip(3), i => Assert.True(i.Columns["reason"].IsMissing));

        var run = Assert.Single(recorded.Recording!.Runs);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "llm_retrieve").Outcome);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "llm_annotate").Outcome);
        var retrieval = Assert.Single(run.ModelCalls, c => c.Model == "llm_retriever");
        Assert.Equal(("ok", 1, false, 1, Ids), (retrieval.Status, retrieval.BatchSize, retrieval.Keys.HasValue, retrieval.InputDigests.Count, retrieval.Texts["answer"]));
        var annotations = run.ModelCalls.Where(c => c.Model == "llm_annotator").ToList();
        Assert.Equal(3, annotations.Count);
        Assert.Equal(recorded.Items.Take(3).Select(i => i.Id.Int64Value).Order(),
            annotations.Select(c => Assert.Single(c.Keys!.Value.EnumerateArray()).GetInt64()).Order()); // each keyed by its row's entity
        Assert.Equal(new[] { ("over_limit", 1.0), ("present", 1.0), ("repeated", 1.0), ("unknown", 1.0), ("used", 3.0) },
            Counts(metrics, StrataRecMetrics.Names.ModelAnswerIds, "llm_retriever"));
        Assert.Equal(new[] { ("used", 3.0) }, Counts(metrics, StrataRecMetrics.Names.ModelAnswers, "llm_annotator"));

        // Through JSON, on a fresh engine whose stand-ins answer otherwise: the texts must come from the recording.
        metrics.Measurements.Clear();
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        var outcome = await NewEngine(HomeFeedTestHost.LoadModified(root => AddLlm(root, "[]", "Something else."))).ReplayAsync(bundle);

        var report = outcome.Report;
        Assert.True(report.Diff.Identical, ReplayTests.Describe(report));
        Assert.Equal(Items(recorded.Items), Items(outcome.Result.Items));
        Assert.Empty(report.Gaps);
        Assert.All(report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
        Assert.Empty(Counts(metrics, StrataRecMetrics.Names.ModelAnswerIds, "llm_retriever")); // a replay is not traffic
        Assert.Empty(Counts(metrics, StrataRecMetrics.Names.ModelAnswers, "llm_annotator"));
    }

    /// <summary>
    /// Each annotation is recorded by its row's entity: with a window of 5 instead of 3, the three recorded rows replay
    /// their text, and the two new ones were never annotated: missing, reported as not recorded, the node Degraded.
    /// </summary>
    [Fact]
    public async Task AfterTheAnnotatorsWindowChanges_EachEntityReplaysItsOwnAnnotation()
    {
        var recorded = await NewEngine(HomeFeedTestHost.LoadModified(root => AddLlm(root, Ids, Reason)))
            .ExecuteAsync(ReplayTests.Request("req-llm-window", 1001, "home", ReplayTests.ControlArms));
        Assert.True(recorded.Succeeded, recorded.Error);
        var modified = HomeFeedTestHost.LoadModified(root =>
        {
            AddLlm(root, Ids, Reason);
            ReplayTests.Edit(root, Pipeline, "\"column\": \"llm.reason\", \"window\": 3", "\"column\": \"llm.reason\", \"window\": 5");
        });

        var outcome = await NewEngine(modified).ReplayAsync(recorded.Recording!);

        var report = outcome.Report;
        var replayed = outcome.Result.Items;
        Assert.Equal(recorded.Items.Select(i => i.Id.Int64Value), replayed.Select(i => i.Id.Int64Value)); // the order is not the annotator's
        Assert.Equal(new[] { Reason, Reason, Reason }, replayed.Take(3).Select(i => i.Columns["reason"].AsString()));
        Assert.All(replayed.Skip(3), i => Assert.True(i.Columns["reason"].IsMissing));
        var gap = Assert.Single(report.Gaps);
        Assert.Equal(("model", "llm_annotator", "not_recorded", 2), (gap.Kind, gap.Source, gap.Reason, gap.Count));
        Assert.Equal(replayed.Skip(3).Take(2).Select(i => i.Id.ToString()), gap.Examples);
        var node = Assert.Single(report.Nodes, n => n.Node == "llm_annotate");
        Assert.Equal(("succeeded", "degraded"), (node.Recorded, node.Replayed));
        Assert.StartsWith("model 'llm_annotator' annotated 3 of 5 rows; 2 call(s) failed", node.ReplayedDetail);
    }

    /// <summary>The retriever's call is recorded whole: after its prompt changes, it is not recorded, and the node is skipped.</summary>
    [Fact]
    public async Task AfterTheRetrieversPromptChanges_ItsCallIsNotRecorded_AndTheNodeIsSkipped()
    {
        var recorded = await NewEngine(HomeFeedTestHost.LoadModified(root => AddLlm(root, Ids, Reason)))
            .ExecuteAsync(ReplayTests.Request("req-llm-prompt", 1001, "home", ReplayTests.ControlArms));
        Assert.True(recorded.Succeeded, recorded.Error);
        var modified = HomeFeedTestHost.LoadModified(root =>
        {
            AddLlm(root, Ids, Reason);
            ReplayTests.Edit(root, Pipeline, "as a JSON array of item ids.", "as a JSON array of item ids, best first.");
        });

        var outcome = await NewEngine(modified).ReplayAsync(recorded.Recording!);

        var report = outcome.Report;
        Assert.True(outcome.Result.Succeeded, outcome.Result.Error); // on_failure: skip
        Assert.Contains(report.Gaps, g => g is { Kind: "model", Source: "llm_retriever", Reason: "not_recorded" });
        var node = Assert.Single(report.Nodes, n => n.Node == "llm_retrieve");
        Assert.Equal(("succeeded", "failed"), (node.Recorded, node.Replayed));
        Assert.StartsWith("model 'llm_retriever' failed: ", node.ReplayedDetail);
        Assert.DoesNotContain(outcome.Result.Items, i => i.Id.Int64Value is 58 or 57);
    }

    [Fact]
    public async Task TheCommandLine_KnowsTheOperators_AndValidatesRunsAndReplaysTheWorkspace()
    {
        var root = HomeFeedTestHost.LoadModified(r => AddLlm(r, Ids, Reason)).Root;
        var operators = new StringWriter();
        Assert.Equal(0, OperatorsCommand.Run(Tool.Environment, "text", operators));
        Assert.Contains("LlmAnnotator@1", operators.ToString());
        Assert.Contains("GenerativeRetriever@1", operators.ToString());
        foreach (var op in new[] { "LlmAnnotator@1", "GenerativeRetriever@1" })
        {
            var schema = new StringWriter();
            Assert.Equal(0, SchemaCommand.Run(Tool.Environment, "operator", op, schema, TextWriter.Null));
            using var document = JsonDocument.Parse(schema.ToString());
            Assert.True(document.RootElement.GetProperty("properties").TryGetProperty("prompts", out _), schema.ToString());
        }

        var validate = new StringWriter();
        Assert.True(ValidateCommand.Run(Tool.Environment, root, "local", null, "text", warningsAsErrors: false, validate) == 0, validate.ToString());

        var bundle = Path.Combine(Directory.CreateTempSubdirectory("stratarec-llm-gen-bundles-").FullName, "user_1001.json");
        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, root, "local", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), false, TextWriter.Null,
            CancellationToken.None, recordPath: bundle));
        using (var recorded = JsonDocument.Parse(File.ReadAllText(bundle)))
        {
            var calls = recorded.RootElement.GetProperty("runs")[0].GetProperty("model_calls").EnumerateArray().ToList();
            Assert.Contains(calls, c => c.GetProperty("model").GetString() == "llm_retriever" && c.GetProperty("texts").GetProperty("answer").GetString() == Ids);
            Assert.Contains(calls, c => c.GetProperty("model").GetString() == "llm_annotator" && c.GetProperty("texts").GetProperty("answer").GetString() == Reason);
        }

        var replay = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, root, "local", bundle, Array.Empty<string>(), "text", expectIdentical: true, replay, CancellationToken.None));
        Assert.Contains("identical to the recorded result", replay.ToString());
    }

    /// <summary>
    /// A retriever of up to 3 ids right after the recall (skipped when it fails), whose model answers <paramref name="ids"/>,
    /// writing each generated row's position to <c>llm.gen_rank</c>; and, after everything, an annotation of the top 3 rows
    /// in <c>llm.reason</c>, whose model answers <paramref name="reason"/>. The recall's attribution columns are declared
    /// nullable: the retriever's rows have none.
    /// </summary>
    private static void AddLlm(string root, string ids, string reason)
    {
        ReplayTests.Edit(root, Pipeline, "    \"score.coarse\":",
            """
                "recall.sources_mask": { "type": "int64?" }, "recall.best_rank": { "type": "int64?" },
                "recall.best_score": { "type": "float64?" }, "recall.primary_source": { "type": "string?" },
                "score.coarse":
            """);
        ReplayTests.Edit(root, Pipeline, "    \"safety_filter\": {",
            """
                "llm_retrieve": {
                  "order": 150, "op": "GenerativeRetriever@1", "on_failure": "skip",
                  "config": {
                    "model": "llm_retriever", "output": "answer", "max_items": 3, "rank_col": "llm.gen_rank", "max_output_tokens": 64,
                    "prompts": {
                      "user": "Suggest {{ctx.request.surface}} items{{#ctx.request.user_id}} for user {{ctx.request.user_id}}{{/ctx.request.user_id}} as a JSON array of item ids."
                    }
                  }
                },
                "llm_annotate": {
                  "order": 600, "op": "LlmAnnotator@1", "on_failure": "skip",
                  "config": {
                    "model": "llm_annotator", "output": "answer", "column": "llm.reason", "window": 3, "max_output_tokens": 48,
                    "prompts": {
                      "user": "In one sentence, why would this user like item {{col.entity_id}} ({{col.item.category}}{{#col.item.age_hours}}, {{col.item.age_hours}} hours old{{/col.item.age_hours}})?"
                    }
                  }
                },
                "safety_filter": {
            """);
        ReplayTests.Edit(root, Pipeline, "\"primary_source\": \"col.recall.primary_source\"",
            "\"primary_source\": \"col.recall.primary_source\", \"reason\": \"col.llm.reason\", \"gen_rank\": \"col.llm.gen_rank\"");
        ReplayTests.Edit(root, "bindings/local.jsonc", "\"models\": {",
            $$"""
            "models": {
                "llm_retriever": { "client": "fixed_text@1", "inputs": ["user"], "outputs": { "answer": {{JsonSerializer.Serialize(ids)}} } },
                "llm_annotator": { "client": "fixed_text@1", "inputs": ["user"], "outputs": { "answer": {{JsonSerializer.Serialize(reason)}} } },
            """);
    }

    private static RecEngine NewEngine(HomeFeedTestHost host)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().AddLlm() });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static List<(long, int, string)> Items(IReadOnlyList<StrataRec.Pipeline.Execution.RankedItem> items) =>
        items.Select(i => (i.Id.Int64Value, i.Rank, string.Join(";", i.Columns.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"))))
            .ToList();

    private static List<(string Outcome, double Count)> Counts(MetricCapture metrics, string instrument, string model) =>
        metrics.Of(instrument)
            .Where(m => Equals(m.Tags[StrataRecTags.Model], model))
            .GroupBy(m => (string)m.Tags[StrataRecTags.Outcome]!)
            .Select(g => (g.Key, g.Sum(m => m.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
}
