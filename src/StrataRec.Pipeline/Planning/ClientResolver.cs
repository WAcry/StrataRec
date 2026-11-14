using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Features.Bindings;
using StrataRec.Models;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Pipeline.Planning;

/// <summary>Model client factories available in this binary.</summary>
public sealed class ModelClientRegistry
{
    private readonly Dictionary<string, IModelClientFactory> _factories = new(StringComparer.Ordinal);

    public static ModelClientRegistry CreateDefault() =>
        new ModelClientRegistry().Add(new LinearModelClientFactory()).Add(new RandomModelClientFactory()).Add(new FixedTextModelClientFactory());

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
/// Binds logical model and candidate-source names to client instances using the environment's bindings (design 6.4).
/// The instances come from the plan's <see cref="IPlanBindings"/>: an engine shares them across plans by binding identity
/// (<see cref="BindingSpec.Identity"/>), so a plan swap never creates or loads a client again; each name is resolved once
/// per compile pass and cached here.
/// </summary>
public sealed class ClientResolver
{
    private readonly SourceBindingArtifact _bindings;
    private readonly ModelClientRegistry _models;
    private readonly CandidateSourceRegistry _sources;
    private readonly string? _baseDirectory;
    private readonly IPlanBindings _instances;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _artifact;
    private readonly Dictionary<string, (IModelClient Client, int? TimeoutMs)> _modelCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ICandidateSource Source, int? TimeoutMs)> _sourceCache = new(StringComparer.Ordinal);

    /// <param name="instances">Where instances (and model signatures) come from.</param>
    /// <param name="diagnostics">Receives errors in a binding's settings, at the setting.</param>
    public ClientResolver(
        SourceBindingArtifact bindings, ModelClientRegistry models, CandidateSourceRegistry sources, string? baseDirectory, IPlanBindings instances,
        DiagnosticBag diagnostics)
    {
        _bindings = bindings;
        _models = models;
        _sources = sources;
        _baseDirectory = baseDirectory;
        _instances = instances;
        _diagnostics = diagnostics;
        _artifact = "bindings:" + bindings.Environment;
    }

    public SourceBindingArtifact Bindings => _bindings;

    /// <summary>The client bound to model <paramref name="name"/>.</summary>
    /// <returns>False with an error for the caller to report at its own config key, or with a null error when the
    /// binding's settings are wrong, which is reported here, at the setting in the bindings artifact.</returns>
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
            client = _instances.Acquire(BindingSpec.Create(BindingSection.Models, name, binding.Client, binding.Settings, _baseDirectory, factory, _artifact), factory.Create);
        }
        catch (BindingException ex)
        {
            ReportSettings("MODEL_CONFIG", BindingSection.Models, name, ex);
            error = null;
            return false;
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

    /// <summary>The signature of a version of a model this resolver handed out (see <see cref="IPlanBindings.Signature"/>).</summary>
    public SignatureLookup Signature(IModelClient client, string model, string? version) => _instances.Signature(client, model, version);

    /// <inheritdoc cref="TryGetModel"/>
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
            source = _instances.Acquire(
                BindingSpec.Create(BindingSection.CandidateSources, name, binding.Client, binding.Settings, _baseDirectory, factory, _artifact), factory.Create);
        }
        catch (BindingException ex)
        {
            ReportSettings("CANDIDATE_SOURCE_CONFIG", BindingSection.CandidateSources, name, ex);
            error = null;
            return false;
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

    private void ReportSettings(string code, BindingSection section, string name, BindingException ex) =>
        _diagnostics.Error(code, _artifact, "/" + BindingSpec.SectionName(section) + "/" + JsonPointer.Escape(name) + ex.Pointer, ex.Reason, ex.Hint);
}
