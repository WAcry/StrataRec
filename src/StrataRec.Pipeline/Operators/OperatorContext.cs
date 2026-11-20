using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Runtime;
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

    public CandidateTableView Table => new(_state.Table);

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

    /// <summary>
    /// This node's buffer for an output column. Written values become visible only if the node succeeds. A row source
    /// writes only the rows it appended; after a node has written its own version of the column, the buffer is that
    /// version, so the appended rows get their values in the column readers see (a score for injected rows, design 4.6).
    /// </summary>
    public ColumnBuffer Output(in OutputColumn column) => _state.Table.GetOrCreateSlot(_state.OutputSlot(_effects, column));

    public ScalarValue Param(in ParamRef parameter) => _state.Params[parameter.Id];

    public ScalarValue ContextValue(int contextId) => _state.Context[contextId];

    /// <summary>
    /// Waits until the context features of each of <paramref name="reads"/> are settled, and calls
    /// <paramref name="settled"/> for each as soon as it is, with its index and the worst state of its features: once every
    /// one is stored, or at once with <see cref="FeatureState.Failed"/> when one failed, since the operator gives up on what
    /// reads it whatever the others do. Then the awaited expressions they come from
    /// (<see cref="OperatorCompileContext.CompileAwaitedExpression"/>) can be evaluated (<see cref="EvalContext"/>); a failed
    /// feature is the operator's to handle, as it handles a failed call: its value is missing.
    /// </summary>
    /// <remarks>
    /// <para>For an operator sending several calls (a recall source's query, design 5.4), each as soon as its own features
    /// are stored; a call with no awaited reads is settled at once. The features were prefetched when the request started, so
    /// only what is still in flight is waited for.</para>
    /// <para>Each round observes the prefetch once, then settles, in the order of <paramref name="reads"/>, every read that
    /// this one observation settles, so reads whose features are stored together are settled in that order whenever the
    /// store happens; then it waits for any feature it observed in flight, so one stored meanwhile starts the next round at
    /// once (review 82: each read observed the tasks on its own, so a store between two reads put them in different rounds,
    /// and one between the check and the wait was missed until another feature was stored).</para>
    /// <para><paramref name="settled"/> runs on the operator's flow, one call at a time; what it throws ends the wait.</para>
    /// <para>The wait is bounded on its own by the attempt's deadline and the caller, as a call is
    /// (<see cref="RetrieveAsync"/>), not by <see cref="CancellationToken"/>, which a client's callback could hold up; and a
    /// prefetch that faulted is rethrown as it is, not taken for the deadline. The bound is also checked before each round
    /// and before each read is judged (an empty one's callback included), so what is stored after it passed is never
    /// settled, and a fault is not reported for a deadline that came first, however long a callback ran. An
    /// operator that stopped waiting reads nothing more of the request's.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">The attempt's budget ran out or the caller gave up first: the reads not
    /// settled by then never are, and the operator stops, as when a call it sent is cut off.</exception>
    public async ValueTask WhenEachReadyAsync(IReadOnlyList<AwaitedReads> reads, Action<int, FeatureFillResult> settled)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(settled);
        List<int>? waiting = null;
        for (var i = 0; i < reads.Count; i++)
        {
            if (reads[i].IsEmpty)
            {
                ThrowIfBoundPassed(); // the callback before may have run past it (review 86)
                settled(i, new FeatureFillResult(FeatureState.Ok));
            }
            else
            {
                (waiting ??= new List<int>(reads.Count)).Add(i);
            }
        }

        if (waiting is null)
        {
            return;
        }

        var features = _state.Features;
        var tasks = new List<Task>(); // this round's observation: each task the waiting reads wait for, once,
        var stored = new List<bool>(); // and whether it had completed
        var pending = new List<Task>();
        while (true)
        {
            ThrowIfBoundPassed();
            tasks.Clear();
            stored.Clear();
            pending.Clear();
            foreach (var i in waiting)
            {
                foreach (var f in reads[i].Features)
                {
                    var task = features.ContextStoredTask(f);
                    if (!tasks.Contains(task))
                    {
                        var completed = task.IsCompleted;
                        tasks.Add(task);
                        stored.Add(completed);
                        if (!completed)
                        {
                            pending.Add(task);
                        }
                    }
                }
            }

            for (var w = 0; w < waiting.Count; w++)
            {
                var i = waiting[w];
                // Before the read's state is read, not only before its callback: a faulted prefetch rethrown there must not
                // stand for a deadline or a caller that came first (review 86).
                ThrowIfBoundPassed();
                if (Settled(features, reads[i].Features, tasks, stored, out var result))
                {
                    waiting.RemoveAt(w--);
                    settled(i, result);
                }
            }

            if (waiting.Count == 0)
            {
                return;
            }

            try
            {
                // Task.WhenAny never faults, so a timeout here is the deadline's alone; a prefetch that faulted is rethrown by
                // the next round's ContextStoredState. The tasks are the prefetch's own: a wait cut off at the deadline
                // leaves nothing behind that reads the request's state.
                await Task.WhenAny(pending).WaitAsync(AttemptBudget.Until(Budget.Deadline), Budget.Caller).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw BudgetRanOut();
            }
        }
    }

    /// <summary>
    /// The bound of <see cref="WhenEachReadyAsync"/> outside its wait: a settled callback that ran past the deadline (or
    /// until the caller gave up) must not let the next round, or the next read of this one, settle what was stored late.
    /// The wait itself would not say so: a task already completed makes <c>Task.WhenAny</c> complete at once, and a timed
    /// wait on a completed task never times out (review 84).
    /// </summary>
    private void ThrowIfBoundPassed()
    {
        Budget.Caller.ThrowIfCancellationRequested();
        if (Budget.Token.IsCancellationRequested || Stopwatch.GetTimestamp() >= Budget.Deadline)
        {
            throw BudgetRanOut();
        }
    }

    private OperationCanceledException BudgetRanOut() =>
        new("The node's budget ran out while it waited for context features.", Budget.Token);

    /// <summary>Whether one round's observation settles <paramref name="features"/>, and with which state (see <see cref="WhenEachReadyAsync"/>).</summary>
    private static bool Settled(FeatureFetcher fetcher, ImmutableArray<int> features, List<Task> tasks, List<bool> stored, out FeatureFillResult result)
    {
        var worst = new FeatureFillResult(FeatureState.Ok);
        var all = true;
        foreach (var f in features)
        {
            if (!stored[tasks.IndexOf(fetcher.ContextStoredTask(f))])
            {
                all = false;
                continue;
            }

            var state = fetcher.ContextStoredState(f);
            if (state.Failed)
            {
                result = state;
                return true;
            }

            if (state.State > worst.State)
            {
                worst = state;
            }
        }

        result = all ? worst : default;
        return all;
    }

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
        var sequence = recorder?.BeginCandidateCall(NodeId, target.Name, query.Limit, query.Values) ?? 0;
        var result = await BoundedCall.RunAsync<CandidateSourceStatus, RetrieveCall>(
            new RetrieveCall(source, query, buffer, recorder, sequence), Budget, _state.Memory, target, timeoutMs, query.RequestId, _state.Events)
            .ConfigureAwait(false);
        // Every end named, no default arm: a new kind of end does not compile until it is decided here (architecture audit).
        return result.End switch
        {
            CallEnd.Answered => result.Value,
            CallEnd.Threw or CallEnd.TimedOut => CandidateSourceStatus.Failed(result.Error ?? "failed"),
            CallEnd.BudgetRanOut or CallEnd.Cancelled => throw result.Exception!, // the node's budget or the caller
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
    /// The client's status. The model's own timeout and an exception the client throws become a failed status, with the
    /// exception's message, as a candidate source's do; only the node's budget running out or the caller cancelling throws
    /// (<see cref="OperationCanceledException"/>).
    /// </returns>
    /// <remarks>
    /// <para>The wait ends at the earlier of the model's timeout and the node's deadline, or when the caller cancels, even
    /// if the client ignores its token. A client still running then is abandoned: it keeps the request memory held until it
    /// completes, so what it writes into <paramref name="outputs"/> afterwards lands in live buffers. Nothing reads them:
    /// the call has failed, and an operator must not read the outputs of a call that did not succeed. The recording has
    /// the call as the request saw it (a timeout or a cancellation, without outputs).</para>
    /// <para>A replay returns what the call returned in the recording, and throws <see cref="OperationCanceledException"/>
    /// where the recorded call was cut off by the budget or the caller. A client exception is a failed status, not a throw,
    /// so a replay never has to recreate an exception type the recording saw (it used to, by reflection over the loaded
    /// assemblies).</para>
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
        // Every end named, no default arm: a new kind of end does not compile until it is decided here (architecture audit).
        return result.End switch
        {
            CallEnd.Answered => result.Value,
            CallEnd.TimedOut or CallEnd.Threw => ModelStatus.Failed(result.Error ?? "failed"),
            CallEnd.BudgetRanOut or CallEnd.Cancelled => Rethrow(result.Exception!), // the budget's or the caller's
        };
    }

    /// <summary>Throws <paramref name="exception"/> with the stack it was thrown with.</summary>
    private static ModelStatus Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Throw(exception);
        return default;
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
            // Whether the operator is thrown cancellation, so a replay throws it too (a failure is a status, not a throw).
            var cutOff = result.End is CallEnd.BudgetRanOut or CallEnd.Cancelled;
            var answered = result.End == CallEnd.Answered;
            if (answered)
            {
                CountGenerated(result.Value, target);
            }

            recorder?.ModelCall(sequence, result.Outcome, result.Error, cutOff, duration, recording!, answered ? result.Value : default);
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
