using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Services;
using StrataRec.Features.Connectors;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Sources;
using static StrataRec.IntegrationTests.PipelineCallCacheEndToEndTests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The runs of one request share their cached reads of one record (<c>RequestReads</c>): a user's features and a call of
/// a service about no items are read once, by the first run the in-process tier does not answer, and the runs that read
/// them meanwhile wait for what it gets instead of asking the store, the connector or the service again. They get what
/// they would have got alone: the record, the origin's failure with the stale record that goes with it, or nothing
/// when the reads do not ask the same.
/// </summary>
public class RequestReadsEndToEndTests
{
    /// <summary>A cache with the distributed tier alone: no run finds in process what another has just read.</summary>
    private const string Shared = """
        "cache": { "ttl_ms": 60000, "stale_if_error_ms": 600000, "distributed": { "store": "shared_memory@1", "timeout_ms": 5000 } }
        """;

    /// <summary>The page's node without a cache of its calls: every row runs the pipeline it calls.</summary>
    private const string Uncached = """
        "config": { "pipeline": "promoted", "inputs": { "slots": "QUOTIENT" }, "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
                              "computed_by": { "request_id": "games.from", "time_ms": "games.at", "called_by": "games.by" } }
        """;

    private static readonly string[] Pipelines = ["promoted", "page"];

    /// <summary>
    /// A page of three rows that each run <c>promoted</c> with slots of their own: three runs in one request, each reading
    /// the request's user for its bids (a context entity's features) and recalling from <c>popular</c> (a service call
    /// about no items), through the counted connector and client.
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

    /// <summary>Each row's list and bids, as text.</summary>
    private static List<string> Lists(RecResult result) =>
        result.Items.Select(i => string.Join(",", i.Columns["games"].AsInt64List().ToArray()) + " at " + string.Join(",", i.Columns["bids"].AsFloat64List().ToArray())).ToList();

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Starts a request for the page whose first run is held, and returns with its three runs under way. The engine runs
    /// on its caller's thread until something it waits for does not answer at once: when the request's task comes back
    /// here the first run is held where the test holds it, and the others have gone as far as they go without it, so
    /// what the test lets go next it lets go to runs that wait. <paramref name="held"/> is that the first run got there,
    /// on this thread: were the engine to leave the thread earlier, the test fails here and not by its timing.
    /// </summary>
    private static Task<RecResult> Start(Rig rig, string requestId, Func<bool> held, long timeMs = T0)
    {
        var running = rig.Run(requestId, timeMs, pipeline: "page").AsTask();
        Assert.True(held(), "the first run is held by the time the request's task is returned");
        return running;
    }

    [Fact]
    public async Task TheRunsOfOneRequest_ReadAUsersFeaturesOnce()
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
        release.SetResult(); // the first run's read answers while the others wait for it
        var result = await running;

        Assert.True(expected.Succeeded && result.Succeeded, expected.Error ?? result.Error);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(1, connectors.Calls);
        Assert.Equal(1, stores.Gets);
        Assert.Equal(Lists(expected), Lists(result));
        Assert.All(result.Calls!, c => Assert.All(c.Trace, n => Assert.Equal("succeeded", NodeOutcomeNames.Name(n.Kind))));

        // The reads that took the first one's record are counted as hits of the request, beside the tiers' own.
        var keys = metrics.Of(StrataRecMetrics.Names.CacheKeys).Where(m => Equals(m.Tags[StrataRecTags.Source], "user_store"))
            .GroupBy(m => $"{m.Tags[StrataRecTags.CacheTier]}/{m.Tags[StrataRecTags.Outcome]}").ToDictionary(g => g.Key, g => g.Sum(m => (long)m.Value));
        Assert.Equal(2, keys.GetValueOrDefault("request/hit"));
        Assert.Equal(1, keys.GetValueOrDefault("distributed/miss"));
    }

    /// <summary>
    /// The connector's own failure fails the reads waiting for it as it failed its reader, and is not asked again. With
    /// no record to fall back on they are told so: an empty one in its place, read as a record, threw out of the reads.
    /// </summary>
    [Fact]
    public async Task AConnectorsFailure_FailsTheReadsWaitingForIt_WithoutAskingAgain()
    {
        var connectors = new CountedConnectors { Fail = true };
        var release = Gate();
        connectors.Hold = release.Task;
        await using var rig = Engine(Host(Shared), connectors);

        var running = Start(rig, "req-1", () => connectors.Calls == 1);
        release.SetResult();
        var result = await running;

        Assert.Equal(1, connectors.Calls);
        Assert.Equal(3, result.Calls!.Count);
        Assert.All(result.Calls, c => Assert.Equal("pipeline 'promoted' failed: node 'bid' failed: feature fetch failed: user store outage", c.Error));
    }

    /// <summary>
    /// When the connector fails and the distributed tier has the record past its time to live, every run serves it, as
    /// each would alone: the runs that waited did not read the tier, and get the record with the failure.
    /// </summary>
    [Fact]
    public async Task AConnectorsFailure_WithAStaleRecordInTheStore_ServesItToEveryRun()
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
        Assert.Equal(2, connectors.Calls); // one a request
        Assert.Equal(Lists(fresh), Lists(stale));
        Assert.All(stale.Calls!, c =>
        {
            var bid = Assert.Single(c.Trace, n => n.NodeId == "bid");
            Assert.Equal("degraded", NodeOutcomeNames.Name(bid.Kind));
            Assert.Contains("served stale from the cache", bid.Detail);
        });
    }

    [Fact]
    public async Task TheRunsOfOneRequest_CallAServiceAboutNoItemsOnce()
    {
        var lists = new CountedLists { HoldFrom = 20 }; // the runs' calls: the page's own recall asks for 3
        var stores = new SharedStores();
        var release = Gate();
        lists.Hold = release.Task;
        await using var reference = Engine(Host());
        await using var rig = Engine(Host(listCache: Shared), lists: lists, stores: stores);

        var expected = await reference.Run("req-1", pipeline: "page");
        var running = Start(rig, "req-1", () => lists.Calls == 2);
        release.SetResult();
        var result = await running;

        Assert.True(expected.Succeeded && result.Succeeded, expected.Error ?? result.Error);
        Assert.Equal(3, rig.Probe.Runs);
        Assert.Equal(2, lists.Calls); // the page's own recall, and one for its three runs of promoted
        Assert.Equal(2, stores.Gets); // and the store was read as often: the runs that waited took the first one's answer
        Assert.Equal(Lists(expected), Lists(result));
    }

    /// <summary>
    /// The service's own failure fails the calls waiting for it as it failed the one that called, and it is not called
    /// again for them; with a record past its time to live in the distributed tier, which those waiting did not read,
    /// every one of them serves it, as each would alone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServicesFailure_FailsTheCallsWaitingForIt_OrServesThemTheStaleRecordWithIt(bool stale)
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
        var running = Start(rig, "req-1", () => lists.Calls == calls + 2); // the page's own recall, which is answered, and the first run's
        release.SetResult();
        var result = await running;

        Assert.Equal(calls + 2, lists.Calls);
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

    /// <summary>
    /// Calls that ask a service for different numbers of items are not one call, though they are one record (a longer
    /// answer serves a shorter call, so the number is not in the record's key): a service that refuses the longer answers
    /// the shorter, which took the other's refusal as its own.
    /// </summary>
    [Fact]
    public async Task ACallRefusedForItsLimit_DoesNotFailACallOfASmallerOne()
    {
        var lists = new CountedLists { Most = 100, HoldFrom = 1000 }; // the call that is refused is held; the others answer at once
        var release = Gate();
        lists.Hold = release.Task;
        var pair = new TwoLimits(large: 1000, small: 5);
        var host = Host(listCache: Shared, more: root => ReplayTests.Edit(root, "pipelines/page.jsonc", "\"nodes\": {",
            "\"nodes\": {\n \"pair\": { \"order\": 50, \"op\": \"TwoLimits@1\", \"config\": { \"service\": \"popular\" } },")); // before the page's own recall
        await using var rig = new Rig(host, stores: new SharedStores(), bind: bindings => bindings.Add(new CountedConnectors()).Add(lists), operators: [pair], pipelines: Pipelines);

        var running = Start(rig, "req-1", () => lists.Calls >= 1); // the call for 1,000, which the service holds
        release.SetResult();
        var result = await running;

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("1000 items are more than the 100", Assert.Single(pair.Large).Error);
        var smaller = Assert.Single(pair.Small);
        Assert.True(smaller.Succeeded, smaller.Error);
    }

    /// <summary>
    /// Calls of a pipeline that judge its results by different times are not one call: a result in the distributed tier
    /// that is past one node's ttl is within another's, which serves it whatever the first one's run comes to. Joined,
    /// the node with the hour's ttl failed with the run of the node with the second's.
    /// </summary>
    [Fact]
    public async Task CallsThatJudgeAResultByOtherTimes_AreNotJoined()
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

        rig.Clock.Advance(2_500); // past the first node's ttl, within the second's
        var release = Gate();
        rig.Probe.Hold = release.Task;
        rig.Probe.Fail = true;
        var runs = rig.Probe.Runs;
        var running = Start(rig, "req-2", () => rig.Probe.Runs == runs + 1, T0 + 2_500); // the run of the node whose ttl has passed
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

    /// <summary><c>counted_memory@1</c>: <c>memory@1</c>, counting its calls; each waits while held, and fails when told.</summary>
    internal sealed class CountedConnectors : IBindingFactory<IFeatureSourceConnector>
    {
        public const string TypeName = "counted_memory@1";

        private readonly MemoryFeatureConnectorFactory _memory = new();
        private int _calls;
        private volatile Task? _hold;
        private volatile bool _fail;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Awaited by every call before it answers.</summary>
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
                Interlocked.Increment(ref owner._calls);
                if (owner.Hold is { } hold)
                {
                    await hold;
                }

                return owner.Fail ? FetchStatus.Failed("user store outage") : await inner.FetchAsync(keys, fields, writer, cancellationToken);
            }
        }
    }

    /// <summary>
    /// <c>TwoLimits@1</c>: a node that calls a service about no items for two numbers of items at once, and keeps what
    /// each call said.
    /// </summary>
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

        /// <summary>The most items a call may ask for.</summary>
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
