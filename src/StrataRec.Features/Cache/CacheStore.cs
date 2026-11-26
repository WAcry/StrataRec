using System.Globalization;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;

namespace StrataRec.Features.Cache;

/// <summary>
/// The keys of one (source, entity) in a distributed cache store: what a store prefixes to every key so that the records
/// of different sources and entities never collide. Built once per table, so a read builds no prefix.
/// </summary>
/// <remarks>
/// <para>The name is <c>{source}:{entity}:</c>. Entity names are identifiers; a source name is escaped (<c>%</c> as
/// <c>%25</c>, <c>:</c> as <c>%3A</c>) so that no source and entity pair can spell another's prefix.</para>
/// <para>A store sees each key as its text (<see cref="KeyText"/>), the same in every process and every run, so instances
/// share records: one part is the value as before keys had parts (an int64 in invariant decimal, a string as is), several
/// parts are their texts joined by <c>:</c>. A key's layout is not in its text: records carry a fingerprint of the layout
/// and the fields, so a record written for another layout whose text happens to match is a miss, never misread.</para>
/// </remarks>
public sealed class CacheKeyspace
{
    /// <summary>A keyspace of one-part keys of <paramref name="keyType"/>.</summary>
    public CacheKeyspace(string source, string entity, EntityKeyType keyType)
        : this(source, entity, EntityKeyLayout.Single(keyType))
    {
    }

    /// <param name="origin">What the source's values come from (<see cref="CacheTableOptions.Origin"/>), which then is
    /// part of the prefix; null for a keyspace whose keys say it themselves.</param>
    public CacheKeyspace(string source, string entity, EntityKeyLayout key, string? origin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(entity);
        ArgumentNullException.ThrowIfNull(key);
        Source = source;
        Entity = entity;
        Key = key;
        Prefix = Escape(source) + ":" + Escape(entity) + ":" + (origin is null ? string.Empty : Escape(origin) + ":");
    }

    public string Source { get; }

    public string Entity { get; }

    /// <summary>Every key of the keyspace has this layout.</summary>
    public EntityKeyLayout Key { get; }

    /// <summary>
    /// <c>{source}:{entity}:</c>, escaped, then <c>{origin}:</c> for a feature source's cache; the store puts its own prefix
    /// (e.g. <c>key_prefix</c>) before it and the key after it.
    /// </summary>
    public string Prefix { get; }

    /// <summary>
    /// The text of a key given by its parts, as a store sees it: for one part, the int64 in invariant decimal or the string
    /// as is (not copied); for several, each part's text in order joined by <c>:</c>, a string part escaped (<c>%</c> as
    /// <c>%25</c>, <c>:</c> as <c>%3A</c>), e.g. <c>10042:US</c>.
    /// </summary>
    /// <remarks>Unambiguous among keys of one layout: no part's text holds a <c>:</c>, so a text splits back into its parts,
    /// and each position has one type. A one-part string is not escaped, as before keys had parts: nothing follows it.</remarks>
    public static string KeyText(ReadOnlySpan<EntityKey> parts)
    {
        if (parts.Length == 1)
        {
            return parts[0].StringValue ?? parts[0].Int64Value.ToString(CultureInfo.InvariantCulture);
        }

        if (parts.IsEmpty)
        {
            throw new ArgumentException("A key has at least one part.", nameof(parts));
        }

        var length = parts.Length - 1;
        Span<char> digits = stackalloc char[20];
        foreach (ref readonly var part in parts)
        {
            if (part.StringValue is { } s)
            {
                length += s.Length + (2 * (s.AsSpan().Count('%') + s.AsSpan().Count(':')));
            }
            else
            {
                part.Int64Value.TryFormat(digits, out var written, provider: CultureInfo.InvariantCulture);
                length += written;
            }
        }

        return string.Create(length, parts, static (span, parts) =>
        {
            var at = 0;
            for (var p = 0; p < parts.Length; p++)
            {
                if (p > 0)
                {
                    span[at++] = ':';
                }

                if (parts[p].StringValue is not { } value)
                {
                    parts[p].Int64Value.TryFormat(span[at..], out var written, provider: CultureInfo.InvariantCulture);
                    at += written;
                    continue;
                }

                foreach (var c in value)
                {
                    if (c is '%' or ':')
                    {
                        span[at++] = '%';
                        span[at++] = c == '%' ? '2' : '3';
                        span[at++] = c == '%' ? '5' : 'A';
                    }
                    else
                    {
                        span[at++] = c;
                    }
                }
            }
        });
    }

    private static string Escape(string name) => name.Replace("%", "%25", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal);
}

/// <summary>One record to write: its key's text (<see cref="CacheKeyspace.KeyText"/>), its bytes (opaque to the store) and
/// how long the store should keep it.</summary>
public readonly record struct CacheStoreEntry(string Key, ReadOnlyMemory<byte> Record, TimeSpan TimeToLive);

/// <summary>
/// A distributed cache shared by a deployment's instances, of a feature source in mode
/// <c>cache</c> or of a service with a <c>cache</c>: the engine reads it for the keys its in-process table misses, before
/// the connector or the service's client, and writes back what they returned. Records are opaque
/// bytes the engine encodes (with their write time and a fingerprint of the field set); the store only moves them.
/// <c>redis@1</c> (package StrataRec.Redis) is one; a host can plug in another distributed cache.
/// </summary>
/// <remarks>
/// <para>A store is a binding instance: the whole lifetime contract (create, share, load, dispose) is on
/// <see cref="BindingContext"/>. Connect in <see cref="ILoadable.LoadAsync"/>, release the connection when disposed.</para>
/// <para>The store is only an optimisation. A read that throws, or that the engine stops waiting for at the store's
/// <c>timeout_ms</c>, sends its keys on to the connector or the client, while the fetch's or the call's bound (and so the
/// request's deadline) leaves time to send them; a read cut off by that bound sends nothing on, as the connector or the
/// client would be cut off too. A write that fails is counted and forgotten. So a store needs no retries of its own and should fail fast.</para>
/// <para>Implementations must be thread-safe: every request of every plan sharing the instance reads it concurrently.</para>
/// </remarks>
public interface ICacheStore
{
    /// <summary>Store type and version, e.g. <c>redis@1</c>.</summary>
    string StoreType { get; }

    /// <summary>
    /// Reads the records of <paramref name="keys"/> in one round trip: <c>records[i]</c> receives the bytes stored for
    /// <c>keys[i]</c>, or stays empty when the store has none. Throws when the read failed. A key is its text
    /// (<see cref="CacheKeyspace.KeyText"/>), stored under the keyspace's <see cref="CacheKeyspace.Prefix"/>.
    /// </summary>
    /// <remarks>
    /// <para>Called inline on the request's thread, like a connector's fetch: the synchronous part (up to the first
    /// asynchronous wait) runs there and the engine does not bound it; only the returned task is waited for, within the
    /// tier's <c>timeout_ms</c> and the source's budget. So send the request and return: do the I/O asynchronously, as
    /// <c>redis@1</c> does, and never block before returning. An answer the engine first finds complete after the tier's
    /// time or the source's deadline is late, however it arrived: its records are not used, and its keys go to the
    /// connector if the source has time left. Chunks not sent yet when the tier's time is up are not sent.</para>
    /// <para><paramref name="keys"/> and <paramref name="records"/> are the request's pooled buffers: valid until the returned
    /// task completes, also when the engine stopped waiting for it earlier, and not after. Write nothing into
    /// <paramref name="records"/> after completing; the bytes it points at must stay unchanged (a fresh array per value, or
    /// memory the store never reuses).</para>
    /// </remarks>
    ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken);

    /// <summary>Writes records, each with its time to live, in one round trip; off the request path. Throws when the write failed.</summary>
    ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken);
}
