using StrataRec.Artifacts.Model;
using StrataRec.Core.Hashing;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Assignment;

/// <summary>Supplies arm assignments from an external experimentation platform (design 11.1).</summary>
public interface IAssignmentProvider
{
    /// <summary>Returns the assigned arms as <c>experiment/arm</c> ids. Unknown arms are ignored and counted.</summary>
    ValueTask<IReadOnlyList<string>> AssignAsync(string pipelineId, RequestInputs inputs, CancellationToken cancellationToken);
}

/// <summary>
/// Built-in overlapping-experiment assignment (design 10.2, 11.1; Tang et al., KDD 2010).
/// </summary>
/// <remarks>
/// <para>The unit is the first present request input listed in <c>unit</c> (e.g. user id, then session id). The domain is
/// <c>bucket(domain_split.salt, unit) ∈ domain range</c>; within the domain each layer computes
/// <c>bucket(layer.salt, unit)</c> and the arm whose inclusive range contains it is assigned.</para>
/// <para><c>bucket(salt, unit) = XXH3-64(enc(salt) ‖ enc(index) ‖ enc(unit)) mod buckets</c> using the canonical encoding
/// of <see cref="StableHash"/>, where <c>index</c> is the position of the matched unit input. Offline jobs can recompute it.</para>
/// </remarks>
public sealed class TrafficAssigner
{
    private readonly int _buckets;
    private readonly UnitSpec? _domainUnit;
    private readonly (string Name, int First, int Last)[] _domains;
    private readonly LayerRule[] _layers;
    private readonly int _layerCount;

    private TrafficAssigner(int buckets, UnitSpec? domainUnit, (string, int, int)[] domains, LayerRule[] layers, int layerCount)
    {
        _buckets = buckets;
        _domainUnit = domainUnit;
        _domains = domains;
        _layers = layers;
        _layerCount = layerCount;
    }

    /// <summary>
    /// Builds an assigner from the manifest's assignment section and each experiment's arm ranges.
    /// Problems (unknown layers, overlapping ranges, bad units) go to <paramref name="diagnostics"/>.
    /// </summary>
    public static TrafficAssigner Create(
        AssignmentSpec spec,
        ExecutionPlan plan,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>> armRangesByExperiment,
        DiagnosticBag diagnostics,
        string artifact = "assignment") =>
        Create(spec, plan.Layers, plan.Experiments, plan.Arms, plan.RequestInputs, armRangesByExperiment, diagnostics, artifact);

    /// <summary>Builds an assigner from the plan's experiment metadata (see the <see cref="ExecutionPlan"/> overload).</summary>
    public static TrafficAssigner Create(
        AssignmentSpec spec,
        LayerInfo[] planLayers,
        ExperimentInfo[] planExperiments,
        ArmInfo[] planArms,
        IReadOnlyDictionary<string, ContextField> requestInputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>> armRangesByExperiment,
        DiagnosticBag diagnostics,
        string artifact = "assignment")
    {
        var buckets = spec.Buckets;
        if (buckets < 1)
        {
            diagnostics.Error("ASSIGN_BAD_BUCKETS", artifact, "/buckets", "buckets must be positive.");
            buckets = 1;
        }

        UnitSpec? domainUnit = null;
        if (spec.Domains.Count > 0 && spec.DomainSplit is null)
        {
            diagnostics.Error("ASSIGN_NO_DOMAIN_SPLIT", artifact, "/domain_split", "Domains are declared but there is no domain_split to choose between them.",
                "add \"domain_split\": { \"salt\": \"...\", \"unit\": [\"ctx.request.user_id\"] }");
        }

        if (spec.DomainSplit is { } split)
        {
            domainUnit = UnitSpec.Create(split.Salt, split.Unit, requestInputs, diagnostics, artifact, "/domain_split/unit");
        }

        var domains = new List<(string, int, int)>();
        foreach (var name in Sorted(spec.Domains.Keys))
        {
            var range = spec.Domains[name].Buckets;
            if (!ValidRange(range, buckets))
            {
                diagnostics.Error("ASSIGN_BAD_RANGE", artifact, "/domains/" + name + "/buckets", $"Domain range must be [first, last] within [0, {buckets - 1}].");
                continue;
            }

            foreach (var (other, first, last) in domains)
            {
                if (range[0] <= last && first <= range[1])
                {
                    diagnostics.Error("ASSIGN_DOMAIN_OVERLAP", artifact, "/domains/" + name + "/buckets",
                        $"Domain '{name}' overlaps domain '{other}'; domains split traffic exclusively (design 11.1).");
                }
            }

            domains.Add((name, range[0], range[1]));
        }

        // Every allocated experiment and arm must exist: a misspelled arm silently receives no traffic otherwise.
        foreach (var (experimentId, armRanges) in armRangesByExperiment)
        {
            ExperimentInfo? experiment = null;
            foreach (var e in planExperiments)
            {
                if (e.Id == experimentId)
                {
                    experiment = e;
                }
            }

            if (experiment is null)
            {
                var ids = new List<string>();
                foreach (var e in planExperiments)
                {
                    ids.Add(e.Id);
                }

                diagnostics.Error("ASSIGN_UNKNOWN_EXPERIMENT", artifact, "/experiments/" + experimentId, $"Experiment '{experimentId}' is not in the plan.",
                    NameSuggestions.DidYouMean(experimentId, ids));
                continue;
            }

            var names = new List<string>();
            foreach (var armIndex in experiment.ArmIndices)
            {
                names.Add(planArms[armIndex].ArmName);
            }

            foreach (var (armName, _) in armRanges)
            {
                if (!names.Contains(armName))
                {
                    diagnostics.Error("ASSIGN_UNKNOWN_ARM", artifact, "/experiments/" + experimentId + "/arms/" + armName,
                        $"Experiment '{experimentId}' has no arm '{armName}'.", NameSuggestions.DidYouMean(armName, names) ?? $"arms: {string.Join(", ", names)}");
                }
            }
        }

        var layers = new List<LayerRule>();
        foreach (var layer in planLayers)
        {
            if (!spec.Layers.TryGetValue(layer.Name, out var layerSpec))
            {
                // Without bucket ranges the layer is assigned externally (IAssignmentProvider) and needs no salt or unit.
                var allocated = false;
                foreach (var experiment in planExperiments)
                {
                    allocated |= experiment.LayerIndex == layer.Index && armRangesByExperiment.ContainsKey(experiment.Id);
                }

                if (allocated)
                {
                    diagnostics.Error("ASSIGN_UNKNOWN_LAYER", artifact, "/layers", $"Layer '{layer.Name}' has allocated experiments but no assignment definition.",
                        $"add \"{layer.Name}\": {{ \"domain\": \"main\", \"salt\": \"...\", \"unit\": [\"ctx.request.user_id\"] }} under /assignment/layers");
                }

                continue;
            }

            if (spec.Domains.Count > 0 && !spec.Domains.ContainsKey(layerSpec.Domain))
            {
                diagnostics.Error("ASSIGN_UNKNOWN_DOMAIN", artifact, "/layers/" + layer.Name + "/domain", $"Domain '{layerSpec.Domain}' is not defined.");
            }

            var unit = UnitSpec.Create(layerSpec.Salt, layerSpec.Unit, requestInputs, diagnostics, artifact, "/layers/" + layer.Name + "/unit");
            var ranges = new List<(int First, int Last, int Arm)>();
            foreach (var experiment in planExperiments)
            {
                if (experiment.LayerIndex != layer.Index || !armRangesByExperiment.TryGetValue(experiment.Id, out var armRanges))
                {
                    continue;
                }

                foreach (var armIndex in experiment.ArmIndices)
                {
                    var arm = planArms[armIndex];
                    if (!armRanges.TryGetValue(arm.ArmName, out var range))
                    {
                        continue; // arm not allocated traffic
                    }

                    var pointer = "/experiments/" + experiment.Id + "/arms/" + arm.ArmName;
                    if (!ValidRange(range, buckets))
                    {
                        diagnostics.Error("ASSIGN_BAD_RANGE", artifact, pointer, $"Arm range must be [first, last] within [0, {buckets - 1}].");
                        continue;
                    }

                    foreach (var other in ranges)
                    {
                        if (range[0] <= other.Last && other.First <= range[1])
                        {
                            diagnostics.Error("ASSIGN_OVERLAP", artifact, pointer,
                                $"Arm '{arm.Id}' overlaps arm '{planArms[other.Arm].Id}' in layer '{layer.Name}'; arms in one layer must use disjoint buckets.");
                        }
                    }

                    ranges.Add((range[0], range[1], arm.Index));
                }
            }

            ranges.Sort(static (a, b) => a.First.CompareTo(b.First));
            layers.Add(new LayerRule(layer.Index, layerSpec.Domain, unit, ranges.ToArray()));
        }

        return new TrafficAssigner(buckets, domainUnit, domains.ToArray(), layers.ToArray(), planLayers.Length);
    }

    /// <summary>Assigns arms for a request. Requests without any unit value get no arms.</summary>
    public RequestArms Assign(RequestInputs inputs)
    {
        var arms = new int[_layerCount];
        Array.Fill(arms, -1);
        string? domain = null;
        if (_domainUnit is not null && _domains.Length > 0)
        {
            var bucket = _domainUnit.Bucket(inputs, _buckets);
            if (bucket < 0)
            {
                return new RequestArms(arms);
            }

            foreach (var (name, first, last) in _domains)
            {
                if (bucket >= first && bucket <= last)
                {
                    domain = name;
                    break;
                }
            }

            if (domain is null)
            {
                return new RequestArms(arms);
            }
        }

        foreach (var layer in _layers)
        {
            if (domain is not null && !string.Equals(layer.Domain, domain, StringComparison.Ordinal))
            {
                continue;
            }

            var bucket = layer.Unit?.Bucket(inputs, _buckets) ?? -1;
            if (bucket < 0)
            {
                continue;
            }

            foreach (var (first, last, arm) in layer.Ranges)
            {
                if (bucket >= first && bucket <= last)
                {
                    arms[layer.LayerIndex] = arm;
                    break;
                }
            }
        }

        return new RequestArms(arms);
    }

    private static bool ValidRange(int[]? range, int buckets) =>
        range is { Length: 2 } && range[0] >= 0 && range[0] <= range[1] && range[1] < buckets;

    private static List<string> Sorted(IEnumerable<string> keys)
    {
        var list = new List<string>(keys);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private sealed record LayerRule(int LayerIndex, string Domain, UnitSpec? Unit, (int First, int Last, int Arm)[] Ranges);

    /// <summary>A salted unit: the first present request input from a list.</summary>
    private sealed class UnitSpec
    {
        private readonly string _salt;
        private readonly string[] _inputs;

        private UnitSpec(string salt, string[] inputs)
        {
            _salt = salt;
            _inputs = inputs;
        }

        public static UnitSpec? Create(string salt, List<string> unit, IReadOnlyDictionary<string, ContextField> requestInputs, DiagnosticBag diagnostics, string artifact, string pointer)
        {
            var inputs = new string[unit.Count];
            for (var i = 0; i < unit.Count; i++)
            {
                const string Prefix = "ctx.request.";
                var name = unit[i].StartsWith(Prefix, StringComparison.Ordinal) ? unit[i][Prefix.Length..] : null;
                if (name is null || !requestInputs.TryGetValue(name, out var field))
                {
                    diagnostics.Error("ASSIGN_BAD_UNIT", artifact, pointer + "/" + i,
                        $"Unit '{unit[i]}' must reference a declared request input as ctx.request.<name>.");
                    return null;
                }

                if (field.Type.Kind is not (StorageKind.Int64 or StorageKind.String))
                {
                    diagnostics.Error("ASSIGN_BAD_UNIT", artifact, pointer + "/" + i, $"Unit input '{name}' must be int64 or string.");
                    return null;
                }

                inputs[i] = name;
            }

            return new UnitSpec(salt, inputs);
        }

        /// <summary>Bucket in [0, buckets), or -1 when no unit input is present.</summary>
        public int Bucket(RequestInputs inputs, int buckets)
        {
            for (var i = 0; i < _inputs.Length; i++)
            {
                var value = inputs[_inputs[i]];
                if (value.IsMissing)
                {
                    continue;
                }

                using var hash = new StableHash.Builder();
                hash.Add(_salt);
                hash.AddIndex(i);
                hash.Add(value);
                return (int)(hash.Finish() % (ulong)buckets);
            }

            return -1;
        }
    }
}
