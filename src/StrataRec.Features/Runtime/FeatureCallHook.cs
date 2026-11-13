using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The request's view of one feature connector call as it finished: what was asked, how it ended and how long it took.
/// </summary>
/// <param name="Group">Source and entity the call fetched from.</param>
/// <param name="Batch">The keys; valid only during <see cref="IFeatureCallHook.Record"/> (they live in pooled buffers).</param>
/// <param name="Fields">Requested fields; staging field i is <c>Fields[i]</c>.</param>
/// <param name="Sequence">Start order within the request, from <see cref="IFeatureCallHook.Begin"/>.</param>
/// <param name="Sent">False when the request had no time left to send the call at all.</param>
/// <param name="Status">What the request did with the call (a timeout is a failed status).</param>
/// <param name="Outcome">How the call ended, as metrics see it.</param>
/// <param name="Duration">Time from start until the request stopped waiting.</param>
public readonly record struct FeatureCallInfo(
    FetchGroup Group,
    EntityKeyBatch Batch,
    CompiledFieldBinding[] Fields,
    int Sequence,
    bool Sent,
    FetchStatus Status,
    IoOutcome Outcome,
    TimeSpan Duration);

/// <summary>Read access to what a connector wrote, for recording a completed call.</summary>
/// <remarks>Valid only during <see cref="IFeatureCallHook.Record"/>: it is a view of one chunk of the fetch's staging,
/// and the engine points the same object at the fetch's next chunk once the call has been recorded.</remarks>
public interface IStagedFeatureValues
{
    /// <summary>The value the connector wrote for (field, key), or missing when it wrote none.</summary>
    ScalarValue Get(int fieldIndex, int keyIndex);
}

/// <summary>
/// Per-request hook at the single place feature connectors are called (design 13.4). A recorder sees every call when it
/// finishes; a replayer answers calls from a recording instead of the connector, without waiting. The fetcher gets a hook
/// only for requests that record or replay, so other requests pay one null check per call.
/// </summary>
/// <remarks>
/// Hooking the call site rather than the connectors makes every connector a host registers recordable and replayable
/// without its own support, and lets a recording read the typed staging buffer the connector decoded into.
/// </remarks>
public interface IFeatureCallHook
{
    /// <summary>True when calls are answered by <see cref="Replay"/> and no connector is called.</summary>
    bool Replays { get; }

    /// <summary>
    /// A call is about to be sent: returns its start order (calls start on the request's thread, so the order is
    /// deterministic). Every begun call is later passed to <see cref="Record"/> with this sequence; until then a recorder
    /// holds it as in flight, so a recording taken when the request is aborted lists it as outstanding.
    /// </summary>
    /// <param name="batch">The keys; valid only during the call (they live in pooled buffers).</param>
    int Begin(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields);

    /// <summary>
    /// Records a finished call. <paramref name="values"/> is non-null only when the call answered within its budget
    /// (ok or partial); a call that timed out may still be writing, so its staging is never read. Both arguments are
    /// valid only during this call.
    /// </summary>
    void Record(in FeatureCallInfo call, IStagedFeatureValues? values);

    /// <summary>Answers a call from the recording: writes recorded values into <paramref name="writer"/> and returns the recorded status.</summary>
    FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer);
}
