using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Services;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Sources;
using static StrataRec.IntegrationTests.PipelineCallCacheEndToEndTests;

namespace StrataRec.IntegrationTests;

[Collection(ProcessWideListenersCollection.Name)]
public class RequestReadsEndToEndTests
{
    /// <summary>
    /// Here a host's metric listener throws as the called run ends.
    /// </summary>
    [Fact]
    public async Task AResultOfARunWithACallThatThrew_IsNotKept_WhateverItsNodeDidWithoutIt()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, own) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name == StrataRecMetrics.Names.RequestDuration)
            {
                own.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(static (_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (Equals(tag.Value, "other"))
                {
                    throw new InvalidOperationException("metric outage");
                }
            }
        });
        listener.Start();

        await ARunWithACallThatWasNotAnswered("throws");
    }

    /// <summary>A cache with the distributed tier alone: no run finds in process what another has just read.</summary>
    private const string Shared = """
        "cache": { "ttl_ms": 60000, "stale_if_error_ms": 600000, "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } }
        """;

    private const string Uncached = """
        "config": { "pipeline": "promoted", "inputs": { "slots": "QUOTIENT" }, "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                              "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
        """;

    private static readonly string[] Pipelines = ["promoted", "page"];

    /// <summary>
    /// A page of three rows that each run <c>promoted</c> with slots of their own: three runs in one request, each
    /// reading the request's user for its bids (a context entity's features) and recalling from <c>popular</c> (a
    /// service call about no items).
    /// </summary>
    private static HomeFeedTestHost Host(string userCache = "", string listCache = "", string content = Uncached, Action<string>? more = null) =>
        PageHost(content: content, more: root =>
        {
            ReplayTests.Edit(root, "pipelines/promoted.jsonc", "+ ctx.request.slots", "+ ctx.request.slots + ctx.user.ctr_30d");
            SampleBindings.Rebind(root, "user_store", $$"""
                { "connector": "{{CountedConnectors.TypeName}}", "data_file": "fixtures/users.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}}{{(userCache.Length == 0 ? "" : ", " + userCache)}} }
                """);
            SampleBindings.RebindService(root, "popular", $$"""
                { "client": "{{CountedLists.TypeName}}", "data_file": "fixtures/recall_popular.json"{{(listCache.Length == 0 ? "" : ", " + listCache)}} }
                """);
            more?.Invoke(root);
        });

    private static Rig Engine(HomeFeedTestHost host, CountedConnectors? connectors = null, CountedLists? lists = null, SharedStores? stores = null, string[]? pipelines = null) =>
        new(host, stores: stores ?? new SharedStores(), bind: bindings => bindings.Add(connectors ?? new CountedConnectors()).Add(lists ?? new CountedLists()),
            pipelines: pipelines ?? Pipelines);

    private static List<string> Lists(RecResult result) =>
        result.Items.Select(i => i.Columns["bids"].IsMissing
            ? string.Empty
            : string.Join(",", i.Columns["games"].AsInt64List().ToArray()) + " at " + string.Join(",", i.Columns["bids"].AsFloat64List().ToArray())).ToList();

    private static List<double> Tenure(RecResult result) => result.Items.Select(i => i.Columns["tenure"].AsFloat64()).ToList();

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The engine runs on its caller's thread until something it waits for does not answer at once: when the request's
    /// task comes back here the first run is held where the test holds it, and the others have gone as far as they go
    /// without it, so what the test lets go next it lets go to runs that wait.
    /// </summary>
    private static Task<RecResult> Start(Rig rig, string requestId, Func<bool> held, long timeMs = T0)
    {
        var running = rig.Run(requestId, timeMs, pipeline: "page").AsTask();
        Assert.True(held(), "the first run is held by the time the request's task is returned");
        return running;
    }

    [Fact]
    public async Task TheRunsOfOneRequest_AskTheConnectorForAUsersFeaturesOnce()
    {
        var connectors = new CountedConnectors();
        var stores = new SharedStores();
        var release = Gate();
        connectors.Hold = release.Task;
        await using var reference = Engine(Host());
        await using var rig = Engine(Host(Shared), connectors, stores: stores);

        using var metrics = new MetricCapture();
        var expected = await reference.Run("req-1", pipeline: "page");
        var running = Start(rig, "req-1", () => connectors.Calls == 1);
        release.SetResult();
        var result = await running;

        Assert.True(expected.Succeeded && result.Succeeded, expected.Error ?? result.Error);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(1, connectors.Calls);
        Assert.Equal(3, stores.Gets);
        Assert.Equal(Lists(expected), Lists(result));
        Assert.All(result.Calls!, c => Assert.All(c.Trace, n => Assert.Equal("succeeded", NodeOutcomeNames.Name(n.Kind))));

        var keys = metrics.Of(StrataRecMetrics.Names.CacheKeys).Where(m => Equals(m.Tags[StrataRecTags.Source], "user_store"))
            .GroupBy(m => $"{m.Tags[StrataRecTags.CacheTier]}/{m.Tags[StrataRecTags.Outcome]}").ToDictionary(g => g.Key, g => g.Sum(m => (long)m.Value));
        Assert.Equal(2, keys.GetValueOrDefault("request/hit"));
        Assert.Equal(3, keys.GetValueOrDefault("distributed/miss"));
    }

    /// <summary>
    /// A user's record is one for the page, which reads the user's tenure, and for the runs it calls, which read
    /// another feature of the user: whole, whichever of them gets it.
    /// </summary>
    [Theory]
    [InlineData("a user's features", 1, 3)] // the page's read, which its three runs of promoted take
    [InlineData("a service's answer", 2, 2)] // the page's own recall, and one for its three runs of promoted
    [InlineData("a pipeline's result", 1, 1)]
    public async Task ACacheWithNoSettings_IsOneRequests_WhoseRunsAskTheOriginOnce(string of, int asked, int taken)
    {
        const string None = "\"cache\": { }";
        const string Twice = """
            "config": { "by": "col.entity_id", "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                        "pipelines": { "5": { "pipeline": "promoted", "inputs": { "slots": "3" }CACHE },
                                       "8": { "pipeline": "promoted", "inputs": { "slots": "3" }CACHE } },
                        "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
            """;
        var connectors = new CountedConnectors();
        var lists = new CountedLists { HoldFrom = 20 }; // the runs' calls: the page's own recall asks for 3
        var stores = new SharedStores();
        var release = Gate();
        Action<string> tenure = root => ReplayTests.Edit(
            root, "pipelines/page.jsonc", "\"columns\": { \"games\": \"col.games\",", "\"columns\": { \"tenure\": \"ctx.user.tenure_days\", \"games\": \"col.games\",");
        await using var reference = Engine(of switch
        {
            "a user's features" => Host(more: tenure),
            "a service's answer" => Host(),
            _ => Host(content: Twice.Replace("CACHE", string.Empty, StringComparison.Ordinal)),
        });
        await using var rig = Engine(
            of switch
            {
                "a user's features" => Host(userCache: None, more: tenure),
                "a service's answer" => Host(listCache: None),
                _ => Host(content: Twice.Replace("CACHE", ", " + None, StringComparison.Ordinal), more: root => SampleBindings.BindPipeline(
                    root, "promoted", """{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } } }""")),
            },
            connectors, lists, stores);
        Func<int> origin = of switch
        {
            "a user's features" => () => connectors.Calls,
            "a service's answer" => () => lists.Calls,
            _ => () => rig.Probe.Runs,
        };
        switch (of)
        {
            case "a user's features": connectors.Hold = release.Task; break;
            case "a service's answer": lists.Hold = release.Task; break;
            default: rig.Probe.Hold = release.Task; break;
        }

        using var metrics = new MetricCapture();
        var expected = await reference.Run("req-1", pipeline: "page");
        var running = Start(rig, "req-1", () => origin() == asked);
        release.SetResult();
        var first = await running;
        var second = await rig.Run("req-2", pipeline: "page");

        Assert.True(expected.Succeeded && first.Succeeded && second.Succeeded, expected.Error ?? first.Error ?? second.Error);
        Assert.Equal(Lists(expected), Lists(first));
        Assert.Equal(Lists(expected), Lists(second));
        if (of == "a user's features")
        {
            Assert.Equal([420, 420, 420], Tenure(first));
            Assert.Equal(Tenure(expected), Tenure(first));
        }

        Assert.Equal(2 * asked, origin());
        Assert.Equal((0, 0), (stores.Gets, stores.Records));
        Assert.Equal(2 * taken, metrics.Of(StrataRecMetrics.Names.CacheKeys)
            .Where(m => Equals(m.Tags[StrataRecTags.CacheTier], "request") && Equals(m.Tags[StrataRecTags.Outcome], "hit")).Sum(m => (long)m.Value));
        if (of == "a pipeline's result")
        {
            Assert.Equal(["req-1 by page/content#0"], first.Calls!.Where(c => c.Cached is not null).Select(c => $"{c.Cached!.RequestId} by {c.Cached.CalledBy}"));
            Assert.Equal(["req-2 by page/content#0"], second.Calls!.Where(c => c.Cached is not null).Select(c => $"{c.Cached!.RequestId} by {c.Cached.CalledBy}"));
        }
    }

    [Fact]
    public async Task ARequestOfOneRun_SharesItsReadsToo()
    {
        var lists = new CountedLists { HoldFrom = 51 }; // the node's calls: the pipeline's own recall asks for fewer
        var release = Gate();
        lists.Hold = release.Task;
        var pair = new TwoLimits(large: 51, small: 51);
        var host = Host(listCache: "\"cache\": { }", more: root => ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"nodes\": {",
            "\"nodes\": {\n \"pair\": { \"order\": 50, \"op\": \"TwoLimits@1\", \"config\": { \"service\": \"popular\" } },"));
        await using var rig = new Rig(host, stores: new SharedStores(), bind: bindings => bindings.Add(new CountedConnectors()).Add(lists), operators: [pair], pipelines: ["promoted"]);
        Task<RecResult> running;
        try
        {
            running = rig.Run("req-1", pipeline: "promoted", inputs: new RequestInputs().Set("user_id", 1001).Set("slots", 3)).AsTask();
            Assert.Equal(1, lists.Calls);
        }
        finally
        {
            release.SetResult();
        }

        var result = await running;

        Assert.True(result.Succeeded, result.Error);
        Assert.True(Assert.Single(pair.Large).Succeeded && Assert.Single(pair.Small).Succeeded);
        Assert.Equal(2, lists.Calls); // the node's one call, and the pipeline's own recall
    }

    /// <summary>
    /// The requests a host sends as one group share their reads as the runs of one request do: under way together, they
    /// ask the origin once (a connector, a service, a pipeline), here through caches that keep nothing, so that nothing
    /// but the group answers the second.
    /// </summary>
    [Theory]
    [InlineData("a user's features")]
    [InlineData("a service's answer")]
    [InlineData("a pipeline's result")]
    public async Task RequestsOfOneGroup_UnderWayTogether_AskTheOriginOnce(string of)
    {
        const string None = "\"cache\": { }";
        const string Twice = """
            "config": { "by": "col.entity_id", "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                        "pipelines": { "5": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": { } },
                                       "8": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": { } } },
                        "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
            """;
        var connectors = new CountedConnectors();
        var lists = new CountedLists { HoldFrom = 20 }; // a run of promoted's: the page's own recall asks for 3
        var release = Gate();
        var called = of == "a pipeline's result";
        await using var rig = Engine(called ? Host(content: Twice) : Host(userCache: None, listCache: None), connectors, lists);
        Func<int> origin = of switch
        {
            "a user's features" => () => connectors.Calls,
            "a service's answer" => () => lists.Calls,
            _ => () => rig.Probe.Runs,
        };
        switch (of)
        {
            case "a user's features": connectors.Hold = release.Task; break;
            case "a service's answer": lists.Hold = release.Task; break;
            default: rig.Probe.Hold = release.Task; break;
        }

        var pipeline = called ? "page" : "promoted";
        var inputs = new RequestInputs().Set("user_id", 1001).Set("slots", 3);
        var group = new RequestGroup();
        Task<RecResult> first, second;
        try
        {
            first = rig.Run("req-a", pipeline: pipeline, inputs: called ? null : inputs, group: group).AsTask();
            Assert.Equal(1, origin());
            second = rig.Run("req-b", pipeline: pipeline, inputs: called ? null : inputs, group: group).AsTask();
            Assert.Equal(1, origin());
        }
        finally
        {
            release.SetResult(); // whatever was found: a held request keeps the engine from closing
        }

        var (a, b) = (await first, await second);
        var third = await rig.Run("req-c", pipeline: pipeline, inputs: called ? null : inputs, group: group);

        Assert.True(a.Succeeded && b.Succeeded && third.Succeeded, a.Error ?? b.Error ?? third.Error);
        Assert.Equal(a.Items.Select(i => i.Id.ToString()), b.Items.Select(i => i.Id.ToString()));
        Assert.Equal(2, origin());
        Assert.Equal(0, group.Reads.Count);
        if (called)
        {
            Assert.All(b.Calls!, c => Assert.Equal("req-a", c.Cached?.RequestId));
            Assert.Equal(["req-c"], third.Calls!.Where(c => c.Cached is not null).Select(c => c.Cached!.RequestId));
        }
    }

    [Fact]
    public async Task ARequestThatWritesNoLogs_IsNotOfItsGroup()
    {
        var connectors = new CountedConnectors();
        var release = Gate();
        connectors.Hold = release.Task;
        await using var rig = Engine(Host(userCache: "\"cache\": { }"), connectors);
        var inputs = new RequestInputs().Set("user_id", 1001).Set("slots", 3);
        var group = new RequestGroup();

        Task<RecResult> captured, served;
        try
        {
            captured = rig.Run("req-a", pipeline: "promoted", inputs: inputs, group: group, debug: new DebugOptions { CaptureLogs = true }).AsTask();
            Assert.Equal((1, 0), (connectors.Calls, group.Reads.Count));
            served = rig.Run("req-b", pipeline: "promoted", inputs: inputs, group: group).AsTask();
            Assert.Equal((2, 1), (connectors.Calls, group.Reads.Count));
        }
        finally
        {
            release.SetResult();
        }

        Assert.True((await captured).Succeeded && (await served).Succeeded);
    }

    [Fact]
    public async Task ARecordGotForTheRequest_IsTakenOnlyWhileItIsFresh()
    {
        var connectors = new CountedConnectors();
        var release = Gate();
        connectors.Hold = release.Task;
        await using var rig = Engine(Host(Shared), connectors);

        using var metrics = new MetricCapture();
        var running = Start(rig, "req-1", () => connectors.Calls == 1);
        rig.Clock.Advance(120_000); // the connector answers two minutes after it was asked: past the record's minute
        release.SetResult();
        var result = await running;

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, connectors.Calls);
        Assert.Equal(2, metrics.Of(StrataRecMetrics.Names.CacheKeys)
            .Where(m => Equals(m.Tags[StrataRecTags.Source], "user_store") && Equals(m.Tags[StrataRecTags.CacheTier], "request") && Equals(m.Tags[StrataRecTags.Outcome], "expired"))
            .Sum(m => (long)m.Value));
    }

    [Fact]
    public async Task AConnectorsFailure_IsItsReadsAlone_AndTheReadsWaitingAskTheConnectorThemselves()
    {
        var connectors = new CountedConnectors { FailFirst = 1 };
        var release = Gate();
        connectors.Hold = release.Task;
        await using var rig = Engine(Host(Shared), connectors);

        var running = Start(rig, "req-1", () => connectors.Calls == 1);
        release.SetResult();
        var result = await running;

        Assert.Equal(3, connectors.Calls);
        Assert.Equal(3, result.Calls!.Count);
        Assert.Equal("pipeline 'promoted' failed: node 'bid' failed: feature fetch failed: user store outage", Assert.Single(result.Calls, c => !c.Succeeded).Error);
    }

    [Fact]
    public async Task AConnectorsFailure_WithAStaleRecordInTheStore_LeavesEveryRunTheRecordItRead()
    {
        var connectors = new CountedConnectors();
        var stores = new SharedStores();
        await using var rig = Engine(Host(Shared), connectors, stores: stores);
        var fresh = await rig.Run("req-1", pipeline: "page");
        await CacheEngineTests.Eventually(stores.HoldsEveryKeyRead, () => "the user's record in the store");

        rig.Clock.Advance(120_000); // past the ttl, within stale_if_error_ms
        var release = Gate();
        connectors.Hold = release.Task;
        connectors.Fail = true;
        var running = Start(rig, "req-2", () => connectors.Calls == 2);
        release.SetResult();
        var stale = await running;

        Assert.True(fresh.Succeeded && stale.Succeeded, fresh.Error ?? stale.Error);
        Assert.Equal(4, connectors.Calls); // the first request's, and each run's of the second: a failure is not shared
        Assert.Equal(Lists(fresh), Lists(stale));
        Assert.All(stale.Calls!, c =>
        {
            var bid = Assert.Single(c.Trace, n => n.NodeId == "bid");
            Assert.Equal("degraded", NodeOutcomeNames.Name(bid.Kind));
            Assert.Contains("served stale from the cache", bid.Detail);
        });
    }

    [Theory]
    [InlineData(false, 2)] // the page's own recall, and one for its three runs of promoted
    [InlineData(true, 4)]
    public async Task TheRunsOfOneRequest_CallAServiceAboutNoItemsOnce_AndTakeItsAnswerWhileItIsFresh(bool late, int calls)
    {
        var lists = new CountedLists { HoldFrom = 20 }; // the runs' calls: the page's own recall asks for 3
        var stores = new SharedStores();
        var release = Gate();
        lists.Hold = release.Task;
        await using var reference = Engine(Host());
        await using var rig = Engine(Host(listCache: Shared), lists: lists, stores: stores);

        var expected = await reference.Run("req-1", pipeline: "page");
        var running = Start(rig, "req-1", () => lists.Calls == 2);
        if (late)
        {
            rig.Clock.Advance(120_000); // the service answers two minutes after it was called: past the record's minute
        }

        release.SetResult();
        var result = await running;

        Assert.True(expected.Succeeded && result.Succeeded, expected.Error ?? result.Error);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(calls, lists.Calls);
        Assert.Equal(4, stores.Gets);
        Assert.Equal(Lists(expected), Lists(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServicesFailure_IsItsCallsAlone_AndEveryCallServesTheStaleRecordItRead(bool stale)
    {
        var lists = new CountedLists { HoldFrom = 20 };
        var stores = new SharedStores();
        await using var rig = Engine(Host(listCache: Shared), lists: lists, stores: stores);
        RecResult? fresh = null;
        if (stale)
        {
            // The page's own recall and the runs' are one record: the runs' list, the longer, is written over the page's.
            fresh = await rig.Run("req-0", pipeline: "page");
            await CacheEngineTests.Eventually(() => stores.Sets == 2, () => "the runs' list in the store");
            rig.Clock.Advance(120_000); // past the ttl, within stale_if_error_ms
        }

        var calls = lists.Calls;
        var release = Gate();
        lists.Hold = release.Task;
        // The runs ask for 20. With a record to fall back on, the page's own recall, of 3, is refused too: answered, it
        // would write its shorter list over the record, which then answers no call of 20.
        lists.Most = stale ? 2 : 10;
        var running = Start(rig, "req-1", () => lists.Calls == calls + 2); // the page's own recall and the first run's
        release.SetResult();
        var result = await running;

        Assert.Equal(calls + 4, lists.Calls);
        Assert.Equal(3, result.Calls!.Count);
        if (stale)
        {
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(Lists(fresh!), Lists(result));
            Assert.All(result.Calls, c => Assert.Contains("its cache answered with values past their TTL", Assert.Single(c.Trace, n => n.NodeId == "recall").Detail));
        }
        else
        {
            Assert.All(result.Calls, c => Assert.Contains("20 items are more than the 10 a call may ask for", c.Error));
        }
    }

    [Fact]
    public async Task ACallRefusedForItsLimit_DoesNotFailACallOfASmallerOne()
    {
        var lists = new CountedLists { Most = 100, HoldFrom = 1000 };
        var release = Gate();
        lists.Hold = release.Task;
        var pair = new TwoLimits(large: 1000, small: 5);
        var host = Host(listCache: Shared, more: root => ReplayTests.Edit(root, "pipelines/page.jsonc", "\"nodes\": {",
            "\"nodes\": {\n \"pair\": { \"order\": 50, \"op\": \"TwoLimits@1\", \"config\": { \"service\": \"popular\" } },")); // before the page's own recall
        await using var rig = new Rig(host, stores: new SharedStores(), bind: bindings => bindings.Add(new CountedConnectors()).Add(lists), operators: [pair], pipelines: Pipelines);

        var running = Start(rig, "req-1", () => lists.Calls >= 1);
        release.SetResult();
        var result = await running;

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("1000 items are more than the 100", Assert.Single(pair.Large).Error);
        var smaller = Assert.Single(pair.Small);
        Assert.True(smaller.Succeeded, smaller.Error);
    }

    [Fact]
    public async Task ACallReadsTheTiersByItsOwnTimes_WhateverAnotherCallForTheSameResultComesTo()
    {
        const string Distributed = """{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } } }""";
        var host = PageHost(content: """
            "config": { "by": "col.entity_id", "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                        "pipelines": { "5": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": { "ttl_ms": 1000 } },
                                       "8": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": { "ttl_ms": 3600000 } } },
                        "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
            """, more: root => SampleBindings.BindPipeline(root, "promoted", Distributed));
        var stores = new SharedStores();
        await using var rig = new Rig(host, stores: stores, pipelines: Pipelines);
        var first = await rig.Run("req-1", pipeline: "page");
        await CacheEngineTests.Eventually(stores.HoldsEveryKeyRead, () => "the result in the store");

        rig.Clock.Advance(2_500);
        var release = Gate();
        rig.Probe.Hold = release.Task;
        rig.Probe.Fail = true;
        var runs = rig.Probe.Runs;
        var running = Start(rig, "req-2", () => rig.Probe.Runs == runs + 1, T0 + 2_500);
        release.SetResult();
        var second = await running;

        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(runs + 1, rig.Probe.Runs);
        var expired = Assert.Single(second.Calls!, c => c.CalledBy == "page/content#0");
        var within = Assert.Single(second.Calls!, c => c.CalledBy == "page/content#1");
        Assert.False(expired.Succeeded);
        Assert.True(within.Succeeded, within.Error);
        Assert.Equal("req-1", within.Cached?.RequestId);
    }

    [Fact]
    public async Task ACallThatKeepsNothing_AndACallThatKeepsItsResults_EachRunThePipeline()
    {
        const string Distributed = """{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } } }""";
        var host = PageHost(content: """
            "config": { "by": "col.entity_id", "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                        "pipelines": { "5": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": { } },
                                       "8": { "pipeline": "promoted", "inputs": { "slots": "3" }, "cache": { "ttl_ms": 3600000 } } },
                        "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
            """, more: root => SampleBindings.BindPipeline(root, "promoted", Distributed));
        var stores = new SharedStores();
        await using var rig = new Rig(host, stores: stores, pipelines: Pipelines);
        var release = Gate();
        rig.Probe.Hold = release.Task;

        var running = Start(rig, "req-1", () => rig.Probe.Runs == 2);
        release.SetResult();
        var first = await running;
        await CacheEngineTests.Eventually(() => stores.Records == 1, () => "the kept call's result in the store");
        var second = await rig.Run("req-2", pipeline: "page");

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.All(first.Calls!, c => Assert.Null(c.Cached));
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(["req-1 by page/content#1"], second.Calls!.Where(c => c.Cached is not null).Select(c => $"{c.Cached!.RequestId} by {c.Cached.CalledBy}"));
    }

    /// <summary>
    /// A call that waits for another's run reads the tiers first, so it holds the result past its ttl that it falls
    /// back to: when its own time ends before that run does, it serves it, as it would alone.
    /// </summary>
    [Fact]
    public async Task ACallWhoseTimeEndsWhileItWaitsForAnothersRun_ServesTheStaleResultItReadItself()
    {
        const string Distributed = """{ "cache": { "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } } }""";
        // The shorter call's time is real: a second, to read the tiers in on a machine that is busy, before it ends.
        var pair = new TwoBudgets(shorter: TimeSpan.FromSeconds(1));
        var host = PageHost(content: Uncached, more: root =>
        {
            SampleBindings.BindPipeline(root, "promoted", Distributed);
            ReplayTests.Edit(root, "pipelines/page.jsonc", "\"nodes\": {",
                "\"nodes\": {\n \"pair\": { \"order\": 50, \"op\": \"TwoBudgets@1\", \"config\": { \"pipeline\": \"promoted\", \"cache\": { \"ttl_ms\": 1000, \"stale_if_error_ms\": 600000 } } },");
        });
        var stores = new SharedStores();
        await using var rig = new Rig(host, stores: stores, operators: [pair], pipelines: Pipelines);
        var first = await rig.Run("req-0", pipeline: "page");
        await CacheEngineTests.Eventually(() => stores.Records >= 1, () => "the result in the store");
        pair.Clear();

        rig.Clock.Advance(2_500);
        var release = Gate();
        rig.Probe.Hold = release.Task;
        var runs = rig.Probe.Runs;

        // A wait may end a moment before its time, and the call then runs the pipeline in what is left, as it would
        // alone: that run fails here, so the call falls back either way and the probe holds the long call's run only.
        rig.Probe.OnRun = () =>
        {
            if (rig.Probe.Runs > runs + 1)
            {
                throw new InvalidOperationException("a run in what was left of the short call's time");
            }
        };
        var running = Start(rig, "req-1", () => rig.Probe.Runs == runs + 1, T0 + 2_500);
        var cutOff = await pair.ShortEnded.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        rig.Probe.OnRun = null;
        release.SetResult();
        var second = await running;

        Assert.True(first.Succeeded && second.Succeeded, first.Error ?? second.Error);
        Assert.True(cutOff.Succeeded, cutOff.Error);
        Assert.Equal(("req-0", true), (cutOff.Cached?.RequestId, cutOff.Cached?.Stale));
        var ran = Assert.Single(pair.Long);
        Assert.True(ran.Succeeded && ran.Cached is null, ran.Error);
    }

    internal sealed class CountedConnectors : IBindingFactory<IFeatureSourceConnector>
    {
        public const string TypeName = "counted_memory@1";

        private readonly MemoryFeatureConnectorFactory _memory = new();
        private int _calls;
        private int _failFirst;
        private volatile Task? _hold;
        private volatile bool _fail;

        public int Calls => Volatile.Read(ref _calls);

        public int FailFirst
        {
            get => Volatile.Read(ref _failFirst);
            set => Volatile.Write(ref _failFirst, value);
        }

        public Task? Hold
        {
            get => _hold;
            set => _hold = value;
        }

        public bool Fail
        {
            get => _fail;
            set => _fail = value;
        }

        public string Type => TypeName;

        public Type SettingsType => _memory.SettingsType;

        public IFeatureSourceConnector Create(BindingContext context) => new Connector(((IBindingFactory<IFeatureSourceConnector>)_memory).Create(context), this);

        private sealed class Connector(IFeatureSourceConnector inner, CountedConnectors owner) : IFeatureSourceConnector
        {
            public string ConnectorType => TypeName;

            public bool Supports(EntityKeyLayout layout) => inner.Supports(layout);

            public async ValueTask<FetchStatus> FetchAsync(
                EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref owner._calls);
                if (owner.Hold is { } hold)
                {
                    await hold;
                }

                return owner.Fail || call <= owner.FailFirst ? FetchStatus.Failed("user store outage") : await inner.FetchAsync(keys, fields, writer, cancellationToken);
            }
        }
    }

    internal sealed class TwoLimits(int large, int small) : IOperatorFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<ServiceStatus> Large { get; } = new();

        public System.Collections.Concurrent.ConcurrentQueue<ServiceStatus> Small { get; } = new();

        public OperatorDescriptor Descriptor { get; } = new("TwoLimits", 1, Effects.None, typeof(TwoLimitsConfig), "Calls a service for two numbers of items at once.");

        public Operator? Compile(OperatorCompileContext context) =>
            context.BindConfig<TwoLimitsConfig>() is { } config && context.Service(config.Service, "/service", IoRoles.Recall) is { } service ? new Node(this, service) : null;

        private sealed class Node(TwoLimits owner, BoundService service) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var larger = Call(context, owner.LargeLimit);
                var smaller = Call(context, owner.SmallLimit);
                owner.Large.Enqueue(await larger);
                owner.Small.Enqueue(await smaller);
            }

            private Task<ServiceStatus> Call(OperatorContext context, int limit) =>
                context.CallAsync(service, new ServiceCall { Service = service.Name, Limit = limit, RequestId = context.RequestId }, new ServiceResponse(context.Memory, limit));
        }

        private int LargeLimit => large;

        private int SmallLimit => small;
    }

    internal sealed class TwoLimitsConfig
    {
        public string Service { get; init; } = "";
    }

    internal sealed class TwoBudgets(TimeSpan shorter) : IOperatorFactory
    {
        private TaskCompletionSource<PipelineCallResult> _shortEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Collections.Concurrent.ConcurrentQueue<PipelineCallResult> Long { get; } = new();

        public Task<PipelineCallResult> ShortEnded => _shortEnded.Task;

        public OperatorDescriptor Descriptor { get; } = new("TwoBudgets", 1, Effects.None, typeof(TwoBudgetsConfig), "Calls a pipeline twice at once, once with a time of its own.");

        public void Clear()
        {
            Long.Clear();
            _shortEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Operator? Compile(OperatorCompileContext context) =>
            context.BindConfig<TwoBudgetsConfig>() is { } config && context.TryCallPipeline(config.Pipeline, "/pipeline", out var call, config.Cache, "/cache")
                && context.TryCallInput(call, "slots", "/pipeline", ColumnType.Int64)
                ? new Node(this, call)
                : null;

        private sealed class Node(TwoBudgets owner, PipelineCall call) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var inputs = new RequestInputs().Set("slots", 3);
                var longer = context.CallPipelineAsync(call, inputs).AsTask();
                var shorter = context.CallPipelineAsync(call, inputs, owner.Shorter).AsTask();
                owner._shortEnded.TrySetResult(await shorter);
                owner.Long.Enqueue(await longer);
            }
        }

        private TimeSpan Shorter => shorter;
    }

    internal sealed class TwoBudgetsConfig
    {
        public string Pipeline { get; init; } = "";

        public PipelineCallCacheConfig? Cache { get; init; }
    }

    /// <summary>
    /// <c>counted_list@1</c>: <c>memory_list@1</c>, counting its calls; one that asks for <see cref="HoldFrom"/> items or
    /// more waits while held, and one that asks for more than <see cref="Most"/> is refused.
    /// </summary>
    internal sealed class CountedLists : IBindingFactory<IServiceClient>
    {
        public const string TypeName = "counted_list@1";

        private readonly MemoryListServiceFactory _lists = new();
        private int _calls;
        private int _most = int.MaxValue;
        private volatile Task? _hold;

        public int Calls => Volatile.Read(ref _calls);

        public Task? Hold
        {
            get => _hold;
            set => _hold = value;
        }

        public int Most
        {
            get => Volatile.Read(ref _most);
            set => Volatile.Write(ref _most, value);
        }

        public int HoldFrom { get; init; }

        public string Type => TypeName;

        public Type SettingsType => _lists.SettingsType;

        public IServiceClient Create(BindingContext context) => new Client(((IBindingFactory<IServiceClient>)_lists).Create(context), this);

        private sealed class Client(IServiceClient inner, CountedLists owner) : IServiceClient
        {
            public ValueTask<ServiceSignature> DescribeAsync(string service, string? version, CancellationToken cancellationToken) =>
                inner.DescribeAsync(service, version, cancellationToken);

            public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._calls);
                if (call.Limit >= owner.HoldFrom && owner.Hold is { } hold)
                {
                    await hold;
                }

                return call.Limit > owner.Most
                    ? ServiceStatus.Failed($"{call.Limit} items are more than the {owner.Most} a call may ask for")
                    : await inner.CallAsync(call, response, cancellationToken);
            }
        }
    }
}
