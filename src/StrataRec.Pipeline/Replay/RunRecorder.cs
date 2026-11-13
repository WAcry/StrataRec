using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Records one pipeline run for a replay bundle (design 13.4) while it executes: every external call at the framework
/// call sites, every node decision with the timing facts it depended on, and the effective sampling.
/// </summary>
/// <remarks>
/// <para>Created only for requests that ask to be recorded; it allocates freely (design 4.8 item 3: trace and replay
/// capture are normal allocations). Calls finish on connector threads, so call records are added under a lock and
/// ordered by their start sequence when the run is built; node records come from the request's thread.</para>
/// <para>Values are copied out of pooled buffers when a call finishes, while the call still holds the request memory.</para>
/// </remarks>
internal sealed class RunRecorder : IFeatureCallHook
{
    private readonly object _gate = new();
    private readonly List<ReplayCandidateCall> _candidates = new();
    private readonly List<ReplayFeatureCall> _features = new();
    private readonly List<ReplayModelCall> _models = new();
    private readonly List<ReplayNodeRecord> _nodes = new();
    private List<ReplayAttemptRecord>? _attempts;
    private double? _remainingMs;
    private bool?[]? _sampled;
    private CompiledTap?[]? _taps;
    private int _sequence;

    public bool Replays => false;

    public int NextSequence() => Interlocked.Increment(ref _sequence) - 1;

    public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
    {
        var batch = call.Batch;
        var rows = batch.KeyCount;
        var fields = new List<ReplayField>(call.Fields.Length);
        Dictionary<string, JsonElement>? recorded = null;
        if (values is not null)
        {
            recorded = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var row = new ScalarValue[rows];
        for (var f = 0; f < call.Fields.Length; f++)
        {
            var field = call.Fields[f];
            fields.Add(new ReplayField { Name = field.Field, Type = field.Type.ToString() });
            if (recorded is null)
            {
                continue;
            }

            for (var k = 0; k < rows; k++)
            {
                row[k] = values!.Get(f, k);
            }

            recorded[field.Field] = ReplayJson.Values(row);
        }

        var record = new ReplayFeatureCall
        {
            Seq = call.Sequence,
            Source = call.Group.SourceName,
            Entity = call.Group.Entity.Name,
            Shape = ReplayJson.ShapeName(batch.Shape),
            ContextKey = batch.Shape == KeyShape.Many ? null : ReplayJson.Key(batch.ContextKey),
            Keys = batch.Shape == KeyShape.Single ? null : ReplayJson.Keys(batch.CandidateKeys.Span),
            Sent = call.Sent,
            Status = IoCall.OutcomeName(call.Outcome),
            Error = call.Status.Error,
            FailedKeys = call.Status.FailedKeys,
            DurationMs = call.Duration.TotalMilliseconds,
            Fields = fields,
            Values = recorded,
        };
        lock (_gate)
        {
            _features.Add(record);
        }
    }

    public FetchStatus Replay(FetchGroup group, EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer) =>
        throw new InvalidOperationException("A recorder does not replay calls.");

    /// <summary>Records a candidate source call; <paramref name="buffer"/> is closed and still rented.</summary>
    public void CandidateCall(int sequence, string node, string source, int limit, IoOutcome outcome, string? error, TimeSpan duration, CandidateBuffer buffer)
    {
        var answered = outcome == IoOutcome.Ok;
        var scores = new List<double>(answered ? buffer.Count : 0);
        if (answered)
        {
            foreach (var score in buffer.Scores)
            {
                scores.Add(score);
            }
        }

        var record = new ReplayCandidateCall
        {
            Seq = sequence,
            Node = node,
            Source = source,
            Limit = limit,
            Status = IoCall.OutcomeName(outcome),
            Error = error,
            DurationMs = duration.TotalMilliseconds,
            Ids = answered ? ReplayJson.Keys(buffer.Keys) : null,
            Scores = scores,
        };
        lock (_gate)
        {
            _candidates.Add(record);
        }
    }

    /// <summary>Records a model call; outputs are copied only from a call that answered.</summary>
    /// <param name="thrown">The type of the exception the client threw, or null when it returned a status.</param>
    public void ModelCall(int sequence, string node, ModelCall call, EntityKey[]? keys, IoOutcome outcome, string? error, Type? thrown, TimeSpan duration,
        RecordingOutputWriter outputs)
    {
        var order = ModelInputDigest.NameOrder(call);
        var digests = new List<string>();
        if (keys is not null)
        {
            for (var r = 0; r < keys.Length; r++)
            {
                digests.Add(ReplayJson.Hex(ModelInputDigest.Row(call, order, r)));
            }
        }
        else
        {
            digests.Add(ReplayJson.Hex(ModelInputDigest.Call(call, order)));
        }

        var floats = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (outcome == IoOutcome.Ok)
        {
            foreach (var (name, values) in outputs.Floats())
            {
                floats[name] = ReplayJson.Floats(values.Span);
            }

            foreach (var (name, text) in outputs.Texts())
            {
                texts[name] = text;
            }
        }

        var record = new ReplayModelCall
        {
            Seq = sequence,
            Node = node,
            Model = call.Model,
            Version = call.Version,
            BatchSize = call.BatchSize,
            Status = IoCall.OutcomeName(outcome),
            Error = error,
            Threw = thrown is not null,
            ErrorType = thrown?.FullName,
            DurationMs = duration.TotalMilliseconds,
            Keys = keys is null ? null : ReplayJson.Keys(keys),
            InputDigests = digests,
            Outputs = floats,
            Texts = texts,
        };
        lock (_gate)
        {
            _models.Add(record);
        }
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

    /// <summary>Builds the run's record once it has a result.</summary>
    /// <param name="logs">none, pipeline or captured: where the run's log batches went.</param>
    public ReplayRunRecord Build(ExecutionPlan plan, PipelineRequest request, ScalarValue[]? parameters, PipelineResult result, string logs)
    {
        var parameterValues = new Dictionary<string, ReplayTypedValue>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var leaf in plan.Schema.Params)
            {
                parameterValues[leaf.Name] = ReplayJson.Typed(leaf.Type, parameters[leaf.Id]);
            }
        }

        List<ReplayCandidateCall> candidates;
        List<ReplayFeatureCall> features;
        List<ReplayModelCall> models;
        lock (_gate)
        {
            candidates = new List<ReplayCandidateCall>(_candidates);
            features = new List<ReplayFeatureCall>(_features);
            models = new List<ReplayModelCall>(_models);
        }

        candidates.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        features.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        models.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        return new ReplayRunRecord
        {
            PipelineId = plan.PipelineId,
            ManifestId = plan.ManifestId,
            Role = "primary",
            Arms = new List<string>(result.AssignedArms),
            Params = parameterValues,
            Nodes = new List<ReplayNodeRecord>(_nodes),
            CandidateCalls = candidates,
            FeatureCalls = features,
            ModelCalls = models,
            Sampling = BuildSampling(plan, request.Arms, logs),
            Result = Result(plan, result, null),
        };
    }

    /// <summary>A result record: items with the output column types needed to decode them.</summary>
    public static ReplayResultRecord Result(ExecutionPlan plan, PipelineResult result, string? servedBy)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, expression) in plan.Output.Columns)
        {
            types[name] = expression.ResultType.ToString();
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
            Error = result.Error,
            Degraded = result.Degraded,
            ElapsedMs = result.ElapsedMs,
            ColumnTypes = types,
            Items = items,
            TriggeredExperiments = new List<string>(result.TriggeredExperiments),
            ColumnProducers = new Dictionary<string, string>(result.ColumnProducers, StringComparer.Ordinal),
        };
    }

    private ReplaySamplingRecord BuildSampling(ExecutionPlan plan, in RequestArms arms, string logs)
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
                EffectiveRate = logs == "none" ? 0.0 : rate,
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
