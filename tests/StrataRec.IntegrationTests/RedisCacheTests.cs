using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using StackExchange.Redis;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Features.Cache;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
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

    /// <summary>
    /// Review 19: credentials the server refuses are a wrong setting, unlike a server that is down. A dedicated ACL user on
    /// the test's Redis (so that a shared server needs no restart): its wrong password fails the load at the setting,
    /// never quoting the secret; its right one loads and serves.
    /// </summary>
    [RedisFact]
    public async Task CredentialsTheServerRefuses_FailTheLoadAtThePassword_AndTheRightOnesLoad()
    {
        var user = "stratarec-test-" + Guid.NewGuid().ToString("N");
        var admin = server.Connection.GetDatabase();
        await admin.ExecuteAsync("ACL", "SETUSER", user, "on", ">right-secret", "~*", "+@all");
        try
        {
            var secrets = new Secrets { ["redis-password"] = "wrong-secret" };
            await using (var refused = Create(server.Endpoint, RedisServer.UniquePrefix(), user, secrets))
            {
                var error = await Assert.ThrowsAsync<BindingException>(() => refused.LoadAsync(CancellationToken.None).AsTask());
                Assert.Equal("/password", error.Pointer);
                Assert.Contains(user, error.Reason);
                Assert.Contains("redis-password", error.Hint);
                Assert.DoesNotContain("wrong-secret", error.ToString());
            }

            secrets["redis-password"] = "right-secret";
            var prefix = RedisServer.UniquePrefix();
            await using var accepted = Create(server.Endpoint, prefix, user, secrets);
            await accepted.LoadAsync(CancellationToken.None);
            var items = new CacheKeyspace("item_store", "item", EntityKeyType.Int64);
            await accepted.SetAsync(items, new[] { new CacheStoreEntry(EntityKey.FromInt64(1), new byte[] { 7 }, TimeSpan.FromMinutes(1)) }, CancellationToken.None);
            var record = new ReadOnlyMemory<byte>[1];
            await accepted.GetAsync(items, new[] { EntityKey.FromInt64(1) }, record, CancellationToken.None);
            Assert.Equal(new byte[] { 7 }, record[0].ToArray());
        }
        finally
        {
            await admin.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    /// <summary>
    /// Review 19: a server whose default user needs a password refuses the connection itself. A wrong password fails the
    /// load at the setting, none at all fails it at the binding (the hint says to add one), the right one loads. A server
    /// that is down still loads (<see cref="RedisDownTests"/>).
    /// </summary>
    [DockerRedisFact]
    public async Task AServerThatRequiresAPassword_RefusesAWrongOneOrNone_AtLoad()
    {
        var endpoint = await server.StartRequirePassAsync();
        var secrets = new Secrets { ["redis-password"] = "wrong-secret" };
        await using (var wrong = Create(endpoint, RedisServer.UniquePrefix(), secrets: secrets))
        {
            var error = await RefusedAsync(wrong);
            Assert.Equal("/password", error.Pointer);
            Assert.DoesNotContain("wrong-secret", error.ToString());
        }

        await using (var none = Create(endpoint, RedisServer.UniquePrefix()))
        {
            var error = await RefusedAsync(none);
            Assert.Equal(string.Empty, error.Pointer);
            Assert.Contains("\"password\"", error.Hint);
        }

        secrets["redis-password"] = RedisServer.RequiredPassword;
        await using var right = Create(endpoint, RedisServer.UniquePrefix(), secrets: secrets);
        await right.LoadAsync(CancellationToken.None);
        var record = new ReadOnlyMemory<byte>[1];
        await right.GetAsync(new CacheKeyspace("item_store", "item", EntityKeyType.Int64), new[] { EntityKey.FromInt64(1) }, record, CancellationToken.None);
        Assert.True(record[0].IsEmpty);
    }

    /// <summary>
    /// The CI flake's first cause (an investigation reproduced it): StackExchange.Redis tells the connect that its
    /// connection ended before it publishes the refusal that ended it. Holding that publication (the client logs it as
    /// event 89 just before) until the load has decided made a refused load succeed, every time. The probe the load then
    /// makes copies the options, logger included, so it may be held too; it fails the load all the same, because a connect
    /// that gives up reads the refusal the client recorded, not the published one (review 24).
    /// </summary>
    [DockerRedisFact]
    public async Task ARefusalTheClientHasNotPublishedYet_StillFailsTheLoad()
    {
        var endpoint = await server.StartRequirePassAsync();
        using var publish = new ManualResetEventSlim();
        var log = new HoldRefusalLog(publish);
        var connects = 0;
        Task<ConnectionMultiplexer> Connect(ConfigurationOptions options)
        {
            if (Interlocked.Increment(ref connects) == 1)
            {
                options.LoggerFactory = log;
            }

            return ConnectionMultiplexer.ConnectAsync(options);
        }

        await using var store = new RedisCacheStore(new RedisStoreSettings(endpoint, RedisServer.UniquePrefix(), null, null, false, 0), Connect);
        BindingException error;
        try
        {
            error = await RefusedAsync(store);
        }
        finally
        {
            publish.Set();
        }

        Assert.Equal(string.Empty, error.Pointer);
        Assert.Contains("\"password\"", error.Hint);

        // The race was set up: the publication was held while the load decided, and never released by the hold's bound
        // (a client that renumbers the event, or stops logging it, would otherwise turn this into a plain refusal test).
        Assert.True(log.Held > 0, "the client never logged a refused handshake as event 89");
        Assert.Equal(0, log.TimedOut);
    }

    /// <summary>
    /// The CI flake's second cause: the client keeps a connection's first failure, so when its first connections are
    /// dropped, the refusal a later one gets never reaches a command's failure. A proxy in front of the server that
    /// requires a password resets the first two connections (the client's interactive and subscription ones).
    /// </summary>
    [DockerRedisFact]
    public async Task ARefusalHiddenByAnEarlierDroppedConnection_StillFailsTheLoad()
    {
        var endpoint = await server.StartRequirePassAsync();
        await using var proxy = new DroppingProxy(endpoint, drop: 2);

        await using var store = new RedisCacheStore(new RedisStoreSettings(proxy.Endpoint, RedisServer.UniquePrefix(), null, null, false, 0));
        var error = await RefusedAsync(store);

        Assert.Equal(string.Empty, error.Pointer);
        Assert.True(proxy.Dropped == 2, $"the proxy dropped {proxy.Dropped} connections");
    }

    /// <summary>
    /// The load's refusal. A load that succeeded instead failed once in CI and never locally: the failure then says what
    /// the server answers a read, which tells a refusal the load missed from a server it took for down.
    /// </summary>
    private static async Task<BindingException> RefusedAsync(RedisCacheStore store)
    {
        try
        {
            await store.LoadAsync(CancellationToken.None);
        }
        catch (BindingException refused)
        {
            return refused;
        }

        string answer;
        try
        {
            var record = new ReadOnlyMemory<byte>[1];
            await store.GetAsync(new CacheKeyspace("item_store", "item", EntityKeyType.Int64), new[] { EntityKey.FromInt64(1) }, record, CancellationToken.None);
            answer = "the read succeeded";
        }
        catch (Exception ex)
        {
            answer = ex.ToString();
        }

        throw new Xunit.Sdk.XunitException("The load accepted credentials the server refuses. A read then gave: " + answer);
    }

    /// <summary>Review 19: through the engine, refused credentials reject the plan at the binding's password setting.</summary>
    [RedisFact]
    public async Task AnEngineWhoseRedisRefusesItsCredentials_RejectsThePlanAtThePassword()
    {
        var user = "stratarec-test-" + Guid.NewGuid().ToString("N");
        var admin = server.Connection.GetDatabase();
        await admin.ExecuteAsync("ACL", "SETUSER", user, "on", ">right-secret", "~*", "+@all");
        try
        {
            var root = HomeFeedTestHost.LoadModified(r => CacheEngineTests.CacheItems(r,
                $$"""{ "store": "redis@1", "endpoint": "{{server.Endpoint}}", "user": "{{user}}", "password": "secret://redis-password" }""")).Root;
            var environment = new PlanEnvironment { BaseDirectory = root, CacheStores = new FeatureCacheStoreRegistry().AddRedis() };
            await using var engine = new RecEngine(environment, bindingOptions: new BindingHostOptions { Secrets = new Secrets { ["redis-password"] = "wrong-secret" } });

            var load = await engine.LoadAsync(CacheEngineTests.Compile(root));

            Assert.False(load.Applied);
            var error = Assert.Single(load.Diagnostics, d => d.IsError);
            Assert.Equal(("BINDING_LOAD_FAILED", "/sources/item_store/cache/distributed/password"), (error.Code, error.Pointer));
            Assert.DoesNotContain("wrong-secret", error.ToString());
        }
        finally
        {
            await admin.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    private static RedisCacheStore Create(string endpoint, string prefix, string? user = null, ISecretResolver? secrets = null)
    {
        var settings = new Dictionary<string, JsonElement>
        {
            ["endpoint"] = JsonSerializer.SerializeToElement(endpoint),
            ["key_prefix"] = JsonSerializer.SerializeToElement(prefix),
        };
        if (user is not null)
        {
            settings["user"] = JsonSerializer.SerializeToElement(user);
        }

        if (secrets is not null)
        {
            settings["password"] = JsonSerializer.SerializeToElement("secret://redis-password");
        }

        return (RedisCacheStore)new RedisCacheStoreFactory().Create(new BindingContext("item_store", settings, Path.GetTempPath(), secrets));
    }

    /// <summary>A secret store in memory.</summary>
    private sealed class Secrets : Dictionary<string, string>, ISecretResolver
    {
        public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken) =>
            new(TryGetValue(name, out var value) ? value : null);
    }
}

/// <summary>
/// A logger that holds the client's thread where it is about to publish a refused handshake (event 89 with an
/// authentication failure), until released: the investigation's deterministic reproduction of the publication race.
/// </summary>
internal sealed class HoldRefusalLog(ManualResetEventSlim release) : Microsoft.Extensions.Logging.ILoggerFactory
{
    private int _held;
    private int _timedOut;

    /// <summary>Publications held so far.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Holds that ended at their 30 s bound rather than by the release.</summary>
    public int TimedOut => Volatile.Read(ref _timedOut);

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Hold(this, release);

    public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Hold(HoldRefusalLog log, ManualResetEventSlim release) : Microsoft.Extensions.Logging.ILogger
    {
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 89 && exception is RedisConnectionException { FailureType: ConnectionFailureType.AuthenticationFailure })
            {
                Interlocked.Increment(ref log._held);
                if (!release.Wait(TimeSpan.FromSeconds(30)))
                {
                    Interlocked.Increment(ref log._timedOut);
                }
            }
        }
    }
}

/// <summary>A TCP proxy on the loopback that resets its first connections and forwards the others to a server.</summary>
internal sealed class DroppingProxy : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _target;
    private readonly int _drop;
    private readonly Task _accepting;
    private int _accepted;

    public DroppingProxy(string target, int drop)
    {
        _target = target;
        _drop = drop;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public string Endpoint => _listener.LocalEndpoint.ToString()!;

    public int Dropped => Math.Min(Volatile.Read(ref _accepted), _drop);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accepting.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            if (Interlocked.Increment(ref _accepted) <= _drop)
            {
                client.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0); // closing resets the connection
                client.Dispose();
                continue;
            }

            _ = ForwardAsync(client);
        }
    }

    private async Task ForwardAsync(System.Net.Sockets.TcpClient client)
    {
        using (client)
        using (var server = new System.Net.Sockets.TcpClient())
        {
            try
            {
                var colon = _target.LastIndexOf(':');
                await server.ConnectAsync(_target[..colon], int.Parse(_target[(colon + 1)..]), _stop.Token);
                var up = client.GetStream().CopyToAsync(server.GetStream(), _stop.Token);
                var down = server.GetStream().CopyToAsync(client.GetStream(), _stop.Token);
                await Task.WhenAny(up, down);
            }
            catch (Exception)
            {
                // A closed side or the proxy stopping ends the forwarding.
            }
        }
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

/// <summary>A <see cref="FactAttribute"/> that needs docker, to start a Redis of its own (<see cref="RedisServer.StartRequirePassAsync"/>).</summary>
public sealed class DockerRedisFactAttribute : FactAttribute
{
    public DockerRedisFactAttribute()
    {
        if (RedisServer.DockerUnavailable is { } why)
        {
            Skip = why;
        }
    }
}

/// <summary>
/// The Redis of <see cref="RedisCacheTests"/>: <c>STRATAREC_REDIS</c> when set, else a <c>redis:7</c> container on a free
/// loopback port, removed when the tests end. Each test writes under its own key prefix, so a shared server needs no
/// flush and runs do not see each other's records. A test that needs a server configured otherwise (a password on the
/// default user) starts its own container through docker, also removed at the end.
/// </summary>
public sealed class RedisServer : IAsyncLifetime
{
    public const string Variable = "STRATAREC_REDIS";

    /// <summary>The password of the default user of <see cref="StartRequirePassAsync"/>'s server.</summary>
    public const string RequiredPassword = "right-secret";

    private static readonly Lazy<string?> WhyDockerUnavailable = new(() => Docker("info", "--format", "{{.ServerVersion}}").ExitCode == 0
        ? null
        : "docker is not available to start a Redis configured for this test (redis:7 with --requirepass)");

    private readonly List<string> _containers = new();
    private ConnectionMultiplexer? _connection;
    private string? _requirePass;

    /// <summary>Why the Redis tests cannot run here, or null when they can.</summary>
    public static string? Unavailable => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 }
        ? null
        : DockerUnavailable is null ? null : $"no Redis to test against: set {Variable}=host:port of a Redis server, or make docker available to start redis:7";

    /// <summary>Why this machine cannot start Redis containers, or null when it can.</summary>
    public static string? DockerUnavailable => WhyDockerUnavailable.Value;

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

        Endpoint = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } endpoint ? endpoint : Start();
        _connection = await ConnectedAsync(Endpoint);
    }

    /// <summary>A server whose default user needs <see cref="RequiredPassword"/>: started on first use, removed at the end.</summary>
    public async Task<string> StartRequirePassAsync()
    {
        if (_requirePass is null)
        {
            var endpoint = Start("redis-server", "--requirepass", RequiredPassword);
            await (await ConnectedAsync(endpoint + ",password=" + RequiredPassword)).DisposeAsync();
            _requirePass = endpoint;
        }

        return _requirePass;
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        foreach (var container in _containers)
        {
            Docker("rm", "-f", container);
        }
    }

    /// <summary>Starts a <c>redis:7</c> container on a free loopback port: its endpoint.</summary>
    private string Start(params string[] command)
    {
        var run = Docker(["run", "-d", "--rm", "-p", "127.0.0.1::6379", "redis:7", .. command]);
        Assert.True(run.ExitCode == 0, "docker run redis:7 failed: " + run.Error);
        var container = run.Output.Trim();
        _containers.Add(container);
        var port = Docker("port", container, "6379/tcp");
        Assert.True(port.ExitCode == 0, "docker port failed: " + port.Error);
        return port.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    /// <summary>A server accepts connections a moment after its container starts: retries until it answers, within a generous bound.</summary>
    private static async Task<ConnectionMultiplexer> ConnectedAsync(string configuration)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var connection = await ConnectionMultiplexer.ConnectAsync(configuration + ",abortConnect=false,connectTimeout=2000");
        while (true)
        {
            try
            {
                await connection.GetDatabase().PingAsync();
                return connection;
            }
            catch (RedisException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
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
