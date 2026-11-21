using StrataRec.Core.Bindings;

namespace StrataRec.UnitTests.Core;

/// <summary>One registry for the factories of every kind of binding, by the type a binding names.</summary>
public sealed class BindingRegistryTests
{
    [Fact]
    public void AFactoryOfAMoreSpecificInstance_ServesItsContract()
    {
        // Declaring the factory by the class it creates must not hide it from the contract a section asks for.
        var registry = new BindingRegistry().Add(new Factory<Special>("special@1"));

        Assert.True(registry.TryGet<IThing>("special@1", out var factory));
        Assert.IsType<Special>(factory.Create(Context()));
        Assert.Equal(["special@1"], registry.Types<IThing>());
    }

    [Fact]
    public void ATypeOfAnotherContract_IsNotFound_ButIsKnown()
    {
        var registry = new BindingRegistry().Add(new Factory<Special>("special@1"));

        Assert.False(registry.TryGet<IOther>("special@1", out _));
        Assert.Empty(registry.Types<IOther>());
        Assert.True(registry.Contains("special@1"));
        Assert.False(registry.Contains("other@1"));
    }

    [Fact]
    public void ATypeNamesOneImplementation_TheLastAddedReplacesTheFirst_WhateverItsContract()
    {
        var registry = new BindingRegistry().Add(new Factory<Special>("t@1")).Add(new Factory<OtherThing>("t@1")).Add(new Factory<Special>("a@1"));

        Assert.False(registry.TryGet<IThing>("t@1", out _));
        Assert.True(registry.TryGet<IOther>("t@1", out _));
        Assert.Equal(["a@1", "t@1"], registry.Factories().ConvertAll(f => f.Type));
    }

    private static BindingContext Context() => new("b", new Dictionary<string, System.Text.Json.JsonElement>(), Path.GetTempPath(), secrets: null);

    private interface IThing;

    private interface IOther;

    private sealed class Special : IThing;

    private sealed class OtherThing : IOther;

    private sealed class Factory<T>(string type) : IBindingFactory<T>
        where T : class, new()
    {
        public string Type => type;

        public Type SettingsType => typeof(NoSettings);

        public T Create(BindingContext context) => new();
    }
}
