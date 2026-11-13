using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Features.Bindings;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>The bindings of a compile that does not serve (design 6.4): one instance per identity and factory object.</summary>
public sealed class UnloadedBindingsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stratarec-config");

    [Fact]
    public void FactoriesThatAreEqualButDistinct_GetInstancesOfTheirOwn()
    {
        var bindings = new UnloadedBindings();
        var a = new ValueFactory("test");
        var b = new ValueFactory("test");
        Assert.Equal(a, b);

        var fromA = bindings.Acquire(Spec(a), a.Create);

        Assert.Same(fromA, bindings.Acquire(Spec(a), a.Create));
        Assert.NotSame(fromA, bindings.Acquire(Spec(b), b.Create));
    }

    [Fact]
    public void AFactoryWhoseHashChangesAsItCreates_GetsItsInstanceAgain()
    {
        var bindings = new UnloadedBindings();
        var factory = new CountingRecordFactory();

        var first = bindings.Acquire(Spec(factory), factory.Create);
        bindings.Acquire(Spec(factory, """{ "uri": "other.bin" }"""), factory.Create);

        Assert.Same(first, bindings.Acquire(Spec(factory), factory.Create));
        Assert.Equal(2, factory.Created);
    }

    private static BindingSpec Spec(object factory, string settings = """{ "uri": "m.bin" }""")
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonDocument.Parse(settings).RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return BindingSpec.Create(BindingSection.Models, "ctr_model", "test@1", values, Root, factory, "bindings:test");
    }

    private sealed record ValueFactory(string Kind)
    {
        public object Create(BindingContext context) => new();
    }

    private sealed record CountingRecordFactory
    {
        public int Created { get; private set; }

        public object Create(BindingContext context)
        {
            Created++;
            return new object();
        }
    }
}
