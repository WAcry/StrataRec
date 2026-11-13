using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Bindings;

namespace StrataRec.Features.Bindings;

/// <summary>The sections of a bindings artifact whose entries name instances (design 6.4).</summary>
public enum BindingSection : byte
{
    /// <summary><c>models</c>: model clients.</summary>
    Models = 1,

    /// <summary><c>candidate_sources</c>: candidate sources.</summary>
    CandidateSources,

    /// <summary><c>sources</c> bound with mode <c>fetch</c> or <c>cache</c>: feature connectors.</summary>
    Sources,

    /// <summary><c>cache.distributed</c> of <c>sources</c> bound with mode <c>cache</c>: distributed cache stores.</summary>
    SourceCaches,
}

/// <summary>
/// What a plan needs from one binding instance (design 6.4): a model client, a candidate source or a feature connector,
/// with the <see cref="Identity"/> that decides whether an instance created for an earlier plan can serve this one. The
/// counterpart of <see cref="Local.LocalTableSpec"/> for everything a binding names that is not a local table.
/// </summary>
public sealed class BindingSpec
{
    private BindingSpec(
        BindingSection section, string name, string type, IReadOnlyDictionary<string, JsonElement> settings, string baseDirectory, object factory,
        string artifact)
    {
        Section = section;
        Name = name;
        Type = type;
        Settings = settings;
        BaseDirectory = baseDirectory;
        Factory = factory;
        Artifact = artifact;
        Pointer = "/" + SectionName(section) + "/" + JsonPointer.Escape(name) + MemberPointer(section);
        Identity = ComputeIdentity(section, name, type, settings, baseDirectory);
    }

    /// <param name="section">Where the binding is.</param>
    /// <param name="name">The logical name, e.g. <c>ctr_ranker</c>.</param>
    /// <param name="type">The client or connector type, e.g. <c>linear@1</c>.</param>
    /// <param name="settings">The binding's own settings, passed to the factory.</param>
    /// <param name="baseDirectory">Where relative paths resolve (relative: against the current directory; null: the current
    /// directory); pinned now (<see cref="BindingContext.PinBaseDirectory"/>), it is part of the identity.</param>
    /// <param name="factory">The factory that creates the instance: instances are only shared between plans that would have
    /// created them with the same factory, so two engines with different registries never share one.</param>
    /// <param name="artifact">The bindings artifact, e.g. <c>bindings:local</c>, for errors.</param>
    public static BindingSpec Create(
        BindingSection section, string name, string type, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory, object factory,
        string artifact)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new BindingSpec(section, name, type, settings, BindingContext.PinBaseDirectory(baseDirectory), factory, artifact);
    }

    public BindingSection Section { get; }

    public string Name { get; }

    public string Type { get; }

    public IReadOnlyDictionary<string, JsonElement> Settings { get; }

    /// <summary>Absolute, pinned when the spec was created.</summary>
    public string BaseDirectory { get; }

    public object Factory { get; }

    /// <summary>The bindings artifact, e.g. <c>bindings:local</c>.</summary>
    public string Artifact { get; }

    /// <summary>The binding's pointer in <see cref="Artifact"/>, e.g. <c>/models/ctr_backup</c>.</summary>
    public string Pointer { get; }

    /// <summary>How messages name the instance, e.g. <c>model 'ctr_backup' (linear@1)</c>.</summary>
    public string Label => Section switch
    {
        BindingSection.Models => "model",
        BindingSection.CandidateSources => "candidate source",
        BindingSection.Sources => "feature source",
        BindingSection.SourceCaches => "distributed cache of feature source",
    } + $" '{Name}' ({Type})";

    /// <summary>
    /// The one rule for sharing an instance across plans: two specs with the same identity (and the same
    /// <see cref="Factory"/>) are served by one instance, so a new plan takes over a loaded model or an open connection
    /// instead of creating it again. The identity is the RFC 8785 canonical JSON of the section, the logical name (the
    /// factory is given it), the type, the binding's own settings as written and the absolute <see cref="BaseDirectory"/>.
    /// What the framework itself reads from a binding (<c>timeout_ms</c>, <c>max_batch</c>, ...) is not part of it, so
    /// changing a timeout never reloads a model; nor is anything else in the configuration. The settings are taken whole,
    /// so a setting added later is part of the identity without anyone remembering to add it.
    /// </summary>
    /// <remarks>Credentials are referenced, never inlined (<see cref="SecretReference"/>): the identity holds no secret, and
    /// a rotated secret keeps the instance, which resolves its secrets when it loads.</remarks>
    public string Identity { get; }

    /// <summary>The section's member name in the bindings artifact.</summary>
    public static string SectionName(BindingSection section) => section switch
    {
        BindingSection.Models => "models",
        BindingSection.CandidateSources => "candidate_sources",
        BindingSection.Sources or BindingSection.SourceCaches => "sources",
    };

    /// <summary>Where the instance's settings are within its entry of the section: the entry itself, or a member of it.</summary>
    public static string MemberPointer(BindingSection section) => section switch
    {
        BindingSection.Models or BindingSection.CandidateSources or BindingSection.Sources => string.Empty,
        BindingSection.SourceCaches => "/cache/distributed",
    };

    /// <summary>The context the factory gets.</summary>
    /// <param name="secrets">The host's secret store; null when it has none (or the plan is never loaded).</param>
    public BindingContext Context(ISecretResolver? secrets) => new(Name, Settings, BaseDirectory, secrets);

    private static string ComputeIdentity(
        BindingSection section, string name, string type, IReadOnlyDictionary<string, JsonElement> settings, string baseDirectory)
    {
        var members = new JsonObject();
        foreach (var (key, value) in settings)
        {
            members[key] = JsonNode.Parse(value.GetRawText());
        }

        var identity = new JsonObject
        {
            ["section"] = SectionName(section) + MemberPointer(section),
            ["name"] = name,
            ["type"] = type,
            ["settings"] = members,
            ["base_directory"] = baseDirectory,
        };
        return JsonCanonicalizer.CanonicalizeToString(identity);
    }
}

/// <summary>
/// Gives a plan being compiled the instance each binding names (design 6.4). The engine's provider (the binding lease in
/// Hosting) shares instances by <see cref="BindingSpec.Identity"/> across plans, loads them before the plan is served
/// and disposes them once nothing can call them; a plan compiled without an engine gets instances that are never loaded.
/// </summary>
public interface IBindingInstances
{
    /// <summary>The instance for <paramref name="spec"/>: one already held with the same identity and factory, or <paramref name="create"/>'s.</summary>
    /// <exception cref="BindingException">The factory rejected the settings.</exception>
    T Acquire<T>(BindingSpec spec, Func<BindingContext, T> create)
        where T : class;
}

/// <summary>Creates a new instance for every acquisition, with no secret store and no load: for compiling pieces of a plan
/// on their own (tests).</summary>
public sealed class UnsharedInstances : IBindingInstances
{
    public static UnsharedInstances Instance { get; } = new();

    public T Acquire<T>(BindingSpec spec, Func<BindingContext, T> create)
        where T : class => create(spec.Context(null));
}
