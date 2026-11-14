using StrataRec.Artifacts.Json;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting.Plans;

/// <summary>Whether a layer, experiment or arm was added, removed or moved to another layer.</summary>
public enum StructureChangeKind : byte
{
    Added = 1,
    Removed,

    /// <summary>An experiment that runs in another layer: its arms are validated with a different set of others (design 11.5).</summary>
    Moved,
}

/// <summary>A layer, experiment or arm present on one side only, or an experiment in another layer.</summary>
/// <param name="Kind"><c>layer</c>, <c>experiment</c> or <c>arm</c>.</param>
/// <param name="Id">The layer name, experiment id or <c>experiment/arm</c>.</param>
/// <param name="Change">What changed.</param>
/// <param name="FromLayer">Its layer on the from side; null when absent there (and for layers).</param>
/// <param name="ToLayer">Its layer on the to side; null when absent there (and for layers).</param>
public sealed record StructureChange(string Kind, string Id, StructureChangeKind Change, string? FromLayer, string? ToLayer);

/// <summary>What differs for one arm combination.</summary>
/// <param name="Label"><c>baseline</c>, or the arms joined with <c> + </c>.</param>
/// <param name="Arms">The combination's arms, <c>experiment/arm</c>.</param>
/// <param name="MissingOn"><c>from</c> or <c>to</c> when the combination does not exist on that side; it is then not compared.</param>
/// <param name="MissingReason">Why it does not exist there.</param>
/// <param name="Changes">Pointers into <see cref="EffectivePlan.ToDocument"/>, in pointer order; empty when <paramref name="MissingOn"/> is set.</param>
public sealed record CombinationDiff(string Label, IReadOnlyList<string> Arms, string? MissingOn, string? MissingReason, IReadOnlyList<JsonChange> Changes);

/// <summary>
/// The semantic difference of two compiled configurations by arm combination (design 13.4 item 3): per combination, the
/// nodes, parameters, taps, funnel groups and output a request with it executes, compared leaf by leaf
/// (<see cref="EffectivePlan"/>, <see cref="JsonDiff"/>); and the layers, experiments and arms either side lacks.
/// </summary>
/// <remarks>
/// A combination that exists on one side only is reported as missing on the other and not compared: there is no plan to
/// compare with, and inventing one (say, the baseline) would report every change of the arm as a change of the
/// configuration. What is not a configuration difference is left out: compile times, manifest ids, how many
/// combinations the compiler validated.
/// </remarks>
public sealed class PlanDiff
{
    public required IReadOnlyList<StructureChange> Structure { get; init; }

    /// <summary>Labels of every combination compared (or found missing on a side), in report order.</summary>
    public required IReadOnlyList<string> Compared { get; init; }

    /// <summary>The combinations that differ, or exist on one side only.</summary>
    public required IReadOnlyList<CombinationDiff> Combinations { get; init; }

    public bool HasDifferences => Structure.Count > 0 || Combinations.Count > 0;

    /// <summary>
    /// Compares two plans. Without <paramref name="arms"/>: the baseline, then each arm of either side alone, in ordinal
    /// order of its id. With <paramref name="arms"/>: exactly that combination, resolved on each side as a request
    /// forcing those arms is (<see cref="ExecutionPlan.ArmsFromIds"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="arms"/> is valid on neither side (an unknown arm, or two arms of one layer).</exception>
    public static PlanDiff Compare(ExecutionPlan from, ExecutionPlan to, IReadOnlyList<string>? arms = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var combinations = new List<IReadOnlyList<string>>();
        if (arms is { Count: > 0 })
        {
            combinations.Add(arms);
        }
        else
        {
            combinations.Add(Array.Empty<string>());
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var arm in from.Arms)
            {
                ids.Add(arm.Id);
            }

            foreach (var arm in to.Arms)
            {
                ids.Add(arm.Id);
            }

            foreach (var id in ids)
            {
                combinations.Add(new[] { id });
            }
        }

        var compared = new List<string>();
        var diffs = new List<CombinationDiff>();
        foreach (var combination in combinations)
        {
            var label = Label(combination);
            compared.Add(label);
            var (fromArms, fromError) = Resolve(from, combination);
            var (toArms, toError) = Resolve(to, combination);
            if (fromError is not null && toError is not null)
            {
                throw new ArgumentException($"{label} exists on neither side: {fromError} in the from configuration; {toError} in the to configuration.");
            }

            if (fromError is not null || toError is not null)
            {
                var side = fromError is not null ? "from" : "to";
                diffs.Add(new CombinationDiff(label, combination, side, $"{fromError ?? toError} in the {side} configuration", Array.Empty<JsonChange>()));
                continue;
            }

            var changes = JsonDiff.Compare(EffectivePlan.Create(from, fromArms).ToDocument(), EffectivePlan.Create(to, toArms).ToDocument());
            if (changes.Count > 0)
            {
                diffs.Add(new CombinationDiff(label, combination, null, null, changes));
            }
        }

        return new PlanDiff { Structure = CompareStructure(from, to), Compared = compared, Combinations = diffs };
    }

    /// <summary><c>baseline</c>, or the arms joined with <c> + </c>.</summary>
    public static string Label(IReadOnlyList<string> arms) => arms.Count == 0 ? "baseline" : string.Join(" + ", arms);

    /// <summary>The combination on one side, or why it does not exist there (the conditions of <see cref="ExecutionPlan.ArmsFromIds"/>).</summary>
    private static (RequestArms Arms, string? Error) Resolve(ExecutionPlan plan, IReadOnlyList<string> arms)
    {
        var byLayer = new Dictionary<int, string>();
        foreach (var id in arms)
        {
            if (!plan.TryGetArm(id, out var arm))
            {
                return (default, $"arm '{id}' does not exist");
            }

            if (byLayer.TryGetValue(arm.LayerIndex, out var other) && other != id)
            {
                return (default, $"arms '{other}' and '{id}' are both in layer '{plan.Layers[arm.LayerIndex].Name}'");
            }

            byLayer[arm.LayerIndex] = id;
        }

        return (plan.ArmsFromIds(arms), null);
    }

    private static List<StructureChange> CompareStructure(ExecutionPlan from, ExecutionPlan to)
    {
        var changes = new List<StructureChange>();
        var fromLayers = Layers(from);
        var toLayers = Layers(to);
        foreach (var layer in Union(fromLayers.Keys, toLayers.Keys))
        {
            if (!toLayers.ContainsKey(layer))
            {
                changes.Add(new StructureChange("layer", layer, StructureChangeKind.Removed, null, null));
            }
            else if (!fromLayers.ContainsKey(layer))
            {
                changes.Add(new StructureChange("layer", layer, StructureChangeKind.Added, null, null));
            }
        }

        var fromExperiments = Experiments(from);
        var toExperiments = Experiments(to);
        foreach (var id in Union(fromExperiments.Keys, toExperiments.Keys))
        {
            var inFrom = fromExperiments.TryGetValue(id, out var fromLayer);
            var inTo = toExperiments.TryGetValue(id, out var toLayer);
            if (inFrom != inTo || fromLayer != toLayer)
            {
                changes.Add(new StructureChange("experiment", id, !inTo ? StructureChangeKind.Removed : !inFrom ? StructureChangeKind.Added : StructureChangeKind.Moved,
                    fromLayer, toLayer));
            }
        }

        // An arm of a moved experiment moved with it: reported once, on the experiment.
        var fromArms = Arms(from);
        var toArms = Arms(to);
        foreach (var id in Union(fromArms.Keys, toArms.Keys))
        {
            var inFrom = fromArms.TryGetValue(id, out var fromLayer);
            var inTo = toArms.TryGetValue(id, out var toLayer);
            if (inFrom != inTo)
            {
                changes.Add(new StructureChange("arm", id, inTo ? StructureChangeKind.Added : StructureChangeKind.Removed, fromLayer, toLayer));
            }
        }

        return changes;
    }

    private static SortedDictionary<string, string?> Layers(ExecutionPlan plan)
    {
        var layers = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var layer in plan.Layers)
        {
            layers[layer.Name] = null;
        }

        return layers;
    }

    private static SortedDictionary<string, string?> Experiments(ExecutionPlan plan)
    {
        var experiments = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var experiment in plan.Experiments)
        {
            experiments[experiment.Id] = plan.Layers[experiment.LayerIndex].Name;
        }

        return experiments;
    }

    private static SortedDictionary<string, string?> Arms(ExecutionPlan plan)
    {
        var arms = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var arm in plan.Arms)
        {
            arms[arm.Id] = plan.Layers[arm.LayerIndex].Name;
        }

        return arms;
    }

    private static SortedSet<string> Union(IEnumerable<string> a, IEnumerable<string> b)
    {
        var union = new SortedSet<string>(a, StringComparer.Ordinal);
        union.UnionWith(b);
        return union;
    }
}
