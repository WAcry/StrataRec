using System.Diagnostics.CodeAnalysis;

namespace StrataRec.Core.Bindings;

/// <summary>
/// Creates the instances a binding of one type names: a binding's selector (<c>client</c>, <c>connector</c>,
/// <c>store</c>, <c>source</c>) names <see cref="Type"/>, and the factory turns the binding's own settings into an
/// instance of <typeparamref name="TInstance"/>, the contract its section serves.
/// </summary>
/// <remarks>
/// <para>The engine creates one instance per binding identity and shares it across plans and their concurrent requests, so
/// an instance lives as long as some plan uses it. <see cref="Create"/> only reads the settings; an instance that must load
/// or connect first implements <see cref="ILoadable"/>, and one holding resources implements <see cref="IAsyncDisposable"/>
/// or <see cref="IDisposable"/>. The whole lifetime contract is on <see cref="BindingContext"/>.</para>
/// <para>The update source of a <c>local</c> table is the exception (see <see cref="BindingContext"/>): each table runs one,
/// outside the shared instances, and its context has no secret store. It is never loaded or disposed: it does its I/O,
/// and frees what it holds, in its <c>RunAsync</c>, which the table's host runs and cancels.</para>
/// </remarks>
/// <typeparam name="TInstance">The contract the instance implements, e.g. a service client.</typeparam>
public interface IBindingFactory<out TInstance>
    where TInstance : class
{
    /// <summary>The type and version a binding names, e.g. <c>linear@1</c>: one implementation, whatever the kind of binding.</summary>
    string Type { get; }

    /// <summary>
    /// The C# type the binding's own settings bind to: the single source of their JSON Schema;
    /// <see cref="NoSettings"/> for a type that takes none.
    /// </summary>
    Type SettingsType { get; }

    /// <param name="context">The binding's logical name, its own settings, the base directory relative files resolve
    /// against and the host's secret store.</param>
    /// <exception cref="BindingException">The settings are wrong: at the setting's pointer, with a hint.</exception>
    TInstance Create(BindingContext context);
}

/// <summary>
/// The binding factories a binary provides, of every kind, by the type a binding names (<c>name@version</c>). A type
/// names one implementation: adding a factory of a type already added replaces the earlier one (a host replacing a
/// built-in), whatever contract either serves.
/// </summary>
public sealed class BindingRegistry
{
    private readonly Dictionary<string, IBindingFactory<object>> _factories = new(StringComparer.Ordinal);

    /// <summary>Adds <paramref name="factory"/> under its <see cref="IBindingFactory{TInstance}.Type"/>.</summary>
    public BindingRegistry Add(IBindingFactory<object> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(factory.Type, nameof(factory));
        _factories[factory.Type] = factory;
        return this;
    }

    /// <summary>The factory of <paramref name="type"/> when it creates <typeparamref name="TInstance"/>.</summary>
    public bool TryGet<TInstance>(string type, [MaybeNullWhen(false)] out IBindingFactory<TInstance> factory)
        where TInstance : class
    {
        if (_factories.TryGetValue(type, out var found) && found is IBindingFactory<TInstance> typed)
        {
            factory = typed;
            return true;
        }

        factory = null;
        return false;
    }

    /// <summary>Whether some factory, of whatever contract, has <paramref name="type"/>.</summary>
    public bool Contains(string type) => _factories.ContainsKey(type);

    /// <summary>The types whose factories create <typeparamref name="TInstance"/>, in ordinal order.</summary>
    public List<string> Types<TInstance>()
        where TInstance : class
    {
        var types = new List<string>();
        foreach (var (type, factory) in _factories)
        {
            if (factory is IBindingFactory<TInstance>)
            {
                types.Add(type);
            }
        }

        types.Sort(StringComparer.Ordinal);
        return types;
    }

    /// <summary>Every factory, by type in ordinal order.</summary>
    public List<IBindingFactory<object>> Factories()
    {
        var factories = new List<IBindingFactory<object>>(_factories.Values);
        factories.Sort(static (a, b) => string.CompareOrdinal(a.Type, b.Type));
        return factories;
    }
}
