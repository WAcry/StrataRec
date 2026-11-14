using System.Diagnostics;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Threading;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>Per-request availability of a feature (design 6.7).</summary>
public enum FeatureState : byte
{
    Unrequested = 0,
    Ok,

    /// <summary>Usable but partially failed or stale; readers report Degraded.</summary>
    Degraded,

    /// <summary>The source failed; nodes reading the feature fail.</summary>
    Failed,
}

/// <summary>Outcome of making a set of features available.</summary>
public readonly record struct FeatureFillResult(FeatureState State, string? Error = null)
{
    public bool Failed => State == FeatureState.Failed;
}

/// <summary>
/// Request-scoped feature enrichment (design 4.5, 5.4, 6.5–6.8). Fetch results land in key-ordered staging buffers and
/// are backfilled into whatever rows are live <em>when a reader needs them</em>, so filters and Compact during in-flight
/// I/O never misplace values. Local groups (design 6.6) are read synchronously from their tables when a reader needs
/// them (<c>FeatureFetcher.Local.cs</c>); cache groups the same way, with the keys the table does not have fresh resolved
/// through the distributed tier and the connector (<c>FeatureFetcher.Cache.cs</c>).
/// </summary>
/// <remarks>
/// <para>Not thread-safe; driven by the request's executor. Connectors and the context prefetch run concurrently
/// underneath.</para>
/// <para>Memory (design 4.8): every connector call holds the request's <see cref="RequestMemory"/> until the connector
/// really completes, even when the request stopped waiting for it at the source timeout or deadline; the background
/// context fetch holds it until its last staging read and derived computation. Buffers therefore never return to the
/// pool while anything may still write or read them.</para>
/// <para>Allocation (design 4.8, 15): a fetch is split into <c>max_batch</c> chunks, often hundreds per request, so a
/// chunk whose connector answers synchronously allocates nothing: its keys are a struct, its staging is a window of the
/// fetch's <see cref="StagingArea"/> seen through a reused view, its cancellation is the fetch's, and its status comes
/// back in a <see cref="ValueTask{TResult}"/>. Only a call that really goes asynchronous allocates what waiting needs.</para>
/// </remarks>
public sealed partial class FeatureFetcher
{
    private static readonly FetchStatus NoTimeLeft = FetchStatus.Failed("no time left before the request deadline");

    private readonly FeaturePlan _plan;
    private readonly CandidateTable _table;
    private readonly ScalarValue[] _context;
    private readonly OutputBindings _bindings;
    private readonly ScalarValue[] _params;
    private readonly RequestMemory _memory;
    private readonly long _deadlineTimestamp;
    private readonly string? _requestId;
    private readonly RequestEventLog? _events;
    private readonly FeatureState[] _state;
    private readonly string?[] _errors;
    private readonly List<PendingFetch> _pending = new();
    private readonly List<Task> _io = new(); // guarded by lock (_io): connector completions and the context fetch
    private readonly ExpressionCounters _counters;
    private readonly IFeatureCallHook? _hook;
    private Task? _contextTask;
    private int _nonFiniteReplaced;

    /// <param name="counters">The request's expression counters, shared with the operators' evaluations so a request
    /// reports one total per event; null gives the fetcher counters of its own.</param>
    /// <param name="requestId">Sample request id for failure summaries (design 13.2).</param>
    /// <param name="events">Source health and failure summaries; null when the engine has no logger.</param>
    /// <param name="hook">Records every connector call, or answers them from a recording (design 13.4); null for ordinary requests.</param>
    public FeatureFetcher(
        FeaturePlan plan,
        CandidateTable table,
        ScalarValue[] context,
        OutputBindings bindings,
        ScalarValue[] parameters,
        RequestMemory memory,
        long deadlineTimestamp,
        ExpressionCounters? counters = null,
        string? requestId = null,
        RequestEventLog? events = null,
        IFeatureCallHook? hook = null)
    {
        _plan = plan;
        _table = table;
        _context = context;
        _bindings = bindings;
        _params = parameters;
        _memory = memory;
        _deadlineTimestamp = deadlineTimestamp;
        _counters = counters ?? new ExpressionCounters();
        _requestId = requestId;
        _events = events;
        _hook = hook;
        _state = new FeatureState[plan.Features.Count];
        _errors = new string?[plan.Features.Count];
        if (plan.HasTables && hook is not { Replays: true })
        {
            _localKeys = new EntityKeyIndex(EntityKeyType.Int64, 0, memory); // reset by each local read, so no read costs an object
        }
    }

    /// <summary>
    /// Number of feature values (physical or derived, candidate or context) that were NaN/±∞ and were replaced by the
    /// feature's default or marked missing (design 4.3, metric <c>column_nonfinite_total</c>).
    /// </summary>
    public int NonFiniteReplaced => Volatile.Read(ref _nonFiniteReplaced);

    /// <summary>
    /// Counters of derived-feature evaluations. <see cref="ExpressionCounters.OutOfRange"/> includes derived values
    /// (candidate or context) that did not fit an <c>int32</c> or <c>list&lt;int32&gt;</c> feature and were replaced by its
    /// default or marked missing (design 4.3, metric <c>column_out_of_range_total</c>).
    /// </summary>
    public ExpressionCounters Counters => _counters;

    public FeatureState GetState(int feature) => _state[feature];

    /// <summary>
    /// Completes when every external operation this request started has really finished: each connector call (also one
    /// whose result was rejected because it arrived after its time budget) and the whole background context fetch.
    /// Each releases its hold on the request memory just before it counts as finished.
    /// </summary>
    public Task WhenIoCompleteAsync()
    {
        lock (_io)
        {
            return Task.WhenAll(_io.ToArray());
        }
    }

    /// <summary>
    /// Starts fetching context features (and computes context-derived ones) concurrently with recall (design 6.8).
    /// </summary>
    public void StartContextFetch(IEnumerable<int> features, CancellationToken cancellationToken)
    {
        var closure = Closure(features, out var member);
        if (!AnyContext(closure))
        {
            _contextTask = Task.CompletedTask;
            return;
        }

        // The operation reads its staging buffers and evaluates derived features after its connectors complete, which
        // may be after the request has returned (a failed request, or one that never reads context features). Holding
        // the memory for the whole operation, not just for each connector call, keeps those reads off returned buffers.
        _memory.Hold();
        _contextTask = FetchContextAsync(closure, member, cancellationToken);
        TrackIo(_contextTask);
    }

    /// <summary>
    /// Starts wave-0 fetches for features the next segment needs (called after each fetch barrier, design 5.4). Only
    /// the listed features (and their dependencies) are fetched, not every member of their groups (design 6.8).
    /// </summary>
    public void Prefetch(IEnumerable<int> features, CancellationToken cancellationToken)
    {
        var closure = Closure(features, out var member);
        var groups = GroupsOf(closure, wave: 0);
        for (var i = 0; i < groups.Count; i++)
        {
            // A group keyed by a feature that is not available yet (e.g. a derived key computed by EnsureAsync) would
            // find no keys now; EnsureAsync fetches it once the key exists.
            var group = _plan.Groups[groups.Items[i]];
            if (!Prefetches(group))
            {
                continue;
            }

            if (group.KeyFeatureIndex >= 0 && !_table.IsFilledForLiveRows(_plan.Features[group.KeyFeatureIndex].Slot))
            {
                continue;
            }

            if (FeaturesToFetch(group, member, skipInFlight: true) is { } fetch)
            {
                _pending.Add(Start(group, fetch, cancellationToken));
            }
        }
    }

    /// <summary>
    /// Ensures every listed feature is filled for every live row (design 5.4, correctness rule). Works through the
    /// dependency graph wave by wave (design 6.8): derived features needing no fetch first; then, per wave, in-flight
    /// fetches, fetches for whatever is still missing, and the derived features whose inputs are now complete — so a
    /// derived foreign key exists before the groups keyed by it are fetched.
    /// </summary>
    public async ValueTask<FeatureFillResult> EnsureAsync(IReadOnlyCollection<int> features, CancellationToken cancellationToken)
    {
        if (features.Count == 0)
        {
            return new FeatureFillResult(FeatureState.Ok);
        }

        var closure = Closure(features, out var member);
        if (_contextTask is not null && AnyContext(closure))
        {
            await _contextTask.ConfigureAwait(false);
        }

        // member marks the whole closure, context features included: every lookup below is of a candidate feature.
        ComputeDerivedOfWave(-1, member);
        for (var wave = 0; wave <= _plan.MaxWave; wave++)
        {
            // In-flight (prefetched) fetches carrying a needed feature first; their keys may cover most rows.
            for (var i = 0; i < _pending.Count; i++)
            {
                var pending = _pending[i];
                if (pending.Group.Wave == wave && Intersects(pending.Features, member))
                {
                    _pending.RemoveAt(i--);
                    await CompleteAsync(pending).ConfigureAwait(false);
                }
            }

            // Then anything still unfilled (rows appended since, fields not prefetched, or never prefetched): fetches
            // first, then the local and cache reads, which run while the fetches are in flight.
            var groups = GroupsOf(closure, wave);
            PendingFetch[]? started = null;
            var startedCount = 0;
            var locals = false;
            for (var g = 0; g < groups.Count; g++)
            {
                var group = _plan.Groups[groups.Items[g]];
                if (ReadsLocally(group))
                {
                    locals = true;
                }
                else if (FeaturesToFetch(group, member, skipInFlight: false) is { } fetch && !FailIfKeyFailed(group, fetch))
                {
                    started ??= _memory.Rent<PendingFetch>(groups.Count);
                    started[startedCount++] = Start(group, fetch, cancellationToken);
                }
            }

            if (locals)
            {
                ReadLocalGroups(groups, member, cancellationToken);
            }

            for (var s = 0; s < startedCount; s++)
            {
                await CompleteAsync(started![s]).ConfigureAwait(false);
            }

            // The cache reads that missed resolve their misses meanwhile (the distributed tier, then the connector).
            while (TakeCacheRead() is { } read)
            {
                await CompleteCacheReadAsync(read).ConfigureAwait(false);
            }

            ComputeDerivedOfWave(wave, member);
        }

        var worst = FeatureState.Ok;
        string? error = null;
        for (var i = 0; i < closure.Count; i++)
        {
            var f = closure.Items[i];
            if (_state[f] > worst)
            {
                worst = _state[f];
                error = _errors[f];
            }
        }

        return new FeatureFillResult(worst, error);
    }

    /// <summary>
    /// The features plus everything they depend on, in dependency order (<see cref="FeaturePlan.Closure(IEnumerable{int})"/>),
    /// in arrays rented from the request memory: an ensure runs for every node, and its closure was a set, a list and a
    /// boxed enumerator each time. <paramref name="member"/> marks the closure by feature index.
    /// </summary>
    private IndexList Closure(IEnumerable<int> features, out bool[] member)
    {
        var length = Math.Max(1, _plan.Features.Count);
        var order = _memory.Rent<int>(length);
        member = _memory.RentCleared<bool>(length);
        return new IndexList(order, _plan.Closure(features, order, member));
    }

    private bool AnyContext(IndexList features)
    {
        for (var i = 0; i < features.Count; i++)
        {
            if (_plan.Features[features.Items[i]].IsContext)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Intersects(int[] features, bool[] member)
    {
        foreach (var f in features)
        {
            if (member[f])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Indices of the distinct candidate fetch groups of the given features in one wave, in group order.</summary>
    private IndexList GroupsOf(IndexList features, int wave)
    {
        var length = Math.Max(1, _plan.Groups.Count);
        var seen = _memory.RentCleared<bool>(length);
        var result = _memory.Rent<int>(length);
        var count = 0;
        for (var i = 0; i < features.Count; i++)
        {
            var info = _plan.Features[features.Items[i]];
            if (info.IsDerived || info.IsContext || info.Wave != wave || info.GroupIndex < 0 || seen[info.GroupIndex])
            {
                continue;
            }

            seen[info.GroupIndex] = true;
            result[count++] = info.GroupIndex;
        }

        return new IndexList(result, count);
    }

    /// <summary>
    /// The members of <paramref name="group"/> this request needs now and does not have yet (late materialization,
    /// design 5.4/6.8): requested (directly or as a dependency), not failed, and unfilled on some live row — optionally
    /// excluding members an in-flight fetch already carries. Returned in group order; <c>null</c> when none.
    /// </summary>
    private int[]? FeaturesToFetch(FetchGroup group, bool[] member, bool skipInFlight)
    {
        var members = group.FeatureIndices;
        Span<int> selected = members.Length <= 128 ? stackalloc int[members.Length] : _memory.Rent<int>(members.Length);
        var count = SelectNeeded(group, member, skipInFlight, selected);
        if (count == 0)
        {
            return null;
        }

        return count == members.Length ? members : selected[..count].ToArray();
    }

    /// <summary>
    /// Writes the members of <paramref name="group"/> this request needs now and does not have yet into
    /// <paramref name="selected"/> (at least as long as the group), in group order; returns how many. The one rule of
    /// what a fetch or a local read supplies (late materialization, design 5.4/6.8).
    /// </summary>
    private int SelectNeeded(FetchGroup group, bool[] member, bool skipInFlight, Span<int> selected)
    {
        var count = 0;
        foreach (var f in group.FeatureIndices)
        {
            if (member[f] && _state[f] != FeatureState.Failed && !_table.IsFilledForLiveRows(_plan.Features[f].Slot)
                && !(skipInFlight && InFlight(group, f)))
            {
                selected[count++] = f;
            }
        }

        return count;
    }

    private bool InFlight(FetchGroup group, int feature)
    {
        foreach (var pending in _pending)
        {
            if (pending.Group == group && Array.IndexOf(pending.Features, feature) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Connector field bindings for a subset of a group's members; field index i is <c>features[i]</c>.</summary>
    private CompiledFieldBinding[] FieldBindings(FetchGroup group, int[] features)
    {
        if (features == group.FeatureIndices)
        {
            return group.Fields;
        }

        var fields = new CompiledFieldBinding[features.Length];
        for (var i = 0; i < features.Length; i++)
        {
            fields[i] = group.Fields[_plan.Features[features[i]].FieldIndex] with { FieldIndex = i };
        }

        return fields;
    }

    /// <summary>
    /// A group keyed by a failed feature cannot be fetched; its features fail too (design 6.7: a dependency failure
    /// never degrades into legitimate missing values).
    /// </summary>
    private bool FailIfKeyFailed(FetchGroup group, ReadOnlySpan<int> features)
    {
        if (group.KeyFeatureIndex < 0 || _state[group.KeyFeatureIndex] != FeatureState.Failed)
        {
            return false;
        }

        var error = _errors[group.KeyFeatureIndex] ?? $"key feature '{_plan.Features[group.KeyFeatureIndex].Definition.Name}' failed";
        foreach (var f in features)
        {
            _state[f] = FeatureState.Failed;
            _errors[f] = error;
        }

        return true;
    }

    private PendingFetch Start(FetchGroup group, int[] features, CancellationToken cancellationToken)
    {
        var fields = FieldBindings(group, features);
        // Collect distinct keys of live rows that still miss any of the fetched features. The index, the key array and
        // the per-chunk arrays are rented from the request memory (design 4.8), so a fetch allocates nothing per key.
        // Lifetimes: the index and the chunk arrays are read only on the request's thread (Backfill, CompleteAsync),
        // before the request releases the memory. The key array is read by connector calls, which may outlive the
        // request, but each call holds the memory from before FetchAsync until the connector really completes and the call
        // has been recorded (ConnectorCall, IoLease), so the keys cannot return to the pool while a connector or a
        // recording can still read them.
        EntityKeyIndex? index = null;
        EntityKey[]? keys = null;
        var keyCount = 0;
        var contextKey = default(EntityKey);
        var noContextKey = false;
        if (group.ContextKeyId >= 0)
        {
            var v = _context[group.ContextKeyId];
            if (v.IsMissing)
            {
                noContextKey = true;
            }
            else
            {
                contextKey = ToKey(v);
            }
        }

        if (!noContextKey)
        {
            var live = _table.Rows.Order;
            foreach (var row in live)
            {
                if (NeedsFill(features, row) && TryCandidateKey(group, row, out var key) == KeyLookup.Found)
                {
                    if (index is null)
                    {
                        // Sized to the live rows, an upper bound on the distinct keys: the index never grows. All keys
                        // of a group have one type (the key column's).
                        index = new EntityKeyIndex(key.IsString ? EntityKeyType.String : EntityKeyType.Int64, live.Length, _memory);
                        keys = _memory.Rent<EntityKey>(live.Length);
                    }

                    if (index.TryAdd(key, keyCount))
                    {
                        keys![keyCount++] = key;
                    }
                }
            }
        }

        // Each max_batch chunk is an independent call, and chunks run concurrently. They share the fetch's staging area
        // (each writes only its own region of it, see StagingArea), its time budget and its cancellation source.
        var chunkSize = group.Supply.MaxBatch;
        var chunkCount = Batching.Count(keyCount, chunkSize);
        if (chunkCount == 0)
        {
            return new PendingFetch(group, features, index, chunkSize, 0, null, Array.Empty<ValueTask<FetchStatus>>(), Array.Empty<bool>(), noContextKey);
        }

        var all = keys!;
        var calls = _memory.Rent<ValueTask<FetchStatus>>(chunkCount);
        var chunkFailed = _memory.RentCleared<bool>(chunkCount);
        var budget = BudgetMs(group);
        if (budget <= 0)
        {
            for (var c = 0; c < chunkCount; c++)
            {
                calls[c] = new ValueTask<FetchStatus>(NoTimeLeft);
                RecordUnsent(group, new EntityKeyBatch { Shape = group.Shape, ContextKey = contextKey, CandidateKeys = ChunkKeys(all, keyCount, chunkSize, c) }, fields);
            }

            return new PendingFetch(group, features, index, chunkSize, chunkCount, null, calls, chunkFailed, noContextKey);
        }

        var staging = new StagingArea(fields, keyCount, chunkSize, _memory);
        var cancellation = _hook is { Replays: true } ? null : new FetchCancellation(cancellationToken, budget, _events, _requestId);
        StagingBuffer? writer = null;
        try
        {
            for (var c = 0; c < chunkCount; c++)
            {
                var batch = new EntityKeyBatch { Shape = group.Shape, ContextKey = contextKey, CandidateKeys = ChunkKeys(all, keyCount, chunkSize, c) };
                writer ??= new StagingBuffer(staging);
                writer.Bind(c);
                calls[c] = CallAsync(group, batch, fields, writer, budget, cancellation, cancellationToken, out var writerHeld);
                if (writerHeld)
                {
                    writer = null; // the call runs on and may write through this view until it really completes
                }
            }
        }
        finally
        {
            cancellation?.Release(); // the fetch's own reference; every sent chunk holds one until its connector completes
        }

        return new PendingFetch(group, features, index, chunkSize, chunkCount, staging, calls, chunkFailed, noContextKey);
    }

    private static ReadOnlyMemory<EntityKey> ChunkKeys(EntityKey[] keys, int keyCount, int chunkSize, int chunk)
    {
        var offset = chunk * chunkSize;
        return keys.AsMemory(offset, Math.Min(chunkSize, keyCount - offset));
    }

    /// <summary>
    /// Runs one connector call, waiting for it at most <paramref name="budgetMs"/> — the source timeout or the time
    /// left before the request deadline, whichever is less — counted from the start of the fetch; the connector's token,
    /// from <paramref name="cancellation"/>, is cancelled then too. Cancellation is only a request to the connector, so
    /// the wait itself is bounded: a result arriving later is rejected as a timeout and its staging is never read.
    /// </summary>
    /// <remarks>
    /// <para>The call's hold on the request memory lasts until the connector really completes, because a connector that
    /// ignores cancellation may still be writing into <paramref name="writer"/> (design 4.8), and until the call has
    /// been recorded, which reads the keys and the staging. Never throws for a connector failure: failures become
    /// <see cref="FetchOutcome.Failed"/>.</para>
    /// <para>The returned task is consumed exactly once, or never (a prefetch nobody awaits): it is a plain result or
    /// a <see cref="Task"/>, never a pooled source.</para>
    /// </remarks>
    /// <param name="cancellation">The fetch's cancellation; null only when the request replays.</param>
    /// <param name="writerHeld">True when the call is still running and owns <paramref name="writer"/>; otherwise the
    /// view may be pointed at the next chunk.</param>
    private ValueTask<FetchStatus> CallAsync(
        FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, FetchCancellation? cancellation,
        CancellationToken cancellationToken, out bool writerHeld)
    {
        if (_hook is null)
        {
            return ConnectorCall(group, batch, fields, writer, budgetMs, 0, cancellation!, cancellationToken, out writerHeld);
        }

        if (_hook.Replays)
        {
            // A replayed call answers at once from the recording: no connector, no wait, no metrics or health (design 13.4).
            writerHeld = false;
            return new ValueTask<FetchStatus>(_hook.Replay(group, batch, fields, writer));
        }

        return ConnectorCall(group, batch, fields, writer, budgetMs, _hook.Begin(group, batch, fields), cancellation!, cancellationToken, out writerHeld);
    }

    /// <summary>Records a chunk the request had no time left to send, so a replay fails it the same way.</summary>
    private void RecordUnsent(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields)
    {
        if (_hook is { Replays: false } recorder)
        {
            recorder.Record(new FeatureCallInfo(group, batch, fields, recorder.Begin(group, batch, fields), false, NoTimeLeft, IoOutcome.Timeout, TimeSpan.Zero), null);
        }
    }

    /// <summary>
    /// Sends one call. A connector that answers synchronously is recorded and released here, with nothing allocated;
    /// only one that returns a pending task goes on to <see cref="AwaitConnectorAsync"/>.
    /// </summary>
    private ValueTask<FetchStatus> ConnectorCall(
        FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, int sequence,
        FetchCancellation cancellation, CancellationToken cancellationToken, out bool writerHeld)
    {
        _memory.Hold();
        var io = group.Io.Start();
        cancellation.AddCall();
        ValueTask<FetchStatus> call;
        try
        {
            call = group.Supply.Connector!.FetchAsync(batch, fields, writer, cancellation.Token);
        }
        catch (Exception ex)
        {
            call = ValueTask.FromException<FetchStatus>(ex); // threw before returning a task: completed, writing no more
        }

        if (call.IsCompleted)
        {
            writerHeld = false;
            try
            {
                return new ValueTask<FetchStatus>(Observed(io, group, batch, fields, writer, budgetMs, sequence, call, cancellation, cancellationToken));
            }
            finally
            {
                cancellation.Release();
                _memory.Release(); // after Observed: a recording reads the keys and the staging there
            }
        }

        writerHeld = true;
        return AwaitConnectorAsync(io, group, batch, fields, writer, budgetMs, sequence, call, cancellation, cancellationToken);
    }

    /// <summary>The rest of a call whose connector did not complete synchronously; the memory is already held.</summary>
    private async ValueTask<FetchStatus> AwaitConnectorAsync(
        IoCall io, FetchGroup group, EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, int sequence,
        ValueTask<FetchStatus> call, FetchCancellation cancellation, CancellationToken cancellationToken)
    {
        var lease = new IoLease(_memory, cancellation);
        var task = call.AsTask();
        TrackIo(task.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // observe a late failure nobody awaits any more
                ((IoLease)state!).ConnectorDone();
            },
            lease,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default));

        // The wait decides the status; the call is completed once, after it (as BoundedCall does): completing inside the
        // try would complete it again from the catch when the completion itself throws (a hook that fails, say). The
        // outer finally covers the wait, the classification and the completion alike: the call site is done however any
        // of them ends, so a diagnostic that throws (an exception whose Message throws) never keeps the memory held.
        try
        {
            var status = default(FetchStatus);
            IoOutcome? outcome = null;
            var observeNow = false;
            try
            {
                // The wait has its own bound — the time left before the fetch's deadline, and the caller's token — and does
                // not wait on the connectors' token: cancelling that one runs the connectors' callbacks synchronously, one
                // after another, so a callback that blocks would hold every wait behind it. The deadline is the fetch's, so
                // a chunk sent later gets no fresh budget. The wait ends with a TimeoutException or the caller's
                // cancellation, even when the connector ignores both; Classify tells them apart by the caller's token.
                var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), cancellation.DeadlineTimestamp);
                var wait = task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);

                // WaitAsync found the task complete (it did between the check in ConnectorCall and here) and returned it
                // without comparing anything with the bound: no wait was installed to decide, so it is observed now.
                observeNow = ReferenceEquals(wait, task);
                if (!observeNow)
                {
                    status = await wait.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                status = Failure(group, ex, budgetMs, cancellationToken);
                outcome = Classify(ex, cancellationToken);
            }

            return observeNow
                ? Observed(io, group, batch, fields, writer, budgetMs, sequence, new ValueTask<FetchStatus>(task), cancellation, cancellationToken)
                : Completed(io, group, batch, fields, writer, sequence, status, outcome);
        }
        finally
        {
            // After Completed: a recording reads the keys and the staging there, and a prefetch nobody awaits may get here
            // after the request has released its own hold.
            lease.CallSiteDone();
        }
    }

    /// <summary>
    /// Settles a call found completed without an installed wait to decide it: a connector that returned (or threw)
    /// synchronously, or a task <c>WaitAsync</c> returned as it was. The one rule of when such a result counts (design
    /// 4.8): only if the fetch observes it within its bound, that is now. Once the caller has cancelled, the deadline has
    /// passed, or the fetch's timer has cancelled the connectors' token (a timer may fire slightly before the clock reaches
    /// the deadline), the chunk is cancelled or timed out: its staging is never read and its keys fail like any timeout's.
    /// A connector that completed in time but is observed late (preempted) is timed out too: conservative by design.
    /// </summary>
    /// <param name="completed">Consumed exactly once, in time or not, so a pooled source is released and a failure observed.</param>
    private FetchStatus Observed(
        in IoCall io, FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, int sequence,
        ValueTask<FetchStatus> completed, FetchCancellation cancellation, CancellationToken cancellationToken)
    {
        IoOutcome? cutOff = cancellationToken.IsCancellationRequested ? IoOutcome.Cancelled
            : Stopwatch.GetTimestamp() >= cancellation.DeadlineTimestamp || cancellation.Token.IsCancellationRequested ? IoOutcome.Timeout
            : null;
        FetchStatus status;
        IoOutcome? outcome = null;
        try
        {
            status = completed.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            status = Failure(group, ex, budgetMs, cancellationToken);
            outcome = Classify(ex, cancellationToken);
        }

        if (cutOff is { } late)
        {
            status = late == IoOutcome.Cancelled ? Cancelled(group) : TimedOut(group, budgetMs);
            outcome = late;
        }

        return Completed(io, group, batch, fields, writer, sequence, status, outcome);
    }

    /// <summary>
    /// Records a finished call (metrics, source health, span; and the call itself when the request records) and passes its
    /// status through. Staged values are recorded only for a call that answered within its budget.
    /// </summary>
    private FetchStatus Completed(
        in IoCall io, FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int sequence, FetchStatus status,
        IoOutcome? outcome = null)
    {
        var result = outcome ?? status.Outcome switch
        {
            FetchOutcome.Failed => IoOutcome.Error,
            FetchOutcome.Partial => IoOutcome.Partial,
            _ => IoOutcome.Ok,
        };
        var duration = _hook is null ? TimeSpan.Zero : io.Elapsed;
        io.Complete(result, batch.KeyCount, _requestId, status.Error, _events);
        if (sequence != NotRecorded)
        {
            _hook?.Record(
                new FeatureCallInfo(group, batch, fields, sequence, true, status, result, duration),
                result is IoOutcome.Ok or IoOutcome.Partial ? writer : null);
        }

        return status;
    }

    private static IoOutcome Classify(Exception ex, CancellationToken requestToken) => ex switch
    {
        OperationCanceledException when requestToken.IsCancellationRequested => IoOutcome.Cancelled,
        TimeoutException or OperationCanceledException => IoOutcome.Timeout,
        _ => IoOutcome.Error,
    };

    private static FetchStatus Failure(FetchGroup group, Exception ex, int budgetMs, CancellationToken requestToken) => ex switch
    {
        TimeoutException => TimedOut(group, budgetMs),
        OperationCanceledException when requestToken.IsCancellationRequested => Cancelled(group),
        OperationCanceledException => TimedOut(group, budgetMs),
        _ => FetchStatus.Failed($"source '{group.SourceName}' failed: {ex.Message}"),
    };

    private static FetchStatus TimedOut(FetchGroup group, int budgetMs) => FetchStatus.Failed($"source '{group.SourceName}' timed out after {budgetMs} ms");

    private static FetchStatus Cancelled(FetchGroup group) => FetchStatus.Failed($"source '{group.SourceName}' was cancelled");

    private void TrackIo(Task task)
    {
        lock (_io)
        {
            _io.Add(task);
        }
    }

    private async ValueTask CompleteAsync(PendingFetch pending)
    {
        var group = pending.Group;
        var failedChunks = 0;
        var partial = false;
        string? error = null;
        for (var c = 0; c < pending.ChunkCount; c++)
        {
            var status = await pending.Calls[c].ConfigureAwait(false);
            if (status.Outcome == FetchOutcome.Failed)
            {
                pending.ChunkFailed[c] = true;
                failedChunks++;
            }

            partial |= status.Outcome == FetchOutcome.Partial;
            error ??= status.Error;
        }

        if (CountsValues && failedChunks < pending.ChunkCount && pending.Staging is { } staging)
        {
            CountFetchedValues(pending, staging);
        }

        // Some chunks failed: the source partially failed (design 6.7) and readers become Degraded. The failed chunks'
        // keys are treated like keys absent from the response; their staging is never read.
        var degraded = failedChunks > 0 || partial;
        if (!SettleKey(group, pending.Features, failedChunks > 0 && failedChunks == pending.ChunkCount, ref degraded, ref error))
        {
            return;
        }

        for (var i = 0; i < pending.Features.Length; i++)
        {
            var f = pending.Features[i];
            if (_state[f] == FeatureState.Failed)
            {
                continue;
            }

            Backfill(pending, _plan.Features[f], i);
            SetFetchedState(f, degraded, error);
        }
    }

    /// <summary>
    /// Counts what the source had of the values a fetch asked for (<see cref="StrataRecMetrics.SourceValues"/>): every key
    /// of a chunk that answered in time, once per fetched feature, present where the connector wrote the field. The keys of
    /// a failed or timed-out chunk are not counted (that call is an error, and its staging is never read).
    /// </summary>
    private void CountFetchedValues(PendingFetch pending, StagingArea staging)
    {
        var answered = 0;
        for (var c = 0; c < pending.ChunkCount; c++)
        {
            answered += pending.ChunkFailed[c] ? 0 : staging.ChunkKeyCount(c);
        }

        for (var i = 0; i < pending.Features.Length; i++)
        {
            var present = 0;
            for (var c = 0; c < pending.ChunkCount; c++)
            {
                present += pending.ChunkFailed[c] ? 0 : staging.CountWritten(i, c);
            }

            CountValues(_plan.Features[pending.Features[i]], present, answered - present);
        }
    }

    /// <summary>
    /// Counts the fetches no reader took (<see cref="StrataRecMetrics.SourceValues"/>): a prefetch whose features only a
    /// node that did not run needed (a fallback, review 25), or one the request ended before reading. A source that
    /// answered was asked whether or not the request used the answer. Only chunks whose calls completed within their
    /// budget count, as for a fetch that was read; a chunk still running when the request ends is abandoned. Call once, at
    /// the end of the request, before its memory is released (the staging lives in it).
    /// </summary>
    public void CountUnreadFetches()
    {
        if (!CountsValues)
        {
            return;
        }

        foreach (var pending in _pending)
        {
            if (pending.Staging is not { } staging)
            {
                continue;
            }

            // Never awaited: each unread call's result is read here once.
            var answered = 0;
            for (var c = 0; c < pending.ChunkCount; c++)
            {
                var call = pending.Calls[c];
                var failed = !call.IsCompleted || call.Result.Outcome == FetchOutcome.Failed;
                pending.ChunkFailed[c] = failed;
                answered += failed ? 0 : 1;
            }

            if (answered > 0)
            {
                CountFetchedValues(pending, staging);
            }
        }

        _pending.Clear();
    }

    /// <summary>
    /// Whether this request counts <see cref="StrataRecMetrics.SourceValues"/>: only while a listener is enabled, so a
    /// request without one pays this check alone, and never in a replay, which is not traffic (design 13.4).
    /// </summary>
    private bool CountsValues => StrataRecMetrics.SourceValues.Enabled && _hook is not { Replays: true };

    /// <summary>
    /// Records what a source had of one feature's values in one read or fetch: one add per outcome, none for a zero, with
    /// the tags the plan built for the feature.
    /// </summary>
    private static void CountValues(FeatureRuntimeInfo feature, int present, int missing)
    {
        var tags = feature.ValueTags;
        if (present > 0)
        {
            StrataRecMetrics.SourceValues.Add(present, tags.Present);
        }

        if (missing > 0)
        {
            StrataRecMetrics.SourceValues.Add(missing, tags.Missing);
        }
    }

    /// <summary>
    /// The first half of the state rule of a fetch or a cache read (design 6.7): a failed source or a failed key fails
    /// every feature (set here; returns false, nothing to fill), and a degraded key degrades them. One rule for both,
    /// so a replay, which answers a cache read through the fetch path, ends in the states the live read did.
    /// </summary>
    private bool SettleKey(FetchGroup group, int[] features, bool failed, ref bool degraded, ref string? error)
    {
        var keyState = group.KeyFeatureIndex >= 0 ? _state[group.KeyFeatureIndex] : FeatureState.Ok;
        if (keyState is FeatureState.Failed or FeatureState.Degraded)
        {
            error ??= _errors[group.KeyFeatureIndex] ?? $"key feature '{_plan.Features[group.KeyFeatureIndex].Definition.Name}' is {keyState}";
        }

        if (failed || keyState == FeatureState.Failed)
        {
            foreach (var f in features)
            {
                _state[f] = FeatureState.Failed;
                _errors[f] = error;
            }

            return false;
        }

        degraded |= keyState == FeatureState.Degraded;
        return true;
    }

    /// <summary>The second half: a filled feature that has not failed is degraded or ok.</summary>
    private void SetFetchedState(int feature, bool degraded, string? error)
    {
        if (degraded)
        {
            _state[feature] = FeatureState.Degraded;
            _errors[feature] = error;
        }
        else if (_state[feature] == FeatureState.Unrequested)
        {
            _state[feature] = FeatureState.Ok;
        }
    }

    /// <summary>
    /// Copies staged values into the live rows that are still unfilled, matching by key (design 4.5). Key index k belongs
    /// to chunk k / max_batch. A value is read only if its chunk succeeded and the connector wrote that field for that
    /// key; anything else takes the default or becomes missing.
    /// </summary>
    private void Backfill(PendingFetch pending, FeatureRuntimeInfo feature, int fieldIndex)
    {
        var definition = feature.Definition;
        var column = _table.GetOrCreateSlot(feature.Slot);
        var filled = _table.GetFilled(feature.Slot);
        var copied = _memory.Rent<int>(Math.Max(1, _table.Rows.Count));
        var copiedCount = 0;
        foreach (var row in _table.Rows.Order)
        {
            if (Bits.Get(filled, row))
            {
                continue;
            }

            var keyIndex = -1; // no key: legitimately missing (e.g. anonymous user, missing foreign key)
            if (!pending.NoContextKey)
            {
                var lookup = TryCandidateKey(pending.Group, row, out var key);
                if (lookup == KeyLookup.NotReady)
                {
                    continue; // the key itself is not available for this row yet; a later fetch fills it
                }

                if (lookup == KeyLookup.Found && (pending.Index is null || !pending.Index.TryGetValue(key, out keyIndex)))
                {
                    continue; // row appended after this fetch started; a later fetch fills it
                }
            }

            if (keyIndex >= 0 && pending.Staging is { } staging && !pending.ChunkFailed[keyIndex / pending.ChunkSize]
                && staging.HasValue(fieldIndex, keyIndex))
            {
                staging.CopyTo(fieldIndex, keyIndex, column, row);
                column.SetValid(row);
                copied[copiedCount++] = row;
            }
            else
            {
                ColumnValues.Set(column, row, definition.Absent);
            }

            Bits.Set(filled, row);
        }

        if (copiedCount > 0 && ColumnValues.IsFloat(column))
        {
            AddNonFinite(feature, ColumnValues.SanitizeNonFinite(column, copied.AsSpan(0, copiedCount), definition.Absent));
        }
    }

    private void ComputeDerivedOfWave(int wave, bool[] member)
    {
        var order = _plan.DerivedOrder;
        for (var i = 0; i < order.Count; i++) // an interface foreach would box the enumerator, twice per ensure
        {
            var f = order[i];
            var info = _plan.Features[f];
            if (member[f] && !info.IsContext && info.Wave == wave)
            {
                ComputeDerived(info);
            }
        }
    }

    private void ComputeDerived(FeatureRuntimeInfo feature)
    {
        var (worst, error) = WorstDependency(feature);
        if (worst == FeatureState.Failed)
        {
            _state[feature.Index] = FeatureState.Failed;
            _errors[feature.Index] = error;
            return;
        }

        var filled = _table.GetFilled(feature.Slot);
        var buffer = _memory.Rent<int>(Math.Max(1, _table.Rows.Count));
        var count = 0;
        foreach (var row in _table.Rows.Order)
        {
            if (!Bits.Get(filled, row))
            {
                buffer[count++] = row;
            }
        }

        if (_state[feature.Index] != FeatureState.Failed)
        {
            _state[feature.Index] = worst;
            _errors[feature.Index] = error;
        }

        if (count == 0)
        {
            return;
        }

        var definition = feature.Definition;
        var column = _table.GetOrCreateSlot(feature.Slot);
        var span = buffer.AsSpan(0, count);
        try
        {
            // A value that does not fit int32 storage takes the default (or becomes missing) at the write and is
            // counted there (design 4.3): unlike NaN, it cannot be stored and sanitized afterwards.
            feature.Expression!.Evaluate(new EvalContext(_table, _bindings, _context, _params, _counters), span, column, definition.Absent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The backend does not throw for data (errors become missing values); an exception here is a defect in
            // a function or connector-provided value. Treat it like a failed source (design 6.7): readers of this
            // feature fail with a clear error, everything else in the request keeps going. Rows stay unfilled.
            _state[feature.Index] = FeatureState.Failed;
            _errors[feature.Index] = $"derived feature '{definition.Name}' failed to evaluate: {ex.Message}";
            return;
        }

        if (definition.HasDefault)
        {
            foreach (var row in span)
            {
                if (!column.IsValid(row))
                {
                    ColumnValues.Set(column, row, definition.Default);
                }
            }
        }
        else if (!feature.Type.Nullable && column.Validity is not null)
        {
            // A non-nullable expression leaves a row missing only where an out-of-range int32 value was replaced at the
            // write. With no default to take, the feature cannot be served as typed: readers compiled against a
            // non-nullable type skip validity checks (same rule as a node output, design 4.6).
            var missing = 0;
            foreach (var row in span)
            {
                if (!column.IsValid(row))
                {
                    missing++;
                }
            }

            if (missing > 0)
            {
                _state[feature.Index] = FeatureState.Failed;
                _errors[feature.Index] = $"derived feature '{definition.Name}' produced {missing} value(s) outside the int32 range of its " +
                    $"{feature.Type} type but is non-nullable and declares no default; declare a default or make the expression nullable";
            }
        }

        if (ColumnValues.IsFloat(column))
        {
            // The expression backend writes NaN/±∞ as-is and leaves sanitizing to the engine (design 4.3).
            var replaced = ColumnValues.SanitizeNonFinite(column, span, definition.Absent);
            AddNonFinite(feature, replaced);
            if (replaced > 0 && !definition.HasDefault && !feature.Type.Nullable)
            {
                // Same rule as a node output (design 4.6): a non-nullable column cannot hold the replacement "missing".
                _state[feature.Index] = FeatureState.Failed;
                _errors[feature.Index] = $"derived feature '{definition.Name}' produced {replaced} non-finite value(s) " +
                    "but is non-nullable and declares no default";
            }
        }

        foreach (var row in span)
        {
            Bits.Set(filled, row);
        }
    }

    private (FeatureState Worst, string? Error) WorstDependency(FeatureRuntimeInfo feature)
    {
        var worst = FeatureState.Ok;
        string? error = null;
        foreach (var d in feature.Dependencies)
        {
            if (_state[d] > worst)
            {
                worst = _state[d];
                error = _errors[d];
            }
        }

        return (worst, error);
    }

    /// <param name="closure">The features to make available; only its context features are fetched.</param>
    /// <param name="member">Marks <paramref name="closure"/> by feature index.</param>
    private async Task FetchContextAsync(IndexList closure, bool[] member, CancellationToken cancellationToken)
    {
        try
        {
            var groupCount = Math.Max(1, _plan.Groups.Count);
            var seenGroup = _memory.RentCleared<bool>(groupCount);
            var fetches = _memory.Rent<ContextFetch>(groupCount);
            var fetchCount = 0;
            var locals = false;
            for (var c = 0; c < closure.Count; c++)
            {
                var info = _plan.Features[closure.Items[c]];
                if (!info.IsContext || info.IsDerived || info.GroupIndex < 0 || seenGroup[info.GroupIndex])
                {
                    continue;
                }

                var group = _plan.Groups[info.GroupIndex];
                if (ReadsLocally(group))
                {
                    locals = true; // read below, while the fetches are in flight
                    continue;
                }

                seenGroup[info.GroupIndex] = true;
                var features = RequestedMembers(group, member);
                var v = group.ContextKeyId >= 0 ? _context[group.ContextKeyId] : ScalarValue.Missing;
                if (v.IsMissing)
                {
                    fetches[fetchCount++] = new ContextFetch(features, null, new ValueTask<FetchStatus>(FetchStatus.Ok));
                    continue;
                }

                var fields = FieldBindings(group, features);
                var batch = new EntityKeyBatch { Shape = group.Shape, ContextKey = ToKey(v) };
                var budget = BudgetMs(group);
                if (budget <= 0)
                {
                    fetches[fetchCount++] = new ContextFetch(features, null, new ValueTask<FetchStatus>(NoTimeLeft));
                    RecordUnsent(group, batch, fields);
                    continue;
                }

                var staging = new StagingBuffer(fields, 1, _memory);
                var cancellation = _hook is { Replays: true } ? null : new FetchCancellation(cancellationToken, budget, _events, _requestId);
                ValueTask<FetchStatus> call;
                try
                {
                    call = CallAsync(group, batch, fields, staging, budget, cancellation, cancellationToken, out _);
                }
                finally
                {
                    cancellation?.Release();
                }

                fetches[fetchCount++] = new ContextFetch(features, staging, call);
            }

            var cached = locals ? ReadLocalContext(closure, member, cancellationToken) : null;
            for (var n = 0; n < fetchCount; n++)
            {
                var fetch = fetches[n];
                var status = await fetch.Call.ConfigureAwait(false);
                if (CountsValues && fetch.Staging is { } answered && status.Outcome != FetchOutcome.Failed)
                {
                    // The one key was asked for (a missing context key sends nothing) and answered in time.
                    for (var i = 0; i < fetch.Features.Length; i++)
                    {
                        var present = answered.HasValue(i, 0) ? 1 : 0;
                        CountValues(_plan.Features[fetch.Features[i]], present, 1 - present);
                    }
                }

                for (var i = 0; i < fetch.Features.Length; i++)
                {
                    var f = fetch.Features[i];
                    if (status.Outcome == FetchOutcome.Failed)
                    {
                        _state[f] = FeatureState.Failed;
                        _errors[f] = status.Error;
                        continue;
                    }

                    var value = fetch.Staging is null ? ScalarValue.Missing : fetch.Staging.Get(i, 0);
                    var partial = status.Outcome == FetchOutcome.Partial;
                    _state[f] = partial ? FeatureState.Degraded : FeatureState.Ok;
                    _errors[f] = partial ? status.Error : null;
                    StoreContext(_plan.Features[f], value);
                }
            }

            for (var read = cached; read is not null; read = read.Next)
            {
                await CompleteCachedContextAsync(read).ConfigureAwait(false);
            }

            var order = _plan.DerivedOrder;
            for (var d = 0; d < order.Count; d++)
            {
                var f = order[d];
                var info = _plan.Features[f];
                if (!info.IsContext || !member[f])
                {
                    continue;
                }

                var (worst, error) = WorstDependency(info);
                _state[f] = worst;
                _errors[f] = error;
                if (worst == FeatureState.Failed)
                {
                    continue;
                }

                ScalarValue derived;
                try
                {
                    derived = info.Expression!.EvaluateScalar(new EvalContext(null, null, _context, _params, _counters));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Same rule as candidate-derived features: a throwing expression fails this feature only.
                    _state[f] = FeatureState.Failed;
                    _errors[f] = $"derived feature '{info.Definition.Name}' failed to evaluate: {ex.Message}";
                    continue;
                }

                StoreContext(info, derived);
            }
        }
        finally
        {
            _memory.Release();
        }
    }

    /// <summary>
    /// The members of a context group the prefetch needs (late materialization applies to context entities too), in
    /// group order. The context fetch runs once per request, so nothing is filled or in flight yet.
    /// </summary>
    private static int[] RequestedMembers(FetchGroup group, bool[] member)
    {
        var count = 0;
        foreach (var f in group.FeatureIndices)
        {
            count += member[f] ? 1 : 0;
        }

        if (count == group.FeatureIndices.Length)
        {
            return group.FeatureIndices;
        }

        var selected = new int[count];
        count = 0;
        foreach (var f in group.FeatureIndices)
        {
            if (member[f])
            {
                selected[count++] = f;
            }
        }

        return selected;
    }

    /// <summary>
    /// Publishes a context feature value: NaN/±∞, and an integer (or list element) outside the int32 range of an
    /// <c>int32</c> or <c>list&lt;int32&gt;</c> feature, are counted and replaced by the default or missing (design 4.3), and
    /// a missing value takes the default.
    /// </summary>
    /// <remarks>Context values are held promoted, so an out-of-range value would not wrap here; it is still replaced,
    /// so a feature's value always lies in its declared type wherever it is read, logged or written.</remarks>
    private void StoreContext(FeatureRuntimeInfo info, ScalarValue value)
    {
        var definition = info.Definition;
        if (value.Kind == StorageKind.Float64 && !double.IsFinite(value.AsFloat64()))
        {
            AddNonFinite(info, 1);
            value = ScalarValue.Missing;
            if (!definition.HasDefault && !info.Type.Nullable)
            {
                _state[info.Index] = FeatureState.Failed;
                _errors[info.Index] = $"feature '{definition.Name}' produced a non-finite value but is non-nullable and declares no default";
            }
        }
        else if (!value.IsMissing && !ColumnValues.FitsStorage(value, info.Type))
        {
            _counters.AddOutOfRange(1);
            if (!definition.HasDefault && !info.Type.Nullable)
            {
                _state[info.Index] = FeatureState.Failed;
                _errors[info.Index] = $"feature '{definition.Name}' produced {value}, outside the int32 range of its {info.Type} type, " +
                    "but is non-nullable and declares no default";
            }

            value = ScalarValue.Missing;
        }

        _context[info.ContextId] = value.IsMissing ? definition.Absent : value;
    }

    private void AddNonFinite(FeatureRuntimeInfo feature, int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _nonFiniteReplaced, count);
            if (_hook is not { Replays: true })
            {
                // A replay is not traffic: it records no metrics (design 13.4).
                StrataRecMetrics.ColumnNonFinite.Add(count, feature.NonFiniteTags);
            }
        }
    }

    private bool NeedsFill(ReadOnlySpan<int> features, int row)
    {
        foreach (var f in features)
        {
            if (_state[f] != FeatureState.Failed && !Bits.Get(_table.GetFilled(_plan.Features[f].Slot), row))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a row's candidate key. A foreign key that is not filled yet for the row is <see cref="KeyLookup.NotReady"/>,
    /// which is not the same as a key that is legitimately missing: treating it as missing would publish default values
    /// for rows whose key is merely not computed yet.
    /// </summary>
    private KeyLookup TryCandidateKey(FetchGroup group, int row, out EntityKey key)
    {
        key = default;
        if (group.KeyFeatureIndex < 0)
        {
            key = _table.GetKey(row);
            return KeyLookup.Found;
        }

        var keyFeature = _plan.Features[group.KeyFeatureIndex];
        var column = _table.TryGetSlot(keyFeature.Slot);
        if (column is null || !Bits.Get(_table.GetFilled(keyFeature.Slot), row))
        {
            return KeyLookup.NotReady;
        }

        if (!column.IsValid(row))
        {
            return KeyLookup.Missing;
        }

        switch (column)
        {
            case ScalarColumn<long> l: key = EntityKey.FromInt64(l[row]); break;
            case ScalarColumn<int> i: key = EntityKey.FromInt64(i[row]); break;
            case DictStringColumn s: key = EntityKey.FromString(s.Get(row)); break;
            default: return KeyLookup.Missing;
        }

        return KeyLookup.Found;
    }

    private static EntityKey ToKey(in ScalarValue value) =>
        value.Kind == StorageKind.Int64 ? EntityKey.FromInt64(value.AsInt64()) : EntityKey.FromString(value.AsString());

    private int BudgetMs(FetchGroup group) => Math.Min(group.Supply.TimeoutMs, RemainingMs());

    private int RemainingMs()
    {
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _deadlineTimestamp);
        return (int)Math.Clamp(remaining.TotalMilliseconds, 0, int.MaxValue);
    }

    private enum KeyLookup : byte
    {
        Found,

        /// <summary>The key column is filled but has no value: the row legitimately has no such entity.</summary>
        Missing,

        /// <summary>The key column is not filled for the row yet.</summary>
        NotReady,
    }

    /// <summary>One candidate fetch: a subset of a group's members for the keys of the rows that missed them.</summary>
    /// <remarks>
    /// The index and the per-chunk arrays are rented from the request memory and may be longer than
    /// <see cref="ChunkCount"/>; they are read only on the request's thread while the request holds the memory.
    /// </remarks>
    private sealed class PendingFetch(
        FetchGroup group,
        int[] features,
        EntityKeyIndex? index,
        int chunkSize,
        int chunkCount,
        StagingArea? staging,
        ValueTask<FetchStatus>[] calls,
        bool[] chunkFailed,
        bool noContextKey)
    {
        public FetchGroup Group { get; } = group;

        /// <summary>Fetched features; staging field i holds <c>Features[i]</c>.</summary>
        public int[] Features { get; } = features;

        /// <summary>Key → key index; key index k belongs to chunk k / ChunkSize. Null when no live row had a key.</summary>
        public EntityKeyIndex? Index { get; } = index;

        public int ChunkSize { get; } = chunkSize;

        /// <summary>Number of chunks; the chunk arrays below are valid up to this length.</summary>
        public int ChunkCount { get; } = chunkCount;

        /// <summary>Staging of every chunk; null when no chunk was sent (no time left).</summary>
        public StagingArea? Staging { get; } = staging;

        /// <summary>Each awaited once, by <see cref="CompleteAsync"/>; never, for a prefetch nobody needs.</summary>
        public ValueTask<FetchStatus>[] Calls { get; } = calls;

        /// <summary>Set on completion: chunks whose staging must not be read.</summary>
        public bool[] ChunkFailed { get; } = chunkFailed;

        public bool NoContextKey { get; } = noContextKey;
    }

    /// <summary>Feature or group indices in an array rented from the request memory, valid up to <see cref="Count"/>.</summary>
    private readonly record struct IndexList(int[] Items, int Count);

    /// <param name="Call">Awaited once, by <see cref="FetchContextAsync"/>.</param>
    private readonly record struct ContextFetch(int[] Features, StagingBuffer? Staging, ValueTask<FetchStatus> Call);

    /// <summary>
    /// The cancellation of one fetch: a single source for all its chunks' connectors, linked to the caller's token and
    /// cancelled at the fetch's budget, which the chunks share. A fetch of hundreds of chunks therefore creates one source
    /// and one timer rather than a source and a timer per chunk. It also fixes the fetch's absolute deadline, by which
    /// an asynchronous call's wait is bounded on its own (see <see cref="AwaitConnectorAsync"/>).
    /// </summary>
    /// <remarks>
    /// <para>The budget cancels the token as an <see cref="ObservedCancellationSource"/> does: the connectors' (and the
    /// cache store's) callbacks run on the pool, and what they throw is counted and reported, never thrown on the timer's
    /// thread. The fetch's cancellation is the source itself, so the timer's wrapper costs no more than the object that
    /// used to hold the source.</para>
    /// <para>Reference-counted: the starting fetch holds one reference until every chunk has been sent, and each sent chunk
    /// one until its connector has really completed. The source is disposed with the last, so a connector that ignores
    /// cancellation and finishes late still holds a live token; a fetch whose connectors never complete leaves its source
    /// to the GC once the timer has fired.</para>
    /// </remarks>
    private sealed class FetchCancellation : ObservedCancellationSource
    {
        private int _references = 1;

        /// <param name="events">Where a failure of the connectors' cancellation callbacks is summarized, with <paramref name="requestId"/>.</param>
        public FetchCancellation(CancellationToken caller, int budgetMs, RequestEventLog? events, string? requestId)
            : base(CancellationSite.Fetch, caller, events, requestId)
        {
            DeadlineTimestamp = Stopwatch.GetTimestamp() + budgetMs * Stopwatch.Frequency / 1000;
            CancelAfterObserved(TimeSpan.FromMilliseconds(budgetMs));
            Token = base.Token;
        }

        /// <summary>
        /// The connectors' token: cancelled at the budget or with the caller's token. Read once, so it stays readable after
        /// the source is disposed; the source's own property throws then.
        /// </summary>
        public new CancellationToken Token { get; }

        /// <summary>When the fetch's budget runs out, as a <see cref="Stopwatch"/> timestamp.</summary>
        public long DeadlineTimestamp { get; }

        /// <summary>A chunk is about to be sent.</summary>
        public void AddCall() => Interlocked.Increment(ref _references);

        /// <summary>A chunk's connector has completed, or the fetch has sent its last chunk.</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                Dispose();
            }
        }
    }

    /// <summary>
    /// What a connector call that did not complete synchronously holds: the hold on the request memory taken before it
    /// was sent, and a reference to its fetch's cancellation. The memory is released when both the connector has really
    /// completed and the call site has finished with the call (recorded it, when the request records), in either order.
    /// One hold per external call, so the abandoned-I/O count stays one per connector call still running. A call answered
    /// synchronously needs none of this: its call site releases the hold right after recording it.
    /// </summary>
    private sealed class IoLease(RequestMemory memory, FetchCancellation cancellation)
    {
        private int _parts = 2;

        /// <summary>The connector's task has completed: it writes no more and no longer needs its token.</summary>
        public void ConnectorDone()
        {
            cancellation.Release();
            Release();
        }

        /// <summary>The call site has finished reading the keys and the staging.</summary>
        public void CallSiteDone() => Release();

        private void Release()
        {
            if (Interlocked.Decrement(ref _parts) == 0)
            {
                memory.Release();
            }
        }
    }
}
