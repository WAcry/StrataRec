using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using StackExchange.Redis;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Features.Cache;
using StrataRec.Hosting;
using StrataRec.Pipeline.Planning;
using StrataRec.Redis;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>redis@1</c> against a real Redis (design 6.6 <c>cache</c>, tier 2): the store's records, keys and times to live, and
/// two engines sharing it. The server is <c>STRATAREC_REDIS</c> (<c>host:port</c>; CI runs a Redis service) or, without
/// it, a <c>redis:7</c> container the fixture starts and removes; with neither, the tests are skipped and say why.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class RedisCacheTests(RedisServer server) : IClassFixture<RedisServer>
{
    [RedisFact]
    public async Task TheStore_ReadsWhatItWrote_UnderItsKeys_WithTheTimesToLiveItWasGiven()
    {
        var prefix = RedisServer.UniquePrefix();
        await using var store = Create(server.Endpoint, prefix);
        await store.LoadAsync(CancellationToken.None);
        var items = new CacheKeyspace("item:store", "item", EntityKeyType.Int64); // ':' in a source name is escaped
        var users = new CacheKeyspace("users", "user", EntityKeyType.String);

        await store.SetAsync(items, new[]
        {
            new CacheStoreEntry(EntityKey.FromInt64(1), new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(10)),
            new CacheStoreEntry(EntityKey.FromInt64(2), new byte[] { 4 }, TimeSpan.FromSeconds(30)),
        }, CancellationToken.None);
        await store.SetAsync(users, new[] { new CacheStoreEntry(EntityKey.FromString("u:7"), new byte[] { 5, 6 }, TimeSpan.FromMinutes(1)) }, CancellationToken.None);

        var records = new ReadOnlyMemory<byte>[3];
        await store.GetAsync(items, new[] { EntityKey.FromInt64(2), EntityKey.FromInt64(3), EntityKey.FromInt64(1) }, records, CancellationToken.None);
        Assert.Equal(new byte[] { 4 }, records[0].ToArray());
        Assert.True(records[1].IsEmpty); // never written
        Assert.Equal(new byte[] { 1, 2, 3 }, records[2].ToArray());
        var user = new ReadOnlyMemory<byte>[1];
        await store.GetAsync(users, new[] { EntityKey.FromString("u:7") }, user, CancellationToken.None);
        Assert.Equal(new byte[] { 5, 6 }, user[0].ToArray());

        var redis = server.Connection.GetDatabase();
        Assert.InRange((await redis.KeyTimeToLiveAsync(prefix + "item%3Astore:item:1"))!.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
        Assert.InRange((await redis.KeyTimeToLiveAsync(prefix + "item%3Astore:item:2"))!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        Assert.Equal(new byte[] { 5, 6 }, (byte[])(await redis.StringGetAsync(prefix + "users:user:u:7"))!);
    }

    [RedisFact]
    public async Task TwoEnginesSharingRedis_TheSecondServesTheFirstsWriteBack_WithoutTheConnector()
    {
        var prefix = RedisServer.UniquePrefix();
        var stores = new RecordedStores(new RedisCacheStoreFactory());
        var root = HomeFeedTestHost.LoadModified(r => CacheEngineTests.CacheItems(r,
            $$"""{ "store": "redis@1", "endpoint": "{{server.Endpoint}}", "key_prefix": "{{prefix}}" }""")).Root;
        await using var first = await CacheEngineTests.Load(root, stores);
        await using var second = await CacheEngineTests.Load(root, stores);
        using var metrics = new MetricCapture();

        var before = CacheEngineTests.Snapshot.Of(metrics);
        var origin = await first.ExecuteAsync(MetricsTests.Request("req-origin"));
        var firstRead = CacheEngineTests.Snapshot.Of(metrics).Minus(before);
        await CacheEngineTests.Eventually(stores.HoldsEveryKeyRead, () => "the first engine's write-back to Redis: " + CacheEngineTests.Snapshot.Of(metrics));
        before = CacheEngineTests.Snapshot.Of(metrics);
        var fromRedis = await second.ExecuteAsync(MetricsTests.Request("req-redis"));
        var secondRead = CacheEngineTests.Snapshot.Of(metrics).Minus(before);

        Assert.True(origin.Succeeded && fromRedis.Succeeded, origin.Error ?? fromRedis.Error);
        Assert.Equal(CacheEngineTests.Items(origin), CacheEngineTests.Items(fromRedis));
        Assert.True(firstRead.Keys("distributed", "miss") >= 1 && firstRead.ConnectorKeys >= 1, firstRead.ToString());
        Assert.True(secondRead.Keys("distributed", "hit") >= 1, secondRead.ToString());
        Assert.True(secondRead.Keys("distributed", "miss") + secondRead.Keys("distributed", "error") + secondRead.Keys("distributed", "mismatch") == 0,
            secondRead.ToString());
        Assert.Equal(0, secondRead.ConnectorKeys);

        // What the engine wrote: under the prefix, each record kept for its lifetime (ttl_ms 600000, jittered by up to 10%).
        var key = Assert.Single(stores.Written, k => k.EndsWith(":item:" + CacheEngineTests.Items(origin)[0].Item1, StringComparison.Ordinal));
        Assert.StartsWith(prefix + "item_store:item:", key, StringComparison.Ordinal);
        Assert.InRange((await server.Connection.GetDatabase().KeyTimeToLiveAsync(key))!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(660));
    }

    private static RedisCacheStore Create(string endpoint, string prefix)
    {
        var settings = new Dictionary<string, JsonElement>
        {
            ["endpoint"] = JsonSerializer.SerializeToElement(endpoint),
            ["key_prefix"] = JsonSerializer.SerializeToElement(prefix),
        };
        return (RedisCacheStore)new RedisCacheStoreFactory().Create(new BindingContext("item_store", settings, Path.GetTempPath(), secrets: null));
    }
}

/// <summary>
/// <c>redis@1</c> when Redis is down: nothing to start, so these run everywhere. The store connects at load without failing
/// it, and every read fails over to the connector.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class RedisDownTests
{
    [Fact]
    public async Task ARedisThatIsDown_FailsNoLoad_AndTheRequestsAreServedFromTheConnector()
    {
        // Port 1 on the loopback: nothing listens there, so every connection attempt is refused.
        var root = HomeFeedTestHost.LoadModified(r => CacheEngineTests.CacheItems(r,
            """{ "store": "redis@1", "endpoint": "127.0.0.1:1", "timeout_ms": 1000 }""")).Root;
        await using var reference = MetricsTests.LoadEngine(HomeFeedTestHost.Load()).Engine;
        await using var engine = await CacheEngineTests.Load(root, new RedisCacheStoreFactory());
        using var metrics = new MetricCapture();

        var expected = await reference.ExecuteAsync(MetricsTests.Request("req-fetched"));
        var before = CacheEngineTests.Snapshot.Of(metrics);
        var result = await engine.ExecuteAsync(MetricsTests.Request("req-redis-down"));
        var read = CacheEngineTests.Snapshot.Of(metrics).Minus(before);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(CacheEngineTests.Items(expected), CacheEngineTests.Items(result));
        Assert.True(read.Keys("distributed", "error") >= 1 && read.Keys("distributed", "hit") == 0, read.ToString());
        Assert.True(read.ConnectorKeys >= 1, read.ToString());
    }
}

/// <summary>A <see cref="FactAttribute"/> skipped, with the reason, when there is no Redis to test against (see <see cref="RedisServer"/>).</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (RedisServer.Unavailable is { } why)
        {
            Skip = why;
        }
    }
}

/// <summary>
/// The Redis of <see cref="RedisCacheTests"/>: <c>STRATAREC_REDIS</c> when set, else a <c>redis:7</c> container on a free
/// loopback port, removed when the tests end. Each test writes under its own key prefix, so a shared server needs no
/// flush and runs do not see each other's records.
/// </summary>
public sealed class RedisServer : IAsyncLifetime
{
    public const string Variable = "STRATAREC_REDIS";

    private static readonly Lazy<string?> WhyUnavailable = new(Check);
    private string? _container;
    private ConnectionMultiplexer? _connection;

    /// <summary>Why the Redis tests cannot run here, or null when they can.</summary>
    public static string? Unavailable => WhyUnavailable.Value;

    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>A connection of the test's own, to look at what the engine wrote.</summary>
    public ConnectionMultiplexer Connection => _connection ?? throw new InvalidOperationException("Redis is not available: " + Unavailable);

    public static string UniquePrefix() => "stratarec-test:" + Guid.NewGuid().ToString("N") + ":";

    public async Task InitializeAsync()
    {
        if (Unavailable is not null)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } endpoint)
        {
            Endpoint = endpoint;
        }
        else
        {
            var run = Docker("run", "-d", "--rm", "-p", "127.0.0.1::6379", "redis:7");
            Assert.True(run.ExitCode == 0, "docker run redis:7 failed: " + run.Error);
            _container = run.Output.Trim();
            var port = Docker("port", _container, "6379/tcp");
            Assert.True(port.ExitCode == 0, "docker port failed: " + port.Error);
            Endpoint = port.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        }

        // The container accepts connections a moment after it starts: retry until it answers, within a generous bound.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        _connection = await ConnectionMultiplexer.ConnectAsync(Endpoint + ",abortConnect=false,connectTimeout=2000");
        while (true)
        {
            try
            {
                await _connection.GetDatabase().PingAsync();
                return;
            }
            catch (RedisException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_container is not null)
        {
            Docker("rm", "-f", _container);
        }
    }

    private static string? Check()
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 })
        {
            return null;
        }

        return Docker("info", "--format", "{{.ServerVersion}}").ExitCode == 0
            ? null
            : $"no Redis to test against: set {Variable}=host:port of a Redis server, or make docker available to start redis:7";
    }

    private static (int ExitCode, string Output, string Error) Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                process.Kill();
                return (-1, string.Empty, "docker " + arguments[0] + " did not finish");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Win32Exception ex)
        {
            return (-1, string.Empty, ex.Message); // no docker
        }
    }
}

/// <summary>
/// Wraps a store factory so that a test knows when the stores hold every key they were asked for: what the first engine
/// missed has been written back. Keys are recorded as written once the inner store's write completed.
/// </summary>
internal sealed class RecordedStores(IFeatureCacheStoreFactory inner) : IFeatureCacheStoreFactory
{
    private readonly ConcurrentDictionary<string, bool> _read = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _written = new(StringComparer.Ordinal);
    private string _prefix = string.Empty;

    public string StoreType => inner.StoreType;

    /// <summary>The full keys written (with the binding's <c>key_prefix</c>).</summary>
    public IEnumerable<string> Written => _written.Keys.Select(k => _prefix + k);

    public bool HoldsEveryKeyRead() => !_read.IsEmpty && _read.Keys.All(_written.ContainsKey);

    public IFeatureCacheStore Create(BindingContext context)
    {
        if (context.Settings.TryGetValue("key_prefix", out var prefix))
        {
            _prefix = prefix.GetString()!;
        }

        return new Store(inner.Create(context), this);
    }

    private static string Name(CacheKeyspace keyspace, in EntityKey key) =>
        keyspace.Prefix + (key.StringValue ?? key.Int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private sealed class Store(IFeatureCacheStore store, RecordedStores recorded) : IFeatureCacheStore, ILoadable, IAsyncDisposable
    {
        public string StoreType => store.StoreType;

        public ValueTask LoadAsync(CancellationToken cancellationToken) =>
            store is ILoadable loadable ? loadable.LoadAsync(cancellationToken) : ValueTask.CompletedTask;

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<EntityKey> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            foreach (var key in keys.Span)
            {
                recorded._read[Name(keyspace, key)] = true;
            }

            return store.GetAsync(keyspace, keys, records, cancellationToken);
        }

        public async ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken)
        {
            await store.SetAsync(keyspace, entries, cancellationToken);
            for (var i = 0; i < entries.Length; i++)
            {
                recorded._written[Name(keyspace, entries.Span[i].Key)] = true;
            }
        }

        public ValueTask DisposeAsync() => store is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }
}
