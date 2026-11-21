using StrataRec.Artifacts.Json;

namespace StrataRec.Artifacts.Model;

/// <summary>The kinds of instance a bindings artifact names (design 6.4): each is one contract, named by one selector in one place.</summary>
public enum BindingKind : byte
{
    /// <summary><c>services.*.client</c>: a service client (a model, a recall source, any service); the settings are the entry's other members.</summary>
    Service = 1,

    /// <summary><c>sources.*.connector</c>: a feature connector.</summary>
    Connector,

    /// <summary><c>sources.*.bootstrap.source</c>: the update source of a <c>local</c> source.</summary>
    UpdateSource,

    /// <summary><c>sources.*.cache.distributed.store</c>: the distributed cache store of a <c>cache</c> source.</summary>
    CacheStore,
}

/// <summary>
/// Where each <see cref="BindingKind"/> is in a bindings artifact and how messages name it: the one table that knows the
/// artifact's layout, which the schema, the identity of shared instances and every error at a binding read.
/// </summary>
public static class BindingKinds
{
    private static readonly Info[] Table =
    [
        default,
        new("services", string.Empty, "client", "service", "service client"),
        new("sources", string.Empty, "connector", "feature source", "feature connector"),
        new("sources", "/bootstrap", "source", "update source of feature source", "update source"),
        new("sources", "/cache/distributed", "store", "distributed cache of feature source", "distributed cache store"),
    ];

    /// <summary>Every kind, in declaration order.</summary>
    public static IReadOnlyList<BindingKind> All { get; } =
        [BindingKind.Service, BindingKind.Connector, BindingKind.UpdateSource, BindingKind.CacheStore];

    /// <summary>The artifact's member holding the entries, e.g. <c>services</c>.</summary>
    public static string Section(this BindingKind kind) => Of(kind).Section;

    /// <summary>Where the instance's settings are within an entry: the entry itself (empty), or a member such as <c>/cache/distributed</c>.</summary>
    public static string Member(this BindingKind kind) => Of(kind).Member;

    /// <summary>The member naming the type, e.g. <c>client</c>.</summary>
    public static string Selector(this BindingKind kind) => Of(kind).Selector;

    /// <summary>How messages name a binding of the kind, e.g. <c>service</c>.</summary>
    public static string Noun(this BindingKind kind) => Of(kind).Noun;

    /// <summary>How messages name what a type of the kind implements, e.g. <c>service client</c>.</summary>
    public static string TypeNoun(this BindingKind kind) => Of(kind).TypeNoun;

    /// <summary>The pointer of entry <paramref name="name"/>'s settings, e.g. <c>/sources/item_store/cache/distributed</c>.</summary>
    public static string Pointer(this BindingKind kind, string name) => "/" + kind.Section() + "/" + JsonPointer.Escape(name) + kind.Member();

    /// <summary>How a sentence starts with entry <paramref name="name"/>, e.g. <c>Model 'ctr_model'</c>.</summary>
    public static string Label(this BindingKind kind, string name)
    {
        var noun = kind.Noun();
        return char.ToUpperInvariant(noun[0]) + noun[1..] + $" '{name}'";
    }

    private static Info Of(BindingKind kind) =>
        (int)kind is > 0 and var index && index < Table.Length ? Table[index] : throw new ArgumentOutOfRangeException(nameof(kind), kind, null);

    private readonly record struct Info(string Section, string Member, string Selector, string Noun, string TypeNoun);
}
