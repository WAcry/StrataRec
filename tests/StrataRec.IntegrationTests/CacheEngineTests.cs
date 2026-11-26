using System.Collections.Concurrent;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A <c>cache</c> source through the engine (design 6.6): the sample's <c>item_store</c> (every column type of the catalog,
/// nullable fields, batches of 25) read through an in-process table in front of its connector, and optionally a
/// distributed tier shared by engines. Served results are those of the fetched source; a recorded request replays exactly
/// without the cache; the distributed tier is a binding instance (design 10.3).
/// </summary>
/// <remarks>
/// Write-backs run in the background, so how many of a first request's keys miss depends on when they land: a later read
/// of the same request finds what an earlier one wrote back, or fetches it again. The tests wait for a state (a request
/// read every key from the table; the store holds every key it was asked for), never for a count or a time.
/// </remarks>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class CacheEngineTests
{
    private const string Source = "item_store";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ACachedSource_ServesWhatTheFetchedOneServes_AndLaterRequestsFromItsTable()
    {
        await using var reference = MetricsTests.LoadEngine(HomeFeedTestHost.Load()).Engine;
        await using var engine = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root => CacheItems(root))).Engine;
        using var metrics = new MetricCapture();

        var expected = await reference.ExecuteAsync(MetricsTests.Request("req-fetched"), TestContext.Current.CancellationToken);
        var before = Snapshot.Of(metrics);
        var first = await engine.ExecuteAsync(MetricsTests.Request("req-first"), TestContext.Current.CancellationToken);
        var firstRead = Snapshot.Of(metrics).Minus(before);
        var (fromTable, tableRead) = await UntilServedFromTable(engine, metrics, MetricsTests.Request);

        Assert.True(expected.Succeeded && first.Succeeded, first.Error);
        Assert.Equal(Items(expected), Items(first));
        Assert.Equal(Items(expected), Items(fromTable));
        Assert.True(firstRead.Keys("local", "miss") >= 1 && firstRead.ConnectorKeys >= 1, firstRead.ToString()); // a cold table
        Assert.Equal(firstRead.Keys("local", "miss") + firstRead.Keys("local", "hit"), tableRead.Keys("local", "hit")); // the same reads, all hits
        Assert.Equal(0, tableRead.Keys("distributed", "miss") + tableRead.Keys("distributed", "hit")); // there is no distributed tier
    }

    [Fact]
    public async Task ARecordedCacheRead_ReplaysExactly_WithoutTheCache()
    {
        static RecRequest Recorded(string id) => ReplayTests.Request(id, 1001, "home", ReplayTests.ControlArms);
        await using var engine = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root => CacheItems(root))).Engine;
        using var metrics = new MetricCapture();
        var missed = await engine.ExecuteAsync(Recorded("req-miss"), TestContext.Current.CancellationToken);
        var (hit, _) = await UntilServedFromTable(engine, metrics, Recorded);

        // A fresh engine on the same configuration: its table is empty, so a replay that read it would differ.
        await using var fresh = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root => CacheItems(root))).Engine;
        var before = metrics.Measurements.Count;
        foreach (var recorded in new[] { missed, hit })
        {
            Assert.True(recorded.Succeeded, recorded.Error);
            var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
            var outcome = await fresh.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
            Assert.Empty(outcome.Report.Gaps);
            Assert.Equal(Items(recorded), Items(outcome.Result));
        }

        // Neither tier nor the connector: a replay reads no table and writes nothing back.
        Assert.DoesNotContain(metrics.Measurements.Skip(before), m => m.Instrument.StartsWith("stratarec.cache.", StringComparison.Ordinal)
            || (m.Instrument == StrataRecMetrics.Names.IoKeys && Equals(m.Tags[StrataRecTags.IoTarget], Source)));
    }

    [Fact]
    public async Task APlanSwap_KeepsTheConnector_AndTakesOverTheTableWithWhatItFetched()
    {
        var root = HomeFeedTestHost.LoadModified(r => CacheItems(r)).Root;
        await using var engine = await Load(root, bindings: null);
        using var metrics = new MetricCapture();
        await UntilServedFromTable(engine, metrics, MetricsTests.Request);
        var table = ItemTable(engine);

        // The configuration loaded again: a new plan with a lease of its own gets the connector the host holds, so the
        // same table, with every row already written back.
        Assert.True((await engine.LoadAsync(Compile(root), cancellationToken: TestContext.Current.CancellationToken)).Applied);
        var before = Snapshot.Of(metrics);
        var result = await engine.ExecuteAsync(MetricsTests.Request("req-swapped"), TestContext.Current.CancellationToken);
        var read = Snapshot.Of(metrics).Minus(before);

        Assert.True(result.Succeeded, result.Error);
        Assert.Same(table, ItemTable(engine));
        Assert.Equal(3, engine.Bindings.TableCount); // item_store's and the sample's local author_store and item_country_store, none left over
        Assert.True(read.Keys("local", "miss") == 0 && read.Keys("local", "hit") > 0, read.ToString());
        Assert.Equal(0, read.ConnectorKeys);
    }

    [Fact]
    public async Task TwoEnginesSharingAHost_WithConnectorsOfTheirOwn_NeverReadEachOthersTable()
    {
        // The same configuration, so the same table identity, but each engine's registry creates its own connector: the
        // second engine finds an empty table and asks its own connector, instead of serving the rows the first engine's
        // connector fetched.
        var root = HomeFeedTestHost.LoadModified(r => CacheItems(r)).Root;
        using var host = new BindingHost();
        await using var first = await Load(root, host);
        await using var second = await Load(root, host);
        using var metrics = new MetricCapture();
        await UntilServedFromTable(first, metrics, MetricsTests.Request); // the first engine's table holds every key it reads

        var before = Snapshot.Of(metrics);
        var result = await second.ExecuteAsync(MetricsTests.Request("req-second"), TestContext.Current.CancellationToken);
        var read = Snapshot.Of(metrics).Minus(before);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(read.Keys("local", "miss") >= 1 && read.ConnectorKeys >= 1, read.ToString());
        Assert.NotSame(ItemTable(first), ItemTable(second));
        Assert.Equal(6, host.TableCount); // item_store's and the sample's local author_store and item_country_store, for each engine
    }

    [Fact]
    public async Task TwoEnginesSharingADistributedTier_TheSecondServesTheFirstsWriteBack_WithoutTheConnector()
    {
        var shared = new SharedStores();
        var root = HomeFeedTestHost.LoadModified(r => CacheItems(r, $$"""{ "store": "{{SharedStores.TypeName}}", "generation": 1 }""")).Root;
        await using var first = await Load(root, shared);
        await using var second = await Load(root, shared);
        using var metrics = new MetricCapture();

        var before = Snapshot.Of(metrics);
        var origin = await first.ExecuteAsync(MetricsTests.Request("req-origin"), TestContext.Current.CancellationToken);
        var firstRead = Snapshot.Of(metrics).Minus(before);
        await Eventually(shared.HoldsEveryKeyRead, () => "the first engine's distributed write-back: " + Snapshot.Of(metrics));
        before = Snapshot.Of(metrics);
        var fromStore = await second.ExecuteAsync(MetricsTests.Request("req-store"), TestContext.Current.CancellationToken);
        var secondRead = Snapshot.Of(metrics).Minus(before);

        Assert.True(origin.Succeeded && fromStore.Succeeded, origin.Error ?? fromStore.Error);
        Assert.Equal(Items(origin), Items(fromStore));
        Assert.True(firstRead.Keys("distributed", "miss") >= 1 && firstRead.ConnectorKeys >= 1, firstRead.ToString());
        Assert.True(secondRead.Keys("distributed", "hit") >= 1, secondRead.ToString());
        Assert.True(secondRead.Keys("distributed", "miss") + secondRead.Keys("distributed", "error") + secondRead.Keys("distributed", "mismatch") == 0, firstRead + " / " + secondRead);
        Assert.Equal(0, secondRead.ConnectorKeys);
        Assert.Equal(2, shared.Created.Count); // one store per engine: each engine has its own binding host
    }

    /// <summary>
    /// The shared tier's keys say where a source's values come from (audit of f47a255): a source bound to other data reads
    /// its connector, not what the earlier binding left under the same source and entity, where every record had the same
    /// layout and was taken for fresh (the new plan served the old data until it expired); a binding that only tunes how
    /// the values are fetched keeps them.
    /// </summary>
    [Fact]
    public async Task ASourceBoundToOtherData_DoesNotReadTheEarlierBindingsValues_AndOneOnlyTunedDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var shared = new SharedStores();
        var root = HomeFeedTestHost.LoadModified(r => CacheItems(r, $$"""{ "store": "{{SharedStores.TypeName}}", "generation": 1 }""")).Root;
        using var metrics = new MetricCapture();
        await using (var first = await Load(root, shared))
        {
            var origin = await first.ExecuteAsync(MetricsTests.Request("req-origin"), ct);
            Assert.True(origin.Succeeded, origin.Error);
            await Eventually(shared.HoldsEveryKeyRead, () => "the first engine's distributed write-back: " + Snapshot.Of(metrics));
        }

        // Fetched in other batches: the same values, read from the shared tier.
        ReplayTests.Edit(root, "bindings/local.jsonc", "\"max_batch\":25", "\"max_batch\":20"); // the file as Rebind wrote it
        var before = Snapshot.Of(metrics);
        await using (var tuned = await Load(root, shared))
        {
            Assert.True((await tuned.ExecuteAsync(MetricsTests.Request("req-tuned"), ct)).Succeeded);
        }

        var tunedRead = Snapshot.Of(metrics).Minus(before);
        Assert.True(tunedRead.Keys("distributed", "hit") >= 1 && tunedRead.ConnectorKeys == 0, tunedRead.ToString());

        // Other data under the same source: every item reported, which the feed's filter drops.
        var items = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "fixtures", "items.json")))!.AsObject();
        foreach (var item in items)
        {
            item.Value!["i_report_rate"] = 1.0;
        }

        File.WriteAllText(Path.Combine(root, "fixtures", "items-v2.json"), items.ToJsonString());
        ReplayTests.Edit(root, "bindings/local.jsonc", "fixtures/items.json", "fixtures/items-v2.json");
        before = Snapshot.Of(metrics);
        await using var moved = await Load(root, shared);
        var after = await moved.ExecuteAsync(MetricsTests.Request("req-moved"), ct);
        var movedRead = Snapshot.Of(metrics).Minus(before);
        await using var cold = await Load(root, new SharedStores());
        var expected = await cold.ExecuteAsync(MetricsTests.Request("req-moved"), ct);

        Assert.True(after.Succeeded && expected.Succeeded, after.Error ?? expected.Error);
        Assert.Equal(Items(expected), Items(after));
        Assert.True(movedRead.Keys("distributed", "hit") == 0 && movedRead.ConnectorKeys >= 1, movedRead.ToString());
    }

    [Fact]
    public async Task TheDistributedTier_IsABindingInstance_SharedWhileUnchanged_ReplacedWhenChanged_DisposedWithTheEngine()
    {
        var shared = new SharedStores();
        var root = HomeFeedTestHost.LoadModified(r => CacheItems(r, $$"""{ "store": "{{SharedStores.TypeName}}", "generation": 1 }""")).Root;
        var engine = await Load(root, shared);
        var store = Assert.Single(shared.Created);
        Assert.Equal(1, store.Loads);

        Assert.True((await engine.LoadAsync(Compile(root), cancellationToken: TestContext.Current.CancellationToken)).Applied); // the same binding: the same store
        Assert.Single(shared.Created);

        CacheItems(root, $$"""{ "store": "{{SharedStores.TypeName}}", "generation": 1, "timeout_ms": 20 }""");
        Assert.True((await engine.LoadAsync(Compile(root), cancellationToken: TestContext.Current.CancellationToken)).Applied); // how long the engine waits is not the store's identity
        Assert.Single(shared.Created);
        Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-unchanged"), TestContext.Current.CancellationToken)).Succeeded);

        CacheItems(root, $$"""{ "store": "{{SharedStores.TypeName}}", "generation": 2 }""");
        Assert.True((await engine.LoadAsync(Compile(root), cancellationToken: TestContext.Current.CancellationToken)).Applied);
        var replacement = Assert.Single(shared.Created, s => s != store);
        await store.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken); // no served plan uses it any more
        Assert.Equal(1, replacement.Loads);
        Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-changed"), TestContext.Current.CancellationToken)).Succeeded);

        await engine.DisposeAsync();
        await replacement.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal((1, 1), (store.Disposals, replacement.Disposals));
    }

    /// <summary>
    /// Binds the sample's <c>item_store</c> as a <c>cache</c> in front of its fixture, untimed (on a slow machine the cache
    /// would at most fetch again what it missed), with the distributed tier <paramref name="distributed"/> when given.
    /// </summary>
    internal static void CacheItems(string root, string? distributed = null) => SampleBindings.Rebind(root, Source, $$"""
        {
          "mode": "cache", "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}}, "max_batch": 25,
          "max_bytes": "4MiB",
          "cache": { "ttl_ms": 600000{{(distributed is null ? string.Empty : ", \"distributed\": " + distributed)}} }
        }
        """);

    internal static async Task<RecEngine> Load(string root, IBindingFactory<ICacheStore> stores)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(stores) });
        var load = await engine.LoadAsync(Compile(root));
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    /// <summary>An engine on its own registries (so its own connector) over <paramref name="bindings"/>, or a host of its own.</summary>
    private static async Task<RecEngine> Load(string root, BindingHost? bindings)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root }, bindings: bindings);
        var load = await engine.LoadAsync(Compile(root));
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static LocalFeatureTable ItemTable(RecEngine engine)
    {
        Assert.True(engine.TryGetPipeline("home_feed", out var pipeline));
        return Assert.Single(pipeline.Bindings.Tables, t => t.Schema.Source == Source);
    }

    internal static PlanCompileRequest Compile(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return request!;
    }

    /// <summary>
    /// Runs requests until one reads every key of <see cref="Source"/> from the in-process table (the earlier ones' write-backs
    /// landed): its result, and what it read.
    /// </summary>
    private static async Task<(RecResult Result, Snapshot Read)> UntilServedFromTable(RecEngine engine, MetricCapture metrics, Func<string, RecRequest> request)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        for (var attempt = 0; ; attempt++)
        {
            var before = Snapshot.Of(metrics);
            var result = await engine.ExecuteAsync(request("req-table-" + attempt));
            Assert.True(result.Succeeded, result.Error);
            var read = Snapshot.Of(metrics).Minus(before);
            if (read.Keys("local", "miss") == 0 && read.Keys("local", "hit") > 0)
            {
                Assert.Equal(0, read.ConnectorKeys);
                return (result, read);
            }

            Assert.True(DateTime.UtcNow < deadline, "no request was served from the table: " + read);
            await Task.Delay(5);
        }
    }

    internal static async Task Eventually(Func<bool> condition, Func<string> what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what());
            await Task.Delay(5);
        }
    }

    internal static List<(long, int, string)> Items(RecResult result) =>
        result.Items.Select(i => (i.Id.Int64Value, i.Rank, string.Join(";", i.Columns.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")))).ToList();

    /// <summary>What <see cref="Source"/> read so far: keys by cache tier and outcome, and keys asked of its connector.</summary>
    internal sealed record Snapshot(Dictionary<(string Tier, string Outcome), long> Counts, long ConnectorKeys)
    {
        public static Snapshot Of(MetricCapture metrics)
        {
            var counts = new Dictionary<(string, string), long>();
            foreach (var m in metrics.Of(StrataRecMetrics.Names.CacheKeys))
            {
                if (Equals(m.Tags[StrataRecTags.Source], Source))
                {
                    var key = ((string)m.Tags[StrataRecTags.CacheTier]!, (string)m.Tags[StrataRecTags.Outcome]!);
                    counts[key] = counts.GetValueOrDefault(key) + (long)m.Value;
                }
            }

            var connector = metrics.Of(StrataRecMetrics.Names.IoKeys)
                .Where(m => Equals(m.Tags[StrataRecTags.IoTarget], Source) && Equals(m.Tags[StrataRecTags.IoRole], IoRoles.Fetch)).Sum(m => (long)m.Value);
            return new Snapshot(counts, connector);
        }

        public long Keys(string tier, string outcome) => Counts.GetValueOrDefault((tier, outcome));

        public Snapshot Minus(Snapshot earlier) =>
            new(Counts.ToDictionary(c => c.Key, c => c.Value - earlier.Keys(c.Key.Tier, c.Key.Outcome)), ConnectorKeys - earlier.ConnectorKeys);

        public override string ToString() =>
            string.Join(", ", Counts.OrderBy(c => c.Key).Select(c => $"{c.Key.Tier}/{c.Key.Outcome}={c.Value}")) + $", connector keys {ConnectorKeys}";
    }
}

/// <summary>
/// <c>shared_memory@1</c>: distributed cache stores over one in-memory dictionary, so that engines with their own binding
/// hosts share records as instances share a Redis. Accepts any settings (a test changes one to change the identity) and
/// counts each store's loads and disposals.
/// </summary>
internal sealed class SharedStores : IBindingFactory<ICacheStore>
{
    public const string TypeName = "shared_memory@1";

    private readonly ConcurrentDictionary<string, byte[]> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _read = new(StringComparer.Ordinal);

    public string Type => TypeName;

    public Type SettingsType => typeof(Core.Bindings.NoSettings);

    public ConcurrentQueue<Store> Created { get; } = new();

    /// <summary>Whether every key any store was asked for has a record: what was missed has been written back.</summary>
    public bool HoldsEveryKeyRead() => !_read.IsEmpty && _read.Keys.All(_records.ContainsKey);

    public ICacheStore Create(BindingContext context)
    {
        var store = new Store(this);
        Created.Enqueue(store);
        return store;
    }

    internal sealed class Store(SharedStores shared) : ICacheStore, ILoadable, IAsyncDisposable
    {
        private int _loads;
        private int _disposals;

        public string StoreType => TypeName;

        public int Loads => Volatile.Read(ref _loads);

        public int Disposals => Volatile.Read(ref _disposals);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask LoadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loads);
            return ValueTask.CompletedTask;
        }

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var name = Name(keyspace, keys.Span[i]);
                shared._read[name] = true;
                if (shared._records.TryGetValue(name, out var record))
                {
                    records.Span[i] = record;
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken)
        {
            foreach (var entry in entries.Span)
            {
                shared._records[Name(keyspace, entry.Key)] = entry.Record.ToArray();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private static string Name(CacheKeyspace keyspace, string key) => keyspace.Prefix + key;
    }
}
