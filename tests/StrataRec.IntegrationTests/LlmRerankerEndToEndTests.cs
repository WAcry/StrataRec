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
/// Runs alone: it counts <c>stratarec.model.answer_ids</c>.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class LlmRerankerEndToEndTests
{
    private const string Pipeline = "pipelines/home_feed.jsonc";

    private const string Answer = "```json\n[3, 1, 9, 3]\n```";

    [Fact]
    public async Task TheAnswer_ReordersTheWindow_AndTheRecording_ReplaysIdentically_FromTheRecordedText()
    {
        var baseline = await NewEngine(HomeFeedTestHost.Load()).ExecuteAsync(ReplayTests.Request("req-llm", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);
        Assert.True(baseline.Succeeded, baseline.Error);
        var host = HomeFeedTestHost.LoadModified(root => AddLlm(root, Answer));
        using var metrics = new MetricCapture();

        var recorded = await NewEngine(host).ExecuteAsync(ReplayTests.Request("req-llm", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);

        Assert.True(recorded.Succeeded, recorded.Error);
        var before = baseline.Items.Select(i => i.Id.Int64Value).ToList();
        Assert.Equal(new[] { before[2], before[0], before[1], before[3] }.Concat(before.Skip(4)), recorded.Items.Select(i => i.Id.Int64Value));
        var run = Assert.Single(recorded.Recording!.Runs);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "llm_rerank").Outcome);
        var call = Assert.Single(run.ServiceCalls.FindAll(c => c.Role == "model"), c => c.Service == "llm_ranker");
        Assert.Equal(("ok", 1, false, 1), (call.Status, call.BatchSize, call.Keys.HasValue, call.InputDigests.Count));
        Assert.Equal(new Dictionary<string, string> { ["answer"] = Answer }, call.Texts);
        Assert.Equal((null, null), (call.OutputTokens, call.Truncated));
        Assert.Equal(new[] { ("repeated", 1.0), ("unknown", 1.0), ("used", 2.0) }, AnswerIds(metrics));

        metrics.Measurements.Clear();
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        var identity = HomeFeedTestHost.LoadModified(root => AddLlm(root, "[1, 2, 3, 4]"));
        var outcome = await NewEngine(identity).ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);

        var report = outcome.Report;
        Assert.True(report.Diff.Identical, ReplayTests.Describe(report));
        Assert.Equal(recorded.Items.Select(i => (i.Id.Int64Value, i.Rank)), outcome.Result.Items.Select(i => (i.Id.Int64Value, i.Rank)));
        Assert.Empty(report.Gaps);
        Assert.All(report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
        Assert.Empty(AnswerIds(metrics));
    }

    [Theory]
    [InlineData("template")]
    [InlineData("feature")]
    public async Task AfterThePromptChanges_TheReplayReportsTheInputsAsChanged_AndTheNodeIsSkipped(string change)
    {
        var baseline = await NewEngine(HomeFeedTestHost.Load()).ExecuteAsync(ReplayTests.Request("req-llm-changed", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);
        var recorded = await NewEngine(HomeFeedTestHost.LoadModified(root => AddLlm(root, Answer)))
            .ExecuteAsync(ReplayTests.Request("req-llm-changed", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);
        Assert.True(recorded.Succeeded, recorded.Error);

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
        var outcome = await NewEngine(modified).ReplayAsync(recorded.Recording!, cancellationToken: TestContext.Current.CancellationToken);

        var report = outcome.Report;
        Assert.True(outcome.Result.Succeeded, outcome.Result.Error);
        Assert.Contains(report.Gaps, g => g is { Kind: "model", Source: "llm_ranker", Reason: "inputs_changed" });
        Assert.DoesNotContain(report.Gaps, g => g.Source == "ctr_model");
        var node = Assert.Single(report.Nodes, n => n.Node == "llm_rerank");
        Assert.Equal(("succeeded", "failed"), (node.Recorded, node.Replayed));
        Assert.StartsWith("model 'llm_ranker' failed: ", node.ReplayedDetail);
        Assert.Equal(baseline.Items.Select(i => i.Id.Int64Value), outcome.Result.Items.Select(i => i.Id.Int64Value));
    }

    [Fact]
    public async Task TheCommandLine_KnowsTheOperatorAndTheClient_AndValidatesRunsAndReplaysTheWorkspace()
    {
        var root = HomeFeedTestHost.LoadModified(r => AddLlm(r, Answer)).Root;
        var operators = new StringWriter();
        Assert.Equal(0, OperatorsCommand.Run(Tool.Environment, "text", operators));
        Assert.Contains("LlmReranker@1", operators.ToString());
        Assert.Contains("fixed_text@1", operators.ToString());

        var validate = new StringWriter();
        Assert.True(ValidateCommand.Run(Tool.Environment, root, "local", null, "text", warningsAsErrors: false, validate) == 0, validate.ToString());

        var bundle = Path.Combine(Directory.CreateTempSubdirectory("stratarec-llm-bundles-").FullName, "user_1001.json");
        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, root, "local", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), false, TextWriter.Null,
            CancellationToken.None, recordPath: bundle));
        using (var recorded = JsonDocument.Parse(File.ReadAllText(bundle)))
        {
            var calls = recorded.RootElement.GetProperty("runs")[0].GetProperty("service_calls").EnumerateArray();
            Assert.Contains(calls, c => c.GetProperty("service").GetString() == "llm_ranker" && c.GetProperty("texts").GetProperty("answer").GetString() == Answer);
        }

        var replay = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, root, "local", [bundle], Array.Empty<string>(), "text", expectIdentical: true, replay, CancellationToken.None));
        Assert.Contains("identical to the recorded result", replay.ToString());
    }

    /// <summary>
    /// After <c>diversity_cap</c>: an LLM rerank of the top 4; the rows show a derived feature only the prompt reads.
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
        ReplayTests.Edit(root, "bindings/local.jsonc", "\"services\": {",
            $$"""
            "services": {
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
            .Where(m => Equals(m.Tags[StrataRecTags.IoTarget], "llm_ranker"))
            .GroupBy(m => (string)m.Tags[StrataRecTags.Outcome]!)
            .Select(g => (g.Key, g.Sum(m => m.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
}
