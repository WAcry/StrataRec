using StrataRec.Experiments.Assignment;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Experiments;

/// <summary>
/// The rules for the arms an experimentation platform sends (design 11.1): unknown ids and same-layer conflicts are
/// ignored and reported, repeats count once, and nothing depends on the order of the platform's list.
/// </summary>
public sealed class ExternalArmsTests
{
    // Layer "rerank" holds two experiments the platform keeps exclusive (exp_mmr, exp_rerank); layer "params" one.
    private static readonly LayerInfo[] Layers = { new(0, "rerank"), new(1, "params") };

    private static readonly ArmInfo[] Arms =
    {
        new(0, "exp_mmr/control", "control", 0, 0),
        new(1, "exp_mmr/mmr", "mmr", 0, 0),
        new(2, "exp_quality/control", "control", 1, 1),
        new(3, "exp_quality/strong", "strong", 1, 1),
        new(4, "exp_rerank/new", "new", 2, 0),
    };

    [Fact]
    public void ArmsOfDifferentLayers_AllApply_AndNothingIsIgnored()
    {
        var arms = Resolve(new[] { "exp_quality/strong", "exp_mmr/mmr" }, out var ignored);

        Assert.Equal(new[] { 1, 3 }, Assigned(arms));
        Assert.Same(Array.Empty<IgnoredArm>(), ignored); // no allocation when every id applies
    }

    [Fact]
    public void AnIdThePlanDoesNotHave_IsIgnoredAsUnknown_AndTheRestApplies()
    {
        // Not in this release, a misspelled arm, an experiment the platform ramped before the release arrived.
        var arms = Resolve(new[] { "exp_new/treatment", "exp_mmr/mmr", "exp_mmr/MMR", "exp_quality" }, out var ignored);

        Assert.Equal(new[] { 1, -1 }, Assigned(arms));
        Assert.Equal(
            new[] { new IgnoredArm("exp_mmr/MMR", IgnoredArmReason.Unknown), new IgnoredArm("exp_new/treatment", IgnoredArmReason.Unknown), new IgnoredArm("exp_quality", IgnoredArmReason.Unknown) },
            ignored);
    }

    [Fact]
    public void TheSameIdTwice_CountsOnce_AndIsNoConflict()
    {
        var arms = Resolve(new[] { "exp_mmr/mmr", "exp_quality/control", "exp_mmr/mmr", "exp_new/x", "exp_new/x" }, out var ignored);

        Assert.Equal(new[] { 1, 2 }, Assigned(arms));
        Assert.Equal(new[] { new IgnoredArm("exp_new/x", IgnoredArmReason.Unknown) }, ignored); // an unknown repeat is one id too
    }

    [Theory]
    [InlineData("exp_mmr/control", "exp_mmr/mmr")] // two arms of one experiment
    [InlineData("exp_mmr/mmr", "exp_rerank/new")] // arms of two experiments sharing a layer
    public void TwoArmsOfOneLayer_LeaveThatLayerOnItsBaseline_AndAreBothIgnored(string first, string second)
    {
        var arms = Resolve(new[] { first, "exp_quality/strong", second }, out var ignored);

        Assert.Equal(new[] { -1, 3 }, Assigned(arms)); // the other layer is unaffected
        var expected = new[] { new IgnoredArm(first, IgnoredArmReason.LayerConflict), new IgnoredArm(second, IgnoredArmReason.LayerConflict) };
        Assert.Equal(expected.OrderBy(i => i.Id, StringComparer.Ordinal), ignored);
    }

    [Fact]
    public void EveryArmOfAConflictedLayer_IsIgnoredOnce_HoweverOftenItCame()
    {
        var arms = Resolve(new[] { "exp_mmr/mmr", "exp_rerank/new", "exp_mmr/mmr", "exp_mmr/control", "exp_rerank/new" }, out var ignored);

        Assert.Equal(new[] { -1, -1 }, Assigned(arms));
        Assert.Equal(
            new[] { new IgnoredArm("exp_mmr/control", IgnoredArmReason.LayerConflict), new IgnoredArm("exp_mmr/mmr", IgnoredArmReason.LayerConflict), new IgnoredArm("exp_rerank/new", IgnoredArmReason.LayerConflict) },
            ignored);
    }

    [Fact]
    public void TheOutcome_DoesNotDependOnTheOrderOfThePlatformsList()
    {
        // The list order is no contract: every permutation gives the same arms and the same report, conflict included.
        string[] ids = { "exp_mmr/mmr", "exp_quality/strong", "exp_rerank/new", "exp_new/x", "exp_quality/strong" };
        var expected = Resolve(ids, out var expectedIgnored);

        foreach (var permutation in Permutations(ids))
        {
            var arms = Resolve(permutation, out var ignored);

            Assert.Equal(Assigned(expected), Assigned(arms));
            Assert.Equal(expectedIgnored, ignored);
        }

        Assert.Equal(new[] { -1, 3 }, Assigned(expected));
    }

    [Fact]
    public void NoList_IsNoArms_AndANullIdIsUnknown()
    {
        var none = Resolve(null, out var noneIgnored);
        var withNull = Resolve(new[] { null!, "exp_mmr/mmr" }, out var nullIgnored);

        Assert.Equal(new[] { -1, -1 }, Assigned(none));
        Assert.Empty(noneIgnored);
        Assert.Equal(new[] { 1, -1 }, Assigned(withNull));
        Assert.Equal(new[] { new IgnoredArm(string.Empty, IgnoredArmReason.Unknown) }, nullIgnored);
    }

    [Fact]
    public void APlanWithoutExperiments_IgnoresEveryIdAsUnknown()
    {
        var arms = ExternalArms.Resolve(Array.Empty<LayerInfo>(), Array.Empty<ArmInfo>(), new[] { "exp_mmr/mmr" }, out var ignored);

        Assert.Equal(0, arms.LayerCount);
        Assert.Equal(new[] { new IgnoredArm("exp_mmr/mmr", IgnoredArmReason.Unknown) }, ignored);
    }

    [Theory]
    [InlineData(IgnoredArmReason.Unknown, "unknown")]
    [InlineData(IgnoredArmReason.LayerConflict, "layer_conflict")]
    public void Reasons_AreSpelledOnce_AndReadBack(IgnoredArmReason reason, string name)
    {
        Assert.Equal(name, IgnoredArm.Name(reason));
        Assert.True(IgnoredArm.TryParse(name, out var parsed));
        Assert.Equal(reason, parsed);
        Assert.False(IgnoredArm.TryParse("Unknown", out _));
        Assert.False(IgnoredArm.TryParse(null, out _));
    }

    private static RequestArms Resolve(IReadOnlyList<string>? ids, out IgnoredArm[] ignored) => ExternalArms.Resolve(Layers, Arms, ids, out ignored);

    private static int[] Assigned(RequestArms arms) => Enumerable.Range(0, arms.LayerCount).Select(arms.ArmIn).ToArray();

    private static IEnumerable<string[]> Permutations(string[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return new[] { items[i] }.Concat(tail).ToArray();
            }
        }
    }
}
