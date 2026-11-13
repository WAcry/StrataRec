using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Compilation;

/// <summary>Validation passes over arm combinations (design 11.5, 11.6).</summary>
internal sealed partial class UnifiedCompilation
{
    internal sealed class CombinationReport
    {
        public int Checked { get; set; }

        public bool Exhaustive { get; set; } = true;
    }

    /// <summary>
    /// Data-flow check per arm combination (design 11.5 check 3): every non-nullable column read has an unskippable
    /// producer before it, fixed columns have one producer per path, same-order nodes are independent. All
    /// combinations of the layers that touch nodes are enumerated when there are at most
    /// <see cref="PlanCompileRequest.MaxCombinationsChecked"/>; otherwise every single arm, every co-occurring pair, and
    /// for each non-nullable column every combination of the layers that touch its producers and readers.
    /// </summary>
    private void ValidateCombinations(TimelineSlot[] timeline, Dictionary<string, TimelineSlot> fallbacks, OutputPlan output, CombinationReport report)
    {
        // Layers that touch nodes, and per layer the arms that touch nodes (others behave like "not assigned" here).
        var choices = new SortedDictionary<int, List<int>>();
        var spaces = new List<VariantSpace>();
        foreach (var (_, v) in _variants)
        {
            spaces.Add(v);
        }

        foreach (var (_, t) in _taps)
        {
            spaces.Add(t);
        }

        foreach (var v in spaces)
        {
            for (var i = 0; i < v.Layers.Length; i++)
            {
                if (!choices.TryGetValue(v.Layers[i], out var list))
                {
                    list = new List<int>();
                    choices[v.Layers[i]] = list;
                }

                foreach (var arm in v.LocalArms[i])
                {
                    if (!list.Contains(arm))
                    {
                        list.Add(arm);
                    }
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allLayers = new List<int>(choices.Keys);
        if (Product(choices, allLayers) <= _request.MaxCombinationsChecked)
        {
            Enumerate(choices, allLayers, combination => Check(combination, timeline, fallbacks, output, seen, report));
            return;
        }

        report.Exhaustive = false;
        var baseline = NewCombination();
        Check(baseline, timeline, fallbacks, output, seen, report);
        foreach (var a in _arms)
        {
            if (!a.Valid || !choices.ContainsKey(a.LayerIndex))
            {
                continue;
            }

            var single = NewCombination();
            single[a.LayerIndex] = a.Index;
            Check(single, timeline, fallbacks, output, seen, report);
            foreach (var b in _arms)
            {
                if (b.Index > a.Index && b.Valid && b.LayerIndex != a.LayerIndex && choices.ContainsKey(b.LayerIndex))
                {
                    var pair = (int[])single.Clone();
                    pair[b.LayerIndex] = b.Index;
                    Check(pair, timeline, fallbacks, output, seen, report);
                }
            }
        }

        // Per column: the layers that can change who produces or reads it (design 11.5 check 3).
        var columnLayers = new Dictionary<int, SortedSet<int>>();
        foreach (var (_, v) in _variants)
        {
            foreach (var node in v.Variants)
            {
                if (node is null)
                {
                    continue;
                }

                foreach (var o in node.Outputs)
                {
                    AddLayers(columnLayers, o.LogicalId, v.Layers);
                }

                foreach (var r in node.RequiredOutputs)
                {
                    AddLayers(columnLayers, r, v.Layers);
                }
            }
        }

        foreach (var (logical, layers) in columnLayers)
        {
            var column = _builder.Schema.Columns[logical];
            if (column.Type.Nullable || layers.Count < 3)
            {
                continue; // nullable columns need no producer; one or two layers are covered by singles and pairs
            }

            var list = new List<int>(layers);
            if (Product(choices, list) > _request.MaxCombinationsChecked)
            {
                // Without a complete check the plan could leave a non-nullable column without a producer for some
                // assignment (design 11.5 check 3), so it is rejected rather than published on a warning.
                _diagnostics.Error("COMBINATION_CHECK_LIMIT", _pipelineLabel, column.Pointer,
                    $"Column '{column.Name}' is affected by arms in {list.Count} layers ({Product(choices, list)} combinations, limit {_request.MaxCombinationsChecked}); not every combination can be checked for a missing producer.",
                    "put experiments that change producers or readers of this column into fewer layers, or graduate some of them");
                continue;
            }

            Enumerate(choices, list, combination => Check(combination, timeline, fallbacks, output, seen, report));
        }
    }

    private void Check(int[] combination, TimelineSlot[] timeline, Dictionary<string, TimelineSlot> fallbacks, OutputPlan output, HashSet<string> seen, CombinationReport report)
    {
        if (!seen.Add(string.Join(",", combination)))
        {
            return;
        }

        var arms = new RequestArms(combination);
        foreach (var (_, v) in _variants)
        {
            if (v.Failed[v.IndexOf(arms)])
            {
                return; // already reported as a compile error
            }
        }

        var taps = new List<CompiledTap>();
        foreach (var (_, t) in _taps)
        {
            var index = t.IndexOf(arms);
            if (t.Failed[index])
            {
                return;
            }

            if (t.Variants[index] is { } tap)
            {
                taps.Add(tap);
            }
        }

        var nodes = new List<CompiledNode>();
        foreach (var slot in timeline)
        {
            if (slot.Select(arms) is { } node)
            {
                nodes.Add(node);
            }
        }

        var selectedFallbacks = new Dictionary<string, CompiledNode>(StringComparer.Ordinal);
        foreach (var (id, slot) in fallbacks)
        {
            if (slot.Select(arms) is { } node)
            {
                selectedFallbacks[id] = node;
            }
        }

        var assigned = new List<ArmPatch>();
        foreach (var arm in combination)
        {
            if (arm >= 0)
            {
                assigned.Add(_arms[arm]);
            }
        }

        var start = _diagnostics.Count;
        _builder.ValidateDataFlow(nodes, selectedFallbacks, output, _pipelineLabel, taps);
        Settle(start, assigned);
        report.Checked++;
    }

    /// <summary>
    /// Checks every pair of arms that can be assigned together (any two of different layers) for path conflicts,
    /// same-order placements (errors) and interactions (warnings for review), design 11.5.
    /// </summary>
    private void ValidatePairs()
    {
        var placed = new Dictionary<int, Dictionary<int, string>>();
        var writes = new Dictionary<int, Dictionary<int, string>>();
        var addedReads = new Dictionary<int, Dictionary<int, string>>();
        foreach (var arm in _arms)
        {
            if (arm.Valid)
            {
                DescribeArm(arm, out var p, out var w, out var r);
                placed[arm.Index] = p;
                writes[arm.Index] = w;
                addedReads[arm.Index] = r;
            }
        }

        foreach (var a in _arms)
        {
            foreach (var b in _arms)
            {
                if (b.Index <= a.Index || !a.Valid || !b.Valid || a.LayerIndex == b.LayerIndex)
                {
                    continue;
                }

                foreach (var conflict in MergePatchAnalyzer.FindConflicts(a.Footprint, b.Footprint))
                {
                    if (IsAddition(a, conflict.First) && IsAddition(b, conflict.Second))
                    {
                        continue; // identical re-adds are fine; different ones are ADD_CONFLICT
                    }

                    _diagnostics.Error("PATCH_CONFLICT", a.Artifact, a.PointerPrefix + conflict.First.Path,
                        $"Arms '{a.Id}' and '{b.Id}' (layers '{_layers[a.LayerIndex]}' and '{_layers[b.LayerIndex]}') can be assigned to the same request and both change '{conflict.First.Path}'"
                        + (conflict.First.Path.Equals(conflict.Second.Path) ? "." : $" / '{conflict.Second.Path}'."),
                        "put the experiments in the same layer so their arms exclude each other, or combine them into one experiment (design 11.5)");
                }

                foreach (var (order, node) in placed[a.Index])
                {
                    if (placed[b.Index].TryGetValue(order, out var other))
                    {
                        _diagnostics.Error("ORDER_COLLISION", a.Artifact, a.PointerPrefix + "/nodes/" + JsonPointer.Escape(node) + "/order",
                            $"Arms '{a.Id}' and '{b.Id}' can be assigned together and both place a node at order {order} ('{node}' and '{other}').",
                            "pick distinct orders; orders are spaced by 100 so there is room in between");
                    }
                }

                Interactions(a, b, writes, addedReads);
                Interactions(b, a, writes, addedReads);
            }
        }
    }

    private void Interactions(ArmPatch a, ArmPatch b, Dictionary<int, Dictionary<int, string>> writes, Dictionary<int, Dictionary<int, string>> addedReads)
    {
        foreach (var (logical, node) in writes[a.Index])
        {
            var column = _builder.Schema.Columns[logical];
            if (a.Index < b.Index && column.Flow && writes[b.Index].TryGetValue(logical, out var otherNode))
            {
                _diagnostics.Warning("INTERACTION_FLOW_COLUMN", a.Artifact, a.PointerPrefix + "/nodes/" + JsonPointer.Escape(node),
                    $"Arms '{a.Id}' ('{node}') and '{b.Id}' ('{otherNode}') can be assigned together and both write flow column '{column.Name}'.",
                    "confirm in review that the two changes compose; otherwise put the experiments in the same layer (design 11.5)");
            }

            if (addedReads[b.Index].TryGetValue(logical, out var reader))
            {
                _diagnostics.Warning("INTERACTION_READ", b.Artifact, b.PointerPrefix + "/nodes/" + JsonPointer.Escape(reader),
                    $"Node '{reader}' added by '{b.Id}' reads '{column.Name}', which '{a.Id}' changes through '{node}'; the two arms can be assigned together.",
                    "confirm in review that the added node should see the other experiment's values; otherwise put the experiments in the same layer");
            }
        }
    }

    /// <summary>
    /// The column footprint of an arm (design 11.5 item 2) from its single-arm variants: orders where it places nodes,
    /// columns its touched nodes write, and columns its added nodes read.
    /// </summary>
    private void DescribeArm(ArmPatch arm, out Dictionary<int, string> placed, out Dictionary<int, string> writes, out Dictionary<int, string> addedReads)
    {
        placed = new Dictionary<int, string>();
        writes = new Dictionary<int, string>();
        addedReads = new Dictionary<int, string>();
        var only = OnlyArm(arm.Index);
        foreach (var nodeId in arm.TouchedNodes)
        {
            if (!_variants.TryGetValue(nodeId, out var v))
            {
                continue;
            }

            var index = v.IndexOf(only);
            var node = v.Variants[index];
            if (node is null)
            {
                continue;
            }

            var baseOrder = _view.Nodes[nodeId] is JsonObject b && b["order"] is JsonValue o && o.TryGetValue<int>(out var bo) ? bo : (int?)null;
            if (node.Order is { } order && (arm.AddedNodes.Contains(nodeId) || order != baseOrder))
            {
                placed[order] = nodeId;
            }

            foreach (var output in node.Outputs)
            {
                writes.TryAdd(output.LogicalId, nodeId);
            }

            if (arm.AddedNodes.Contains(nodeId))
            {
                foreach (var read in node.ReadColumns)
                {
                    addedReads.TryAdd(read, nodeId);
                }
            }
        }
    }

    private static bool IsAddition(ArmPatch arm, PatchOperation op)
    {
        if (op.Kind != PatchOpKind.Add || op.Path.Depth != 2)
        {
            return false;
        }

        return arm.Additions.TryGetValue(op.Path.Tokens[0], out var entries) && entries.ContainsKey(op.Path.Tokens[1]);
    }

    /// <summary>
    /// Compiles baseline ⊕ arm on its own for every arm (design 11.5 check 1). It sees only the baseline and the arm's
    /// own additions, which enforces the independence rule (design 11.6).
    /// </summary>
    private void ValidateArmsIndependently()
    {
        var plain = WithoutExperiments(_request);
        foreach (var arm in _arms)
        {
            if (!arm.Valid)
            {
                continue;
            }

            var scratch = new DiagnosticBag();
            var merged = _view.Apply(arm.Patch, scratch, arm.Artifact, arm.PointerPrefix);
            if (merged is not null)
            {
                new UnifiedCompilation(plain, _environment, merged, scratch, buildPlan: false, _bindings).Run();
            }

            foreach (var d in scratch.Items)
            {
                Report(d, new[] { arm });
            }
        }
    }

    /// <summary>
    /// Compiles the bare baseline on its own when arms add schema entries or parameter leaves (design 11.6): the
    /// unified schema contains every addition, so without this an addition could make an invalid baseline resolve,
    /// and removing that experiment would break the baseline.
    /// </summary>
    private void ValidateBaselineAlone()
    {
        var needed = false;
        foreach (var arm in _arms)
        {
            needed |= arm.Valid && (arm.Additions.Count > 0 || arm.ExtraParamLeaves.Count > 0);
        }

        if (!needed)
        {
            return;
        }

        var scratch = new DiagnosticBag();
        new UnifiedCompilation(WithoutExperiments(_request), _environment, _view, scratch, buildPlan: false, _bindings).Run();
        foreach (var d in scratch.Items)
        {
            Report(d, Array.Empty<ArmPatch>());
        }
    }

    private int[] NewCombination()
    {
        var combination = new int[_layers.Count];
        Array.Fill(combination, -1);
        return combination;
    }

    private static long Product(SortedDictionary<int, List<int>> choices, List<int> layers)
    {
        long product = 1;
        foreach (var layer in layers)
        {
            product *= choices[layer].Count + 1;
            if (product > int.MaxValue)
            {
                return long.MaxValue;
            }
        }

        return product;
    }

    /// <summary>Odometer over every assignment of the given layers (each: not assigned, or one of its arms).</summary>
    private void Enumerate(SortedDictionary<int, List<int>> choices, List<int> layers, Action<int[]> visit)
    {
        var digits = new int[layers.Count];
        while (true)
        {
            var combination = NewCombination();
            for (var i = 0; i < layers.Count; i++)
            {
                combination[layers[i]] = digits[i] == 0 ? -1 : choices[layers[i]][digits[i] - 1];
            }

            visit(combination);
            var position = layers.Count - 1;
            while (position >= 0)
            {
                if (++digits[position] <= choices[layers[position]].Count)
                {
                    break;
                }

                digits[position] = 0;
                position--;
            }

            if (position < 0)
            {
                return;
            }
        }
    }

    private static void AddLayers(Dictionary<int, SortedSet<int>> map, int logical, int[] layers)
    {
        if (!map.TryGetValue(logical, out var set))
        {
            set = new SortedSet<int>();
            map[logical] = set;
        }

        foreach (var layer in layers)
        {
            set.Add(layer);
        }
    }
}
