using System.Collections.Immutable;
using System.Text.Json;
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
using StrataRec.Core.Services;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Replays one recorded pipeline run (design 13.4): answers feature and service calls from the recording and
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
internal sealed class RunReplay : IFeatureCallHook
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, List<FeatureEntry>> _featureCalls = new();
    private readonly Dictionary<(string Source, string Entity), Dictionary<RowKey, Dictionary<string, ScalarValue>>> _featureValues = new();
    private readonly Dictionary<(string Service, string? Version), List<ServiceEntry>> _serviceCalls = new();
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
        replay.IndexServices(run, pointer);
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
                foreach (var ((kind, source, fieldName, reason), counter) in _gaps)
                {
                    gaps.Add(new ReplayGap(kind, source, fieldName, reason, counter.Count, counter.Examples));
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
                foreach (var ((source, fieldName), counts) in _stats)
                {
                    stats.Add(new ReplayFieldStats(source, fieldName, counts[0], counts[1]));
                }

                stats.Sort(static (a, b) => string.CompareOrdinal(a.Source, b.Source) is var c && c != 0 ? c : string.CompareOrdinal(a.Field, b.Field));
                return stats;
            }
        }
    }

    public int Begin(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields) => 0;

    public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
    {
    }

    public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields, IFeatureBatchWriter writer)
    {
        lock (_gate)
        {
            var source = group.SourceName;
            var parts = batch.PartCount;
            var keys = Flatten(batch);
            var rows = batch.Count;
            var entry = TakeFeatureCall(source, group.Entity.Name, parts, keys, fields);
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
                    var rowKey = RowKeyOf(parts, keys, r);
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
    /// Answers a service call from the recording. A call about no items is matched by the digest of its whole input: this
    /// node's next recorded call with that digest is replayed as recorded, its failure included, and consumed; otherwise
    /// any successful recorded call with that digest answers it, and nothing is consumed (a node the configuration added
    /// asks what another node asked, whatever order the nodes ask in). A call about items takes the next recorded call with
    /// the same items and the same inputs for each, and replays it as recorded; otherwise each item's outputs come from
    /// whichever successful call had that item with the same inputs (<see cref="Synthesize"/>). A call the recording
    /// cannot answer fails, and is a gap: <c>inputs_changed</c> when the recording has the node's call of the service with
    /// other inputs, else <c>not_recorded</c>. A recorded call the budget or the caller cut off throws
    /// <see cref="OperationCanceledException"/> again, as the operator saw it. An output the call asks for and the answer
    /// lacks is a <c>not_recorded</c> gap of that output: only the outputs a call asked for are recorded, so a
    /// configuration that reads another output of the same call (an experiment maps one more) finds none in the bundle.
    /// </summary>
    /// <remarks>
    /// A recorded failure answers only the call it was: the same node, entities and inputs. Taken by entities alone, the
    /// failure of another call of the same model and entity (another node, another prompt) answered this one, and hid the
    /// success the recording has for its own inputs (review 30). A call whose inputs changed since a recorded failure is
    /// synthesized like any other, so its items are reported as gaps rather than failed for a reason about other inputs.
    /// </remarks>
    /// <param name="node">The calling node: its own recorded calls answer it first, and a synthesized call whose items all
    /// changed takes its outputs' widths from them before another node's.</param>
    internal ServiceStatus Call(string node, IoTarget target, ServiceCall call, ServiceResponse response)
    {
        lock (_gate)
        {
            var kind = target.Role;
            var order = ServiceInputDigest.NameOrder(call);
            _serviceCalls.TryGetValue((call.Service, call.Version), out var calls);
            var items = call.Items.Span;
            if (items.IsEmpty)
            {
                var digest = ReplayJson.Hex(ServiceInputDigest.Call(call, order));
                var answer = Take(calls, entry => entry.Keys is null && entry.Node == node && entry.Digests[0] == digest)
                    ?? Find(calls, entry => entry.Keys is null && entry.Outcome == IoOutcome.Ok && entry.Digests[0] == digest);
                if (answer is null)
                {
                    var changed = Find(calls, entry => entry.Keys is null && entry.Node == node) is not null;
                    AddGap(kind, call.Service, null, changed ? "inputs_changed" : "not_recorded", 1, node);
                    return ServiceStatus.Failed($"{kind} '{call.Service}': this call is not in the replay bundle");
                }

                return Unanswered(call, response, Answer(answer, call, response, kind, node), kind, node, 1);
            }

            var keys = items.ToArray();
            var digests = new string[keys.Length];
            for (var r = 0; r < keys.Length; r++)
            {
                digests[r] = ReplayJson.Hex(ServiceInputDigest.Row(call, order, r));
            }

            var positional = Take(calls, entry => entry.Keys is not null && entry.Keys.AsSpan().SequenceEqual(keys) && entry.Digests.AsSpan().SequenceEqual(digests));
            var status = positional is not null
                ? Answer(positional, call, response, kind, node)
                : Synthesize(call, calls, response, keys, digests, node, kind);
            return Unanswered(call, response, status, kind, node, keys.Length);
        }
    }

    /// <summary>Reports each output the call asks for that its successful answer lacks (see <see cref="Call"/>).</summary>
    private ServiceStatus Unanswered(ServiceCall call, ServiceResponse response, ServiceStatus status, string kind, string node, int count)
    {
        if (status.Succeeded)
        {
            foreach (var name in call.Outputs.Span)
            {
                if (!response.TryGetValues(name, out _) && !response.TryGetText(name, out _))
                {
                    AddGap(kind, call.Service, name, "not_recorded", count, node);
                }
            }
        }

        return status;
    }

    /// <summary>Time left when the node passed its <c>when</c> in the recording; infinite for a node the recording lacks.</summary>
    internal double RemainingMs(string node) => _remaining.TryGetValue(node, out var ms) ? ms : double.PositiveInfinity;

    /// <summary>The next recorded attempt of <paramref name="node"/>, or null when the recording has none left.</summary>
    internal ReplayAttemptRecord? NextAttempt(string node) =>
        _attempts.TryGetValue(node, out var attempts) && attempts.Count > 0 ? attempts.Dequeue() : null;

    private FetchStatus ReplayExact(FeatureEntry entry, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields, IFeatureBatchWriter writer)
    {
        var source = entry.Source;
        var rows = batch.Count;
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
                    gap.Add(1, RowKeyOf(entry.PartCount, entry.Keys, r).ToString());
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
                        AddGap("feature", source, name, "type_changed", 1, RowKeyOf(entry.PartCount, entry.Keys, r).ToString());
                    }
                }
            }

            AddStats(source, name, rows, missing);
        }

        return entry.Outcome == IoOutcome.Partial ? new FetchStatus(FetchOutcome.Partial, entry.FailedKeys, entry.Error) : FetchStatus.Ok;
    }

    /// <summary>
    /// The first unconsumed recorded call with the same source, entity and keys (tuple by tuple) that carries every requested
    /// field. A call carrying only some is not this call: its other fields may well be in other successful calls (the
    /// configuration regrouped the fetch), so the call is synthesized value by value instead, and nothing is consumed.
    /// </summary>
    private FeatureEntry? TakeFeatureCall(string source, string entity, int partCount, ReadOnlySpan<EntityKey> keys, ImmutableArray<CompiledFieldBinding> fields)
    {
        if (!_featureCalls.TryGetValue(Signature(source, entity, partCount, keys), out var candidates))
        {
            return null;
        }

        foreach (var entry in candidates)
        {
            if (entry.Consumed || entry.Source != source || entry.Entity != entity || entry.PartCount != partCount
                || !entry.Keys.AsSpan().SequenceEqual(keys) || !HasFields(entry, fields))
            {
                continue;
            }

            entry.Consumed = true;
            return entry;
        }

        return null;

        static bool HasFields(FeatureEntry entry, ImmutableArray<CompiledFieldBinding> fields)
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

    /// <summary>
    /// Replays a recorded call as it was: its failure, or every output, text and item it returned (items up to the call's
    /// limit), with the generation details its status had (tokens, truncation), so the operator decides and reports as the
    /// request did. Asking for more items than the recording holds is a shortfall only when the recorded call stopped at
    /// its own limit (the service might have had more); one that returned fewer had none.
    /// </summary>
    private ServiceStatus Answer(ServiceEntry entry, ServiceCall call, ServiceResponse response, string kind, string node)
    {
        if (entry.Outcome != IoOutcome.Ok)
        {
            if (entry.Threw)
            {
                throw new OperationCanceledException(entry.Error ?? $"{kind} '{entry.Service}' was cut off in the recording");
            }

            return ServiceStatus.Failed(entry.Error ?? $"{kind} '{entry.Service}' failed in the recording") with
            {
                OutputTokens = entry.OutputTokens,
                Truncated = entry.Truncated,
            };
        }

        foreach (var (name, tensor) in entry.Outputs)
        {
            Write(response, name, tensor);
        }

        foreach (var (name, text) in entry.Texts)
        {
            response.SetText(name, text);
        }

        var count = Math.Min(entry.Ids.Length, call.Limit);
        for (var i = 0; i < count; i++)
        {
            response.AddItem(entry.Ids[i], entry.Scores[i]);
        }

        if (call.Limit > entry.Ids.Length && entry.Limit > 0 && entry.Ids.Length >= entry.Limit)
        {
            AddGap(kind, entry.Service, null, "limit_exceeds_recording", call.Limit - entry.Ids.Length, node);
        }

        return ServiceStatus.Ok with { OutputTokens = entry.OutputTokens, Truncated = entry.Truncated };
    }

    /// <summary>Writes a recorded tensor output into the operator's writer, as the client wrote it.</summary>
    private static void Write(ServiceResponse outputs, string name, in ReplayTensor tensor)
    {
        switch (tensor.Values)
        {
            case bool[] b:
                b.CopyTo(outputs.GetValues<bool>(name, b.Length).Span);
                break;
            case int[] i:
                i.CopyTo(outputs.GetValues<int>(name, i.Length).Span);
                break;
            case long[] l:
                l.CopyTo(outputs.GetValues<long>(name, l.Length).Span);
                break;
            case float[] f:
                f.CopyTo(outputs.GetValues<float>(name, f.Length).Span);
                break;
            case double[] d:
                d.CopyTo(outputs.GetValues<double>(name, d.Length).Span);
                break;
            case string?[] t:
                t.CopyTo(outputs.GetValues<string?>(name, t.Length).Span);
                break;
        }
    }

    /// <summary>
    /// A keyed call the recording does not have as such (other batches, or changed inputs): each row's output comes from
    /// a successful recorded call that scored the same entity with the same inputs, whichever call and row that was. An
    /// entity scored only with other inputs is an <c>inputs_changed</c> gap; one never scored is <c>not_recorded</c>.
    /// </summary>
    /// <remarks>
    /// Tensor outputs are split by row; a row the recording cannot answer gets NaN in a float output (which the column
    /// writer replaces with its default, as any non-finite value) and the type's zero (false, 0, an empty string) in
    /// another, and is reported as a gap either way. Text outputs are not split: a text belongs to its whole call, so a multi-row call's text is
    /// replayed only with that call. A single-row call is the exception: when a successful recorded call answered that one
    /// entity alone with the same inputs, it is answered as that call was, texts and generation details included (a
    /// per-row generative call, such as <c>LlmAnnotator@1</c>'s, whose recorded call another call of the same entity took).
    /// </remarks>
    private ServiceStatus Synthesize(
        ServiceCall call, List<ServiceEntry>? calls, ServiceResponse outputs, EntityKey[] keys, string[] digests, string node, string kind)
    {
        // The rows by (entity, input digest), from the successful keyed calls of the same model and version. An entity can
        // be scored several times with different inputs.
        var layout = new List<(string Name, TensorElementType Type, int PerRow)>();
        var rows = new Dictionary<(EntityKey Key, string Digest), List<(ServiceEntry Entry, int Row)>>();
        var scored = new HashSet<EntityKey>();
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
                    if (!rows.TryGetValue((entry.Keys[r], entry.Digests[r]), out var list))
                    {
                        list = new List<(ServiceEntry Entry, int Row)>(1);
                        rows[(entry.Keys[r], entry.Digests[r])] = list;
                    }

                    list.Add((entry, r));
                    scored.Add(entry.Keys[r]);
                }
            }
        }

        // The output layout (each output's type and values per row): as the recorded rows this call matches have it, since
        // another node of the same model may write the same output at another width (a list<int32,2> column and a
        // list<int32,3> one); an output none of them has (every row's inputs changed), as this node's calls have it, then
        // as any call has it. Each recorded call is looked at once.
        var seen = new HashSet<ServiceEntry>(ReferenceEqualityComparer.Instance);
        for (var r = 0; r < keys.Length; r++)
        {
            if (rows.TryGetValue((keys[r], digests[r]), out var matched))
            {
                foreach (var (entry, _) in matched)
                {
                    if (seen.Add(entry))
                    {
                        AddLayout(layout, entry, call.Outputs.Span);
                    }
                }
            }
        }

        for (var pass = 0; calls is not null && pass < 2; pass++)
        {
            foreach (var entry in calls)
            {
                if (entry.Outcome == IoOutcome.Ok && entry.Keys is not null && (pass == 1 || string.Equals(entry.Node, node, StringComparison.Ordinal))
                    && seen.Add(entry))
                {
                    AddLayout(layout, entry, call.Outputs.Span);
                }
            }
        }

        if (keys.Length == 1 && Alone(rows, keys[0], digests[0]) is { } alone)
        {
            return Answer(alone, call, outputs, kind, node);
        }

        var lacking = new bool[keys.Length];
        if (layout.Count == 0)
        {
            Array.Fill(lacking, true);
            ReportLacking(kind, call.Service, keys, digests, lacking, rows, scored);
            return ServiceStatus.Failed($"{kind} '{call.Service}' has no recorded output in the replay bundle");
        }

        foreach (var (name, type, perRow) in layout)
        {
            switch (type)
            {
                case TensorElementType.Bool:
                    Fill(outputs.GetValues<bool>(name, keys.Length * perRow).Span, false);
                    break;
                case TensorElementType.Int32:
                    Fill(outputs.GetValues<int>(name, keys.Length * perRow).Span, 0);
                    break;
                case TensorElementType.Int64:
                    Fill(outputs.GetValues<long>(name, keys.Length * perRow).Span, 0L);
                    break;
                case TensorElementType.Float32:
                    Fill(outputs.GetValues<float>(name, keys.Length * perRow).Span, float.NaN);
                    break;
                case TensorElementType.Float64:
                    Fill(outputs.GetValues<double>(name, keys.Length * perRow).Span, double.NaN);
                    break;
                default:
                    Fill(outputs.GetValues<string>(name, keys.Length * perRow).Span, string.Empty);
                    break;
            }

            // Each row from its recorded row, or the gap value.
            void Fill<T>(Span<T> destination, T gap)
            {
                for (var r = 0; r < keys.Length; r++)
                {
                    var slice = destination.Slice(r * perRow, perRow);
                    if (Recorded(rows, keys[r], digests[r], name, type, perRow) is { } found)
                    {
                        ((T[])found.Values).AsSpan(found.Start, perRow).CopyTo(slice);
                    }
                    else
                    {
                        slice.Fill(gap);
                        lacking[r] = true;
                    }
                }
            }
        }

        ReportLacking(kind, call.Service, keys, digests, lacking, rows, scored);
        return ServiceStatus.Ok;

        static int PerRow(ServiceEntry entry, Array values) => entry.BatchSize > 0 ? values.Length / entry.BatchSize : 1;

        // Only an output of whole rows splits by row. One of no whole number a row is not synthesized: one the operator
        // reads was longer than its shape (the node failed on it when recorded, and fails on its absence now), and one it
        // does not read (a call-level value such as a loss) is not needed.
        static bool ByRow(ServiceEntry entry, Array values) => entry.BatchSize > 0 && values.Length % entry.BatchSize == 0;

        // Only the outputs the call reads, when it names them (a call-level value such as a loss is not needed).
        static void AddLayout(List<(string Name, TensorElementType Type, int PerRow)> layout, ServiceEntry entry, ReadOnlySpan<string> read)
        {
            foreach (var (name, values) in entry.Outputs)
            {
                if (!read.IsEmpty && read.IndexOf(name) < 0)
                {
                    continue;
                }

                var known = false;
                foreach (var output in layout)
                {
                    known |= output.Name == name;
                }

                if (!known && ByRow(entry, values.Values))
                {
                    layout.Add((name, values.Type, PerRow(entry, values.Values)));
                }
            }
        }

        // The first successful recorded call of this one entity alone, with these inputs, that answered text.
        static ServiceEntry? Alone(Dictionary<(EntityKey, string), List<(ServiceEntry Entry, int Row)>> rows, EntityKey key, string digest)
        {
            if (rows.TryGetValue((key, digest), out var list))
            {
                foreach (var (entry, _) in list)
                {
                    if (entry.Keys!.Length == 1 && entry.Texts.Count > 0)
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        // The first recorded row of the entity with these inputs that has the output in the same layout and element type.
        static (Array Values, int Start)? Recorded(
            Dictionary<(EntityKey, string), List<(ServiceEntry Entry, int Row)>> rows, EntityKey key, string digest, string name, TensorElementType type, int perRow)
        {
            if (!rows.TryGetValue((key, digest), out var list))
            {
                return null;
            }

            foreach (var (entry, row) in list)
            {
                if (entry.Outputs.TryGetValue(name, out var tensor) && tensor.Type == type && ByRow(entry, tensor.Values)
                    && PerRow(entry, tensor.Values) == perRow && tensor.Values.Length >= (long)(row + 1) * perRow) // a malformed bundle may have more keys than rows
                {
                    return (tensor.Values, row * perRow);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The gaps of the rows a synthesized call could not answer: <c>inputs_changed</c> for an entity the recording answered
    /// only with other inputs, otherwise <c>not_recorded</c>.
    /// </summary>
    private void ReportLacking(
        string kind, string service, EntityKey[] keys, string[] digests, bool[] lacking, Dictionary<(EntityKey Key, string Digest), List<(ServiceEntry Entry, int Row)>> rows,
        HashSet<EntityKey> scored)
    {
        GapCounter? missing = null;
        GapCounter? changed = null;
        for (var r = 0; r < keys.Length; r++)
        {
            if (!lacking[r])
            {
                continue;
            }

            if (!rows.ContainsKey((keys[r], digests[r])) && scored.Contains(keys[r]))
            {
                (changed ??= Gap(kind, service, null, "inputs_changed")).Add(1, keys[r].ToString());
            }
            else
            {
                (missing ??= Gap(kind, service, null, "not_recorded")).Add(1, keys[r].ToString());
            }
        }
    }

    /// <summary>The first recorded call that matches, consumed or not; nothing is consumed.</summary>
    private static ServiceEntry? Find(List<ServiceEntry>? calls, Predicate<ServiceEntry> match)
    {
        if (calls is not null)
        {
            foreach (var entry in calls)
            {
                if (match(entry))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private static ServiceEntry? Take(List<ServiceEntry>? calls, Predicate<ServiceEntry> match)
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
    /// widening, rounding to float32 as the source itself would. A value outside the new type's range (an integer beyond
    /// int32, a finite number beyond float32) does not fit: storing it would publish a wrapped value or an
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
            case StorageKind.Float32 when value.Kind is StorageKind.Float64 or StorageKind.Int64 && Float32Range.Fits(value.AsFloat64()):
                writer.GetValues<float>(field).Span[key] = (float)value.AsFloat64();
                break;
            case StorageKind.Float64 when value.Kind is StorageKind.Float64 or StorageKind.Int64:
                writer.GetValues<double>(field).Span[key] = value.AsFloat64();
                break;
            case StorageKind.String when value.Kind == StorageKind.String:
                writer.SetString(field, key, value.AsString());
                return true;
            case StorageKind.List when value.Kind == StorageKind.List:
                return TryWriteList(writer, field, key, type.Element, value);
            default:
                return false;
        }

        writer.SetValid(field, key);
        return true;
    }

    /// <summary>A recorded list into a list field, element by element under the scalar rule of <see cref="TryWrite"/>.</summary>
    private static bool TryWriteList(IFeatureBatchWriter writer, int field, int key, StorageKind element, in ScalarValue value)
    {
        var recorded = value.ListElement;
        switch (element)
        {
            case StorageKind.Bool when recorded == StorageKind.Bool:
                writer.SetBoolList(field, key, value.AsBoolList());
                return true;
            case StorageKind.Int32 when recorded == StorageKind.Int64 && Int32Range.AllFit(value.AsInt64List()):
                var longs = value.AsInt64List();
                var ints = new int[longs.Length];
                Int32Range.Narrow(longs, ints);
                writer.SetInt32List(field, key, ints);
                return true;
            case StorageKind.Int64 when recorded == StorageKind.Int64:
                writer.SetInt64List(field, key, value.AsInt64List());
                return true;
            case StorageKind.Float32 when recorded is StorageKind.Float64 or StorageKind.Int64:
                var floats = new float[value.ListLength];
                for (var i = 0; i < floats.Length; i++)
                {
                    var d = recorded == StorageKind.Int64 ? value.AsInt64List()[i] : value.AsFloat64List()[i];
                    if (!Float32Range.Fits(d))
                    {
                        return false;
                    }

                    floats[i] = (float)d;
                }

                writer.SetFloat32List(field, key, floats);
                return true;
            case StorageKind.Float64 when recorded == StorageKind.Float64:
                writer.SetFloat64List(field, key, value.AsFloat64List());
                return true;
            case StorageKind.Float64 when recorded == StorageKind.Int64:
                var widened = new double[value.ListLength];
                for (var i = 0; i < widened.Length; i++)
                {
                    widened[i] = value.AsInt64List()[i];
                }

                writer.SetFloat64List(field, key, widened);
                return true;
            case StorageKind.String when recorded == StorageKind.String:
                writer.SetStringList(field, key, value.AsStringList());
                return true;
            default:
                return false;
        }
    }

    private static RowKey RowKeyOf(int partCount, EntityKey[] keys, int row) => new(keys.AsSpan(row * partCount, partCount));

    /// <summary>A batch's keys flattened, key r's parts at <c>r * PartCount</c>: as a recording holds them.</summary>
    private static EntityKey[] Flatten(in EntityKeyBatch batch)
    {
        var keys = new EntityKey[batch.Count * batch.PartCount];
        for (var r = 0; r < batch.Count; r++)
        {
            for (var p = 0; p < batch.PartCount; p++)
            {
                keys[(r * batch.PartCount) + p] = batch.Key(r, p);
            }
        }

        return keys;
    }

    private static ulong Signature(string source, string entity, int partCount, ReadOnlySpan<EntityKey> keys)
    {
        var builder = default(StableHash.Builder);
        try
        {
            builder.Add(source);
            builder.Add(entity);
            builder.AddIndex(partCount);
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
            var keys = ReplayJson.KeyTuples(call.Keys, at + "/keys", out var parts);
            var rows = keys.Length / parts;
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

            var entry = new FeatureEntry(call.Source, call.Entity, parts, keys, outcome, call.Error, call.FailedKeys, names, values);
            var signature = Signature(call.Source, call.Entity, parts, keys);
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
                var rowKey = RowKeyOf(parts, keys, r);
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

    private void IndexServices(ReplayRunRecord run, string pointer)
    {
        for (var c = 0; c < run.ServiceCalls.Count; c++)
        {
            var call = run.ServiceCalls[c];
            var at = $"{pointer}/service_calls/{c}";
            var outcome = ReplayJson.Outcome(call.Status, at + "/status");
            EntityKey[]? keys = call.Keys is null ? null : ReplayJson.Keys(call.Keys, at + "/keys");
            var expectedDigests = keys?.Length ?? 1;
            if (call.InputDigests.Count != expectedDigests)
            {
                throw new ReplayBundleException(at + "/input_digests", $"expected {expectedDigests} digest(s), got {call.InputDigests.Count}",
                    "record one digest per key, or one for a call without keys");
            }

            var ids = call.Ids is null ? [] : ReplayJson.Keys(call.Ids, at + "/ids");
            if (ids.Length != call.Scores.Count)
            {
                throw new ReplayBundleException(at + "/scores", $"{call.Scores.Count} score(s) for {ids.Length} id(s)", "record one score per id");
            }

            var outputs = new Dictionary<string, ReplayTensor>(StringComparer.Ordinal);
            foreach (var (name, values) in call.Outputs)
            {
                outputs[name] = ReplayJson.Tensor(values, $"{at}/outputs/{JsonPointer.Escape(name)}");
            }

            var entry = new ServiceEntry(call.Seq, call.Node, call.Service, call.BatchSize, call.Limit, outcome, call.Error, call.Threw, keys,
                call.InputDigests.ToArray(), outputs, new Dictionary<string, string>(call.Texts, StringComparer.Ordinal), ids, call.Scores.ToArray(),
                call.OutputTokens, call.Truncated == true);
            if (!_serviceCalls.TryGetValue((call.Service, call.Version), out var list))
            {
                list = new List<ServiceEntry>();
                _serviceCalls[(call.Service, call.Version)] = list;
            }

            list.Add(entry);
        }

        foreach (var (_, list) in _serviceCalls)
        {
            list.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        }
    }

    /// <summary>The staging row a recorded value belongs to: its key's parts, compared part by part.</summary>
    private readonly struct RowKey : IEquatable<RowKey>
    {
        private readonly EntityKey[] _parts;

        public RowKey(ReadOnlySpan<EntityKey> parts)
        {
            _parts = parts.ToArray();
        }

        public bool Equals(RowKey other) => _parts.AsSpan().SequenceEqual(other._parts);

        public override bool Equals(object? obj) => obj is RowKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            foreach (var part in _parts)
            {
                hash.Add(part);
            }

            return hash.ToHashCode();
        }

        /// <summary>A one-part key's value, or the parts joined by <c>/</c>.</summary>
        public override string ToString() => string.Join('/', _parts);
    }

    private sealed class FeatureEntry(
        string source, string entity, int partCount, EntityKey[] keys, IoOutcome outcome, string? error, int failedKeys,
        string[] fieldNames, ScalarValue[][]? values)
    {
        public string Source { get; } = source;

        public string Entity { get; } = entity;

        /// <summary>Parts of every key.</summary>
        public int PartCount { get; } = partCount;

        /// <summary>The keys flattened: key r's parts at <c>r * PartCount</c>.</summary>
        public EntityKey[] Keys { get; } = keys;

        public IoOutcome Outcome { get; } = outcome;

        public string? Error { get; } = error;

        public int FailedKeys { get; } = failedKeys;

        public string[] FieldNames { get; } = fieldNames;

        /// <summary>Per field, per row; null for a call that did not answer.</summary>
        public ScalarValue[][]? Values { get; } = values;

        public bool Consumed { get; set; }
    }

    private sealed class ServiceEntry(
        int seq, string? node, string service, int batchSize, int limit, IoOutcome outcome, string? error, bool threw, EntityKey[]? keys, string[] digests,
        Dictionary<string, ReplayTensor> outputs, Dictionary<string, string> texts, EntityKey[] ids, double[] scores, int? outputTokens, bool truncated)
    {
        public int Seq { get; } = seq;

        public string? Node { get; } = node;

        public string Service { get; } = service;

        public int BatchSize { get; } = batchSize;

        /// <summary>The most items the call could add.</summary>
        public int Limit { get; } = limit;

        public IoOutcome Outcome { get; } = outcome;

        public string? Error { get; } = error;

        public bool Threw { get; } = threw;

        public EntityKey[]? Keys { get; } = keys;

        public string[] Digests { get; } = digests;

        public Dictionary<string, ReplayTensor> Outputs { get; } = outputs;

        public Dictionary<string, string> Texts { get; } = texts;

        /// <summary>The items the call added, in rank order, and their scores.</summary>
        public EntityKey[] Ids { get; } = ids;

        public double[] Scores { get; } = scores;

        public int? OutputTokens { get; } = outputTokens;

        public bool Truncated { get; } = truncated;

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
