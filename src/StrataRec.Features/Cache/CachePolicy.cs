using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Cache;

/// <summary>
/// How long a cache's records are served: <c>ttl_ms</c> and <c>stale_if_error_ms</c> of one binding as a plan compiled it.
/// A reader judges a record by its own plan's policy, from the record's write time, so the tiers hold records and no
/// policy: two plans that share a table or a store may keep different times, and a binding that changes its times keeps
/// its records.
/// </summary>
/// <remarks>Each key's lifetime is the TTL ±10% by the key's hash, the same on every instance, so records written
/// together do not all expire together.</remarks>
public sealed class CachePolicy
{
    /// <summary>
    /// The longest time to live a distributed tier is given, in milliseconds: what a <see cref="TimeSpan"/> holds. A
    /// binding with a distributed tier whose <see cref="RetentionOf"/> exceeds it is rejected.
    /// </summary>
    internal const long MaxRetentionMs = long.MaxValue / TimeSpan.TicksPerMillisecond;

    private const double Jitter = 0.1;

    /// <summary>
    /// The policy by which no record is ever past its time (the longest <c>ttl_ms</c>). The table of a pipeline's
    /// results, whose calling nodes each judge by their own times; and a cache with no tier, whose records are the
    /// request's that got them and are gone with it.
    /// </summary>
    internal static CachePolicy Unlimited { get; } = new(CacheSettings.MaxMs);

    public CachePolicy(long ttlMs, long staleIfErrorMs = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttlMs);
        ArgumentOutOfRangeException.ThrowIfNegative(staleIfErrorMs);
        if (ttlMs > CacheSettings.MaxMs || staleIfErrorMs > CacheSettings.MaxMs)
        {
            throw new ArgumentOutOfRangeException(nameof(ttlMs), "ttl_ms and stale_if_error_ms are at most 2^61 ms.");
        }

        TtlMs = ttlMs;
        StaleIfErrorMs = staleIfErrorMs;
        MinLifetimeMs = LifetimeMs(0UL);
        MaxLifetimeMs = MaxLifetime(ttlMs);
    }

    /// <summary><c>cache.ttl_ms</c>: how long a value is served. Each key's lifetime is this ±10%, by the key's hash.</summary>
    public long TtlMs { get; }

    /// <summary><c>cache.stale_if_error_ms</c>: how much longer an expired value may be served when the origin fails.</summary>
    public long StaleIfErrorMs { get; }

    internal long MinLifetimeMs { get; }

    /// <summary>The longest lifetime of any key: the TTL plus the most jitter.</summary>
    public long MaxLifetimeMs { get; }

    /// <summary>How long after its write no reader of this policy could use a record, even stale.</summary>
    public long RetentionMs => MaxLifetimeMs + StaleIfErrorMs;

    internal static long MaxLifetime(long ttlMs) => (long)Math.Ceiling(ttlMs * (1 + Jitter));

    internal static long RetentionOf(long ttlMs, long staleIfErrorMs) => MaxLifetime(ttlMs) + staleIfErrorMs;

    internal long LifetimeMs(in EntityKey key) => LifetimeMs(LocalTableCache.KeyHash(key));

    internal long LifetimeMs(ulong keyHash) => (long)(TtlMs * (1 - Jitter + (2 * Jitter * StableHash.ToUnit(keyHash))));

    internal CacheFreshness Classify(long writtenMs, in EntityKey key, long nowMs) => Classify(writtenMs, LocalTableCache.KeyHash(key), nowMs);

    internal CacheFreshness Classify(long writtenMs, in EntityKeyBatch keys, int index, long nowMs)
    {
        var age = AgeMs(writtenMs, nowMs);
        if (age <= MinLifetimeMs)
        {
            return CacheFreshness.Fresh;
        }

        if (age > MaxLifetimeMs)
        {
            if (age > MaxLifetimeMs + StaleIfErrorMs)
            {
                return CacheFreshness.Expired;
            }

            if (age <= MinLifetimeMs + StaleIfErrorMs)
            {
                return CacheFreshness.Stale;
            }
        }

        return ClassifyAge(age, LocalTableCache.KeyHash(keys, index));
    }

    /// <remarks>A write time ahead of the clock (another instance's clock runs ahead) counts as written now.</remarks>
    internal CacheFreshness Classify(long writtenMs, ulong keyHash, long nowMs) =>
        ClassifyAge(AgeMs(writtenMs, nowMs), keyHash);

    /// <remarks>
    /// The age is judged as <see cref="Classify(long, ulong, long)"/> judges it: a write time ahead of the clock (the
    /// clock stepped back during the fetch, say) counts as written now, so the result is at most
    /// <see cref="RetentionMs"/>, which the binding checked against <see cref="MaxRetentionMs"/>. In ticks, so no
    /// floating-point rounding takes the boundary past what a <see cref="TimeSpan"/> holds.
    /// </remarks>
    internal TimeSpan StoreTimeToLive(long writtenMs, long nowMs)
    {
        var retention = RetentionMs;
        var age = AgeMs(writtenMs, nowMs);
        var remaining = age >= retention ? 1 : retention - age;
        return TimeSpan.FromTicks(remaining * TimeSpan.TicksPerMillisecond);
    }

    private static long AgeMs(long writtenMs, long nowMs)
    {
        if (writtenMs >= nowMs)
        {
            return 0;
        }

        // A stored timestamp can span the entire signed range. A past record that overflows the difference is expired.
        var age = unchecked(nowMs - writtenMs);
        return age < 0 ? long.MaxValue : age;
    }

    private CacheFreshness ClassifyAge(long age, ulong keyHash)
    {
        var lifetime = LifetimeMs(keyHash);
        return age <= lifetime ? CacheFreshness.Fresh : age <= lifetime + StaleIfErrorMs ? CacheFreshness.Stale : CacheFreshness.Expired;
    }
}
