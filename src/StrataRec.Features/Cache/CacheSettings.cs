using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Features.Bindings;
using StrataRec.Features.Local;

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

/// <summary>What <see cref="CacheSettings.TryRead"/> read of a <c>cache</c> binding.</summary>
/// <param name="Policy">Its times.</param>
/// <param name="Version"><c>version</c>; null when the cache follows the configuration.</param>
/// <param name="MemoryBytes"><c>memory.max_bytes</c>; null without an in-process tier.</param>
/// <param name="Distributed">The distributed tier; null without one.</param>
/// <param name="DistributedTimeoutMs">The tier's own <c>timeout_ms</c>; null takes half the owner's.</param>
internal readonly record struct CacheSpec(CachePolicy Policy, string? Version, long? MemoryBytes, DistributedCacheTier? Distributed, int? DistributedTimeoutMs)
{
    /// <summary>
    /// Whether a tier keeps the cache's records. A cache with no tier (<c>"cache": {}</c>) has the reads of one request
    /// share what one of them got from the origin, and nothing more.
    /// </summary>
    public bool Kept => MemoryBytes is not null || Distributed is not null;

    /// <summary>
    /// What a cache's records come from, in its keys and its table's identity: the binding's <c>version</c> when it names
    /// one (<c>v.3</c>: no digest has a dot), otherwise <paramref name="derived"/>, a digest of the configuration that
    /// decides the values.
    /// </summary>
    public string Origin(string derived) => Version is null ? derived : "v." + Version;

    /// <summary>The table of the cache's in-process tier, or the empty one a cache without that tier has.</summary>
    public LocalFeatureTableOptions TableOptions(TimeProvider clock, string? origin) => new()
    {
        MaxBytes = MemoryBytes ?? long.MaxValue,
        Cache = new CacheTableOptions
        {
            TtlMs = Policy.TtlMs, StaleIfErrorMs = Policy.StaleIfErrorMs, Clock = clock, Origin = origin, Memory = MemoryBytes is not null, Kept = Kept,
        },
    };
}

/// <summary>
/// The one reading of a cache's settings: its times, its version and its tiers, each problem reported at its setting.
/// Feature sources, services and pipeline calls read their caches here, so they say the same about the same mistake.
/// A cache with tiers says how long they serve a value; one with no setting at all keeps nothing, and only has the
/// reads of one request share a value (<see cref="CacheSpec.Kept"/>); anything between is a mistake.
/// </summary>
internal static class CacheSettings
{
    /// <summary>The longest <c>ttl_ms</c> and <c>stale_if_error_ms</c>: 2^61 ms, so their sums cannot overflow.</summary>
    public const long MaxMs = 1L << 61;

    /// <param name="pointer">The <c>cache</c> member's pointer.</param>
    /// <param name="ownerTimeoutMs">The owner's <c>timeout_ms</c>: the distributed tier's default is half of it; with none,
    /// the tier must give its own.</param>
    public static bool TryRead(CacheBinding cache, CacheOwner owner, BindingResolver resolver, string pointer, int? ownerTimeoutMs, out CacheSpec spec)
    {
        var diagnostics = resolver.Diagnostics;
        var artifact = resolver.Artifact;
        var ok = true;
        long ttlMs = 0;
        spec = default;
        if (!HasTier(cache))
        {
            if (Unkept(cache.TtlMs, cache.StaleIfErrorMs, cache.Version) is { } given)
            {
                // One error, whatever the settings' values: whether they apply at all is what there is to decide first.
                diagnostics.Error("CACHE_TIERS", artifact, pointer,
                    $"The cache of {owner.Noun} '{owner.Name}' gives {given} but neither a 'memory' nor a 'distributed' tier: nothing would keep its values.",
                    "add \"memory\": { \"max_bytes\": \"64MiB\" } to keep them in process, or \"distributed\": { \"store\": \"redis@1\", ... } to keep them in a store the instances share; "
                    + $"or remove {given}: a cache with no settings keeps nothing, and has the reads of one request share a value");
                return false;
            }

            spec = new CacheSpec(CachePolicy.Unlimited, null, null, null, null);
            return true;
        }

        if (cache.TtlMs is not { } ttl)
        {
            ok = false;
            diagnostics.Error("CACHE_TTL", artifact, pointer, $"The cache of {owner.Noun} '{owner.Name}' has no 'ttl_ms'.",
                "add how long a fetched value is served, in milliseconds, e.g. \"ttl_ms\": 60000");
        }
        else if (!TryTtl(ttl, diagnostics, artifact, pointer))
        {
            ok = false;
        }
        else
        {
            ttlMs = ttl;
        }

        ok &= TryStale(cache.StaleIfErrorMs, owner.Origin, diagnostics, artifact, pointer, out var staleMs);
        ok &= TryVersion(cache.Version, diagnostics, artifact, pointer);
        ok &= TryTiers(cache.Memory, cache.Distributed, owner, resolver, pointer, ownerTimeoutMs, out var memoryBytes, out var distributed, out var distributedTimeoutMs);
        ok &= distributed is null || TryRetention(owner.Noun, owner.Name, ttlMs, staleMs, diagnostics, artifact, pointer);
        if (ok)
        {
            spec = new CacheSpec(new CachePolicy(ttlMs, staleMs), cache.Version, memoryBytes, distributed, distributedTimeoutMs);
        }

        return ok;
    }

    /// <summary>Whether the binding names a tier that keeps the cache's records, right or wrong (<see cref="CacheSpec.Kept"/> of what it compiles to).</summary>
    public static bool HasTier(CacheBinding cache) => cache.Memory is not null || cache.Distributed is not null;

    /// <summary>
    /// The settings a cache that keeps nothing gives all the same, as a message names them (<c>'ttl_ms' and 'version'</c>);
    /// null when it gives none, which is the cache of a request's reads alone.
    /// </summary>
    public static string? Unkept(long? ttlMs, long? staleIfErrorMs, string? version)
    {
        string? given = null;
        Add(ttlMs is not null, "'ttl_ms'");
        Add(staleIfErrorMs is not null, "'stale_if_error_ms'");
        Add(version is not null, "'version'");
        return given;

        void Add(bool has, string name)
        {
            if (has)
            {
                given = given is null ? name : given + " and " + name;
            }
        }
    }

    /// <summary>
    /// The tiers of a cache: <c>memory</c>, <c>distributed</c> or both, at least one; each problem reported at its setting.
    /// </summary>
    /// <param name="pointer">The <c>cache</c> member's pointer.</param>
    /// <param name="ownerTimeoutMs">As for <see cref="TryRead"/>.</param>
    public static bool TryTiers(
        MemoryCacheBinding? memory, DistributedCacheBinding? shared, CacheOwner owner, BindingResolver resolver, string pointer, int? ownerTimeoutMs,
        out long? memoryBytes, out DistributedCacheTier? distributed, out int? distributedTimeoutMs)
    {
        var diagnostics = resolver.Diagnostics;
        var artifact = resolver.Artifact;
        var ok = true;
        memoryBytes = null;
        distributed = null;
        distributedTimeoutMs = null;
        if (memory is not null)
        {
            if (memory.MaxBytes is null)
            {
                ok = false;
                diagnostics.Error("CACHE_MAX_BYTES", artifact, pointer + "/memory", $"The in-process cache of {owner.Noun} '{owner.Name}' has no 'max_bytes'.",
                    "add the most memory its table may take, e.g. \"max_bytes\": \"64MiB\"");
            }
            else if (!ByteSize.TryParse(memory.MaxBytes, out var bytes) || bytes == 0)
            {
                ok = false;
                diagnostics.Error("CACHE_MAX_BYTES", artifact, pointer + "/memory/max_bytes", $"'{memory.MaxBytes}' is not a positive size.",
                    "write an integer and a unit B, KiB, MiB, GiB or TiB, e.g. \"64MiB\"");
            }
            else
            {
                memoryBytes = bytes;
            }
        }
        else if (shared is null)
        {
            ok = false;
            diagnostics.Error("CACHE_TIERS", artifact, pointer, $"The cache of {owner.Noun} '{owner.Name}' has neither a 'memory' nor a 'distributed' tier: nothing would keep its values.",
                "add \"memory\": { \"max_bytes\": \"64MiB\" } to keep them in process, or \"distributed\": { \"store\": \"redis@1\", ... } to keep them in a store the instances share");
        }

        if (shared is not null)
        {
            var at = pointer + "/distributed";
            if (shared.TimeoutMs is <= 0)
            {
                ok = false;
                diagnostics.Error("DISTRIBUTED_CACHE_CONFIG", artifact, at + "/timeout_ms", $"timeout_ms {shared.TimeoutMs} is not positive.",
                    "give the longest a request waits for the distributed cache, in milliseconds"
                    + (ownerTimeoutMs is null ? ", e.g. \"timeout_ms\": 10" : $", or leave it out (half the {owner.Noun}'s timeout_ms)"));
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
        }

        return ok;
    }

    /// <summary><c>ttl_ms</c> within what a policy takes; false (diagnosed at <paramref name="pointer"/><c>/ttl_ms</c>) otherwise.</summary>
    public static bool TryTtl(long ttl, DiagnosticBag diagnostics, string artifact, string pointer)
    {
        if (ttl is > 0 and <= MaxMs)
        {
            return true;
        }

        diagnostics.Error("CACHE_TTL", artifact, pointer + "/ttl_ms", $"ttl_ms {ttl} is not between 1 and 2^61 milliseconds.",
            "give how long a fetched value is served, e.g. \"ttl_ms\": 60000");
        return false;
    }

    /// <summary><c>stale_if_error_ms</c> (none is 0) within what a policy takes; false (diagnosed) otherwise.</summary>
    /// <param name="origin">What the cache is in front of, for the hint: <c>connector</c>, <c>service</c>, <c>pipeline</c>.</param>
    public static bool TryStale(long? stale, string origin, DiagnosticBag diagnostics, string artifact, string pointer, out long staleMs)
    {
        staleMs = stale ?? 0;
        if (staleMs is >= 0 and <= MaxMs)
        {
            return true;
        }

        diagnostics.Error("CACHE_STALE_IF_ERROR", artifact, pointer + "/stale_if_error_ms",
            $"stale_if_error_ms {stale} is not between 0 and 2^61 milliseconds.",
            $"give how much longer an expired value may serve when the {origin} fails, e.g. 300000, or 0");
        staleMs = 0;
        return false;
    }

    /// <summary><c>version</c>, when given, in its form; false (diagnosed) otherwise.</summary>
    public static bool TryVersion(string? version, DiagnosticBag diagnostics, string artifact, string pointer)
    {
        if (version is null || IsVersion(version))
        {
            return true;
        }

        diagnostics.Error("CACHE_VERSION", artifact, pointer + "/version", $"'{version}' is not a cache version.",
            "name it with letters, digits, '.', '_' and '-', at most 64, e.g. \"version\": \"3\"; or remove it to let the cache follow the configuration");
        return false;
    }

    /// <summary>Whether <paramref name="version"/> has the form of <see cref="CacheBinding.VersionPattern"/>.</summary>
    private static bool IsVersion(string version)
    {
        if (version.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var c in version)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the distributed tier can be given the longest time to live a record of this cache needs (the TTL with all
    /// its jitter, plus the stale window): a <see cref="TimeSpan"/>. Diagnosed at the setting that takes it past, of the
    /// <c>cache</c> member at <paramref name="pointer"/>: a binding's, or for a pipeline's results the calling node's.
    /// </summary>
    /// <param name="ttlMs">0 for one that is missing or wrong: diagnosed already, and nothing more is said of it here.</param>
    public static bool TryRetention(string noun, string name, long ttlMs, long staleMs, DiagnosticBag diagnostics, string artifact, string pointer)
    {
        if (ttlMs == 0 || CachePolicy.RetentionOf(ttlMs, staleMs) <= CachePolicy.MaxRetentionMs)
        {
            return true;
        }

        var setting = CachePolicy.MaxLifetime(ttlMs) > CachePolicy.MaxRetentionMs ? "ttl_ms" : "stale_if_error_ms";
        diagnostics.Error("CACHE_RETENTION", artifact, pointer + "/" + setting,
            $"The distributed cache of {noun} '{name}' would keep a record for up to {CachePolicy.RetentionOf(ttlMs, staleMs)} ms "
            + $"(ttl_ms with its 10% jitter, plus stale_if_error_ms), more than the {CachePolicy.MaxRetentionMs} ms a time to live can be.",
            $"lower ttl_ms or stale_if_error_ms so that ttl_ms × 1.1 + stale_if_error_ms is at most {CachePolicy.MaxRetentionMs} (about 29,000 years)");
        return false;
    }

    /// <summary>What to do about a store type the binary does not have, besides the types it has: where <c>redis@1</c> comes from.</summary>
    private static string StoreHint(string? type) =>
        type is not null && type.StartsWith("redis@", StringComparison.Ordinal)
            ? "redis@1 is in the package StrataRec.Redis: the host adds it with PlanEnvironment.Bindings.AddRedis()"
            : "or remove 'distributed' and keep the values in process with 'memory'";
}
