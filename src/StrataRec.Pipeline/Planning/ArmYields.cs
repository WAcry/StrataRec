using System.Collections.Immutable;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// Which experiments yield to which (a release's <c>yields_to</c>): an experiment that yields to another
/// runs in none of its arms on a request that is also in an arm of the other. The one rule for the arms a request is
/// assigned and for the combinations the experiment compiler validates, so what is validated is what runs.
/// </summary>
/// <remarks>
/// <para>Judged on the arms a request is in, not on those left to run: whether an experiment runs then depends only on
/// the other experiments' assignment, never on its own arm or on an order. Its arms stay comparable with each other
/// (its control arm yields as its other arms do), and the experiment it yields to never runs beside it. A request in arms of
/// A, B and C where C yields to B and B to A runs A only.</para>
/// <para>Two experiments one of which yields to the other never run together, as two of one layer do not: the compiler
/// neither checks the pair for conflicts nor compiles their combinations.</para>
/// </remarks>
public sealed class ArmYields
{
    private readonly int[] _experimentOfArm;
    private readonly int[] _layerOfExperiment;
    private readonly ImmutableArray<int>[] _yieldsTo;

    private ArmYields(int[] experimentOfArm, int[] layerOfExperiment, ImmutableArray<int>[] yieldsTo)
    {
        _experimentOfArm = experimentOfArm;
        _layerOfExperiment = layerOfExperiment;
        _yieldsTo = yieldsTo;
        foreach (var to in yieldsTo)
        {
            IsEmpty &= to.IsDefaultOrEmpty;
        }
    }

    /// <summary>No experiment yields.</summary>
    public static ArmYields None { get; } = new([], [], []);

    /// <summary>True when no experiment yields to another: <see cref="Resolve"/> returns the arms it is given.</summary>
    public bool IsEmpty { get; } = true;

    /// <summary>The yields of a plan's experiments (<see cref="ExperimentInfo.YieldsTo"/>).</summary>
    public static ArmYields Create(ImmutableArray<ExperimentInfo> experiments, ImmutableArray<ArmInfo> arms)
    {
        var experimentOfArm = new int[arms.Length];
        foreach (var arm in arms)
        {
            experimentOfArm[arm.Index] = arm.ExperimentIndex;
        }

        var layerOfExperiment = new int[experiments.Length];
        var yieldsTo = new ImmutableArray<int>[experiments.Length];
        foreach (var experiment in experiments)
        {
            layerOfExperiment[experiment.Index] = experiment.LayerIndex;
            yieldsTo[experiment.Index] = experiment.YieldsTo;
        }

        return Create(experimentOfArm, layerOfExperiment, yieldsTo);
    }

    /// <summary>The yields over arms and experiments by index: each arm's experiment, each experiment's layer and the experiments it yields to.</summary>
    public static ArmYields Create(int[] experimentOfArm, int[] layerOfExperiment, ImmutableArray<int>[] yieldsTo)
    {
        ArgumentNullException.ThrowIfNull(experimentOfArm);
        ArgumentNullException.ThrowIfNull(layerOfExperiment);
        ArgumentNullException.ThrowIfNull(yieldsTo);
        return new ArmYields(experimentOfArm, layerOfExperiment, yieldsTo);
    }

    /// <summary>
    /// <paramref name="arms"/> without the arms that yield (their layers run the baseline), or <paramref name="arms"/>
    /// itself when none does; each arm that yields is added to <paramref name="yielded"/>, in layer order.
    /// </summary>
    public RequestArms Resolve(in RequestArms arms, List<int>? yielded = null)
    {
        if (IsEmpty)
        {
            return arms;
        }

        int[]? running = null;
        for (var layer = 0; layer < arms.LayerCount; layer++)
        {
            var arm = arms.ArmIn(layer);
            if (arm >= 0 && YieldsTo(arm, arms) >= 0)
            {
                running ??= arms.CopyArms(); // judged on the arms as assigned, so the copy is written, not read
                running[layer] = -1;
                yielded?.Add(arm);
            }
        }

        return running is null ? arms : new RequestArms(running);
    }

    /// <summary>The experiment <paramref name="arm"/>'s experiment yields to that has an arm in <paramref name="arms"/>, or -1.</summary>
    public int YieldsTo(int arm, in RequestArms arms)
    {
        if (IsEmpty)
        {
            return -1;
        }

        var yieldsTo = _yieldsTo[_experimentOfArm[arm]];
        if (yieldsTo.IsDefault)
        {
            return -1;
        }

        foreach (var to in yieldsTo)
        {
            var other = arms.ArmIn(_layerOfExperiment[to]);
            if (other >= 0 && _experimentOfArm[other] == to)
            {
                return to;
            }
        }

        return -1;
    }

    /// <summary>The experiments <paramref name="experiment"/> yields to, ascending.</summary>
    public ImmutableArray<int> YieldsOf(int experiment) =>
        experiment < _yieldsTo.Length && !_yieldsTo[experiment].IsDefault ? _yieldsTo[experiment] : [];

    /// <summary>Whether experiment <paramref name="experiment"/> yields to <paramref name="to"/>, so their arms never run together.</summary>
    public bool Yields(int experiment, int to)
    {
        if (IsEmpty || _yieldsTo[experiment].IsDefault)
        {
            return false;
        }

        return _yieldsTo[experiment].IndexOf(to) >= 0;
    }
}
