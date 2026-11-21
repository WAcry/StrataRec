using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
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
/// operator declared (design 5.2), so declarations and behavior cannot drift apart.
/// </summary>
/// <remarks>
/// <para>The attempt has an absolute deadline, its time budget (design 5.6), and <see cref="CancellationToken"/> is
/// cancelled at it or with the caller's token. Cancellation is cooperative, so the framework does not rely on it at its
/// I/O boundary: <see cref="CallAsync"/> waits for a service no longer than the deadline (or the call's own timeout) and
/// the caller allow, whether or not the client honours its token. A client still running then is abandoned: it keeps the
/// request memory held until it really completes, and nothing it writes afterwards is read. An attempt whose operator
/// returns only after the deadline has timed out all the same.</para>
/// <para>Custom operators doing their own I/O outside <see cref="CallAsync"/> are responsible for bounding it: by
/// <see cref="CancellationToken"/> when the client honours it, or by waiting no longer than <see cref="BudgetRemaining"/>
/// and keeping every buffer the client may still write alive (through <see cref="RequestMemory.Hold"/>) until it completes.
/// Such I/O is also not recorded or replayed (design 13.4).</para>
/// <para>Nothing may write into <see cref="Table"/> once the attempt has returned: a failed attempt's appended rows are
/// rolled back and reused by the fallback (<see cref="CandidateTable.Rollback"/>), so a late write would land on the
/// fallback's rows. Clients called through this context write into rented buffers, never into the table.</para>
/// </remarks>
public sealed class OperatorContext : IServiceCallScope
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
    public ColumnBuffer? Read(in ColumnRef column) => _state.ReadColumn(column.FixedSlot, column.LogicalId);

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
        // as its callback (review 95: Count too): indexed in each round, a slow list let a callback run after the bound passed (review 89) and a faulted
        // prefetch stand for it (91); read up front without a check, the rest of the list was read after it (93).
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
                // caller that came first (review 86).
                ThrowIfBoundPassed();
                if (Settled(features, read, tasks, stored, out var result))
                {
                    ThrowIfBoundPassed(); // and before its callback: judging a read of many features takes time too (review 93)
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
    /// Calls a service for this node the way the framework calls every external dependency (through
    /// <see cref="BoundedCall"/>): holds the request memory until the client completes (design 4.8), waits for it no
    /// longer than the binding's <c>timeout_ms</c> and the node's budget allow, records duration, errors, health and a span
    /// through the service's <see cref="BoundService.Target"/> (design 13.1, 13.2), counts the tokens a generative client
    /// reports, closes <paramref name="response"/>, and records the call or answers it from a recording when the request
    /// records or replays (design 13.4). Custom operators that call services through it are bounded, measured, recorded
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
        // Not an async method: the shared path's task is the call's (review of IO step 5: awaiting it here cost a second
        // task per call). A wrong call is still a faulted task, and a cancellation (an operator's output names may be a
        // memory of its own) a cancelled one, as an async method's would be.
        try
        {
            Check(service, call, response);
        }
        catch (OperationCanceledException ex)
        {
            // An async method's builder: the task is cancelled and keeps the exception thrown (review of da5cfe6).
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
    /// service never reads that the key does, and two orders of the two were one key (review of 3a0d9e8). Hashes first, so
    /// a call of many inputs compares few names, and nothing is allocated for up to 256.
    /// </summary>
    private static void CheckInputs(ServiceCall call)
    {
        var inputs = call.Inputs.Span;
        if (inputs.Length < 2)
        {
            return;
        }

        Span<int> hashes = inputs.Length <= 256 ? stackalloc int[inputs.Length] : new int[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            hashes[i] = string.GetHashCode(inputs[i].Name, StringComparison.Ordinal);
        }

        hashes.Sort();
        for (var i = 1; i < hashes.Length; i++)
        {
            if (hashes[i] == hashes[i - 1])
            {
                ThrowIfNamedTwice(inputs);
                return; // a hash collision of two names
            }
        }

        static void ThrowIfNamedTwice(ReadOnlySpan<NamedInput> inputs)
        {
            for (var i = 1; i < inputs.Length; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (string.Equals(inputs[i].Name, inputs[j].Name, StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"The call names input '{inputs[i].Name}' twice: name each input once, as the service reads it by its name.", "call");
                    }
                }
            }
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
        // the rows a cache sends) or shared (review of IO step 3).
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

    RunRecorder? IServiceCallScope.Recorder => _state.Recorder;

    /// <summary>A node's pending call holds the request memory until it completes; nothing else waits for it.</summary>
    void IServiceCallScope.Pending(Task done)
    {
    }

    /// <summary>Marks the result as Degraded (completed with partial or stale inputs, design 5.6).</summary>
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
