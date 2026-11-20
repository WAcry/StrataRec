using System.Collections.Immutable;
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
    /// <see cref="PlanCompileRequest.MaxCombinationsChecked"/>. Otherwise every single arm and every pair, and for each
    /// rule of the check every combination of the layers that decide it (<see cref="PlanBuilder.DataFlowScopes"/>);
    /// a rule whose layers have more combinations than the limit is an error, never left unchecked.
    /// </summary>
    private void ValidateCombinations(ImmutableArray<TimelineSlot> timeline, Dictionary<string, TimelineSlot> fallbacks, OutputPlan output, CombinationReport report)
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

        // Per rule: every combination of the layers that decide it (design 11.5 check 3). A rule depends only on the
        // variants of its scope, so this checks it as completely as the whole space would; scopes within two layers are
        // covered by the singles and pairs above.
        var nodes = new Dictionary<string, List<CompiledNode>>(StringComparer.Ordinal);
        foreach (var (id, v) in _variants)
        {
            nodes[id] = Compiled(v.Variants);
        }

        var taps = new Dictionary<string, List<CompiledTap>>(StringComparer.Ordinal);
        foreach (var (id, t) in _taps)
        {
            taps[id] = Compiled(t.Variants);
        }

        var enumerated = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in _builder.DataFlowScopes(nodes, taps, output, Conditional))
        {
            var layers = new SortedSet<int>();
            foreach (var id in scope.Nodes)
            {
                if (_variants.TryGetValue(id, out var v))
                {
                    layers.UnionWith(v.Layers);
                }
            }

            foreach (var id in scope.Taps)
            {
                layers.UnionWith(_taps[id].Layers);
            }

            if (layers.Count < 3)
            {
                continue;
            }

            var list = new List<int>(layers);
            var product = Product(choices, list);
            if (product > _request.MaxCombinationsChecked)
            {
                // Without a complete check some assignment could run a plan this rule rejects, so the plan is rejected
                // rather than published on a warning.
                if (reported.Add(scope.Pointer))
                {
                    // Each layer with what it changes in the scope: which experiments to move into one layer is what the
                    // author has to decide, and the subject alone does not say which node a layer reaches it through.
                    var names = new List<string>(list.Count);
                    foreach (var layer in list)
                    {
                        names.Add($"{_layers[layer]} (changes {ChangedBy(layer, scope)})");
                    }

                    _diagnostics.Error("COMBINATION_CHECK_LIMIT", _pipelineLabel, scope.Pointer,
                        $"The data flow of {scope.Subject} depends on arms in {list.Count} layers ({string.Join("; ", names)}): " +
                        $"{(product == long.MaxValue ? "too many" : product.ToString(System.Globalization.CultureInfo.InvariantCulture))} combinations, " +
                        $"more than the {_request.MaxCombinationsChecked} that can be checked.",
                        "put experiments that change these nodes into fewer layers (arms of one layer never run together), or graduate some of them");
                }

                continue;
            }

            if (enumerated.Add(string.Join(",", list)))
            {
                Enumerate(choices, list, combination => Check(combination, timeline, fallbacks, output, seen, report));
            }
        }
    }

    /// <summary>The nodes and taps of <paramref name="scope"/> whose variants <paramref name="layer"/> decides, by id.</summary>
    private string ChangedBy(int layer, DataFlowScope scope)
    {
        var changed = new List<string>();
        foreach (var id in scope.Nodes)
        {
            if (_variants.TryGetValue(id, out var v) && v.Layers.IndexOf(layer) >= 0)
            {
                changed.Add($"node '{id}'");
            }
        }

        foreach (var id in scope.Taps)
        {
            if (_taps[id].Layers.IndexOf(layer) >= 0)
            {
                changed.Add($"tap '{id}'");
            }
        }

        return string.Join(", ", changed);
    }

    /// <summary>The distinct variants of a space (combinations share deduplicated variants), without "absent".</summary>
    private static List<T> Compiled<T>(T?[] variants)
        where T : class
    {
        var compiled = new List<T>();
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var variant in variants)
        {
            if (variant is not null && seen.Add(variant))
            {
                compiled.Add(variant);
            }
        }

        return compiled;
    }

    private void Check(int[] combination, ImmutableArray<TimelineSlot> timeline, Dictionary<string, TimelineSlot> fallbacks, OutputPlan output, HashSet<string> seen, CombinationReport report)
    {
        combination = Running(combination); // one with an arm that yields runs without it: that is what is checked
        if (!seen.Add(string.Join(",", combination)))
        {
            return;
        }

        // A combination with a variant that failed to compile was reported there; only how its columns are declared is
        // still checked, since a compile error at a reader may come from a column this combination declares otherwise.
        var arms = new RequestArms(combination);
        var failed = false;
        foreach (var (_, v) in _variants)
        {
            failed |= v.Failed[v.IndexOf(arms)];
        }

        var taps = new List<CompiledTap>();
        foreach (var (_, t) in _taps)
        {
            var index = t.IndexOf(arms);
            failed |= t.Failed[index];
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
        ValidateImplicitDeclarations(nodes, selectedFallbacks, assigned);
        if (failed)
        {
            Settle(start, assigned);
            return;
        }

        _builder.ValidateDataFlow(nodes, selectedFallbacks, output, _pipelineLabel, taps);
        if (assigned.Count > 0)
        {
            ValidateNames(nodes, selectedFallbacks, taps, output, assigned, start); // without an arm: ValidateBaselineAlone
        }

        Settle(start, assigned);
        report.Checked++;
    }

    /// <summary>
    /// Checks every pair of arms that can run together (any two of different layers, unless one's experiment yields to the
    /// other's) for path conflicts (errors) and interactions (warnings for review), design 11.5. Two arms placing nodes at
    /// the same order are not a conflict: the per-combination data-flow check refuses same-order nodes that depend on each
    /// other (ORDER_DEPENDENCY), and independent ones run in either order with the same result. A pair that never runs
    /// together because of a yield is not reported: what it would be is what the yield resolves (<see cref="ValidateYields"/>).
    /// </summary>
    private void ValidatePairs()
    {
        var writes = new Dictionary<int, Dictionary<int, string>>();
        var addedReads = new Dictionary<int, Dictionary<int, ArmRead>>();
        foreach (var arm in _arms)
        {
            if (arm.Valid)
            {
                DescribeArm(arm, out var w, out var r);
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

                var resolves = _yields.Yields(a.ExperimentIndex, b.ExperimentIndex) || _yields.Yields(b.ExperimentIndex, a.ExperimentIndex)
                    ? YieldResolves(a.ExperimentIndex, b.ExperimentIndex)
                    : null;
                foreach (var conflict in MergePatchAnalyzer.FindConflicts(a.Footprint, b.Footprint))
                {
                    if (IsAddition(a, conflict.First) && IsAddition(b, conflict.Second))
                    {
                        continue; // identical re-adds are fine; different ones are ADD_CONFLICT
                    }

                    if (resolves is not null)
                    {
                        resolves.Add("PATCH_CONFLICT " + conflict.First.Path + (conflict.First.Path.Equals(conflict.Second.Path) ? string.Empty : " / " + conflict.Second.Path));
                        continue;
                    }

                    _diagnostics.Error("PATCH_CONFLICT", a.Artifact, a.PointerPrefix + conflict.First.Path,
                        $"Arms '{a.Id}' and '{b.Id}' (layers '{_layers[a.LayerIndex]}' and '{_layers[b.LayerIndex]}') can be assigned to the same request and both change '{conflict.First.Path}'"
                        + (conflict.First.Path.Equals(conflict.Second.Path) ? "." : $" / '{conflict.Second.Path}'."),
                        "put the experiments in the same layer so their arms exclude each other, combine them into one experiment, "
                        + $"or let one take the other's traffic where both are assigned: {YieldHint(a, b)} (design 11.1, 11.5)");
                }

                Interactions(a, b, writes, addedReads, resolves);
                Interactions(b, a, writes, addedReads, resolves);
            }
        }
    }

    /// <summary>What a yield between two experiments resolves (<see cref="_yieldResolves"/>), by the pair, lower index first.</summary>
    private SortedSet<string> YieldResolves(int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        if (!_yieldResolves.TryGetValue(key, out var set))
        {
            set = new SortedSet<string>(StringComparer.Ordinal);
            _yieldResolves[key] = set;
        }

        return set;
    }

    /// <summary>How to declare that <paramref name="a"/>'s experiment yields to <paramref name="b"/>'s, for a hint.</summary>
    private string YieldHint(ArmPatch a, ArmPatch b) =>
        $"in the release, \"{a.ExperimentId}\": {{ \"layer\": \"{_layers[a.LayerIndex]}\", \"yields_to\": [\"{b.ExperimentId}\"] }}";

    /// <param name="resolves">Where the interactions go when a yield keeps the two arms from running together: no
    /// warning, and no reviewed interaction is used up by them.</param>
    private void Interactions(
        ArmPatch a, ArmPatch b, Dictionary<int, Dictionary<int, string>> writes, Dictionary<int, Dictionary<int, ArmRead>> addedReads, SortedSet<string>? resolves)
    {
        foreach (var (logical, node) in writes[a.Index])
        {
            var column = _builder.Schema.Columns[logical];
            if (a.Index < b.Index && column.Flow && writes[b.Index].TryGetValue(logical, out var otherNode) && resolves is not null)
            {
                resolves.Add("INTERACTION_FLOW_COLUMN " + column.Name);
            }
            else if (a.Index < b.Index && column.Flow && writes[b.Index].TryGetValue(logical, out otherNode) && !Reviewed(a, b, column.Name))
            {
                _diagnostics.Warning("INTERACTION_FLOW_COLUMN", a.Artifact, a.PointerPrefix + "/nodes/" + JsonPointer.Escape(node),
                    $"Arms '{a.Id}' ('{node}') and '{b.Id}' ('{otherNode}') can be assigned together and both add a node writing flow column '{column.Name}'.",
                    ReviewHint(a, b, column.Name));
            }

            if (addedReads[b.Index].TryGetValue(logical, out var read) && resolves is not null)
            {
                resolves.Add("INTERACTION_READ " + column.Name);
            }
            else if (addedReads[b.Index].TryGetValue(logical, out read) && !Reviewed(a, b, column.Name))
            {
                var reader = read.Node;
                var how = read.Fallback is { } fallback
                    ? $"As '{b.Id}' changes node '{reader}', it fails over to '{fallback}', reading '{column.Name}'"
                    : $"Node '{reader}' added by '{b.Id}' reads '{column.Name}'";
                _diagnostics.Warning("INTERACTION_READ", b.Artifact, b.PointerPrefix + "/nodes/" + JsonPointer.Escape(reader),
                    $"{how}, which '{a.Id}' writes in the node it adds, '{node}'; the two arms can be assigned together.",
                    ReviewHint(b, a, column.Name));
            }
        }
    }

    private string ReviewHint(ArmPatch a, ArmPatch b, string column) =>
        $"if the two may compose, record the review in '{a.ExperimentId}': \"reviewed_interactions\": {{ \"{b.ExperimentId}\": [\"{column}\"] }}; " +
        $"otherwise put the experiments in the same layer, or let one yield to the other where both are assigned: {YieldHint(a, b)} (design 11.1, 11.5)";

    /// <summary>
    /// A yield that resolves nothing is a warning: the arms of the two neither conflict nor interact, so it only costs the
    /// experiment that yields the traffic of the other, and a yield that outlived its conflict would hide the next one.
    /// </summary>
    private void ValidateYields()
    {
        // With an error anywhere, conflicts and interactions are not all known (as for reviewed interactions, review 69).
        if (_diagnostics.HasErrors)
        {
            return;
        }

        foreach (var (from, to, release, pointer) in _yieldDeclarations)
        {
            if (!_yieldResolves.TryGetValue(from < to ? (from, to) : (to, from), out var resolves) || resolves.Count == 0)
            {
                _diagnostics.Warning("YIELD_UNUSED", release, pointer,
                    $"'{_experiments[from].Id}' yields to '{_experiments[to].Id}', but their arms neither conflict nor interact: it only costs '{_experiments[from].Id}' the traffic '{_experiments[to].Id}' runs on.",
                    "remove the entry (design 11.1)");
            }
        }
    }

    private List<YieldReport> YieldReports()
    {
        var reports = new List<YieldReport>(_yieldDeclarations.Count);
        foreach (var (from, to, _, _) in _yieldDeclarations)
        {
            var resolves = _yieldResolves.TryGetValue(from < to ? (from, to) : (to, from), out var set) ? new List<string>(set) : new List<string>();
            reports.Add(new YieldReport(_experiments[from].Id, _experiments[to].Id, resolves));
        }

        reports.Sort(static (x, y) => string.CompareOrdinal(x.Experiment, y.Experiment) is var c && c != 0 ? c : string.CompareOrdinal(x.To, y.To));
        return reports;
    }

    /// <summary>Whether either experiment recorded the interaction on <paramref name="column"/> as reviewed; marks what matched.</summary>
    private bool Reviewed(ArmPatch a, ArmPatch b, string column) => Reviewed(a.ExperimentId, b.ExperimentId, column) | Reviewed(b.ExperimentId, a.ExperimentId, column);

    private bool Reviewed(string experiment, string other, string column)
    {
        if (_reviewed.TryGetValue(experiment, out var reviewed) && reviewed.Columns.TryGetValue(other, out var columns) && columns.Contains(column))
        {
            _reviewUsed.Add((experiment, other, column));
            return true;
        }

        return false;
    }

    /// <summary>
    /// A reviewed interaction with an experiment of this release that no interaction needed is a warning: the experiments
    /// share a layer now, or neither adds a node interacting on the column, and a review that outlived its interaction would
    /// silence the next one. One with an experiment this release does not have says nothing here (another environment's).
    /// </summary>
    private void ValidateReviewedInteractions()
    {
        // With an error anywhere, interactions are not known: an arm whose patch or node did not compile has none to find
        // (review 69: a node's config error made a valid review "unused", and the hint said to remove it).
        if (_diagnostics.HasErrors)
        {
            return;
        }

        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, _, _) in _experiments)
        {
            placed.Add(id);
        }

        foreach (var (experiment, (artifact, reviewed)) in _reviewed)
        {
            foreach (var (other, columns) in reviewed)
            {
                if (!placed.Contains(other))
                {
                    continue;
                }

                for (var i = 0; i < columns.Count; i++)
                {
                    if (!_reviewUsed.Contains((experiment, other, columns[i])))
                    {
                        _diagnostics.Warning("INTERACTION_REVIEW_UNUSED", artifact, $"/reviewed_interactions/{JsonPointer.Escape(other)}/{i}",
                            $"No interaction between '{experiment}' and '{other}' on '{columns[i]}' needs a review in this release.",
                            "remove the entry: the experiments share a layer, or neither adds a node that interacts on the column");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The column footprint of an arm (design 11.5 item 2) from its single-arm variants: columns its touched nodes write,
    /// and columns its added nodes read.
    /// </summary>
    private void DescribeArm(ArmPatch arm, out Dictionary<int, string> writes, out Dictionary<int, ArmRead> addedReads)
    {
        writes = new Dictionary<int, string>();
        addedReads = new Dictionary<int, ArmRead>();
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

            // What the arm adds to the pipeline's structure: every column of a node it adds, and of a node it changes only the
            // columns the baseline's node did not write or read. Changing an existing node's settings, inline or through a
            // parameter, changes its values as any parameter does and composes by the structure (design 11.5): counting
            // every column of a touched node warned for every pair of experiments tuning different multipliers of one flow
            // column. A node whose operator or config now writes or reads another column is a change of structure (review
            // 69: a filter replaced by a writer of score.final, read by another experiment's added node, did not warn). Only
            // a read of the current version counts: a pinned one (col.x@node) does not see a writer another arm adds, and a
            // read changed from pinned to current is a new dependency (review 70).
            var baseline = arm.AddedNodes.Contains(nodeId) ? null : v.Variants[v.IndexOf(RequestArms.None)];
            foreach (var output in node.Outputs)
            {
                if (baseline is null || !Writes(baseline, output.LogicalId))
                {
                    writes.TryAdd(output.LogicalId, nodeId);
                }
            }

            foreach (var read in node.CurrentReads)
            {
                if (baseline is null || baseline.CurrentReads.IndexOf(read) < 0)
                {
                    addedReads.TryAdd(read, new ArmRead(nodeId, null));
                }
            }

            // A node's fallback reads when the node fails: an arm that turns the fallback on, or points the node at another
            // one, adds what that fallback reads (review 71). Compared with what the baseline's node could reach (itself and
            // its own fallback), so a fallback the baseline already ran adds nothing, and one no node reaches counts nowhere.
            if (FallbackOf(node, only) is { } fallback)
            {
                var baselineFallback = baseline is null ? null : FallbackOf(baseline, RequestArms.None);
                foreach (var read in fallback.CurrentReads)
                {
                    if (baseline is null || (baseline.CurrentReads.IndexOf(read) < 0 && (baselineFallback is null || baselineFallback.CurrentReads.IndexOf(read) < 0)))
                    {
                        addedReads.TryAdd(read, new ArmRead(nodeId, fallback.NodeId));
                    }
                }
            }
        }

        CompiledNode? FallbackOf(CompiledNode primary, RequestArms arms) =>
            primary.OnFailure == FailurePolicy.Fallback && primary.FallbackNodeId is { } id && _variants.TryGetValue(id, out var fv)
                ? fv.Variants[fv.IndexOf(arms)]
                : null;

        static bool Writes(CompiledNode node, int logical)
        {
            foreach (var output in node.Outputs)
            {
                if (output.LogicalId == logical)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A current read an arm adds: the node the arm adds or changes, and the fallback that reads it, if one does.</summary>
    private readonly record struct ArmRead(string Node, string? Fallback);

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
    /// Compiles the bare baseline on its own whenever there is an arm (design 11.6): the unified schema has whatever any
    /// arm declares, so an invalid baseline could resolve there, and removing that experiment (a revocation, which does
    /// not compile again) would publish a release no node can load. Not only when arms add schema entries or parameter
    /// leaves: a node an arm adds declares its outputs implicitly, and listing the ways an arm can declare a name missed
    /// that one (external review, P1). One more compile of the baseline, without building a plan, per plan compile.
    /// </summary>
    private void ValidateBaselineAlone()
    {
        var needed = false;
        foreach (var arm in _arms)
        {
            needed |= arm.Valid;
        }

        if (!needed)
        {
            return;
        }

        // What a check of the unified plan has already found here (a tap after a node only an arm adds, say) it said
        // more precisely, for the arm combinations: the same code at the same place is not said again.
        var found = new HashSet<(string, string, string)>();
        foreach (var d in _diagnostics.Items)
        {
            found.Add((d.Code, d.Artifact, d.Pointer));
        }

        var scratch = new DiagnosticBag();
        new UnifiedCompilation(WithoutExperiments(_request), _environment, _view, scratch, buildPlan: false, _bindings).Run();
        foreach (var d in scratch.Items)
        {
            if (!found.Contains((d.Code, d.Artifact, d.Pointer)))
            {
                Report(d, Array.Empty<ArmPatch>());
            }
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
}
