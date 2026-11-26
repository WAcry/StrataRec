using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Services;
using StrataRec.Features.Bindings;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// Binds logical service names to client instances using the environment's bindings. The instances come
/// from the plan's <see cref="IPlanBindings"/> through the <see cref="BindingResolver"/>: an engine shares them across
/// plans by binding identity (<see cref="BindingSpec.Identity"/>), so a plan swap never creates or loads a client again;
/// each name is resolved once per compile pass and cached here, a failure too, so it is reported once.
/// </summary>
public sealed class ClientResolver
{
    private readonly SourceBindingArtifact _bindings;
    private readonly BindingResolver _resolver;
    private readonly IPlanBindings _instances;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (IServiceClient? Client, int? TimeoutMs, ServiceCache? Cache)> _services = new(StringComparer.Ordinal);

    /// <param name="resolver">Creates the instances, from <paramref name="instances"/>, and reports a binding's errors at the binding.</param>
    /// <param name="instances">Where service signatures and the tables of services' caches come from.</param>
    /// <param name="clock">The clock of the services' cache tables (write times and expiry).</param>
    public ClientResolver(SourceBindingArtifact bindings, BindingResolver resolver, IPlanBindings instances, TimeProvider? clock = null)
    {
        _bindings = bindings;
        _resolver = resolver;
        _instances = instances;
        _clock = clock ?? TimeProvider.System;
    }

    public SourceBindingArtifact Bindings => _bindings;

    /// <summary>The service names bound so far: what the compiled configuration needs (<see cref="SourceBindingArtifact.AddTypes"/>).</summary>
    public List<string> BoundServices
    {
        get
        {
            var names = new List<string>(_services.Count);
            foreach (var (name, entry) in _services)
            {
                if (entry.Client is not null)
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>The client bound to service <paramref name="name"/>, and its cache when the binding declares one.</summary>
    /// <returns>False with an error for the caller to report at its own config key (the name has no binding), or with a
    /// null error when the binding is wrong (its cache included), which is reported at the binding.</returns>
    internal bool TryGetService(string name, out IServiceClient client, out int? timeoutMs, out ServiceCache? cache, out string? error, out string? hint)
    {
        error = null;
        hint = null;
        if (!_services.TryGetValue(name, out var entry))
        {
            if (!_bindings.Services.TryGetValue(name, out var binding))
            {
                client = null!;
                timeoutMs = null;
                cache = null;
                error = $"{BindingKind.Service.Label(name)} has no binding in environment '{_bindings.Environment}'.";
                hint = NameSuggestions.DidYouMean(name, _bindings.Services.Keys) ?? $"add it under '{BindingKind.Service.Section()}' in the bindings artifact";
                return false;
            }

            var bound = _resolver.Acquire<IServiceClient>(BindingKind.Service, name, binding.Client, binding.Settings);
            ServiceCache? serviceCache = null;
            if (binding.Cache is { } settings && !TryCache(name, binding, settings, bound, out serviceCache))
            {
                bound = null; // a wrong cache is a wrong binding: the service is not served without the cache it declares
            }

            entry = (bound, binding.TimeoutMs, serviceCache);
            _services[name] = entry;
        }

        client = entry.Client!;
        timeoutMs = entry.TimeoutMs;
        cache = entry.Cache;
        return entry.Client is not null;
    }

    /// <summary>
    /// The cache a service's binding declares: its table, shared across plans by the binding's identity and
    /// the client object, and its distributed tier; every problem reported at its setting. False when any is wrong.
    /// </summary>
    private bool TryCache(string name, ClientBinding binding, ServiceCacheBinding settings, IServiceClient? client, out ServiceCache? cache)
    {
        cache = null;
        var entry = BindingKind.Service.Pointer(name);
        var pointer = entry + "/cache";
        var diagnostics = _resolver.Diagnostics;
        var ok = true;
        long maxBytes = 0;
        if (settings.MaxBytes is null)
        {
            ok = false;
            diagnostics.Error("CACHE_MAX_BYTES", _resolver.Artifact, pointer, $"The cache of service '{name}' has no 'max_bytes'.",
                "add the most memory its table may take, e.g. \"max_bytes\": \"64MiB\"");
        }
        else if (!ByteSize.TryParse(settings.MaxBytes, out maxBytes) || maxBytes == 0)
        {
            ok = false;
            diagnostics.Error("CACHE_MAX_BYTES", _resolver.Artifact, pointer + "/max_bytes", $"'{settings.MaxBytes}' is not a positive size.",
                "write an integer and a unit B, KiB, MiB, GiB or TiB, e.g. \"64MiB\"");
        }

        ok &= CacheSettings.TryRead(
            settings, new CacheOwner(BindingKind.ServiceCacheStore, name, "service", "service"), _resolver, pointer, binding.TimeoutMs,
            out var ttlMs, out var staleMs, out var distributed, out var distributedTimeoutMs);
        if (!ok || client is null)
        {
            return false;
        }

        var options = new LocalFeatureTableOptions
        {
            MaxBytes = maxBytes,
            Cache = new CacheTableOptions { TtlMs = ttlMs, StaleIfErrorMs = staleMs, Clock = _clock },
        };
        var spec = LocalTableSpec.CreateServiceCache(ServiceCache.Schema(name), options, binding, client, _resolver.Artifact, entry, _resolver.BaseDirectory);
        LocalFeatureTable table;
        try
        {
            table = _instances.Acquire(spec);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // max_bytes below what an empty table takes.
            diagnostics.Error("CACHE_MAX_BYTES", _resolver.Artifact, pointer + "/max_bytes", ex.Message.Split('\n')[0], "raise max_bytes");
            return false;
        }

        // A distributed tier's timeout defaults to half the service's (which then exists: CacheSettings requires one or the other).
        cache = new ServiceCache(name, table, distributed, distributedTimeoutMs ?? Math.Max(1, (binding.TimeoutMs ?? 2) / 2), AnswerIdentity(binding));
        return true;
    }

    /// <summary>
    /// The content id of what a binding answers with: its client and settings, canonical (RFC 8785), so every instance
    /// with the same configuration has the same one. Not its <c>timeout_ms</c> or <c>cache</c>, which change when an
    /// answer arrives and how long it is kept, not what it is.
    /// </summary>
    private static string AnswerIdentity(ClientBinding binding)
    {
        var node = ArtifactSerializer.ToNode(binding).AsObject();
        node.Remove("timeout_ms");
        node.Remove("cache");
        return ContentHash.Compute(node);
    }

    /// <summary>The signature of a version of a service this resolver handed out (see <see cref="IPlanBindings.Signature"/>).</summary>
    public SignatureLookup Signature(IServiceClient client, string service, string? version) => _instances.Signature(client, service, version);
}
