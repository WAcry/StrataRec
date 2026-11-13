using StrataRec.Core.Values;

namespace StrataRec.Experiments.Assignment;

/// <summary>
/// Asks the experimentation platform which arms a request is in (design 11.1). Traffic allocation (ratios, ramping,
/// salts, holdouts) is the platform's; the engine only applies the arms it answers.
/// </summary>
public interface IAssignmentProvider
{
    /// <summary>Returns the assigned arms as <c>experiment/arm</c> ids. Unknown arms are ignored and counted.</summary>
    ValueTask<IReadOnlyList<string>> AssignAsync(string pipelineId, RequestInputs inputs, CancellationToken cancellationToken);
}
