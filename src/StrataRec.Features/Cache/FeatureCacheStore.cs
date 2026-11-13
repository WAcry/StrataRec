using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;

namespace StrataRec.Features.Cache;

/// <summary>
/// The keys of one (source, entity) in a distributed cache store: what a store prefixes to every key so that the records
/// of different sources and entities never collide. Built once per table, so a read builds no prefix.
/// </summary>
/// <remarks>The name is <c>{source}:{entity}:</c>. Entity names are identifiers; a source name is escaped (<c>%</c> as
/// <c>%25</c>, <c>:</c> as <c>%3A</c>) so that no source and entity pair can spell another's prefix.</remarks>
public sealed class CacheKeyspace
{
    public CacheKeyspace(string source, string entity, EntityKeyType keyType)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(entity);
        Source = source;
        Entity = entity;
        KeyType = keyType;
        Prefix = Escape(source) + ":" + Escape(entity) + ":";
    }

    public string Source { get; }

    public string Entity { get; }

    /// <summary>Every key of the keyspace has this type.</summary>
    public EntityKeyType KeyType { get; }

    /// <summary><c>{source}:{entity}:</c>, escaped; the store puts its own prefix (e.g. <c>key_prefix</c>) before it and the key after it.</summary>
    public string Prefix { get; }

    private static string Escape(string name) => name.Replace("%", "%25", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal);
}

/// <summary>One record to write: its key, its bytes (opaque to the store) and how long the store should keep it.</summary>
public readonly record struct CacheStoreEntry(EntityKey Key, ReadOnlyMemory<byte> Record, TimeSpan TimeToLive);

/// <summary>
/// A distributed cache shared by a service's instances (design 6.6 <c>cache</c>, tier 2): the engine reads it for the keys
/// its in-process table misses, before the connector, and writes back what the connector returned. Records are opaque
/// bytes the engine encodes (with their write time and a fingerprint of the field set); the store only moves them.
/// <c>redis@1</c> (package StrataRec.Redis) is one; a host can plug in another distributed cache.
/// </summary>
/// <remarks>
/// <para>A store is a binding instance: the whole lifetime contract (create, share, load, dispose) is on
/// <see cref="BindingContext"/>. Connect in <see cref="ILoadable.LoadAsync"/>, release the connection when disposed.</para>
/// <para>The store is only an optimisation. A read that throws, or that the engine stops waiting for (the store's
/// <c>timeout_ms</c>, the source's budget, the request's deadline), sends its keys to the connector; a write that fails is
/// counted and forgotten. So a store needs no retries of its own and should fail fast.</para>
/// <para>Implementations must be thread-safe: every request of every plan sharing the instance reads it concurrently.</para>
/// </remarks>
public interface IFeatureCacheStore
{
    /// <summary>Store type and version, e.g. <c>redis@1</c>.</summary>
    string StoreType { get; }

    /// <summary>
    /// Reads the records of <paramref name="keys"/> in one round trip: <c>records[i]</c> receives the bytes stored for
    /// <c>keys[i]</c>, or stays empty when the store has none. Throws when the read failed.
    /// </summary>
    /// <remarks>
    /// <paramref name="keys"/> and <paramref name="records"/> are the request's pooled buffers: valid until the returned
    /// task completes, also when the engine stopped waiting for it earlier, and not after. Write nothing into
    /// <paramref name="records"/> after completing; the bytes it points at must stay unchanged (a fresh array per value, or
    /// memory the store never reuses).
    /// </remarks>
    ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<EntityKey> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken);

    /// <summary>Writes records, each with its time to live, in one round trip; off the request path. Throws when the write failed.</summary>
    ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken);
}

/// <summary>Creates distributed cache stores for bindings of one store type (the counterpart of <see cref="Connectors.IFeatureConnectorFactory"/>).</summary>
/// <remarks><see cref="Create"/> only reads and checks the settings (see <see cref="BindingContext"/>).</remarks>
public interface IFeatureCacheStoreFactory
{
    string StoreType { get; }

    /// <param name="context">The source's logical name, the store's own settings (the members of <c>cache.distributed</c> other
    /// than <c>store</c> and <c>timeout_ms</c>), the base directory and the host's secret store.</param>
    /// <exception cref="BindingException">A setting is wrong: at its pointer within <c>cache.distributed</c>, with a hint.</exception>
    IFeatureCacheStore Create(BindingContext context);
}

/// <summary>Distributed cache store factories available in this binary, keyed by type (<c>name@version</c>). Empty by
/// default: the core packages carry no store; StrataRec.Redis adds <c>redis@1</c>.</summary>
public sealed class FeatureCacheStoreRegistry
{
    private readonly Dictionary<string, IFeatureCacheStoreFactory> _factories = new(StringComparer.Ordinal);

    public IEnumerable<string> Types
    {
        get
        {
            var types = new List<string>(_factories.Keys);
            types.Sort(StringComparer.Ordinal);
            return types;
        }
    }

    public FeatureCacheStoreRegistry Add(IFeatureCacheStoreFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[factory.StoreType] = factory;
        return this;
    }

    public bool TryGet(string type, out IFeatureCacheStoreFactory factory) => _factories.TryGetValue(type, out factory!);
}
