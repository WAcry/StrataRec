using System.Diagnostics;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
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
/// I/O never misplace values.
/// </summary>
/// <remarks>
/// <para>Not thread-safe; driven by the request's executor. Connectors and the context prefetch run concurrently
/// underneath.</para>
/// <para>Memory (design 4.8): every connector call holds the request's <see cref="RequestMemory"/> until the connector
/// really completes, even when the request stopped waiting for it at the source timeout or deadline; the background
/// context fetch holds it until its last staging read and derived computation. Buffers therefore never return to the
/// pool while anything may still write or read them.</para>
/// </remarks>
public sealed class FeatureFetcher
{
    private static readonly Task<FetchStatus> NoTimeLeft =
        Task.FromResult(FetchStatus.Failed("no time left before the request deadline"));

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
        var closure = Select(_plan.Closure(features), context: true);
        if (closure.Count == 0)
        {
            _contextTask = Task.CompletedTask;
            return;
        }

        // The operation reads its staging buffers and evaluates derived features after its connectors complete, which
        // may be after the request has returned (a failed request, or one that never reads context features). Holding
        // the memory for the whole operation, not just for each connector call, keeps those reads off returned buffers.
        _memory.Hold();
        _contextTask = FetchContextAsync(closure, cancellationToken);
        TrackIo(_contextTask);
    }

    /// <summary>
    /// Starts wave-0 fetches for features the next segment needs (called after each fetch barrier, design 5.4). Only
    /// the listed features (and their dependencies) are fetched, not every member of their groups (design 6.8).
    /// </summary>
    public void Prefetch(IEnumerable<int> features, CancellationToken cancellationToken)
    {
        var closure = _plan.Closure(features);
        var member = Membership(closure);
        foreach (var group in GroupsOf(closure, wave: 0))
        {
            // A group keyed by a feature that is not available yet (e.g. a derived key computed by EnsureAsync) would
            // find no keys now; EnsureAsync fetches it once the key exists.
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

        var closure = _plan.Closure(features);
        var candidate = Select(closure, context: false);
        if (_contextTask is not null && candidate.Count != closure.Count)
        {
            await _contextTask.ConfigureAwait(false);
        }

        var member = Membership(candidate);
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

            // Then anything still unfilled (rows appended since, fields not prefetched, or never prefetched).
            List<PendingFetch>? started = null;
            foreach (var group in GroupsOf(candidate, wave))
            {
                if (FeaturesToFetch(group, member, skipInFlight: false) is { } fetch && !FailIfKeyFailed(group, fetch))
                {
                    (started ??= new List<PendingFetch>()).Add(Start(group, fetch, cancellationToken));
                }
            }

            if (started is not null)
            {
                foreach (var pending in started)
                {
                    await CompleteAsync(pending).ConfigureAwait(false);
                }
            }

            ComputeDerivedOfWave(wave, member);
        }

        var worst = FeatureState.Ok;
        string? error = null;
        foreach (var f in closure)
        {
            if (_state[f] > worst)
            {
                worst = _state[f];
                error = _errors[f];
            }
        }

        return new FeatureFillResult(worst, error);
    }

    private List<int> Select(List<int> features, bool context)
    {
        var result = new List<int>(features.Count);
        foreach (var f in features)
        {
            if (_plan.Features[f].IsContext == context)
            {
                result.Add(f);
            }
        }

        return result;
    }

    private bool[] Membership(List<int> features)
    {
        var member = new bool[_plan.Features.Count];
        foreach (var f in features)
        {
            member[f] = true;
        }

        return member;
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

    /// <summary>Distinct candidate fetch groups of the given features in one wave, in group order.</summary>
    private List<FetchGroup> GroupsOf(List<int> features, int wave)
    {
        var seen = new bool[_plan.Groups.Count];
        var result = new List<FetchGroup>();
        foreach (var f in features)
        {
            var info = _plan.Features[f];
            if (info.IsDerived || info.IsContext || info.Wave != wave || info.GroupIndex < 0 || seen[info.GroupIndex])
            {
                continue;
            }

            seen[info.GroupIndex] = true;
            result.Add(_plan.Groups[info.GroupIndex]);
        }

        return result;
    }

    /// <summary>
    /// The members of <paramref name="group"/> this request needs now and does not have yet (late materialization,
    /// design 5.4/6.8): requested (directly or as a dependency), not failed, and unfilled on some live row — optionally
    /// excluding members an in-flight fetch already carries. Returned in group order; <c>null</c> when none.
    /// </summary>
    private int[]? FeaturesToFetch(FetchGroup group, bool[] member, bool skipInFlight)
    {
        List<int>? selected = null;
        foreach (var f in group.FeatureIndices)
        {
            if (member[f] && _state[f] != FeatureState.Failed && !_table.IsFilledForLiveRows(_plan.Features[f].Slot)
                && !(skipInFlight && InFlight(group, f)))
            {
                (selected ??= new List<int>(group.FeatureIndices.Length)).Add(f);
            }
        }

        if (selected is null)
        {
            return null;
        }

        return selected.Count == group.FeatureIndices.Length ? group.FeatureIndices : selected.ToArray();
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
    private bool FailIfKeyFailed(FetchGroup group, int[] features)
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
        // request, but each call holds the memory from before FetchAsync until the connector really completes
        // (CallAsync/IoLease), so the keys cannot return to the pool while a connector can still read them.
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

        // Each max_batch chunk is an independent call with its own staging buffer: chunks run concurrently, and a
        // buffer's bitmaps, string dictionary and list arena are not safe for concurrent writers.
        var chunkSize = group.MaxBatch;
        var chunkCount = (keyCount + chunkSize - 1) / chunkSize;
        if (chunkCount == 0)
        {
            return new PendingFetch(group, features, index, chunkSize, 0, Array.Empty<StagingBuffer?>(), Array.Empty<Task<FetchStatus>>(), Array.Empty<bool>(), noContextKey);
        }

        var all = keys!;
        var chunks = _memory.Rent<StagingBuffer?>(chunkCount);
        var calls = _memory.Rent<Task<FetchStatus>>(chunkCount);
        var chunkFailed = _memory.RentCleared<bool>(chunkCount);
        var budget = BudgetMs(group);
        for (var c = 0; c < chunkCount; c++)
        {
            var offset = c * chunkSize;
            var count = Math.Min(chunkSize, keyCount - offset);
            if (budget <= 0)
            {
                chunks[c] = null;
                calls[c] = NoTimeLeft;
                RecordUnsent(group, group.Shape, contextKey, all.AsMemory(offset, count), fields);
                continue;
            }

            var staging = new StagingBuffer(fields, count, _memory);
            chunks[c] = staging;
            var batch = new EntityKeyBatch { Shape = group.Shape, ContextKey = contextKey, CandidateKeys = all.AsMemory(offset, count) };
            calls[c] = CallAsync(group, batch, fields, staging, budget, cancellationToken);
        }

        return new PendingFetch(group, features, index, chunkSize, chunkCount, chunks, calls, chunkFailed, noContextKey);
    }

    /// <summary>
    /// Runs one connector call, waiting for it at most <paramref name="budgetMs"/> — the source timeout or the time
    /// left before the request deadline, whichever is less. Cancellation is only a request to the connector, so the
    /// wait itself is bounded: a result arriving later is rejected as a timeout and its staging is never read.
    /// </summary>
    /// <remarks>
    /// The request memory stays held until the connector task really completes (released by a continuation, not by
    /// this method), because a connector that ignores cancellation may still be writing into
    /// <paramref name="writer"/> (design 4.8). Never throws: failures become <see cref="FetchOutcome.Failed"/>.
    /// </remarks>
    private Task<FetchStatus> CallAsync(
        FetchGroup group, EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, CancellationToken cancellationToken)
    {
        if (_hook is null)
        {
            return ConnectorCallAsync(group, batch, fields, writer, budgetMs, 0, cancellationToken);
        }

        // A replayed call answers at once from the recording: no connector, no wait, no metrics or health (design 13.4).
        return _hook.Replays
            ? Task.FromResult(_hook.Replay(group, batch, fields, writer))
            : RecordedCallAsync(group, batch, fields, writer, budgetMs, cancellationToken);
    }

    /// <summary>
    /// A connector call of a recorded request. The extra hold keeps the keys and the staging buffer rented until the call
    /// is recorded: a prefetch nobody awaits may finish after the request returned, when its lease is already released.
    /// </summary>
    private async Task<FetchStatus> RecordedCallAsync(
        FetchGroup group, EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, CancellationToken cancellationToken)
    {
        var sequence = _hook!.NextSequence();
        _memory.Hold();
        try
        {
            return await ConnectorCallAsync(group, batch, fields, writer, budgetMs, sequence, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _memory.Release();
        }
    }

    /// <summary>Records a chunk the request had no time left to send, so a replay fails it the same way.</summary>
    private void RecordUnsent(FetchGroup group, KeyShape shape, EntityKey contextKey, ReadOnlyMemory<EntityKey> keys, CompiledFieldBinding[] fields)
    {
        if (_hook is { Replays: false } recorder)
        {
            var batch = new EntityKeyBatch { Shape = shape, ContextKey = contextKey, CandidateKeys = keys };
            recorder.Record(new FeatureCallInfo(group, batch, fields, recorder.NextSequence(), false, NoTimeLeft.Result, IoOutcome.Timeout, TimeSpan.Zero), null);
        }
    }

    private async Task<FetchStatus> ConnectorCallAsync(
        FetchGroup group, EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, int sequence, CancellationToken cancellationToken)
    {
        _memory.Hold();
        var io = group.Io.Start();
        var lease = new IoLease(_memory, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        lease.Cancellation.CancelAfter(budgetMs);
        ValueTask<FetchStatus> call;
        try
        {
            call = group.Connector.FetchAsync(batch, fields, writer, lease.Cancellation.Token);
        }
        catch (Exception ex)
        {
            lease.Dispose(); // threw before returning a task: the connector is no longer writing
            return Completed(io, group, batch, fields, writer, sequence, Failure(group, ex, budgetMs, cancellationToken), Classify(ex, cancellationToken));
        }

        if (call.IsCompleted)
        {
            try
            {
                return Completed(io, group, batch, fields, writer, sequence, call.GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                return Completed(io, group, batch, fields, writer, sequence, Failure(group, ex, budgetMs, cancellationToken), Classify(ex, cancellationToken));
            }
            finally
            {
                lease.Dispose();
            }
        }

        var task = call.AsTask();
        TrackIo(task.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // observe a late failure nobody awaits any more
                ((IoLease)state!).Dispose();
            },
            lease,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default));

        try
        {
            var status = await task.WaitAsync(TimeSpan.FromMilliseconds(budgetMs), cancellationToken).ConfigureAwait(false);
            return Completed(io, group, batch, fields, writer, sequence, status);
        }
        catch (Exception ex)
        {
            return Completed(io, group, batch, fields, writer, sequence, Failure(group, ex, budgetMs, cancellationToken), Classify(ex, cancellationToken));
        }
    }

    /// <summary>
    /// Records a finished call (metrics, source health, span; and the call itself when the request records) and passes its
    /// status through. Staged values are recorded only for a call that answered within its budget.
    /// </summary>
    private FetchStatus Completed(
        in IoCall io, FetchGroup group, EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int sequence, FetchStatus status,
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
        _hook?.Record(
            new FeatureCallInfo(group, batch, fields, sequence, true, status, result, duration),
            result is IoOutcome.Ok or IoOutcome.Partial ? writer : null);
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
        TimeoutException => FetchStatus.Failed($"source '{group.SourceName}' timed out after {budgetMs} ms"),
        OperationCanceledException when requestToken.IsCancellationRequested => FetchStatus.Failed($"source '{group.SourceName}' was cancelled"),
        OperationCanceledException => FetchStatus.Failed($"source '{group.SourceName}' timed out after {budgetMs} ms"),
        _ => FetchStatus.Failed($"source '{group.SourceName}' failed: {ex.Message}"),
    };

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

        var keyState = group.KeyFeatureIndex >= 0 ? _state[group.KeyFeatureIndex] : FeatureState.Ok;
        if (keyState is FeatureState.Failed or FeatureState.Degraded)
        {
            error ??= _errors[group.KeyFeatureIndex] ?? $"key feature '{_plan.Features[group.KeyFeatureIndex].Definition.Name}' is {keyState}";
        }

        if ((failedChunks > 0 && failedChunks == pending.ChunkCount) || keyState == FeatureState.Failed)
        {
            foreach (var f in pending.Features)
            {
                _state[f] = FeatureState.Failed;
                _errors[f] = error;
            }

            return;
        }

        // Some chunks failed: the source partially failed (design 6.7) and readers become Degraded. The failed chunks'
        // keys are treated like keys absent from the response; their staging is never read.
        var degraded = failedChunks > 0 || partial || keyState == FeatureState.Degraded;
        for (var i = 0; i < pending.Features.Length; i++)
        {
            var f = pending.Features[i];
            if (_state[f] == FeatureState.Failed)
            {
                continue;
            }

            Backfill(pending, _plan.Features[f], i);
            if (degraded)
            {
                _state[f] = FeatureState.Degraded;
                _errors[f] = error;
            }
            else if (_state[f] == FeatureState.Unrequested)
            {
                _state[f] = FeatureState.Ok;
            }
        }
    }

    /// <summary>
    /// Copies staged values into the live rows that are still unfilled, matching by key (design 4.5). Key index k lives
    /// in chunk k / max_batch at local row k % max_batch. A value is read only if its chunk succeeded and the connector
    /// wrote that field for that key; anything else takes the default or becomes missing.
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

            StagingBuffer? staging = null;
            var local = 0;
            if (keyIndex >= 0)
            {
                var chunk = keyIndex / pending.ChunkSize;
                local = keyIndex - (chunk * pending.ChunkSize);
                staging = pending.ChunkFailed[chunk] ? null : pending.Chunks[chunk];
            }

            if (staging is not null && staging.HasValue(fieldIndex, local))
            {
                column.CopyFrom(staging.Field(fieldIndex), local, row);
                column.SetValid(row);
                copied[copiedCount++] = row;
            }
            else if (definition.HasDefault)
            {
                ColumnValues.Set(column, row, definition.Default);
            }
            else
            {
                column.SetMissing(row);
            }

            Bits.Set(filled, row);
        }

        if (copiedCount > 0 && ColumnValues.IsFloat(column))
        {
            AddNonFinite(feature, ColumnValues.SanitizeNonFinite(column, copied.AsSpan(0, copiedCount), DefaultOrMissing(definition)));
        }
    }

    private void ComputeDerivedOfWave(int wave, bool[] member)
    {
        foreach (var f in _plan.DerivedOrder)
        {
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
            feature.Expression!.Evaluate(new EvalContext(_table, _bindings, _context, _params, _counters), span, column, DefaultOrMissing(definition));
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
            var replaced = ColumnValues.SanitizeNonFinite(column, span, DefaultOrMissing(definition));
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

    private async Task FetchContextAsync(List<int> closure, CancellationToken cancellationToken)
    {
        try
        {
            var member = Membership(closure);
            var seenGroup = new bool[_plan.Groups.Count];
            var fetches = new List<ContextFetch>();
            foreach (var f in closure)
            {
                var info = _plan.Features[f];
                if (info.IsDerived || info.GroupIndex < 0 || seenGroup[info.GroupIndex])
                {
                    continue;
                }

                seenGroup[info.GroupIndex] = true;
                var group = _plan.Groups[info.GroupIndex];
                var features = RequestedMembers(group, member);
                var v = group.ContextKeyId >= 0 ? _context[group.ContextKeyId] : ScalarValue.Missing;
                if (v.IsMissing)
                {
                    fetches.Add(new ContextFetch(features, null, Task.FromResult(FetchStatus.Ok)));
                    continue;
                }

                var fields = FieldBindings(group, features);
                var budget = BudgetMs(group);
                if (budget <= 0)
                {
                    fetches.Add(new ContextFetch(features, null, NoTimeLeft));
                    RecordUnsent(group, group.Shape, ToKey(v), default, fields);
                    continue;
                }

                var staging = new StagingBuffer(fields, 1, _memory);
                var batch = new EntityKeyBatch { Shape = group.Shape, ContextKey = ToKey(v) };
                fetches.Add(new ContextFetch(features, staging, CallAsync(group, batch, fields, staging, budget, cancellationToken)));
            }

            foreach (var fetch in fetches)
            {
                var status = await fetch.Task.ConfigureAwait(false);
                for (var i = 0; i < fetch.Features.Length; i++)
                {
                    var f = fetch.Features[i];
                    if (status.Outcome == FetchOutcome.Failed)
                    {
                        _state[f] = FeatureState.Failed;
                        _errors[f] = status.Error;
                        continue;
                    }

                    var value = fetch.Staging is not null && fetch.Staging.HasValue(i, 0)
                        ? ColumnValues.Get(fetch.Staging.Field(i), 0)
                        : ScalarValue.Missing;
                    var partial = status.Outcome == FetchOutcome.Partial;
                    _state[f] = partial ? FeatureState.Degraded : FeatureState.Ok;
                    _errors[f] = partial ? status.Error : null;
                    StoreContext(_plan.Features[f], value);
                }
            }

            foreach (var f in _plan.DerivedOrder)
            {
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

        if (value.IsMissing && definition.HasDefault)
        {
            value = definition.Default;
        }

        _context[info.ContextId] = value;
    }

    private static ScalarValue DefaultOrMissing(FeatureDefinition definition) =>
        definition.HasDefault ? definition.Default : ScalarValue.Missing;

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

    private bool NeedsFill(int[] features, int row)
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

    private int BudgetMs(FetchGroup group) => Math.Min(group.TimeoutMs, RemainingMs());

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
        StagingBuffer?[] chunks,
        Task<FetchStatus>[] calls,
        bool[] chunkFailed,
        bool noContextKey)
    {
        public FetchGroup Group { get; } = group;

        /// <summary>Fetched features; staging field i holds <c>Features[i]</c>.</summary>
        public int[] Features { get; } = features;

        /// <summary>
        /// Key → key index; key index k is row k % ChunkSize of chunk k / ChunkSize. Null when no live row had a key.
        /// </summary>
        public EntityKeyIndex? Index { get; } = index;

        public int ChunkSize { get; } = chunkSize;

        /// <summary>Number of chunks; the chunk arrays below are valid up to this length.</summary>
        public int ChunkCount { get; } = chunkCount;

        /// <summary>Staging per chunk; null when the chunk was never sent (no time left).</summary>
        public StagingBuffer?[] Chunks { get; } = chunks;

        public Task<FetchStatus>[] Calls { get; } = calls;

        /// <summary>Set on completion: chunks whose staging must not be read.</summary>
        public bool[] ChunkFailed { get; } = chunkFailed;

        public bool NoContextKey { get; } = noContextKey;
    }

    private sealed record ContextFetch(int[] Features, StagingBuffer? Staging, Task<FetchStatus> Task);

    /// <summary>What a connector call holds until it really completes: the request memory and its cancellation source.</summary>
    private sealed class IoLease(RequestMemory memory, CancellationTokenSource cancellation)
    {
        private int _disposed;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Cancellation.Dispose();
                memory.Release();
            }
        }
    }
}
