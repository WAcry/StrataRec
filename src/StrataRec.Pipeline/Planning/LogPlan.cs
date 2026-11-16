using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
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
    public required ImmutableArray<CompiledExpression> Keys { get; init; }
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

    /// <summary>The fully merged definition this variant was compiled from, read-only like <see cref="CompiledNode.Definition"/>.</summary>
    public required JsonElement Definition { get; init; }

    public required int Group { get; init; }

    public required LogDelivery Delivery { get; init; }

    /// <summary>Node id the tap fires after; null for output taps.</summary>
    public string? AttachAfter { get; init; }

    public bool AttachOutput => AttachAfter is null;

    public required TapScope Scope { get; init; }

    public CompiledExpression? Filter { get; init; }

    public double Rate { get; init; } = 1.0;

    public int? MaxRows { get; init; }

    public required ImmutableArray<(string Name, CompiledExpression Expression)> Context { get; init; }

    public required ImmutableArray<(string Name, CompiledExpression Expression)> Columns { get; init; }

    /// <summary>The tap's schema (lineage, design 8.7): its columns and context fields with their types and expressions,
    /// and as its id the content id of the definition plus the resolved types. Every batch of the tap shares it.</summary>
    public required Logging.LogSchema Schema { get; init; }

    /// <summary>Output columns (logical ids) the tap reads through bindings; must be produced before the attach point.</summary>
    public required ImmutableArray<int> ReadOutputs { get; init; }

    /// <summary>Every name the tap's expressions bind (<see cref="CompiledNode.Requires"/>): a tap compiles after every node.</summary>
    public ImmutableArray<NameRef> Requires { get; init; } = [];

    /// <summary>Candidate feature indices the tap reads; must have been fetched before the attach point.</summary>
    public required ImmutableArray<int> ReadFeatures { get; init; }

    /// <summary>Context feature indices read by context fields, keys and filters (prefetched with the request).</summary>
    public required ImmutableArray<int> ReadContextFeatures { get; init; }

    /// <summary>Log column → logical column it reads directly (for <c>column_producers</c>), or -1.</summary>
    public required ImmutableArray<int> ColumnSources { get; init; }

    /// <summary>Log column → node of a pinned version it reads (<c>col.x@node</c>), else null.</summary>
    public required ImmutableArray<string?> PinnedProducers { get; init; }

    /// <summary>Nodes whose pinned versions any expression of the tap reads; they must not run after the attach point.</summary>
    public ImmutableArray<string> PinnedNodes { get; init; } = [];

    /// <summary>The pinned versions any expression of the tap reads (<c>col.x@node</c>), by node and column.</summary>
    public ImmutableArray<VersionRead> VersionReads { get; init; } = [];
}

/// <summary>Variants of one tap per arm combination of the layers that patch it (design 12.3: taps vary on their own).</summary>
public sealed class TapSlot
{
    public required string TapId { get; init; }

    public required ImmutableArray<int> TouchingLayers { get; init; }

    public required ImmutableArray<FrozenDictionary<int, int>> LocalIndex { get; init; }

    public required ImmutableArray<int> Radix { get; init; }

    public required ImmutableArray<CompiledTap?> Variants { get; init; }

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
    public static LogPlan Empty { get; } = new() { Groups = [], Taps = [] };

    public required ImmutableArray<CompiledFunnelGroup> Groups { get; init; }

    public required ImmutableArray<TapSlot> Taps { get; init; }
}
