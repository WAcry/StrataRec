using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;

namespace StrataRec.Features.Bindings;

/// <summary>
/// The one path from a bindings entry to what it names (design 6.4), for every <see cref="BindingKind"/>: the factory of
/// the type the entry names, the instance shared by identity, and every error at the entry, with the same codes and
/// words whatever the kind.
/// </summary>
/// <remarks>
/// Errors are reported here, at the binding, never at whatever reads it: an unknown type at the entry's selector (the
/// binding is what to fix), wrong settings at the setting. A caller gets null and reports nothing more (design: one error
/// is reported once).
/// </remarks>
public sealed class BindingResolver
{
    /// <summary>The entry names no type, or a type this binary does not register.</summary>
    public const string TypeUnknownCode = "BINDING_TYPE_UNKNOWN";

    /// <summary>The factory rejected the entry's own settings.</summary>
    public const string SettingsCode = "BINDING_SETTINGS";

    private readonly BindingRegistry _registry;
    private readonly IBindingInstances _instances;

    /// <param name="registry">The factories of this binary.</param>
    /// <param name="instances">Where instances come from: the engine shares them across plans by binding identity.</param>
    /// <param name="baseDirectory">Where relative paths in settings resolve.</param>
    /// <param name="artifact">The bindings artifact, e.g. <c>bindings:local</c>.</param>
    public BindingResolver(BindingRegistry registry, IBindingInstances instances, string? baseDirectory, string artifact, DiagnosticBag diagnostics)
    {
        _registry = registry;
        _instances = instances;
        BaseDirectory = baseDirectory;
        Artifact = artifact;
        Diagnostics = diagnostics;
    }

    /// <summary>Where binding errors go: also where whoever resolves through this resolver reports the rest of its errors,
    /// so one compile has one list.</summary>
    public DiagnosticBag Diagnostics { get; }

    /// <summary>The bindings artifact, e.g. <c>bindings:local</c>.</summary>
    public string Artifact { get; }

    public string? BaseDirectory { get; }

    /// <summary>The factory of the type entry <paramref name="name"/> names; null (reported) when it names none or an unknown one.</summary>
    /// <param name="type">The type the entry's selector names; null when it has none.</param>
    /// <param name="hint">What else to do about an unknown type, after the list of registered types (when none is close to it).</param>
    public IBindingFactory<T>? Factory<T>(BindingKind kind, string name, string? type, string? hint = null)
        where T : class
    {
        if (type is not null && _registry.TryGet<T>(type, out var factory))
        {
            return factory;
        }

        var registered = _registry.Types<T>();
        var list = (registered.Count == 0 ? $"this binary registers no {kind.TypeNoun()}" : "registered: " + string.Join(", ", registered))
            + (hint is null ? string.Empty : "; " + hint);
        if (type is null)
        {
            Diagnostics.Error(TypeUnknownCode, Artifact, kind.Pointer(name), $"{kind.Label(name)} names no '{kind.Selector()}'.", list);
        }
        else
        {
            Diagnostics.Error(TypeUnknownCode, Artifact, kind.Pointer(name) + "/" + kind.Selector(),
                _registry.Contains(type)
                    ? $"{kind.Label(name)} names type '{type}', which is not a {kind.TypeNoun()}."
                    : $"{kind.Label(name)} names type '{type}', which this binary does not register.",
                NameSuggestions.DidYouMean(type, registered) ?? list);
        }

        return null;
    }

    /// <summary>The instance entry <paramref name="name"/> names, shared by identity; null (reported) when its type or settings are wrong.</summary>
    /// <param name="settings">The entry's own settings, passed to the factory.</param>
    public T? Acquire<T>(BindingKind kind, string name, string? type, IReadOnlyDictionary<string, JsonElement> settings, string? hint = null)
        where T : class =>
        Acquire<T, T>(kind, name, type, settings, static instance => instance, hint);

    /// <summary>As <see cref="Acquire{T}"/>, sharing what <paramref name="wrap"/> makes of the instance (one wrapper per instance).</summary>
    public TShared? Acquire<T, TShared>(
        BindingKind kind, string name, string? type, IReadOnlyDictionary<string, JsonElement> settings, Func<T, TShared> wrap, string? hint = null)
        where T : class
        where TShared : class
    {
        if (Factory<T>(kind, name, type, hint) is not { } factory)
        {
            return null;
        }

        try
        {
            var spec = BindingSpec.Create(kind, name, type!, settings, BaseDirectory, factory, Artifact);
            return _instances.Acquire(spec, context => wrap(factory.Create(context)));
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            Report(kind, name, ex);
            return null;
        }
    }

    /// <summary>
    /// Checks entry <paramref name="name"/>'s settings by creating an instance that is not kept, for a kind whose instances
    /// are created later, outside the shared ones (update sources, created with their tables); false (reported) when wrong.
    /// </summary>
    public bool Check<T>(IBindingFactory<T> factory, BindingKind kind, string name, IReadOnlyDictionary<string, JsonElement> settings)
        where T : class
    {
        try
        {
            _ = factory.Create(new BindingContext(name, settings, BindingContext.PinBaseDirectory(BaseDirectory), secrets: null));
            return true;
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            Report(kind, name, ex);
            return false;
        }
    }

    // A factory reads only its settings, so these are the binding's to fix, as a BindingException is.
    private static bool IsSettingsError(Exception ex) =>
        ex is BindingException or FormatException or InvalidOperationException or IOException or JsonException or KeyNotFoundException;

    /// <summary>Reports <paramref name="ex"/>, thrown by the factory of entry <paramref name="name"/>, at the entry's settings.</summary>
    public void Report(BindingKind kind, string name, Exception ex)
    {
        if (ex is BindingException binding)
        {
            Diagnostics.Error(SettingsCode, Artifact, kind.Pointer(name) + binding.Pointer, binding.Reason, binding.Hint);
        }
        else
        {
            Diagnostics.Error(SettingsCode, Artifact, kind.Pointer(name), $"{kind.Label(name)}: {ExceptionText.Of(ex)}");
        }
    }
}
