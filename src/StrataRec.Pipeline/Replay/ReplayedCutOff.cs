using StrataRec.Core.Diagnostics;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// A recorded call that the budget or the caller cut off, thrown again by a replay as the operator saw it: with how it
/// ended (<see cref="IoOutcome.Timeout"/> or <see cref="IoOutcome.Cancelled"/>), which the replay's own tokens cannot
/// tell (its caller did not cancel). A feature source reports it as the request did.
/// </summary>
internal sealed class ReplayedCutOff(string message, IoOutcome outcome) : OperationCanceledException(message)
{
    public IoOutcome Outcome { get; } = outcome;
}
