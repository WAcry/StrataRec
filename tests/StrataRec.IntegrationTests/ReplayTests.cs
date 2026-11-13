using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>Recording requests into replay bundles and replaying them on the home_feed sample (design 13.4).</summary>
public sealed class ReplayTests
{
    internal static readonly string[] ControlArms = { "exp_freshness_calibration/control", "exp_mmr_diversity/control", "exp_quality_weight/control" };

    private static readonly string[] TreatmentArms = { "exp_freshness_calibration/calibrated", "exp_mmr_diversity/mmr", "exp_quality_weight/strong" };

    [Theory]
    [InlineData(1001L, "home", false)]
    [InlineData(null, "explore", false)]
    [InlineData(1002L, "home", true)]
    public async Task HomeFeed_RecordThenReplay_ReproducesTheResultExactly(long? userId, string surface, bool treatment)
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(Request("req-rt", userId, surface, treatment ? TreatmentArms : null));
        Assert.True(recorded.Succeeded, recorded.Error);

        // Through JSON, and on a fresh engine: the bundle alone carries everything the replay needs.
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        var (fresh, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var outcome = await fresh.ReplayAsync(bundle);

        var report = outcome.Report;
        Assert.True(report.Diff.Identical, Describe(report));
        Assert.Equal(Items(recorded), Items(outcome.Result));
        Assert.Equal(recorded.ServedBy, outcome.Result.ServedBy);
        Assert.Equal(recorded.Experiments.Assigned, outcome.Result.Experiments.Assigned);
        Assert.Equal(recorded.Experiments.Triggered, outcome.Result.Experiments.Triggered);
        Assert.Equal(report.RecordedArms, report.ReplayedArms);
        Assert.Equal(bundle.Manifest.Id, report.ReplayedManifest);
        Assert.Empty(report.Artifacts);
        Assert.Empty(report.Params);
        Assert.Empty(report.Gaps);
        Assert.Empty(report.Notes);
        Assert.NotEmpty(report.Nodes);
        Assert.All(report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
    }

    [Fact]
    public async Task Bundle_RecordsInputsAssignmentManifestEveryCallDecisionSamplingAndResult()
    {
        var (engine, plan) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var recorded = await engine.ExecuteAsync(Request("req-shape", 1001, "home", ControlArms, captureLogs: true));

        var bundle = recorded.Recording!;
        Assert.Equal(ReplayBundle.CurrentFormat, bundle.Format);
        Assert.Equal(1001, bundle.Request.Inputs["user_id"].Value!.Value.GetInt64());
        Assert.Equal("forced", bundle.Request.Assignment.Source);
        Assert.Equal(ControlArms.Order(), bundle.Request.Assignment.Arms.Order());
        Assert.Equal(plan.ManifestId, bundle.Manifest.Id);
        Assert.Contains("pipeline:home_feed", bundle.Manifest.Artifacts.Keys);
        Assert.Contains("catalog:home_feed", bundle.Manifest.Artifacts.Keys);

        var run = Assert.Single(bundle.Runs);
        Assert.Equal("primary", run.Role);
        Assert.Equal("30", run.Params["coarse_k"].Value!.Value.GetRawText());
        var recall = run.Nodes.Single(n => n.Node == "recall");
        Assert.Equal("succeeded", recall.Outcome);
        Assert.InRange(recall.RemainingMs!.Value, 1, 5000);
        Assert.Equal(ReplayTiming.Ran, Assert.Single(recall.Attempts).Timing);

        // Candidate calls with the returned candidates, feature calls with the values per key, model outputs per row.
        Assert.Equal(new[] { ("two_tower", 40), ("popular", 19) }, run.CandidateCalls.Select(c => (c.Source, c.Ids!.Value.GetArrayLength())));
        Assert.All(run.CandidateCalls, c => Assert.Equal("ok", c.Status));
        var items = run.FeatureCalls.Where(c => c.Source == "item_store").ToList();
        Assert.True(items.Count >= 3, "max_batch 25 splits the 59 recalled items into chunks");
        Assert.All(items, c => Assert.InRange(c.Keys!.Value.GetArrayLength(), 1, 25));
        Assert.All(items, c => Assert.All(c.Values!, v => Assert.Equal(c.Keys!.Value.GetArrayLength(), v.Value.GetArrayLength())));
        Assert.Equal(Enumerable.Range(0, run.FeatureCalls.Count + run.CandidateCalls.Count + run.ModelCalls.Count),
            run.FeatureCalls.Select(c => c.Seq).Concat(run.CandidateCalls.Select(c => c.Seq)).Concat(run.ModelCalls.Select(c => c.Seq)).Order());
        var model = Assert.Single(run.ModelCalls);
        Assert.Equal(("ctr_model", "ok", 30, 30), (model.Model, model.Status, model.Keys!.Value.GetArrayLength(), model.InputDigests.Count));
        Assert.Equal(30, model.Outputs["p_click"].GetArrayLength());

        Assert.Equal("captured", run.Sampling.Logs);
        Assert.Contains(run.Sampling.Groups, g => g is { Group: "all", Rate: 1.0, EffectiveRate: 1.0, Sampled: true });
        Assert.Contains(run.Sampling.Taps, t => t is { Tap: "served", Active: true });

        Assert.Equal(10, bundle.Result.Items.Count);
        Assert.Equal(("float64", "string"), (bundle.Result.ColumnTypes["score"], bundle.Result.ColumnTypes["category"]));
        Assert.Equal(recorded.Items[0].Id.Int64Value, bundle.Result.Items[0].Id.GetInt64());
    }

    [Fact]
    public async Task ExternalAssignment_UnknownArms_AreReplayedAsRecorded()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-unknown-arms",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Assignment = new[] { "exp_quality_weight/strong", "not_real/unknown" },
            Deadline = TimeSpan.FromSeconds(30),
            TimeMs = 1_767_225_600_000,
            Debug = new DebugOptions { Record = true },
        });
        Assert.Equal(1, recorded.Experiments.UnknownArms);
        Assert.Equal(1, recorded.Recording!.Request.Assignment.UnknownArms);

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording)));
        var withArms = await engine.ReplayAsync(recorded.Recording, new ReplayOptions { Arms = new[] { "exp_quality_weight/strong" } });

        Assert.True(outcome.Report.Diff.Identical, Describe(outcome.Report));
        Assert.Equal(recorded.Experiments.Assigned, outcome.Result.Experiments.Assigned);
        Assert.Equal(1, outcome.Result.Experiments.UnknownArms);
        Assert.Equal(0, withArms.Result.Experiments.UnknownArms); // arms given to the replay replace the external assignment
    }

    [Fact]
    public async Task NonFiniteValues_TheRequestObserved_AreRecorded_AndReplayExactly()
    {
        // Item 1's embedding has a component beyond float32: memory@1 stages it as +∞ and the raw vector stays present
        // (has() is true). An output expression divides zero by zero. Neither is sanitized on the way to the caller, so a
        // bundle that wrote them as null would replay other values.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var path = Path.Combine(root, "fixtures", "items.json");
            var items = JsonNode.Parse(File.ReadAllText(path))!;
            items["1"]!["i_emb"]![0] = 1e100;
            File.WriteAllText(path, items.ToJsonString());
            Edit(root, "pipelines/home_feed.jsonc", "\"score\": \"col.score.final\",",
                "\"score\": \"col.score.final\", \"embedding_present\": \"has(col.item.embedding)\", \"undefined\": \"0.0 / 0.0\",");
        });
        var (engine, _) = MetricsTests.LoadEngine(host);
        var recorded = await engine.ExecuteAsync(Request("req-non-finite", 1001, "home", ControlArms, deadlineMs: 30_000));
        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.True(recorded.Items.Single(i => i.Id.Int64Value == 1).Columns["embedding_present"].AsBool());
        Assert.All(recorded.Items, i => Assert.True(double.IsNaN(i.Columns["undefined"].AsFloat64())));

        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        var embedding = bundle.Runs[0].FeatureCalls.Single(c => c.Fields.Any(f => f.Name == "i_emb") && c.Keys!.Value.EnumerateArray().Any(k => k.GetInt64() == 1));
        var row = embedding.Keys!.Value.EnumerateArray().Select(k => k.GetInt64()).ToList().IndexOf(1);
        Assert.Equal("\"Infinity\"", embedding.Values!["i_emb"][row][0].GetRawText());
        Assert.All(bundle.Result.Items, i => Assert.Equal("\"NaN\"", i.Columns["undefined"]!.Value.GetRawText()));

        var outcome = await engine.ReplayAsync(bundle);

        Assert.True(outcome.Report.Diff.Identical, Describe(outcome.Report));
        Assert.Empty(outcome.Report.Gaps);
        Assert.True(outcome.Result.Items.Single(i => i.Id.Int64Value == 1).Columns["embedding_present"].AsBool());

        // A NaN score column: the comparison ends, leaves the NaNs out of the distributions and counts them.
        var report = ResultComparisonBound.Run(() => ReplayComparison.Compare(new[] { new ReplayPair("a", "a", outcome, outcome) }, 10, "undefined", "base", "base"));
        Assert.Equal((10, 0, 0.0), (report.Baseline.NonFiniteScores, report.Baseline.Scores.Count, report.ScoreKs));
        Assert.Equal(1, report.Identical);
    }

    [Fact]
    public async Task RecordingIsOptIn()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var result = await engine.ExecuteAsync(MetricsTests.Request("req-no-record"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Recording);
    }

    [Fact]
    public async Task RecordedTimeouts_ReplayAsTimeouts_WithTheSameDecisions_WithoutWaiting()
    {
        // popular and the CTR model would take a minute; the source's 20 ms timeout and the node's 50 ms timeout cut
        // them off, ctr_ranker falls back to ctr_backup, and diversity_cap is skipped for want of 10 s (deadline 5 s).
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            Edit(root, "bindings/local.jsonc", "\"data_file\": \"fixtures/recall_popular.json\"",
                "\"data_file\": \"fixtures/recall_popular.json\", \"latency_ms\": 60000, \"timeout_ms\": 20");
            Edit(root, "bindings/local.jsonc", "\"client\": \"linear@1\",", "\"client\": \"linear@1\", \"latency_ms\": 60000,");
            Edit(root, "pipelines/home_feed.jsonc", "\"order\": 520,", "\"order\": 520, \"min_remaining_ms\": 10000,");
        });
        var (engine, _) = MetricsTests.LoadEngine(host);
        var recorded = await engine.ExecuteAsync(Request("req-slow", 1001, "home", ControlArms));
        Assert.True(recorded.Succeeded, recorded.Error);

        var run = recorded.Recording!.Runs[0];
        var popular = run.CandidateCalls.Single(c => c.Source == "popular");
        Assert.Equal(("timeout", 0), (popular.Status, popular.Ids?.GetArrayLength() ?? 0));
        var model = Assert.Single(run.ModelCalls);
        Assert.Equal(("timeout", true), (model.Status, model.Threw));
        var ranker = run.Nodes.Single(n => n.Node == "ctr_ranker");
        Assert.Equal("fallback", ranker.Outcome);
        Assert.Equal(ReplayTiming.TimedOut, ranker.Attempts[0].Timing);
        Assert.Equal("skipped_deadline", run.Nodes.Single(n => n.Node == "diversity_cap").Outcome);

        // The replay answers every call from the bundle and takes timing from it: nothing is called, nothing is
        // awaited, so it completes synchronously (on a slow machine as on a fast one).
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));
        var pending = engine.ReplayAsync(bundle);

        Assert.True(pending.IsCompletedSuccessfully, "the replay waited for a call or a timer");
        var outcome = await pending;
        Assert.True(outcome.Report.Diff.Identical, Describe(outcome.Report));
        Assert.All(outcome.Report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
        var replayed = outcome.Report.Nodes.Single(n => n.Node == "ctr_ranker");
        Assert.Equal(("fallback", ranker.Detail), (replayed.Replayed, replayed.ReplayedDetail));
        Assert.Equal("skipped_deadline", outcome.Report.Nodes.Single(n => n.Node == "diversity_cap").Replayed);
        Assert.Contains(outcome.Result.Trace!, t => t.NodeId == "recall" && t.Detail is { } d && d.Contains("popular", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordedSourceFailure_ReplaysAsTheSameFailure()
    {
        var host = HomeFeedTestHost.LoadModified(MetricsTests.FailAuthorStore);
        var (engine, _) = MetricsTests.LoadEngine(host);
        var recorded = await engine.ExecuteAsync(Request("req-fail", 1001, "home", ControlArms));
        Assert.Contains(recorded.Recording!.Runs[0].FeatureCalls, c => c.Source == "author_store" && c.Status == "error" && c.Values is null);

        // Replayed on the healthy sample: the recorded failure is replayed, not the healthy source.
        var (healthy, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var outcome = await healthy.ReplayAsync(recorded.Recording);

        Assert.True(outcome.Report.Diff.Identical, Describe(outcome.Report));
        Assert.Equal("fallback", outcome.Report.Nodes.Single(n => n.Node == "ctr_ranker").Replayed);
        Assert.Contains(outcome.Report.Artifacts, a => a.Name == "bindings:local");
    }

    [Fact]
    public async Task FallbackPipeline_IsRecordedAsItsOwnRun_AndReplayedFromIt()
    {
        var root = ReviewRegressionTests.FallbackWorkspace(brokenLatencyMs: 0);
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
        ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", new[] { "primary" }).ThrowIfErrors("load");
        var recorded = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "primary", RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(2),
            Debug = new DebugOptions { Record = true },
        });
        Assert.Equal("backup", recorded.ServedBy);

        var bundle = recorded.Recording!;
        Assert.Equal(new[] { ("primary", "primary"), ("backup", "fallback_pipeline") }, bundle.Runs.Select(r => (r.PipelineId, r.Role)));
        Assert.Equal("error", bundle.Runs[0].CandidateCalls.Single().Status);
        Assert.Equal(("backup", true), (bundle.Result.ServedBy, bundle.Result.Degraded));

        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(bundle)));

        Assert.True(outcome.Report.Diff.Identical, Describe(outcome.Report));
        Assert.Equal("backup", outcome.Result.ServedBy);
        Assert.Contains(outcome.Report.Nodes, n => n is { Node: "backup:recall", Recorded: "succeeded", Replayed: "succeeded" });
        Assert.Empty(outcome.Report.Gaps);
    }

    [Fact]
    public async Task ReplayOnAModifiedConfiguration_TreatsDataTheBundleLacksAsMissing_AndReportsIt()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(Request("req-mod", 1001, "home", ControlArms));

        // More candidates from two_tower than the recording holds, 10 more rows for the model, and an output column
        // reading a feature the recorded request never fetched.
        var modified = HomeFeedTestHost.LoadModified(root =>
        {
            Edit(root, "pipelines/home_feed.jsonc", "\"two_tower\": 40, \"popular\": 20", "\"two_tower\": 50, \"popular\": 20");
            Edit(root, "pipelines/home_feed.jsonc", "\"coarse_k\": 30,", "\"coarse_k\": 40,");
            Edit(root, "pipelines/home_feed.jsonc", "\"category\": \"col.item.category\",", "\"category\": \"col.item.category\", \"age\": \"col.item.age_hours\",");
        });
        var (candidate, _) = MetricsTests.LoadEngine(modified);
        var outcome = await candidate.ReplayAsync(recorded.Recording!);

        var report = outcome.Report;
        Assert.True(outcome.Result.Succeeded, outcome.Result.Error);
        Assert.False(report.Diff.Identical);
        Assert.Equal(new[] { "age" }, report.Diff.AddedColumns);
        Assert.Contains(report.Artifacts, a => a.Name == "pipeline:home_feed" && a.Recorded != a.Replayed);
        Assert.Contains(new ValueChange("coarse_k", "30", "40"), report.Params);
        Assert.Contains(new ValueChange("recall_quotas.two_tower", "40", "50"), report.Params);

        Assert.Equal(10, Gap(report, "candidate_source", "two_tower", null, "limit_exceeds_recording").Count);
        Assert.Equal(10, Gap(report, "model", "ctr_model", null, "not_recorded").Count);
        var age = Gap(report, "feature", "item_store", "i_age_h", "not_recorded");
        Assert.True(age.Count >= outcome.Result.Items.Count);
        Assert.NotEmpty(age.Examples);

        // Never invented: the field the bundle lacks is missing in every item, and its missing rate is 100%.
        Assert.All(outcome.Result.Items, i => Assert.True(i.Columns["age"].IsMissing));
        Assert.Equal(1.0, report.FieldStats.Single(s => s is { Source: "item_store", Field: "i_age_h" }).MissingRate);
        Assert.Equal(0.0, report.FieldStats.Single(s => s is { Source: "item_store", Field: "i_clk_7d" }).MissingRate);
    }

    [Fact]
    public async Task ReplayWithOtherArms_RunsTheirNodes_FromTheRecordedData()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(Request("req-arms", 1001, "home", ControlArms));

        var outcome = await engine.ReplayAsync(recorded.Recording!, new ReplayOptions { Arms = new[] { "exp_mmr_diversity/mmr", "no_such/arm" } });

        var report = outcome.Report;
        Assert.Equal(new[] { "exp_mmr_diversity/mmr" }, report.ReplayedArms);
        Assert.Contains(report.Notes, n => n.Contains("no_such/arm", StringComparison.Ordinal));
        Assert.Contains(report.Nodes, n => n is { Node: "mmr_diversifier", Recorded: null, Replayed: "succeeded" });
        Assert.Contains(report.Nodes, n => n is { Node: "diversity_cap", Recorded: "succeeded", Replayed: null });
        Assert.Contains(new ValueChange("mmr_lambda", "null", "0.7"), report.Params);

        // MMR reads item embeddings, which the control recording did not fetch: reported, not invented.
        Assert.Contains(report.Gaps, g => g is { Kind: "feature", Source: "item_store", Field: "i_emb", Reason: "not_recorded" });
    }

    [Fact]
    public async Task BatchComparison_ReportsRankingAndDataChangesOverSeveralBundles()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var bundles = new List<ReplayBundle>();
        foreach (var (id, userId, surface) in new[] { ("a", (long?)1001, "home"), ("b", (long?)1002, "home"), ("c", null, "explore") })
        {
            bundles.Add((await engine.ExecuteAsync(Request("req-" + id, userId, surface, ControlArms))).Recording!);
        }

        var candidateHost = HomeFeedTestHost.LoadModified(root =>
        {
            Edit(root, "pipelines/home_feed.jsonc", "\"quality_weight\": 0.3", "\"quality_weight\": 1.0");
            Edit(root, "pipelines/home_feed.jsonc", "\"coarse_k\": 30,", "\"coarse_k\": 35,");
        });
        var (candidate, _) = MetricsTests.LoadEngine(candidateHost);
        var pairs = new List<ReplayPair>();
        foreach (var bundle in bundles)
        {
            pairs.Add(new ReplayPair(bundle.Request.RequestId, bundle.Request.RequestId, await engine.ReplayAsync(bundle), await candidate.ReplayAsync(bundle)));
        }

        var report = ReplayComparison.Compare(pairs, 10, "score", "base", "cand");

        Assert.Equal(3, report.Bundles);
        Assert.Equal(3, report.Baseline.ReproducedRecording);
        Assert.Equal((0.0, 0.0), (report.Baseline.ErrorRate, report.Baseline.EmptyRate));
        Assert.InRange(report.Baseline.FeatureMissingRate, 0, 0.5); // fixtures leave a few values out
        Assert.Equal(0.0, report.Candidate.ErrorRate);
        Assert.Equal(30, report.Baseline.Scores.Count);
        Assert.Equal(3, report.Overlap.Count);
        Assert.InRange(report.Overlap.Mean!.Value, 0, 1);
        Assert.InRange(report.KendallTau.Mean!.Value, -1, 1);
        Assert.InRange(report.ScoreKs, 0.000001, 1); // a stronger quality weight scales every final score
        Assert.True(report.ScoreDelta.Count > 0);
        Assert.Empty(report.Baseline.Gaps);

        // coarse_k 35 sends rows to the model and to later stages that the recordings never fetched for.
        Assert.Contains(report.Candidate.Gaps, g => g is { Kind: "model", Source: "ctr_model", Reason: "not_recorded" });
        Assert.Contains(report.Candidate.Gaps, g => g is { Kind: "feature", Source: "item_store", Field: "i_author_id", Reason: "not_recorded" });
        Assert.True(report.Candidate.FeatureMissingRate > report.Baseline.FeatureMissingRate);
        Assert.Contains(report.FeatureMissing, f => f is { Source: "item_store", Field: "i_author_id", Baseline: 0.0, Candidate: > 0.0 });
        Assert.Equal(3, report.Requests.Count);

        // Comparing a configuration with itself: everything identical.
        var same = ReplayComparison.Compare(pairs.Select(p => p with { Candidate = p.Baseline }).ToList(), 10, "score", "base", "base");
        Assert.Equal((3, 1.0, 1.0, 0.0), (same.Identical, same.Overlap.Mean!.Value, same.KendallTau.Mean!.Value, same.ScoreKs));
        Assert.Empty(same.FeatureMissing);
    }

    internal static RecRequest Request(string requestId, long? userId, string surface, IReadOnlyList<string>? arms, bool captureLogs = false, int deadlineMs = 5000) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(userId, surface),
        Deadline = TimeSpan.FromMilliseconds(deadlineMs),
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Record = true, ForceArms = arms, CaptureLogs = captureLogs },
    };

    internal static void Edit(string root, string file, string from, string to)
    {
        var path = Path.Combine(root, file);
        var text = File.ReadAllText(path);
        var edited = text.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(text, edited);
        File.WriteAllText(path, edited);
    }

    internal static string Describe(ReplayReport report) =>
        string.Join("\n", report.Diff.Changes.Select(c => $"rank {c.Rank}: {c.RecordedId} -> {c.ReplayedId} " +
            string.Join(", ", c.Columns.Select(col => $"{col.Column} {col.Recorded} -> {col.Replayed}"))))
        + "\n" + string.Join("\n", report.Gaps.Select(g => g.Description))
        + "\n" + string.Join("\n", report.Nodes.Where(n => n.Changed).Select(n => $"{n.Node}: {n.Recorded} -> {n.Replayed} ({n.ReplayedDetail})"));

    private static ReplayGap Gap(ReplayReport report, string kind, string source, string? field, string reason) =>
        Assert.Single(report.Gaps, g => g.Kind == kind && g.Source == source && g.Field == field && g.Reason == reason);

    private static List<(long, int, string)> Items(RecResult result) =>
        result.Items.Select(i => (i.Id.Int64Value, i.Rank, string.Join(";", i.Columns.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")))).ToList();
}

/// <summary>
/// Runs a computation on a dedicated background thread with a generous bound, so a regression to an endless loop fails
/// the test instead of hanging the run (no thread-pool timing involved).
/// </summary>
internal static class ResultComparisonBound
{
    public static T Run<T>(Func<T> compute)
    {
        T result = default!;
        var thread = new Thread(() => result = compute()) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the computation did not end");
        return result;
    }
}

/// <summary>A replay is not traffic: it leaves metrics, spans and event logs (and so dependency health) untouched.</summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class ReplayTelemetryTests
{
    [Fact]
    public async Task Replay_RecordsNoMetricsSpansOrEvents()
    {
        var logging = new ListLoggerFactory();
        using var events = new RequestEventLog(logging.CreateLogger("test"), autoFlush: false);
        var host = HomeFeedTestHost.LoadModified(MetricsTests.FailAuthorStore);
        var (engine, _) = MetricsTests.LoadEngine(host, new PipelineExecutor(null, null, events));
        ReplayBundle bundle;
        using (var recording = new MetricCapture())
        {
            var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-telemetry", 1001, "home", ReplayTests.ControlArms));
            bundle = recorded.Recording!;
            Assert.NotEmpty(recording.Of(StrataRecMetrics.Names.SourceErrors)); // recording a request does not hide it
        }

        events.Flush();
        Assert.NotEmpty(logging.Entries);
        logging.Entries.Clear();

        using var metrics = new MetricCapture();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var outcome = await engine.ReplayAsync(bundle);

        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal("fallback", outcome.Report.Nodes.Single(n => n.Node == "ctr_ranker").Replayed); // the failure was replayed...
        Assert.Empty(metrics.Measurements); // ...without counting against author_store or the node
        Assert.Empty(spans);
        events.Flush();
        Assert.Empty(logging.Entries);
    }
}
