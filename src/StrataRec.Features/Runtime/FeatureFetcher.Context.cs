using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The context prefetch (design 6.8 item 5): everything a request does to make its context features available is here.
/// </summary>
/// <remarks>
/// <para><b>Starts.</b> <see cref="StartContextFetch"/> starts, at request start and concurrently with recall, every context
/// group the plan's readers need. A group keyed by request inputs alone starts at once: the fetched groups are sent first,
/// then the local and cache groups are read while those are in flight. A group whose key reads context features (a chain:
/// a user id that another source resolves from a login key) starts as soon as those features are stored: the groups that
/// supply them, and for a derived one its inputs, from which it is computed at that moment. It waits for nothing else, so
/// a slow source the key does not read delays neither its start nor its readers. Each group has its own budget, the source
/// timeout or what is left before the request deadline when it starts, so the later groups of a chain get what is left.</para>
/// <para><b>Readiness.</b> Every context feature the prefetch supplies has a task that completes once the feature is stored
/// (<see cref="_contextReady"/>, all set before <see cref="StartContextFetch"/> returns): its group's, or, for a derived
/// feature a key reads, its computation's. A reader waits for the features it reads (audit B6). A derived context feature
/// no key reads is computed once every group is stored, and its readers wait for the whole prefetch.</para>
/// <para><b>Keys.</b> A chained group's key is read from the stored values, as a request input is: a missing one (absent,
/// failed, not answered) is no key, so nothing is called and the features take their absent values. As for a foreign key
/// (design 6.7), a failed key feature fails the group's features, without a call, and a degraded one degrades them.</para>
/// <para><b>Lifetime</b> (review 72). The prefetch holds the request memory until every group it started is stored and
/// every derived feature it computes is computed, however the starts ended; then it rethrows the first failure. A chained
/// group is not started when what its key reads failed with an exception, and not sent when its key is stored after the
/// request has ended (<see cref="EndRequest"/>) or been cancelled; its task completes all the same, so nothing is left
/// waiting.</para>
/// <para><b>Threads.</b> A chained group starts on the thread that stored the last of its key features (a connector's
/// completion), while the request's thread goes on: it writes only its own features' states and context values, rents
/// from the request memory (whose ledger is locked) and pins its table in the version array, which the prefetch rents
/// before anything starts. Its call's place in a recording's start order is therefore when its key was stored.</para>
/// </remarks>
public sealed partial class FeatureFetcher
{
    private Task? _contextTask;

    /// <summary>
    /// Per feature index, the task that has stored a context feature the prefetch supplies: its group's, or a derived
    /// feature's that a key reads; null for any other feature (a derived context feature no key reads is stored with the
    /// whole prefetch, <see cref="_contextTask"/>). Set before <see cref="StartContextFetch"/> returns.
    /// </summary>
    private Task?[]? _contextReady;

    /// <summary>Set by <see cref="EndRequest"/>: a chained context group whose key is stored later is not sent.</summary>
    private volatile bool _ended;

    /// <summary>
    /// Starts fetching context features (and computes context-derived ones) concurrently with recall (design 6.8), each group
    /// once what its key reads is stored.
    /// </summary>
    public void StartContextFetch(ReadOnlySpan<int> features, CancellationToken cancellationToken)
    {
        var closure = Closure(features, out var member, scratch: false); // read by the background fetch after this returns
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
    /// The request has its result (call once, at its end, before its memory is released): nothing reads its features any
    /// more. Counts the fetches no reader took (<see cref="StrataRecMetrics.SourceValues"/>), and stops the chained context
    /// groups still waiting for their keys from being sent.
    /// </summary>
    public void EndRequest()
    {
        _ended = true;
        CountUnreadFetches();
    }

    /// <summary>
    /// Waits until the context features of <paramref name="closure"/> are stored: each when its group is, or a derived one
    /// a key reads when it is computed (an ANN recall reading the user's embedding does not wait for a slower source of other
    /// user features), any other derived one when the whole prefetch is, which computes it last.
    /// </summary>
    private async ValueTask ContextReadyAsync(IndexList closure)
    {
        for (var c = 0; c < closure.Count; c++)
        {
            var f = closure.Items[c];
            if (_plan.Features[f].IsContext)
            {
                // Awaited even when complete, so a fault is observed; a completed task does not yield.
                await (_contextReady?[f] ?? _contextTask!).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Waits until each of <paramref name="features"/>, context features the plan prefetches, is stored, as a reader of them
    /// would (<see cref="ContextReadyAsync"/>), and returns their worst state: the wait of an operator that reads context
    /// features while it runs rather than before it starts, once per call it sends (a recall source's query values, design
    /// 5.9), so each call waits for its own features and not another call's. It only waits for the prefetch's tasks and
    /// reads the states they stored, so several waits may run at once, alongside the request's other work.
    /// </summary>
    /// <exception cref="InvalidOperationException">A listed feature is not a context feature (the operator's defect), or
    /// the prefetch has not started (<see cref="StartContextFetch"/>, which the executor calls before any node runs).</exception>
    public async ValueTask<FeatureFillResult> WhenContextStoredAsync(ImmutableArray<int> features)
    {
        if (_contextTask is null)
        {
            throw new InvalidOperationException("Context features are waited for before the context prefetch has started: they would never be stored.");
        }

        var worst = FeatureState.Ok;
        string? error = null;
        foreach (var f in features)
        {
            if (!_plan.Features[f].IsContext)
            {
                throw new InvalidOperationException($"Feature '{_plan.Features[f].Definition.Name}' is not a context feature: only those are waited for this way.");
            }

            if ((_contextReady?[f] ?? _contextTask) is { } stored)
            {
                // Awaited even when complete, so a fault is observed; a completed task does not yield.
                await stored.ConfigureAwait(false);
            }

            if (_state[f] > worst)
            {
                worst = _state[f];
                error = _errors[f];
            }
        }

        return new FeatureFillResult(worst, error);
    }

    /// <summary>Whether context feature <paramref name="feature"/> is stored (or will never be: no prefetch was started),
    /// without waiting: a prefetch of a candidate group keyed by it is started only then.</summary>
    private bool ContextStored(int feature) => (_contextReady?[feature] ?? _contextTask) is null or { IsCompletedSuccessfully: true };

    /// <param name="closure">The features to make available; only its context features are fetched.</param>
    /// <param name="member">Marks <paramref name="closure"/> by feature index.</param>
    private async Task FetchContextAsync(IndexList closure, bool[] member, CancellationToken cancellationToken)
    {
        try
        {
            var featureCount = Math.Max(1, _plan.Features.Count);
            var groupCount = Math.Max(1, _plan.Groups.Count);
            var seen = _memory.RentCleared<bool>(groupCount);
            var stored = _memory.Rent<Task>(groupCount + featureCount); // a task per group, and per derived feature a key reads
            var ready = _contextReady = _memory.RentCleared<Task?>(featureCount);
            if (_plan.HasTables)
            {
                // A chained group read locally pins its table on another thread than the request's: the array they share
                // is rented here, before anything starts, and each writes only its own groups' entries.
                _pins ??= _memory.RentCleared<TableVersion?>(groupCount);
            }

            var storedCount = 0;
            ExceptionDispatchInfo? failure = null;
            try
            {
                // Pass 0 sends the fetched groups keyed by request inputs alone, pass 1 reads the local and cache ones while
                // those are in flight, pass 2 sets up, in the closure's (dependency) order, the groups keyed by context
                // features and the derived features keys read: each starts once what it reads is stored, and finds the tasks
                // of what it reads set already, since the closure lists every feature after what it depends on.
                for (var pass = 0; pass < 3; pass++)
                {
                    for (var c = 0; c < closure.Count; c++)
                    {
                        var info = _plan.Features[closure.Items[c]];
                        if (!info.IsContext)
                        {
                            continue;
                        }

                        if (info.IsDerived)
                        {
                            if (pass == 2 && info.ReadByKey)
                            {
                                var computed = ComputeKeyDerivedAsync(info);
                                ready[info.Index] = computed;
                                stored[storedCount++] = computed;
                            }

                            continue;
                        }

                        if (info.GroupIndex < 0 || seen[info.GroupIndex])
                        {
                            continue;
                        }

                        var group = _plan.Groups[info.GroupIndex];
                        var chained = group.ContextKeyFeatures.Length > 0;
                        if (pass != (chained ? 2 : ReadsLocally(group) ? 1 : 0))
                        {
                            continue;
                        }

                        seen[info.GroupIndex] = true;
                        var features = RequestedMembers(group, member);
                        var done = chained ? StartChainedContextGroupAsync(group, features, cancellationToken) : StartContextGroup(group, features, cancellationToken);
                        foreach (var f in features)
                        {
                            ready[f] = done;
                        }

                        stored[storedCount++] = done;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            // Every group started reads its staging and writes its features when its source answers, and a chained one
            // starts when its key is stored: the memory is held until all of them are done, however the start ended (review
            // 72: a recording hook that threw while the next group was sent released it, and a group answered later read and
            // wrote returned buffers). The first failure is rethrown.
            for (var n = 0; n < storedCount; n++)
            {
                try
                {
                    await stored[n].ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure ??= ExceptionDispatchInfo.Capture(ex);
                }
            }

            failure?.Throw();

            var order = _plan.DerivedOrder;
            for (var d = 0; d < order.Count; d++)
            {
                var info = _plan.Features[order[d]];
                if (info.IsContext && member[info.Index] && !info.ReadByKey)
                {
                    ComputeContextDerived(info);
                }
            }
        }
        finally
        {
            _memory.Release();
        }
    }

    /// <summary>
    /// Starts one context group for <paramref name="features"/>, the members the prefetch needs, and returns the task that
    /// has stored them. The key's state rule is a foreign key's (design 6.7): a failed key feature fails the features without
    /// a call, a degraded one degrades them; a key of request inputs alone is ok.
    /// </summary>
    private Task StartContextGroup(FetchGroup group, int[] features, CancellationToken cancellationToken)
    {
        var keyState = KeyState(group, out var keyError);
        if (keyState == FeatureState.Failed)
        {
            FailContext(features, keyError);
            return Task.CompletedTask;
        }

        var stored = ReadsLocally(group) ? ReadContextGroup(group, features, cancellationToken) : FetchContextGroup(group, features, cancellationToken);
        return keyState == FeatureState.Degraded ? DegradeWhenStoredAsync(stored, features, keyError) : stored;
    }

    /// <summary>
    /// A group whose key reads context features: started once each of them is stored. One whose store failed with an
    /// exception is not started (the exception is the prefetch's failure); one whose key is stored after the request ended
    /// is not sent, its features failed for anyone who looks.
    /// </summary>
    private async Task StartChainedContextGroupAsync(FetchGroup group, int[] features, CancellationToken cancellationToken)
    {
        var ready = _contextReady!;
        foreach (var k in group.ContextKeyFeatures)
        {
            if (ready[k] is { } stored)
            {
                await stored.ConfigureAwait(false); // completes without yielding when stored already (a synchronous source)
            }
        }

        if (_ended)
        {
            FailContext(features, $"the request ended before the key of '{group.Entity.Name}' was stored");
            return;
        }

        await StartContextGroup(group, features, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A derived context feature a key reads: computed as soon as what it reads is stored.</summary>
    private async Task ComputeKeyDerivedAsync(FeatureRuntimeInfo info)
    {
        var ready = _contextReady!;
        foreach (var d in info.Dependencies)
        {
            if (ready[d] is { } stored)
            {
                await stored.ConfigureAwait(false);
            }
        }

        ComputeContextDerived(info);
    }

    /// <summary>Evaluates a derived context feature once what it reads is stored, with the state of the worst of its inputs.</summary>
    private void ComputeContextDerived(FeatureRuntimeInfo info)
    {
        var f = info.Index;
        var (worst, error) = WorstDependency(info);
        _state[f] = worst;
        _errors[f] = error;
        if (worst == FeatureState.Failed)
        {
            return;
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
            return;
        }

        StoreContext(info, derived);
    }

    /// <summary>The features of a group stored with a degraded key: degraded too, unless their own fetch already was (or failed).</summary>
    private async Task DegradeWhenStoredAsync(Task stored, int[] features, string? keyError)
    {
        await stored.ConfigureAwait(false);
        foreach (var f in features)
        {
            if (_state[f] == FeatureState.Ok)
            {
                _state[f] = FeatureState.Degraded;
                _errors[f] = keyError;
            }
        }
    }

    private void FailContext(int[] features, string? error)
    {
        foreach (var f in features)
        {
            _state[f] = FeatureState.Failed;
            _errors[f] = error;
        }
    }

    /// <summary>
    /// Reads a local or cache context group (one key) and returns the task that has stored it: complete at once, but for a
    /// cache read that missed, which completes once its misses have resolved.
    /// </summary>
    private Task ReadContextGroup(FetchGroup group, int[] features, CancellationToken cancellationToken) =>
        ReadLocalContext(group, features, cancellationToken) is { } resolving ? CompleteCachedContextAsync(resolving) : Task.CompletedTask;

    /// <summary>Sends the one call of a fetched context group (none without a key) and returns the task that stores its answer.</summary>
    private Task FetchContextGroup(FetchGroup group, int[] features, CancellationToken cancellationToken)
    {
        ContextFetch fetch;
        if (!TryContextKey(group, out var batch))
        {
            fetch = new ContextFetch(features, null, new ValueTask<FetchStatus>(FetchStatus.Ok));
        }
        else
        {
            var fields = FieldBindings(group, features);
            var budget = BudgetMs(group);
            var cancellation = budget <= 0 || _hook is { Replays: true } ? null : new FetchCancellation(cancellationToken, budget, _events, _requestId);
            if (budget <= 0)
            {
                fetch = new ContextFetch(features, null, new ValueTask<FetchStatus>(NoTimeLeft));
                RecordUnsent(group, batch, fields, NoTimeLeft, IoOutcome.Timeout);
            }
            else if (cancellation is not null && CutOff(cancellation, cancellationToken) is { } cut)
            {
                // As a candidate chunk (FetchCandidates): a context fetch is not sent once the caller has cancelled, or its
                // time is up (an earlier group's connector ran its synchronous part; review 49).
                cancellation.Release();
                var unsent = cut == IoOutcome.Cancelled ? Cancelled(group) : TimedOut(group, budget);
                fetch = new ContextFetch(features, null, new ValueTask<FetchStatus>(unsent));
                RecordUnsent(group, batch, fields, unsent, cut);
            }
            else
            {
                var staging = new StagingBuffer(fields, 1, _memory);
                ValueTask<FetchStatus> call;
                try
                {
                    call = CallAsync(group, batch, fields, staging, budget, cancellation, cancellationToken, out _);
                }
                finally
                {
                    cancellation?.Release();
                }

                fetch = new ContextFetch(features, staging, call);
            }
        }

        // Each group is stored as its source answers, not in the order they were sent.
        return StoreContextGroupAsync(fetch);
    }

    /// <summary>
    /// Stores one context group's values when its source answers: the features' states and values, and the values
    /// counted. Groups complete concurrently; each writes only its own features' slots.
    /// </summary>
    private async Task StoreContextGroupAsync(ContextFetch fetch)
    {
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
            return group.FeatureIndexArray;
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
    /// Publishes a context feature value as its storage holds it (<see cref="ColumnValues.AsStored"/>: a float32 feature's
    /// value is rounded to float32): NaN/±∞, including a float beyond the float32 range, an integer (or list element)
    /// outside the int32 range of an <c>int32</c> or <c>list&lt;int32&gt;</c> feature, and a list of another length than a
    /// <c>list&lt;E,N&gt;</c> feature's, are counted and replaced by the default or missing (design 4.3), and a missing value
    /// takes the default.
    /// </summary>
    /// <remarks>Context values are held promoted, so an out-of-range value would not wrap here; it is still replaced,
    /// so a feature's value always lies in its declared type wherever it is read, logged or written, and is the value the
    /// same feature computed per candidate would have.</remarks>
    private void StoreContext(FeatureRuntimeInfo info, ScalarValue value)
    {
        var definition = info.Definition;
        value = ColumnValues.AsStored(value, info.Type);
        // A list of the wrong length is out of range whatever its elements are, as the column writers count it.
        var wrongLength = value.Kind == StorageKind.List && !ColumnValues.FitsLength(value.ListLength, info.Type);
        if (!wrongLength && ColumnValues.IsNonFinite(value))
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
                var produced = wrongLength ? "a list" : value.ToString();
                _state[info.Index] = FeatureState.Failed;
                _errors[info.Index] = $"feature '{definition.Name}' produced {produced} its {info.Type} type cannot hold ({ColumnValues.Misfit(value, info.Type)}) "
                    + "but is non-nullable and declares no default";
            }

            value = ScalarValue.Missing;
        }

        // The default too is published as storage holds it (the catalog's are already; a test's or a host's may not be).
        _context[info.ContextId] = value.IsMissing ? ColumnValues.AsStored(definition.Absent, info.Type) : value;
    }

    /// <summary>The one key of a context-scoped group, every part shared, in a buffer rented from the request memory;
    /// false when a part is missing (a request input, or a context key feature absent, failed or not answered), which leaves
    /// the features absent.</summary>
    private bool TryContextKey(FetchGroup group, out EntityKeyBatch batch)
    {
        var layout = group.KeyLayout;
        var buffer = _memory.Rent<EntityKey>(layout.PartCount);
        if (!TrySharedKey(group, buffer))
        {
            batch = default;
            return false;
        }

        batch = new EntityKeyBatch(buffer, 1, 0, 1, layout.PartCount, layout.SharedMask);
        return true;
    }

    /// <param name="Call">Awaited once, by <see cref="StoreContextGroupAsync"/>.</param>
    private readonly record struct ContextFetch(int[] Features, StagingBuffer? Staging, ValueTask<FetchStatus> Call);
}
