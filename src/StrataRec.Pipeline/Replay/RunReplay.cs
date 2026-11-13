using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Models;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Data a replay needed and the bundle did not have (design 13.4). The replay treated it as missing (a feature value, a
/// candidate list, a model output) and never invented it; this is the list a comparison report shows.
/// </summary>
/// <param name="Kind"><c>feature</c>, <c>candidate_source</c> or <c>model</c>.</param>
/// <param name="Source">Feature source, candidate source or model.</param>
/// <param name="Field">Feature field, when the gap is about one.</param>
/// <param name="Reason">
/// <c>not_recorded</c> (never fetched, or only by a call that failed), <c>type_changed</c> (the field's type changed
/// incompatibly), <c>limit_exceeds_recording</c> (a larger quota than the recording could answer) or
/// <c>inputs_changed</c> (model inputs differ from the recorded ones, so the recorded output does not apply).
/// </param>
/// <param name="Count">Keys, candidates or rows affected.</param>
/// <param name="Examples">Up to five affected keys (or nodes).</param>
public sealed record ReplayGap(string Kind, string Source, string? Field, string Reason, int Count, IReadOnlyList<string> Examples)
{
    public string Description => (Kind, Reason) switch
    {
        ("feature", "type_changed") => $"feature field {Source}/{Field}: {Count} recorded value(s) do not fit its new type; treated as missing",
        ("feature", _) => $"feature field {Source}/{Field}: {Count} key(s) have no recorded value; treated as missing",
        ("candidate_source", "limit_exceeds_recording") => $"candidate source {Source}: asked for {Count} more candidate(s) than the recording holds",
        ("candidate_source", _) => $"candidate source {Source} was not called in the recording; it returned no candidates",
        ("model", "inputs_changed") => $"model {Source}: {Count} row(s) have different inputs than recorded; their outputs are missing",
        ("model", _) => $"model {Source}: {Count} row(s) or call(s) have no recorded output; treated as missing",
        _ => $"{Kind} {Source}: {Reason} ({Count})",
    };
}

/// <summary>How many values of one feature field a replay requested and how many came back missing.</summary>
public sealed record ReplayFieldStats(string Source, string Field, long Requested, long Missing)
{
    public double MissingRate => Requested == 0 ? 0.0 : (double)Missing / Requested;
}

/// <summary>An exception a recorded call threw, thrown again by its replay with the original message.</summary>
public sealed class ReplayedCallException(string message) : Exception(message);

/// <summary>
/// Replays one recorded pipeline run (design 13.4): answers feature, candidate and model calls from the recording and
/// supplies the recorded timing facts (time left, budget timeouts) in place of the clock, so the run makes the same
/// decisions without calling or waiting for anything.
/// </summary>
/// <remarks>
/// <para>Calls are matched exactly first (same source and keys, in recorded order), which replays the recorded status:
/// a timeout times out again. A call the recording does not have exactly (a modified configuration asks for other keys,
/// fields, batches or sources) is answered value by value from everything the recording fetched successfully; what it
/// lacks is missing and reported as a <see cref="ReplayGap"/>.</para>
/// <para>One instance per replayed run; not reusable.</para>
/// </remarks>
public sealed class RunReplay : IFeatureCallHook
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, List<FeatureEntry>> _featureCalls = new();
    private readonly Dictionary<(string Source, string Entity), Dictionary<RowKey, Dictionary<string, ScalarValue>>> _featureValues = new();
    private readonly List<CandidateEntry> _candidateCalls = new();
    private readonly Dictionary<(string Model, string? Version), List<ModelEntry>> _modelCalls = new();
    private readonly Dictionary<string, double> _remaining = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<ReplayAttemptRecord>> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Kind, string Source, string? Field, string Reason), GapCounter> _gaps = new();
    private readonly Dictionary<(string Source, string Field), long[]> _stats = new();

    private RunReplay(ReplayRunRecord? recorded) => Recorded = recorded;

    /// <summary>The recording being replayed; null when the bundle had no run for this pipeline.</summary>
    public ReplayRunRecord? Recorded { get; }

    /// <summary>The deadline a replayed run executes with: long enough never to decide anything; timing comes from the recording.</summary>
    public static TimeSpan Deadline => TimeSpan.FromHours(1);

    public bool Replays => true;

    /// <summary>A replay of a run the bundle does not contain (a fallback pipeline that did not run): every call is a gap.</summary>
    public static RunReplay Empty() => new(null);

    /// <summary>Indexes a recorded run, decoding every value with its recorded type.</summary>
    /// <param name="pointer">Pointer of <paramref name="run"/> in its bundle, for errors.</param>
    /// <exception cref="ReplayBundleException">A value, key, type or status in the run is malformed.</exception>
    public static RunReplay Create(ReplayRunRecord run, string pointer)
    {
        var replay = new RunReplay(run);
        replay.IndexNodes(run, pointer);
        replay.IndexFeatures(run, pointer);
        replay.IndexCandidates(run, pointer);
        replay.IndexModels(run, pointer);
        return replay;
    }

    /// <summary>Everything the replay lacked so far, in a stable order.</summary>
    public IReadOnlyList<ReplayGap> Gaps
    {
        get
        {
            lock (_gate)
            {
                var gaps = new List<ReplayGap>(_gaps.Count);
                foreach (var ((kind, source, field, reason), counter) in _gaps)
                {
                    gaps.Add(new ReplayGap(kind, source, field, reason, counter.Count, counter.Examples));
                }

                gaps.Sort(static (a, b) =>
                {
                    var c = string.CompareOrdinal(a.Kind, b.Kind);
                    c = c != 0 ? c : string.CompareOrdinal(a.Source, b.Source);
                    c = c != 0 ? c : string.CompareOrdinal(a.Field, b.Field);
                    return c != 0 ? c : string.CompareOrdinal(a.Reason, b.Reason);
                });
                return gaps;
            }
        }
    }

    /// <summary>Requested and missing values per feature field (the feature-missing rate of design 10.8).</summary>
    public IReadOnlyList<ReplayFieldStats> FieldStats
    {
        get
        {
            lock (_gate)
            {
                var stats = new List<ReplayFieldStats>(_stats.Count);
                foreach (var ((source, field), counts) in _stats)
                {
                    stats.Add(new ReplayFieldStats(source, field, counts[0], counts[1]));
                }

                stats.Sort(static (a, b) => string.CompareOrdinal(a.Source, b.Source) is var c && c != 0 ? c : string.CompareOrdinal(a.Field, b.Field));
                return stats;
            }
        }
    }

    public int NextSequence() => 0;

    public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
    {
    }

    public FetchStatus Replay(FetchGroup group, EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer)
    {
        lock (_gate)
        {
            var source = group.SourceName;
            var keys = batch.CandidateKeys.Span;
            var context = batch.Shape == KeyShape.Many ? default : batch.ContextKey;
            var rows = batch.KeyCount;
            var entry = TakeFeatureCall(source, group.Entity.Name, batch.Shape, context, keys, fields);
            if (entry is not null)
            {
                return ReplayExact(entry, batch, fields, writer);
            }

            // Not in the recording as such: answer value by value from what the recording fetched successfully.
            _featureValues.TryGetValue((source, group.Entity.Name), out var store);
            for (var f = 0; f < fields.Length; f++)
            {
                var name = fields[f].Field;
                long missing = 0;
                GapCounter? gap = null;
                for (var r = 0; r < rows; r++)
                {
                    var rowKey = RowKeyOf(batch.Shape, context, keys, r);
                    if (store is not null && store.TryGetValue(rowKey, out var byField) && byField.TryGetValue(name, out var value))
                    {
                        if (value.IsMissing)
                        {
                            missing++;
                        }
                        else if (!TryWrite(writer, f, r, fields[f].Type, value))
                        {
                            missing++;
                            AddGap("feature", source, name, "type_changed", 1, rowKey.ToString());
                        }

                        continue;
                    }

                    missing++;
                    gap ??= Gap("feature", source, name, "not_recorded");
                    gap.Add(1, rowKey.ToString());
                }

                AddStats(source, name, rows, missing);
            }

            return FetchStatus.Ok;
        }
    }

    /// <summary>
    /// Answers a candidate source call; <paramref name="buffer"/> is filled up to its limit. The node's own recorded call
    /// of the source is replayed exactly, status included, and consumed. A node the recording has no (unconsumed) call
    /// for, such as a node the configuration added, gets a copy of the longest list the source returned to any node, and
    /// consumes nothing: another node's call stays that node's, whatever order the nodes ask in.
    /// </summary>
    internal CandidateSourceStatus Retrieve(string node, string source, CandidateQuery query, CandidateBuffer buffer)
    {
        lock (_gate)
        {
            CandidateEntry? exact = null;
            foreach (var candidate in _candidateCalls)
            {
                if (!candidate.Consumed && candidate.Source == source && candidate.Node == node)
                {
                    exact = candidate;
                    break;
                }
            }

            if (exact is not null)
            {
                exact.Consumed = true;
                if (exact.Outcome != IoOutcome.Ok)
                {
                    return CandidateSourceStatus.Failed(exact.Error ?? "timed out");
                }

                Copy(exact, node, query, buffer);
                return CandidateSourceStatus.Ok;
            }

            // The data of a successful call; a failure belongs to the call that failed.
            CandidateEntry? longest = null;
            foreach (var candidate in _candidateCalls)
            {
                if (candidate.Source == source && candidate.Outcome == IoOutcome.Ok && (longest is null || candidate.Ids.Length > longest.Ids.Length))
                {
                    longest = candidate;
                }
            }

            if (longest is null)
            {
                AddGap("candidate_source", source, null, "not_recorded", 1, node);
                return CandidateSourceStatus.Ok;
            }

            Copy(longest, node, query, buffer);
            return CandidateSourceStatus.Ok;
        }
    }

    /// <summary>
    /// Copies a recorded list up to the query's limit. Asking for more than the recording holds is a shortfall only when
    /// the recorded call stopped at its own limit (the source might have had more); a source that returned fewer had none.
    /// </summary>
    private void Copy(CandidateEntry entry, string node, CandidateQuery query, CandidateBuffer buffer)
    {
        var count = Math.Min(entry.Ids.Length, query.Limit);
        for (var i = 0; i < count; i++)
        {
            buffer.Add(entry.Ids[i], entry.Scores[i]);
        }

        if (query.Limit > entry.Ids.Length && entry.Ids.Length >= entry.Limit)
        {
            AddGap("candidate_source", entry.Source, null, "limit_exceeds_recording", query.Limit - entry.Ids.Length, node);
        }
    }

    /// <summary>
    /// Answers a model call. Keyed calls (<paramref name="keys"/>: the entity of each batch row) are matched per row, and a
    /// recorded output is used only where the row's inputs are unchanged. A recorded failure is replayed: returned, or
    /// thrown when the client threw (a timeout as <see cref="OperationCanceledException"/>).
    /// </summary>
    internal ModelStatus InvokeModel(ModelCall call, IModelOutputWriter outputs, EntityKey[]? keys)
    {
        lock (_gate)
        {
            var order = ModelInputDigest.NameOrder(call);
            _modelCalls.TryGetValue((call.Model, call.Version), out var calls);
            if (keys is null)
            {
                var digest = ReplayJson.Hex(ModelInputDigest.Call(call, order));
                var unkeyed = Take(calls, entry => entry.Keys is null && entry.Digests.Length == 1 && entry.Digests[0] == digest);
                if (unkeyed is null)
                {
                    AddGap("model", call.Model, null, "not_recorded", 1, null);
                    return ModelStatus.Failed($"model '{call.Model}': this call is not in the replay bundle");
                }

                return Answer(unkeyed, outputs, call.BatchSize, null);
            }

            var digests = new string[keys.Length];
            for (var r = 0; r < keys.Length; r++)
            {
                digests[r] = ReplayJson.Hex(ModelInputDigest.Row(call, order, r));
            }

            var exact = Take(calls, entry => entry.Keys is not null && entry.Keys.AsSpan().SequenceEqual(keys));
            if (exact is not null)
            {
                return Answer(exact, outputs, call.BatchSize, digests);
            }

            return Synthesize(call, calls, outputs, keys, digests);
        }
    }

    /// <summary>Time left when the node passed its <c>when</c> in the recording; infinite for a node the recording lacks.</summary>
    internal double RemainingMs(string node) => _remaining.TryGetValue(node, out var ms) ? ms : double.PositiveInfinity;

    /// <summary>The next recorded attempt of <paramref name="node"/>, or null when the recording has none left.</summary>
    internal ReplayAttemptRecord? NextAttempt(string node) =>
        _attempts.TryGetValue(node, out var attempts) && attempts.Count > 0 ? attempts.Dequeue() : null;

    private FetchStatus ReplayExact(FeatureEntry entry, EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer)
    {
        var source = entry.Source;
        var rows = batch.KeyCount;
        if (entry.Outcome is not (IoOutcome.Ok or IoOutcome.Partial))
        {
            foreach (var field in fields)
            {
                AddStats(source, field.Field, rows, rows);
            }

            return FetchStatus.Failed(entry.Error ?? $"source '{source}' failed");
        }

        for (var f = 0; f < fields.Length; f++)
        {
            var name = fields[f].Field;
            var recorded = Array.IndexOf(entry.FieldNames, name);
            long missing = 0;
            if (recorded < 0 || entry.Values is null)
            {
                missing = rows;
                var gap = Gap("feature", source, name, "not_recorded");
                for (var r = 0; r < rows; r++)
                {
                    gap.Add(1, RowKeyOf(entry.Shape, entry.Context, entry.Keys, r).ToString());
                }
            }
            else
            {
                var values = entry.Values[recorded];
                for (var r = 0; r < rows; r++)
                {
                    if (values[r].IsMissing)
                    {
                        missing++;
                    }
                    else if (!TryWrite(writer, f, r, fields[f].Type, values[r]))
                    {
                        missing++;
                        AddGap("feature", source, name, "type_changed", 1, RowKeyOf(entry.Shape, entry.Context, entry.Keys, r).ToString());
                    }
                }
            }

            AddStats(source, name, rows, missing);
        }

        return entry.Outcome == IoOutcome.Partial ? new FetchStatus(FetchOutcome.Partial, entry.FailedKeys, entry.Error) : FetchStatus.Ok;
    }

    /// <summary>
    /// The first unconsumed recorded call with the same source, entity, shape and keys that carries every requested
    /// field. A call carrying only some is not this call: its other fields may well be in other successful calls (the
    /// configuration regrouped the fetch), so the call is synthesized value by value instead, and nothing is consumed.
    /// </summary>
    private FeatureEntry? TakeFeatureCall(string source, string entity, KeyShape shape, in EntityKey context, ReadOnlySpan<EntityKey> keys, CompiledFieldBinding[] fields)
    {
        if (!_featureCalls.TryGetValue(Signature(source, entity, shape, context, keys), out var candidates))
        {
            return null;
        }

        foreach (var entry in candidates)
        {
            if (entry.Consumed || entry.Source != source || entry.Entity != entity || entry.Shape != shape || entry.Context != context
                || !entry.Keys.AsSpan().SequenceEqual(keys) || !HasFields(entry, fields))
            {
                continue;
            }

            entry.Consumed = true;
            return entry;
        }

        return null;

        static bool HasFields(FeatureEntry entry, CompiledFieldBinding[] fields)
        {
            foreach (var field in fields)
            {
                if (Array.IndexOf(entry.FieldNames, field.Field) < 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private ModelStatus Answer(ModelEntry entry, IModelOutputWriter outputs, int batchSize, string[]? digests)
    {
        if (entry.Outcome != IoOutcome.Ok)
        {
            if (entry.Threw)
            {
                if (entry.Outcome is IoOutcome.Timeout or IoOutcome.Cancelled)
                {
                    throw new OperationCanceledException($"model '{entry.Model}' timed out in the recording");
                }

                throw new ReplayedCallException(entry.Error ?? $"model '{entry.Model}' threw in the recording");
            }

            return ModelStatus.Failed(entry.Error ?? $"model '{entry.Model}' failed in the recording");
        }

        GapCounter? changed = null;
        foreach (var (name, values) in entry.Outputs)
        {
            var perRow = entry.BatchSize > 0 ? values.Length / entry.BatchSize : values.Length;
            var destination = outputs.GetFloat32(name, digests is null ? values.Length : batchSize * perRow).Span;
            if (digests is null)
            {
                values.CopyTo(destination);
                continue;
            }

            for (var r = 0; r < batchSize; r++)
            {
                var slice = destination.Slice(r * perRow, perRow);
                if (digests[r] == entry.Digests[r])
                {
                    values.AsSpan(r * perRow, perRow).CopyTo(slice);
                }
                else
                {
                    slice.Fill(float.NaN);
                }
            }
        }

        if (digests is not null)
        {
            for (var r = 0; r < batchSize; r++)
            {
                if (digests[r] != entry.Digests[r])
                {
                    changed ??= Gap("model", entry.Model, null, "inputs_changed");
                    changed.Add(1, entry.Keys![r].ToString());
                }
            }
        }

        foreach (var (name, text) in entry.Texts)
        {
            outputs.SetText(name, text);
        }

        return ModelStatus.Ok;
    }

    /// <summary>A keyed call the recording does not have as such: each row's output comes from whichever recorded call scored it.</summary>
    private ModelStatus Synthesize(ModelCall call, List<ModelEntry>? calls, IModelOutputWriter outputs, EntityKey[] keys, string[] digests)
    {
        // Output layout (names and values per row) from the recorded calls of the same model and version.
        var layout = new List<(string Name, int PerRow)>();
        var rowsByKey = new Dictionary<EntityKey, (ModelEntry Entry, int Row)>();
        if (calls is not null)
        {
            foreach (var entry in calls)
            {
                if (entry.Outcome != IoOutcome.Ok || entry.Keys is null)
                {
                    continue;
                }

                for (var r = 0; r < entry.Keys.Length; r++)
                {
                    rowsByKey.TryAdd(entry.Keys[r], (entry, r));
                }

                foreach (var (name, values) in entry.Outputs)
                {
                    if (!layout.Exists(o => o.Name == name))
                    {
                        layout.Add((name, entry.BatchSize > 0 ? values.Length / entry.BatchSize : 1));
                    }
                }
            }
        }

        if (layout.Count == 0)
        {
            var gap = Gap("model", call.Model, null, "not_recorded");
            foreach (var key in keys)
            {
                gap.Add(1, key.ToString());
            }

            return ModelStatus.Failed($"model '{call.Model}' has no recorded output in the replay bundle");
        }

        GapCounter? missing = null;
        GapCounter? changed = null;
        foreach (var (name, perRow) in layout)
        {
            var destination = outputs.GetFloat32(name, keys.Length * perRow).Span;
            for (var r = 0; r < keys.Length; r++)
            {
                var slice = destination.Slice(r * perRow, perRow);
                if (rowsByKey.TryGetValue(keys[r], out var recorded) && recorded.Entry.Digests[recorded.Row] == digests[r]
                    && recorded.Entry.Outputs.TryGetValue(name, out var values) && values.Length >= (recorded.Row + 1) * perRow)
                {
                    values.AsSpan(recorded.Row * perRow, perRow).CopyTo(slice);
                }
                else
                {
                    slice.Fill(float.NaN);
                }
            }
        }

        for (var r = 0; r < keys.Length; r++)
        {
            if (!rowsByKey.TryGetValue(keys[r], out var recorded))
            {
                (missing ??= Gap("model", call.Model, null, "not_recorded")).Add(1, keys[r].ToString());
            }
            else if (recorded.Entry.Digests[recorded.Row] != digests[r])
            {
                (changed ??= Gap("model", call.Model, null, "inputs_changed")).Add(1, keys[r].ToString());
            }
        }

        return ModelStatus.Ok;
    }

    private static ModelEntry? Take(List<ModelEntry>? calls, Predicate<ModelEntry> match)
    {
        if (calls is null)
        {
            return null;
        }

        foreach (var entry in calls)
        {
            if (!entry.Consumed && match(entry))
            {
                entry.Consumed = true;
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes a recorded value into a staging field; false (nothing written) when it does not fit. The value was decoded
    /// with its recorded type; a field whose type changed takes it only when it converts without loss of meaning: numeric
    /// widening, rounding to float32 as the source itself would, string ↔ text. A value outside the new type's range (an
    /// integer beyond int32, a finite number beyond float32) does not fit: storing it would publish a wrapped value or an
    /// infinity the recording never had. Exact and synthesized calls both write through here, so they apply one rule.
    /// </summary>
    private static bool TryWrite(IFeatureBatchWriter writer, int field, int key, ColumnType type, in ScalarValue value)
    {
        switch (type.Kind)
        {
            case StorageKind.Bool when value.Kind == StorageKind.Bool:
                writer.GetValues<bool>(field).Span[key] = value.AsBool();
                break;
            case StorageKind.Int32 when value.Kind == StorageKind.Int64 && Int32Range.Fits(value.AsInt64()):
                writer.GetValues<int>(field).Span[key] = (int)value.AsInt64();
                break;
            case StorageKind.Int64 when value.Kind == StorageKind.Int64:
                writer.GetValues<long>(field).Span[key] = value.AsInt64();
                break;
            case StorageKind.Float32 when value.Kind is StorageKind.Float64 or StorageKind.Int64 && FitsFloat32(value.AsFloat64()):
                writer.GetValues<float>(field).Span[key] = (float)value.AsFloat64();
                break;
            case StorageKind.Float64 when value.Kind is StorageKind.Float64 or StorageKind.Int64:
                writer.GetValues<double>(field).Span[key] = value.AsFloat64();
                break;
            case StorageKind.String or StorageKind.Text when value.Kind is StorageKind.String or StorageKind.Text:
                writer.SetString(field, key, value.AsString());
                return true;
            case StorageKind.Vector when value.Kind == StorageKind.Vector && value.AsVector().Length == type.Dimension:
                value.AsVector().CopyTo(writer.GetValues<float>(field).Span.Slice(key * type.Dimension, type.Dimension));
                break;
            case StorageKind.ListInt64 when value.Kind == StorageKind.ListInt64:
                writer.SetInt64List(field, key, value.AsInt64List());
                return true;
            case StorageKind.ListInt32 when value.Kind == StorageKind.ListInt64 && Int32Range.AllFit(value.AsInt64List()):
                var longs = value.AsInt64List();
                var ints = new int[longs.Length];
                for (var i = 0; i < ints.Length; i++)
                {
                    ints[i] = (int)longs[i];
                }

                writer.SetInt32List(field, key, ints);
                return true;
            case StorageKind.ListString when value.Kind == StorageKind.ListString:
                writer.SetStringList(field, key, value.AsStringList());
                return true;
            default:
                return false;
        }

        writer.SetValid(field, key);
        return true;
    }

    /// <summary>NaN and ±∞ stay what they are; a finite value must not overflow to an infinity.</summary>
    private static bool FitsFloat32(double value) => !double.IsFinite(value) || float.IsFinite((float)value);

    private static RowKey RowKeyOf(KeyShape shape, in EntityKey context, ReadOnlySpan<EntityKey> keys, int row) => shape switch
    {
        KeyShape.Single => new RowKey(context, default),
        KeyShape.Many => new RowKey(default, keys[row]),
        _ => new RowKey(context, keys[row]),
    };

    private static ulong Signature(string source, string entity, KeyShape shape, in EntityKey context, ReadOnlySpan<EntityKey> keys)
    {
        var builder = default(StableHash.Builder);
        try
        {
            builder.Add(source);
            builder.Add(entity);
            builder.AddIndex((int)shape);
            AddKey(ref builder, context);
            builder.AddIndex(keys.Length);
            foreach (var key in keys)
            {
                AddKey(ref builder, key);
            }

            return builder.Finish();
        }
        finally
        {
            builder.Dispose();
        }

        static void AddKey(ref StableHash.Builder builder, in EntityKey key)
        {
            if (key.StringValue is { } s)
            {
                builder.Add(s);
            }
            else
            {
                builder.Add(key.Int64Value);
            }
        }
    }

    private GapCounter Gap(string kind, string source, string? field, string reason)
    {
        if (!_gaps.TryGetValue((kind, source, field, reason), out var counter))
        {
            counter = new GapCounter();
            _gaps[(kind, source, field, reason)] = counter;
        }

        return counter;
    }

    private void AddGap(string kind, string source, string? field, string reason, int count, string? example) =>
        Gap(kind, source, field, reason).Add(count, example);

    private void AddStats(string source, string field, long requested, long missing)
    {
        if (!_stats.TryGetValue((source, field), out var counts))
        {
            counts = new long[2];
            _stats[(source, field)] = counts;
        }

        counts[0] += requested;
        counts[1] += missing;
    }

    private void IndexNodes(ReplayRunRecord run, string pointer)
    {
        for (var i = 0; i < run.Nodes.Count; i++)
        {
            var node = run.Nodes[i];
            if (node.RemainingMs is { } remaining)
            {
                _remaining.TryAdd(node.Node, remaining);
            }

            for (var a = 0; a < node.Attempts.Count; a++)
            {
                var attempt = node.Attempts[a];
                if (attempt.Timing is not (ReplayTiming.Ran or ReplayTiming.DeadlineExceeded or ReplayTiming.TimedOut))
                {
                    throw new ReplayBundleException($"{pointer}/nodes/{i}/attempts/{a}/timing", $"unknown timing '{attempt.Timing}'",
                        "use ran, deadline_exceeded or timed_out");
                }

                if (!_attempts.TryGetValue(attempt.Node, out var queue))
                {
                    queue = new Queue<ReplayAttemptRecord>();
                    _attempts[attempt.Node] = queue;
                }

                queue.Enqueue(attempt);
            }
        }
    }

    private void IndexFeatures(ReplayRunRecord run, string pointer)
    {
        for (var c = 0; c < run.FeatureCalls.Count; c++)
        {
            var call = run.FeatureCalls[c];
            var at = $"{pointer}/feature_calls/{c}";
            var shape = call.Shape switch
            {
                "single" => KeyShape.Single,
                "many" => KeyShape.Many,
                "cross" => KeyShape.Cross,
                "context_map" => KeyShape.ContextMap,
                _ => throw new ReplayBundleException(at + "/shape", $"unknown shape '{call.Shape}'", "use single, many, cross or context_map"),
            };
            var context = shape == KeyShape.Many || call.ContextKey is not { } contextKey ? default : ReplayJson.Key(contextKey, at + "/context_key");
            var keys = shape == KeyShape.Single ? Array.Empty<EntityKey>() : ReplayJson.Keys(call.Keys, at + "/keys");
            var rows = shape == KeyShape.Single ? 1 : keys.Length;
            var names = new string[call.Fields.Count];
            var types = new ColumnType[call.Fields.Count];
            for (var f = 0; f < names.Length; f++)
            {
                names[f] = call.Fields[f].Name;
                types[f] = ReplayJson.Type(call.Fields[f].Type, $"{at}/fields/{f}/type");
            }

            var outcome = ReplayJson.Outcome(call.Status, at + "/status");
            ScalarValue[][]? values = null;
            if (outcome is IoOutcome.Ok or IoOutcome.Partial)
            {
                if (call.Values is null)
                {
                    throw new ReplayBundleException(at, $"a call with status '{call.Status}' needs values", "record values for every requested field");
                }

                values = new ScalarValue[names.Length][];
                for (var f = 0; f < names.Length; f++)
                {
                    if (!call.Values.TryGetValue(names[f], out var array))
                    {
                        throw new ReplayBundleException(at + "/values", $"field '{names[f]}' has no values", "record one value (or null) per key for every requested field");
                    }

                    values[f] = ReplayJson.Values(array, types[f], rows, $"{at}/values/{JsonPointer.Escape(names[f])}");
                }
            }

            var entry = new FeatureEntry(call.Source, call.Entity, shape, context, keys, outcome, call.Error, call.FailedKeys, names, values);
            var signature = Signature(call.Source, call.Entity, shape, context, keys);
            if (!_featureCalls.TryGetValue(signature, out var list))
            {
                list = new List<FeatureEntry>();
                _featureCalls[signature] = list;
            }

            list.Add(entry);
            if (values is null)
            {
                continue;
            }

            if (!_featureValues.TryGetValue((call.Source, call.Entity), out var store))
            {
                store = new Dictionary<RowKey, Dictionary<string, ScalarValue>>();
                _featureValues[(call.Source, call.Entity)] = store;
            }

            for (var r = 0; r < rows; r++)
            {
                var rowKey = RowKeyOf(shape, context, keys, r);
                if (!store.TryGetValue(rowKey, out var byField))
                {
                    byField = new Dictionary<string, ScalarValue>(StringComparer.Ordinal);
                    store[rowKey] = byField;
                }

                for (var f = 0; f < names.Length; f++)
                {
                    // A value another call returned wins over a missing one: a partial call leaves its failed keys missing.
                    if (!byField.TryGetValue(names[f], out var existing) || existing.IsMissing)
                    {
                        byField[names[f]] = values[f][r];
                    }
                }
            }
        }
    }

    private void IndexCandidates(ReplayRunRecord run, string pointer)
    {
        for (var c = 0; c < run.CandidateCalls.Count; c++)
        {
            var call = run.CandidateCalls[c];
            var at = $"{pointer}/candidate_calls/{c}";
            var outcome = ReplayJson.Outcome(call.Status, at + "/status");
            var ids = ReplayJson.Keys(call.Ids, at + "/ids");
            if (ids.Length != call.Scores.Count)
            {
                throw new ReplayBundleException(at + "/scores", $"{call.Scores.Count} score(s) for {ids.Length} id(s)", "record one score per id");
            }

            _candidateCalls.Add(new CandidateEntry(call.Seq, call.Node, call.Source, call.Limit, outcome, call.Error, ids, call.Scores.ToArray()));
        }

        _candidateCalls.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
    }

    private void IndexModels(ReplayRunRecord run, string pointer)
    {
        for (var c = 0; c < run.ModelCalls.Count; c++)
        {
            var call = run.ModelCalls[c];
            var at = $"{pointer}/model_calls/{c}";
            var outcome = ReplayJson.Outcome(call.Status, at + "/status");
            EntityKey[]? keys = call.Keys is null ? null : ReplayJson.Keys(call.Keys, at + "/keys");
            var expectedDigests = keys?.Length ?? 1;
            if (call.InputDigests.Count != expectedDigests)
            {
                throw new ReplayBundleException(at + "/input_digests", $"expected {expectedDigests} digest(s), got {call.InputDigests.Count}",
                    "record one digest per key, or one for a call without keys");
            }

            var outputs = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var (name, values) in call.Outputs)
            {
                outputs[name] = ReplayJson.Floats(values, $"{at}/outputs/{JsonPointer.Escape(name)}");
            }

            var entry = new ModelEntry(call.Seq, call.Model, call.BatchSize, outcome, call.Error, call.Threw, keys, call.InputDigests.ToArray(), outputs,
                new Dictionary<string, string>(call.Texts, StringComparer.Ordinal));
            if (!_modelCalls.TryGetValue((call.Model, call.Version), out var list))
            {
                list = new List<ModelEntry>();
                _modelCalls[(call.Model, call.Version)] = list;
            }

            list.Add(entry);
        }

        foreach (var (_, list) in _modelCalls)
        {
            list.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        }
    }

    /// <summary>The staging row a recorded value belongs to: context key, candidate key, or both (cross shapes).</summary>
    private readonly record struct RowKey(EntityKey Context, EntityKey Candidate)
    {
        public override string ToString() =>
            Context == default ? Candidate.ToString() : Candidate == default ? Context.ToString() : Context + "/" + Candidate;
    }

    private sealed class FeatureEntry(
        string source, string entity, KeyShape shape, EntityKey context, EntityKey[] keys, IoOutcome outcome, string? error, int failedKeys,
        string[] fieldNames, ScalarValue[][]? values)
    {
        public string Source { get; } = source;

        public string Entity { get; } = entity;

        public KeyShape Shape { get; } = shape;

        public EntityKey Context { get; } = context;

        public EntityKey[] Keys { get; } = keys;

        public IoOutcome Outcome { get; } = outcome;

        public string? Error { get; } = error;

        public int FailedKeys { get; } = failedKeys;

        public string[] FieldNames { get; } = fieldNames;

        /// <summary>Per field, per row; null for a call that did not answer.</summary>
        public ScalarValue[][]? Values { get; } = values;

        public bool Consumed { get; set; }
    }

    private sealed class CandidateEntry(int seq, string? node, string source, int limit, IoOutcome outcome, string? error, EntityKey[] ids, double[] scores)
    {
        public int Seq { get; } = seq;

        public string? Node { get; } = node;

        public string Source { get; } = source;

        public int Limit { get; } = limit;

        public IoOutcome Outcome { get; } = outcome;

        public string? Error { get; } = error;

        public EntityKey[] Ids { get; } = ids;

        public double[] Scores { get; } = scores;

        public bool Consumed { get; set; }
    }

    private sealed class ModelEntry(
        int seq, string model, int batchSize, IoOutcome outcome, string? error, bool threw, EntityKey[]? keys, string[] digests,
        Dictionary<string, float[]> outputs, Dictionary<string, string> texts)
    {
        public int Seq { get; } = seq;

        public string Model { get; } = model;

        public int BatchSize { get; } = batchSize;

        public IoOutcome Outcome { get; } = outcome;

        public string? Error { get; } = error;

        public bool Threw { get; } = threw;

        public EntityKey[]? Keys { get; } = keys;

        public string[] Digests { get; } = digests;

        public Dictionary<string, float[]> Outputs { get; } = outputs;

        public Dictionary<string, string> Texts { get; } = texts;

        public bool Consumed { get; set; }
    }

    private sealed class GapCounter
    {
        private readonly List<string> _examples = new();

        public int Count { get; private set; }

        public IReadOnlyList<string> Examples => _examples;

        public void Add(int count, string? example)
        {
            Count += count;
            if (example is not null && _examples.Count < 5 && !_examples.Contains(example))
            {
                _examples.Add(example);
            }
        }
    }
}
