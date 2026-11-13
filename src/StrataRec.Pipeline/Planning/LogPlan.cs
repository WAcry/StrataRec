using System.Text.Json.Nodes;
using StrataRec.Expressions;
using StrataRec.Logging;

namespace StrataRec.Pipeline.Planning;

/// <summary>A compiled funnel group: one request-level sampling decision shared by its taps (design 8.2).</summary>
public sealed class CompiledFunnelGroup
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    /// <summary>Hash seed of the group; also seeds row priorities (design 8.4).</summary>
    public required long Seed { get; init; }

    public required double Rate { get; init; }

    /// <summary>Key expressions; the first non-missing value is the sampling key.</summary>
    public required CompiledExpression[] Keys { get; init; }
}

/// <summary>Which rows a tap records (design 8.3).</summary>
public enum TapScope : byte
{
    /// <summary>Rows that entered the node.</summary>
    Entered = 1,

    /// <summary>Rows alive after the node.</summary>
    Survived,

    /// <summary>Rows the node removed.</summary>
    Dropped,
}

/// <summary>
/// One compiled variant of a log tap (design 8.3, 8.6): row selection, sampling and column expressions are fixed at
/// load time, so capture on the request thread is row selection plus copying.
/// </summary>
public sealed class CompiledTap
{
    public required string TapId { get; init; }

    public required JsonObject Definition { get; init; }

    public required int Group { get; init; }

    public required LogDelivery Delivery { get; init; }

    /// <summary>Node id the tap fires after; null for output taps.</summary>
    public string? AttachAfter { get; init; }

    public bool AttachOutput => AttachAfter is null;

    public required TapScope Scope { get; init; }

    public CompiledExpression? Filter { get; init; }

    public double Rate { get; init; } = 1.0;

    public int? MaxRows { get; init; }

    public required (string Name, CompiledExpression Expression)[] Context { get; init; }

    public required (string Name, CompiledExpression Expression)[] Columns { get; init; }

    public string? Purpose { get; init; }

    /// <summary>Content id of the tap's schema: definition plus resolved column types (lineage, design 8.7).</summary>
    public required string SchemaId { get; init; }

    /// <summary>Output columns (logical ids) the tap reads through bindings; must be produced before the attach point.</summary>
    public required int[] ReadOutputs { get; init; }

    /// <summary>Candidate feature indices the tap reads; must have been fetched before the attach point.</summary>
    public required int[] ReadFeatures { get; init; }

    /// <summary>Context feature indices read by context fields, keys and filters (prefetched with the request).</summary>
    public required int[] ReadContextFeatures { get; init; }

    /// <summary>Log column → logical column it reads directly (for <c>column_producers</c>), or -1.</summary>
    public required int[] ColumnSources { get; init; }

    /// <summary>Log column → node of a pinned version it reads (<c>col.x@node</c>), else null.</summary>
    public required string?[] PinnedProducers { get; init; }
}

/// <summary>Variants of one tap per arm combination of the layers that patch it (design 12.3: taps vary on their own).</summary>
public sealed class TapSlot
{
    public required string TapId { get; init; }

    public required int[] TouchingLayers { get; init; }

    public required Dictionary<int, int>[] LocalIndex { get; init; }

    public required int[] Radix { get; init; }

    public required CompiledTap?[] Variants { get; init; }

    public CompiledTap? Select(in RequestArms arms)
    {
        var index = 0;
        for (var i = 0; i < TouchingLayers.Length; i++)
        {
            var arm = arms.ArmIn(TouchingLayers[i]);
            var local = arm >= 0 && LocalIndex[i].TryGetValue(arm, out var l) ? l : 0;
            index = (index * Radix[i]) + local;
        }

        return Variants[index];
    }
}

/// <summary>The logging part of a plan.</summary>
public sealed class LogPlan
{
    public static LogPlan Empty { get; } = new() { Groups = Array.Empty<CompiledFunnelGroup>(), Taps = Array.Empty<TapSlot>() };

    public required CompiledFunnelGroup[] Groups { get; init; }

    public required TapSlot[] Taps { get; init; }
}
