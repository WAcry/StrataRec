using System.Globalization;
using System.Net;
using System.Text.Json;
using StackExchange.Redis;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Features.Cache;

namespace StrataRec.Redis;

/// <summary>
/// <c>redis@1</c>: the distributed tier of <c>cache</c> sources in Redis (design 6.6 <c>cache</c>, tier 2), through
/// StackExchange.Redis. One string value per (source, entity, key) under
/// <c>{key_prefix}{source}:{entity}:{key}</c>, holding the engine's encoded record, with the time to live the engine
/// gives (the record's lifetime and its <c>stale_if_error_ms</c> window).
/// </summary>
/// <remarks>
/// <para><b>Settings</b> (members of <c>cache.distributed</c> besides <c>store</c> and <c>timeout_ms</c>):
/// <c>endpoint</c> (required, <c>host:port</c>; for a cluster, any node), <c>key_prefix</c> (default
/// <c>stratarec:</c>), <c>user</c> (an ACL user), <c>password</c> (a <c>secret://</c> reference), <c>tls</c> (default
/// false), <c>database</c> (default 0; a cluster has only 0).</para>
/// <para><b>Round trips.</b> A read is one round trip per chunk: one <c>MGET</c> on a standalone server; on a cluster, whose
/// <c>MGET</c> cannot span hash slots, pipelined <c>GET</c>s, sent together. A write is one pipelined batch of
/// <c>SET … PX</c>. Keys are not hash-tagged, so a keyspace spreads over the cluster's shards.</para>
/// <para><b>Failing fast.</b> The connection is made at load, but a server that is down then does not fail the load: the
/// tier is an optimisation, so the plan serves from the connector while the client reconnects in the background, and
/// commands sent while it is disconnected fail at once rather than queue. A wrong password or a missing secret does fail
/// the load. Commands time out after one second; the engine stops waiting for a read much earlier (the tier's
/// <c>timeout_ms</c>), and a read that throws or is late sends its keys to the connector.</para>
/// </remarks>
public sealed class RedisCacheStore : IFeatureCacheStore, ILoadable, IAsyncDisposable
{
    public const string Type = "redis@1";

    /// <summary>How long a command may take before the client fails it.</summary>
    private const int CommandTimeoutMs = 1000;

    private readonly RedisStoreSettings _settings;
    private readonly Func<ConfigurationOptions, Task<ConnectionMultiplexer>> _connect;
    private Task<ConnectionMultiplexer>? _connecting;
    private IDatabase? _database;
    private volatile bool _cluster;

    /// <param name="settings">The checked settings.</param>
    /// <param name="connect">Connects; tests pass one they can hold.</param>
    internal RedisCacheStore(RedisStoreSettings settings, Func<ConfigurationOptions, Task<ConnectionMultiplexer>>? connect = null)
    {
        _settings = settings;
        _connect = connect ?? (static options => ConnectionMultiplexer.ConnectAsync(options));
    }

    public string StoreType => Type;

    /// <summary>Resolves the password and connects. A server that cannot be reached does not fail the load (see remarks).</summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        var options = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            BacklogPolicy = BacklogPolicy.FailFast,
            ClientName = "stratarec",
            ConnectTimeout = 2000,
            AsyncTimeout = CommandTimeoutMs,
            SyncTimeout = CommandTimeoutMs,
            DefaultDatabase = _settings.Database,
            Ssl = _settings.Tls,
            User = _settings.User,
        };
        options.EndPoints.Add(_settings.Endpoint);
        if (_settings.Password is { } password)
        {
            options.Password = (await password.ResolveAsync(cancellationToken).ConfigureAwait(false)).Reveal();
        }

        // The connect itself cannot be cancelled: a cancelled load stops waiting for it, and the store keeps it, so that
        // its disposal waits for a connection that completes later and disposes it (review 19).
        var connecting = _connect(options);
        Volatile.Write(ref _connecting, connecting);
        var connection = await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        connection.ConnectionRestored += (_, _) => _cluster = IsCluster(connection);
        _database = connection.GetDatabase(_settings.Database);
        _cluster = IsCluster(connection);
    }

    /// <summary>Whether the connection is to a cluster (whose multi-key commands cannot span hash slots).</summary>
    private static bool IsCluster(ConnectionMultiplexer connection)
    {
        foreach (var server in connection.GetServers())
        {
            if (server.IsConnected && server.ServerType == ServerType.Cluster)
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask GetAsync(
        CacheKeyspace keyspace, ReadOnlyMemory<EntityKey> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
    {
        var database = _database ?? throw new InvalidOperationException("redis@1 is not loaded.");
        var redisKeys = Keys(keyspace, keys.Span);
        if (!_cluster)
        {
            var values = await database.StringGetAsync(redisKeys).WaitAsync(cancellationToken).ConfigureAwait(false);
            Copy(values, records.Span);
            return;
        }

        var batch = database.CreateBatch();
        var gets = new Task<RedisValue>[redisKeys.Length];
        for (var i = 0; i < redisKeys.Length; i++)
        {
            gets[i] = batch.StringGetAsync(redisKeys[i]);
        }

        batch.Execute();
        await Task.WhenAll(gets).WaitAsync(cancellationToken).ConfigureAwait(false);
        Copy(gets, records.Span);
    }

    public async ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken)
    {
        var database = _database ?? throw new InvalidOperationException("redis@1 is not loaded.");
        await Task.WhenAll(Send(database, _settings.KeyPrefix + keyspace.Prefix, entries.Span)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one <c>SET … PX</c> per entry in one pipelined batch.</summary>
    private static Task<bool>[] Send(IDatabase database, string prefix, ReadOnlySpan<CacheStoreEntry> entries)
    {
        var batch = database.CreateBatch();
        var sets = new Task<bool>[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            sets[i] = batch.StringSetAsync(prefix + KeyText(entries[i].Key), entries[i].Record, entries[i].TimeToLive, When.Always);
        }

        batch.Execute();
        return sets;
    }

    /// <summary>
    /// Closes the connection, also one a cancelled load left connecting: the disposal then waits for the connect to end
    /// (within the connect timeout) and disposes what it made.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _connecting) is not { } connecting)
        {
            return;
        }

        ConnectionMultiplexer connection;
        try
        {
            connection = await connecting.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return; // it made no connection
        }

        await connection.CloseAsync(allowCommandsToComplete: false).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private RedisKey[] Keys(CacheKeyspace keyspace, ReadOnlySpan<EntityKey> keys)
    {
        var prefix = _settings.KeyPrefix + keyspace.Prefix;
        var redisKeys = new RedisKey[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            redisKeys[i] = prefix + KeyText(keys[i]);
        }

        return redisKeys;
    }

    private static string KeyText(in EntityKey key) => key.StringValue ?? key.Int64Value.ToString(CultureInfo.InvariantCulture);

    private static void Copy(Task<RedisValue>[] gets, Span<ReadOnlyMemory<byte>> records)
    {
        for (var i = 0; i < gets.Length; i++)
        {
            records[i] = Bytes(gets[i].Result);
        }
    }

    private static void Copy(RedisValue[] values, Span<ReadOnlyMemory<byte>> records)
    {
        for (var i = 0; i < values.Length; i++)
        {
            records[i] = Bytes(values[i]);
        }
    }

    /// <summary>A fresh array per value: the engine may keep it (as the table's write-back) after the read.</summary>
    private static ReadOnlyMemory<byte> Bytes(in RedisValue value) => value.IsNull ? default : (byte[])value!;
}

/// <summary>The checked settings of one <c>redis@1</c> binding.</summary>
internal sealed record RedisStoreSettings(string Endpoint, string KeyPrefix, string? User, SecretReference? Password, bool Tls, int Database);

/// <summary>Creates <see cref="RedisCacheStore"/>s; register it with <see cref="RedisRegistration.AddRedis"/>.</summary>
public sealed class RedisCacheStoreFactory : IFeatureCacheStoreFactory
{
    private static readonly string[] Known = ["endpoint", "key_prefix", "user", "password", "tls", "database"];

    public string StoreType => RedisCacheStore.Type;

    public IFeatureCacheStore Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var (name, _) in context.Settings)
        {
            if (Array.IndexOf(Known, name) < 0)
            {
                throw new BindingException("/" + BindingContext.EscapePointerToken(name), $"'{name}' is not a setting of {RedisCacheStore.Type}.",
                    NameSuggestions.DidYouMean(name, Known) ?? $"its settings are {string.Join(", ", Known)}");
            }
        }

        var endpoint = String(context, "endpoint") ?? throw new BindingException(string.Empty, $"{RedisCacheStore.Type} needs an 'endpoint'.",
            "add the server, e.g. \"endpoint\": \"redis.internal:6379\" (for a cluster, any of its nodes)");
        if (!IsEndpoint(endpoint))
        {
            throw new BindingException("/endpoint", $"'{endpoint}' is not a host and port.", "write it as host:port, e.g. \"redis.internal:6379\"");
        }

        var keyPrefix = String(context, "key_prefix") ?? "stratarec:";
        var database = 0;
        if (context.Settings.TryGetValue("database", out var db) && (db.ValueKind != JsonValueKind.Number || !db.TryGetInt32(out database) || database < 0))
        {
            throw new BindingException("/database", "'database' must be a database number, 0 or more.", "leave it out for database 0 (a cluster has only 0)");
        }

        var tls = false;
        if (context.Settings.TryGetValue("tls", out var t))
        {
            tls = t.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new BindingException("/tls", "'tls' must be true or false.", "write \"tls\": true to connect over TLS"),
            };
        }

        return new RedisCacheStore(new RedisStoreSettings(endpoint, keyPrefix, String(context, "user"), context.SecretReference("password"), tls, database));
    }

    private static string? String(BindingContext context, string setting)
    {
        if (!context.Settings.TryGetValue(setting, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new BindingException("/" + setting, $"'{setting}' must be a non-empty string.");
    }

    private static bool IsEndpoint(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        var host = endpoint.AsSpan(0, colon);
        return host.Length > 0 && (Uri.CheckHostName(host.ToString()) != UriHostNameType.Unknown || IPAddress.TryParse(host.Trim("[]"), out _));
    }
}

/// <summary>Registers <c>redis@1</c>.</summary>
public static class RedisRegistration
{
    /// <summary>Adds <c>redis@1</c> to the distributed cache stores a host's plans may use.</summary>
    public static FeatureCacheStoreRegistry AddRedis(this FeatureCacheStoreRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(new RedisCacheStoreFactory());
    }
}
