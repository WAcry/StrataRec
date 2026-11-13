using System.Diagnostics;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
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
/// <remarks>Not thread-safe; driven by the request's executor. Connectors run concurrently underneath.</remarks>
public sealed class FeatureFetcher
{
    private readonly FeaturePlan _plan;
    private readonly CandidateTable _table;
    private readonly ScalarValue[] _context;
    private readonly OutputBindings _bindings;
    private readonly ScalarValue[] _params;
    private readonly RequestMemory _memory;
    private readonly long _deadlineTimestamp;
    private readonly FeatureState[] _state;
    private readonly string?[] _errors;
    private readonly PendingFetch?[] _pendingByGroup;
    private readonly List<Task> _io = new();
    private Task? _contextTask;

    public FeatureFetcher(
        FeaturePlan plan,
        CandidateTable table,
        ScalarValue[] context,
        OutputBindings bindings,
        ScalarValue[] parameters,
        RequestMemory memory,
        long deadlineTimestamp)
    {
        _plan = plan;
        _table = table;
        _context = context;
        _bindings = bindings;
        _params = parameters;
        _memory = memory;
        _deadlineTimestamp = deadlineTimestamp;
        _state = new FeatureState[plan.Features.Count];
        _errors = new string?[plan.Features.Count];
        _pendingByGroup = new PendingFetch?[plan.Groups.Count];
    }

    /// <summary>Number of rows whose fetched value was non-finite and replaced (metric hook).</summary>
    public int NonFiniteReplaced { get; private set; }

    public FeatureState GetState(int feature) => _state[feature];

    /// <summary>Completes when every connector call started by this request has finished (memory is released after).</summary>
    public Task WhenIoCompleteAsync() => Task.WhenAll(_io);

    /// <summary>
    /// Starts fetching context features (and computes context-derived ones) concurrently with recall (design 6.8).
    /// </summary>
    public void StartContextFetch(IEnumerable<int> features, CancellationToken cancellationToken)
    {
        var closure = _plan.Closure(features);
        closure.RemoveAll(f => !_plan.Features[f].IsContext);
        _contextTask = closure.Count == 0 ? Task.CompletedTask : FetchContextAsync(closure, cancellationToken);
    }

    /// <summary>
    /// Starts wave-0 fetches for features the next segment needs (called after each fetch barrier, design 5.4).
    /// </summary>
    public void Prefetch(IEnumerable<int> features, CancellationToken cancellationToken)
    {
        var closure = _plan.Closure(features);
        foreach (var group in GroupsNeedingFetch(closure, Membership(closure), wave: 0))
        {
            if (_pendingByGroup[group.Index] is null)
            {
                _pendingByGroup[group.Index] = Start(group, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Ensures every listed feature is filled for every live row (design 5.4, correctness rule): awaits in-flight
    /// fetches, fetches whatever is still missing wave by wave, backfills, then computes derived features.
    /// </summary>
    public async ValueTask<FeatureFillResult> EnsureAsync(IReadOnlyCollection<int> features, CancellationToken cancellationToken)
    {
        if (features.Count == 0)
        {
            return new FeatureFillResult(FeatureState.Ok);
        }

        var closure = _plan.Closure(features);
        if (_contextTask is not null && closure.Exists(f => _plan.Features[f].IsContext))
        {
            await _contextTask.ConfigureAwait(false);
        }

        var candidate = new List<int>(closure);
        candidate.RemoveAll(f => _plan.Features[f].IsContext);
        var member = Membership(candidate);
        for (var wave = 0; wave <= _plan.MaxWave; wave++)
        {
            // In-flight fetches (prefetched) first; their keys may cover most rows.
            foreach (var group in GroupsOf(candidate, wave))
            {
                if (_pendingByGroup[group.Index] is { } pending)
                {
                    _pendingByGroup[group.Index] = null;
                    await CompleteAsync(pending).ConfigureAwait(false);
                }
            }

            // Then anything still unfilled (rows appended since, or never prefetched).
            var started = new List<PendingFetch>();
            foreach (var group in GroupsNeedingFetch(candidate, member, wave))
            {
                started.Add(Start(group, cancellationToken));
            }

            foreach (var pending in started)
            {
                await CompleteAsync(pending).ConfigureAwait(false);
            }
        }

        foreach (var f in _plan.DerivedOrder)
        {
            if (member[f])
            {
                ComputeDerived(_plan.Features[f]);
            }
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

    private bool[] Membership(List<int> features)
    {
        var member = new bool[_plan.Features.Count];
        foreach (var f in features)
        {
            member[f] = true;
        }

        return member;
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

    private List<FetchGroup> GroupsNeedingFetch(List<int> features, bool[] member, int wave)
    {
        var groups = GroupsOf(features, wave);
        groups.RemoveAll(group =>
        {
            foreach (var f in group.FeatureIndices)
            {
                if (member[f] && _state[f] != FeatureState.Failed && !_table.IsFilledForLiveRows(_plan.Features[f].Slot))
                {
                    return false;
                }
            }

            return true;
        });
        return groups;
    }

    private PendingFetch Start(FetchGroup group, CancellationToken cancellationToken)
    {
        // Collect distinct keys of live rows that still miss any of the group's features.
        var keys = new List<EntityKey>();
        var index = new Dictionary<EntityKey, int>();
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
                contextKey = v.Kind == StorageKind.Int64 ? EntityKey.FromInt64(v.AsInt64()) : EntityKey.FromString(v.AsString());
            }
        }

        if (!noContextKey)
        {
            foreach (var row in _table.Rows.Order)
            {
                if (!NeedsFill(group, row) || !TryCandidateKey(group, row, out var key))
                {
                    continue;
                }

                if (!index.ContainsKey(key))
                {
                    index[key] = keys.Count;
                    keys.Add(key);
                }
            }
        }

        var staging = new StagingBuffer(group.Fields, keys.Count, _memory);
        Task<FetchStatus> task;
        if (keys.Count == 0)
        {
            task = Task.FromResult(FetchStatus.Ok);
        }
        else
        {
            var batch = keys.ToArray();
            task = RunConnectorAsync(group, contextKey, batch, staging, cancellationToken);
            _io.Add(task);
        }

        return new PendingFetch(group, index, staging, task, noContextKey);
    }

    private async Task<FetchStatus> RunConnectorAsync(
        FetchGroup group, EntityKey contextKey, EntityKey[] keys, StagingBuffer staging, CancellationToken cancellationToken)
    {
        var budget = Math.Min(group.TimeoutMs, RemainingMs());
        if (budget <= 0)
        {
            return FetchStatus.Failed("no time left before the request deadline");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);
        var chunks = (keys.Length + group.MaxBatch - 1) / group.MaxBatch;
        var calls = new Task<FetchStatus>[chunks];
        for (var c = 0; c < chunks; c++)
        {
            var offset = c * group.MaxBatch;
            var count = Math.Min(group.MaxBatch, keys.Length - offset);
            calls[c] = CallAsync(group, contextKey, keys.AsMemory(offset, count), new StagingWindow(staging, offset, count), cts.Token);
        }

        var results = await Task.WhenAll(calls).ConfigureAwait(false);
        var failed = 0;
        var partial = false;
        var failedKeys = 0;
        string? error = null;
        foreach (var r in results)
        {
            if (r.Outcome == FetchOutcome.Failed)
            {
                failed++;
            }

            partial |= r.Outcome == FetchOutcome.Partial;
            failedKeys += r.FailedKeys;
            error ??= r.Error;
        }

        if (failed == results.Length)
        {
            return results[0];
        }

        return failed > 0 || partial ? new FetchStatus(FetchOutcome.Partial, failedKeys, error) : FetchStatus.Ok;
    }

    private async Task<FetchStatus> CallAsync(
        FetchGroup group, EntityKey contextKey, ReadOnlyMemory<EntityKey> keys, IFeatureBatchWriter writer, CancellationToken cancellationToken)
    {
        _memory.Hold();
        try
        {
            var batch = new EntityKeyBatch { Shape = group.Shape, ContextKey = contextKey, CandidateKeys = keys };
            return await group.Connector.FetchAsync(batch, group.Fields, writer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FetchStatus.Failed($"source '{group.SourceName}' timed out");
        }
        catch (Exception ex)
        {
            return FetchStatus.Failed($"source '{group.SourceName}' failed: {ex.Message}");
        }
        finally
        {
            _memory.Release();
        }
    }

    private async ValueTask CompleteAsync(PendingFetch pending)
    {
        var status = await pending.Task.ConfigureAwait(false);
        var group = pending.Group;
        if (status.Outcome == FetchOutcome.Failed)
        {
            foreach (var f in group.FeatureIndices)
            {
                _state[f] = FeatureState.Failed;
                _errors[f] = status.Error;
            }

            return;
        }

        var degraded = status.Outcome == FetchOutcome.Partial;
        foreach (var f in group.FeatureIndices)
        {
            var info = _plan.Features[f];
            if (_state[f] == FeatureState.Failed)
            {
                continue;
            }

            Backfill(pending, info);
            if (degraded)
            {
                _state[f] = FeatureState.Degraded;
                _errors[f] = status.Error;
            }
            else if (_state[f] == FeatureState.Unrequested)
            {
                _state[f] = FeatureState.Ok;
            }
        }
    }

    /// <summary>Copies staged values into the live rows that are still unfilled, matching by key (design 4.5).</summary>
    private void Backfill(PendingFetch pending, FeatureRuntimeInfo feature)
    {
        var column = _table.GetOrCreateSlot(feature.Slot);
        var filled = _table.GetFilled(feature.Slot);
        var staged = pending.Staging.Field(feature.FieldIndex);
        var copied = _memory.Rent<int>(Math.Max(1, _table.Rows.Count));
        var copiedCount = 0;
        foreach (var row in _table.Rows.Order)
        {
            if (Bits.Get(filled, row))
            {
                continue;
            }

            int keyIndex;
            if (pending.NoContextKey || !TryCandidateKey(pending.Group, row, out var key))
            {
                keyIndex = -1; // no key: legitimately missing (e.g. anonymous user, missing foreign key)
            }
            else if (!pending.Index.TryGetValue(key, out keyIndex))
            {
                continue; // row appended after this fetch started; a later fetch fills it
            }

            if (keyIndex >= 0 && pending.Staging.HasValue(feature.FieldIndex, keyIndex))
            {
                column.CopyFrom(staged, keyIndex, row);
                column.SetValid(row);
                copied[copiedCount++] = row;
            }
            else if (feature.Definition.HasDefault)
            {
                ColumnValues.Set(column, row, feature.Definition.Default);
            }
            else
            {
                column.SetMissing(row);
            }

            Bits.Set(filled, row);
        }

        if (copiedCount > 0 && ColumnValues.IsFloat(column) && feature.Definition.HasDefault)
        {
            NonFiniteReplaced += ColumnValues.SanitizeNonFinite(column, copied.AsSpan(0, copiedCount), feature.Definition.Default);
        }
    }

    private void ComputeDerived(FeatureRuntimeInfo feature)
    {
        var worst = FeatureState.Ok;
        foreach (var d in feature.Dependencies)
        {
            if (_state[d] > worst)
            {
                worst = _state[d];
                _errors[feature.Index] = _errors[d];
            }
        }

        if (worst == FeatureState.Failed)
        {
            _state[feature.Index] = FeatureState.Failed;
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

        _state[feature.Index] = worst == FeatureState.Unrequested ? FeatureState.Ok : worst;
        if (count == 0)
        {
            return;
        }

        var column = _table.GetOrCreateSlot(feature.Slot);
        var span = buffer.AsSpan(0, count);
        feature.Expression!.Evaluate(new EvalContext(_table, _bindings, _context, _params), span, column);
        if (feature.Definition.HasDefault)
        {
            foreach (var row in span)
            {
                if (!column.IsValid(row))
                {
                    ColumnValues.Set(column, row, feature.Definition.Default);
                }
            }

            if (ColumnValues.IsFloat(column))
            {
                NonFiniteReplaced += ColumnValues.SanitizeNonFinite(column, span, feature.Definition.Default);
            }
        }

        foreach (var row in span)
        {
            Bits.Set(filled, row);
        }
    }

    private async Task FetchContextAsync(List<int> closure, CancellationToken cancellationToken)
    {
        var member = Membership(closure);
        var seenGroup = new bool[_plan.Groups.Count];
        var groups = new List<FetchGroup>();
        foreach (var f in closure)
        {
            var info = _plan.Features[f];
            if (!info.IsDerived && info.GroupIndex >= 0 && !seenGroup[info.GroupIndex])
            {
                seenGroup[info.GroupIndex] = true;
                groups.Add(_plan.Groups[info.GroupIndex]);
            }
        }

        var fetches = new List<(FetchGroup Group, StagingBuffer Staging, Task<FetchStatus> Task, bool NoKey)>();
        foreach (var group in groups)
        {
            var v = group.ContextKeyId >= 0 ? _context[group.ContextKeyId] : ScalarValue.Missing;
            var staging = new StagingBuffer(group.Fields, 1, _memory);
            if (v.IsMissing)
            {
                fetches.Add((group, staging, Task.FromResult(FetchStatus.Ok), true));
                continue;
            }

            var key = v.Kind == StorageKind.Int64 ? EntityKey.FromInt64(v.AsInt64()) : EntityKey.FromString(v.AsString());
            var task = RunSingleAsync(group, key, staging, cancellationToken);
            _io.Add(task);
            fetches.Add((group, staging, task, false));
        }

        foreach (var (group, staging, task, noKey) in fetches)
        {
            var status = await task.ConfigureAwait(false);
            foreach (var f in group.FeatureIndices)
            {
                var info = _plan.Features[f];
                if (status.Outcome == FetchOutcome.Failed)
                {
                    _state[f] = FeatureState.Failed;
                    _errors[f] = status.Error;
                    continue;
                }

                var value = !noKey && staging.HasValue(info.FieldIndex, 0)
                    ? ColumnValues.Get(staging.Field(info.FieldIndex), 0)
                    : ScalarValue.Missing;
                if ((value.IsMissing || (value.Kind == StorageKind.Float64 && !double.IsFinite(value.AsFloat64()))) && info.Definition.HasDefault)
                {
                    value = info.Definition.Default;
                }

                _context[info.ContextId] = value;
                _state[f] = status.Outcome == FetchOutcome.Partial ? FeatureState.Degraded : FeatureState.Ok;
            }
        }

        foreach (var f in _plan.DerivedOrder)
        {
            var info = _plan.Features[f];
            if (!info.IsContext || !member[f])
            {
                continue;
            }

            var dependencyFailed = false;
            foreach (var d in info.Dependencies)
            {
                dependencyFailed |= _state[d] == FeatureState.Failed;
            }

            if (dependencyFailed)
            {
                _state[f] = FeatureState.Failed;
                continue;
            }

            var value = info.Expression!.EvaluateScalar(new EvalContext(null, null, _context, _params));
            if ((value.IsMissing || (value.Kind == StorageKind.Float64 && !double.IsFinite(value.AsFloat64()))) && info.Definition.HasDefault)
            {
                value = info.Definition.Default;
            }

            _context[info.ContextId] = value;
            _state[f] = FeatureState.Ok;
        }
    }

    private async Task<FetchStatus> RunSingleAsync(FetchGroup group, EntityKey key, StagingBuffer staging, CancellationToken cancellationToken)
    {
        var budget = Math.Min(group.TimeoutMs, RemainingMs());
        if (budget <= 0)
        {
            return FetchStatus.Failed("no time left before the request deadline");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);
        return await CallAsync(group, key, ReadOnlyMemory<EntityKey>.Empty, staging, cts.Token).ConfigureAwait(false);
    }

    private bool NeedsFill(FetchGroup group, int row)
    {
        foreach (var f in group.FeatureIndices)
        {
            var slot = _plan.Features[f].Slot;
            if (_state[f] != FeatureState.Failed && !Bits.Get(_table.GetFilled(slot), row))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryCandidateKey(FetchGroup group, int row, out EntityKey key)
    {
        if (group.KeyFeatureIndex < 0)
        {
            key = _table.GetKey(row);
            return true;
        }

        var keyFeature = _plan.Features[group.KeyFeatureIndex];
        var column = _table.TryGetSlot(keyFeature.Slot);
        if (column is null || !Bits.Get(_table.GetFilled(keyFeature.Slot), row) || !column.IsValid(row))
        {
            key = default;
            return false;
        }

        key = column switch
        {
            ScalarColumn<long> l => EntityKey.FromInt64(l[row]),
            ScalarColumn<int> i => EntityKey.FromInt64(i[row]),
            DictStringColumn s => EntityKey.FromString(s.Get(row)),
            _ => default,
        };
        return true;
    }

    private int RemainingMs()
    {
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _deadlineTimestamp);
        return (int)Math.Clamp(remaining.TotalMilliseconds, 0, int.MaxValue);
    }

    private sealed record PendingFetch(
        FetchGroup Group, Dictionary<EntityKey, int> Index, StagingBuffer Staging, Task<FetchStatus> Task, bool NoContextKey);
}
