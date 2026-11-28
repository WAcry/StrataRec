using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting.Plans;

/// <summary>Whether a layer, experiment or arm was added, removed or moved to another layer.</summary>
public enum StructureChangeKind : byte
{
    Added = 1,
    Removed,

    /// <summary>An experiment that runs in another layer: its arms are validated with a different set of others.</summary>
    Moved,
}

/// <summary>
/// A layer, experiment, arm or yield present on one side only, or an experiment in another layer. A yield changes no
/// combination the comparison runs (the baseline and each arm alone) but what a request in both experiments runs; a
/// reversed one is one removed and one added.
/// </summary>
/// <param name="Kind"><c>layer</c>, <c>experiment</c>, <c>arm</c> or <c>yield</c>.</param>
/// <param name="Id">The layer name, experiment id, <c>experiment/arm</c>, or for a yield the experiment that yields.</param>
/// <param name="FromLayer">Its layer on the from side; null when absent there (and for layers and yields).</param>
/// <param name="ToLayer">Its layer on the to side; null when absent there (and for layers and yields).</param>
public sealed record StructureChange(string Kind, string Id, StructureChangeKind Change, string? FromLayer, string? ToLayer)
{
    /// <summary>For a yield, the experiment <see cref="Id"/> yields to; otherwise null.</summary>
    public string? YieldsTo { get; init; }
}

/// <param name="Label"><c>baseline</c>, or the arms joined with <c> + </c>.</param>
/// <param name="Arms">The combination's arms, <c>experiment/arm</c>.</param>
/// <param name="MissingOn"><c>from</c> or <c>to</c> when the combination does not exist on that side; it is then not compared.</param>
/// <param name="Changes">Pointers into <see cref="EffectivePlan.ToDocument"/>, in pointer order; empty when <paramref name="MissingOn"/> is set.</param>
public sealed record CombinationDiff(string Label, IReadOnlyList<string> Arms, string? MissingOn, string? MissingReason, IReadOnlyList<JsonChange> Changes);

/// <summary>
/// One side's plan of a combination holds a value RFC 8785 cannot hold exactly (<see cref="EffectivePlan.ToDocument"/>
/// compares plans in that form), so the sides cannot be compared. It names the side and the combination.
/// </summary>
public sealed class PlanDiffCanonicalizationException : Exception
{
    public PlanDiffCanonicalizationException(string side, string combination, JsonCanonicalizationException inner)
        : base($"The {side} configuration's plan of {combination} cannot be compared: {inner.Message}", inner)
    {
        Side = side;
        Combination = combination;
        Canonicalization = inner;
    }

    /// <summary><c>from</c> or <c>to</c>.</summary>
    public string Side { get; }

    /// <summary>The combination's label (<see cref="PlanDiff.Label"/>).</summary>
    public string Combination { get; }

    /// <summary>The value at fault, its pointer into <see cref="EffectivePlan.ToDocument"/>.</summary>
    public JsonCanonicalizationException Canonicalization { get; }
}

/// <summary>
/// The semantic difference of two compiled configurations by arm combination: per combination, the
/// nodes, parameters, taps, funnel groups and output a request with it executes, compared leaf by leaf
/// (<see cref="EffectivePlan"/>, <see cref="JsonDiff"/>); and the layers, experiments and arms either side lacks.
/// </summary>
/// <remarks>
/// A combination that exists on one side only is reported as missing on the other and not compared What is not a
/// configuration difference is left out: compile times, manifest ids, how many combinations the compiler validated.
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
    /// Without <paramref name="arms"/>: the baseline, then each arm of either side alone, in ordinal order of its id.
    /// With <paramref name="arms"/>: exactly that combination, resolved on each side as a request forcing those arms is
    /// (<see cref="ExecutionPlan.ArmsFromIds"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="arms"/> is valid on neither side (an unknown arm, or two arms of one layer).</exception>
    /// <exception cref="PlanDiffCanonicalizationException">A side's plan holds a value RFC 8785 cannot hold exactly.</exception>
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

            var changes = JsonDiff.Compare(Document(from, fromArms, "from", label), Document(to, toArms, "to", label));
            if (changes.Count > 0)
            {
                diffs.Add(new CombinationDiff(label, combination, null, null, changes));
            }
        }

        return new PlanDiff { Structure = CompareStructure(from, to), Compared = compared, Combinations = diffs };
    }

    /// <summary><c>baseline</c>, or the arms joined with <c> + </c>.</summary>
    public static string Label(IReadOnlyList<string> arms) => arms.Count == 0 ? "baseline" : string.Join(" + ", arms);

    private static JsonObject Document(ExecutionPlan plan, RequestArms arms, string side, string label)
    {
        try
        {
            return EffectivePlan.Create(plan, arms).ToDocument();
        }
        catch (JsonCanonicalizationException ex)
        {
            throw new PlanDiffCanonicalizationException(side, label, ex);
        }
    }

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

        var resolved = plan.ArmsFromIds(arms);
        return plan.NeverRunTogether(resolved) is { } never ? (default, never) : (resolved, null);
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

        var fromYields = Yields(from);
        var toYields = Yields(to);
        foreach (var yield in Union(fromYields.Keys, toYields.Keys))
        {
            var inTo = toYields.TryGetValue(yield, out var edge);
            if (!inTo || !fromYields.ContainsKey(yield))
            {
                edge = inTo ? edge : fromYields[yield];
                changes.Add(new StructureChange("yield", edge.From, inTo ? StructureChangeKind.Added : StructureChangeKind.Removed, null, null) { YieldsTo = edge.To });
            }
        }

        return changes;
    }

    private static SortedDictionary<string, (string From, string To)> Yields(ExecutionPlan plan)
    {
        var yields = new SortedDictionary<string, (string From, string To)>(StringComparer.Ordinal);
        foreach (var experiment in plan.Experiments)
        {
            foreach (var to in experiment.YieldsTo)
            {
                var target = plan.Experiments[to].Id;
                yields[experiment.Id + "\n" + target] = (experiment.Id, target);
            }
        }

        return yields;
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
