using System.Collections.Immutable;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The request's view of one feature connector call as it finished: what was asked, how it ended and how long it took.
/// </summary>
/// <param name="Group">Source and entity the call fetched from.</param>
/// <param name="Batch">The keys; valid only during <see cref="IFeatureCallHook.Record"/>.</param>
/// <param name="Fields">Requested fields; staging field i is <c>Fields[i]</c>. Read-only: they may be the plan's own.</param>
/// <param name="Sequence">Start order within the request, from <see cref="IFeatureCallHook.Begin"/>.</param>
/// <param name="Sent">False when the call was not sent: the fetch had no time left, or the caller had cancelled, before it.</param>
/// <param name="Status">What the request did with the call (a timeout is a failed status).</param>
/// <param name="Outcome">How the call ended, as metrics see it.</param>
/// <param name="Duration">Time from start until the request stopped waiting.</param>
public readonly record struct FeatureCallInfo(
    FetchGroup Group,
    EntityKeyBatch Batch,
    ImmutableArray<CompiledFieldBinding> Fields,
    int Sequence,
    bool Sent,
    FetchStatus Status,
    IoOutcome Outcome,
    TimeSpan Duration);

/// <summary>Read access to what a connector wrote, for recording a completed call.</summary>
/// <remarks>Valid only during <see cref="IFeatureCallHook.Record"/>.</remarks>
public interface IStagedFeatureValues
{
    /// <summary>The value the connector wrote for (field, key), or missing when it wrote none.</summary>
    ScalarValue Get(int fieldIndex, int keyIndex);
}

/// <summary>
/// Per-request hook at the single place feature connectors are called. A recorder sees every call when it finishes; a
/// replayer answers calls from a recording instead of the connector, without waiting.
/// </summary>
public interface IFeatureCallHook
{
    /// <summary>True when calls are answered by <see cref="Replay"/> and no connector is called.</summary>
    bool Replays { get; }

    /// <summary>
    /// A call is about to be sent: returns its start order. Calls start on the request's thread, so the order is
    /// deterministic, but for a context group keyed by context features, which starts where the last of them was stored (a
    /// connector's completion), so its place follows when that was; may be called concurrently. Every begun call is later
    /// passed to <see cref="Record"/> with this sequence; until then a recorder holds it as in flight, so a recording taken
    /// when the request is aborted lists it as outstanding.
    /// </summary>
    /// <param name="batch">The keys; valid only during the call.</param>
    int Begin(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields);

    /// <summary>
    /// Records a finished call. <paramref name="values"/> is non-null only when the call answered within its budget
    /// (ok or partial); a call that timed out may still be writing, so its staging is never read. Both arguments are
    /// valid only during this call.
    /// </summary>
    void Record(in FeatureCallInfo call, IStagedFeatureValues? values);

    /// <summary>Answers a call from the recording: writes recorded values into <paramref name="writer"/> and returns the recorded status.</summary>
    FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields, IFeatureBatchWriter writer);
}
