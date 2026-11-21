using StrataRec.Core.Bindings;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

internal static class TestBindings
{
    /// <summary>The built-in binding types plus <paramref name="factories"/> (null ones skipped); one of a built-in's type replaces it.</summary>
    public static BindingRegistry With(params IBindingFactory<object>?[] factories)
    {
        var registry = PlanEnvironment.DefaultBindings();
        foreach (var factory in factories)
        {
            if (factory is not null)
            {
                registry.Add(factory);
            }
        }

        return registry;
    }
}
