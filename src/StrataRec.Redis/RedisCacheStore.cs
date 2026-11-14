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
/// commands sent while it is disconnected fail at once rather than queue. Credentials the server refuses (a wrong password
/// or user, or none where the server requires one) and a missing secret do fail the load: they are a wrong setting, which
/// reconnecting does not fix. Commands time out after one second; the engine stops waiting for a read much earlier (the tier's
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
        if (await RefusalAsync(connection, options, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            throw refused; // the connection is disposed with the store, which the host disposes after a failed load
        }

        connection.ConnectionRestored += (_, _) => _cluster = IsCluster(connection);
        _database = connection.GetDatabase(_settings.Database);
        _cluster = IsCluster(connection);
    }

    /// <summary>
    /// Why the server refuses this store, if it does: its credentials, or (with <c>tls</c>) the TLS handshake. Any
    /// other failure is the server being down, which does not fail the load.
    /// </summary>
    /// <remarks>
    /// <para>Connected, one command tells (<see cref="CommandRefusedAsync"/>), since it reaches the server.</para>
    /// <para>Not connected, the client's failure does not tell. StackExchange.Redis (2.13) tells the connect that its
    /// connection ended before it publishes the refusal that ended it. It also keeps a connection's first failure, so a
    /// dropped first connection hides a later refusal. A command failing fast then carries a plain connection failure,
    /// and a load against a server that refuses the credentials succeeded (CI once; reproduced deterministically by an
    /// investigation). A connect that must succeed reports the refusal it saw, read when it gives up. So one is tried,
    /// once, with the store's settings. It costs a second connect timeout only when the server is down.</para>
    /// </remarks>
    private async Task<BindingException?> RefusalAsync(ConnectionMultiplexer connection, ConfigurationOptions options, CancellationToken cancellationToken)
    {
        if (connection.IsConnected)
        {
            return await CommandRefusedAsync(connection, _settings.User).ConfigureAwait(false) ? CredentialsRefused() : null;
        }

        var probe = options.Clone();
        probe.AbortOnConnectFail = true;
        probe.ConnectRetry = 1;
        var probing = _connect(probe);
        ConnectionMultiplexer probed;
        try
        {
            probed = await probing.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = DisposeOnceConnectedAsync(probing);
            throw;
        }
        catch (Exception ex)
        {
            return RefusalIn(ex);
        }

        await using (probed.ConfigureAwait(false))
        {
            return await CommandRefusedAsync(probed, _settings.User).ConfigureAwait(false) ? CredentialsRefused() : null;
        }
    }

    /// <summary>
    /// Whether the server refused a connection's credentials, by one command:
    /// <list type="bullet">
    /// <item>With a <paramref name="user"/> and connected: <c>ACL WHOAMI</c>. A server whose default user needs no
    /// password accepts the connection even when it refused the user's credentials (<c>AUTH</c> failed, the connection
    /// stays the default user's). A server that cannot answer it (before Redis 6, or a user not allowed to ask) is
    /// trusted.</item>
    /// <item>Otherwise: a <c>PING</c>, answered <c>NOAUTH</c> by a server that requires a password.</item>
    /// </list>
    /// </summary>
    private static async Task<bool> CommandRefusedAsync(ConnectionMultiplexer connection, string? user)
    {
        var database = connection.GetDatabase();
        try
        {
            if (user is not null && connection.IsConnected)
            {
                return (string?)await database.ExecuteAsync("ACL", "WHOAMI").ConfigureAwait(false) != user;
            }

            await database.PingAsync().ConfigureAwait(false);
            return false; // the server answers this connection
        }
        catch (Exception ex)
        {
            return IsRefusal(ex); // anything else: as if the server were down, or (WHOAMI) cannot tell
        }
    }

    /// <summary>The refusal a failed connect reports, if any: a failed TLS handshake, or refused credentials.</summary>
    private BindingException? RefusalIn(Exception failure)
    {
        if (Find(failure, static e => e is System.Security.Authentication.AuthenticationException) is { } tls)
        {
            return new BindingException(
                "/tls",
                $"The TLS handshake with the Redis server at {_settings.Endpoint} failed: {tls.Message}",
                "the server's certificate must be trusted by this host and name the endpoint's host; set \"tls\": false if the server does not use TLS");
        }

        return IsRefusal(failure) ? CredentialsRefused() : null;
    }

    private BindingException CredentialsRefused() => new(
        _settings.Password?.Pointer ?? (_settings.User is null ? string.Empty : "/user"),
        $"The Redis server at {_settings.Endpoint} refused the credentials" + (_settings.User is null ? "." : $" of user '{_settings.User}'."),
        _settings.Password is { } secret
            ? $"check the secret '{secret.Name}'" + (_settings.User is null ? string.Empty : " and the user") + ": the server does not accept them"
            : "the server requires a password: add \"password\": \"secret://<name>\"");

    /// <summary>
    /// Whether a failure, anywhere in its tree (inner exceptions, and every branch of an aggregate), is the server
    /// refusing the credentials: the client's authentication failure, or the server's own <c>NOAUTH</c> /
    /// <c>WRONGPASS</c> reply.
    /// </summary>
    private static bool IsRefusal(Exception failure) => Find(failure, static e =>
        e is RedisConnectionException { FailureType: ConnectionFailureType.AuthenticationFailure }
        || (e is RedisServerException server
            && (server.Message.StartsWith("NOAUTH", StringComparison.Ordinal) || server.Message.StartsWith("WRONGPASS", StringComparison.Ordinal)))) is not null;

    /// <summary>The first exception of <paramref name="failure"/>'s tree that matches, depth first.</summary>
    private static Exception? Find(Exception? failure, Func<Exception, bool> match)
    {
        for (; failure is not null; failure = failure.InnerException)
        {
            if (match(failure))
            {
                return failure;
            }

            if (failure is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find(inner, match) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            }
        }

        return null;
    }

    /// <summary>A connect the load stopped waiting for: disposed once it completes, if it connected.</summary>
    private static async Task DisposeOnceConnectedAsync(Task<ConnectionMultiplexer> connecting)
    {
        try
        {
            await (await connecting.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // It did not connect, or its disposal failed: nothing is left to release.
        }
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
