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
/// Write-backs run in the background, so the tests wait for the table to hold a record, never for a time.
/// </summary>
public sealed class PipelineCallCacheEndToEndTests
{
    internal const long T0 = 1_767_225_600_000;

    private const string Memory = """{ "cache": { "memory": { "max_bytes": "1MiB" } } }""";

    private const string Hour = """{ "ttl_ms": 3600000 }""";

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
                      "columns": { "promoted": "col.is_promoted ?? false", "promo_bid": "col.promo.bid", "from": "col.promo.from", "at": "col.promo.at", "by": "col.promo.by" } }
        }
        """;

    private static readonly string Called = PipelineCallEndToEndTests.Promoted
        .Replace("\"nodes\": {", "\"nodes\": {\n \"probe\": { \"order\": 50, \"op\": \"Probe@1\" },", StringComparison.Ordinal)
        .Replace("\"output\": {", """
            "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
            "log_taps": { "bids": { "group": "all", "delivery": "durable", "attach": { "output": true }, "columns": { "bid": "col.bid" } } },
            "output": {
            """, StringComparison.Ordinal);

    private const string ComputedBy = """{ "request_id": "promo.from", "time_ms": "promo.at", "called_by": "promo.by" }""";

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

    internal sealed class Rig : IAsyncDisposable
    {
        public Rig(
            HomeFeedTestHost host, Probe? probe = null, SharedStores? stores = null, IAssignmentProvider? provider = null, Action<BindingRegistry>? bind = null,
            IOperatorFactory[]? operators = null, bool alone = false, params string[] pipelines)
        {
            Host = host;
            Probe = probe ?? new Probe();
            var bindings = PlanEnvironment.DefaultBindings();
            if (stores is not null)
            {
                bindings.Add(stores);
            }

            bind?.Invoke(bindings);

            Logs = new LogPipeline(Sink);
            var registry = OperatorRegistry.CreateDefault().Add(Probe);
            foreach (var factory in operators ?? [])
            {
                registry.Add(factory);
            }

            Engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = registry, Bindings = bindings, Clock = Clock }, provider, Logs);
            foreach (var id in pipelines.Length == 0 ? ["promoted", "feed"] : pipelines)
            {
                if (alone)
                {
                    LoadAlone(id);
                }
                else
                {
                    Load(id);
                }
            }
        }

        public HomeFeedTestHost Host { get; }

        public RecEngine Engine { get; }

        public Probe Probe { get; }

        public MovableClock Clock { get; } = new();

        public InMemoryLogSink Sink { get; } = new();

        public LogPipeline Logs { get; }

        public void Load(string pipelineId)
        {
            var load = Engine.Load(Compile(Host.Root, pipelineId));
            Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        }

        /// <summary>Loads the pipeline compiled without the others, as a release is: what it calls is checked when it calls.</summary>
        private void LoadAlone(string pipelineId)
        {
            var load = Engine.Load(new PlanCompileRequest
            {
                Pipeline = Host.Workspace.Pipelines[pipelineId].Value, Catalog = Host.Request.Catalog, Bindings = Host.Request.Bindings, Tables = Host.Request.Tables,
                Policy = Host.Request.Policy,
            });
            Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        }

        /// <param name="ask">The platform assigned nothing with the request: the engine's provider is asked.</param>
        public ValueTask<RecResult> Run(
            string requestId, long timeMs = T0, IReadOnlyList<string>? assignment = null, RequestInputs? inputs = null, DebugOptions? debug = null, string pipeline = "feed",
            bool ask = false, RequestGroup? group = null) =>
            Engine.ExecuteAsync(new RecRequest
            {
                Group = group,
                PipelineId = pipeline,
                RequestId = requestId,
                Inputs = inputs ?? new RequestInputs().Set("user_id", 1001),
                Assignment = ask ? null : assignment ?? [],
                Deadline = HomeFeedTestHost.Deadline,
                TimeMs = timeMs,
                Debug = debug ?? new DebugOptions { Trace = true },
            }, TestContext.Current.CancellationToken);

        public LocalFeatureTable Results(string pipelineId = "promoted", string holder = "feed")
        {
            Assert.True(Engine.TryGetPipeline(holder, out var pipeline));
            return Assert.Single(pipeline.Bindings.Tables, t => t.Schema.Source == pipelineId && t.Schema.Entity == "results");
        }

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

    private static List<(long Id, double Bid)> Rows(RecResult result) => Promoted(result).Select(i => (i.Id.Int64Value, i.Columns["promo_bid"].AsFloat64())).ToList();

    private static List<(string From, long At)> Origins(RecResult result) =>
        Promoted(result).Select(i => (i.Columns["from"].AsString(), i.Columns["at"].AsInt64())).Distinct().ToList();

    private static List<string> Calls(RecResult result) => Promoted(result).Select(i => i.Columns["by"].AsString()).Distinct().ToList();

    private static HomeFeedTestHost Nested(string cache = Hour, string? otherCache = null, string when = "") =>
        Host(cache, more: root => WriteNested(root, otherCache, when));

    private static void WriteNested(string root, string? otherCache, string when = "")
    {
        File.WriteAllText(Path.Combine(root, "pipelines", "promoted.jsonc"), Called
            .Replace("\"columns\": { \"bid\": { \"type\": \"float32\", \"default\": 0.0 } }",
                "\"columns\": { \"bid\": { \"type\": \"float32\", \"default\": 0.0 }, \"other.bid\": { \"type\": \"float32?\" } }", StringComparison.Ordinal)
            .Replace("\"popular\": 20", "\"popular\": 0", StringComparison.Ordinal)
            .Replace("\"nodes\": {", "\"nodes\": {\n \"more\": { \"order\": 150, \"op\": \"PipelineSource@1\"," + when
                + " \"config\": { \"pipeline\": \"other\", \"limit_param\": \"top_k\", \"columns\": { \"other.bid\": \"bid\" }"
                + (otherCache is null ? string.Empty : ", \"cache\": " + otherCache) + " } },", StringComparison.Ordinal)
            .Replace("+ ctx.request.slots", "+ ctx.request.slots + (col.other.bid ?? 0.0)", StringComparison.Ordinal));
        WriteOther(root);
        if (otherCache is not null)
        {
            SampleBindings.BindPipeline(root, "other", Memory);
        }
    }

    private static void WriteOther(string root)
    {
        File.WriteAllText(Path.Combine(root, "pipelines", "other.jsonc"), PipelineCallEndToEndTests.Promoted
            .Replace("\"pipeline_id\": \"promoted\"", "\"pipeline_id\": \"other\"", StringComparison.Ordinal)
            .Replace("\"slots\": { \"type\": \"int64\" }", "\"slots\": { \"type\": \"int64?\" }", StringComparison.Ordinal)
            .Replace("+ ctx.request.slots", OtherBids, StringComparison.Ordinal)
            .Replace("\"nodes\": {", "\"nodes\": {\n \"probe\": { \"order\": 50, \"op\": \"Probe@1\", \"config\": { \"name\": \"other\" } },", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(root, "releases", "other.local.jsonc"), PipelineCallEndToEndTests.Releases.Replace("PIPELINE", "other", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(root, "experiments", "exp_other.jsonc"), PipelineCallEndToEndTests.Experiments.Replace("PIPELINE", "other", StringComparison.Ordinal));
    }

    private const string OtherBids = "+ (ctx.request.slots ?? 0)", OtherBidsMore = "+ (ctx.request.slots ?? 0) + 100";

    private static readonly string[] NestedPipelines = ["other", "promoted", "feed"];

    /// <summary>Another call than the tests' usual one: its result is written after theirs, so once it is there, theirs would be.</summary>
    private static RequestInputs Elsewhere => new RequestInputs().Set("user_id", 1001).Set("country", "nz");

    internal static HomeFeedTestHost PageHost(string slots = "col.entity_id", string? content = null, Action<string>? more = null) => Host(more: root =>
    {
        var page = content is null ? Page : Page.Replace(PageContent, content, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(root, "pipelines", "page.jsonc"), page.Replace("QUOTIENT", slots, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(root, "releases", "page.local.jsonc"), """{ "pipeline_id": "page", "environment": "local", "catalog": "home_feed", "bindings": "local" }""");
        more?.Invoke(root);
    });

    internal const string PageContent = """
        "config": { "pipeline": "promoted", "inputs": { "slots": "QUOTIENT" }, "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                              "cache": { "ttl_ms": 3600000 }, "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
        """;

    private const string Page = $$"""
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
              {{PageContent}} }
          },
          "output": { "limit_param": "top_k", "columns": { "games": "col.games", "bids": "col.bids", "from": "col.games.from", "at": "col.games.at", "by": "col.games.by" } }
        }
        """;

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

        Assert.Equal([("req-1", T0)], Origins(first));
        Assert.Equal([("req-1", T0)], Origins(second));
        Assert.Equal(["feed/promoted#0"], Calls(first));
        Assert.Equal(["feed/promoted#0"], Calls(second));
        Assert.All(second.Items.Where(i => !i.Columns["promoted"].AsBool()), i => Assert.True(i.Columns["from"].IsMissing && i.Columns["at"].IsMissing));

        var ran = Assert.Single(first.Calls!);
        Assert.Null(ran.Cached);
        Assert.Contains(ran.Trace, n => n.NodeId == "probe");
        var served = Assert.Single(second.Calls!);
        Assert.Equal(new CachedFrom("req-1", T0, "feed/promoted#0"), served.Cached);
        Assert.True(served.Succeeded);
        Assert.Empty(served.Trace);
        Assert.Equal("succeeded", NodeOutcomeNames.Name(second.Trace!.Single(n => n.NodeId == "promoted").Kind));
        Assert.Contains($$"""
            "cached_from":{"request_id":"req-1","time_ms":{{T0}},"called_by":"feed/promoted#0"}
            """.Trim(), Json(second));
        Assert.DoesNotContain("cached_from", Json(first));

        var text = new StringWriter();
        StrataRec.CommandLine.Commands.ResultText.Write(text, second);
        Assert.Contains("  call      feed/promoted#0 -> promoted: not run, served the cached result of request req-1 (feed/promoted#0, 2026-01-01T00:00:00.000Z), ", text.ToString());
    }

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

        var otherUser = await rig.Run("req-user", inputs: new RequestInputs().Set("user_id", 1002));
        Assert.Equal(2, rig.Probe.Runs);
        Assert.Equal([("req-user", T0)], Origins(otherUser));
        await rig.Cached(2);

        var inArm = await rig.Run("req-arm", assignment: ["exp_promoted/few"]);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.InRange(Rows(inArm).Count, 1, 2);
        await rig.Cached(3);
        var inArmAgain = await rig.Run("req-arm-again", assignment: ["exp_promoted/few"]);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(Rows(inArm), Rows(inArmAgain));
        Assert.Equal(new[] { "exp_promoted/few" }, Assert.Single(inArmAgain.Calls!).Arms);

        Assert.Equal([("req-1", T0)], Origins(await rig.Run("req-last")));
        Assert.Equal(3, rig.Probe.Runs);
    }

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
            "cached_from":{"request_id":"req-1","time_ms":{{T0}},"called_by":"feed/promoted#0"}
            """.Trim(), LogBatchJson.Encode(exposure, indented: false));
        Assert.DoesNotContain("cached_from", LogBatchJson.Encode(ran, indented: false));
    }

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
        Assert.Equal(2, rig.Probe.Runs);
        Assert.Equal(Rows(first), Rows(stale));
        Assert.Equal([("req-1", T0)], Origins(stale));
        Assert.Equal("degraded", NodeOutcomeNames.Name(stale.Trace!.Single(n => n.NodeId == "promoted").Kind));
        var call = Assert.Single(stale.Calls!);
        Assert.Equal(new CachedFrom("req-1", T0, "feed/promoted#0", Stale: true), call.Cached);
        Assert.Contains("probe outage", call.Error);
        Assert.Contains(call.Trace, n => n.NodeId == "probe");
        Assert.Contains("\"stale\":true", Json(stale));

        rig.Clock.Advance(600_000);
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
        Assert.Equal(new CachedFrom("req-kept", T0, "feed/promoted#0"), Assert.Single(served.Calls!).Cached);

        var captured = await rig.Run("req-captured-2", assignment: [], debug: new DebugOptions { Trace = true, CaptureLogs = true });
        Assert.Equal(4, rig.Probe.Runs);
        Assert.Equal(new CachedFrom("req-kept", T0, "feed/promoted#0"), Assert.Single(captured.Calls!).Cached);
        var exposure = Assert.Single(captured.LogBatches!, b => b.PipelineId == "promoted");
        Assert.Equal(("$experiments", "req-captured-2", "req-kept"), (exposure.TapId, exposure.RequestId, exposure.CachedFromRequestId));

        var patch = new System.Text.Json.Nodes.JsonObject { ["params"] = new System.Text.Json.Nodes.JsonObject { ["promoted_k"] = 9 } };
        var overridden = await rig.Run("req-override", debug: new DebugOptions { Trace = true, ConfigOverride = patch });
        Assert.True(overridden.Succeeded, overridden.Error);
        Assert.Equal(5, rig.Probe.Runs);
        Assert.Null(Assert.Single(overridden.Calls!).Cached);
    }

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

        Edit(host, "pipelines/feed.jsonc", "\"promoted_k\": 10", "\"promoted_k\": 9");
        rig.Load("feed");
        Assert.Equal([("req-1", T0)], Origins(await rig.Run("req-2")));
        Assert.Equal(2, rig.Probe.Runs);

        Edit(host, "experiments/exp_promoted.jsonc", "\"top_k\": 2", "\"top_k\": 3");
        rig.Load("promoted");
        Assert.Equal([("req-1", T0)], Origins(await rig.Run("req-3")));
        Assert.Equal(2, rig.Probe.Runs);
        var inArm = await rig.Run("req-arm-2", assignment: arm);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal([("req-arm-2", T0)], Origins(inArm));
        await rig.Cached(3);

        Edit(host, "pipelines/promoted.jsonc", "\"top_k\": 5", "\"top_k\": 4");
        rig.Load("promoted");
        var after = await rig.Run("req-4");
        Assert.Equal(4, rig.Probe.Runs);
        Assert.Equal([("req-4", T0)], Origins(after));
    }

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
        Assert.Equal(new CachedFrom("req-1", T0, "feed/promoted#0"), Assert.Single(served.Calls!).Cached);

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

        foreach (var member in new[] { "time_ms", "called_by" })
        {
            var bundle = System.Text.Json.Nodes.JsonNode.Parse(ReplayBundleFile.Serialize(hit.Recording!))!;
            Assert.True(bundle["runs"]![0]!["pipeline_calls"]![0]!["cached_from"]!.AsObject().Remove(member));
            var error = Assert.Throws<StrataRec.Pipeline.Replay.ReplayBundleException>(() => ReplayBundleFile.Parse(bundle.ToJsonString()));
            Assert.Equal("/runs/0/pipeline_calls/0/cached_from", error.Pointer);
        }
    }

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
        Assert.Equal(new CachedFrom("req-1", T0, "feed/promoted#0"), Assert.Single(second.Calls!).Cached);

        var exposures = rig.Sink.Snapshot().Where(b => b.RequestId == "req-2" && b.PipelineId != "feed").OrderBy(b => b.PipelineId, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { ("other", "feed/promoted#0 > promoted/more#0", "exp_other/few"), ("promoted", "feed/promoted#0", "exp_promoted/control") },
            exposures.Select(b => (b.PipelineId, b.CalledBy!, Assert.Single(b.AssignedArms))));
        Assert.All(exposures, b => Assert.Equal(("$experiments", "req-1", T0), (b.TapId, b.CachedFromRequestId, b.CachedFromTimeMs)));
        var ran = rig.Sink.Snapshot().Where(b => b.RequestId == "req-1" && b.TapId == "$experiments" && b.PipelineId != "feed").OrderBy(b => b.PipelineId, StringComparer.Ordinal).ToList();
        Assert.Equal(ran.Select(b => (b.PipelineId, b.CalledBy, b.ManifestId)), exposures.Select(b => (b.PipelineId, b.CalledBy, b.ManifestId)));

        Assert.Equal(new[] { "req-1:feed", "req-1:other", "req-1:promoted", "req-2:feed", "req-2:other", "req-2:promoted" }, asked.Order(StringComparer.Ordinal));

        var otherArm = await rig.Run("req-other-arm", ask: true);
        Assert.Equal(2, rig.Probe.Runs);
        Assert.Contains("exp_other/control", otherArm.Experiments.Assigned);
    }

    [Fact]
    public async Task ARowsCall_IsCachedByItsInputs_AndTheRowSaysWhichRequestComputedItsLists()
    {
        var host = PageHost();
        await using var rig = new Rig(host, pipelines: ["promoted", "page"]);

        var first = await rig.Run("req-1", T0, pipeline: "page");
        await rig.Cached(3, holder: "page");
        var second = await rig.Run("req-2", T0 + 5_000, pipeline: "page");

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(3, first.Items.Count);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(first.Items.Select(i => (i.Id, string.Join(",", i.Columns["games"].AsInt64List().ToArray()), string.Join(",", i.Columns["bids"].AsFloat64List().ToArray()))),
            second.Items.Select(i => (i.Id, string.Join(",", i.Columns["games"].AsInt64List().ToArray()), string.Join(",", i.Columns["bids"].AsFloat64List().ToArray()))));
        Assert.All(first.Items.Concat(second.Items), i => Assert.Equal(("req-1", T0), (i.Columns["from"].AsString(), i.Columns["at"].AsInt64())));
        Assert.All(second.Calls!, c => Assert.Equal(new CachedFrom("req-1", T0, c.CalledBy), c.Cached));

        // The request's id is the same for its three runs of the pipeline: the call tells which of them computed a row.
        Assert.Equal(["page/content#0", "page/content#1", "page/content#2"], first.Items.Select(i => i.Columns["by"].AsString()));
        Assert.Equal(first.Items.Select(i => i.Columns["by"].AsString()), second.Items.Select(i => i.Columns["by"].AsString()));

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

    [Fact]
    public async Task AResultComputedWhileAPipelineUnderItWasReplaced_IsNotKept()
    {
        var host = Nested();
        await using var rig = new Rig(host, pipelines: NestedPipelines);
        rig.Probe.OnRun = () =>
        {
            rig.Probe.OnRun = null; // once: promoted runs, and has not called other yet
            Edit(host, "pipelines/other.jsonc", OtherBids, OtherBidsMore);
            rig.Load("other");
        };

        var during = await rig.Run("req-during");
        Edit(host, "pipelines/other.jsonc", OtherBidsMore, OtherBids);
        rig.Load("other"); // rolled back: a call's key is again what it was when the run began
        var elsewhere = await rig.Run("req-elsewhere", inputs: Elsewhere);
        await CacheEngineTests.Eventually(() => rig.Results().Pin().RecordCount >= 1, () => "the result of req-elsewhere");
        var after = await rig.Run("req-after");

        Assert.True(during.Succeeded && elsewhere.Succeeded && after.Succeeded, during.Error ?? elsewhere.Error ?? after.Error);
        Assert.Null(Assert.Single(after.Calls!, c => c.PipelineId == "promoted").Cached);
        Assert.NotEqual(Rows(during), Rows(after));
    }

    [Fact]
    public async Task AResultComputedWhileAPipelineUnderItCameToBeServed_IsNotKept()
    {
        var host = Nested();
        SampleBindings.BindPipeline(host.Root, "promoted", """{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } } }""");
        var stores = new SharedStores();
        string[] before = ["promoted", "feed"];
        await using var rolling = new Rig(host, stores: stores, pipelines: before);
        await using var behind = new Rig(host, stores: stores, pipelines: before);
        rolling.Probe.OnRun = () =>
        {
            rolling.Probe.OnRun = null; // once: promoted runs, and has not called other yet
            rolling.Load("other");
        };

        var during = await rolling.Run("req-during");
        var elsewhere = await rolling.Run("req-elsewhere", inputs: Elsewhere);
        await CacheEngineTests.Eventually(() => stores.Records >= 1, () => "the result of req-elsewhere in the store");
        var after = await behind.Run("req-after");

        Assert.True(during.Succeeded && elsewhere.Succeeded, during.Error ?? elsewhere.Error);
        Assert.NotEmpty(Rows(during));
        Assert.Equal(1, behind.Probe.Runs);
        Assert.DoesNotContain(after.Calls ?? [], c => c.Cached is not null);
        Assert.Empty(Rows(after));
    }

    [Theory]
    [InlineData("ctx.user.ctr_30d > 0.03", "", "failed", "feature fetch failed: user store outage", false)]
    [InlineData("ctx.user.ctr_30d > 0.03", " \"on_failure\": \"skip\",", "failed", "feature fetch failed: user store outage (skipped)", true)]
    [InlineData("(ctx.request.user_id ?? 0) < 0 && (ctx.user.w ?? 0.0) > 0.0", "", "skipped_when", "when is false, on features that failed: user store outage", true)]
    public async Task ANodeWhoseWhenReadsAFeatureThatFailed_FailsByItsOwnFill_OrIsDecidedWhereTheFeatureCanBeMissing(
        string when, string policy, string outcome, string detail, bool succeeds)
    {
        var connectors = new RequestReadsEndToEndTests.CountedConnectors { Fail = true };
        var host = PageHost(more: root =>
        {
            ReplayTests.Edit(root, "catalog/home_feed.jsonc", "\"user.tenure_days\":",
                "\"user.w\": { \"type\": \"float32\", \"source\": \"user_store\", \"field\": \"u_w\" },\n    \"user.tenure_days\":"); // no default: it can be missing
            ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"recall\": { \"order\": 100, \"op\": \"MultiSourceRecall@1\",",
                $"\"recall\": {{ \"order\": 100, \"op\": \"MultiSourceRecall@1\", \"when\": \"{when}\",{policy}");
            SampleBindings.Rebind(root, "user_store", $$"""
                { "connector": "{{RequestReadsEndToEndTests.CountedConnectors.TypeName}}", "data_file": "fixtures/users.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}} }
                """);
        });
        await using var rig = new Rig(host, bind: bindings => bindings.Add(connectors), pipelines: ["promoted", "page"]);

        var outage = await rig.Run("req-1", pipeline: "page");
        connectors.Fail = false;
        var elsewhere = await rig.Run("req-elsewhere", inputs: Elsewhere, pipeline: "page");
        await CacheEngineTests.Eventually(() => rig.Results(holder: "page").Pin().RecordCount >= 3, () => "the results of req-elsewhere");
        var runs = rig.Probe.Runs;
        var after = await rig.Run("req-2", T0 + 5_000, pipeline: "page");

        Assert.True(elsewhere.Succeeded && after.Succeeded, elsewhere.Error ?? after.Error);
        Assert.Equal(3, outage.Calls!.Count);
        Assert.All(outage.Calls, c =>
        {
            var recall = Assert.Single(c.Trace, n => n.NodeId == "recall");
            Assert.Equal((outcome, detail), (NodeOutcomeNames.Name(recall.Kind), recall.Detail));
            Assert.Equal(succeeds, c.Succeeded);
        });
        Assert.Equal(succeeds, outage.Succeeded);
        if (succeeds)
        {
            Assert.Equal("degraded", NodeOutcomeNames.Name(Assert.Single(outage.Trace!, n => n.NodeId == "content").Kind));
        }

        Assert.Equal(runs + 3, rig.Probe.Runs);
        Assert.All(after.Calls!, c => Assert.Null(c.Cached));
    }

    [Theory]
    [InlineData("aaa")]
    [InlineData("zzz")]
    public async Task AFeatureThatFailedForAnArmTheRequestIsNotIn_DoesNotDegradeTheRun(string arm)
    {
        var connectors = new RequestReadsEndToEndTests.CountedConnectors { Fail = true };
        var host = PageHost(more: root =>
        {
            File.WriteAllText(Path.Combine(root, "experiments", "exp_promoted.jsonc"), $$"""
                { "experiment_id": "exp_promoted", "pipeline_id": "promoted", "owner": "team", "description": "Recalls for some.", "expires_at": "2099-12-31",
                  "arms": { "control": {}, "{{arm}}": { "nodes": { "recall": { "when": "ctx.user.ctr_30d > 0.03" } } } } }
                """);
            SampleBindings.Rebind(root, "user_store", $$"""
                { "connector": "{{RequestReadsEndToEndTests.CountedConnectors.TypeName}}", "data_file": "fixtures/users.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}} }
                """);
        });
        await using var rig = new Rig(host, bind: bindings => bindings.Add(connectors), pipelines: ["promoted", "page"]);

        var result = await rig.Run("req-1", assignment: ["exp_promoted/control"], pipeline: "page");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("succeeded", NodeOutcomeNames.Name(Assert.Single(result.Trace!, n => n.NodeId == "content").Kind));
        Assert.All(result.Calls!, c => Assert.All(c.Trace, n => Assert.Equal("succeeded", NodeOutcomeNames.Name(n.Kind))));
    }

    [Theory]
    [InlineData("ctx.user.ctr_30d > 0.03", "degraded", "degraded features: user store outage")] // its own fill covers what its when reads
    [InlineData("ctx.user.ctr_30d > 0.05", "skipped_when", "when is false, on degraded features: user store outage")]
    public async Task ANodeWhoseWhenReadsAFeatureServedStale_DecidesOnIt_AndSaysSo(string when, string outcome, string detail)
    {
        var connectors = new RequestReadsEndToEndTests.CountedConnectors();
        var host = PageHost(more: root =>
        {
            ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"recall\": { \"order\": 100, \"op\": \"MultiSourceRecall@1\",",
                $"\"recall\": {{ \"order\": 100, \"op\": \"MultiSourceRecall@1\", \"when\": \"{when}\",");
            SampleBindings.Rebind(root, "user_store", $$"""
                { "connector": "{{RequestReadsEndToEndTests.CountedConnectors.TypeName}}", "data_file": "fixtures/users.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}},
                  "cache": { "ttl_ms": 60000, "stale_if_error_ms": 600000, "memory": { "max_bytes": "1MiB" } } }
                """);
        });
        await using var rig = new Rig(host, bind: bindings => bindings.Add(connectors), pipelines: ["promoted", "page"]);
        Assert.True(rig.Engine.TryGetPipeline("promoted", out var promoted));
        var users = Assert.Single(promoted.Bindings.Tables, t => t.Schema.Source == "user_store");

        var fresh = await rig.Run("req-1", pipeline: "page");
        await CacheEngineTests.Eventually(() => users.Pin().RecordCount >= 1, () => "the user's record");
        rig.Clock.Advance(120_000);
        connectors.Fail = true;
        var stale = await rig.Run("req-2", T0 + 120_000, inputs: Elsewhere, pipeline: "page"); // calls no request made: the pipeline runs

        Assert.True(fresh.Succeeded && stale.Succeeded, fresh.Error ?? stale.Error);
        Assert.Equal("succeeded", NodeOutcomeNames.Name(Assert.Single(fresh.Trace!, n => n.NodeId == "content").Kind));
        Assert.Equal(3, stale.Calls!.Count);
        Assert.All(stale.Calls, c =>
        {
            var recall = Assert.Single(c.Trace, n => n.NodeId == "recall");
            Assert.Equal(outcome, NodeOutcomeNames.Name(recall.Kind));
            Assert.StartsWith(detail, recall.Detail);
        });
        Assert.Equal("degraded", NodeOutcomeNames.Name(Assert.Single(stale.Trace!, n => n.NodeId == "content").Kind));
    }

    [Fact]
    public async Task CallsOfOneRequestThatAskForItsArmsAtOnce_AllRunTheAnswerThatCameFirst()
    {
        var provider = new Staggered("promoted", expected: 3, first: ["exp_promoted/few"], rest: ["exp_promoted/control"]);
        await using var rig = new Rig(PageHost(), provider: provider, pipelines: ["promoted", "page"]);

        var result = await rig.Run("req-1", pipeline: "page", ask: true);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, provider.Asked);
        Assert.Equal(3, result.Calls!.Count);
        Assert.Single(result.Calls.Select(c => string.Join(",", c.Arms)).Distinct());
        Assert.Single(result.Experiments.Assigned, arm => arm.StartsWith("exp_promoted/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADegradedRunOfACalledPipeline_DegradesTheNodeThatCalled_AndNoCacheAboveKeepsWhatItReturned()
    {
        await using var rig = new Rig(Nested(), pipelines: NestedPipelines);
        rig.Probe.DegradeAt = "other";
        var outage = await rig.Run("req-outage");
        rig.Probe.DegradeAt = null;
        var elsewhere = await rig.Run("req-elsewhere", inputs: Elsewhere);
        await CacheEngineTests.Eventually(() => rig.Results().Pin().RecordCount >= 1, () => "the result of req-elsewhere");
        var after = await rig.Run("req-after");

        Assert.True(outage.Succeeded && elsewhere.Succeeded && after.Succeeded, outage.Error ?? elsewhere.Error ?? after.Error);
        var more = Assert.Single(Assert.Single(outage.Calls!, c => c.PipelineId == "promoted").Trace, n => n.NodeId == "more");
        Assert.Equal(("degraded", "pipeline 'other' returned a degraded result"), (NodeOutcomeNames.Name(more.Kind), more.Detail));
        Assert.Equal("degraded", NodeOutcomeNames.Name(outage.Trace!.Single(n => n.NodeId == "promoted").Kind));
        Assert.Null(Assert.Single(after.Calls!, c => c.PipelineId == "promoted").Cached);
        Assert.Equal("succeeded", NodeOutcomeNames.Name(after.Trace!.Single(n => n.NodeId == "promoted").Kind));
    }

    /// <summary>
    /// A read of the distributed tier that outlives its wait holds the request, as every abandoned call does: the
    /// plan's bindings, the store among them, are released when it has ended.
    /// </summary>
    [Fact]
    public async Task AStoreStillReadingForARequest_IsNotDisposedWhenItsPlanIsReplaced()
    {
        var stores = new SharedStores();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Host(bound: """{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": 20 } } }""");
        await using var rig = new Rig(host, stores: stores);
        stores.BeforeGet = () => reading.Task;

        var result = await rig.Run("req-1");
        var store = Assert.Single(stores.Created);
        SampleBindings.BindPipeline(host.Root, "promoted", Memory);
        rig.Load("feed");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, rig.Probe.Runs);
        Assert.Equal(0, store.Disposals);
        stores.BeforeGet = null;
        reading.SetResult();
        await store.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AHitOfAResultWhoseOwnCallACacheAnswered_SaysOfEachRunWhichRequestsCallComputedIt()
    {
        await using var rig = new Rig(Nested(otherCache: Hour), pipelines: NestedPipelines);

        var below = await rig.Run("req-below", T0, pipeline: "promoted", inputs: new RequestInputs().Set("user_id", 1001).Set("slots", 3));
        await rig.Cached(1, pipelineId: "other", holder: "promoted");
        var between = await rig.Run("req-between", T0 + 1_000);
        await rig.Cached(1);
        var served = await rig.Run("req-served", T0 + 2_000);
        await rig.Logs.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(below.Succeeded && between.Succeeded && served.Succeeded, below.Error ?? between.Error ?? served.Error);
        Assert.Equal(2, rig.Probe.Runs);
        var exposures = rig.Sink.Snapshot().Where(b => b.RequestId == "req-served" && b.PipelineId != "feed").OrderBy(b => b.PipelineId, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                ("other", "feed/promoted#0 > promoted/more#0", "req-below", T0, "promoted/more#0"),
                ("promoted", "feed/promoted#0", "req-between", T0 + 1_000, "feed/promoted#0"),
            },
            exposures.Select(b => (b.PipelineId, b.CalledBy!, b.CachedFromRequestId!, b.CachedFromTimeMs, b.CachedFromCalledBy!)));
    }

    [Fact]
    public async Task APipelineWithAnIntegerNoContentIdHolds_IsLoaded()
    {
        var host = Host();
        var request = Compile(host.Root, "promoted");
        request.Pipeline.ParamSchema["large"] = new ParamSpec { Type = "int64" };
        request.Pipeline.Params["large"] = System.Text.Json.Nodes.JsonValue.Create(9_007_199_254_740_993L);
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new Probe()) });

        var load = engine.Load(request);

        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
    }

    [Fact]
    public async Task WithAVersion_ACallAddedUnderTheCalledPipeline_KeepsTheResultsOfTheRequestsInNoArmOfIt()
    {
        var host = Nested(cache: """{ "ttl_ms": 3600000, "version": "v1" }""", when: " \"when\": \"false\",");
        var promoted = Path.Combine(host.Root, "pipelines", "promoted.jsonc");
        var calling = File.ReadAllText(promoted);
        File.WriteAllText(promoted, Called);
        await using var rig = new Rig(host, pipelines: NestedPipelines);
        var first = await rig.Run("req-1");
        await rig.Cached(1);

        File.WriteAllText(promoted, calling);
        rig.Load("promoted");
        var second = await rig.Run("req-2", T0 + 5_000);

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(1, rig.Probe.Runs);
        Assert.Equal(new CachedFrom("req-1", T0, "feed/promoted#0"), Assert.Single(second.Calls!).Cached);
    }

    [Fact]
    public async Task AnArmAPipelineUnderACachedCallDoesNotHave_IsReportedForARequestTheCacheAnswered()
    {
        var provider = new Asked((pipeline, _) => pipeline switch
        {
            "other" => ["no-such-arm", "exp_other/few"],
            "promoted" => ["exp_promoted/control"],
            _ => [],
        });
        await using var rig = new Rig(Nested(), provider: provider, pipelines: NestedPipelines);

        var first = await rig.Run("req-1", ask: true);
        await rig.Cached(1);
        var second = await rig.Run("req-2", T0 + 5_000, ask: true);

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(1, rig.Probe.Runs);
        Assert.Equal([new IgnoredArm("no-such-arm", IgnoredArmReason.Unknown)], first.Experiments.Ignored);
        Assert.Equal(first.Experiments.Ignored, second.Experiments.Ignored);
    }

    [Fact]
    public async Task ACacheWithAVersion_IsStillOfWhatAResultHolds()
    {
        var host = Host("""{ "ttl_ms": 3600000, "version": "v1" }""");
        Edit(host, "pipelines/promoted.jsonc", "\"columns\": { \"bid\": \"col.bid\" }", "\"columns\": { \"a\": \"col.bid * 0.0 + 77.0\", \"bid\": \"col.bid\" }");
        await using var rig = new Rig(host);
        var first = await rig.Run("req-1");
        await rig.Cached(1);

        Edit(host, "pipelines/promoted.jsonc", "\"a\": \"col.bid * 0.0 + 77.0\"", "\"z\": \"col.bid * 0.0 + 77.0\"");
        rig.Load("promoted");
        var second = await rig.Run("req-2", T0 + 5_000);

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(2, rig.Probe.Runs);
        Assert.Equal(Rows(first), Rows(second));
    }

    [Fact]
    public async Task AReleaseOfAPipelineUnderTheCalledOne_ComputesAnew()
    {
        var host = Nested();
        await using var rig = new Rig(host, pipelines: NestedPipelines);
        var first = await rig.Run("req-1");
        await rig.Cached(1);

        Edit(host, "pipelines/other.jsonc", OtherBids, OtherBidsMore);
        rig.Load("other");
        var second = await rig.Run("req-2", T0 + 5_000);

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.Equal(2, rig.Probe.Runs);
        Assert.NotEqual(Rows(first), Rows(second));
    }

    [Fact]
    public async Task AStaleResult_IsJudgedWhenTheRunHasFailed_NotWhenItWasRead()
    {
        await using var rig = new Rig(Host("""{ "ttl_ms": 1000, "stale_if_error_ms": 5000 }"""));
        var first = await rig.Run("req-1");
        await rig.Cached(1);

        rig.Clock.Advance(2_000);
        rig.Probe.Fail = true;
        rig.Probe.OnRun = () => rig.Clock.Advance(6_000);
        var late = await rig.Run("req-2", T0 + 2_000);

        Assert.True(first.Succeeded, first.Error);
        Assert.False(late.Succeeded);
        Assert.Contains("probe outage", late.Error);
    }

    [Fact]
    public void ACacheOnANodeThatOnlyDispatchesItsRows_IsAnErrorAtTheCache()
    {
        var host = PageHost(content: """
            "config": { "by": "col.entity_id", "pipelines": { "8": { "pipeline": "promoted", "inputs": { "slots": "col.entity_id" } } },
                        "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10, "cache": { "ttl_ms": -5 },
                        "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
            """);
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new Probe()) });

        var load = engine.Load(Compile(host.Root, "page"));

        Assert.False(load.Applied);
        var error = Assert.Single(load.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("PIPELINE_COLUMNS_CONFIG", "/nodes/content/config/cache"), (error.Code, error.Pointer));
        Assert.Contains("under the entry", error.Hint);
    }

    [Fact]
    public void EveryWrongSettingOfACache_IsSaidAtOnce_AndItsHintWorks()
    {
        static List<ConfigDiagnostic> Errors(string cache, int timeoutMs)
        {
            var host = Host(cache, $$"""{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": {{timeoutMs}} } } }""");
            var engine = new RecEngine(new PlanEnvironment
            {
                BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new Probe()), Bindings = PlanEnvironment.DefaultBindings().Add(new SharedStores()),
            });
            return engine.Load(Compile(host.Root, "feed")).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        }

        Assert.Equal(
            new[] { ("CACHE_RETENTION", "/nodes/promoted/config/cache/ttl_ms"), ("CACHE_VERSION", "/nodes/promoted/config/cache/version") },
            Errors("""{ "ttl_ms": 900000000000000, "version": "bad name" }""", timeoutMs: 5_000).Select(d => (d.Code, d.Pointer)).Order());

        var timeout = Assert.Single(Errors(Hour, timeoutMs: 0));
        Assert.Equal(("DISTRIBUTED_CACHE_CONFIG", "/pipelines/promoted/cache/distributed/timeout_ms"), (timeout.Code, timeout.Pointer));
        Assert.DoesNotContain("leave it out", timeout.Hint);
        Assert.Contains("\"timeout_ms\": 10", timeout.Hint);
    }

    [Fact]
    public async Task CallsOfOneRequestForTheSameResult_RunThePipelineOnce_AndSayWhichCallComputedIt()
    {
        await using var rig = new Rig(PageHost(slots: "3"), pipelines: ["promoted", "page"]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Probe.Hold = release.Task;

        var running = Start(rig, "req-1");
        release.SetResult();
        var result = await running;

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, rig.Probe.Runs);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, i => Assert.Equal(result.Items[0].Columns["games"].AsInt64List().ToArray(), i.Columns["games"].AsInt64List().ToArray()));
        Assert.NotEmpty(result.Items[0].Columns["games"].AsInt64List().ToArray());
        Assert.All(result.Items, i => Assert.Equal(("req-1", T0, "page/content#0"), (i.Columns["from"].AsString(), i.Columns["at"].AsInt64(), i.Columns["by"].AsString())));
        Assert.Equal(
            new CachedFrom?[] { null, new("req-1", T0, "page/content#0"), new("req-1", T0, "page/content#0") },
            result.Calls!.Select(c => c.Cached));
        Assert.All(result.Calls!, c => Assert.True(c.Succeeded));
    }

    /// <summary>
    /// The engine runs on its caller's thread until something it waits for does not answer at once: when the request's
    /// task comes back here the first run is held, and the other calls have gone as far as they go without it, so what
    /// the test lets go next it lets go to calls that wait.
    /// </summary>
    private static Task<RecResult> Start(Rig rig, string requestId)
    {
        var before = rig.Probe.Runs;
        var running = rig.Run(requestId, pipeline: "page").AsTask();
        Assert.Equal(before + 1, rig.Probe.Runs);
        return running;
    }

    [Theory]
    [InlineData("fails")]
    [InlineData("fails once")]
    [InlineData("degrades")]
    public async Task ARunThatFailedOrDegraded_IsItsCallsOwn_AndTheCallsWaitingRunThePipelineThemselves(string run)
    {
        await using var rig = new Rig(PageHost(slots: "3"), pipelines: ["promoted", "page"]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Probe.Hold = release.Task;
        rig.Probe.Fail = run == "fails";
        rig.Probe.FailFirst = run == "fails once" ? 1 : 0;
        rig.Probe.Degrade = run == "degrades";

        var running = Start(rig, "req-1"); // the others wait: none runs while the first does
        release.SetResult();
        var result = await running;

        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(3, result.Calls!.Count);
        Assert.All(result.Calls, c => Assert.True(c.Cached is null && c.Trace.Count > 0));
        const string Outage = "pipeline 'promoted' failed: node 'probe' failed: probe outage";
        Assert.Equal(
            run switch { "fails" => [Outage, Outage, Outage], "fails once" => [Outage, null, null], _ => new string?[] { null, null, null } },
            result.Calls.OrderBy(c => c.CalledBy, StringComparer.Ordinal).Select(c => c.Error));
    }

    [Fact]
    public async Task AResultComputedForTheRequest_IsTakenOnlyWhileItIsFresh()
    {
        await using var rig = new Rig(PageHost(slots: "3"), pipelines: ["promoted", "page"]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Probe.Hold = release.Task;

        var running = Start(rig, "req-1");
        rig.Clock.Advance(7_200_000);
        release.SetResult();
        var result = await running;

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.All(result.Calls!, c => Assert.True(c.Succeeded && c.Cached is null));
    }

    [Theory]
    [InlineData("not served")]
    [InlineData("no time")]
    [InlineData("in a called run")]
    [InlineData("not waited for")]
    public Task AResultOfARunWithACallThatWasNotAnswered_IsNotKept_WhateverItsNodeDidWithoutIt(string how) => ARunWithACallThatWasNotAnswered(how);

    /// <summary>
    /// A call its operator did not wait for goes on after its request has returned, and holds the request's memory
    /// until it ends: it reads it after waits of its own (for its cache's distributed tier, after its assignment).
    /// </summary>
    [Fact]
    public async Task ACallItsOperatorDidNotWaitFor_HoldsItsRequestsMemory_UntilItEnds()
    {
        var left = new LeftCall();
        var host = Host(more: root =>
        {
            WriteOther(root);
            ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"nodes\": {", "\"nodes\": {\n \"left\": { \"order\": 150, \"op\": \"LeftCall@1\" },");
        });
        var letGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(host, operators: [left], pipelines: NestedPipelines);
        try
        {
            rig.Probe.HoldNamed = letGo.Task; // the run of other stays under way

            var returned = await rig.Run("req-1");

            Assert.True(returned.Succeeded, returned.Error);
            Assert.False(left.Call!.IsCompleted);
            Assert.False(left.Memory!.IsReturned);
        }
        finally
        {
            letGo.TrySetResult();
        }

        var late = await left.Call!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(late.Succeeded, late.Error);
        await CacheEngineTests.Eventually(() => left.Memory!.IsReturned, () => "the memory of req-1, given back once its call has ended");
    }

    /// <param name="how"><c>throws</c>: the call throws (the caller arranges that) and the operator catches it. <c>in a
    /// called run</c>: promoted's own call is answered, by a run that made the call that was not.</param>
    internal static async Task ARunWithACallThatWasNotAnswered(string how)
    {
        var optional = new OptionalCall();
        var below = how == "in a called run";
        var config = how switch { "no time" => ", \"no_time\": true", "throws" => ", \"catches\": true", "not waited for" => ", \"leaves\": true", _ => "" };
        var host = PageHost(slots: "3", more: root =>
        {
            ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"nodes\": {",
                "\"nodes\": {\n \"more\": { \"order\": 150, \"when\": \"(ctx.request.country ?? 'us') != 'nz'\", " + (below
                    ? "\"op\": \"PipelineSource@1\", \"config\": { \"pipeline\": \"other\", \"limit_param\": \"top_k\" } },"
                    : "\"op\": \"OptionalCall@1\", \"config\": { \"pipeline\": \"other\"" + config + " } },"));
            WriteOther(root);
            if (below)
            {
                ReplayTests.Edit(root, "pipelines/other.jsonc", "\"nodes\": {",
                    "\"nodes\": {\n \"further\": { \"order\": 150, \"op\": \"OptionalCall@1\", \"config\": { \"pipeline\": \"elsewhere\" } },");
            }
        });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(host, operators: [optional], alone: true, pipelines: how == "not served" ? ["promoted", "page"] : ["other", "promoted", "page"]);
        try
        {
            rig.Probe.Hold = release.Task;
            rig.Probe.HoldNamed = how == "not waited for" ? letGo.Task : null; // the runs of other stay under way to the end

            var running = Start(rig, "req-1");
            release.SetResult();
            var result = await running;

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(how == "not waited for" ? 0 : 3, optional.Unanswered);
            Assert.Equal(3, rig.Probe.Runs);
            var runs = result.Calls!.Where(c => c.PipelineId == "promoted").ToList();
            Assert.Equal(3, runs.Count);
            Assert.All(runs, c =>
            {
                Assert.True(c.Succeeded && c.Cached is null, c.Error);
                Assert.Equal("succeeded", NodeOutcomeNames.Name(Assert.Single(c.Trace, n => n.NodeId == "more").Kind));
            });

            // One whose run is whole is kept, after it in the cache's queue: once that is there, the first one's would
            // be.
            var whole = await rig.Run("req-elsewhere", inputs: Elsewhere, pipeline: "page");
            await CacheEngineTests.Eventually(() => rig.Results(holder: "page").Pin().RecordCount >= 1, () => "the result of req-elsewhere");
            var after = await rig.Run("req-2", pipeline: "page");

            Assert.True(whole.Succeeded && after.Succeeded, whole.Error ?? after.Error);
            Assert.Equal(1, rig.Results(holder: "page").Pin().RecordCount);
            Assert.Equal(7, rig.Probe.Runs); // one for the three rows of the whole run, and three again
            Assert.DoesNotContain(after.Calls!, c => c.Cached is not null);
        }
        finally
        {
            letGo.TrySetResult();
        }
    }

    [Fact]
    public async Task ACallOfAPlanPublishedSince_DoesNotWaitForARunOfThePlanBefore()
    {
        const string Versioned = """{ "ttl_ms": 3600000, "version": "v" }""";
        static string Calling(string id, string? next) => Called
            .Replace("\"pipeline_id\": \"promoted\"", $"\"pipeline_id\": \"{id}\"", StringComparison.Ordinal)
            .Replace("\"nodes\": {", next is null ? "\"nodes\": {" : "\"nodes\": {\n \"cross\": { \"order\": 150, \"op\": \"PipelineSource@1\", \"on_failure\": \"fail_request\","
                + " \"timeout_ms\": 2000, \"config\": { \"pipeline\": \"" + next + "\", \"limit_param\": \"top_k\", \"cache\": " + Versioned + " } },", StringComparison.Ordinal);
        var host = PageHost(content: $$"""
            "config": { "by": "col.entity_id", "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                        "pipelines": { "5": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": {{Versioned}} },
                                       "8": { "pipeline": "other", "inputs": { "slots": "3" }, "cache": {{Versioned}} } },
                        "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
            """, more: root =>
            {
                // Each calls the other with the inputs the page passes: under one branch, for the result the other computes.
                File.WriteAllText(Path.Combine(root, "pipelines", "promoted.jsonc"), Calling("promoted", "other"));
                File.WriteAllText(Path.Combine(root, "pipelines", "other.jsonc"), Calling("other", "promoted"));
                File.WriteAllText(Path.Combine(root, "releases", "other.local.jsonc"), """{ "pipeline_id": "other", "environment": "local", "catalog": "home_feed", "bindings": "local" }""");
                SampleBindings.BindPipeline(root, "other", Memory);
            });
        await using var rig = new Rig(host, alone: true, pipelines: ["promoted", "other", "page"]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Probe.Hold = release.Task;
        var running = rig.Engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "page", RequestId = "req-1", Inputs = new RequestInputs().Set("user_id", 1001), Assignment = [], Deadline = TimeSpan.FromSeconds(20), TimeMs = T0,
            Debug = new DebugOptions { Trace = true },
        }, TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(2, rig.Probe.Runs); // both runs are under way, before either calls on

        foreach (var id in new[] { "promoted", "other" })
        {
            File.WriteAllText(Path.Combine(host.Root, "pipelines", id + ".jsonc"), Calling(id, null));
        }

        rig.Load("promoted");
        rig.Load("other");
        release.SetResult();
        var result = await running;

        Assert.All(result.Calls!, c => Assert.True(c.Succeeded, c.CalledBy + ": " + c.Error));
        Assert.Equal(4, result.Calls!.Count);
        Assert.Equal(4, rig.Probe.Runs);
    }

    [Fact]
    public async Task CallsOfOnePipelineThroughTwoCaches_EachRunIt_AndKeepTheirOwn()
    {
        var stores = new SharedStores();
        var host = PageHost(content: """
            "config": { "by": "col.entity_id", "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                        "pipelines": { "5": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": { "ttl_ms": 3600000 } },
                                       "8": { "pipeline": "second", "inputs": { "slots": "3" } } },
                        "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
            """, more: root =>
            {
                File.WriteAllText(Path.Combine(root, "pipelines", "second.jsonc"), PipelineCallEndToEndTests.Promoted
                    .Replace("\"pipeline_id\": \"promoted\"", "\"pipeline_id\": \"second\"", StringComparison.Ordinal)
                    .Replace("\"nodes\": {", "\"nodes\": {\n \"on\": { \"order\": 150, \"op\": \"PipelineSource@1\","
                        + " \"config\": { \"pipeline\": \"promoted\", \"limit_param\": \"top_k\", \"inputs\": { \"slots\": \"3\" }, \"cache\": " + Hour + " } },", StringComparison.Ordinal));
                File.WriteAllText(Path.Combine(root, "releases", "second.local.jsonc"), """{ "pipeline_id": "second", "environment": "local", "catalog": "home_feed", "bindings": "apart" }""");
                SampleBindings.CopyAs(root, "apart");
                SampleBindings.BindPipeline(root, "promoted", """{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } } }""", "apart");
            });
        await using var rig = new Rig(host, stores: stores, pipelines: ["promoted", "second", "page"]);

        var result = await rig.Run("req-1", pipeline: "page");

        Assert.True(result.Succeeded, result.Error);
        Assert.All(result.Calls!, c => Assert.True(c.Succeeded && c.Cached is null, c.Error));
        Assert.Equal(2, rig.Probe.Runs);
        await CacheEngineTests.Eventually(() => stores.Records == 1 && rig.Results(holder: "page").Pin().RecordCount == 1, () => "the result in each caller's cache");
    }

    [Fact]
    public async Task ACallThatLeadsBack_AsksForNoArmsOfAPipelineItsRunDoesNotGetTo()
    {
        static string Never(string node, string pipeline, int order) =>
            $"\n \"{node}\": {{ \"order\": {order}, \"op\": \"PipelineSource@1\", \"when\": \"false\", \"config\": {{ \"pipeline\": \"{pipeline}\", \"limit_param\": \"top_k\" }} }},";
        var host = Host(more: root =>
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "promoted.jsonc"), Called.Replace("\"nodes\": {", "\"nodes\": {" + Never("back", "other", 150) + Never("further", "unused", 160), StringComparison.Ordinal));
            WriteOther(root);
            ReplayTests.Edit(root, "pipelines/other.jsonc", "\"config\": { \"name\": \"other\" } },", "\"config\": { \"name\": \"other\" } },\n \"on\": { \"order\": 150, \"op\": \"PipelineSource@1\","
                + " \"config\": { \"pipeline\": \"promoted\", \"limit_param\": \"top_k\", \"inputs\": { \"slots\": \"3\" }, \"cache\": " + Hour + " } },");
            File.WriteAllText(Path.Combine(root, "pipelines", "unused.jsonc"), Called.Replace("\"pipeline_id\": \"promoted\"", "\"pipeline_id\": \"unused\"", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "releases", "unused.local.jsonc"), PipelineCallEndToEndTests.Releases.Replace("PIPELINE", "unused", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "experiments", "exp_unused.jsonc"), PipelineCallEndToEndTests.Experiments.Replace("PIPELINE", "unused", StringComparison.Ordinal));
        });
        var asked = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var provider = new Asked((pipeline, _) =>
        {
            asked.Enqueue(pipeline);
            return [];
        });
        await using var rig = new Rig(host, provider: provider, alone: true, pipelines: ["promoted", "other"]);
        rig.Load("unused");

        var result = await rig.Run("req-1", pipeline: "other", ask: true, inputs: new RequestInputs().Set("user_id", 1001).Set("slots", 3));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(Assert.Single(result.Calls!).Succeeded);
        Assert.Equal(["other"], asked); // the request's own pipeline; promoted, loaded alone, has no experiment to ask about
    }

    [Fact]
    public async Task PipelinesThatCallEachOther_AreRefusedAtTheCallThatLeadsBack_WhenTheirCallsAreCached()
    {
        // The page calls promoted for one row and other for another; promoted calls other, and other calls promoted,
        // each with the inputs the page passes: the calls under one branch are for the result the other branch computes.
        var host = PageHost(content: """
            "config": { "by": "col.entity_id", "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                        "pipelines": { "5": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": { "ttl_ms": 3600000 } },
                                       "8": { "pipeline": "other", "inputs": { "slots": "3" }, "cache": { "ttl_ms": 3600000 } } },
                        "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
            """, more: root =>
            {
                WriteNested(root, otherCache: Hour);
                ReplayTests.Edit(root, "pipelines/other.jsonc", "\"config\": { \"name\": \"other\" } },", "},\n \"back\": { \"order\": 150, \"op\": \"PipelineSource@1\","
                    + " \"config\": { \"pipeline\": \"promoted\", \"limit_param\": \"top_k\", \"inputs\": { \"slots\": \"3\" }, \"cache\": " + Hour + " } },");
            });
        await using var rig = new Rig(host, alone: true, pipelines: ["other", "promoted", "page"]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Probe.Hold = release.Task;

        // With a deadline, so that calls which wait for each other end, and say what they waited for.
        var running = rig.Engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "page", RequestId = "req-1", Inputs = new RequestInputs().Set("user_id", 1001), Assignment = [], Deadline = TimeSpan.FromSeconds(20), TimeMs = T0,
            Debug = new DebugOptions { Trace = true },
        }, TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(2, rig.Probe.Runs); // both branches' runs are under way before either calls on
        release.SetResult();
        var result = await running;

        Assert.Equal(
            [
                "page/content#0 > promoted/more#0 > other/back#0: pipeline 'promoted' was not called: the calls lead back to it (promoted → other → promoted)",
                "page/content#1 > other/back#0 > promoted/more#0: pipeline 'other' was not called: the calls lead back to it (other → promoted → other)",
            ],
            result.Calls!.Where(c => c.Error?.Contains(" failed: ", StringComparison.Ordinal) == false).Select(c => c.CalledBy + ": " + c.Error).Order(StringComparer.Ordinal));
        Assert.Equal(6, result.Calls!.Count);
        Assert.All(result.Calls, c => Assert.Contains("the calls lead back to it", c.Error));
        Assert.Equal(4, rig.Probe.Runs);
    }

    [Theory]
    [InlineData(Hour, null, "PIPELINE_CALL_CACHE_UNBOUND", "/nodes/promoted/config/cache")]
    [InlineData("""{ "version": "3" }""", Memory, "CACHE_TTL", "/nodes/promoted/config/cache")]
    [InlineData("""{ "stale_if_error_ms": -1 }""", null, "CACHE_TTL", "/nodes/promoted/config/cache")] // one error: nothing else of it applies without a time
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

    [Fact]
    public async Task ANodesCacheWithNoSettings_NeedsNoTierOfTheEnvironment()
    {
        var host = Host("{ }", bound: null);
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new Probe()) });

        var load = engine.Load(Compile(host.Root, "feed"));

        Assert.True(load.Applied, string.Join("; ", load.Diagnostics));
        Assert.DoesNotContain(load.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
    }

    [Theory]
    [InlineData("""{ }""", "", "COMPUTED_BY_CONFIG", "/nodes/promoted/config/computed_by")]
    [InlineData(ComputedBy, """, "promo.from": { "type": "int64?" }""", "COLUMN_TYPE_MISMATCH", "/nodes/promoted/config/computed_by/request_id")]
    [InlineData(ComputedBy, """, "promo.at": { "type": "float64?" }""", "COLUMN_TYPE_MISMATCH", "/nodes/promoted/config/computed_by/time_ms")]
    [InlineData(ComputedBy, """, "promo.by": { "type": "int64?" }""", "COLUMN_TYPE_MISMATCH", "/nodes/promoted/config/computed_by/called_by")]
    public void WrongComputedByColumns_AreAnErrorAtTheColumn(string computedBy, string declared, string code, string pointer)
    {
        var host = Host(computedBy: computedBy, declared: declared);
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new Probe()) });

        var load = engine.Load(Compile(host.Root, "feed"));

        Assert.False(load.Applied);
        Assert.Contains(load.Diagnostics, d => d.Code == code && d.Pointer == pointer);
    }

    internal static void Edit(HomeFeedTestHost host, string file, string from, string to)
    {
        var path = Path.Combine(host.Root, file);
        var text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    private sealed class Asked(Func<string, string, IReadOnlyList<string>> answer) : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken) =>
            new(answer(context.PipelineId, context.RequestId));
    }

    internal sealed class MovableClock : TimeProvider
    {
        private long _timeMs = 1_720_000_000_000L;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _timeMs));

        public void Advance(long milliseconds) => Interlocked.Add(ref _timeMs, milliseconds);
    }

    /// <summary>
    /// One with a <c>name</c> is another pipeline's: it is told by its name, and not counted.
    /// </summary>
    internal sealed class Probe : IOperatorFactory
    {
        private int _runs;
        private int _failFirst;
        private volatile bool _fail;
        private volatile bool _degrade;
        private volatile string? _degradeAt;
        private volatile Action? _onRun;
        private volatile Task? _hold;
        private volatile Task? _holdNamed;

        public int Runs => Volatile.Read(ref _runs);

        public bool Fail
        {
            get => _fail;
            set => _fail = value;
        }

        public int FailFirst
        {
            get => Volatile.Read(ref _failFirst);
            set => Volatile.Write(ref _failFirst, value);
        }

        public bool Degrade
        {
            get => _degrade;
            set => _degrade = value;
        }

        public string? DegradeAt
        {
            get => _degradeAt;
            set => _degradeAt = value;
        }

        /// <summary>Called by each run of the probe without a name, before it does what it was told: while its pipeline runs.</summary>
        public Action? OnRun
        {
            get => _onRun;
            set => _onRun = value;
        }

        /// <summary>Awaited by each run of the probe without a name, once counted: its pipeline's run stays under way until the test lets it go.</summary>
        public Task? Hold
        {
            get => _hold;
            set => _hold = value;
        }

        /// <summary>Awaited by each run of a probe with a name: the run of the pipeline it is in stays under way.</summary>
        public Task? HoldNamed
        {
            get => _holdNamed;
            set => _holdNamed = value;
        }

        public OperatorDescriptor Descriptor { get; } = new("Probe", 1, Effects.None, typeof(ProbeConfig), "Counts its runs, and fails or degrades when told.");

        public Operator? Compile(OperatorCompileContext context) => context.BindConfig<ProbeConfig>() is { } config ? new Node(this, config.Name) : null;

        private sealed class Node(Probe probe, string? name) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                if (name is not null)
                {
                    if (probe.DegradeAt == name)
                    {
                        context.ReportDegraded("probe degraded");
                    }

                    if (probe.HoldNamed is { } held)
                    {
                        await held;
                    }

                    return;
                }

                var run = Interlocked.Increment(ref probe._runs);
                probe.OnRun?.Invoke();
                if (probe.Hold is { } hold)
                {
                    await hold;
                }

                if (probe.Fail || run <= probe.FailFirst)
                {
                    context.Fail("probe outage");
                }
                else if (probe.Degrade)
                {
                    context.ReportDegraded("probe degraded");
                }
            }
        }
    }

    internal sealed class ProbeConfig
    {
        public string? Name { get; init; }
    }

    internal sealed class OptionalCall : IOperatorFactory
    {
        private int _unanswered;

        public int Unanswered => Volatile.Read(ref _unanswered);

        public OperatorDescriptor Descriptor { get; } = new("OptionalCall", 1, Effects.None, typeof(OptionalCallConfig), "Calls a pipeline, and goes on without it when the call fails.");

        public Operator? Compile(OperatorCompileContext context) =>
            context.BindConfig<OptionalCallConfig>() is { } config && context.TryCallPipeline(config.Pipeline, "/pipeline", out var call) ? new Node(this, call, config) : null;

        private sealed class Node(OptionalCall owner, PipelineCall call, OptionalCallConfig config) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                if (config.Leaves)
                {
                    _ = context.CallPipelineAsync(call).AsTask();
                    return;
                }

                try
                {
                    if ((await context.CallPipelineAsync(call, timeout: config.NoTime ? TimeSpan.Zero : null)).Succeeded)
                    {
                        return;
                    }
                }
                catch (InvalidOperationException) when (config.Catches)
                {
                }

                Interlocked.Increment(ref owner._unanswered);
            }
        }
    }

    internal sealed class LeftCall : IOperatorFactory
    {
        private volatile Task<PipelineCallResult>? _call;
        private volatile Core.Memory.RequestMemory? _memory;

        public Task<PipelineCallResult>? Call => _call;

        public Core.Memory.RequestMemory? Memory => _memory;

        public OperatorDescriptor Descriptor { get; } = new("LeftCall", 1, Effects.None, typeof(ProbeConfig), "Calls a pipeline, and does not wait for it.");

        public Operator? Compile(OperatorCompileContext context) => context.TryCallPipeline("other", "/pipeline", out var call) ? new Node(this, call) : null;

        private sealed class Node(LeftCall owner, PipelineCall call) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                owner._memory = context.Memory;
                owner._call = context.CallPipelineAsync(call).AsTask();
                return ValueTask.CompletedTask;
            }
        }
    }

    internal sealed class OptionalCallConfig
    {
        public string Pipeline { get; init; } = "";

        /// <summary>The call is given no time: it is not made.</summary>
        public bool NoTime { get; init; }

        /// <summary>What the call throws is caught, and the node goes on.</summary>
        public bool Catches { get; init; }

        /// <summary>The node ends without waiting for the call it made.</summary>
        public bool Leaves { get; init; }
    }

    /// <summary>
    /// A provider that holds the asks about one pipeline until <paramref name="expected"/> of them wait, then answers
    /// the first one way and the others another: calls that asked at once, answered differently.
    /// </summary>
    private sealed class Staggered(string pipelineId, int expected, IReadOnlyList<string> first, IReadOnlyList<string> rest) : IAssignmentProvider
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<string>>> _asks = new();

        public int Asked
        {
            get
            {
                lock (_asks)
                {
                    return _asks.Count;
                }
            }
        }

        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            if (context.PipelineId != pipelineId)
            {
                return new([]);
            }

            var ask = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_asks)
            {
                _asks.Add(ask);
                if (_asks.Count == expected)
                {
                    for (var i = 0; i < _asks.Count; i++)
                    {
                        _asks[i].SetResult(i == 0 ? first : rest);
                    }
                }
            }

            return new(ask.Task);
        }
    }

}
