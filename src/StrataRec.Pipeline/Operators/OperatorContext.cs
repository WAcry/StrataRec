using System.Runtime.ExceptionServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Replay;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// The runtime view one node attempt gets of its request. Row-set operations are available only for the effects the
/// operator declared (design 5.2), so declarations and behavior cannot drift apart.
/// </summary>
/// <remarks>
/// <para>The attempt has an absolute deadline, its time budget (design 5.6), and <see cref="CancellationToken"/> is
/// cancelled at it or with the caller's token. Cancellation is cooperative, so the framework does not rely on it at its
/// I/O boundary: <see cref="RetrieveAsync"/> and <see cref="InvokeModelAsync"/> wait for a client no longer than the
/// deadline (or the call's own timeout) and the caller allow, whether or not the client honours its token. A client still
/// running then is abandoned: it keeps the request memory held until it really completes, and nothing it writes
/// afterwards is read. An attempt whose operator returns only after the deadline has timed out all the same.</para>
/// <para>Custom operators doing their own I/O outside these two methods are responsible for bounding it: by
/// <see cref="CancellationToken"/> when the client honours it, or by waiting no longer than <see cref="BudgetRemaining"/>
/// and keeping every buffer the client may still write alive (through <see cref="RequestMemory.Hold"/>) until it completes.
/// Such I/O is also not recorded or replayed (design 13.4).</para>
/// <para>Nothing may write into <see cref="Table"/> once the attempt has returned: a failed attempt's appended rows are
/// rolled back and reused by the fallback (<see cref="CandidateTable.Rollback"/>), so a late write would land on the
/// fallback's rows. Clients called through this context write into rented buffers, never into the table.</para>
/// </remarks>
public sealed class OperatorContext
{
    private readonly RequestState _state;
    private readonly Effects _effects;

    internal OperatorContext(RequestState state, string nodeId, Effects effects, AttemptBudget budget)
    {
        _state = state;
        NodeId = nodeId;
        _effects = effects;
        Budget = budget;
    }

    public string NodeId { get; }

    /// <summary>
    /// The node token: cancelled when the attempt's budget runs out or the caller cancels. Clients called through this
    /// context get it, or a token linked to it that their own timeout also cancels.
    /// </summary>
    public CancellationToken CancellationToken => Budget.Token;

    /// <summary>
    /// Time left in this attempt's budget (the node's <c>timeout_ms</c>, or the request's remaining time, less the fallback
    /// reserve); never negative. <see cref="TimeSpan.MaxValue"/> when the attempt is untimed (a replay).
    /// </summary>
    public TimeSpan BudgetRemaining => Budget.Remaining;

    /// <summary>The attempt's budget; the executor releases its reference when the attempt ends.</summary>
    internal AttemptBudget Budget { get; }

    public CandidateTable Table => _state.Table;

    /// <summary>Live physical rows in rank order.</summary>
    public ReadOnlySpan<int> LiveRows => _state.Table.Rows.Order;

    public RequestMemory Memory => _state.Memory;

    public string RequestId => _state.RequestId;

    /// <summary>Request seed for deterministic randomness (design 5.7).</summary>
    public long Seed => _state.Seed;

    public RequestInputs Inputs => _state.Inputs;

    public EvalContext EvalContext => new(_state.Table, _state.Bindings, _state.Context, _state.Params, _state.Counters);

    /// <summary>Time left before the request deadline.</summary>
    public TimeSpan Remaining => _state.Remaining;

    /// <summary>Health and windowed failure summaries of external calls (design 13.2); null without a logger.</summary>
    internal RequestEventLog? Events => _state.Events;

    /// <summary>
    /// The caller's token, as opposed to <see cref="CancellationToken"/>, which the node budget also cancels: external
    /// calls cut off by the caller are not counted as dependency timeouts.
    /// </summary>
    internal CancellationToken CallerCancellation => _state.CallerCancellation;

    /// <summary>
    /// True while the request replays a recording: it is not traffic, so traffic metrics are not counted (design 13.4).
    /// Public for operators outside this assembly that count metrics of their own (<c>stratarec.model.answer_ids</c>).
    /// </summary>
    public bool Replays => _state.Replay is not null;

    internal string? FailureReason { get; private set; }

    internal string? DegradedReason { get; private set; }

    /// <summary>The committed version of a column (or its fixed slot); null when not produced in this request.</summary>
    public ColumnBuffer? Read(in ColumnRef column)
    {
        var slot = column.FixedSlot >= 0 ? column.FixedSlot : _state.Bindings.Resolve(column.LogicalId);
        return slot < 0 ? null : _state.Table.TryGetSlot(slot);
    }

    /// <summary>This node's buffer for an output column. Written values become visible only if the node succeeds.</summary>
    public ColumnBuffer Output(in OutputColumn column) => _state.Table.GetOrCreateSlot(column.Slot);

    public ScalarValue Param(in ParamRef parameter) => _state.Params[parameter.Id];

    public ScalarValue ContextValue(int contextId) => _state.Context[contextId];

    /// <summary>Drops rows whose rank position is not flagged. Requires <see cref="Effects.RowFilter"/>.</summary>
    public void Retain(ReadOnlySpan<bool> keepByRank)
    {
        Require(Effects.RowFilter, nameof(Retain));
        _state.Table.Rows.Retain(keepByRank);
    }

    /// <summary>Keeps the first <paramref name="count"/> rows. Requires <see cref="Effects.RowFilter"/>.</summary>
    public void Truncate(int count)
    {
        Require(Effects.RowFilter, nameof(Truncate));
        _state.Table.Rows.Truncate(count);
    }

    /// <summary>
    /// Sets a new ranking. A permutation of the live rows needs <see cref="Effects.RowOrder"/>; dropping rows also
    /// needs <see cref="Effects.RowFilter"/>.
    /// </summary>
    public void SetOrder(ReadOnlySpan<int> newOrder)
    {
        Require(Effects.RowOrder, nameof(SetOrder));
        var drops = newOrder.Length < _state.Table.Rows.Count;
        if (drops)
        {
            Require(Effects.RowFilter, nameof(SetOrder) + " (dropping rows)");
        }

        _state.Table.Rows.Replace(newOrder, allowDrop: drops);
    }

    /// <summary>Appends rows. Requires <see cref="Effects.RowSource"/>.</summary>
    public int AppendRows(ReadOnlySpan<long> keys)
    {
        Require(Effects.RowSource, nameof(AppendRows));
        return _state.Table.AppendRows(keys);
    }

    /// <summary>Appends rows with string keys. Requires <see cref="Effects.RowSource"/>.</summary>
    public int AppendRows(IReadOnlyList<string> keys)
    {
        Require(Effects.RowSource, nameof(AppendRows));
        return _state.Table.AppendRows(keys);
    }

    /// <summary>
    /// Calls a candidate source for this node the way the framework calls every external dependency (through
    /// <see cref="BoundedCall"/>): holds the request memory until the source completes (design 4.8), waits for it no
    /// longer than <paramref name="timeoutMs"/> and the node's budget allow, records duration, errors, health and a span
    /// through <paramref name="target"/> (design 13.1, 13.2), closes the buffer, and records the call or answers it from a
    /// recording when the request records or replays (design 13.4). Custom operators that call sources through it are
    /// bounded, recorded and replayed like the built-in ones.
    /// </summary>
    /// <param name="timeoutMs">The source's own timeout (its binding's <c>timeout_ms</c>), within the node's budget; null for none.</param>
    /// <returns>
    /// The source's status. A source failure, exception or own timeout becomes a failed status; only the node's budget
    /// running out or the caller cancelling throws <see cref="OperationCanceledException"/>.
    /// </returns>
    /// <remarks>
    /// The wait ends at the earlier of the source's timeout and the node's deadline, or when the caller cancels, even if the
    /// source ignores its token. A source still running then is abandoned: it keeps the request memory held until it
    /// completes, and <paramref name="buffer"/> reads as empty from then on, so neither the operator nor the recording reads
    /// a buffer the source may still be writing. The recording has the call as the request saw it (a timeout or a
    /// cancellation, without candidates), and a replay takes the same path at once.
    /// </remarks>
    public async Task<CandidateSourceStatus> RetrieveAsync(ICandidateSource source, IoTarget target, int? timeoutMs, CandidateQuery query, CandidateBuffer buffer)
    {
        if (_state.Replay is { } replay)
        {
            var replayed = replay.Retrieve(NodeId, target.Name, query, buffer);
            buffer.Close();
            return replayed;
        }

        var recorder = _state.Recorder;
        var sequence = recorder?.BeginCandidateCall(NodeId, target.Name, query.Limit) ?? 0;
        var result = await BoundedCall.RunAsync<CandidateSourceStatus, RetrieveCall>(
            new RetrieveCall(source, query, buffer, recorder, sequence), Budget, _state.Memory, target, timeoutMs, query.RequestId, _state.Events)
            .ConfigureAwait(false);
        return result.End switch
        {
            CallEnd.Answered => result.Value,
            CallEnd.Threw or CallEnd.TimedOut => CandidateSourceStatus.Failed(result.Error ?? "failed"),
            _ => throw result.Exception!, // the node's budget or the caller
        };
    }

    /// <summary>
    /// Calls a model for this node the way the framework calls every external dependency (through
    /// <see cref="BoundedCall"/>): holds the request memory until the client completes (design 4.8), waits for it no
    /// longer than <paramref name="timeoutMs"/> and the node's budget allow, records duration, errors, health and a span
    /// through <paramref name="target"/>, and records the call or answers it from a recording when the request records or
    /// replays (design 13.4).
    /// </summary>
    /// <param name="rows">
    /// Physical rows of the batch, one per entry of the leading dimension of row-wise inputs and outputs; a recording
    /// keys each row's output by the row's entity id, so a replay matches outputs per entity even when the batches
    /// differ. Empty for calls whose outputs are not per row; those are matched by their whole input.
    /// </param>
    /// <param name="timeoutMs">The model's own timeout (its binding's <c>timeout_ms</c>), within the node's budget; null for none.</param>
    /// <returns>
    /// The client's status. The model's own timeout becomes a failed status; the node's budget running out or the caller
    /// cancelling throws <see cref="OperationCanceledException"/>; any other exception the client throws propagates.
    /// </returns>
    /// <remarks>
    /// <para>The wait ends at the earlier of the model's timeout and the node's deadline, or when the caller cancels, even
    /// if the client ignores its token. A client still running then is abandoned: it keeps the request memory held until it
    /// completes, so what it writes into <paramref name="outputs"/> afterwards lands in live buffers. Nothing reads them:
    /// the call has failed, and an operator must not read the outputs of a call that did not succeed. The recording has
    /// the call as the request saw it (a timeout or a cancellation, without outputs).</para>
    /// <para>A replay returns or throws what the call returned or threw in the recording, so an operator that handles
    /// particular client exceptions takes the same path: a <see cref="TimeoutException"/> as
    /// <see cref="TimeoutException"/>, cancellation as <see cref="OperationCanceledException"/>, any other type recreated
    /// through its <c>(string message)</c> constructor when the replaying process has the type; otherwise a
    /// <see cref="ReplayedCallException"/> naming the original type.</para>
    /// </remarks>
    public async Task<ModelStatus> InvokeModelAsync(
        IModelClient client, IoTarget target, ModelCall call, IModelOutputWriter outputs, ReadOnlyMemory<int> rows, int? timeoutMs)
    {
        if (_state.Replay is { } replay)
        {
            return replay.InvokeModel(call, outputs, KeysOf(rows));
        }

        var recorder = _state.Recorder;
        var sequence = recorder?.BeginModelCall(NodeId, call, KeysOf(rows)) ?? 0;
        var recording = recorder is null ? null : new RecordingOutputWriter(outputs);
        var result = await BoundedCall.RunAsync<ModelStatus, ModelInvocation>(
            new ModelInvocation(client, call, (IModelOutputWriter?)recording ?? outputs, target, recorder, recording, sequence), Budget, _state.Memory, target,
            timeoutMs, RequestId, _state.Events).ConfigureAwait(false);
        switch (result.End)
        {
            case CallEnd.Answered:
                return result.Value;
            case CallEnd.TimedOut:
                return ModelStatus.Failed(result.Error!);
            default:
                // What the client threw, or the cancellation of the node's budget or the caller.
                ExceptionDispatchInfo.Throw(result.Exception!);
                return default;
        }
    }

    /// <summary>Marks the result as Degraded (completed with partial or stale inputs, design 5.6).</summary>
    public void ReportDegraded(string reason) => DegradedReason ??= reason;

    /// <summary>Fails the node without throwing; the engine applies <c>on_failure</c>.</summary>
    public void Fail(string reason) => FailureReason ??= reason;

    /// <summary>Entity ids of a batch's rows; computed only for requests that record or replay (no per-row work otherwise).</summary>
    private EntityKey[]? KeysOf(ReadOnlyMemory<int> rows)
    {
        if (rows.IsEmpty)
        {
            return null;
        }

        var span = rows.Span;
        var keys = new EntityKey[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            keys[i] = _state.Table.GetKey(span[i]);
        }

        return keys;
    }

    private void Require(Effects effect, string operation)
    {
        if ((_effects & effect) == 0)
        {
            throw new InvalidOperationException(
                $"Node '{NodeId}' called {operation}, which needs the {effect} effect, but its operator does not declare it.");
        }
    }

    /// <summary>A candidate source call: closes or abandons the buffer when the call ends, and records what the request saw.</summary>
    private readonly struct RetrieveCall(ICandidateSource source, CandidateQuery query, CandidateBuffer buffer, RunRecorder? recorder, int sequence)
        : IBoundedCall<CandidateSourceStatus>
    {
        public ValueTask<CandidateSourceStatus> Send(CancellationToken token) => source.RetrieveAsync(query, buffer, token);

        public bool Succeeded(CandidateSourceStatus value, out string? error)
        {
            error = value.Error;
            return value.Succeeded;
        }

        public void Ended(in CallResult<CandidateSourceStatus> result, TimeSpan duration)
        {
            // Closed before the memory is released: a source adding after it completed cannot write into returned buffers.
            // One still running may be in the middle of an add: its buffer reads as empty from now on.
            if (result.Abandoned)
            {
                buffer.Abandon();
            }
            else
            {
                buffer.Close();
            }

            recorder?.CandidateCall(sequence, result.Outcome, result.Error, duration, result.Outcome == IoOutcome.Ok ? buffer : null);
        }
    }

    /// <summary>
    /// A model call: counts what a generative client reported (<see cref="StrataRecMetrics.ModelOutputTokens"/>,
    /// <see cref="StrataRecMetrics.ModelTruncated"/>) for every call that answered, in this one place for every model
    /// operator, and records what the request saw, with the outputs only of a call that answered. A replay never gets here:
    /// it is answered from the recording before the call is sent, so it counts nothing.
    /// </summary>
    private readonly struct ModelInvocation(
        IModelClient client, ModelCall call, IModelOutputWriter outputs, IoTarget target, RunRecorder? recorder, RecordingOutputWriter? recording, int sequence)
        : IBoundedCall<ModelStatus>
    {
        public ValueTask<ModelStatus> Send(CancellationToken token) => client.InvokeAsync(call, outputs, token);

        public bool Succeeded(ModelStatus value, out string? error)
        {
            error = value.Error;
            return value.Succeeded;
        }

        public void Ended(in CallResult<ModelStatus> result, TimeSpan duration)
        {
            // What the operator is thrown, so a replay throws the same (an own timeout is a failed status, not a throw).
            var thrown = result.End is CallEnd.Threw or CallEnd.BudgetRanOut or CallEnd.Cancelled ? result.Exception?.GetType() : null;
            var answered = result.End == CallEnd.Answered;
            if (answered)
            {
                CountGenerated(result.Value, target);
            }

            recorder?.ModelCall(sequence, result.Outcome, result.Error, thrown, duration, recording!, answered ? result.Value : default);
        }

        private static void CountGenerated(in ModelStatus status, IoTarget target)
        {
            if (status.OutputTokens is { } tokens && StrataRecMetrics.ModelOutputTokens.Enabled)
            {
                StrataRecMetrics.ModelOutputTokens.Add(tokens, target.Tags);
            }

            if (status.Truncated && StrataRecMetrics.ModelTruncated.Enabled)
            {
                StrataRecMetrics.ModelTruncated.Add(1, target.Tags);
            }
        }
    }
}
