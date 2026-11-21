using StrataRec.Core.Diagnostics;
using StrataRec.Core.Threading;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// The time budget of one node attempt (design 5.6): the bound (<see cref="CallBound"/>) its operator and every external
/// call it makes share, with the node token handed to the operator and its clients.
/// </summary>
internal sealed class AttemptBudget(CancellationToken caller, long deadline, RequestEventLog? events = null, string? requestId = null)
    : CallBound(CancellationSite.Node, caller, deadline, events, requestId);
