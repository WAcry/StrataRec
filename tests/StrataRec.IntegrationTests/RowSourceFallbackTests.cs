using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A failed row-source attempt leaves the candidate table as it was before the attempt (design 4.6,
/// <c>CandidateTable.Rollback</c>): the rows the primary appended are gone, so the fallback may append the same ids and
/// gets their capacity back (the table reserves max(primary, fallback) per position), and a replay, which skips a primary
/// recorded as timed out instead of running it, serves the same result. Rows filtered out earlier stay known, so no row
/// source revives them.
/// </summary>
/// <remarks>
/// The primary is <c>MultiSourceRecall@1</c> itself, wrapped so that it fails in a given way once its standard merge has
/// appended its candidates: every failure path of a node attempt ends in the same rollback.
/// </remarks>
public sealed class RowSourceFallbackTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("fails")]
    [InlineData("throws")]
    [InlineData("returns late")]
    [InlineData("fails to commit")]
    public async Task APrimaryThatAppendedThenFailed_LeavesNoRows_SoTheFallbackServesTheIdsItShares_AndAReplayAgrees(string how)
    {
        // seed appends 5 and 6, and drop filters 6 out. The primary appends 7, which the fallback also returns, and fails.
        // The fallback answers [6, 7, 11, 22]: 6 stays filtered, 7 is the fallback's. The primary's dead row 7 used to
        // be found by the fallback's dedup, so 7 was lost; a replay, skipping the timed-out primary, served it.
        var plan = Compile(how, seed: true, primary: "[7]", backup: "[6, 7, 11, 22]", backupQuota: 4);

        var recorded = await Execute(plan, record: true);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(new (long, string)[] { (5, "seed"), (7, "backup"), (11, "backup"), (22, "backup") }, Items(recorded));
        var recall = recorded.Outcomes.Single(o => o.NodeId == "recall");
        Assert.Equal(("recall_backup", 1, 4), (recall.ProducedBy, recall.RowsIn, recall.RowsOut));
        Assert.Equal(Failure(how) + "; served by fallback 'recall_backup'", recall.Detail);

        var replay = RunReplay.Create(recorded.Recording!, "/runs/0");
        var replayed = await Execute(plan, replay: replay);

        Assert.Empty(replay.Gaps);
        Assert.Equal(Items(recorded), Items(replayed));
        Assert.Equal(Outcomes(recorded), Outcomes(replayed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheFallback_ReturnsItsWholeQuota_WhenThePrimarysQuotaIsSmaller(bool primaryAppended)
    {
        // The position reserves max(primary 1, fallback 3) rows. A primary that appended its one row before failing gives
        // it back, so the fallback still fits [11, 22, 33]; without that it was cut to [11, 22].
        var plan = Compile(primaryAppended ? "fails" : null, seed: false, primary: "[7]", backup: "[11, 22, 33]", backupQuota: 3);

        var result = await Execute(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 11, 22, 33 }, Items(result).Select(i => i.Id));
        Assert.Equal("recall_backup", result.Outcomes.Single(o => o.NodeId == "recall").ProducedBy);
    }

    [Fact]
    public async Task AFailedRowSource_WithFeaturesPrefetchedAcrossIt_EndsAsIfOnlyItsFallbackHadRun()
    {
        // home_feed with a second row source between recall and safety_filter. It is no fetch barrier, so the item features
        // safety_filter reads are prefetched after recall, for recall's rows, and are still to be backfilled when the new
        // row source has appended 42 and 43 and failed. Its fallback appends 43, 45, 48 and 49 into the rows it gave back.
        // Every candidate is served (no diversity cap, k = 100), so every row's features and scores are compared with a
        // run where the fallback is the row source itself.
        var (failing, failingEngine) = await RunHomeFeed(primaryFails: true);
        var (reference, _) = await RunHomeFeed(primaryFails: false);

        Assert.True(failing.Succeeded, failing.Error);
        Assert.Equal(("extra_backup", "gave up after appending; served by fallback 'extra_backup'"),
            (failing.Trace!.Single(n => n.NodeId == "extra").ProducedBy, failing.Trace!.Single(n => n.NodeId == "extra").Detail));
        Assert.Contains(failing.Items, i => i.Id.Int64Value == 43); // the id both returned
        Assert.DoesNotContain(failing.Items, i => i.Id.Int64Value == 42); // the failed primary's own
        Assert.Equal(Served(reference.Items), Served(failing.Items));
        Assert.Equal(reference.Trace!.Select(n => (n.NodeId, n.RowsIn, n.RowsOut)), failing.Trace!.Select(n => (n.NodeId, n.RowsIn, n.RowsOut)));

        // The premise: the prefetch was sent before the failed attempt, for the rows that existed then; the fallback's rows
        // were fetched afterwards, and the failed primary's own row never.
        var run = failing.Recording!.Runs[0];
        var attempt = run.CandidateCalls.Single(c => c.Source == "extra_primary").Seq;
        var items = run.FeatureCalls.Where(c => c.Source == "item_store").ToList();
        var prefetched = items.Where(c => c.Seq < attempt).SelectMany(c => c.Keys!.Value.EnumerateArray().Select(k => k.GetInt64())).ToList();
        var after = items.Where(c => c.Seq > attempt).SelectMany(c => c.Keys!.Value.EnumerateArray().Select(k => k.GetInt64())).ToList();
        Assert.Equal(47, prefetched.Count); // recall's candidates
        Assert.Empty(prefetched.Intersect(new long[] { 42, 43, 45, 48, 49 }));
        Assert.Subset(after.ToHashSet(), new HashSet<long> { 43, 45, 48, 49 });
        Assert.DoesNotContain(42L, after);

        var replayed = await failingEngine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(failing.Recording!)));

        Assert.True(replayed.Report.Diff.Identical, ReplayTests.Describe(replayed.Report));
        Assert.Empty(replayed.Report.Gaps);
        Assert.Equal(Served(failing.Items), Served(replayed.Result.Items));
    }

    private static async Task<(RecResult Result, RecEngine Engine)> RunHomeFeed(bool primaryFails)
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            const string pipeline = "pipelines/home_feed.jsonc";
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"candidate_sources\": {", """
                "candidate_sources": {
                  "extra_primary": { "client": "memory_list@1", "items": [42, 43] },
                  "extra_backup": { "client": "memory_list@1", "items": [43, 45, 48, 49] },
                """);
            ReplayTests.Edit(root, pipeline, "\"quality_weight\": { \"type\": \"float64\", \"min\": 0, \"max\": 1 }",
                "\"quality_weight\": { \"type\": \"float64\", \"min\": 0, \"max\": 1 }, " +
                "\"extra_primary_quotas\": { \"type\": \"map<string,int64>\" }, \"extra_backup_quotas\": { \"type\": \"map<string,int64>\" }");
            ReplayTests.Edit(root, pipeline, "\"coarse_k\": 30,",
                "\"coarse_k\": 100, \"extra_primary_quotas\": { \"extra_primary\": 2 }, \"extra_backup_quotas\": { \"extra_backup\": 4 },");
            ReplayTests.Edit(root, pipeline, "\"final_top_k\": 10,", "\"final_top_k\": 100,");
            ReplayTests.Edit(root, pipeline, "\"safety_filter\": {", primaryFails
                ? """
                  "extra": { "order": 150, "op": "FailingRecall@1", "on_failure": "fallback:extra_backup", "fetch_barrier": false,
                             "config": { "quotas_param": "extra_primary_quotas" } },
                  "extra_backup": { "role": "fallback", "op": "MultiSourceRecall@1", "config": { "quotas_param": "extra_backup_quotas" } },
                  "safety_filter": {
                  """
                : """
                  "extra": { "order": 150, "op": "MultiSourceRecall@1", "fetch_barrier": false, "config": { "quotas_param": "extra_backup_quotas" } },
                  "safety_filter": {
                  """);
        });
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Operators = OperatorRegistry.CreateDefault().Add(new FailingRecallFactory("fails")),
        });
        var load = engine.Load(host.Request, host.Workspace.ArmRanges("home_feed", "local"));
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-extra",
            Inputs = HomeFeedTestHost.Inputs(1001, "explore"),
            Deadline = TimeSpan.FromSeconds(600),
            TimeMs = 1_767_225_600_000,
            Debug = new DebugOptions { Record = true, Trace = true, ForceArms = ReplayTests.ControlArms },
        }).AsTask().WaitAsync(Patience);
        return (result, engine);
    }

    private static List<string> Served(IReadOnlyList<RankedItem> items) =>
        items.Select(i => $"{i.Id.Int64Value}: " + string.Join(", ", i.Columns.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"))).ToList();

    private static string Failure(string how) => how switch
    {
        "fails" => "gave up after appending",
        "throws" => "InvalidOperationException: broke after appending",
        "returns late" => "timed out after 50 ms",
        _ => "wrote a missing value into non-nullable column 'recall.best_score'",
    };

    /// <param name="how">How the primary fails once it has appended its candidates; null for a primary whose source fails, so it appends nothing.</param>
    private static ExecutionPlan Compile(string? how, bool seed, string primary, string backup, int backupQuota)
    {
        var sources = $$"""
            "seed": { "client": "memory_list@1", "items": [5, 6] },
            "primary": { "client": "memory_list@1", "items": {{primary}}{{(how is null ? ", \"fail\": true" : "")}} },
            "backup": { "client": "memory_list@1", "items": {{backup}} }
            """;
        var seedNodes = seed
            ? """
              "seed": { "order": 50, "op": "MultiSourceRecall@1", "config": { "quotas_param": "seed_quotas" } },
              "drop": { "order": 60, "op": "ExpressionFilter@1", "config": { "predicate": "col.entity_id != 6" } },
              """
            : string.Empty;
        var timeout = how == "returns late" ? ", \"timeout_ms\": 50" : string.Empty;
        var pipeline = $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "param_schema": { "seed_quotas": { "type": "map<string,int64>" }, "primary_quotas": { "type": "map<string,int64>" },
                                "backup_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "seed_quotas": { "seed": 2 }, "primary_quotas": { "primary": 1 }, "backup_quotas": { "backup": {{backupQuota}} }, "k": 10 },
              "nodes": {
                {{seedNodes}}
                "recall": { "order": 100, "op": "{{(how is null ? "MultiSourceRecall@1" : "FailingRecall@1")}}", "on_failure": "fallback:recall_backup"{{timeout}},
                            "config": { "quotas_param": "primary_quotas" } },
                "recall_backup": { "role": "fallback", "op": "MultiSourceRecall@1", "config": { "quotas_param": "backup_quotas" } } },
              "output": { "limit_param": "k", "columns": { "primary": "col.recall.primary_source" } } }
            """;
        var operators = OperatorRegistry.CreateDefault().Add(new FailingRecallFactory(how ?? "fails"));
        return MiniWorkspace.Compile(sources, pipeline, operators: operators);
    }

    private static async Task<PipelineResult> Execute(ExecutionPlan plan, bool record = false, RunReplay? replay = null) =>
        await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = new RequestInputs(),
            TimeMs = 1_767_225_600_000,
            Deadline = replay is null ? TimeSpan.FromSeconds(600) : RunReplay.Deadline,
            Record = record,
            Replay = replay,
        }).AsTask().WaitAsync(Patience);

    private static List<(long Id, string Primary)> Items(PipelineResult result) =>
        result.Items.Select(i => (i.Id.Int64Value, i.Columns["primary"].AsString())).ToList();

    private static List<(string, NodeOutcomeKind, string?, string?, int, int)> Outcomes(PipelineResult result) =>
        result.Outcomes.Select(o => (o.NodeId, o.Kind, o.Detail, o.ProducedBy, o.RowsIn, o.RowsOut)).ToList();

    /// <summary>
    /// <c>FailingRecall@1</c>: <c>MultiSourceRecall@1</c> that fails once it has appended its candidates: it reports a
    /// failure, throws, returns only after its budget ran out, or leaves its non-nullable <c>recall.best_score</c> missing
    /// so that its commit fails.
    /// </summary>
    private sealed class FailingRecallFactory(string how) : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } =
            new("FailingRecall", 1, Effects.RowSource, typeof(MultiSourceRecallConfig), "MultiSourceRecall@1 that fails after appending.");

        public Operator? Compile(OperatorCompileContext context) =>
            new MultiSourceRecallFactory().Compile(context) is { } recall
                ? new FailingRecall(recall, how, context.Outputs.Single(o => o.Name == "recall.best_score"))
                : null;

        private sealed class FailingRecall(Operator recall, string how, OutputColumn score) : Operator
        {
            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => recall.GetRowLimit(parameters);

            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var before = context.Table.PhysicalRowCount;
                await recall.ExecuteAsync(context);
                Assert.True(context.Table.PhysicalRowCount > before, "the primary appended nothing");
                Assert.Equal(before, context.Table.FindRow(context.Table.GetKey(before))); // its own row is now in the table's index
                switch (how)
                {
                    case "fails":
                        context.Fail("gave up after appending");
                        break;
                    case "throws":
                        throw new InvalidOperationException("broke after appending");
                    case "returns late":
                        Assert.True(context.CancellationToken.WaitHandle.WaitOne(Patience)); // returns after its budget, as a success
                        break;
                    default:
                        context.Output(score).SetMissing(before);
                        break;
                }
            }
        }
    }
}
