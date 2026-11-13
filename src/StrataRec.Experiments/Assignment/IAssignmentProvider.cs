using StrataRec.Core.Values;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Assignment;

/// <summary>
/// Asks the experimentation platform which arms a request is in (design 11.1). Traffic allocation (ratios, ramping,
/// salts, holdouts) is the platform's; the engine only applies the arms it answers.
/// </summary>
/// <remarks>
/// The engine asks only for requests that do not carry the platform's answer already (<c>RecRequest.Assignment</c>) and
/// are not forced (<c>DebugOptions.ForceArms</c>). The call shares the request's deadline: its token is cancelled at the
/// deadline, and a provider that ignores the token is not waited for beyond it (the request fails as past its deadline).
/// The engine keeps no mapping between the platform's ids and the plan's: the provider translates, using the plan's
/// metadata in <see cref="AssignmentContext"/>.
/// </remarks>
public interface IAssignmentProvider
{
    /// <summary>
    /// Returns the request's arms as <c>experiment/arm</c> ids (<see cref="ArmInfo.Id"/>), in any order. What the request
    /// cannot run with is ignored, counted and reported, never an error (<see cref="ExternalArms"/>): an id the served
    /// plan does not have, and two arms of one layer (that layer then runs its baseline).
    /// </summary>
    ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken);
}

/// <summary>What an <see cref="IAssignmentProvider"/> is told about the request it assigns (design 11.1).</summary>
/// <remarks>
/// <para>A readonly struct of references the engine holds anyway (the plan's views are built once per plan), so asking a
/// provider allocates nothing for it. New members are added as optional init properties, so neither providers nor the
/// tests that build contexts break when the context grows.</para>
/// <para>The metadata is the served plan's: a layer-oriented platform can be asked for exactly these layers, and its
/// answer translated into these arm ids. Experiments the platform runs but this release does not have are not here; an
/// id for them is ignored.</para>
/// </remarks>
public readonly struct AssignmentContext
{
    public required string PipelineId { get; init; }

    public required string RequestId { get; init; }

    /// <summary>The request's inputs as the caller passed them (user id, session id, …); the platform's unit is among them.</summary>
    public required RequestInputs Inputs { get; init; }

    /// <summary>The plan's layers by index (<see cref="LayerInfo.Index"/>): a request is in at most one arm per layer.</summary>
    public required IReadOnlyList<LayerInfo> Layers { get; init; }

    /// <summary>The plan's experiments by index, each with its layer and arms.</summary>
    public required IReadOnlyList<ExperimentInfo> Experiments { get; init; }

    /// <summary>The plan's arms by index; <see cref="ArmInfo.Id"/> is what <see cref="IAssignmentProvider.AssignAsync"/> returns.</summary>
    public required IReadOnlyList<ArmInfo> Arms { get; init; }
}
