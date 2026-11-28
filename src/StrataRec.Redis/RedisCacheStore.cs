using System.Globalization;
using System.Net;
using StackExchange.Redis;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Features.Cache;

namespace StrataRec.Redis;

/// <remarks>
/// <para>The connection is made at load, but a server that is down then does not fail the load: the plan serves from
/// the connector (or the service) while the client reconnects in the background, and commands sent while it is
/// disconnected fail at once rather than queue. Credentials the server refuses (a wrong password or user, or none where
/// the server requires one) and a missing secret do fail the load (<see cref="LoadAsync"/> says how they are told
/// apart).</para>
/// </remarks>
public sealed class RedisCacheStore : ICacheStore, ILoadable, IAsyncDisposable
{
    public const string Type = "redis@1";

    private const int CommandTimeoutMs = 1000;

    /// <summary>
    /// Connects that must succeed, at most, before the server is taken for down (see <see cref="LoadAsync"/>): a second
    /// one tells a refusal from a connection the server, or the network, dropped before the handshake.
    /// </summary>
    private const int StrictConnects = 2;

    private readonly RedisStoreSettings _settings;
    private readonly Func<ConfigurationOptions, Task<ConnectionMultiplexer>> _connect;
    private Task<ConnectionMultiplexer>? _connecting;
    private IDatabase? _database;
    private volatile bool _cluster;

    internal RedisCacheStore(RedisStoreSettings settings, Func<ConfigurationOptions, Task<ConnectionMultiplexer>>? connect = null)
    {
        _settings = settings;
        _connect = connect ?? (static options => ConnectionMultiplexer.ConnectAsync(options));
    }

    public string StoreType => Type;

    /// <summary>Resolves the password and connects. A server that cannot be reached does not fail the load.</summary>
    /// <remarks>
    /// <para>A server that drops two connections before it answers their handshakes and refuses the third is the one
    /// case not told from a server that is down; it is then served as one: from the connector.</para>
    /// </remarks>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        var options = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            BacklogPolicy = BacklogPolicy.FailFast,
            ClientName = "stratarec",
            ConnectTimeout = 2000,

            // One attempt per connect: a connection that did not come up keeps reconnecting in the background
            // (AbortOnConnectFail is false), so the client's default of three only triples how long a load waits on a
            // server that does not answer, and how long a disposal waits for a cancelled load's connection.
            ConnectRetry = 1,
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

        ConnectionMultiplexer? connection = null;
        for (var attempt = 0; attempt < StrictConnects && connection is null; attempt++)
        {
            var strict = options.Clone();
            strict.AbortOnConnectFail = true;
            connection = await ConnectAsync(strict, cancellationToken).ConfigureAwait(false);
        }

        connection ??= (await ConnectAsync(options, cancellationToken).ConfigureAwait(false))!;
        if (await CommandRefusalAsync(connection).ConfigureAwait(false) is { } refused)
        {
            throw refused; // the connection is disposed with the store, which the host disposes after a failed load
        }

        connection.ConnectionRestored += (_, _) => _cluster = IsCluster(connection);
        _database = connection.GetDatabase(_settings.Database);
        _cluster = IsCluster(connection);
    }

    /// <remarks>
    /// The connect itself cannot be cancelled: a cancelled load stops waiting for it, and the store keeps it, so that its
    /// disposal waits for a connection that completes later and disposes it. The store keeps the latest one:
    /// the load makes no other after a cancellation, and a connect that failed made no connection.
    /// </remarks>
    private async Task<ConnectionMultiplexer?> ConnectAsync(ConfigurationOptions options, CancellationToken cancellationToken)
    {
        var connecting = _connect(options);
        Volatile.Write(ref _connecting, connecting);
        try
        {
            return await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (options.AbortOnConnectFail && !cancellationToken.IsCancellationRequested)
        {
            return RefusalIn(ex) is { } refused ? throw refused : null;
        }
    }

    /// <summary>
    /// Why the server refuses a connection, if it does: with a <c>user</c>, <c>ACL WHOAMI</c> (a server whose default
    /// user needs no password accepts a connection whose <c>AUTH</c> failed); otherwise a <c>PING</c> answered
    /// <c>NOAUTH</c>. Anything else (down, or cannot tell) is no refusal.
    /// </summary>
    private async Task<BindingException?> CommandRefusalAsync(ConnectionMultiplexer connection)
    {
        var database = connection.GetDatabase();
        try
        {
            if (_settings.User is { } user && connection.IsConnected)
            {
                return (string?)await database.ExecuteAsync("ACL", "WHOAMI").ConfigureAwait(false) == user ? null : CredentialsRefused();
            }

            await database.PingAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return RefusalIn(ex);
        }
    }

    /// <summary>
    /// The refusal a failure reports, if any: a failed TLS handshake (first: the client reports it as an authentication
    /// failure too), or refused credentials.
    /// </summary>
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

    private static bool IsRefusal(Exception failure) => Find(failure, static e =>
        e is RedisConnectionException { FailureType: ConnectionFailureType.AuthenticationFailure }
        || (e is RedisServerException server
            && (server.Message.StartsWith("NOAUTH", StringComparison.Ordinal) || server.Message.StartsWith("WRONGPASS", StringComparison.Ordinal)))) is not null;

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
        CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
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

    private static Task<bool>[] Send(IDatabase database, string prefix, ReadOnlySpan<CacheStoreEntry> entries)
    {
        var batch = database.CreateBatch();
        var sets = new Task<bool>[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            sets[i] = batch.StringSetAsync(prefix + entries[i].Key, entries[i].Record, entries[i].TimeToLive, When.Always);
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
            return;
        }

        await connection.CloseAsync(allowCommandsToComplete: false).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private RedisKey[] Keys(CacheKeyspace keyspace, ReadOnlySpan<string> keys)
    {
        var prefix = _settings.KeyPrefix + keyspace.Prefix;
        var redisKeys = new RedisKey[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            redisKeys[i] = prefix + keys[i];
        }

        return redisKeys;
    }

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

/// <summary>Settings of <c>redis@1</c>: the members of <c>cache.distributed</c> besides <c>store</c> and <c>timeout_ms</c>.</summary>
public sealed class RedisSettings
{
    /// <summary>The server as host:port, e.g. <c>redis.internal:6379</c>; for a cluster, any of its nodes.</summary>
    public required string Endpoint { get; init; }

    /// <summary>What every key starts with (default <c>stratarec:</c>).</summary>
    public string KeyPrefix { get; init; } = "stratarec:";

    /// <summary>The ACL user to authenticate as.</summary>
    public string? User { get; init; }

    /// <summary>The password, as a reference to the host's secret store (<c>secret://name</c>), resolved at load.</summary>
    public SecretReference? Password { get; init; }

    /// <summary>Connect over TLS (default false).</summary>
    public bool Tls { get; init; }

    /// <summary>The database number (default 0; a cluster has only 0).</summary>
    public int Database { get; init; }
}

/// <summary>Creates <see cref="RedisCacheStore"/>s; register it with <see cref="RedisRegistration.AddRedis"/>.</summary>
public sealed class RedisCacheStoreFactory : BindingFactory<ICacheStore, RedisSettings>
{
    public override string Type => RedisCacheStore.Type;

    protected override ICacheStore Create(RedisSettings settings, BindingContext context)
    {
        if (!IsEndpoint(settings.Endpoint))
        {
            throw new BindingException("/endpoint", $"'{settings.Endpoint}' is not a host and port.", "write it as host:port, e.g. \"redis.internal:6379\"");
        }

        if (settings.KeyPrefix.Length == 0)
        {
            throw new BindingException("/key_prefix", "'key_prefix' must not be empty.", "leave it out for the prefix 'stratarec:'");
        }

        if (settings.User is { Length: 0 })
        {
            throw new BindingException("/user", "'user' must not be empty.", "name the ACL user, or leave it out for the default user");
        }

        if (settings.Database < 0)
        {
            throw new BindingException("/database", "'database' must be a database number, 0 or more.", "leave it out for database 0 (a cluster has only 0)");
        }

        return new RedisCacheStore(new RedisStoreSettings(settings.Endpoint, settings.KeyPrefix, settings.User, settings.Password, settings.Tls, settings.Database));
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
    /// <summary>Adds <c>redis@1</c> to the binding types a host's plans may use, for distributed caches.</summary>
    public static BindingRegistry AddRedis(this BindingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(new RedisCacheStoreFactory());
    }
}
