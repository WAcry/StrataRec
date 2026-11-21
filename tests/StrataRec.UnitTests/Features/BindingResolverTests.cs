using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Features.Bindings;

namespace StrataRec.UnitTests.Features;

/// <summary>The one path from a bindings entry to its instance: the same codes, places and words for every kind.</summary>
public sealed class BindingResolverTests
{
    [Theory]
    [InlineData(BindingKind.Service, "/services/b", "Service 'b' names no 'client'.", "this binary registers no service client")]
    [InlineData(BindingKind.UpdateSource, "/sources/b/bootstrap", "Update source of feature source 'b' names no 'source'.", "this binary registers no update source")]
    [InlineData(BindingKind.CacheStore, "/sources/b/cache/distributed", "Distributed cache of feature source 'b' names no 'store'.",
        "this binary registers no distributed cache store")]
    public void AnEntryNamingNoType_IsAnErrorAtTheEntry(BindingKind kind, string pointer, string message, string hint)
    {
        var (resolver, diagnostics) = Resolver(new BindingRegistry());

        Assert.Null(resolver.Acquire<IThing>(kind, "b", type: null, Settings("{}")));

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal(("BINDING_TYPE_UNKNOWN", "bindings:test", pointer, message, hint),
            (error.Code, error.Artifact, error.Pointer, error.Message, error.Hint));
    }

    [Fact]
    public void AnUnknownType_ListsTheRegisteredOnes_ThenWhatElseToDo()
    {
        var (resolver, diagnostics) = Resolver(new BindingRegistry().Add(new ThingFactory("thing@1")).Add(new ThingFactory("other_thing@1")));

        Assert.Null(resolver.Acquire<IThing>(BindingKind.CacheStore, "b", "zzz@9", Settings("{}"), hint: "or remove 'distributed'"));

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal(("/sources/b/cache/distributed/store", "registered: other_thing@1, thing@1; or remove 'distributed'"), (error.Pointer, error.Hint));
    }

    [Fact]
    public void ASettingsError_IsAtTheSetting_AndAnyOtherFactoryError_AtTheEntry()
    {
        var (resolver, diagnostics) = Resolver(new BindingRegistry()
            .Add(new ThingFactory("binding@1", _ => throw new BindingException("/uri", "'uri' is not a URI.", "write a URI")))
            .Add(new ThingFactory("format@1", _ => throw new FormatException("bad number"))));

        Assert.Null(resolver.Acquire<IThing>(BindingKind.Connector, "a", "binding@1", Settings("{}")));
        Assert.Null(resolver.Acquire<IThing>(BindingKind.Connector, "b", "format@1", Settings("{}")));

        Assert.Equal(
            new[]
            {
                ("BINDING_SETTINGS", "/sources/a/uri", "'uri' is not a URI.", (string?)"write a URI"),
                ("BINDING_SETTINGS", "/sources/b", "Feature source 'b': bad number", null),
            },
            diagnostics.Items.Select(d => (d.Code, d.Pointer, d.Message, d.Hint)));
    }

    [Fact]
    public void AnInstance_IsWrappedOnce_AndSharedByIdentity()
    {
        var created = 0;
        var registry = new BindingRegistry().Add(new ThingFactory("thing@1", _ =>
        {
            created++;
            return new Thing();
        }));
        var instances = new CountingInstances();
        var resolver = new BindingResolver(registry, instances, Path.GetTempPath(), "bindings:test", new DiagnosticBag());

        var first = resolver.Acquire<IThing, Wrapped>(BindingKind.CacheStore, "b", "thing@1", Settings("""{ "x": 1 }"""), static t => new Wrapped(t));
        var second = resolver.Acquire<IThing, Wrapped>(BindingKind.CacheStore, "b", "thing@1", Settings("""{ "x": 1 }"""), static t => new Wrapped(t));

        Assert.Same(first, second);
        Assert.Equal(1, created);
    }

    private static (BindingResolver Resolver, DiagnosticBag Diagnostics) Resolver(BindingRegistry registry)
    {
        var diagnostics = new DiagnosticBag();
        return (new BindingResolver(registry, UnsharedInstances.Instance, Path.GetTempPath(), "bindings:test", diagnostics), diagnostics);
    }

    private static Dictionary<string, JsonElement> Settings(string json)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonDocument.Parse(json).RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return values;
    }

    private interface IThing;

    private sealed class Thing : IThing;

    private sealed record Wrapped(IThing Inner);

    private sealed class ThingFactory(string type, Func<BindingContext, IThing>? create = null) : IBindingFactory<IThing>
    {
        public string Type => type;

        public Type SettingsType => typeof(NoSettings);

        public IThing Create(BindingContext context) => create is null ? new Thing() : create(context);
    }

    /// <summary>Shares by key, as an engine's instances do.</summary>
    private sealed class CountingInstances : IBindingInstances
    {
        private readonly Dictionary<BindingKey, object> _instances = new();

        public T Acquire<T>(BindingSpec spec, Func<BindingContext, T> create)
            where T : class
        {
            if (!_instances.TryGetValue(spec.Key, out var instance))
            {
                instance = create(spec.Context(secrets: null));
                _instances[spec.Key] = instance;
            }

            return (T)instance;
        }
    }
}
