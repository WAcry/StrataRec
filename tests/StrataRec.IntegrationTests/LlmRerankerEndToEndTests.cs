using System.Text.Json;
using StrataRec.Cli.Commands;
using StrataRec.Core.Diagnostics;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Llm;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The sample with an <c>LlmReranker@1</c> after the diversity cap, its model bound to <c>fixed_text@1</c> by the
/// bindings alone: the prompts render request context, row features (a derived one included) and a nullable feature,
/// the fixed answer reorders the window, and a recording replays it without the client. Runs alone: it counts
/// <c>stratarec.model.answer_ids</c>.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class LlmRerankerEndToEndTests
{
    private const string Pipeline = "pipelines/home_feed.jsonc";

    /// <summary>Ranks 3 and 1 first; 9 names no row, and the second 3 repeats one.</summary>
    private const string Answer = "```json\n[3, 1, 9, 3]\n```";

    [Fact]
    public async Task TheAnswer_ReordersTheWindow_AndTheRecording_ReplaysIdentically_FromTheRecordedText()
    {
        var baseline = await NewEngine(HomeFeedTestHost.Load()).ExecuteAsync(ReplayTests.Request("req-llm", 1001, "home", ReplayTests.ControlArms));
        Assert.True(baseline.Succeeded, baseline.Error);
        var host = HomeFeedTestHost.LoadModified(root => AddLlm(root, Answer));
        using var metrics = new MetricCapture();

        var recorded = await NewEngine(host).ExecuteAsync(ReplayTests.Request("req-llm", 1001, "home", ReplayTests.ControlArms));

        Assert.True(recorded.Succeeded, recorded.Error);
        var before = baseline.Items.Select(i => i.Id.Int64Value).ToList();
        Assert.Equal(new[] { before[2], before[0], before[1], before[3] }.Concat(before.Skip(4)), recorded.Items.Select(i => i.Id.Int64Value));
        var run = Assert.Single(recorded.Recording!.Runs);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "llm_rerank").Outcome);
        var call = Assert.Single(run.ModelCalls, c => c.Model == "llm_ranker");
        Assert.Equal(("ok", 1, false, 1), (call.Status, call.BatchSize, call.Keys.HasValue, call.InputDigests.Count)); // one digest for the whole call
        Assert.Equal(new Dictionary<string, string> { ["answer"] = Answer }, call.Texts);
        Assert.Equal((null, null), (call.OutputTokens, call.Truncated)); // fixed_text@1 counts no tokens
        Assert.Equal(new[] { ("repeated", 1.0), ("unknown", 1.0), ("used", 2.0) }, AnswerIds(metrics));

        // Through JSON, on a fresh engine whose stand-in would answer the identity order: the text must come from the recording.
        metrics.Measurements.Clear();
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        var identity = HomeFeedTestHost.LoadModified(root => AddLlm(root, "[1, 2, 3, 4]"));
        var outcome = await NewEngine(identity).ReplayAsync(bundle);

        var report = outcome.Report;
        Assert.True(report.Diff.Identical, ReplayTests.Describe(report));
        Assert.Equal(recorded.Items.Select(i => (i.Id.Int64Value, i.Rank)), outcome.Result.Items.Select(i => (i.Id.Int64Value, i.Rank)));
        Assert.Empty(report.Gaps);
        Assert.All(report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
        Assert.Empty(AnswerIds(metrics)); // a replay is not traffic
    }

    [Theory]
    [InlineData("template")]
    [InlineData("feature")]
    public async Task AfterThePromptChanges_TheReplayReportsTheCallAsNotRecorded_AndTheNodeIsSkipped(string change)
    {
        var baseline = await NewEngine(HomeFeedTestHost.Load()).ExecuteAsync(ReplayTests.Request("req-llm-changed", 1001, "home", ReplayTests.ControlArms));
        var recorded = await NewEngine(HomeFeedTestHost.LoadModified(root => AddLlm(root, Answer)))
            .ExecuteAsync(ReplayTests.Request("req-llm-changed", 1001, "home", ReplayTests.ControlArms));
        Assert.True(recorded.Succeeded, recorded.Error);

        // The instruction is worded differently, or the derived feature the rows show is scaled differently.
        var modified = HomeFeedTestHost.LoadModified(root =>
        {
            AddLlm(root, Answer);
            if (change == "template")
            {
                ReplayTests.Edit(root, Pipeline, "Answer with a JSON array of ranks, best first.", "Answer with a JSON array of ranks.");
            }
            else
            {
                ReplayTests.Edit(root, "catalog/home_feed.jsonc", "col.item.smoothed_ctr * 100.0", "col.item.smoothed_ctr * 1000.0");
            }
        });
        var outcome = await NewEngine(modified).ReplayAsync(recorded.Recording!);

        var report = outcome.Report;
        Assert.True(outcome.Result.Succeeded, outcome.Result.Error); // on_failure: skip
        Assert.Contains(report.Gaps, g => g is { Kind: "model", Source: "llm_ranker", Reason: "not_recorded" });
        Assert.DoesNotContain(report.Gaps, g => g.Source == "ctr_model"); // its inputs did not change
        var node = Assert.Single(report.Nodes, n => n.Node == "llm_rerank");
        Assert.Equal(("succeeded", "failed"), (node.Recorded, node.Replayed));
        Assert.StartsWith("model 'llm_ranker' failed: ", node.ReplayedDetail);
        Assert.Equal(baseline.Items.Select(i => i.Id.Int64Value), outcome.Result.Items.Select(i => i.Id.Int64Value)); // the order left as it was
    }

    [Fact]
    public async Task TheCommandLine_KnowsTheOperatorAndTheClient_AndValidatesRunsAndReplaysTheWorkspace()
    {
        var root = HomeFeedTestHost.LoadModified(r => AddLlm(r, Answer)).Root;
        var operators = new StringWriter();
        Assert.Equal(0, OperatorsCommand.Run("text", operators));
        Assert.Contains("LlmReranker@1", operators.ToString());
        Assert.Contains("fixed_text@1", operators.ToString());

        var validate = new StringWriter();
        Assert.True(ValidateCommand.Run(root, "local", null, "text", warningsAsErrors: false, validate) == 0, validate.ToString());

        var bundle = Path.Combine(Directory.CreateTempSubdirectory("stratarec-llm-bundles-").FullName, "user_1001.json");
        Assert.Equal(0, await RunCommand.RunAsync(root, "local", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), false, TextWriter.Null,
            CancellationToken.None, recordPath: bundle));
        using (var recorded = JsonDocument.Parse(File.ReadAllText(bundle)))
        {
            var calls = recorded.RootElement.GetProperty("runs")[0].GetProperty("model_calls").EnumerateArray();
            Assert.Contains(calls, c => c.GetProperty("model").GetString() == "llm_ranker" && c.GetProperty("texts").GetProperty("answer").GetString() == Answer);
        }

        var replay = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(root, "local", bundle, Array.Empty<string>(), "text", expectIdentical: true, replay, CancellationToken.None));
        Assert.Contains("identical to the recorded result", replay.ToString());
    }

    /// <summary>
    /// After <c>diversity_cap</c>: an LLM rerank of the top 4, skipped when it fails, whose model is <c>fixed_text@1</c>
    /// answering <paramref name="answer"/>; the rows show a derived feature only the prompt reads.
    /// </summary>
    private static void AddLlm(string root, string answer)
    {
        ReplayTests.Edit(root, "catalog/home_feed.jsonc", "    \"author.log_followers\":",
            """
                "item.click_rate_pct": { "type": "float32", "expr": "col.item.smoothed_ctr * 100.0" },
                "author.log_followers":
            """);
        ReplayTests.Edit(root, Pipeline, "    \"diversity_cap\": {",
            """
                "llm_rerank": {
                  "order": 530, "op": "LlmReranker@1", "on_failure": "skip",
                  "config": {
                    "model": "llm_ranker", "output": "answer", "window": 4, "answer": "rank", "max_output_tokens": 32,
                    "prompts": {
                      "system": "You rank a {{ctx.request.surface}} feed{{#ctx.request.user_id}} for user {{ctx.request.user_id}}{{/ctx.request.user_id}}. Answer with a JSON array of ranks, best first.",
                      "user": "{{#rows}}{{rank}}. item {{col.entity_id}} ({{col.item.category}}): click rate {{col.item.click_rate_pct}}%, {{#col.item.age_hours}}{{col.item.age_hours}} hours old{{/col.item.age_hours}}{{^col.item.age_hours}}age unknown{{/col.item.age_hours}}\n{{/rows}}"
                    }
                  }
                },
                "diversity_cap": {
            """);
        ReplayTests.Edit(root, "bindings/local.jsonc", "\"models\": {",
            $$"""
            "models": {
                "llm_ranker": { "client": "fixed_text@1", "inputs": ["system", "user"], "outputs": { "answer": {{JsonSerializer.Serialize(answer)}} } },
            """);
    }

    private static RecEngine NewEngine(HomeFeedTestHost host)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().AddLlm() });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static List<(string Outcome, double Count)> AnswerIds(MetricCapture metrics) =>
        metrics.Of(StrataRecMetrics.Names.ModelAnswerIds)
            .Where(m => Equals(m.Tags[StrataRecTags.Model], "llm_ranker"))
            .GroupBy(m => (string)m.Tags[StrataRecTags.Outcome]!)
            .Select(g => (g.Key, g.Sum(m => m.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
}
