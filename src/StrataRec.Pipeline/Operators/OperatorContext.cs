using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Replay;
using StrataRec.Pipeline.Sources;
using StrataRec.Core.Services;
using StrataRec.Core.Threading;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// The runtime view one node attempt gets of its request. Row-set operations are available only for the effects the
/// operator declared, so declarations and behavior cannot drift apart.
/// </summary>
/// <remarks>
/// <para>The attempt has an absolute deadline, its time budget, and <see cref="CancellationToken"/> is
/// cancelled at it or with the caller's token. Cancellation is cooperative, so the framework does not rely on it at its
/// I/O boundary: <see cref="CallAsync"/> waits for a service no longer than the deadline (or the call's own timeout) and
/// the caller allow, whether or not the client honours its token. A client still running then is abandoned: it keeps the
/// request memory held until it really completes, and nothing it writes afterwards is read. An attempt whose operator
/// returns only after the deadline has timed out all the same.</para>
/// <para>Custom operators doing their own I/O outside <see cref="CallAsync"/> are responsible for bounding it: by
/// <see cref="CancellationToken"/> when the client honours it, or by waiting no longer than <see cref="BudgetRemaining"/>
/// and keeping every buffer the client may still write alive (through <see cref="RequestMemory.Hold"/>) until it completes.
/// Such I/O is also not recorded or replayed.</para>
/// <para>Nothing may write into <see cref="Table"/> once the attempt has returned: a failed attempt's appended rows are
/// rolled back and reused by the fallback (<see cref="CandidateTable.Rollback"/>), so a late write would land on the
/// fallback's rows. Clients called through this context write into rented buffers, never into the table.</para>
/// </remarks>
public sealed class OperatorContext : IServiceCallScope
{
    private readonly RequestState _state;
    private readonly Effects _effects;
    private readonly ImmutableArray<RequestOutput> _requestOutputs;

    /// <summary>The attempt's staged request values, by position in <see cref="_requestOutputs"/>, missing where none was; made on the first write.</summary>
    private ScalarValue[]? _staged;

    /// <summary>
    /// The node's output slots that held a version when the attempt started (<see cref="Rewrites"/>), each with the buffer
    /// this attempt writes instead, made when it is first asked for. Null for a node that runs for the first time in the
    /// request, which is nearly every attempt.
    /// </summary>
    private Dictionary<int, ColumnBuffer?>? _rewrites;

    internal OperatorContext(RequestState state, string nodeId, Effects effects, AttemptBudget budget, ImmutableArray<RequestOutput> requestOutputs = default)
    {
        _state = state;
        NodeId = nodeId;
        _effects = effects;
        Budget = budget;
        _requestOutputs = requestOutputs.IsDefault ? [] : requestOutputs;
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

    /// <summary>The request's time, in Unix milliseconds: the one every node and every pipeline the request calls sees.</summary>
    public long RequestTimeMs => _state.Context[Planning.PlanSchema.RequestTimeContext].AsInt64();

    /// <summary>Request seed for deterministic randomness.</summary>
    public long Seed => _state.Seed;

    public RequestInputs Inputs => _state.Inputs;

    public EvalContext EvalContext => new(_state.Table, _state.Bindings, _state.Context, _state.Params, _state.Counters);

    /// <summary>Time left before the request deadline.</summary>
    public TimeSpan Remaining => _state.Remaining;

    /// <summary>Health and windowed failure summaries of external calls; null without a logger.</summary>
    internal RequestEventLog? Events => _state.Events;

    /// <summary>
    /// The caller's token, as opposed to <see cref="CancellationToken"/>, which the node budget also cancels: external
    /// calls cut off by the caller are not counted as dependency timeouts.
    /// </summary>
    internal CancellationToken CallerCancellation => _state.CallerCancellation;

    /// <summary>
    /// True while the request replays a recording: it is not traffic, so traffic metrics are not counted.
    /// Public for operators outside this assembly that count metrics of their own (<c>stratarec.model.answer_ids</c>).
    /// </summary>
    public bool Replays => _state.Replay is not null;

    internal string? FailureReason { get; private set; }

    internal string? DegradedReason { get; private set; }

    /// <summary>The committed version of a column (or its fixed slot); null when not produced in this request.</summary>
    public ColumnBuffer? Read(in ColumnRef column) => _state.ReadColumn(column.FixedSlot, column.LogicalId);

    /// <summary>
    /// This node's buffer for an output column. Written values become visible only if the node succeeds. A row source
    /// writes only the rows it appended; after a node has written its own version of the column, the buffer is that
    /// version, so the appended rows get their values in the column readers see (a score for injected rows).
    /// </summary>
    /// <remarks>
    /// Every attempt writes a version of its own. A node that runs again in a request (a fallback node shared by several
    /// nodes) does not get the buffer of what it wrote before: that stays what readers see, this node included, until this
    /// attempt commits, and stays for good when it fails. So a row the attempt does not write is missing, as in any new
    /// version, and never keeps the earlier run's value.
    /// </remarks>
    public ColumnBuffer Output(in OutputColumn column)
    {
        var slot = _state.OutputSlot(_effects, column);
        if (_rewrites is { } rewrites && rewrites.TryGetValue(slot, out var mine))
        {
            return mine ?? (rewrites[slot] = _state.Table.CreateDetached(slot));
        }

        return _state.Table.GetOrCreateSlot(slot);
    }

    /// <summary>
    /// Says that the node's own slot <paramref name="slot"/> holds a version an earlier run of the node committed: the
    /// attempt writes another buffer (<see cref="Output"/>), published by <see cref="PublishRewrites"/> when it commits.
    /// </summary>
    internal void Rewrites(int slot) => (_rewrites ??= new Dictionary<int, ColumnBuffer?>()).Add(slot, null);

    /// <summary>Makes the buffers the attempt wrote instead of its earlier versions the slots' own: the attempt commits.</summary>
    internal void PublishRewrites()
    {
        if (_rewrites is null)
        {
            return;
        }

        foreach (var (slot, buffer) in _rewrites)
        {
            if (buffer is not null)
            {
                _state.Table.ReplaceSlot(slot, buffer);
            }
        }
    }

    public ScalarValue Param(in ParamRef parameter) => _state.Params[parameter.Id];

    /// <summary>
    /// A request-level value by id: a <c>ctx.*</c> field, or a request column (<c>req.*</c>) as the nodes before this one
    /// left it. A value this node stages with <see cref="SetRequestValue"/> is not there until the node commits.
    /// </summary>
    public ScalarValue ContextValue(int contextId) => _state.Context[contextId];

    /// <summary>
    /// Stages the value of a request column the node writes (<see cref="OperatorCompileContext.TryWriteRequestColumn"/>);
    /// the last value staged is the one written. When the node commits, the value is stored as the column's type holds it,
    /// as a column's are: a float32 column's rounded, NaN/±∞ and an integer outside an int32 column's range replaced by the
    /// column's default or missing (and counted); a non-nullable column left missing fails the node. When the attempt
    /// fails, nothing it staged is written. Requires <see cref="Effects.RequestWriter"/>.
    /// </summary>
    /// <param name="value">A value of the column's type, held promoted as every <see cref="ScalarValue"/> is (an integer for a
    /// float column is a float); missing to leave the column missing.</param>
    /// <exception cref="InvalidOperationException">The node did not resolve <paramref name="output"/> when it compiled.</exception>
    /// <exception cref="ArgumentException">The value is of another kind than the column (a string for an int64 column), or
    /// a list with a null element.</exception>
    public void SetRequestValue(in RequestOutput output, in ScalarValue value)
    {
        Require(Effects.RequestWriter, nameof(SetRequestValue));
        if ((uint)output.Index >= (uint)_requestOutputs.Length || _requestOutputs[output.Index].ContextId != output.ContextId)
        {
            throw new InvalidOperationException(
                $"Node '{NodeId}' set request column '{output.Name}', which it did not resolve with TryWriteRequestColumn when it compiled.");
        }

        var resolved = _requestOutputs[output.Index]; // as it compiled, whatever the handle says
        if (!value.IsMissing && !OfKind(value, resolved.Type.Promoted()))
        {
            throw new ArgumentException(
                $"Request column '{resolved.Name}' is {resolved.Type}, and node '{NodeId}' set it to a {Kind(value)} value ({value}).", nameof(value));
        }

        if (value.ListElement == StorageKind.String && value.AsStringList().IndexOf((string)null!) is >= 0 and var element)
        {
            throw new ArgumentException($"Node '{NodeId}' set request column '{resolved.Name}' to a list whose element {element} is null.", nameof(value));
        }

        (_staged ??= new ScalarValue[_requestOutputs.Length])[output.Index] = value;
    }

    /// <summary>Replaces a staged value with what the commit stores (<see cref="PipelineExecutor"/>); missing needs no storage.</summary>
    internal void RestageRequestValue(int position, in ScalarValue value)
    {
        if (_staged is not null)
        {
            _staged[position] = value;
        }
    }

    /// <summary>The value staged for the node's <paramref name="position"/>-th request output; missing when none was.</summary>
    internal ScalarValue StagedRequestValue(int position) => _staged is { } staged ? staged[position] : ScalarValue.Missing;

    /// <summary>Whether a present value is of <paramref name="promoted"/>'s kind; an integer is a float's (it converts exactly as stored).</summary>
    private static bool OfKind(in ScalarValue value, ColumnType promoted)
    {
        var (want, have) = promoted.IsList
            ? (value.Kind == StorageKind.List ? (promoted.Element, value.ListElement) : (StorageKind.List, value.Kind))
            : (promoted.Kind, value.Kind);
        return have == want || (want == StorageKind.Float64 && have == StorageKind.Int64);
    }

    private static string Kind(in ScalarValue value) =>
        value.Kind == StorageKind.List ? $"list<{value.ListElement.ToString().ToLowerInvariant()}>" : value.Kind.ToString().ToLowerInvariant();

    /// <summary>
    /// Waits until the context features of each of <paramref name="reads"/> are settled, and calls
    /// <paramref name="settled"/> for each as soon as it is, with its index and the worst state of its features: once every
    /// one is stored, or at once with <see cref="FeatureState.Failed"/> when one failed, since the operator gives up on what
    /// reads it whatever the others do. Then the awaited expressions they come from
    /// (<see cref="OperatorCompileContext.CompileAwaitedExpression"/>) can be evaluated (<see cref="EvalContext"/>); a failed
    /// feature is the operator's to handle, as it handles a failed call: its value is missing.
    /// </summary>
    /// <remarks>
    /// <para>For an operator sending several calls (a recall source's query), each as soon as its own features
    /// are stored; a call with no awaited reads is settled at once. The features were prefetched when the request started, so
    /// only what is still in flight is waited for.</para>
    /// <para>Each round observes the prefetch once, then settles, in the order of <paramref name="reads"/>, every read that
    /// this one observation settles, so reads whose features are stored together are settled in that order whenever the
    /// store happens; then it waits for any feature it observed in flight, so one stored meanwhile starts the next round at
    /// once (each read observed the tasks on its own, so a store between two reads put them in different rounds,
    /// and one between the check and the wait was missed until another feature was stored).</para>
    /// <para><paramref name="settled"/> runs on the operator's flow, one call at a time; what it throws ends the wait.</para>
    /// <para>The wait is bounded on its own by the attempt's deadline and the caller, as a call is
    /// (<see cref="CallAsync"/>), not by <see cref="CancellationToken"/>, which a client's callback could hold up; and a
    /// prefetch that faulted is rethrown as it is, not taken for the deadline. The bound is also checked before each round
    /// and before each read is judged (an empty one's callback included), so what is stored after it passed is never
    /// settled, and a fault is not reported for a deadline that came first, however long a callback ran. Each read of
    /// <paramref name="reads"/> is asked for once, in order, with the bound checked before: none of the operator's code (its
    /// list, its callback) is called once a check saw the bound pass. An operator that stopped waiting reads nothing more of
    /// the request's.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">The attempt's budget ran out or the caller gave up first: the reads not
    /// settled by then never are, and the operator stops, as when a call it sent is cut off.</exception>
    public async ValueTask WhenEachReadyAsync(IReadOnlyList<AwaitedReads> reads, Action<int, FeatureFillResult> settled)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(settled);

        // One pass over the operator's list, which reads each read once and never again: one with nothing to wait for is
        // settled as it is read, the others are kept with their features (no copy of the list: none for an operator that
        // waits for nothing). The bound is checked before each call into the operator's code, its list's Count and indexer
        // as its callback (Count too): indexed in each round, a slow list let a callback run after the bound passed and a faulted
        // prefetch stand for it; read up front without a check, the rest of the list was read after it.
        List<(int Index, ImmutableArray<int> Features)>? waiting = null;
        ThrowIfBoundPassed();
        var count = reads.Count;
        for (var i = 0; i < count; i++)
        {
            ThrowIfBoundPassed();
            var read = (reads[i] ?? throw new ArgumentException($"reads[{i}] is null.", nameof(reads))).Features;
            if (read.IsEmpty)
            {
                ThrowIfBoundPassed(); // the indexer may have run past it
                settled(i, new FeatureFillResult(FeatureState.Ok));
            }
            else
            {
                (waiting ??= new List<(int, ImmutableArray<int>)>(count - i)).Add((i, read));
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
            foreach (var (_, read) in waiting)
            {
                foreach (var f in read)
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
                var (i, read) = waiting[w];
                // Before the read's state is read: a faulted prefetch rethrown there must not stand for a deadline or a
                // caller that came first.
                ThrowIfBoundPassed();
                if (Settled(features, read, tasks, stored, out var result))
                {
                    ThrowIfBoundPassed(); // and before its callback: judging a read of many features takes time too
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
    /// wait on a completed task never times out.
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

    /// <summary>
    /// Appends rows. Requires <see cref="Effects.RowSource"/>. A key is appended only while it is not live: skip the keys
    /// <see cref="CandidateTableView.FindRow"/> finds at a live row, or every key the request has seen
    /// (<see cref="CandidateTableView.ContainsKey"/>), as the built-in row sources do.
    /// </summary>
    public int AppendRows(ReadOnlySpan<long> keys)
    {
        Require(Effects.RowSource, nameof(AppendRows));
        return _state.Table.AppendRows(keys);
    }

    /// <summary>Appends rows with string keys. Requires <see cref="Effects.RowSource"/>. A key is appended only while it is not live (see the int64 overload).</summary>
    public int AppendRows(IReadOnlyList<string> keys)
    {
        Require(Effects.RowSource, nameof(AppendRows));
        return _state.Table.AppendRows(keys);
    }

    /// <summary>
    /// Calls another pipeline for this request, with the handle the node declared
    /// (<see cref="OperatorCompileContext.TryCallPipeline"/>), and returns its result: the rows its output returns with
    /// their columns, and its output values. What the operator does with them is its own: append them as candidates
    /// (<see cref="AppendRows(ReadOnlySpan{long})"/>), order rows by them, write columns.
    /// </summary>
    /// <param name="inputs">
    /// The inputs the operator passes, each declared with <see cref="OperatorCompileContext.TryCallInput"/>. Every other
    /// input the pipeline declares gets this request's input of the same name, when it has one.
    /// </param>
    /// <param name="timeout">
    /// The called run's deadline, when less than what is left of the node's budget: for a call the node can do without
    /// (a page's sponsored rows), so that a slow one ends while the node still has time to go on. It is the run's
    /// deadline as a request's is, not a time after which its answer is thrown away: a pipeline that serves what it has
    /// when a node of its own times out (<c>on_failure: skip</c>) answers so here too.
    /// </param>
    /// <returns>
    /// The result. A call that cannot be answered is a failed result, never an exception: the pipeline is not served, does
    /// not fit the call (an input, a column or the key type the node declared), rejects the inputs, fails, or no time is
    /// left. The operator decides what that means for its node (<see cref="Fail"/>, <see cref="ReportDegraded"/>, nothing).
    /// </returns>
    /// <remarks>
    /// <para>The called run is the same request: its id, its time, and its experiment assignment, of which the pipeline
    /// runs the arms it has; its seed is derived from this run's, the node and the call's place among the calls the node
    /// made in the run, so calls made in the same order get the same seeds. It has what is left of the node's budget, and does not fail
    /// over to its <c>fallback_pipeline</c>: the node's <c>on_failure</c> decides.</para>
    /// <para>An operator may make several calls at once, as it may call several services.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The handle is another node's, or an input was not declared.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the request.</exception>
    public ValueTask<PipelineCallResult> CallPipelineAsync(PipelineCall call, RequestInputs? inputs = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.NodeId != NodeId || call.Caller != _state.Plan.PipelineId)
        {
            throw new ArgumentException($"Node '{NodeId}' called pipeline '{call.PipelineId}' with a handle node '{call.NodeId}' of '{call.Caller}' declared.", nameof(call));
        }

        if (inputs is not null)
        {
            foreach (var (name, _) in inputs)
            {
                if (!call.PassesInput(name))
                {
                    throw new ArgumentException(
                        $"Node '{NodeId}' passes input '{name}' to pipeline '{call.PipelineId}' without having declared it (OperatorCompileContext.TryCallInput).",
                        nameof(inputs));
                }
            }
        }

        if (_state.CallerCancellation.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<PipelineCallResult>(_state.CallerCancellation); // the caller's cancellation is never a failed call
        }

        var sequence = _state.NextPipelineCall(NodeId);
        if (_state.Replay is { } replay)
        {
            return new(Heard(replay.PipelineCall(NodeId, sequence, call.PipelineId, inputs))); // answered from the recording: a replay runs no other pipeline, and reads no cache
        }

        if (_state.Recorder is { } recorder)
        {
            return RecordedCallAsync(recorder, call, inputs, sequence, timeout);
        }

        return HeardAsync(call, inputs, sequence, timeout);
    }

    private async ValueTask<PipelineCallResult> HeardAsync(PipelineCall call, RequestInputs? inputs, int sequence, TimeSpan? timeout)
    {
        try
        {
            return Heard(await SendAsync(call, inputs, sequence, timeout).ConfigureAwait(false));
        }
        catch
        {
            _state.CallsUnanswered = true; // an operator may catch what the call threw and go on, as after a failed result
            throw;
        }
    }

    /// <summary>
    /// A result as the node gets it: one its cache served past its <c>ttl_ms</c> because the pipeline failed, or one of a
    /// run that was itself degraded, degrades the node, as a service's stale or partial answer does, whatever the
    /// operator does with it. The request's result is then degraded too, and is not kept by a cache of a call of it.
    /// A call that was not answered, for whatever reason and wherever that was decided, is the run's
    /// (<see cref="PipelineResult.CallsUnanswered"/>): every call's result comes to its node through here.
    /// </summary>
    private PipelineCallResult Heard(PipelineCallResult result)
    {
        if (!result.Succeeded || result.CallsUnanswered)
        {
            _state.CallsUnanswered = true;
        }

        if (result.Cached is { Stale: true } from)
        {
            ReportDegraded($"pipeline '{result.PipelineId}' failed, and its cached result of request '{from.RequestId}', past its ttl_ms, was served");
        }
        else if (result.Degraded)
        {
            ReportDegraded($"pipeline '{result.PipelineId}' returned a degraded result");
        }

        return result;
    }

    private async ValueTask<PipelineCallResult> RecordedCallAsync(
        Replay.RunRecorder recorder, PipelineCall call, RequestInputs? inputs, int sequence, TimeSpan? timeout)
    {
        // What was passed, as it is now: the operator may change its inputs for its next call while this one runs.
        var passed = Replay.ReplayJson.Inputs(inputs);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await HeardAsync(call, inputs, sequence, timeout).ConfigureAwait(false);
        recorder.PipelineCall(NodeId, sequence, call.PipelineId, passed, result, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    private ValueTask<PipelineCallResult> SendAsync(PipelineCall call, RequestInputs? inputs, int sequence, TimeSpan? timeout)
    {
        var budget = BudgetRemaining;
        if (timeout is { } most && most < budget)
        {
            budget = most;
        }

        if (budget <= TimeSpan.Zero)
        {
            return new(PipelineCallResult.Failed(call.PipelineId, $"pipeline '{call.PipelineId}' was not called: nothing is left of the node's budget"));
        }

        if (_state.Calls is not { } calls)
        {
            return new(PipelineCallResult.Failed(call.PipelineId, $"pipeline '{call.PipelineId}' was not called: this run is not an engine's, which serves the pipelines a node calls"));
        }

        return calls.CallAsync(new PipelineCallRequest(call, inputs, _state.Inputs, sequence, _state.Seed, RequestTimeMs, budget, _state.Memory), _state.CallerCancellation);
    }

    /// <summary>
    /// Calls a service for this node the way the framework calls every external dependency (through
    /// <see cref="BoundedCall"/>): holds the request memory until the client completes, waits for it no
    /// longer than the binding's <c>timeout_ms</c> and the node's budget allow, records duration, errors, health and a span
    /// through the service's <see cref="BoundService.Target"/>, counts the tokens a generative client
    /// reports, closes <paramref name="response"/>, and records the call or answers it from a recording when the request
    /// records or replays. Custom operators that call services through it are bounded, measured, recorded
    /// and replayed like the built-in ones.
    /// </summary>
    /// <param name="call">
    /// The call. One about rows gives the rows' <see cref="ServiceCall.Items"/> (<see cref="KeysOf"/>): the recording keys
    /// each item's outputs by its entity and inputs, so a replay answers an item however the items were batched. Its
    /// <see cref="ServiceCall.Outputs"/> name what the operator reads: only those are recorded and replayed.
    /// </param>
    /// <param name="response">Created for this call, with the call's <see cref="ServiceCall.Limit"/>; read it only after the call succeeded.</param>
    /// <returns>
    /// The client's status. The service's own timeout and an exception the client throws become a failed status, with the
    /// exception's message; only the node's budget running out or the caller cancelling throws
    /// (<see cref="OperationCanceledException"/>).
    /// </returns>
    /// <remarks>
    /// <para>The wait ends at the earlier of the service's timeout and the node's deadline, or when the caller cancels,
    /// even if the client ignores its token. A client still running then is abandoned: it keeps the request memory held
    /// until it completes, and <paramref name="response"/> reads as empty from then on, so neither the operator nor the
    /// recording reads a buffer the client may still be writing. The recording has the call as the request saw it (a
    /// timeout or a cancellation, without outputs).</para>
    /// <para>A replay returns what the call returned in the recording, and throws <see cref="OperationCanceledException"/>
    /// where the recorded call was cut off by the budget or the caller. A client exception is a failed status, not a throw,
    /// so a replay never has to recreate an exception type the recording saw.</para>
    /// </remarks>
    public Task<ServiceStatus> CallAsync(BoundService service, ServiceCall call, ServiceResponse response)
    {
        // Not an async method: the shared path's task is the call's (awaiting it here cost a second
        // task per call). A wrong call is still a faulted task, and a cancellation (an operator's output names may be a
        // memory of its own) a cancelled one, as an async method's would be.
        try
        {
            Check(service, call, response);
        }
        catch (OperationCanceledException ex)
        {
            // An async method's builder: the task is cancelled and keeps the exception thrown.
            var cancelled = AsyncTaskMethodBuilder<ServiceStatus>.Create();
            cancelled.SetException(ex);
            return cancelled.Task;
        }
        catch (Exception ex)
        {
            return Task.FromException<ServiceStatus>(ex);
        }

        return ServiceCallPath.CallAsync(this, service, call, response);
    }

    private void Check(BoundService service, ServiceCall call, ServiceResponse response)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(response);
        if (call.Limit != response.Limit)
        {
            throw new ArgumentException($"The call asks for {call.Limit} item(s) and the response keeps {response.Limit}: create the response with the call's limit.", nameof(response));
        }

        if (!string.Equals(call.Service, service.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The call names service '{call.Service}', but it is sent to the binding '{service.Name}': name the binding.", nameof(call));
        }

        CheckOutputs(call);
        CheckInputs(call);
        if (!call.Items.IsEmpty)
        {
            CheckItems(call);
        }
    }

    /// <summary>Each output is named once: the recording, the replay's choice of a recording and the cache count outputs by name.</summary>
    private static void CheckOutputs(ServiceCall call)
    {
        var outputs = call.Outputs.Span;
        for (var i = 1; i < outputs.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (string.Equals(outputs[i], outputs[j], StringComparison.Ordinal))
                {
                    throw new ArgumentException($"The call names output '{outputs[i]}' twice: name each output it reads once.", nameof(call));
                }
            }
        }
    }

    /// <summary>
    /// Each input is named once: a service reads an input by its name (<see cref="ServiceCall.TryGetInput"/> finds the
    /// first), and a replay's digests and a cache's keys take every input in name order, so a second of a name is one the
    /// service never reads that the key does, and two orders of the two were one key. By the names'
    /// hashes, sorted with the inputs' indices: only names of one hash are compared (one collision
    /// compared every pair of names), and nothing is allocated for up to 256 inputs.
    /// </summary>
    internal static void CheckInputs(ServiceCall call)
    {
        var inputs = call.Inputs.Span;
        if (inputs.Length < 2)
        {
            return;
        }

        // The hash in the high half, the index in the low: sorted, the inputs of one hash are adjacent.
        Span<long> keyed = inputs.Length <= 256 ? stackalloc long[inputs.Length] : new long[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            keyed[i] = ((long)string.GetHashCode(inputs[i].Name, StringComparison.Ordinal) << 32) | (uint)i;
        }

        keyed.Sort();
        for (var start = 0; start < keyed.Length;)
        {
            var end = start + 1;
            while (end < keyed.Length && keyed[end] >> 32 == keyed[start] >> 32)
            {
                end++;
            }

            for (var a = start + 1; a < end; a++)
            {
                for (var b = start; b < a; b++)
                {
                    var name = inputs[(int)(uint)keyed[a]].Name;
                    if (string.Equals(name, inputs[(int)(uint)keyed[b]].Name, StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"The call names input '{name}' twice: name each input once, as the service reads it by its name.", "call");
                    }
                }
            }

            start = end;
        }
    }

    /// <summary>
    /// The shape of a call about items (<see cref="ServiceCall.Items"/>): one item per row, no items added, and each input
    /// either per item (its leading dimension the batch size) or shared (leading dimension 1, a scalar or a text). A shared
    /// input of another leading dimension would be split by row where the batch size happened to match it: a replay's
    /// digests and a cache's call of the items it misses split inputs on this rule.
    /// </summary>
    private static void CheckItems(ServiceCall call)
    {
        if (call.Limit != 0)
        {
            throw new ArgumentException("A call about items adds none: its outputs are per item. Send items or ask for a limit, not both.", nameof(call));
        }

        if (call.Items.Length != call.BatchSize)
        {
            throw new ArgumentException($"A call about items has one per row: {call.Items.Length} item(s) for a batch of {call.BatchSize}.", nameof(call));
        }

        // A missing input too: it keeps its declared shape, which says whether it is per item (missing on every row, cut to
        // the rows a cache sends) or shared.
        foreach (var input in call.Inputs.Span)
        {
            if (input.Shape is [var leading, ..] && leading != call.BatchSize && leading != 1)
            {
                throw new ArgumentException(
                    $"Input '{input.Name}' has shape [{string.Join(", ", input.Shape.ToArray())}] in a call about {call.BatchSize} item(s): an input is per item "
                    + "(its leading dimension the batch size) or shared, with leading dimension 1 (send a shared vector as [1, d]).", nameof(call));
            }
        }
    }

    /// <summary>
    /// The entity of each of <paramref name="rows"/> (physical rows of the table), for a call's
    /// <see cref="ServiceCall.Items"/>: in buffers rented from the request, valid until it ends.
    /// </summary>
    public ReadOnlyMemory<EntityKey> KeysOf(ReadOnlySpan<int> rows)
    {
        if (rows.IsEmpty)
        {
            return ReadOnlyMemory<EntityKey>.Empty;
        }

        var keys = _state.Memory.Rent<EntityKey>(rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            keys[i] = _state.Table.GetKey(rows[i]);
        }

        return keys.AsMemory(0, rows.Length);
    }

    /// <summary>The request's recorder, when it records.</summary>
    internal RunRecorder? Recorder => _state.Recorder;

    ServiceCaller IServiceCallScope.Caller => ServiceCaller.OfNode(NodeId);

    CallBound IServiceCallScope.Budget => Budget;

    RunReplay? IServiceCallScope.Replay => _state.Replay;

    RequestReads? IServiceCallScope.Reads => _state.Reads;

    RunRecorder? IServiceCallScope.Recorder => _state.Recorder;

    /// <summary>A node's pending call holds the request memory until it completes; nothing else waits for it.</summary>
    void IServiceCallScope.Pending(Task done)
    {
    }

    /// <summary>Marks the result as Degraded (completed with partial or stale inputs).</summary>
    public void ReportDegraded(string reason) => DegradedReason ??= reason;

    /// <summary>Fails the node without throwing; the engine applies <c>on_failure</c>.</summary>
    public void Fail(string reason) => FailureReason ??= reason;

    private void Require(Effects effect, string operation)
    {
        if ((_effects & effect) == 0)
        {
            throw new InvalidOperationException(
                $"Node '{NodeId}' called {operation}, which needs the {effect} effect, but its operator does not declare it.");
        }
    }
}
