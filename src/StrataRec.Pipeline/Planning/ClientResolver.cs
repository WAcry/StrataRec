using StrataRec.Artifacts.Model;
using StrataRec.Core.Services;
using StrataRec.Features.Bindings;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// Binds logical service names to client instances using the environment's bindings (design 6.4). The instances come
/// from the plan's <see cref="IPlanBindings"/> through the <see cref="BindingResolver"/>: an engine shares them across
/// plans by binding identity (<see cref="BindingSpec.Identity"/>), so a plan swap never creates or loads a client again;
/// each name is resolved once per compile pass and cached here, a failure too, so it is reported once.
/// </summary>
public sealed class ClientResolver
{
    private readonly SourceBindingArtifact _bindings;
    private readonly BindingResolver _resolver;
    private readonly IPlanBindings _instances;
    private readonly Dictionary<string, (IServiceClient? Client, int? TimeoutMs)> _services = new(StringComparer.Ordinal);

    /// <param name="resolver">Creates the instances, from <paramref name="instances"/>, and reports a binding's errors at the binding.</param>
    /// <param name="instances">Where service signatures come from.</param>
    public ClientResolver(SourceBindingArtifact bindings, BindingResolver resolver, IPlanBindings instances)
    {
        _bindings = bindings;
        _resolver = resolver;
        _instances = instances;
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

    /// <summary>The client bound to service <paramref name="name"/>.</summary>
    /// <returns>False with an error for the caller to report at its own config key (the name has no binding), or with a
    /// null error when the binding is wrong, which is reported at the binding.</returns>
    public bool TryGetService(string name, out IServiceClient client, out int? timeoutMs, out string? error, out string? hint)
    {
        error = null;
        hint = null;
        if (!_services.TryGetValue(name, out var entry))
        {
            if (!_bindings.Services.TryGetValue(name, out var binding))
            {
                client = null!;
                timeoutMs = null;
                error = $"{BindingKind.Service.Label(name)} has no binding in environment '{_bindings.Environment}'.";
                hint = NameSuggestions.DidYouMean(name, _bindings.Services.Keys) ?? $"add it under '{BindingKind.Service.Section()}' in the bindings artifact";
                return false;
            }

            entry = (_resolver.Acquire<IServiceClient>(BindingKind.Service, name, binding.Client, binding.Settings), binding.TimeoutMs);
            _services[name] = entry;
        }

        client = entry.Client!;
        timeoutMs = entry.TimeoutMs;
        return entry.Client is not null;
    }

    /// <summary>The signature of a version of a service this resolver handed out (see <see cref="IPlanBindings.Signature"/>).</summary>
    public SignatureLookup Signature(IServiceClient client, string service, string? version) => _instances.Signature(client, service, version);
}
