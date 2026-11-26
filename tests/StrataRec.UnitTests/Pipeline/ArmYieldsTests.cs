using System.Collections.Immutable;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// The rule of <see cref="ArmYields"/>: an experiment yields in all its arms to an experiment the request is
/// in, judged on the arms as assigned, so no order and no arm of its own decides it.
/// </summary>
public sealed class ArmYieldsTests
{
    // Experiments A (layer 0: arms 0 control, 1 treatment), B (layer 1: arms 2, 3) and C (layer 2: arm 4).
    private static readonly int[] ExperimentOfArm = [0, 0, 1, 1, 2];
    private static readonly int[] LayerOfExperiment = [0, 1, 2];

    private static ArmYields Yields(params (int From, int To)[] yields)
    {
        var to = new List<int>[3] { new(), new(), new() };
        foreach (var (from, target) in yields)
        {
            to[from].Add(target);
        }

        return ArmYields.Create(ExperimentOfArm, LayerOfExperiment, [.. to.Select(list => list.ToImmutableArray())]);
    }

    private static int[] Run(ArmYields yields, int[] arms, List<int>? yielded = null)
    {
        var running = yields.Resolve(new RequestArms(arms), yielded);
        return Enumerable.Range(0, running.LayerCount).Select(running.ArmIn).ToArray();
    }

    [Fact]
    public void WithoutYields_TheArmsAreReturnedAsTheyAre()
    {
        var arms = new RequestArms([1, 3, 4]);

        Assert.True(Yields().IsEmpty);
        Assert.True(Yields().Resolve(arms).SameAs(arms));
        var notInB = new RequestArms([1, -1, 4]);
        Assert.True(Yields((0, 1)).Resolve(notInB).SameAs(notInB)); // nothing yields: no copy
    }

    [Fact]
    public void AnExperimentYields_InEachOfItsArms_ToAnExperimentTheRequestIsIn()
    {
        var yields = Yields((0, 1));
        var yielded = new List<int>();

        Assert.Equal([-1, 2, -1], Run(yields, [1, 2, -1], yielded));
        Assert.Equal([1], yielded);
        Assert.Equal([-1, 3, 4], Run(yields, [0, 3, 4])); // the control too: the experiment's arms stay comparable
        Assert.Equal([1, -1, 4], Run(yields, [1, -1, 4])); // not in B: A runs
        Assert.Equal(1, yields.YieldsTo(1, new RequestArms([1, 2, -1])));
        Assert.Equal(-1, yields.YieldsTo(2, new RequestArms([1, 2, -1]))); // B yields to nothing
    }

    [Fact]
    public void AChain_IsJudgedOnTheArmsAsAssigned()
    {
        // A yields to B, B to C: in all three only C runs; A yields to B although B yields itself.
        var yields = Yields((0, 1), (1, 2));
        var yielded = new List<int>();

        Assert.Equal([-1, -1, 4], Run(yields, [1, 2, 4], yielded));
        Assert.Equal([1, 2], yielded); // in layer order
        Assert.Equal([1, -1, 4], Run(yields, [1, -1, 4])); // A does not yield to C
        Assert.True(yields.Yields(0, 1) && yields.Yields(1, 2) && !yields.Yields(0, 2) && !yields.Yields(1, 0));
    }

    [Fact]
    public void TheArmsGiven_AreNotChanged()
    {
        var armByLayer = new[] { 1, 2, -1 };

        Run(Yields((0, 1)), armByLayer);

        Assert.Equal([1, 2, -1], armByLayer);
    }
}
