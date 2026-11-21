using StrataRec.Artifacts.Model;
using StrataRec.Features.Bindings;

namespace StrataRec.Features.Cache;

/// <summary>
/// Whose cache a binding's <c>cache</c> settings are, as messages name it: a feature source in front of its connector, or a
/// service.
/// </summary>
/// <param name="StoreKind">The binding kind of the distributed store.</param>
/// <param name="Name">The source's or the service's name.</param>
/// <param name="Noun"><c>source</c> or <c>service</c>.</param>
/// <param name="Origin">What the cache is in front of: <c>connector</c>, <c>service</c>.</param>
internal readonly record struct CacheOwner(BindingKind StoreKind, string Name, string Noun, string Origin);

/// <summary>
/// The one reading of a <c>cache</c> binding (design 6.6): its TTL, its stale window and its distributed tier, each problem
/// reported at its setting. Feature sources and services read their caches here, so the two say the same about the same
/// mistake.
/// </summary>
internal static class CacheSettings
{
    /// <summary>The longest <c>ttl_ms</c> and <c>stale_if_error_ms</c>: 2^61 ms, so their sums cannot overflow.</summary>
    public const long MaxMs = 1L << 61;

    /// <param name="pointer">The <c>cache</c> member's pointer.</param>
    /// <param name="ownerTimeoutMs">The owner's <c>timeout_ms</c>: the distributed tier's default is half of it; with none,
    /// the tier must give its own.</param>
    public static bool TryRead(
        CacheBinding cache, CacheOwner owner, BindingResolver resolver, string pointer, int? ownerTimeoutMs,
        out long ttlMs, out long staleMs, out DistributedCacheTier? distributed, out int? distributedTimeoutMs)
    {
        var diagnostics = resolver.Diagnostics;
        var artifact = resolver.Artifact;
        var ok = true;
        ttlMs = 0;
        staleMs = 0;
        distributed = null;
        distributedTimeoutMs = null;
        if (cache.TtlMs is not { } ttl)
        {
            ok = false;
            diagnostics.Error("CACHE_TTL", artifact, pointer, $"The cache of {owner.Noun} '{owner.Name}' has no 'ttl_ms'.",
                "add how long a fetched value is served, in milliseconds, e.g. \"ttl_ms\": 60000");
        }
        else if (ttl is <= 0 or > MaxMs)
        {
            ok = false;
            diagnostics.Error("CACHE_TTL", artifact, pointer + "/ttl_ms", $"ttl_ms {ttl} is not between 1 and 2^61 milliseconds.",
                "give how long a fetched value is served, e.g. \"ttl_ms\": 60000");
        }
        else
        {
            ttlMs = ttl;
        }

        if (cache.StaleIfErrorMs is { } stale && stale is < 0 or > MaxMs)
        {
            ok = false;
            diagnostics.Error("CACHE_STALE_IF_ERROR", artifact, pointer + "/stale_if_error_ms",
                $"stale_if_error_ms {stale} is not between 0 and 2^61 milliseconds.",
                $"give how much longer an expired value may serve when the {owner.Origin} fails, e.g. 300000, or 0");
        }
        else
        {
            staleMs = cache.StaleIfErrorMs ?? 0;
        }

        if (cache.Distributed is { } shared)
        {
            var at = pointer + "/distributed";
            if (shared.TimeoutMs is <= 0)
            {
                ok = false;
                diagnostics.Error("DISTRIBUTED_CACHE_CONFIG", artifact, at + "/timeout_ms", $"timeout_ms {shared.TimeoutMs} is not positive.",
                    $"give the longest a request waits for the distributed cache, in milliseconds, or leave it out (half the {owner.Noun}'s timeout_ms)");
            }
            else if (shared.TimeoutMs is null && ownerTimeoutMs is null)
            {
                ok = false;
                diagnostics.Error("DISTRIBUTED_CACHE_CONFIG", artifact, at,
                    $"The distributed cache of {owner.Noun} '{owner.Name}' has no 'timeout_ms', and the {owner.Noun} has none to take half of.",
                    "give the longest a request waits for the distributed cache, e.g. \"timeout_ms\": 10");
            }

            distributedTimeoutMs = shared.TimeoutMs;
            distributed = resolver.Acquire<ICacheStore, DistributedCacheTier>(
                owner.StoreKind, owner.Name, shared.Store, shared.Settings, static store => new DistributedCacheTier(store), StoreHint(shared.Store));
            ok &= distributed is not null;
            ok &= TryRetention(owner, ttlMs, staleMs, pointer, resolver);
        }

        return ok;
    }

    /// <summary>
    /// Whether the distributed tier can be given the longest time to live a record of this binding needs (the TTL with all
    /// its jitter, plus the stale window): a <see cref="TimeSpan"/>. Diagnosed at the setting that takes it past.
    /// </summary>
    private static bool TryRetention(CacheOwner owner, long ttlMs, long staleMs, string pointer, BindingResolver resolver)
    {
        if (ttlMs == 0 || LocalTableCache.RetentionMs(ttlMs, staleMs) <= LocalTableCache.MaxRetentionMs)
        {
            return true; // a ttl_ms that is missing or wrong is diagnosed already
        }

        var setting = LocalTableCache.MaxLifetime(ttlMs) > LocalTableCache.MaxRetentionMs ? "ttl_ms" : "stale_if_error_ms";
        resolver.Diagnostics.Error("CACHE_RETENTION", resolver.Artifact, pointer + "/" + setting,
            $"The distributed cache of {owner.Noun} '{owner.Name}' would keep a record for up to {LocalTableCache.RetentionMs(ttlMs, staleMs)} ms "
            + $"(ttl_ms with its 10% jitter, plus stale_if_error_ms), more than the {LocalTableCache.MaxRetentionMs} ms a time to live can be.",
            $"lower ttl_ms or stale_if_error_ms so that ttl_ms × 1.1 + stale_if_error_ms is at most {LocalTableCache.MaxRetentionMs} (about 29,000 years)");
        return false;
    }

    /// <summary>What to do about a store type the binary does not have, besides the types it has: where <c>redis@1</c> comes from.</summary>
    private static string StoreHint(string? type) =>
        type is not null && type.StartsWith("redis@", StringComparison.Ordinal)
            ? "redis@1 is in the package StrataRec.Redis: the host adds it with PlanEnvironment.Bindings.AddRedis()"
            : "or remove 'distributed' to cache in process only";
}
