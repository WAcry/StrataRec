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
/// The runtime view one node gets of its request. Row-set operations are available only for the effects the
/// operator declared (design 5.2), so declarations and behavior cannot drift apart.
/// </summary>
public sealed class OperatorContext
{
    private readonly RequestState _state;
    private readonly Effects _effects;

    internal OperatorContext(RequestState state, string nodeId, Effects effects, CancellationToken cancellationToken)
    {
        _state = state;
        NodeId = nodeId;
        _effects = effects;
        CancellationToken = cancellationToken;
    }

    public string NodeId { get; }

    public CancellationToken CancellationToken { get; }

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
    /// Calls a candidate source for this node the way the framework calls every external dependency: holds the request
    /// memory until the source completes (design 4.8), applies <paramref name="timeoutMs"/> within the node's budget,
    /// records duration, errors, health and a span through <paramref name="target"/> (design 13.1, 13.2), closes the
    /// buffer, and records the call or answers it from a recording when the request records or replays (design 13.4).
    /// Custom operators that call sources through it are recorded and replayed like the built-in ones.
    /// </summary>
    /// <returns>
    /// The source's status. A source failure, exception or own timeout becomes a failed status; only the node's budget
    /// running out or the caller cancelling throws <see cref="OperationCanceledException"/>.
    /// </returns>
    public async Task<CandidateSourceStatus> RetrieveAsync(ICandidateSource source, IoTarget target, int? timeoutMs, CandidateQuery query, CandidateBuffer buffer)
    {
        if (_state.Replay is { } replay)
        {
            var replayed = replay.Retrieve(NodeId, target.Name, query, buffer);
            buffer.Close();
            return replayed;
        }

        var recorder = _state.Recorder;
        var sequence = recorder?.NextSequence() ?? 0;
        var memory = _state.Memory;
        memory.Hold();
        var io = target.Start();
        var status = default(CandidateSourceStatus);
        var outcome = IoOutcome.Timeout; // the node's budget ran out (the exception propagates) unless set below
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            if (timeoutMs is { } timeout)
            {
                cts.CancelAfter(timeout);
            }

            status = await source.RetrieveAsync(query, buffer, cts.Token).ConfigureAwait(false);
            outcome = status.Succeeded ? IoOutcome.Ok : IoOutcome.Error;
            return status;
        }
        catch (OperationCanceledException) when (!CancellationToken.IsCancellationRequested)
        {
            return status = CandidateSourceStatus.Failed("timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A client's own transport timeout is a timeout, as for feature sources.
            outcome = ex is TimeoutException ? IoOutcome.Timeout : IoOutcome.Error;
            return status = CandidateSourceStatus.Failed(ex.Message);
        }
        finally
        {
            // Closed before the memory is released: a source adding after it completed cannot write into returned buffers.
            buffer.Close();

            // A call cut off by the caller is not the source's fault: neither timed nor counted against its health.
            if (outcome == IoOutcome.Timeout && _state.CallerCancellation.IsCancellationRequested)
            {
                outcome = IoOutcome.Cancelled;
            }

            var duration = recorder is null ? TimeSpan.Zero : io.Elapsed;
            io.Complete(outcome, -1, query.RequestId, status.Error, _state.Events);
            recorder?.CandidateCall(sequence, NodeId, target.Name, query.Limit, outcome, status.Error, duration, buffer);
            memory.Release();
        }
    }

    /// <summary>
    /// Calls a model for this node the way the framework calls every external dependency: holds the request memory until
    /// the client completes (design 4.8), records duration, errors, health and a span through <paramref name="target"/>,
    /// and records the call or answers it from a recording when the request records or replays (design 13.4).
    /// </summary>
    /// <param name="rows">
    /// Physical rows of the batch, one per entry of the leading dimension of row-wise inputs and outputs; a recording
    /// keys each row's output by the row's entity id, so a replay matches outputs per entity even when the batches
    /// differ. Empty for calls whose outputs are not per row; those are matched by their whole input.
    /// </param>
    /// <param name="cancellationToken">The node's token, or one linked to it with the model's own timeout.</param>
    /// <returns>The client's status. Exceptions, including cancellation, propagate to the operator.</returns>
    public async Task<ModelStatus> InvokeModelAsync(
        IModelClient client, IoTarget target, ModelCall call, IModelOutputWriter outputs, ReadOnlyMemory<int> rows, CancellationToken cancellationToken)
    {
        if (_state.Replay is { } replay)
        {
            return replay.InvokeModel(call, outputs, KeysOf(rows));
        }

        var recorder = _state.Recorder;
        var sequence = recorder?.NextSequence() ?? 0;
        var recording = recorder is null ? null : new RecordingOutputWriter(outputs);
        var memory = _state.Memory;
        memory.Hold();
        var io = target.Start();
        var outcome = IoOutcome.Error;
        string? error = null;
        var threw = false;
        try
        {
            var status = await client.InvokeAsync(call, (IModelOutputWriter?)recording ?? outputs, cancellationToken).ConfigureAwait(false);
            (outcome, error) = status.Succeeded ? (IoOutcome.Ok, null) : (IoOutcome.Error, status.Error);
            return status;
        }
        catch (OperationCanceledException)
        {
            // The model timeout or the node's budget, unless the caller gave up on the request.
            outcome = _state.CallerCancellation.IsCancellationRequested ? IoOutcome.Cancelled : IoOutcome.Timeout;
            threw = true;
            throw;
        }
        catch (Exception ex)
        {
            // A client's own transport timeout is a timeout, as for feature sources.
            outcome = ex is TimeoutException ? IoOutcome.Timeout : IoOutcome.Error;
            error = ex.Message;
            threw = true;
            throw;
        }
        finally
        {
            var duration = recorder is null ? TimeSpan.Zero : io.Elapsed;
            io.Complete(outcome, -1, RequestId, error, _state.Events);
            recorder?.ModelCall(sequence, NodeId, call, KeysOf(rows), outcome, error, threw, duration, recording!);
            memory.Release();
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
}
