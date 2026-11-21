using System.Collections.Immutable;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Records one pipeline run for a replay bundle (design 13.4) while it executes: every external call at the framework
/// call sites, every node decision with the timing facts it depended on, and the effective sampling.
/// </summary>
/// <remarks>
/// <para>Created only for requests that ask to be recorded; it allocates freely (design 4.8 item 3: trace and replay
/// capture are normal allocations). Calls finish on connector threads, so call records are added under a lock and
/// ordered by their start sequence when the run is built; node records come from the request's thread.</para>
/// <para>A call is registered when it begins, with what is known before it is sent (keys, input digests), and replaced
/// by its full record when it finishes. Building the run is a snapshot: a call still in flight is recorded as
/// <c>outstanding</c> and nothing that finishes afterwards is added, so an aborted request is recorded without waiting
/// for anything.</para>
/// <para>Values are copied out of pooled buffers when a call begins or finishes, while the call still holds the request
/// memory.</para>
/// </remarks>
internal sealed class RunRecorder : IFeatureCallHook
{
    private readonly object _gate = new();
    private readonly List<ReplayServiceCall> _services = new();
    private readonly List<ReplayFeatureCall> _features = new();

    // Calls begun and not yet finished, as the outstanding records a snapshot lists them as.
    private readonly Dictionary<int, ReplayServiceCall> _pendingServices = new();
    private readonly Dictionary<int, ReplayFeatureCall> _pendingFeatures = new();

    // The outputs each service call begun reads (empty: all), which are what its record keeps.
    private readonly Dictionary<int, string[]> _requested = new();
    private readonly List<ReplayNodeRecord> _nodes = new();
    private List<ReplayAttemptRecord>? _attempts;
    private double? _remainingMs;
    private bool?[]? _sampled;
    private CompiledTap?[]? _taps;
    private int _sequence;
    private bool _built;

    public bool Replays => false;

    public int Begin(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields)
    {
        var sequence = NextSequence();
        var requested = new List<ReplayField>(fields.Length);
        foreach (var field in fields)
        {
            requested.Add(new ReplayField { Name = field.Field, Type = field.Type.ToString() });
        }

        Pending(_pendingFeatures, sequence, new ReplayFeatureCall
        {
            Seq = sequence,
            Source = group.SourceName,
            Entity = group.Entity.Name,
            Keys = ReplayJson.Keys(batch),
            Status = ReplayJson.Outstanding,
            Error = ReplayJson.OutstandingError,
            Fields = requested,
        });
        return sequence;
    }

    public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
    {
        if (Begun(_pendingFeatures, call.Sequence) is not { } begun)
        {
            return; // the run was built while the call was in flight: it stays outstanding there
        }

        Dictionary<string, JsonElement>? recorded = null;
        if (values is not null)
        {
            recorded = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var row = new ScalarValue[call.Batch.Count];
            for (var f = 0; f < call.Fields.Length; f++)
            {
                for (var k = 0; k < row.Length; k++)
                {
                    row[k] = values.Get(f, k);
                }

                recorded[call.Fields[f].Field] = ReplayJson.Values(row);
            }
        }

        Finish(_pendingFeatures, _features, call.Sequence, new ReplayFeatureCall
        {
            Seq = begun.Seq,
            Source = begun.Source,
            Entity = begun.Entity,
            Keys = begun.Keys,
            Sent = call.Sent,
            Status = IoCall.OutcomeName(call.Outcome),
            Error = call.Status.Error,
            FailedKeys = call.Status.FailedKeys,
            DurationMs = call.Duration.TotalMilliseconds,
            Fields = begun.Fields,
            Values = recorded,
        });
    }

    public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields, IFeatureBatchWriter writer) =>
        throw new InvalidOperationException("A recorder does not replay calls.");

    /// <summary>
    /// Registers a service call about to be sent, with its input digests (taken now, before the client sees the inputs):
    /// one per item for a call about items, one of the whole call otherwise. Returns its sequence for <see cref="EndCall"/>.
    /// </summary>
    public int BeginCall(in ServiceCaller caller, IoTarget target, ServiceCall call)
    {
        var sequence = NextSequence();
        var order = ServiceInputDigest.NameOrder(call);
        var items = call.Items.Span;
        var digests = new List<string>(Math.Max(1, items.Length));
        if (!items.IsEmpty)
        {
            for (var r = 0; r < items.Length; r++)
            {
                digests.Add(ReplayJson.Hex(ServiceInputDigest.Row(call, order, r)));
            }
        }
        else
        {
            digests.Add(ReplayJson.Hex(ServiceInputDigest.Call(call, order)));
        }

        var begun = new ReplayServiceCall
        {
            Seq = sequence,
            Node = caller.Node,
            Source = caller.Source,
            Service = target.Name,
            Role = target.Role,
            Version = call.Version,
            BatchSize = call.BatchSize,
            Limit = call.Limit,
            Status = ReplayJson.Outstanding,
            Error = ReplayJson.OutstandingError,
            Keys = items.IsEmpty ? null : ReplayJson.Keys(items),
            InputDigests = digests,
        };
        lock (_gate)
        {
            if (!_built)
            {
                _pendingServices[sequence] = begun;
                _requested[sequence] = call.Outputs.ToArray();
            }
        }

        return sequence;
    }

    /// <summary>Records a finished service call, as the request saw it; its answer is copied only from a call that answered.</summary>
    /// <param name="cutOff">The node's budget or the caller cut the call off: the operator was thrown cancellation, which a replay throws too.</param>
    /// <param name="response">Closed, and read only when the call answered: an abandoned client may still be writing it.</param>
    /// <param name="status">
    /// The status the client returned (default when it did not answer): its generation details are recorded, so a replay
    /// returns the same status and the operator reports the same (a truncated answer, design 13.4 item 7).
    /// </param>
    /// <param name="degraded">Why the call left its node Degraded, which a replay reports again.</param>
    public void EndCall(
        int sequence, IoOutcome outcome, string? error, bool cutOff, TimeSpan duration, ServiceResponse response, in ServiceStatus status, string? degraded = null)
    {
        ReplayServiceCall? begun;
        string[]? requested;
        lock (_gate)
        {
            begun = !_built && _pendingServices.TryGetValue(sequence, out var call) ? call : null;
            requested = begun is not null && _requested.TryGetValue(sequence, out var names) ? names : null;
        }

        if (begun is null)
        {
            return; // the run's record is out: the call is outstanding there
        }

        var answered = outcome == IoOutcome.Ok;
        var tensors = new Dictionary<string, ReplayOutput>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var scores = new List<double>(answered ? response.Count : 0);
        if (answered)
        {
            foreach (var (name, values) in response.Tensors)
            {
                if (Reads(requested, name))
                {
                    tensors[name] = ReplayJson.Tensor(values);
                }
            }

            foreach (var (name, text) in response.Texts)
            {
                if (Reads(requested, name))
                {
                    texts[name] = text;
                }
            }

            foreach (var score in response.Scores)
            {
                scores.Add(score);
            }
        }

        var record = new ReplayServiceCall
        {
            Seq = begun.Seq,
            Node = begun.Node,
            Source = begun.Source,
            Service = begun.Service,
            Role = begun.Role,
            Version = begun.Version,
            BatchSize = begun.BatchSize,
            Limit = begun.Limit,
            Status = IoCall.OutcomeName(outcome),
            Error = error,
            Threw = cutOff,
            DurationMs = duration.TotalMilliseconds,
            Keys = begun.Keys,
            InputDigests = begun.InputDigests,
            Outputs = tensors,
            Texts = texts,
            Ids = answered && begun.Limit > 0 ? ReplayJson.Keys(response.Keys) : null,
            Scores = scores,
            OutputTokens = status.OutputTokens,
            Truncated = status.Truncated ? true : null,
            Degraded = degraded,
            Dropped = answered && response.Dropped > 0 ? response.Dropped : null,
        };
        lock (_gate)
        {
            // A call that finishes after the snapshot is left out: the run's record is already out.
            if (!_built && _pendingServices.Remove(sequence))
            {
                _requested.Remove(sequence);
                _services.Add(record);
            }
        }

        // Only the outputs the call reads: a recall reads none, only the items it adds.
        static bool Reads(string[]? requested, string name) => requested is not null && Array.IndexOf(requested, name) >= 0;
    }

    /// <summary>The time left when the current position passed its <c>when</c>.</summary>
    public void Remaining(double milliseconds) => _remainingMs = double.IsFinite(milliseconds) ? Math.Round(milliseconds, 3) : null;

    /// <summary>How the current position's node (or its fallback) ended relative to its budget.</summary>
    public void Attempt(string node, string timing, double budgetMs) =>
        (_attempts ??= new List<ReplayAttemptRecord>()).Add(new ReplayAttemptRecord { Node = node, Timing = timing, BudgetMs = Math.Round(budgetMs, 3) });

    /// <summary>Closes the current position with its outcome.</summary>
    public void Position(in NodeOutcome outcome, string category)
    {
        _nodes.Add(new ReplayNodeRecord
        {
            Node = outcome.NodeId,
            Outcome = category,
            Detail = outcome.Detail,
            ProducedBy = outcome.ProducedBy,
            DurationMs = outcome.DurationMs,
            RowsIn = outcome.RowsIn,
            RowsOut = outcome.RowsOut,
            RemainingMs = _remainingMs,
            Attempts = _attempts ?? new List<ReplayAttemptRecord>(),
        });
        _attempts = null;
        _remainingMs = null;
    }

    /// <summary>The request-level sampling decisions (design 8.2) and the tap variants they activated.</summary>
    public void Sampling(bool?[] sampled, CompiledTap?[] taps)
    {
        _sampled = sampled;
        _taps = taps;
    }

    /// <summary>
    /// Builds the run's record as a snapshot: calls still in flight are <c>outstanding</c>, and nothing that finishes
    /// afterwards is added. Never waits for a call. Call it once, while the request still holds its memory.
    /// </summary>
    /// <param name="result">What the run returned; for an aborted run, the failure its logs were published with.</param>
    /// <param name="logs">none, pipeline or captured: where the run's log batches went.</param>
    /// <param name="logsConsumed">Something took the run's logs, so it sampled: the groups' effective rates are their rates.
    /// Apart from <paramref name="logs"/>, since an aborted run that captured its logs sampled them and returned none.</param>
    /// <param name="aborted">The run was aborted (cancelled by its caller, or a defect) and returned no result.</param>
    public ReplayRunRecord Build(
        ExecutionPlan plan, PipelineRequest request, ScalarValue[]? parameters, PipelineResult result, string logs, bool logsConsumed, bool aborted = false)
    {
        var parameterValues = new Dictionary<string, ReplayTypedValue>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var leaf in plan.Schema.Params)
            {
                parameterValues[leaf.Name] = ReplayJson.Typed(leaf.Type, parameters[leaf.Id]);
            }
        }

        List<ReplayServiceCall> services;
        List<ReplayFeatureCall> features;
        lock (_gate)
        {
            _built = true;
            services = new List<ReplayServiceCall>(_services);
            services.AddRange(_pendingServices.Values);
            features = new List<ReplayFeatureCall>(_features);
            features.AddRange(_pendingFeatures.Values);
        }

        services.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        features.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        return new ReplayRunRecord
        {
            PipelineId = plan.PipelineId,
            ManifestId = plan.ManifestId,
            Role = "primary",
            Arms = new List<string>(result.AssignedArms),
            Params = parameterValues,
            Nodes = new List<ReplayNodeRecord>(_nodes),
            ServiceCalls = services,
            FeatureCalls = features,
            Sampling = BuildSampling(plan, request.Arms, logs, logsConsumed),
            Result = Result(plan, result, null, aborted),
        };
    }

    /// <summary>A result record: items with the output column types needed to decode them.</summary>
    /// <param name="aborted">The run or request was aborted: <paramref name="result"/> is the failure it was logged with.</param>
    public static ReplayResultRecord Result(ExecutionPlan plan, PipelineResult result, string? servedBy, bool aborted = false)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, expression) in plan.Output.Columns)
        {
            types[name] = plan.Schema.DeclaredType(expression).ToString();
        }

        var items = new List<ReplayItem>(result.Items.Count);
        foreach (var item in result.Items)
        {
            var columns = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            foreach (var (name, value) in item.Columns)
            {
                columns[name] = value.IsMissing ? null : ReplayJson.Value(value);
            }

            items.Add(new ReplayItem { Id = ReplayJson.Key(item.Id), Rank = item.Rank, Columns = columns });
        }

        return new ReplayResultRecord
        {
            ServedBy = servedBy,
            Succeeded = result.Succeeded,
            Aborted = aborted,
            Error = result.Error,
            Degraded = result.Degraded,
            ElapsedMs = result.ElapsedMs,
            ColumnTypes = types,
            Items = items,
            TriggeredExperiments = new List<string>(result.TriggeredExperiments),
            ColumnProducers = new Dictionary<string, string>(result.ColumnProducers, StringComparer.Ordinal),
        };
    }

    private int NextSequence() => Interlocked.Increment(ref _sequence) - 1;

    private void Pending<T>(Dictionary<int, T> pending, int sequence, T begun)
    {
        lock (_gate)
        {
            if (!_built)
            {
                pending[sequence] = begun;
            }
        }
    }

    /// <summary>The record of a call as it began; null once the run has been built (the call is outstanding there).</summary>
    private T? Begun<T>(Dictionary<int, T> pending, int sequence)
        where T : class
    {
        lock (_gate)
        {
            return !_built && pending.TryGetValue(sequence, out var begun) ? begun : null;
        }
    }

    private void Finish<T>(Dictionary<int, T> pending, List<T> finished, int sequence, T record)
    {
        lock (_gate)
        {
            // A call that finishes after the snapshot is left out: the run's record is already out.
            if (!_built && pending.Remove(sequence))
            {
                finished.Add(record);
            }
        }
    }

    private ReplaySamplingRecord BuildSampling(ExecutionPlan plan, in RequestArms arms, string logs, bool logsConsumed)
    {
        var log = plan.Log;
        var groups = new List<ReplayGroupSampling>(log.Groups.Length);
        foreach (var group in log.Groups)
        {
            var rate = Math.Clamp(group.Rate, 0.0, 1.0);
            groups.Add(new ReplayGroupSampling
            {
                Group = group.Name,
                Rate = group.Rate,
                EffectiveRate = logsConsumed ? rate : 0.0,
                Sampled = _sampled is { } sampled ? sampled[group.Index] : null,
            });
        }

        var taps = new List<ReplayTapSampling>(log.Taps.Length);
        for (var i = 0; i < log.Taps.Length; i++)
        {
            if (log.Taps[i].Select(arms) is not { } tap)
            {
                continue;
            }

            taps.Add(new ReplayTapSampling
            {
                Tap = tap.TapId,
                Group = log.Groups[tap.Group].Name,
                RowRate = tap.Rate,
                MaxRows = tap.MaxRows,
                Active = _taps is { } active && active[i] is not null,
            });
        }

        return new ReplaySamplingRecord { Logs = logs, Groups = groups, Taps = taps };
    }
}
