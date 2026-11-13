using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Experiments;

public sealed class TrafficAssignerTests
{
    private static readonly LayerInfo[] Layers = { new(0, "ranking"), new(1, "rerank") };

    private static readonly ExperimentInfo[] Experiments =
    {
        new(0, "exp_rank", 0, new[] { 0, 1 }),
        new(1, "exp_mmr", 1, new[] { 2, 3 }),
    };

    private static readonly ArmInfo[] Arms =
    {
        new(0, "exp_rank/control", "control", 0, 0),
        new(1, "exp_rank/treatment", "treatment", 0, 0),
        new(2, "exp_mmr/control", "control", 1, 1),
        new(3, "exp_mmr/mmr", "mmr", 1, 1),
    };

    private static readonly Dictionary<string, ContextField> Inputs = new()
    {
        ["user_id"] = new ContextField { Id = 3, Name = "request.user_id", Type = ColumnType.Int64.AsNullable(), Kind = ContextFieldKind.RequestInput },
        ["session_id"] = new ContextField { Id = 4, Name = "request.session_id", Type = ColumnType.String.AsNullable(), Kind = ContextFieldKind.RequestInput },
    };

    private static AssignmentSpec Spec(bool withDomains = false) => new()
    {
        Buckets = 1000,
        DomainSplit = withDomains ? new DomainSplitSpec { Salt = "domains", Unit = new() { "ctx.request.user_id" } } : null,
        Domains = withDomains
            ? new() { ["main"] = new DomainSpec { Buckets = new[] { 0, 899 } }, ["holdout"] = new DomainSpec { Buckets = new[] { 900, 999 } } }
            : new(),
        Layers = new()
        {
            ["ranking"] = new LayerSpec { Domain = "main", Salt = "ranking-v1", Unit = new() { "ctx.request.user_id", "ctx.request.session_id" } },
            ["rerank"] = new LayerSpec { Domain = "main", Salt = "rerank-v1", Unit = new() { "ctx.request.user_id" } },
        },
    };

    private static readonly Dictionary<string, IReadOnlyDictionary<string, int[]>> Ranges = new()
    {
        ["exp_rank"] = new Dictionary<string, int[]> { ["control"] = new[] { 0, 99 }, ["treatment"] = new[] { 100, 199 } },
        ["exp_mmr"] = new Dictionary<string, int[]> { ["control"] = new[] { 0, 499 }, ["mmr"] = new[] { 500, 999 } },
    };

    private static TrafficAssigner Create(AssignmentSpec spec, DiagnosticBag diagnostics, Dictionary<string, IReadOnlyDictionary<string, int[]>>? ranges = null) =>
        TrafficAssigner.Create(spec, Layers, Experiments, Arms, Inputs, ranges ?? Ranges, diagnostics);

    [Fact]
    public void AssignmentIsDeterministicAndFollowsRanges()
    {
        var diagnostics = new DiagnosticBag();
        var assigner = Create(Spec(), diagnostics);
        Assert.Empty(diagnostics.Items);

        var counts = new int[Arms.Length];
        var unassignedRanking = 0;
        for (var user = 0; user < 20_000; user++)
        {
            var inputs = new RequestInputs().Set("user_id", user);
            var arms = assigner.Assign(inputs);
            var again = assigner.Assign(inputs);
            Assert.Equal(arms.ArmIn(0), again.ArmIn(0));
            Assert.Equal(arms.ArmIn(1), again.ArmIn(1));
            if (arms.ArmIn(0) >= 0)
            {
                counts[arms.ArmIn(0)]++;
            }
            else
            {
                unassignedRanking++;
            }

            counts[arms.ArmIn(1)]++; // rerank covers all buckets
        }

        // 10% / 10% / 80% unassigned in ranking; 50% / 50% in rerank. Tolerances are ~6 standard deviations.
        Assert.InRange(counts[0], 1_750, 2_250);
        Assert.InRange(counts[1], 1_750, 2_250);
        Assert.InRange(unassignedRanking, 15_500, 16_500);
        Assert.InRange(counts[2], 9_550, 10_450);
        Assert.InRange(counts[3], 9_550, 10_450);
    }

    [Fact]
    public void LayersAreOrthogonal()
    {
        var assigner = Create(Spec(), new DiagnosticBag());
        var both = 0;
        var treatment = 0;
        for (var user = 0; user < 40_000; user++)
        {
            var arms = assigner.Assign(new RequestInputs().Set("user_id", user));
            if (arms.ArmIn(0) == 1)
            {
                treatment++;
                both += arms.ArmIn(1) == 3 ? 1 : 0;
            }
        }

        // Independent salts: half of the ranking treatment lands in each rerank arm.
        Assert.InRange((double)both / treatment, 0.44, 0.56);
    }

    [Fact]
    public void FallsBackToTheNextUnitAndSkipsLayersWithoutOne()
    {
        var assigner = Create(Spec(), new DiagnosticBag());
        var anonymous = assigner.Assign(new RequestInputs().Set("session_id", "s-123"));
        var nothing = assigner.Assign(new RequestInputs());

        Assert.Equal(-1, anonymous.ArmIn(1)); // rerank is keyed by user only
        Assert.Equal(-1, nothing.ArmIn(0));
        Assert.Equal(-1, nothing.ArmIn(1));
    }

    [Fact]
    public void HoldoutDomainGetsNoArms()
    {
        var assigner = Create(Spec(withDomains: true), new DiagnosticBag());
        var unassigned = 0;
        for (var user = 0; user < 10_000; user++)
        {
            var arms = assigner.Assign(new RequestInputs().Set("user_id", user));
            unassigned += arms.ArmIn(1) < 0 ? 1 : 0;
        }

        Assert.InRange(unassigned, 850, 1_150); // ~10% holdout
    }

    [Fact]
    public void OverlappingArmRangesAreRejected()
    {
        var diagnostics = new DiagnosticBag();
        var ranges = new Dictionary<string, IReadOnlyDictionary<string, int[]>>(Ranges)
        {
            ["exp_rank"] = new Dictionary<string, int[]> { ["control"] = new[] { 0, 150 }, ["treatment"] = new[] { 100, 199 } },
        };
        Create(Spec(), diagnostics, ranges);

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal("ASSIGN_OVERLAP", error.Code);
    }

    [Fact]
    public void OverlappingDomainsAreRejected()
    {
        var diagnostics = new DiagnosticBag();
        var spec = Spec(withDomains: true);
        spec.Domains["holdout"] = new DomainSpec { Buckets = new[] { 0, 999 } };
        Create(spec, diagnostics);

        Assert.Equal("ASSIGN_DOMAIN_OVERLAP", Assert.Single(diagnostics.Items).Code);
    }

    [Fact]
    public void MisspelledAllocatedArmIsRejected()
    {
        var diagnostics = new DiagnosticBag();
        var ranges = new Dictionary<string, IReadOnlyDictionary<string, int[]>>(Ranges)
        {
            ["exp_rank"] = new Dictionary<string, int[]> { ["control"] = new[] { 0, 99 }, ["treatement"] = new[] { 100, 199 } },
            ["exp_gone"] = new Dictionary<string, int[]> { ["control"] = new[] { 0, 9 } },
        };
        Create(Spec(), diagnostics, ranges);

        var arm = Assert.Single(diagnostics.Items, d => d.Code == "ASSIGN_UNKNOWN_ARM");
        Assert.Equal("/experiments/exp_rank/arms/treatement", arm.Pointer);
        Assert.Contains("treatment", arm.Hint);
        Assert.Single(diagnostics.Items, d => d.Code == "ASSIGN_UNKNOWN_EXPERIMENT");
    }

    [Fact]
    public void ExternallyAssignedLayersNeedNoSaltOrUnit()
    {
        var diagnostics = new DiagnosticBag();
        var spec = Spec();
        spec.Layers.Remove("rerank");
        var ranges = new Dictionary<string, IReadOnlyDictionary<string, int[]>> { ["exp_rank"] = Ranges["exp_rank"] };
        Create(spec, diagnostics, ranges);

        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void UnitsMustBeDeclaredRequestInputs()
    {
        var diagnostics = new DiagnosticBag();
        var spec = Spec();
        spec.Layers["rerank"] = new LayerSpec { Domain = "main", Salt = "x", Unit = new() { "ctx.request.device_id" } };
        Create(spec, diagnostics);

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal("ASSIGN_BAD_UNIT", error.Code);
        Assert.Equal("/layers/rerank/unit/0", error.Pointer);
    }
}
