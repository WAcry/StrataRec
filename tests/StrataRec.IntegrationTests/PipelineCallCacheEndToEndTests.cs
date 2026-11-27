using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Local;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A node's cache of its calls of a pipeline (<c>cache</c> of <c>PipelineSource@1</c>, with the tiers bound under
/// <c>pipelines.&lt;id&gt;.cache</c>): pipeline <c>feed</c> appends the rows of <c>promoted</c>, which counts its runs
/// (<see cref="Probe"/>). A hit is a call for which the pipeline did not run; what it returns says which request's run
/// computed it. Write-backs run in the background, so the tests wait for the table to hold a record, never for a time.
/// </summary>
public sealed class PipelineCallCacheEndToEndTests
{
    private const long T0 = 1_767_225_600_000;

    private const string Memory = """{ "cache": { "memory": { "max_bytes": "1MiB" } } }""";

    private const string Hour = """{ "ttl_ms": 3600000 }""";

    /// <summary>The calling pipeline: a few rows of its own, then the called pipeline's, with where they come from.</summary>
    private const string Feed = """
        {
          "pipeline_id": "feed",
          "entity_key_type": "int64",
          "request_schema": { "user_id": { "type": "int64?" }, "country": { "type": "string?" } },
          "columns": { "is_promoted": { "type": "bool?" }, "promo.bid": { "type": "float32?" } DECLARED },
          "param_schema": { "recall_quotas": { "type": "map<string,int64>", "min": 0, "max": 1000 }, "top_k": { "type": "int64", "min": 1, "max": 100 },
                            "promoted_k": { "type": "int64", "min": 0, "max": 100 } },
          "params": { "recall_quotas": { "two_tower": 6 }, "top_k": 50, "promoted_k": 10 },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1",
                        "config": { "quotas_param": "recall_quotas", "queries": { "two_tower": { "user_embedding": "ctx.user.embedding" } } } },
            "promoted": { "order": 200, "op": "PipelineSource@1", "on_failure": "fail_request",
              "config": { "pipeline": "promoted", "limit_param": "promoted_k", "inputs": { "slots": "3" }, "columns": { "promo.bid": "bid" }, "mark": "is_promoted",
                          "cache": CACHE, "computed_by": COMPUTED_BY } }
          },
          "output": { "limit_param": "top_k",
                      "columns": { "promoted": "col.is_promoted ?? false", "promo_bid": "col.promo.bid", "from": "col.promo.from", "at": "col.promo.at" } }
        }
        """;

    /// <summary>The called pipeline of <see cref="PipelineCallEndToEndTests"/> with a node that counts its runs, and a log tap.</summary>
    private static readonly string Called = PipelineCallEndToEndTests.Promoted
        .Replace("\"nodes\": {", "\"nodes\": {\n \"probe\": { \"order\": 50, \"op\": \"Probe@1\" },", StringComparison.Ordinal)
        .Replace("\"output\": {", """
            "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
            "log_taps": { "bids": { "group": "all", "delivery": "durable", "attach": { "output": true }, "columns": { "bid": "col.bid" } } },
            "output": {
            """, StringComparison.Ordinal);

    private const string ComputedBy = """{ "request_id": "promo.from", "time_ms": "promo.at" }""";

    private static string FeedWith(string cache, string computedBy = ComputedBy, string declared = "") =>
        Feed.Replace("CACHE", cache, StringComparison.Ordinal).Replace("COMPUTED_BY", computedBy, StringComparison.Ordinal).Replace("DECLARED", declared, StringComparison.Ordinal);

    private static HomeFeedTestHost Host(string cache = Hour, string? bound = Memory, Action<string>? more = null, string computedBy = ComputedBy, string declared = "") =>
        HomeFeedTestHost.LoadModified(root =>
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "promoted.jsonc"), Called);
            File.WriteAllText(Path.Combine(root, "pipelines", "feed.jsonc"), FeedWith(cache, computedBy, declared));
            foreach (var id in new[] { "feed", "promoted" })
            {
                File.WriteAllText(Path.Combine(root, "releases", id + ".local.jsonc"), PipelineCallEndToEndTests.Releases.Replace("PIPELINE", id, StringComparison.Ordinal));
                File.WriteAllText(Path.Combine(root, "experiments", "exp_" + id + ".jsonc"), PipelineCallEndToEndTests.Experiments.Replace("PIPELINE", id, StringComparison.Ordinal));
            }

            if (bound is not null)
            {
                SampleBindings.BindPipeline(root, "promoted", bound);
            }

            more?.Invoke(root);
        });

    /// <summary>An engine serving <c>promoted</c> and <c>feed</c>, with what the tests move and read.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public Rig(HomeFeedTestHost host, Probe? probe = null, SharedStores? stores = null, IAssignmentProvider? provider = null, params string[] pipelines)
        {
            Host = host;
            Probe = probe ?? new Probe();
            var bindings = PlanEnvironment.DefaultBindings();
            if (stores is not null)
            {
                bindings.Add(stores);
            }

            Logs = new LogPipeline(Sink);
            Engine = new RecEngine(
                new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(Probe), Bindings = bindings, Clock = Clock }, provider, Logs);
            foreach (var id in pipelines.Length == 0 ? ["promoted", "feed"] : pipelines)
            {
                Load(id);
            }
        }

        public HomeFeedTestHost Host { get; }

        public RecEngine Engine { get; }

        public Probe Probe { get; }

        public MovableClock Clock { get; } = new();

        public InMemoryLogSink Sink { get; } = new();

        public LogPipeline Logs { get; }

        /// <summary>Loads the workspace's pipeline as it is now on disk.</summary>
        public void Load(string pipelineId)
        {
            var load = Engine.Load(Compile(Host.Root, pipelineId));
            Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        }

        /// <param name="ask">The platform assigned nothing with the request: the engine's provider is asked.</param>
        public ValueTask<RecResult> Run(
            string requestId, long timeMs = T0, IReadOnlyList<string>? assignment = null, RequestInputs? inputs = null, DebugOptions? debug = null, string pipeline = "feed",
            bool ask = false) =>
            Engine.ExecuteAsync(new RecRequest
            {
                PipelineId = pipeline,
                RequestId = requestId,
                Inputs = inputs ?? new RequestInputs().Set("user_id", 1001),
                Assignment = ask ? null : assignment ?? [],
                Deadline = HomeFeedTestHost.Deadline,
                TimeMs = timeMs,
                Debug = debug ?? new DebugOptions { Trace = true },
            }, TestContext.Current.CancellationToken);

        /// <summary>The table of the results of <paramref name="pipelineId"/>'s calls.</summary>
        public LocalFeatureTable Results(string pipelineId = "promoted", string holder = "feed")
        {
            Assert.True(Engine.TryGetPipeline(holder, out var pipeline));
            return Assert.Single(pipeline.Bindings.Tables, t => t.Schema.Source == pipelineId && t.Schema.Entity == "results");
        }

        /// <summary>Waits until the table holds <paramref name="records"/> results: the write-backs so far have landed.</summary>
        public Task Cached(int records, string pipelineId = "promoted", string holder = "feed") =>
            CacheEngineTests.Eventually(() => Results(pipelineId, holder).Pin().RecordCount == records,
                () => $"{records} cached results of '{pipelineId}' (it holds {Results(pipelineId, holder).Pin().RecordCount})");

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Logs.DisposeAsync();
        }
    }

    private static PlanCompileRequest Compile(string root, string pipelineId)
    {
        var diagnostics = new DiagnosticBag();
        var request = Hosting.Workspace.ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest(pipelineId, "local", diagnostics);
        Assert.True(request is not null && !diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return request!;
    }

    private static List<RankedItem> Promoted(RecResult result) => result.Items.Where(i => i.Columns["promoted"].AsBool()).ToList();

    /// <summary>The called pipeline's rows as the feed returned them: ids and bids, in order.</summary>
    private static List<(long Id, double Bid)> Rows(RecResult result) => Promoted(result).Select(i => (i.Id.Int64Value, i.Columns["promo_bid"].AsFloat64())).ToList();

    private static List<(string From, long At)> Origins(RecResult result) =>
        Promoted(result).Select(i => (i.Columns["from"].AsString(), i.Columns["at"].AsInt64())).Distinct().ToList();

    private static string Json(RecResult result)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            RecResultJson.Write(json, result);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task ACachedCall_DoesNotRunThePipelineAgain_AndItsRowsSayWhichRequestComputedThem()
    {
        await using var rig = new Rig(Host());

        var first = await rig.Run("req-1", T0);
        await rig.Cached(1);
        var second = await rig.Run("req-2", T0 + 5_000);

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(1, rig.Probe.Runs);
        Assert.NotEmpty(Rows(first));
        Assert.Equal(Rows(first), Rows(second));

        // The rows say whose run computed them, a run of this request or of an earlier one; the feed's own rows say nothing.
        Assert.Equal([("req-1", T0)], Origins(first));
        Assert.Equal([("req-1", T0)], Origins(second));
        Assert.All(second.Items.Where(i => !i.Columns["promoted"].AsBool()), i => Assert.True(i.Columns["from"].IsMissing && i.Columns["at"].IsMissing));

        // The trace has the run with its nodes, and the hit with where its result comes from and no nodes: nothing ran.
        var ran = Assert.Single(first.Calls!);
        Assert.Null(ran.Cached);
        Assert.Contains(ran.Trace, n => n.NodeId == "probe");
        var served = Assert.Single(second.Calls!);
        Assert.Equal(new CachedFrom("req-1", T0), served.Cached);
        Assert.True(served.Succeeded);
        Assert.Empty(served.Trace);
        Assert.Equal("succeeded", NodeOutcomeNames.Name(second.Trace!.Single(n => n.NodeId == "promoted").Kind));
        Assert.Contains($$"""
            "cached_from":{"request_id":"req-1","time_ms":{{T0}}}
            """.Trim(), Json(second));
        Assert.DoesNotContain("cached_from", Json(first));
    }

    /// <summary>
    /// A result is found again by what the called pipeline would compute and from what: its inputs and the request's arms
    /// in it. Not by the request's id or time, nor by the arms of the pipeline that calls.
    /// </summary>
    [Fact]
    public async Task OnlyACallThatWouldComputeTheSame_IsAnsweredFromTheCache()
    {
        await using var rig = new Rig(Host());
        await rig.Run("req-1");
        await rig.Cached(1);

        var again = await rig.Run("req-again", T0 + 60_000);
        var callerArm = await rig.Run("req-caller-arm", assignment: ["exp_feed/control"]);
        Assert.Equal(1, rig.Probe.Runs);
        Assert.Equal([("req-1", T0)], Origins(again));
        Assert.Equal([("req-1", T0)], Origins(callerArm));

        // Another user: the called pipeline declares user_id and gets the request's.
        var otherUser = await rig.Run("req-user", inputs: new RequestInputs().Set("user_id", 1002));
        Assert.Equal(2, rig.Probe.Runs);
        Assert.Equal([("req-user", T0)], Origins(otherUser));
        await rig.Cached(2);

        // An arm of the called pipeline: its result is its own, and the next request in the arm gets it.
        var inArm = await rig.Run("req-arm", assignment: ["exp_promoted/few"]);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.InRange(Rows(inArm).Count, 1, 2);
        await rig.Cached(3);
        var inArmAgain = await rig.Run("req-arm-again", assignment: ["exp_promoted/few"]);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(Rows(inArm), Rows(inArmAgain));
        Assert.Equal(new[] { "exp_promoted/few" }, Assert.Single(inArmAgain.Calls!).Arms);

        // The first result is still there for a request outside the arm.
        Assert.Equal([("req-1", T0)], Origins(await rig.Run("req-last")));
        Assert.Equal(3, rig.Probe.Runs);
    }

    /// <summary>
    /// A request served a cached result was exposed to the arms that computed it: its result has them, and it logs that
    /// exposure with where the result comes from. Nothing else of the called pipeline is logged for it: its taps wrote
    /// under the request whose run computed the rows.
    /// </summary>
    [Fact]
    public async Task AHit_ExposesTheRequestToTheArmsThatComputedTheResult_AndLogsOnlyThat_WithWhereItComesFrom()
    {
        await using var rig = new Rig(Host());
        var arms = new[] { "exp_feed/control", "exp_promoted/few" };

        var first = await rig.Run("req-1", T0, arms);
        await rig.Cached(1);
        var second = await rig.Run("req-2", T0 + 5_000, arms);
        await rig.Logs.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, rig.Probe.Runs);
        Assert.Equal(arms, second.Experiments.Assigned);
        Assert.Contains("exp_promoted", second.Experiments.Triggered);
        Assert.Equal(first.Experiments.Triggered, second.Experiments.Triggered);
        Assert.Empty(second.Experiments.Ignored);

        var logged = rig.Sink.Snapshot().Where(b => b.PipelineId == "promoted").ToList();
        Assert.Equal(new[] { "$experiments", "bids" }, logged.Where(b => b.RequestId == "req-1").Select(b => b.TapId).Order(StringComparer.Ordinal));
        var ran = logged.Single(b => b.RequestId == "req-1" && b.TapId == "$experiments");
        var exposure = Assert.Single(logged, b => b.RequestId == "req-2");
        Assert.Equal(("$experiments", "feed/promoted#0", "req-1", T0, T0 + 5_000),
            (exposure.TapId, exposure.CalledBy, exposure.CachedFromRequestId, exposure.CachedFromTimeMs, exposure.TimeMs));
        Assert.Null(ran.CachedFromRequestId);
        Assert.Equal((ran.ManifestId, ran.CalledBy), (exposure.ManifestId, exposure.CalledBy));
        Assert.Equal(ran.AssignedArms, exposure.AssignedArms);
        Assert.Equal(ran.TriggeredExperiments, exposure.TriggeredExperiments);
        Assert.Contains($$"""
            "cached_from":{"request_id":"req-1","time_ms":{{T0}}}
            """.Trim(), LogBatchJson.Encode(exposure, indented: false));
        Assert.DoesNotContain("cached_from", LogBatchJson.Encode(ran, indented: false));
    }

    /// <summary>
    /// Past its <c>ttl_ms</c> a result is computed anew; when that fails, the old one serves for <c>stale_if_error_ms</c>
    /// more and the node is degraded; after that the call fails as the pipeline did.
    /// </summary>
    [Fact]
    public async Task WhenThePipelineFails_AResultPastItsTtl_IsServedWithinStaleIfError_AndTheNodeIsDegraded()
    {
        await using var rig = new Rig(Host("""{ "ttl_ms": 60000, "stale_if_error_ms": 600000 }"""));
        var first = await rig.Run("req-1", T0);
        await rig.Cached(1);

        rig.Clock.Advance(70_000); // past the ttl and its jitter
        rig.Probe.Fail = true;
        var stale = await rig.Run("req-2", T0 + 70_000);

        Assert.True(stale.Succeeded, stale.Error);
        Assert.Equal(2, rig.Probe.Runs); // it ran, and failed
        Assert.Equal(Rows(first), Rows(stale));
        Assert.Equal([("req-1", T0)], Origins(stale));
        Assert.Equal("degraded", NodeOutcomeNames.Name(stale.Trace!.Single(n => n.NodeId == "promoted").Kind));
        var call = Assert.Single(stale.Calls!);
        Assert.Equal(new CachedFrom("req-1", T0, Stale: true), call.Cached);
        Assert.Contains("probe outage", call.Error);
        Assert.Contains(call.Trace, n => n.NodeId == "probe"); // the run that failed
        Assert.Contains("\"stale\":true", Json(stale));

        rig.Clock.Advance(600_000); // past the stale window too
        var failed = await rig.Run("req-3", T0 + 670_000);
        Assert.False(failed.Succeeded);
        Assert.Contains("probe outage", failed.Error);

        rig.Probe.Fail = false;
        var fresh = await rig.Run("req-4", T0 + 670_000);
        Assert.True(fresh.Succeeded, fresh.Error);
        Assert.Equal(4, rig.Probe.Runs);
        Assert.Equal([("req-4", T0 + 670_000)], Origins(fresh));
        Assert.Null(Assert.Single(fresh.Calls!).Cached);
    }

    /// <summary>
    /// What is kept is what a run computed whole, for a request whose runs write their logs: not a degraded result, which
    /// would be served for the whole ttl, nor one of a request that captures its logs or runs a configuration of its own,
    /// under which nothing the run logged would be found.
    /// </summary>
    [Fact]
    public async Task OnlyAWholeRunOfARequestThatWritesItsLogs_IsKept()
    {
        await using var rig = new Rig(Host());

        rig.Probe.Degrade = true;
        Assert.True((await rig.Run("req-degraded")).Succeeded);
        rig.Probe.Degrade = false;
        Assert.True((await rig.Run("req-captured", debug: new DebugOptions { Trace = true, CaptureLogs = true })).Succeeded);
        Assert.True((await rig.Run("req-forced", debug: new DebugOptions { Trace = true, ForceArms = ["exp_feed/control"] })).Succeeded);
        Assert.True((await rig.Run("req-kept")).Succeeded);
        await rig.Cached(1);
        var served = await rig.Run("req-served");

        // Writes land in the order they were handed over, and one of the same time does not replace a record: had an
        // earlier request's result been kept, it would be the one served.
        Assert.Equal(4, rig.Probe.Runs);
        Assert.Equal(new CachedFrom("req-kept", T0), Assert.Single(served.Calls!).Cached);

        // A request that captures its logs reads the cache as any other, and gets the exposure it would have written.
        var captured = await rig.Run("req-captured-2", assignment: [], debug: new DebugOptions { Trace = true, CaptureLogs = true });
        Assert.Equal(4, rig.Probe.Runs);
        Assert.Equal(new CachedFrom("req-kept", T0), Assert.Single(captured.Calls!).Cached);
        var exposure = Assert.Single(captured.LogBatches!, b => b.PipelineId == "promoted");
        Assert.Equal(("$experiments", "req-captured-2", "req-kept"), (exposure.TapId, exposure.RequestId, exposure.CachedFromRequestId));

        // One that runs a configuration of its own does neither: what it computes is not what is served.
        var patch = new System.Text.Json.Nodes.JsonObject { ["params"] = new System.Text.Json.Nodes.JsonObject { ["promoted_k"] = 9 } };
        var overridden = await rig.Run("req-override", debug: new DebugOptions { Trace = true, ConfigOverride = patch });
        Assert.True(overridden.Succeeded, overridden.Error);
        Assert.Equal(5, rig.Probe.Runs);
        Assert.Null(Assert.Single(overridden.Calls!).Cached);
    }

    /// <summary>
    /// Without a <c>version</c> the cache follows what the called pipeline would run: a release that changes it computes
    /// anew, one that changes an experiment only for the requests in it, and one of the pipeline that calls for nobody.
    /// </summary>
    [Fact]
    public async Task AReleaseOfTheCalledPipeline_ComputesAnew_ForTheRequestsItChangesTheResultOf()
    {
        var host = Host();
        await using var rig = new Rig(host);
        var arm = new[] { "exp_promoted/few" };
        await rig.Run("req-1");
        await rig.Cached(1);
        await rig.Run("req-arm", assignment: arm);
        await rig.Cached(2);
        Assert.Equal(2, rig.Probe.Runs);

        // The pipeline that calls is released with another limit of its own: the results are still there, in the table
        // its new plan took over.
        Edit(host, "pipelines/feed.jsonc", "\"promoted_k\": 10", "\"promoted_k\": 9");
        rig.Load("feed");
        Assert.Equal([("req-1", T0)], Origins(await rig.Run("req-2")));
        Assert.Equal(2, rig.Probe.Runs);

        // An experiment of the called pipeline changes: only the requests in it compute anew.
        Edit(host, "experiments/exp_promoted.jsonc", "\"top_k\": 2", "\"top_k\": 3");
        rig.Load("promoted");
        Assert.Equal([("req-1", T0)], Origins(await rig.Run("req-3")));
        Assert.Equal(2, rig.Probe.Runs);
        var inArm = await rig.Run("req-arm-2", assignment: arm);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal([("req-arm-2", T0)], Origins(inArm));
        await rig.Cached(3);

        // The called pipeline itself changes: everyone computes anew.
        Edit(host, "pipelines/promoted.jsonc", "\"top_k\": 5", "\"top_k\": 4");
        rig.Load("promoted");
        var after = await rig.Run("req-4");
        Assert.Equal(4, rig.Probe.Runs);
        Assert.Equal([("req-4", T0)], Origins(after));
    }

    /// <summary>
    /// With a <c>version</c> the results are kept across releases of the called pipeline, until the name changes: for
    /// results that cost much to compute again and change little with a release.
    /// </summary>
    [Fact]
    public async Task ACacheWithAVersion_KeepsItsResultsAcrossReleasesOfTheCalledPipeline_UntilTheVersionChanges()
    {
        var host = Host("""{ "ttl_ms": 3600000, "version": "1" }""");
        await using var rig = new Rig(host);
        var first = await rig.Run("req-1");
        await rig.Cached(1);

        Edit(host, "pipelines/promoted.jsonc", "\"top_k\": 5", "\"top_k\": 4");
        rig.Load("promoted");
        var kept = await rig.Run("req-2");
        Assert.Equal(1, rig.Probe.Runs);
        Assert.Equal(Rows(first), Rows(kept));

        File.WriteAllText(Path.Combine(host.Root, "pipelines", "feed.jsonc"), FeedWith("""{ "ttl_ms": 3600000, "version": "2" }"""));
        rig.Load("feed");
        var anew = await rig.Run("req-3");
        Assert.Equal(2, rig.Probe.Runs);
        Assert.Equal([("req-3", T0)], Origins(anew));
        Assert.True(Rows(anew).Count <= 4);
    }

    /// <summary>
    /// The distributed tier is every instance's: a result one instance computed answers the call on another, which says
    /// where it comes from all the same. With no in-process tier bound, nothing is kept in process.
    /// </summary>
    [Theory]
    [InlineData("""{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } } }""", false)]
    [InlineData("""{ "cache": { "memory": { "max_bytes": "1MiB" }, "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } } }""", true)]
    public async Task AResultOneInstanceComputed_AnswersTheCallOnAnother_ThroughTheDistributedTier(string bound, bool memory)
    {
        var host = Host(bound: bound);
        var stores = new SharedStores();
        await using var computing = new Rig(host, stores: stores);
        await using var other = new Rig(host, stores: stores);

        var first = await computing.Run("req-1");
        await CacheEngineTests.Eventually(stores.HoldsEveryKeyRead, () => "the result in the distributed tier");
        var served = await other.Run("req-2", T0 + 5_000);

        Assert.True(first.Succeeded && served.Succeeded, first.Error ?? served.Error);
        Assert.Equal((1, 0), (computing.Probe.Runs, other.Probe.Runs));
        Assert.Equal(Rows(first), Rows(served));
        Assert.Equal([("req-1", T0)], Origins(served));
        Assert.Equal(new CachedFrom("req-1", T0), Assert.Single(served.Calls!).Cached);

        // The instance that read it keeps a copy in process, when it has that tier.
        if (memory)
        {
            await other.Cached(1);
        }
        else
        {
            Assert.Equal(0, other.Results().Pin().RecordCount);
            Assert.Equal(0, computing.Results().Pin().RecordCount);
        }
    }

    /// <summary>
    /// A recorded request whose call the cache answered replays without the cache and without the pipeline: the recording
    /// has the result with where it comes from, a stale one included.
    /// </summary>
    [Fact]
    public async Task ARecordedRequestTheCacheAnswered_ReplaysIdentically_OnAnEngineWhoseCacheIsEmpty()
    {
        var host = Host("""{ "ttl_ms": 60000, "stale_if_error_ms": 600000 }""");
        await using var rig = new Rig(host);
        await using var fresh = new Rig(host);
        var ct = TestContext.Current.CancellationToken;
        await rig.Run("req-1");
        await rig.Cached(1);

        var hit = await rig.Run("req-2", T0 + 5_000, debug: new DebugOptions { Record = true, Trace = true });
        rig.Clock.Advance(70_000);
        rig.Probe.Fail = true;
        var stale = await rig.Run("req-3", T0 + 70_000, debug: new DebugOptions { Record = true, Trace = true });

        foreach (var recorded in new[] { hit, stale })
        {
            Assert.True(recorded.Succeeded, recorded.Error);
            var outcome = await fresh.Engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!)), cancellationToken: ct);
            Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
            Assert.Empty(outcome.Report.Gaps);
            Assert.Equal([("req-1", T0)], Origins(outcome.Result));
            Assert.All(outcome.Report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: recorded {n.Recorded}, replayed {n.Replayed}"));
        }

        Assert.Equal(0, fresh.Probe.Runs);
        Assert.Equal(0, fresh.Results().Pin().RecordCount);
        Assert.Equal("degraded", Assert.Single(stale.Recording!.Runs[0].Nodes, n => n.Node == "promoted").Outcome);
    }

    /// <summary>
    /// What a cached result is of includes the pipelines its run called: the request's arms in them are part of what it
    /// is found by, a hit exposes the request to them and logs each with the calls that led to it, and the platform is
    /// asked for a pipeline's arms once a request, whether the run or the lookup needs them.
    /// </summary>
    [Fact]
    public async Task AHitOfAPipelineThatCallsAnother_ExposesTheRequestToBothRuns_AndTheArmsOfBothAreWhatItIsFoundBy()
    {
        var host = Host(more: root =>
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "promoted.jsonc"), Called.Replace("\"nodes\": {",
                "\"nodes\": {\n \"more\": { \"order\": 150, \"op\": \"PipelineSource@1\", \"config\": { \"pipeline\": \"other\", \"limit_param\": \"top_k\" } },", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "pipelines", "other.jsonc"), PipelineCallEndToEndTests.Promoted
                .Replace("\"pipeline_id\": \"promoted\"", "\"pipeline_id\": \"other\"", StringComparison.Ordinal)
                .Replace("\"slots\": { \"type\": \"int64\" }", "\"slots\": { \"type\": \"int64?\" }", StringComparison.Ordinal)
                .Replace("+ ctx.request.slots", "+ (ctx.request.slots ?? 0)", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "releases", "other.local.jsonc"), PipelineCallEndToEndTests.Releases.Replace("PIPELINE", "other", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "experiments", "exp_other.jsonc"), PipelineCallEndToEndTests.Experiments.Replace("PIPELINE", "other", StringComparison.Ordinal));
        });
        var arms = new[] { "exp_other/few", "exp_promoted/control" };
        var asked = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var provider = new Asked((pipeline, request) =>
        {
            asked.Enqueue(request + ":" + pipeline);
            return request == "req-other-arm" ? ["exp_other/control", "exp_promoted/control"] : arms;
        });
        await using var rig = new Rig(host, provider: provider, pipelines: ["other", "promoted", "feed"]);

        var first = await rig.Run("req-1", T0, ask: true);
        await rig.Cached(1);
        var second = await rig.Run("req-2", T0 + 5_000, ask: true);
        await rig.Logs.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(1, rig.Probe.Runs);
        Assert.Equal(arms, first.Experiments.Assigned);
        Assert.Equal(arms, second.Experiments.Assigned);
        Assert.Equal(first.Experiments.Triggered, second.Experiments.Triggered);
        Assert.Equal(2, first.Calls!.Count);
        Assert.Equal(new CachedFrom("req-1", T0), Assert.Single(second.Calls!).Cached);

        // Each run that computed the result is logged for the request served it, where the run was.
        var exposures = rig.Sink.Snapshot().Where(b => b.RequestId == "req-2" && b.PipelineId != "feed").OrderBy(b => b.PipelineId, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { ("other", "feed/promoted#0 > promoted/more#0", "exp_other/few"), ("promoted", "feed/promoted#0", "exp_promoted/control") },
            exposures.Select(b => (b.PipelineId, b.CalledBy!, Assert.Single(b.AssignedArms))));
        Assert.All(exposures, b => Assert.Equal(("$experiments", "req-1", T0), (b.TapId, b.CachedFromRequestId, b.CachedFromTimeMs)));
        var ran = rig.Sink.Snapshot().Where(b => b.RequestId == "req-1" && b.TapId == "$experiments" && b.PipelineId != "feed").OrderBy(b => b.PipelineId, StringComparer.Ordinal).ToList();
        Assert.Equal(ran.Select(b => (b.PipelineId, b.CalledBy, b.ManifestId)), exposures.Select(b => (b.PipelineId, b.CalledBy, b.ManifestId)));

        // The platform was asked for each pipeline's arms once a request.
        Assert.Equal(new[] { "req-1:feed", "req-1:other", "req-1:promoted", "req-2:feed", "req-2:other", "req-2:promoted" }, asked.Order(StringComparer.Ordinal));

        // In another arm of the pipeline the called one calls, the result is another.
        var otherArm = await rig.Run("req-other-arm", ask: true);
        Assert.Equal(2, rig.Probe.Runs);
        Assert.Contains("exp_other/control", otherArm.Experiments.Assigned);
    }

    /// <summary><c>PipelineColumns@1</c> caches each row's call by what the row passes, and says where each row's lists come from.</summary>
    [Fact]
    public async Task ARowsCall_IsCachedByItsInputs_AndTheRowSaysWhichRequestComputedItsLists()
    {
        const string Page = """
            {
              "pipeline_id": "page",
              "entity_key_type": "int64",
              "request_schema": { "user_id": { "type": "int64?" }, "country": { "type": "string?" } },
              "columns": { "games": { "type": "list<int64>" }, "bids": { "type": "list<float64>?" } },
              "param_schema": { "recall_quotas": { "type": "map<string,int64>", "min": 0, "max": 1000 }, "top_k": { "type": "int64", "min": 1, "max": 100 } },
              "params": { "recall_quotas": { "popular": 3 }, "top_k": 50 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
                "content": { "order": 200, "op": "PipelineColumns@1",
                  "config": { "pipeline": "promoted", "inputs": { "slots": "QUOTIENT" }, "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                              "cache": { "ttl_ms": 3600000 }, "computed_by": { "request_id": "games.from", "time_ms": "games.at" } } }
              },
              "output": { "limit_param": "top_k", "columns": { "games": "col.games", "bids": "col.bids", "from": "col.games.from", "at": "col.games.at" } }
            }
            """;
        var host = Host(more: root =>
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "page.jsonc"), Page.Replace("QUOTIENT", "col.entity_id", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "releases", "page.local.jsonc"), """{ "pipeline_id": "page", "environment": "local", "catalog": "home_feed", "bindings": "local" }""");
        });
        await using var rig = new Rig(host, pipelines: ["promoted", "page"]);

        var first = await rig.Run("req-1", T0, pipeline: "page");
        await rig.Cached(3, holder: "page");
        var second = await rig.Run("req-2", T0 + 5_000, pipeline: "page");

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(3, first.Items.Count);
        Assert.Equal(3, rig.Probe.Runs); // a run for each row, each with its own input
        Assert.Equal(first.Items.Select(i => (i.Id, string.Join(",", i.Columns["games"].AsInt64List().ToArray()), string.Join(",", i.Columns["bids"].AsFloat64List().ToArray()))),
            second.Items.Select(i => (i.Id, string.Join(",", i.Columns["games"].AsInt64List().ToArray()), string.Join(",", i.Columns["bids"].AsFloat64List().ToArray()))));
        Assert.All(first.Items.Concat(second.Items), i => Assert.Equal(("req-1", T0), (i.Columns["from"].AsString(), i.Columns["at"].AsInt64())));
        Assert.All(second.Calls!, c => Assert.Equal(new CachedFrom("req-1", T0), c.Cached));

        // The rows now pass one input between them: one result answers them all, computed once more.
        Edit(host, "pipelines/page.jsonc", "col.entity_id", "col.entity_id * 0 + 7");
        rig.Load("page");
        var shared = await rig.Run("req-3", T0 + 9_000, pipeline: "page");
        Assert.True(shared.Succeeded, shared.Error);
        await rig.Cached(4, holder: "page");
        var runs = rig.Probe.Runs;
        var sharedAgain = await rig.Run("req-4", T0 + 9_500, pipeline: "page");
        Assert.All(sharedAgain.Items, i => Assert.Equal("req-3", i.Columns["from"].AsString()));
        Assert.Equal(runs, rig.Probe.Runs);
    }

    /// <summary>
    /// A cache that is wrong is an error where it is written: the node's times and version at the node, the tiers at the
    /// binding; and a node that asks for a cache the environment binds no tier for does not compile.
    /// </summary>
    [Theory]
    [InlineData(Hour, null, "PIPELINE_CALL_CACHE_UNBOUND", "/nodes/promoted/config/cache")]
    [InlineData("{ }", Memory, "CACHE_TTL", "/nodes/promoted/config/cache")]
    [InlineData("""{ "ttl_ms": 0 }""", Memory, "CACHE_TTL", "/nodes/promoted/config/cache/ttl_ms")]
    [InlineData("""{ "ttl_ms": 1000, "stale_if_error_ms": -1 }""", Memory, "CACHE_STALE_IF_ERROR", "/nodes/promoted/config/cache/stale_if_error_ms")]
    [InlineData("""{ "ttl_ms": 1000, "version": "a b" }""", Memory, "CACHE_VERSION", "/nodes/promoted/config/cache/version")]
    [InlineData(Hour, """{ "cache": { } }""", "CACHE_TIERS", "/pipelines/promoted/cache")]
    [InlineData(Hour, """{ "cache": { "memory": { } } }""", "CACHE_MAX_BYTES", "/pipelines/promoted/cache/memory")]
    [InlineData(Hour, """{ "cache": { "distributed": { "store": "shared_memory@1" } } }""", "DISTRIBUTED_CACHE_CONFIG", "/pipelines/promoted/cache/distributed")]
    public void AWrongCache_IsAnErrorWhereItIsWritten(string cache, string? bound, string code, string pointer)
    {
        var host = Host(cache, bound);
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new Probe()), Bindings = PlanEnvironment.DefaultBindings().Add(new SharedStores()),
        });

        var load = engine.Load(Compile(host.Root, "feed"));

        Assert.False(load.Applied);
        var error = Assert.Single(load.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal((code, pointer), (error.Code, error.Pointer));
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    /// <summary>The columns that say where a result comes from are a string and an integer, declared or not; any other type is an error at the column.</summary>
    [Theory]
    [InlineData("""{ }""", "", "COMPUTED_BY_CONFIG", "/nodes/promoted/config/computed_by")]
    [InlineData(ComputedBy, """, "promo.from": { "type": "int64?" }""", "COLUMN_TYPE_MISMATCH", "/nodes/promoted/config/computed_by/request_id")]
    [InlineData(ComputedBy, """, "promo.at": { "type": "float64?" }""", "COLUMN_TYPE_MISMATCH", "/nodes/promoted/config/computed_by/time_ms")]
    public void WrongComputedByColumns_AreAnErrorAtTheColumn(string computedBy, string declared, string code, string pointer)
    {
        var host = Host(computedBy: computedBy, declared: declared);
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new Probe()) });

        var load = engine.Load(Compile(host.Root, "feed"));

        Assert.False(load.Applied);
        Assert.Contains(load.Diagnostics, d => d.Code == code && d.Pointer == pointer);
    }

    private static void Edit(HomeFeedTestHost host, string file, string from, string to)
    {
        var path = Path.Combine(host.Root, file);
        var text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    /// <summary>A provider that answers with what the test says, and notes each question.</summary>
    private sealed class Asked(Func<string, string, IReadOnlyList<string>> answer) : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken) =>
            new(answer(context.PipelineId, context.RequestId));
    }

    /// <summary>A wall clock the test moves.</summary>
    private sealed class MovableClock : TimeProvider
    {
        private long _timeMs = 1_720_000_000_000L;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _timeMs));

        public void Advance(long milliseconds) => Interlocked.Add(ref _timeMs, milliseconds);
    }

    /// <summary><c>Probe@1</c>: counts its runs, and fails or degrades its node when told.</summary>
    private sealed class Probe : IOperatorFactory
    {
        private int _runs;
        private volatile bool _fail;
        private volatile bool _degrade;

        public int Runs => Volatile.Read(ref _runs);

        public bool Fail
        {
            get => _fail;
            set => _fail = value;
        }

        public bool Degrade
        {
            get => _degrade;
            set => _degrade = value;
        }

        public OperatorDescriptor Descriptor { get; } = new("Probe", 1, Effects.None, typeof(ProbeConfig), "Counts its runs, and fails or degrades when told.");

        public Operator? Compile(OperatorCompileContext context) => context.BindConfig<ProbeConfig>() is null ? null : new Node(this);

        private sealed class Node(Probe probe) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                Interlocked.Increment(ref probe._runs);
                if (probe.Fail)
                {
                    context.Fail("probe outage");
                }
                else if (probe.Degrade)
                {
                    context.ReportDegraded("probe degraded");
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ProbeConfig;
}
