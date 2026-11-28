using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Services;
using StrataRec.Features.Bindings;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// Binds logical service names to client instances using the environment's bindings. The instances come from the plan's
/// <see cref="IPlanBindings"/>: an engine shares them across plans by binding identity
/// (<see cref="BindingSpec.Identity"/>), so a plan swap never creates or loads a client again; each name is resolved
/// once per compile pass, a failure too, so it is reported once.
/// </summary>
public sealed class ClientResolver
{
    private readonly SourceBindingArtifact _bindings;
    private readonly BindingResolver _resolver;
    private readonly IPlanBindings _instances;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (IServiceClient? Client, int? TimeoutMs, ServiceCache? Cache)> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceCache?> _pipelineCaches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceCache> _requestCaches = new(StringComparer.Ordinal);

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
                bound = null;
            }

            entry = (bound, binding.TimeoutMs, serviceCache);
            _services[name] = entry;
        }

        client = entry.Client!;
        timeoutMs = entry.TimeoutMs;
        cache = entry.Cache;
        return entry.Client is not null;
    }

    /// <summary>The pipelines whose calls' cache was bound so far (<see cref="SourceBindingArtifact.AddTypes"/>).</summary>
    public List<string> CachedPipelines
    {
        get
        {
            var names = new List<string>(_pipelineCaches.Count);
            foreach (var (name, tiers) in _pipelineCaches)
            {
                if (tiers is not null)
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>
    /// A digest of what the named feature sources and the services bound so far give their values from in this
    /// environment, each as its own cache's identity is (its connector or client and settings, or its
    /// <c>cache.version</c>; not timeouts, batch sizes or cache settings): what a cache of a pipeline's results follows.
    /// </summary>
    internal string ValuesIdentity(IEnumerable<string> featureSources)
    {
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in featureSources)
        {
            if (_bindings.Sources.TryGetValue(name, out var source))
            {
                sources[name] = Features.Runtime.FeatureSourceResolver.ValuesIdentity(source);
            }
        }

        var services = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, entry) in _services)
        {
            if (entry.Client is not null && _bindings.Services.TryGetValue(name, out var service))
            {
                services[name] = service.Cache?.Version is { } version ? "v." + version : AnswerIdentity(service);
            }
        }

        var node = new System.Text.Json.Nodes.JsonObject();
        foreach (var (kind, identities) in new[] { ("sources", sources), ("services", services) })
        {
            var section = new System.Text.Json.Nodes.JsonObject();
            foreach (var (name, identity) in identities)
            {
                section[name] = identity;
            }

            node[kind] = section;
        }

        return ContentHash.Compute(node);
    }

    internal bool TryGetPipelineCache(string pipelineId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ServiceCache? tiers, out string? error, out string? hint)
    {
        error = null;
        hint = null;
        if (_pipelineCaches.TryGetValue(pipelineId, out tiers))
        {
            return tiers is not null;
        }

        if (!_bindings.Pipelines.TryGetValue(pipelineId, out var binding) || binding.Cache is not { } cache)
        {
            error = $"Environment '{_bindings.Environment}' binds no cache for the calls of pipeline '{pipelineId}': nothing would keep their results.";
            hint = $"add \"pipelines\": {{ \"{pipelineId}\": {{ \"cache\": {{ \"distributed\": {{ \"store\": \"redis@1\", \"endpoint\": \"...\", \"timeout_ms\": 15 }} }} }} }} "
                + "to the bindings (or \"memory\": { \"max_bytes\": \"64MiB\" } for an in-process tier), or remove 'cache' from the node";
            return false; // not kept: another node of the compile reports it at its own config too
        }

        _pipelineCaches[pipelineId] = null;
        var entry = "/" + BindingKind.PipelineCacheStore.Section() + "/" + JsonPointer.Escape(pipelineId);
        var pointer = entry + "/cache";
        if (!CacheSettings.TryTiers(
                cache.Memory, cache.Distributed, new CacheOwner(BindingKind.PipelineCacheStore, pipelineId, "pipeline", "pipeline"), _resolver, pointer,
                ownerTimeoutMs: null, out var memoryBytes, out var distributed, out var distributedTimeoutMs))
        {
            return false;
        }

        // The table never judges a record dead by its age: each calling node has its own times, and a reader judges by
        // its own. Its identity names no origin: a record's key says what it is of (PipelineCallCache.Format), so the
        // table is kept across every change of the configuration.
        var spec = new CacheSpec(CachePolicy.Unlimited, null, memoryBytes, distributed, distributedTimeoutMs);
        var table = LocalTableSpec.CreateCache(
            ServiceCache.Schema(pipelineId, PipelineCallCache.Entity), spec.TableOptions(_clock, origin: null), PipelineCallCache.Entity, "pipeline", _resolver.Registry,
            _resolver.Artifact, entry, _resolver.BaseDirectory);
        try
        {
            tiers = new ServiceCache(pipelineId, _instances.Acquire(table), spec.Policy, distributed, distributedTimeoutMs ?? 0, string.Empty);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _resolver.Diagnostics.Error("CACHE_MAX_BYTES", _resolver.Artifact, pointer + "/memory/max_bytes", ex.Message.Split('\n')[0], "raise max_bytes");
            return false;
        }

        _pipelineCaches[pipelineId] = tiers;
        return true;
    }

    /// <summary>
    /// Its table, which stays empty, is what the calls of one request that share a result are found by, and is not the
    /// table of the tiers the environment may bind for the nodes that keep their results: those calls share among
    /// themselves.
    /// </summary>
    internal ServiceCache RequestPipelineCache(string pipelineId)
    {
        if (!_requestCaches.TryGetValue(pipelineId, out var cache))
        {
            var spec = new CacheSpec(CachePolicy.Unlimited, null, null, null, null);
            var table = LocalTableSpec.CreateCache(
                ServiceCache.Schema(pipelineId, PipelineCallCache.Entity), spec.TableOptions(_clock, origin: null), PipelineCallCache.Entity, "pipeline",
                _resolver.Registry, _resolver.Artifact, "/" + BindingKind.PipelineCacheStore.Section() + "/" + JsonPointer.Escape(pipelineId), _resolver.BaseDirectory);
            cache = new ServiceCache(pipelineId, _instances.Acquire(table), spec.Policy, null, 0, string.Empty);
            _requestCaches[pipelineId] = cache;
        }

        return cache;
    }

    private bool TryCache(string name, ClientBinding binding, CacheBinding settings, IServiceClient? client, out ServiceCache? cache)
    {
        cache = null;
        var entry = BindingKind.Service.Pointer(name);
        var pointer = entry + "/cache";
        var diagnostics = _resolver.Diagnostics;
        var ok = CacheSettings.TryRead(
            settings, new CacheOwner(BindingKind.ServiceCacheStore, name, "service", "service"), _resolver, pointer, binding.TimeoutMs, out var read);
        if (!ok || client is null)
        {
            return false;
        }

        // The answers' identity is in every key, and in the table's: a release that only changes the cache's times or
        // limits keeps the table.
        var identity = read.Origin(AnswerIdentity(binding));
        var spec = LocalTableSpec.CreateCache(
            ServiceCache.Schema(name), read.TableOptions(_clock, origin: null), identity, "service", _resolver.Registry, _resolver.Artifact, entry, _resolver.BaseDirectory);
        LocalFeatureTable table;
        try
        {
            table = _instances.Acquire(spec);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            diagnostics.Error("CACHE_MAX_BYTES", _resolver.Artifact, pointer + "/memory/max_bytes", ex.Message.Split('\n')[0], "raise max_bytes");
            return false;
        }

        // A distributed tier's timeout defaults to half the service's (which then exists: CacheSettings requires one or the other).
        cache = new ServiceCache(name, table, read.Policy, read.Distributed, read.DistributedTimeoutMs ?? Math.Max(1, (binding.TimeoutMs ?? 2) / 2), identity);
        return true;
    }

    /// <summary>
    /// Not its <c>timeout_ms</c> or <c>cache</c>, which change when an answer arrives and how long it is kept, not what
    /// it is.
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
