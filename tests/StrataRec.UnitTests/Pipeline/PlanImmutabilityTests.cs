using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// A compiled plan is shared by every concurrent request and handed to hosts for inspection, so its public surface is
/// read-only: a host that sorts <c>plan.Nodes</c> for display or edits a node's definition must not be able to change
/// what requests run. This walks every plan type reachable from <see cref="ExecutionPlan"/> (the planning and feature
/// runtime types) and fails on a public setter or a mutable collection or JSON tree, so a member added later stays so.
/// </summary>
public sealed class PlanImmutabilityTests
{
    private static readonly string[] Namespaces = [typeof(ExecutionPlan).Namespace!, typeof(FeaturePlan).Namespace!];

    [Fact]
    public void EveryPlanTypeReachableFromTheExecutionPlan_HasNoPublicSetterAndNoMutableCollection()
    {
        var problems = new List<string>();
        var visited = new HashSet<Type>();
        var pending = new Queue<Type>();
        pending.Enqueue(typeof(ExecutionPlan));
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = $"{type.Name}.{property.Name}";
                if (property.SetMethod is { IsPublic: true } setter && !IsInit(setter))
                {
                    problems.Add($"{name} has a public setter");
                }

                foreach (var problem in Mutable(property.PropertyType))
                {
                    problems.Add($"{name}: {problem}");
                }

                foreach (var reached in PlanTypes(property.PropertyType))
                {
                    pending.Enqueue(reached);
                }
            }
        }

        Assert.Empty(problems);
        Assert.Contains(typeof(CompiledNode), visited); // the walk reaches the types it guards
        Assert.Contains(typeof(CompiledTap), visited);
        Assert.Contains(typeof(ParamLeaf), visited);
        Assert.Contains(typeof(FetchGroup), visited);
        Assert.Contains(typeof(LocalGroupTable), visited);
    }

    private static bool IsInit(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    /// <summary>Why a property type lets a reader change the plan, if it does: an array, a mutable collection or a JSON tree.</summary>
    private static IEnumerable<string> Mutable(Type type)
    {
        if (type.IsArray)
        {
            yield return $"{type.Name} is an array";
            yield break;
        }

        if (typeof(JsonNode).IsAssignableFrom(type))
        {
            yield return $"{type.Name} is a mutable JSON tree";
            yield break;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>)
                || definition == typeof(SortedSet<>) || definition == typeof(SortedDictionary<,>))
            {
                yield return $"{type.Name} is a mutable collection";
                yield break;
            }

            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var problem in Mutable(argument))
                {
                    yield return problem;
                }
            }
        }
    }

    /// <summary>The plan types a property type holds, through read-only collections, tuples and nullables.</summary>
    private static IEnumerable<Type> PlanTypes(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(ImmutableArray<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyDictionary<,>)
                || definition == typeof(FrozenDictionary<,>) || definition == typeof(Nullable<>) || type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal))
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    foreach (var reached in PlanTypes(argument))
                    {
                        yield return reached;
                    }
                }
            }

            yield break;
        }

        if (!type.IsEnum && Namespaces.Contains(type.Namespace))
        {
            yield return type;
        }
    }
}
