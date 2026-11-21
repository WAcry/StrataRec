using StrataRec.Artifacts.Model;
using StrataRec.Features.Bindings;
using StrataRec.Models;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// Binds logical model and candidate-source names to client instances using the environment's bindings (design 6.4).
/// The instances come from the plan's <see cref="IPlanBindings"/> through the <see cref="BindingResolver"/>: an engine
/// shares them across plans by binding identity (<see cref="BindingSpec.Identity"/>), so a plan swap never creates or loads
/// a client again; each name is resolved once per compile pass and cached here, a failure too, so it is reported once.
/// </summary>
public sealed class ClientResolver
{
    private readonly SourceBindingArtifact _bindings;
    private readonly BindingResolver _resolver;
    private readonly IPlanBindings _instances;
    private readonly Dictionary<string, (IModelClient? Client, int? TimeoutMs)> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ICandidateSource? Source, int? TimeoutMs)> _sources = new(StringComparer.Ordinal);

    /// <param name="resolver">Creates the instances, from <paramref name="instances"/>, and reports a binding's errors at the binding.</param>
    /// <param name="instances">Where model signatures come from.</param>
    public ClientResolver(SourceBindingArtifact bindings, BindingResolver resolver, IPlanBindings instances)
    {
        _bindings = bindings;
        _resolver = resolver;
        _instances = instances;
    }

    public SourceBindingArtifact Bindings => _bindings;

    /// <summary>The model names bound so far: what the compiled configuration needs (<see cref="SourceBindingArtifact.AddTypes"/>).</summary>
    public List<string> BoundModels => Bound(_models);

    /// <summary>The candidate-source names bound so far.</summary>
    public List<string> BoundCandidateSources => Bound(_sources);

    /// <summary>The client bound to model <paramref name="name"/>.</summary>
    /// <returns>False with an error for the caller to report at its own config key (the name has no binding), or with a
    /// null error when the binding is wrong, which is reported at the binding.</returns>
    public bool TryGetModel(string name, out IModelClient client, out int? timeoutMs, out string? error, out string? hint) =>
        TryGet(BindingKind.Model, _bindings.Models, _models, name, out client, out timeoutMs, out error, out hint);

    /// <summary>The signature of a version of a model this resolver handed out (see <see cref="IPlanBindings.Signature"/>).</summary>
    public SignatureLookup Signature(IModelClient client, string model, string? version) => _instances.Signature(client, model, version);

    /// <inheritdoc cref="TryGetModel"/>
    public bool TryGetCandidateSource(string name, out ICandidateSource source, out int? timeoutMs, out string? error, out string? hint) =>
        TryGet(BindingKind.CandidateSource, _bindings.CandidateSources, _sources, name, out source, out timeoutMs, out error, out hint);

    private bool TryGet<T>(
        BindingKind kind, Dictionary<string, ClientBinding> entries, Dictionary<string, (T? Instance, int? TimeoutMs)> resolved, string name,
        out T instance, out int? timeoutMs, out string? error, out string? hint)
        where T : class
    {
        error = null;
        hint = null;
        if (!resolved.TryGetValue(name, out var entry))
        {
            if (!entries.TryGetValue(name, out var binding))
            {
                instance = null!;
                timeoutMs = null;
                error = $"{kind.Label(name)} has no binding in environment '{_bindings.Environment}'.";
                hint = NameSuggestions.DidYouMean(name, entries.Keys) ?? $"add it under '{kind.Section()}' in the bindings artifact";
                return false;
            }

            entry = (_resolver.Acquire<T>(kind, name, binding.Client, binding.Settings), binding.TimeoutMs);
            resolved[name] = entry;
        }

        instance = entry.Instance!;
        timeoutMs = entry.TimeoutMs;
        return entry.Instance is not null;
    }

    private static List<string> Bound<T>(Dictionary<string, (T? Instance, int? TimeoutMs)> resolved)
        where T : class
    {
        var names = new List<string>(resolved.Count);
        foreach (var (name, entry) in resolved)
        {
            if (entry.Instance is not null)
            {
                names.Add(name);
            }
        }

        return names;
    }
}
