using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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
using StrataRec.Features.Local;

namespace StrataRec.Features.Runtime;

/// <summary>Per-request availability of a feature.</summary>
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
/// Request-scoped feature enrichment. Fetch results land in key-ordered staging buffers and
/// are backfilled into whatever rows are live <em>when a reader needs them</em>, so filters and Compact during in-flight
/// I/O never misplace values. Local groups are read synchronously from their tables when a reader needs
/// them (<c>FeatureFetcher.Local.cs</c>); cache groups the same way, with the keys the table does not have fresh resolved
/// through the distributed tier and the connector (<c>FeatureFetcher.Cache.cs</c>).
/// </summary>
/// <remarks>
/// <para>Not thread-safe; driven by the request's executor. Connectors and the context prefetch run concurrently
/// underneath.</para>
/// <para>Memory: every connector call holds the request's <see cref="RequestMemory"/> until the connector
/// really completes, even when the request stopped waiting for it at the source timeout or deadline; the background
/// context fetch holds it until its last group is stored and its last derived feature computed
/// (<c>FeatureFetcher.Context.cs</c>). Buffers therefore never return to the pool while anything may still write or read
/// them.</para>
/// <para>Allocation: a fetch is split into <c>max_batch</c> chunks, often hundreds per request, so a
/// chunk whose connector answers synchronously allocates nothing: its keys are a struct, its staging is a window of the
/// fetch's <see cref="StagingArea"/> seen through a reused view, its cancellation is the fetch's, and its status comes
/// back in a <see cref="ValueTask{TResult}"/>. Only a call that really goes asynchronous allocates what waiting needs.</para>
/// </remarks>
public sealed partial class FeatureFetcher : IIoTracker
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
    private readonly long? _seed;
    private readonly RequestEventLog? _events;
    private readonly FeatureState[] _state;
    private readonly string?[] _errors;
    private readonly List<PendingFetch> _pending = new();
    private readonly List<Task> _io = new(); // guarded by lock (_io): connector calls, cache reads' resolutions, the context fetch
    private readonly ExpressionCounters _counters;
    private readonly IFeatureCallHook? _hook;
    private int _nonFiniteReplaced;

    /// <summary>What the cached reads of the request's runs share (<see cref="RequestReads"/>); null for a request of one run.</summary>
    internal RequestReads? Reads { get; init; }

    /// <param name="counters">The request's expression counters, shared with the operators' evaluations so a request
    /// reports one total per event; null gives the fetcher counters of its own.</param>
    /// <param name="requestId">Sample request id for failure summaries.</param>
    /// <param name="events">Source health and failure summaries; null when the engine has no logger.</param>
    /// <param name="hook">Records every connector call, or answers them from a recording; null for ordinary requests.</param>
    /// <param name="seed">The request's seed, which a source in mode service sends with its call, as a node does.</param>
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
        IFeatureCallHook? hook = null,
        long? seed = null)
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
        _seed = seed;
        _events = events;
        _hook = hook;
        _state = new FeatureState[plan.Features.Count];
        _errors = new string?[plan.Features.Count];
        if (plan.HasTables && hook is not { Replays: true })
        {
            _localKeys = FetchKeys.Prepared(memory, plan.TableTupleParts); // reset by each local read, so no read costs an object
        }
    }

    /// <summary>
    /// Number of feature values (physical or derived, candidate or context) that were NaN/±∞ and were replaced by the
    /// feature's default or marked missing (metric <c>column_nonfinite_total</c>).
    /// </summary>
    public int NonFiniteReplaced => Volatile.Read(ref _nonFiniteReplaced);

    /// <summary>
    /// Counters of derived-feature evaluations. <see cref="ExpressionCounters.OutOfRange"/> includes derived values
    /// (candidate or context) that did not fit an <c>int32</c> or <c>list&lt;int32&gt;</c> feature and were replaced by its
    /// default or marked missing (metric <c>column_out_of_range_total</c>).
    /// </summary>
    public ExpressionCounters Counters => _counters;

    public FeatureState GetState(int feature) => _state[feature];

    /// <summary>
    /// Completes when every external operation this request started has really finished: each connector or store call
    /// that did not answer synchronously (also one whose result was rejected because it arrived after its time budget),
    /// both its connector and its call site (which records it), each cache read's resolution of its misses, and the whole
    /// background context fetch. Each releases its hold on the request memory just before it counts as finished.
    /// </summary>
    /// <remarks>
    /// The set of operations can grow while this waits: a chained context group starts where its key was stored, on a
    /// connector's thread, and registers its call then, possibly after this was called; a cache read sends its
    /// origin calls once its distributed tier answered, possibly after the request stopped reading it. Each
    /// registers before its parent, which is tracked (the context fetch, the read's resolution), completes, so a wait
    /// that finds nothing registered since it began has seen everything: this waits round after round until one finds
    /// nothing new, and every round to its end before it rethrows the first failure.
    /// </remarks>
    public async Task WhenIoCompleteAsync()
    {
        var seen = 0;
        ExceptionDispatchInfo? first = null;
        while (true)
        {
            Task[] round;
            lock (_io)
            {
                if (_io.Count == seen)
                {
                    break;
                }

                round = _io.GetRange(seen, _io.Count - seen).ToArray();
                seen = _io.Count;
            }

            try
            {
                await Task.WhenAll(round).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                first ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        first?.Throw();
    }

    /// <summary>
    /// Starts wave-0 fetches for features the next segment needs (called after each fetch barrier). Only
    /// the listed features (and their dependencies) are fetched, not every member of their groups.
    /// </summary>
    public void Prefetch(ReadOnlySpan<int> features, CancellationToken cancellationToken)
    {
        var closure = Closure(features, out var member, scratch: true);
        var groups = GroupsOf(closure, wave: 0);
        try
        {
            for (var i = 0; i < groups.Count; i++)
            {
                // A group keyed by a feature that is not available yet (e.g. a derived key computed by EnsureAsync) would
                // find no keys now; EnsureAsync fetches it once the key exists.
                var group = _plan.Groups[groups.Items[i]];
                if (!Prefetches(group))
                {
                    continue;
                }

                if (!KeyFeaturesFilled(group))
                {
                    continue;
                }

                if (FeaturesToFetch(group, member, skipInFlight: true, RowScope.Live) is { } fetch)
                {
                    _pending.Add(Start(group, fetch, RowScope.Live, cancellationToken));
                }
            }
        }
        finally
        {
            // Nothing a started fetch keeps refers to them: it has its own feature array, keys and staging.
            _memory.ReturnScratch(groups.Items);
            ReturnClosure(closure, member);
        }
    }

    /// <summary>
    /// Ensures every listed feature is filled for every live row (correctness rule). Works through the
    /// dependency graph wave by wave: derived features needing no fetch first; then, per wave, in-flight
    /// fetches, fetches for whatever is still missing, and the derived features whose inputs are now complete — so a
    /// derived foreign key exists before the groups keyed by it are fetched.
    /// </summary>
    /// <remarks>The features are read before this returns (a span: a node's read features, a list of the request's), so
    /// the caller may reuse their storage while the fill is awaited.</remarks>
    public ValueTask<FeatureFillResult> EnsureAsync(ReadOnlySpan<int> features, CancellationToken cancellationToken)
    {
        if (features.IsEmpty)
        {
            return new ValueTask<FeatureFillResult>(new FeatureFillResult(FeatureState.Ok));
        }

        var closure = Closure(features, out var member, scratch: true);
        return EnsureClosureAsync(closure, member, RowScope.Live, cancellationToken);
    }

    /// <summary>
    /// Ensures every listed feature is filled for <paramref name="rows"/> only: distinct live physical rows, the output's
    /// returned rows, known once the final order is, when no reader is left for the other live rows. The waves
    /// of <see cref="EnsureAsync(ReadOnlySpan{int}, CancellationToken)"/>, but a fetch or a table read takes the keys of these
    /// rows, a derived feature is computed for them, and only they are marked filled, so a later whole fill fills the
    /// rest. A fetch already in flight (a prefetch) that asked for the key of one of these rows still unfilled on a needed
    /// feature is completed for every live row it covers, as for any reader; any other is not waited for. With no rows
    /// nothing is filled or waited for, context features included: no row reads them.
    /// </summary>
    /// <remarks>
    /// <para>The state, one per feature for the request, is recorded by the rules of a whole fill applied to the rows this
    /// fill fills, and a failure is never lowered. So a row-scoped fill cannot mislead a later whole fill: a failure it
    /// records is one on live rows, which a whole fill covers as well (a value they cannot hold, a source that failed for
    /// every key it was asked, as for rows appended after a fill); an ok or degraded state it records covers its rows alone,
    /// and the rows it leaves unfilled are filled, and their failure recorded, by the next fill that needs them. The state
    /// returned is the features' state for the request, so one that failed in an earlier fill fails here too.</para>
    /// <para>A prefetch these rows need nothing from is left in flight, settled as an unread one when the request ends (or
    /// completed by a later whole fill). Waiting for one that failed for all its keys set the feature failed and failed
    /// the output: one started for rows not returned (a fallback's declared reads, say) when the returned rows were filled
    /// already, or when their keys were not among its keys, the rows having been appended after it started,
    /// so that the connector was never asked for them. Need is judged by key, as the prefetch's backfill
    /// matches rows: a row of these still unfilled on a feature it carries, whose key is one of its keys. One such row is
    /// enough: the prefetch is completed, its failure recorded, as before, and the rows whose keys it lacks are fetched
    /// by this fill.</para>
    /// <para>The features and rows are read before this returns, so the caller may reuse their storage.</para>
    /// </remarks>
    public ValueTask<FeatureFillResult> EnsureAsync(ReadOnlySpan<int> features, ReadOnlySpan<int> rows, CancellationToken cancellationToken)
    {
        if (features.IsEmpty || rows.IsEmpty)
        {
            return new ValueTask<FeatureFillResult>(new FeatureFillResult(FeatureState.Ok));
        }

        var closure = Closure(features, out var member, scratch: true);
        var scoped = _memory.RentScratch<int>(rows.Length);
        rows.CopyTo(scoped);
        return EnsureClosureAsync(closure, member, new RowScope(scoped, rows.Length), cancellationToken);
    }

    /// <summary>
    /// The fill of both <see cref="EnsureAsync(ReadOnlySpan{int}, CancellationToken)"/> overloads, wave by wave.
    /// It owns the fill's temporaries, scratch buffers each given back as soon as nothing refers to them: the
    /// closure, its marks and a row-scoped fill's rows when it ends, however it ends; a wave's groups and its started
    /// fetches at the wave's end. What a started fetch keeps (its feature array, keys, staging and calls) is its own, on
    /// the request's ledger, since its connector calls may outlive the fill. One method, so a fill that waits costs one
    /// state machine, not one per wave.
    /// </summary>
    /// <param name="closure">Scratch, from <see cref="Closure"/>; given back here.</param>
    /// <param name="member">Scratch, from <see cref="Closure"/>; given back here.</param>
    /// <param name="scope">The rows to fill: every live row, or a row-scoped fill's scratch copy of its rows, given back here.</param>
    private async ValueTask<FeatureFillResult> EnsureClosureAsync(IndexList closure, bool[] member, RowScope scope, CancellationToken cancellationToken)
    {
        try
        {
            if (_contextTask is not null && AnyContext(closure))
            {
                await ContextReadyAsync(closure).ConfigureAwait(false);
            }

            // member marks the whole closure, context features included: every lookup below is of a candidate feature.
            ComputeDerivedOfWave(-1, member, scope);
            for (var wave = 0; wave <= _plan.MaxWave; wave++)
            {
                // In-flight (prefetched) fetches carrying a needed feature first; their keys may cover most rows. A
                // row-scoped fill takes only one that asked for the key of a row it still has to fill: any other was
                // started for other rows, and nothing this fill reads comes from it (see the remarks of the row-scoped
                // overload).
                for (var i = 0; i < _pending.Count; i++)
                {
                    var pending = _pending[i];
                    if (pending.Group.Wave == wave && Intersects(pending.Features, member)
                        && (scope.Rows is null || CoversAnUnfilledRow(pending, member, scope)))
                    {
                        _pending.RemoveAt(i--);
                        await CompleteAsync(pending).ConfigureAwait(false);
                    }
                }

                // Then anything still unfilled (rows appended since, fields not prefetched, or never prefetched): fetches
                // first, then the local and cache reads, which run while the fetches are in flight.
                var groups = GroupsOf(closure, wave);
                PendingFetch[]? started = null;
                try
                {
                    var startedCount = 0;
                    var locals = false;
                    for (var g = 0; g < groups.Count; g++)
                    {
                        var group = _plan.Groups[groups.Items[g]];
                        if (ReadsLocally(group))
                        {
                            locals = true;
                        }
                        else if (FeaturesToFetch(group, member, skipInFlight: false, scope) is { } fetch && !FailIfKeyFailed(group, fetch))
                        {
                            started ??= _memory.RentScratch<PendingFetch>(groups.Count);
                            started[startedCount++] = Start(group, fetch, scope, cancellationToken);
                        }
                    }

                    if (locals)
                    {
                        ReadLocalGroups(groups, member, scope, cancellationToken);
                    }

                    for (var s = 0; s < startedCount; s++)
                    {
                        await CompleteAsync(started![s]).ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Each fetch is completed above; one an exception left behind holds the request memory through its
                    // calls, not through this array (cleared as it is given back).
                    if (started is not null)
                    {
                        _memory.ReturnScratch(started);
                    }

                    _memory.ReturnScratch(groups.Items);
                }

                // The cache reads that missed resolve their misses meanwhile (the distributed tier, then the connector).
                while (TakeCacheRead() is { } read)
                {
                    await CompleteCacheReadAsync(read).ConfigureAwait(false);
                }

                ComputeDerivedOfWave(wave, member, scope);
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
        finally
        {
            ReturnClosure(closure, member);
            if (scope.Rows is { } rows)
            {
                _memory.ReturnScratch(rows);
            }
        }
    }

    /// <summary>
    /// The features plus everything they depend on, in dependency order (<see cref="FeaturePlan.Closure(IEnumerable{int})"/>),
    /// in arrays from the request memory: an ensure runs for every node, and its closure was a set, a list and a boxed
    /// enumerator each time. <paramref name="member"/> marks the closure by feature index.
    /// </summary>
    /// <param name="scratch">True for a fill or a prefetch, which give both back (<see cref="ReturnClosure"/>) once they are
    /// done; false for the context prefetch, which reads them on after it has returned, so they stay on the ledger.</param>
    private IndexList Closure(ReadOnlySpan<int> features, out bool[] member, bool scratch)
    {
        var length = Math.Max(1, _plan.Features.Count);
        var order = scratch ? _memory.RentScratch<int>(length) : _memory.Rent<int>(length);
        member = scratch ? _memory.RentScratch<bool>(length) : _memory.Rent<bool>(length);
        Array.Clear(member, 0, length);
        return new IndexList(order, _plan.Closure(features, order, member));
    }

    /// <summary>Gives back a scratch <see cref="Closure"/>.</summary>
    private void ReturnClosure(IndexList closure, bool[] member)
    {
        _memory.ReturnScratch(closure.Items);
        _memory.ReturnScratch(member);
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

    /// <summary>
    /// Whether a row of <paramref name="scope"/> needs what <paramref name="pending"/> fetches: a feature of it in
    /// <paramref name="member"/> is unfilled on the row, and the row's key is one of the fetch's keys. The key
    /// is looked up as the fetch's backfill looks it up (<see cref="FetchKeyOf"/>: its varying parts, composite or read from
    /// a key feature this fill filled in an earlier wave; the shared parts are the request's, the same for every fetch),
    /// so a fetch this finds no row for would have filled none of them. Allocates nothing.
    /// </summary>
    private bool CoversAnUnfilledRow(PendingFetch pending, bool[] member, RowScope scope)
    {
        foreach (var row in RowsOf(scope))
        {
            if (AnyUnfilled(pending.Features, member, row) && FetchKeyOf(pending, row) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a feature of <paramref name="features"/> in <paramref name="member"/> is unfilled on <paramref name="row"/>.</summary>
    private bool AnyUnfilled(int[] features, bool[] member, int row)
    {
        foreach (var f in features)
        {
            if (member[f] && !Bits.Get(_table.GetFilled(_plan.Features[f].Slot), row))
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

    /// <summary>
    /// Indices of the distinct candidate fetch groups of the given features in one wave, in group order, in a scratch
    /// array the caller gives back once it has started their fetches and reads.
    /// </summary>
    private IndexList GroupsOf(IndexList features, int wave)
    {
        var length = Math.Max(1, _plan.Groups.Count);
        var seen = _memory.RentScratch<bool>(length);
        Array.Clear(seen, 0, length);
        var result = _memory.RentScratch<int>(length);
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

        _memory.ReturnScratch(seen);
        return new IndexList(result, count);
    }

    /// <summary>
    /// The members of <paramref name="group"/> this request needs now and does not have yet (late materialization): requested (directly or as a dependency), not failed, and unfilled on some row of
    /// <paramref name="scope"/> — optionally excluding members an in-flight fetch already carries. Returned in group order;
    /// <c>null</c> when none.
    /// </summary>
    private int[]? FeaturesToFetch(FetchGroup group, bool[] member, bool skipInFlight, RowScope scope)
    {
        var members = group.FeatureIndexArray;
        if (members.Length <= 128)
        {
            Span<int> selected = stackalloc int[members.Length];
            return Selected(members, selected, SelectNeeded(group, member, skipInFlight, scope, selected));
        }

        var rented = _memory.RentScratch<int>(members.Length);
        try
        {
            return Selected(members, rented, SelectNeeded(group, member, skipInFlight, scope, rented));
        }
        finally
        {
            _memory.ReturnScratch(rented);
        }

        // The fetch keeps an array of its own (all the members, or a copy of the selection): it outlives this call.
        static int[]? Selected(int[] members, ReadOnlySpan<int> selected, int count) =>
            count == 0 ? null : count == members.Length ? members : selected[..count].ToArray();
    }

    /// <summary>
    /// Writes the members of <paramref name="group"/> this request needs now and does not have yet into
    /// <paramref name="selected"/> (at least as long as the group), in group order; returns how many. The one rule of
    /// what a fetch or a local read supplies (late materialization).
    /// </summary>
    private int SelectNeeded(FetchGroup group, bool[] member, bool skipInFlight, RowScope scope, Span<int> selected)
    {
        var count = 0;
        foreach (var f in group.FeatureIndices)
        {
            if (member[f] && _state[f] != FeatureState.Failed && !IsFilled(_plan.Features[f].Slot, scope)
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
        if (features == group.FeatureIndexArray)
        {
            return group.FieldArray;
        }

        var fields = new CompiledFieldBinding[features.Length];
        for (var i = 0; i < features.Length; i++)
        {
            fields[i] = group.Fields[_plan.Features[features[i]].FieldIndex] with { FieldIndex = i };
        }

        return fields;
    }

    /// <summary>
    /// A group keyed by a failed feature cannot be fetched; its features fail too (a dependency failure
    /// never degrades into legitimate missing values).
    /// </summary>
    private bool FailIfKeyFailed(FetchGroup group, ReadOnlySpan<int> features)
    {
        if (KeyState(group, out var error) != FeatureState.Failed)
        {
            return false;
        }

        foreach (var f in features)
        {
            _state[f] = FeatureState.Failed;
            _errors[f] = error;
        }

        return true;
    }

    /// <summary>
    /// The worst state of the group's key features (the key's state is its parts'), and the error of the first
    /// key feature in it; <see cref="FeatureState.Ok"/> for a key that reads no feature.
    /// </summary>
    private FeatureState KeyState(FetchGroup group, out string? error)
    {
        var worst = FeatureState.Ok;
        error = null;
        foreach (var k in group.KeyFeatures)
        {
            var state = _state[k];
            if ((state == FeatureState.Failed && worst != FeatureState.Failed) || (state == FeatureState.Degraded && worst == FeatureState.Ok))
            {
                worst = state;
                error = _errors[k] ?? (state == FeatureState.Failed
                    ? $"key feature '{_plan.Features[k].Definition.Name}' failed"
                    : $"key feature '{_plan.Features[k].Definition.Name}' is {state}");
            }
        }

        return worst;
    }

    /// <summary>
    /// Whether every key feature of the group is available (a prefetch finds no keys otherwise): a candidate one filled for
    /// the live rows, a context one stored by the context prefetch (a prefetch does not wait; the group's reader does).
    /// </summary>
    private bool KeyFeaturesFilled(FetchGroup group)
    {
        foreach (var k in group.KeyFeatures)
        {
            var key = _plan.Features[k];
            if (key.IsContext ? !ContextStored(k) : !_table.IsFilledForLiveRows(key.Slot))
            {
                return false;
            }
        }

        return true;
    }

    private PendingFetch Start(FetchGroup group, int[] features, RowScope scope, CancellationToken cancellationToken)
    {
        var fields = FieldBindings(group, features);
        // Collect distinct keys of the scope's rows that still miss any of the fetched features. The index, the key array and
        // the per-chunk arrays are rented from the request memory, so a fetch allocates nothing per key.
        // Lifetimes: the index and the chunk arrays are read only on the request's thread (Backfill, CompleteAsync),
        // before the request releases the memory. The key array is read by connector calls, which may outlive the
        // request, but each call holds the memory from before FetchAsync until the connector really completes and the call
        // has been recorded (ConnectorCall, BoundedCall), so the keys cannot return to the pool while a connector or a
        // recording can still read them.
        // A key part that is a request input has one value for the whole fetch: a missing one leaves every row without a
        // key, so nothing is fetched and the features take their absent values.
        var index = default(FetchKeys);
        var keyCount = 0;
        var layout = group.KeyLayout;
        var sharedBuffer = default(KeyPartBuffer);
        Span<EntityKey> shared = sharedBuffer;
        var noSharedKey = !TrySharedKey(group, shared);
        if (!noSharedKey)
        {
            var live = RowsOf(scope);
            var varyingBuffer = default(KeyPartBuffer);
            Span<EntityKey> varying = varyingBuffer;
            varying = varying[..layout.VaryingCount];
            var one = group.OneVaryingFeature;
            foreach (var row in live)
            {
                EntityKey key = default;
                if (NeedsFill(features, row) && (one != int.MinValue ? PartKey(one, row, out key) : RowKey(group, row, varying)) == KeyLookup.Found)
                {
                    if (!index.IsSet)
                    {
                        // Sized to the rows, an upper bound on the distinct keys: the index never grows.
                        index.Reset(_memory, layout, shared[..(layout.PartCount - layout.VaryingCount)], live.Length, key);
                    }

                    _ = one != int.MinValue ? index.GetOrAdd(key) : index.GetOrAdd(varying);
                }
            }

            keyCount = index.IsSet ? index.Count : 0;
        }

        // Each max_batch chunk is an independent call, and chunks run concurrently. They share the fetch's staging area
        // (each writes only its own region of it, see StagingArea), its time budget and its cancellation source.
        var chunkSize = Batching.Size(keyCount, group.Supply.MaxBatch); // the fewest chunks, even
        var chunkCount = Batching.Count(keyCount, chunkSize);
        if (chunkCount == 0)
        {
            return new PendingFetch(group, features, scope, index, chunkSize, 0, null, Array.Empty<ValueTask<CallResult<FetchStatus>>>(), Array.Empty<bool>(), noSharedKey, 0);
        }

        var all = index.Batch(_memory);
        var calls = _memory.Rent<ValueTask<CallResult<FetchStatus>>>(chunkCount);
        var chunkFailed = _memory.RentCleared<bool>(chunkCount);
        var deadline = DeadlineOf(group, out var budget);
        if (budget <= 0)
        {
            for (var c = 0; c < chunkCount; c++)
            {
                calls[c] = Answered(NoTimeLeft);
                RecordUnsent(group, ChunkKeys(all, chunkSize, c), fields, NoTimeLeft, IoOutcome.Timeout);
            }

            return new PendingFetch(group, features, scope, index, chunkSize, chunkCount, null, calls, chunkFailed, noSharedKey, budget);
        }

        // The bound ends at the deadline taken with the budget, so neither the staging's setup nor the hook delays it
        // (set up first, and then made from the budget, the time before the bound
        // was added to it, and a chunk was sent past the deadline). The staging is set up inside the try: a pool that throws
        // leaves no bound behind, with its timer and its registration on the caller's token.
        var bound = _hook is { Replays: true } ? null : new CallBound(CancellationSite.Fetch, cancellationToken, deadline, _events, _requestId);
        StagingArea staging;
        StagingBuffer? writer = null;
        try
        {
            staging = new StagingArea(fields, keyCount, chunkSize, _memory);
            for (var c = 0; c < chunkCount; c++)
            {
                // A connector's synchronous part runs here, unbounded (see IFeatureSourceConnector.FetchAsync): once the
                // fetch's time is up or the caller has cancelled, the chunks not sent yet are not sent (each
                // was sent with no time left, its synchronous part run in turn). They fail as a late answer would,
                // recorded as not sent, so a replay fails them alike without a connector or a clock (BoundedCall, rule 8).
                writer ??= new StagingBuffer(staging);
                writer.Bind(c);
                calls[c] = CallAsync(group, ChunkKeys(all, chunkSize, c), fields, writer, budget, bound, out var writerHeld);
                if (writerHeld)
                {
                    writer = null; // the call runs on and may write through this view until it really completes
                }
            }
        }
        finally
        {
            bound?.Release(); // the fetch's own reference; every sent chunk holds one until its connector completes
        }

        return new PendingFetch(group, features, scope, index, chunkSize, chunkCount, staging, calls, chunkFailed, noSharedKey, budget);
    }

    private static EntityKeyBatch ChunkKeys(in EntityKeyBatch keys, int chunkSize, int chunk)
    {
        var offset = chunk * chunkSize;
        return keys.Slice(offset, Math.Min(chunkSize, keys.Count - offset));
    }

    /// <summary>
    /// Runs one connector call through the one I/O boundary (<see cref="BoundedCall"/>): waited for at most
    /// <paramref name="budgetMs"/>, the source timeout or the time left before the request deadline, whichever is less,
    /// counted from the start of the fetch (the bound every chunk of it shares, none with a fresh budget). The connector's
    /// token is the bound's. Cancellation is only a request to the connector, so the wait itself is bounded: a result
    /// arriving later is rejected as a timeout and its staging is never read. Replayed, the call is answered from the
    /// recording at once.
    /// </summary>
    /// <remarks>
    /// The call holds the request memory until the connector really completes, because a connector that ignores
    /// cancellation may still be writing into <paramref name="writer"/>, and until the call has been
    /// recorded, which reads the keys and the staging. Never throws for a connector failure: failures become
    /// <see cref="FetchOutcome.Failed"/> (<see cref="StatusOf"/>). The returned task is consumed exactly once, or never (a
    /// prefetch nobody awaits): it is a plain result or a task, never a pooled source.
    /// </remarks>
    /// <param name="bound">The fetch's bound; null only when the request replays.</param>
    /// <param name="writerHeld">True when the call is still running and owns <paramref name="writer"/>; otherwise the
    /// view may be pointed at the next chunk.</param>
    private ValueTask<CallResult<FetchStatus>> CallAsync(
        FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, CallBound? bound, out bool writerHeld)
    {
        if (_hook is { Replays: true } replay)
        {
            // A replayed call answers at once from the recording: no connector, no wait, no metrics or health.
            writerHeld = false;
            return Answered(replay.Replay(group, batch, ImmutableCollectionsMarshal.AsImmutableArray(fields), writer));
        }

        var sequence = _hook is { } recorder ? recorder.Begin(group, batch, ImmutableCollectionsMarshal.AsImmutableArray(fields)) : NotRecorded;
        return Send(group, batch, fields, writer, budgetMs, sequence, bound!, out writerHeld);
    }

    /// <summary>Sends one connector call; recorded with <paramref name="sequence"/> unless it is <see cref="NotRecorded"/>.</summary>
    private ValueTask<CallResult<FetchStatus>> Send(
        FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, int sequence, CallBound bound,
        out bool writerHeld)
    {
        var call = BoundedCall.RunAsync<FetchStatus, ConnectorCall>(
            new ConnectorCall(this, group, batch, fields, writer, budgetMs, sequence), bound, _memory, group.Io, CallDeadline.None);

        // A call whose wait ended at once (the caller had cancelled while the connector's task was pending) has abandoned a
        // connector that may still write through the view: it is not the next chunk's. The result is
        // a plain value or a task, so reading it here does not consume it.
        writerHeld = !call.IsCompletedSuccessfully || call.Result.Abandoned;
        return call;
    }

    /// <summary>Records a chunk the request had no time left to send, so a replay fails it the same way.</summary>
    private void RecordUnsent(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, FetchStatus status, IoOutcome outcome)
    {
        if (_hook is { Replays: false } recorder)
        {
            var shared = ImmutableCollectionsMarshal.AsImmutableArray(fields);
            recorder.Record(new FeatureCallInfo(group, batch, shared, recorder.Begin(group, batch, shared), false, status, outcome, TimeSpan.Zero), null);
        }
    }

    /// <summary>A status the fetch decided without a connector (no time left, a replay), as a call's result.</summary>
    private static ValueTask<CallResult<FetchStatus>> Answered(FetchStatus status) =>
        new(new CallResult<FetchStatus>(CallEnd.Answered, status, null, OutcomeOf(status), status.Error, false));

    private static IoOutcome OutcomeOf(in FetchStatus status) => status.Outcome switch
    {
        FetchOutcome.Failed => IoOutcome.Error,
        FetchOutcome.Partial => IoOutcome.Partial,
        _ => IoOutcome.Ok,
    };

    /// <summary>
    /// What a connector call's result is to the fetch: the connector's status when it answered in time; otherwise a failure
    /// naming the source: timed out after <paramref name="budgetMs"/> (the fetch's bound, the call's own or a connector's
    /// <see cref="TimeoutException"/>), cancelled by the caller, or what the connector threw. A failure the connector gave
    /// no words for gets the words a replay of its recording gives it, so the request fails with the same words in both.
    /// </summary>
    private static FetchStatus StatusOf(FetchGroup group, int budgetMs, in CallResult<FetchStatus> result) => result.End switch
    {
        CallEnd.Answered when result.Value is { Outcome: FetchOutcome.Failed, Error: null } silent => silent with { Error = FailedWithoutWords(group.SourceName) },
        CallEnd.Answered => result.Value,
        CallEnd.Cancelled => Cancelled(group),
        CallEnd.TimedOut or CallEnd.BudgetRanOut => TimedOut(group, budgetMs),
        _ when result.Outcome == IoOutcome.Timeout => TimedOut(group, budgetMs),
        _ => FetchStatus.Failed($"source '{group.SourceName}' failed: {result.Error ?? "the call did not complete"}"), // read once (BoundedCall)
    };

    /// <summary>Whether a connector call's staging must not be read: it did not answer in time, or answered a failure.</summary>
    private static bool Failed(in CallResult<FetchStatus> result) => result.End != CallEnd.Answered || result.Value.Outcome == FetchOutcome.Failed;

    /// <summary>What a source's failure says when its connector said nothing: live, and in a replay of its recording.</summary>
    internal static string FailedWithoutWords(string source) => $"source '{source}' failed";

    private static FetchStatus TimedOut(FetchGroup group, int budgetMs) => FetchStatus.Failed($"source '{group.SourceName}' timed out after {budgetMs} ms");

    private static FetchStatus Cancelled(FetchGroup group) => FetchStatus.Failed($"source '{group.SourceName}' was cancelled");

    private void TrackIo(Task task)
    {
        lock (_io)
        {
            _io.Add(task);
        }
    }

    /// <summary>A source in mode service's call, or its cache's read, still running.</summary>
    void IIoTracker.Track(Task done) => TrackIo(done);

    private async ValueTask CompleteAsync(PendingFetch pending)
    {
        var group = pending.Group;
        var failedChunks = 0;
        var partial = false;
        string? error = null;
        for (var c = 0; c < pending.ChunkCount; c++)
        {
            var status = StatusOf(group, pending.BudgetMs, await pending.Calls[c].ConfigureAwait(false));
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

        // Some chunks failed: the source partially failed and readers become Degraded. The failed chunks'
        // keys are treated like keys absent from the response; their staging is never read.
        var degraded = failedChunks > 0 || partial;
        if (!SettleKey(group, pending.Features, failedChunks > 0 && failedChunks == pending.ChunkCount, ref degraded, ref error))
        {
            return;
        }

        var scratch = default(BackfillScratch); // shared by the fetch's features: a row's key is looked up once, not per feature
        try
        {
            for (var i = 0; i < pending.Features.Length; i++)
            {
                var f = pending.Features[i];
                if (_state[f] == FeatureState.Failed)
                {
                    continue;
                }

                Backfill(pending, _plan.Features[f], i, ref scratch);
                SetFetchedState(f, degraded, error);
            }
        }
        finally
        {
            if (scratch.Keys is not null)
            {
                _memory.ReturnScratch(scratch.Keys);
                _memory.ReturnScratch(scratch.Copied!);
            }
        }
    }

    /// <summary>
    /// What the features of one completed fetch share while they are backfilled: each live row's fetch key, resolved the
    /// first time a feature needs it (<see cref="UnresolvedKey"/> until then), and the list of rows a feature copied. The
    /// rows and their order do not change while a fetch's features are backfilled, and neither does the key column.
    /// </summary>
    private struct BackfillScratch
    {
        public int[]? Keys; // by rank position: a fetch key, NoFetchKey, NotThisFetch or UnresolvedKey
        public int[]? Copied;
    }

    private const int UnresolvedKey = int.MinValue;

    /// <summary>The row has no key (an anonymous user, a missing foreign key): the feature is legitimately missing.</summary>
    private const int NoFetchKey = -1;

    /// <summary>The row is not this fetch's: its key is not ready yet, or the row was appended after the fetch started.</summary>
    private const int NotThisFetch = -2;

    private int FetchKeyOf(PendingFetch pending, int row)
    {
        if (pending.NoSharedKey)
        {
            return NoFetchKey;
        }

        var one = pending.Group.OneVaryingFeature;
        if (one != int.MinValue)
        {
            return PartKey(one, row, out var key) switch
            {
                KeyLookup.Found => pending.Keys.IsSet && pending.Keys.TryGetValue(key, out var keyIndex) ? keyIndex : NotThisFetch,
                KeyLookup.NotReady => NotThisFetch, // a later fetch fills it
                _ => NoFetchKey,
            };
        }

        var varyingBuffer = default(KeyPartBuffer);
        Span<EntityKey> varying = varyingBuffer;
        varying = varying[..pending.Group.KeyLayout.VaryingCount];
        return RowKey(pending.Group, row, varying) switch
        {
            KeyLookup.Found => pending.Keys.IsSet && pending.Keys.TryGetValue(varying, out var keyIndex) ? keyIndex : NotThisFetch,
            KeyLookup.NotReady => NotThisFetch, // a later fetch fills it
            _ => NoFetchKey,
        };
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
    /// node that did not run needed (a fallback), or one the request ended before reading. A source that
    /// answered was asked whether or not the request used the answer. Only chunks whose calls completed within their
    /// budget count, as for a fetch that was read; a chunk still running when the request ends is abandoned. Called once, by
    /// <see cref="EndRequest"/>, before the request's memory is released (the staging lives in it).
    /// </summary>
    private void CountUnreadFetches()
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
                var failed = !call.IsCompleted || Failed(call.Result);
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
    /// request without one pays this check alone, and never in a replay, which is not traffic.
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
    /// The first half of the state rule of a fetch or a cache read: a failed source or a failed key fails
    /// every feature (set here; returns false, nothing to fill), and a degraded key degrades them. One rule for both,
    /// so a replay, which answers a cache read through the fetch path, ends in the states the live read did.
    /// </summary>
    private bool SettleKey(FetchGroup group, int[] features, bool failed, ref bool degraded, ref string? error)
    {
        var keyState = KeyState(group, out var keyError);
        if (keyState is FeatureState.Failed or FeatureState.Degraded)
        {
            error ??= keyError;
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
    /// Copies staged values into the rows of the fetch's scope that are still unfilled (the live rows now, for a fetch of
    /// every live row), matching by key. Key index k belongs to chunk k / the fetch's chunk size (Batching.Size, at most max_batch). A value is read only if
    /// its chunk succeeded and the connector wrote that field for that key; anything else takes the default or becomes
    /// missing, and so does a list of another length than a <c>list&lt;E,N&gt;</c> feature's, counted out of range
    ///.
    /// </summary>
    private void Backfill(PendingFetch pending, FeatureRuntimeInfo feature, int fieldIndex, ref BackfillScratch scratch)
    {
        var definition = feature.Definition;
        var column = _table.GetOrCreateSlot(feature.Slot);
        var filled = _table.GetFilled(feature.Slot);
        var order = RowsOf(pending.Scope);
        if (scratch.Keys is null)
        {
            scratch.Keys = _memory.RentScratch<int>(Math.Max(1, order.Length));
            scratch.Keys.AsSpan(0, order.Length).Fill(UnresolvedKey);
            scratch.Copied = _memory.RentScratch<int>(Math.Max(1, order.Length));
        }

        var keys = scratch.Keys;
        var copied = scratch.Copied!;
        var copiedCount = 0;
        var outOfRange = 0;
        for (var p = 0; p < order.Length; p++)
        {
            var row = order[p];
            if (Bits.Get(filled, row))
            {
                continue;
            }

            var keyIndex = keys[p];
            if (keyIndex == UnresolvedKey)
            {
                keys[p] = keyIndex = FetchKeyOf(pending, row);
            }

            if (keyIndex == NotThisFetch)
            {
                continue;
            }

            var stored = false;
            if (keyIndex >= 0 && pending.Staging is { } staging && !pending.ChunkFailed[keyIndex / pending.ChunkSize]
                && staging.HasValue(fieldIndex, keyIndex))
            {
                stored = staging.TryCopyTo(fieldIndex, keyIndex, column, row);
                outOfRange += stored ? 0 : 1;
            }

            if (stored)
            {
                column.SetValid(row);
                copied[copiedCount++] = row;
            }
            else
            {
                ColumnValues.Set(column, row, definition.Absent);
            }

            Bits.Set(filled, row);
        }

        if (outOfRange > 0)
        {
            _counters.AddOutOfRange(outOfRange);
        }

        if (copiedCount > 0 && ColumnValues.IsFloat(column))
        {
            AddNonFinite(feature, ColumnValues.SanitizeNonFinite(column, copied.AsSpan(0, copiedCount), definition.Absent));
        }
    }

    private void ComputeDerivedOfWave(int wave, bool[] member, RowScope scope)
    {
        var order = _plan.DerivedOrder;
        for (var i = 0; i < order.Count; i++) // an interface foreach would box the enumerator, twice per ensure
        {
            var f = order[i];
            var info = _plan.Features[f];
            if (member[f] && !info.IsContext && info.Wave == wave)
            {
                ComputeDerived(info, scope);
            }
        }
    }

    private void ComputeDerived(FeatureRuntimeInfo feature, RowScope scope)
    {
        var (worst, error) = WorstDependency(feature);
        if (worst == FeatureState.Failed)
        {
            _state[feature.Index] = FeatureState.Failed;
            _errors[feature.Index] = error;
            return;
        }

        var rows = RowsOf(scope);
        var buffer = _memory.RentScratch<int>(Math.Max(1, rows.Length));
        try
        {
            ComputeDerived(feature, worst, error, rows, buffer);
        }
        finally
        {
            _memory.ReturnScratch(buffer);
        }
    }

    /// <summary>Computes the feature for those of <paramref name="rows"/> it is not filled on, and sets its state by them.</summary>
    /// <param name="buffer">Scratch for the rows the feature fills, returned by the caller.</param>
    private void ComputeDerived(FeatureRuntimeInfo feature, FeatureState worst, string? error, ReadOnlySpan<int> rows, int[] buffer)
    {
        var filled = _table.GetFilled(feature.Slot);
        var count = 0;
        foreach (var row in rows)
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
            // counted there: unlike NaN, it cannot be stored and sanitized afterwards.
            feature.Expression!.Evaluate(new EvalContext(_table, _bindings, _context, _params, _counters), span, column, definition.Absent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The backend does not throw for data (errors become missing values); an exception here is a defect in
            // a function or connector-provided value. Treat it like a failed source: readers of this
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
            // A non-nullable expression leaves a row missing only where an out-of-range int32 value, or a list of another
            // length than the feature's fixed one, was replaced at the write. With no default to take, the feature cannot
            // be served as typed: readers compiled against a non-nullable type skip validity checks (same rule as a node
            // output).
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
                var why = !feature.Type.IsFixedLength ? "outside the int32 range"
                    : feature.Type.IsInt32Storage ? "outside the int32 range or of another length" : "of another length";
                _errors[feature.Index] = $"derived feature '{definition.Name}' produced {missing} value(s) its {feature.Type} type cannot hold ({why}) " +
                    "but is non-nullable and declares no default; declare a default or make the expression nullable";
            }
        }

        if (ColumnValues.IsFloat(column))
        {
            // The expression backend writes NaN/±∞ as-is and leaves sanitizing to the engine.
            var replaced = ColumnValues.SanitizeNonFinite(column, span, definition.Absent);
            AddNonFinite(feature, replaced);
            if (replaced > 0 && !definition.HasDefault && !feature.Type.Nullable)
            {
                // Same rule as a node output: a non-nullable column cannot hold the replacement "missing".
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

    private void AddNonFinite(FeatureRuntimeInfo feature, int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _nonFiniteReplaced, count);
            if (_hook is not { Replays: true })
            {
                // A replay is not traffic: it records no metrics.
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
    /// Resolves the parts of a row's key that vary per row (<see cref="FetchGroup.VaryingParts"/>, in part order) into
    /// <paramref name="varying"/>. A part that is missing makes the key missing (it is not looked up: the features take
    /// their absent values); otherwise a part not computed yet for the row makes it <see cref="KeyLookup.NotReady"/>,
    /// which is not the same as a missing key: treating it as missing would publish default values for rows whose key is
    /// merely not computed yet.
    /// </summary>
    private KeyLookup RowKey(FetchGroup group, int row, Span<EntityKey> varying)
    {
        var result = KeyLookup.Found;
        var parts = group.VaryingParts;
        for (var v = 0; v < parts.Length; v++)
        {
            var part = PartKey(group.KeyParts[parts[v]].Feature, row, out varying[v]);
            if (part == KeyLookup.Missing)
            {
                return KeyLookup.Missing;
            }

            if (part == KeyLookup.NotReady)
            {
                result = KeyLookup.NotReady;
            }
        }

        return result;
    }

    /// <summary>
    /// The values of the group's shared key parts (request inputs and context key features, as the prefetch stored them),
    /// in part order. False when one is missing: then no row has a key.
    /// </summary>
    private bool TrySharedKey(FetchGroup group, Span<EntityKey> shared)
    {
        var s = 0;
        foreach (var part in group.KeyParts)
        {
            if (!part.IsContext)
            {
                continue;
            }

            var value = _context[part.Context];
            if (value.IsMissing)
            {
                return false;
            }

            shared[s++] = ToKey(value);
        }

        return true;
    }

    /// <summary>A row's value of one varying key part: the candidate's entity id (<paramref name="feature"/> -1) or the
    /// value of key feature <paramref name="feature"/> (<see cref="ColumnKey"/>).</summary>
    private KeyLookup PartKey(int feature, int row, out EntityKey key)
    {
        if (feature < 0)
        {
            key = _table.GetKey(row);
            return KeyLookup.Found;
        }

        return ColumnKey(feature, row, out key);
    }

    private KeyLookup ColumnKey(int feature, int row, out EntityKey key)
    {
        key = default;
        var keyFeature = _plan.Features[feature];
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
            case StringColumn s: key = EntityKey.FromString(s.Get(row)); break;
            default: return KeyLookup.Missing;
        }

        return KeyLookup.Found;
    }

    private static EntityKey ToKey(in ScalarValue value) =>
        value.Kind == StorageKind.Int64 ? EntityKey.FromInt64(value.AsInt64()) : EntityKey.FromString(value.AsString());

    /// <summary>
    /// The deadline of a call of <paramref name="group"/> sent now: the source's timeout from now or the request's deadline,
    /// whichever is earlier, as a <see cref="Stopwatch"/> timestamp, and the time to it in whole milliseconds. A bound made
    /// from the deadline ends then however late it is made; one made from the budget would start it again.
    /// </summary>
    private long DeadlineOf(FetchGroup group, out int budgetMs)
    {
        var now = Stopwatch.GetTimestamp();
        var deadline = Math.Min(_deadlineTimestamp, now + ((long)Math.Max(0, group.Supply.TimeoutMs) * Stopwatch.Frequency / 1000));
        budgetMs = (int)Math.Clamp(Stopwatch.GetElapsedTime(now, deadline).TotalMilliseconds, 0, int.MaxValue);
        return deadline;
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
        RowScope scope,
        FetchKeys keys,
        int chunkSize,
        int chunkCount,
        StagingArea? staging,
        ValueTask<CallResult<FetchStatus>>[] calls,
        bool[] chunkFailed,
        bool noSharedKey,
        int budgetMs)
    {
        public FetchGroup Group { get; } = group;

        /// <summary>Fetched features; staging field i holds <c>Features[i]</c>.</summary>
        public int[] Features { get; } = features;

        /// <summary>The rows backfilled: the live ones when it completes, or a row-scoped fill's, which completes it itself.</summary>
        public RowScope Scope { get; } = scope;

        /// <summary>Key → key index; key index k belongs to chunk k / ChunkSize. Not set when no live row had a key.</summary>
        public FetchKeys Keys { get; } = keys;

        public int ChunkSize { get; } = chunkSize;

        /// <summary>Number of chunks; the chunk arrays below are valid up to this length.</summary>
        public int ChunkCount { get; } = chunkCount;

        /// <summary>Staging of every chunk; null when no chunk was sent (no time left).</summary>
        public StagingArea? Staging { get; } = staging;

        /// <summary>Each awaited once, by <see cref="CompleteAsync"/>; never, for a prefetch nobody needs.</summary>
        public ValueTask<CallResult<FetchStatus>>[] Calls { get; } = calls;

        /// <summary>The fetch's time budget, which a timeout names (<see cref="StatusOf"/>).</summary>
        public int BudgetMs { get; } = budgetMs;

        /// <summary>Set on completion: chunks whose staging must not be read.</summary>
        public bool[] ChunkFailed { get; } = chunkFailed;

        /// <summary>A shared key part (a request input or a context key feature) is missing: no row has a key.</summary>
        public bool NoSharedKey { get; } = noSharedKey;
    }

    /// <summary>Feature or group indices in an array rented from the request memory, valid up to <see cref="Count"/>.</summary>
    private readonly record struct IndexList(int[] Items, int Count);

    /// <summary>
    /// The rows a fill covers: every live row (<see cref="Live"/>, a node's reads), or the first <see cref="Count"/> of
    /// <see cref="Rows"/> (a row-scoped fill's, <see cref="EnsureAsync(ReadOnlySpan{int}, ReadOnlySpan{int}, CancellationToken)"/>).
    /// Every step of a fill that looks at rows (what is still unfilled, the keys a fetch or a table read takes, the rows a
    /// fetch backfills, the rows a derived feature is computed for) takes them from here, so the two kinds share one path.
    /// </summary>
    private readonly record struct RowScope(int[]? Rows, int Count)
    {
        public static RowScope Live => default;
    }

    /// <summary>The rows of <paramref name="scope"/>: the live rows as they are now, or the scope's own.</summary>
    private ReadOnlySpan<int> RowsOf(RowScope scope) => scope.Rows is null ? _table.Rows.Order : scope.Rows.AsSpan(0, scope.Count);

    /// <summary>Whether the slot is filled on every row of <paramref name="scope"/>.</summary>
    private bool IsFilled(int slot, RowScope scope) =>
        scope.Rows is null ? _table.IsFilledForLiveRows(slot) : _table.IsFilled(slot, scope.Rows.AsSpan(0, scope.Count));

    /// <summary>
    /// A connector call (a fetch's chunk, a context fetch, a cache read's origin chunk) as <see cref="BoundedCall"/>'s
    /// call site: sends it, judges its status (a partial answer is <see cref="IoOutcome.Partial"/>), has the request's
    /// I/O drain track it while it runs on, and records it (unless it is <see cref="NotRecorded"/>) with the staging the
    /// connector decoded into, read only when it answered in time. A fetch's chunks share one bound (one token source and
    /// one timer, however many chunks), so a fetch of hundreds of chunks creates one source rather than one per chunk.
    /// </summary>
    private readonly struct ConnectorCall(
        FeatureFetcher fetcher, FetchGroup group, EntityKeyBatch batch, CompiledFieldBinding[] fields, StagingBuffer writer, int budgetMs, int sequence)
        : IBoundedCall<FetchStatus>
    {
        public int Keys => batch.Count;

        public ValueTask<FetchStatus> Send(CancellationToken token) => group.Supply.Connector!.FetchAsync(batch, fields, writer, token);

        public IoOutcome Judge(FetchStatus value, out string? error)
        {
            error = value.Error;
            return OutcomeOf(value);
        }

        public void Pending(Task done) => fetcher.TrackIo(done); // done once the connector and this call site both are

        public void Ended(in CallResult<FetchStatus> result, TimeSpan duration)
        {
            if (sequence != NotRecorded && fetcher._hook is { } hook)
            {
                hook.Record(
                    new FeatureCallInfo(group, batch, ImmutableCollectionsMarshal.AsImmutableArray(fields), sequence, result.Sent, StatusOf(group, budgetMs, result),
                        result.Outcome, duration),
                    result.Outcome is IoOutcome.Ok or IoOutcome.Partial ? writer : null);
            }
        }
    }
}
