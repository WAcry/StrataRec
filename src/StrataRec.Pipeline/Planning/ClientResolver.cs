using StrataRec.Artifacts.Model;
using StrataRec.Models;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Pipeline.Planning;

/// <summary>Model client factories available in this binary.</summary>
public sealed class ModelClientRegistry
{
    private readonly Dictionary<string, IModelClientFactory> _factories = new(StringComparer.Ordinal);

    public static ModelClientRegistry CreateDefault() => new ModelClientRegistry().Add(new LinearModelClientFactory());

    public IEnumerable<string> Types => SortedKeys(_factories);

    public ModelClientRegistry Add(IModelClientFactory factory)
    {
        _factories[factory.ClientType] = factory;
        return this;
    }

    public bool TryGet(string type, out IModelClientFactory factory) => _factories.TryGetValue(type, out factory!);

    internal static List<string> SortedKeys<T>(Dictionary<string, T> dictionary)
    {
        var keys = new List<string>(dictionary.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}

/// <summary>Candidate source factories available in this binary.</summary>
public sealed class CandidateSourceRegistry
{
    private readonly Dictionary<string, ICandidateSourceFactory> _factories = new(StringComparer.Ordinal);

    public static CandidateSourceRegistry CreateDefault() => new CandidateSourceRegistry().Add(new MemoryListCandidateSourceFactory());

    public IEnumerable<string> Types => ModelClientRegistry.SortedKeys(_factories);

    public CandidateSourceRegistry Add(ICandidateSourceFactory factory)
    {
        _factories[factory.ClientType] = factory;
        return this;
    }

    public bool TryGet(string type, out ICandidateSourceFactory factory) => _factories.TryGetValue(type, out factory!);
}

/// <summary>
/// Binds logical model and candidate-source names to client instances using the environment's bindings
/// (design 6.4). Instances are created once per plan and cached.
/// </summary>
public sealed class ClientResolver
{
    private readonly SourceBindingArtifact _bindings;
    private readonly ModelClientRegistry _models;
    private readonly CandidateSourceRegistry _sources;
    private readonly string? _baseDirectory;
    private readonly Dictionary<string, (IModelClient Client, int? TimeoutMs)> _modelCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ICandidateSource Source, int? TimeoutMs)> _sourceCache = new(StringComparer.Ordinal);

    public ClientResolver(SourceBindingArtifact bindings, ModelClientRegistry models, CandidateSourceRegistry sources, string? baseDirectory)
    {
        _bindings = bindings;
        _models = models;
        _sources = sources;
        _baseDirectory = baseDirectory;
    }

    public SourceBindingArtifact Bindings => _bindings;

    public bool TryGetModel(string name, out IModelClient client, out int? timeoutMs, out string? error, out string? hint)
    {
        hint = null;
        if (_modelCache.TryGetValue(name, out var cached))
        {
            (client, timeoutMs) = cached;
            error = null;
            return true;
        }

        client = null!;
        timeoutMs = null;
        if (!_bindings.Models.TryGetValue(name, out var binding))
        {
            error = $"Model '{name}' has no binding in environment '{_bindings.Environment}'.";
            hint = NameSuggestions.DidYouMean(name, _bindings.Models.Keys) ?? "add it under 'models' in the bindings artifact";
            return false;
        }

        if (!_models.TryGet(binding.Client, out var factory))
        {
            error = $"Model client type '{binding.Client}' is not registered in this binary.";
            hint = "registered: " + string.Join(", ", _models.Types);
            return false;
        }

        try
        {
            client = factory.Create(name, binding.Settings);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            error = $"Model '{name}': {ex.Message}";
            return false;
        }

        timeoutMs = binding.TimeoutMs;
        _modelCache[name] = (client, timeoutMs);
        error = null;
        return true;
    }

    public bool TryGetCandidateSource(string name, out ICandidateSource source, out int? timeoutMs, out string? error, out string? hint)
    {
        hint = null;
        if (_sourceCache.TryGetValue(name, out var cached))
        {
            (source, timeoutMs) = cached;
            error = null;
            return true;
        }

        source = null!;
        timeoutMs = null;
        if (!_bindings.CandidateSources.TryGetValue(name, out var binding))
        {
            error = $"Candidate source '{name}' has no binding in environment '{_bindings.Environment}'.";
            hint = NameSuggestions.DidYouMean(name, _bindings.CandidateSources.Keys) ?? "add it under 'candidate_sources' in the bindings artifact";
            return false;
        }

        if (!_sources.TryGet(binding.Client, out var factory))
        {
            error = $"Candidate source client type '{binding.Client}' is not registered in this binary.";
            hint = "registered: " + string.Join(", ", _sources.Types);
            return false;
        }

        try
        {
            source = factory.Create(name, binding.Settings, _baseDirectory);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            error = $"Candidate source '{name}': {ex.Message}";
            return false;
        }

        timeoutMs = binding.TimeoutMs;
        _sourceCache[name] = (source, timeoutMs);
        error = null;
        return true;
    }
}
