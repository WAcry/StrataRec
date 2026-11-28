using StrataRec.Core.Values;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Assignment;

/// <summary>
/// Traffic allocation (ratios, ramping, salts, holdouts) is the platform's; the engine only applies the arms it
/// answers.
/// </summary>
/// <remarks>
/// <para>The engine asks only for requests that do not carry the platform's answer already (<c>RecRequest.Assignment</c>)
/// and are not forced (<c>DebugOptions.ForceArms</c>). It keeps no mapping between the platform's ids and the plan's: the
/// provider translates, using the plan's metadata in <see cref="AssignmentContext"/>.</para>
/// <para>The call runs inline on the request's thread. What the provider does before it returns its
/// <see cref="ValueTask{T}"/> is therefore not bounded: the request waits for it however long it takes. Do the
/// platform's I/O asynchronously, or answer quickly from a local SDK. A provider the request stops waiting for, at the
/// deadline or because the caller cancelled, has its token cancelled, and the token stays valid until the provider
/// ends.</para>
/// <para>An answer is used only if the engine observes it before the deadline and before the caller cancels.</para>
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

/// <summary>What an <see cref="IAssignmentProvider"/> is told about the request it assigns.</summary>
/// <remarks>
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
