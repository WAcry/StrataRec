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

/// <remarks>
/// <para>One instance per replayed run; not reusable.</para>
/// </remarks>
internal sealed class RunReplay : IFeatureCallHook
{
    private readonly object _gate = new();
    private readonly CallEpoch _epoch = new();
    private readonly Dictionary<List<ServiceEntry>, (Dictionary<(EntityKey Key, string Digest), List<(ServiceEntry Entry, int Row)>> Rows, HashSet<EntityKey> Scored)> _rowsOf =
        new(ReferenceEqualityComparer.Instance);
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

    /// <param name="pointer">Pointer of <paramref name="run"/> in its bundle, for errors.</param>
    /// <exception cref="ReplayBundleException">A value, key, type or status in the run is malformed.</exception>
    public static RunReplay Create(ReplayRunRecord run, string pointer)
    {
        var replay = new RunReplay(run);
        replay.IndexNodes(run, pointer);
        replay.IndexFeatures(run, pointer);
        replay.IndexServices(run, pointer);
        replay.IndexPipelineCalls(run, pointer);
        return replay;
    }

    private readonly Dictionary<(string Node, int Seq, string Pipeline), (Dictionary<string, ReplayTypedValue> Inputs, Operators.PipelineCallResult Result)> _pipelineCalls = new();

    private void IndexPipelineCalls(ReplayRunRecord run, string pointer)
    {
        for (var i = 0; i < run.PipelineCalls.Count; i++)
        {
            var call = run.PipelineCalls[i];
            var at = $"{pointer}/pipeline_calls/{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            // Decoded and encoded again: a bundle read from a file has its values as the file spelled them (a list over
            // several lines), and a call's inputs are compared as values, not as text.
            var inputs = new Dictionary<string, ReplayTypedValue>(call.Inputs.Count, StringComparer.Ordinal);
            foreach (var (name, input) in call.Inputs)
            {
                var type = ReplayJson.Type(input.Type, $"{at}/inputs/{name}/type");
                inputs[name] = ReplayJson.Typed(type, ReplayJson.Value(input.Value, type, $"{at}/inputs/{name}/value"));
            }

            if (!_pipelineCalls.TryAdd((call.Node, call.Seq, call.Pipeline), (inputs, ReplayJson.CallResult(call, at))))
            {
                throw new ReplayBundleException(at, $"node '{call.Node}' has two calls {call.Seq} of pipeline '{call.Pipeline}'", "each call of a node has its own 'seq'");
            }
        }
    }

    /// <summary>
    /// A call the recording does not have, or has with other inputs, is a gap and fails: the replay does not run the
    /// pipeline.
    /// </summary>
    internal Operators.PipelineCallResult PipelineCall(string node, int sequence, string pipelineId, RequestInputs? given)
    {
        lock (_gate)
        {
            if (!_pipelineCalls.TryGetValue((node, sequence, pipelineId), out var recorded))
            {
                AddGap("pipeline", pipelineId, null, "not_recorded", 1, node);
                return Operators.PipelineCallResult.Failed(pipelineId, $"pipeline '{pipelineId}': this call is not in the replay bundle");
            }

            if (!SameInputs(recorded.Inputs, ReplayJson.Inputs(given)))
            {
                AddGap("pipeline", pipelineId, null, "inputs_changed", 1, node);
                return Operators.PipelineCallResult.Failed(pipelineId, $"pipeline '{pipelineId}': the replay bundle has this call with other inputs");
            }

            return recorded.Result;
        }
    }

    private static bool SameInputs(Dictionary<string, ReplayTypedValue> recorded, Dictionary<string, ReplayTypedValue> given)
    {
        if (recorded.Count != given.Count)
        {
            return false;
        }

        foreach (var (name, value) in given)
        {
            if (!recorded.TryGetValue(name, out var was) || was.Type != value.Type || (was.Value is null) != (value.Value is null)
                || (was.Value is { } a && value.Value is { } b && a.GetRawText() != b.GetRawText()))
            {
                return false;
            }
        }

        return true;
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

    internal ServiceStatus Call(in ServiceCaller caller, IoTarget target, ServiceCall call, ServiceResponse response, out string? degraded)
    {
        var node = caller.Key;
        var fromSource = caller.Source is not null; // with the key, which a node's id might spell too
        degraded = null;
        lock (_gate)
        {
            // What an entry remembers of a call is of this one only (ServiceEntry.AskedFor): each call has an epoch of
            // its own, never one used before, and one made while it is answered (an operator's code may call again,
            // from a getter of what it hands over) ends with it, the epoch of the call it interrupted active again.
            var interrupted = _epoch.Active;
            _epoch.Active = ++_epoch.Last;
            try
            {
                var kind = target.Role;
                var order = ServiceInputDigest.NameOrder(call);
                _serviceCalls.TryGetValue((call.Service, call.Version), out var calls);
                var items = call.Items.Span;
                if (items.IsEmpty)
                {
                    var digest = ReplayJson.Hex(ServiceInputDigest.Call(call, order));
                    var own = TakeOwn(calls, entry => entry.Keys is null && entry.Is(node, fromSource) && entry.Digests[0] == digest, call);
                    if (own is not null && (own.Outcome != IoOutcome.Ok || Answers(own, call)))
                    {
                        return Unanswered(call, response, Answer(own, call, response, kind, node, out degraded), kind, node, null, own);
                    }

                    var answer = Best(calls, entry => entry.Keys is null && entry.Outcome == IoOutcome.Ok && entry.Digests[0] == digest, call);
                    if (answer is null)
                    {
                        // Inputs changed when the node has the service's call only with other inputs; its calls with these
                        // inputs used up (a recorded failure replayed already) is not a change.
                        var changed = Find(calls, entry => entry.Keys is null && entry.Is(node, fromSource) && entry.Digests[0] != digest) is not null
                            && Find(calls, entry => entry.Keys is null && entry.Is(node, fromSource) && entry.Digests[0] == digest) is null;
                        var otherLimit = !changed
                            && Find(calls, entry => entry.Keys is null && entry.Is(node, fromSource) && entry.Digests[0] == digest && entry.Limit == call.Limit) is null
                            && Find(calls, entry => entry.Keys is null && entry.Is(node, fromSource) && entry.Digests[0] == digest) is not null;
                        var otherOutputs = !changed && !otherLimit
                            && Find(calls, entry => entry.Keys is null && entry.Is(node, fromSource) && entry.Digests[0] == digest && entry.Limit == call.Limit && entry.AskedFor(call)) is null
                            && Find(calls, entry => entry.Keys is null && entry.Is(node, fromSource) && entry.Digests[0] == digest && entry.Limit == call.Limit) is not null;
                        AddGap(kind, call.Service, null, changed ? "inputs_changed" : otherLimit ? "limit_changed" : otherOutputs ? "outputs_changed" : "not_recorded", 1, node);
                        return ServiceStatus.Failed($"{kind} '{call.Service}': this call is not in the replay bundle");
                    }

                    return Unanswered(call, response, Answer(answer, call, response, kind, node, out degraded), kind, node, null, answer);
                }

                var keys = items.ToArray();
                var digests = new string[keys.Length];
                var rows = new ulong[keys.Length];
                ServiceInputDigest.Rows(call, order, rows);
                for (var r = 0; r < keys.Length; r++)
                {
                    digests[r] = ReplayJson.Hex(rows[r]);
                }

                bool Same(ServiceEntry entry) => entry.Keys is not null && entry.Keys.AsSpan().SequenceEqual(keys) && entry.Digests.AsSpan().SequenceEqual(digests);
                var ownCall = TakeOwn(calls, entry => entry.Is(node, fromSource) && Same(entry), call);
                if (ownCall is not null && (ownCall.Outcome != IoOutcome.Ok || Answers(ownCall, call)))
                {
                    return Unanswered(call, response, Answer(ownCall, call, response, kind, node, out degraded), kind, node, keys, ownCall);
                }

                // A recorded call of these items and inputs answers as recorded, with gaps for what it lacks: synthesizing it item
                // by item would lose its texts and the order of repeated items.
                var same = Best(calls, entry => entry.Outcome == IoOutcome.Ok && Same(entry), call);
                return same is not null
                    ? Unanswered(call, response, Answer(same, call, response, kind, node, out degraded), kind, node, keys, same)
                    : Synthesize(call, calls, response, keys, digests, node, fromSource, kind);
            }
            finally
            {
                _epoch.Active = interrupted;
            }
        }
    }

    private static bool Answers(ServiceEntry entry, ServiceCall call)
    {
        foreach (var name in call.Outputs.Span)
        {
            if (!Has(entry, call, name))
            {
                return false;
            }
        }

        return Shortfall(entry, call) == 0;
    }

    /// <summary>
    /// Whether a recorded call has output <paramref name="name"/> for <paramref name="call"/>, at the call's limit (an
    /// output or a text may say how many items it was asked for): recorded, or asked for and left unwritten by the
    /// service, which is its answer too.
    /// </summary>
    private static bool Has(ServiceEntry entry, ServiceCall call, string name) =>
        entry.Limit == call.Limit && (entry.Outputs.ContainsKey(name) || entry.Texts.ContainsKey(name) || LeftUnwritten(entry, call, name));

    /// <summary>
    /// Whether the service left output <paramref name="name"/> unwritten in a recorded call that is
    /// <paramref name="call"/>: the same limit and outputs, which an answer may depend on. One that asked for other
    /// outputs says nothing of it.
    /// </summary>
    private static bool LeftUnwritten(ServiceEntry entry, ServiceCall call, string name) =>
        entry.Limit == call.Limit && entry.AskedFor(call) && !entry.Outputs.ContainsKey(name) && !entry.Texts.ContainsKey(name);

    /// <summary>
    /// A call that asked for none (a limit of 0) says nothing about the items the service has.
    /// </summary>
    private static int Shortfall(ServiceEntry entry, ServiceCall call) =>
        call.Limit <= entry.Ids.Length || (entry.Limit > 0 && entry.Ids.Length < entry.Limit) ? 0 : call.Limit - entry.Ids.Length;

    /// <summary>
    /// Nothing is consumed.
    /// </summary>
    private static ServiceEntry? Best(List<ServiceEntry>? calls, Predicate<ServiceEntry> match, ServiceCall call)
    {
        if (calls is null)
        {
            return null;
        }

        ServiceEntry? best = null;
        (int Outputs, int Items, int Shortfall) bestHas = (-1, -1, int.MinValue);
        foreach (var entry in calls)
        {
            if (!match(entry))
            {
                continue;
            }

            if (Answers(entry, call))
            {
                return entry;
            }

            var outputs = 0;
            foreach (var name in call.Outputs.Span)
            {
                outputs += Has(entry, call, name) ? 1 : 0;
            }

            (int Outputs, int Items, int Shortfall) has = (outputs, Math.Min(entry.Ids.Length, call.Limit), -Shortfall(entry, call));
            if (has.CompareTo(bestHas) > 0)
            {
                (best, bestHas) = (entry, has);
            }
        }

        return best;
    }

    private ServiceStatus Unanswered(ServiceCall call, ServiceResponse response, ServiceStatus status, string kind, string node, EntityKey[]? keys, ServiceEntry from)
    {
        if (status.Succeeded)
        {
            foreach (var name in call.Outputs.Span)
            {
                if (!response.TryGetValues(name, out _) && !response.TryGetText(name, out _) && !LeftUnwritten(from, call, name))
                {
                    // Asked for at another limit with the same outputs is recorded there, written or not (one
                    // asking for other outputs says nothing of it).
                    var recorded = from.Outputs.ContainsKey(name) || from.Texts.ContainsKey(name) || from.AskedFor(call);
                    AddOutputGap(kind, call.Service, name, node, keys, null, recorded && from.Limit != call.Limit ? "limit_changed" : "not_recorded");
                }
            }
        }

        return status;
    }

    private void AddOutputGap(string kind, string service, string output, string node, EntityKey[]? keys, bool[]? only, string reason = "not_recorded")
    {
        if (keys is null)
        {
            AddGap(kind, service, output, reason, 1, node);
            return;
        }

        GapCounter? gap = null;
        for (var r = 0; r < keys.Length; r++)
        {
            if (only is null || only[r])
            {
                (gap ??= Gap(kind, service, output, reason)).Add(1, keys[r].ToString());
            }
        }
    }

    /// <summary>Time left when the node passed its <c>when</c> in the recording; infinite for a node the recording lacks.</summary>
    internal double RemainingMs(string node) => _remaining.TryGetValue(node, out var ms) ? ms : double.PositiveInfinity;

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

            return FetchStatus.Failed(entry.Error ?? FeatureFetcher.FailedWithoutWords(source));
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
    /// A call carrying only some is not this call: its other fields may well be in other successful calls (the
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
    /// Replays a recorded call as it was: its failure, or every output, text and item it returned (items up to the
    /// call's limit), with the generation details its status had (tokens, truncation), so the operator decides and
    /// reports as the request did; a recording of another limit, only the start of its list.
    /// </summary>
    private ServiceStatus Answer(ServiceEntry entry, ServiceCall call, ServiceResponse response, string kind, string node, out string? degraded)
    {
        degraded = entry.Outcome == IoOutcome.Ok ? entry.Degraded : null;
        if (entry.Outcome != IoOutcome.Ok)
        {
            if (entry.Threw)
            {
                throw new ReplayedCutOff(entry.Error ?? $"{kind} '{entry.Service}' was cut off in the recording", entry.Outcome);
            }

            return ServiceStatus.Failed(entry.Error ?? $"{kind} '{entry.Service}' failed in the recording") with
            {
                OutputTokens = entry.OutputTokens,
                Truncated = entry.Truncated,
            };
        }

        // A recorded call of another limit answers only the start of its list (or a list the service could not fill), as
        // a service's cache does: its outputs, and the items its service added past its limit, were for its own limit.
        var sameLimit = entry.Limit == call.Limit;
        if (sameLimit)
        {
            foreach (var (name, tensor) in entry.Outputs)
            {
                Write(response, name, tensor);
            }

            foreach (var (name, text) in entry.Texts)
            {
                response.SetText(name, text);
            }
        }

        var count = Math.Min(entry.Ids.Length, call.Limit);
        for (var i = 0; i < count; i++)
        {
            response.AddItem(entry.Ids[i], entry.Scores[i]);
        }

        if (sameLimit)
        {
            response.AddDropped(entry.Dropped);
        }

        if (Shortfall(entry, call) is > 0 and var shortfall)
        {
            AddGap(kind, entry.Service, null, "limit_exceeds_recording", shortfall, node);
        }

        // The generation details describe what the recorded call generated for its own limit.
        return sameLimit ? ServiceStatus.Ok with { OutputTokens = entry.OutputTokens, Truncated = entry.Truncated } : ServiceStatus.Ok;
    }

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

    private (Dictionary<(EntityKey Key, string Digest), List<(ServiceEntry Entry, int Row)>> Rows, HashSet<EntityKey> Scored) RowsOf(List<ServiceEntry>? calls)
    {
        if (calls is null)
        {
            return (new(), new());
        }

        if (_rowsOf.TryGetValue(calls, out var built))
        {
            return built;
        }

        var rows = new Dictionary<(EntityKey Key, string Digest), List<(ServiceEntry Entry, int Row)>>();
        var scored = new HashSet<EntityKey>();
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

        _rowsOf[calls] = (rows, scored);
        return (rows, scored);
    }

    private ServiceStatus Synthesize(
        ServiceCall call, List<ServiceEntry>? calls, ServiceResponse outputs, EntityKey[] keys, string[] digests, string node, bool fromSource, string kind)
    {
        var (rows, scored) = RowsOf(calls);
        var layout = new List<(string Name, TensorElementType Type, int PerRow)>();

        // The output layout (each output's type and values per row): as the recorded rows this call matches have it,
        // since another node of the same model may write the same output at another width; an output none of them has
        // (every row's inputs changed), as this node's calls have it, then as any call has it.
        var seen = new HashSet<ServiceEntry>(ReferenceEqualityComparer.Instance);
        for (var r = 0; r < keys.Length; r++)
        {
            if (rows.TryGetValue((keys[r], digests[r]), out var recorded))
            {
                foreach (var (entry, _) in recorded)
                {
                    if (seen.Add(entry))
                    {
                        AddLayout(layout, entry, call.Outputs.Span);
                    }
                }
            }
        }

        var ofRows = layout.Count;
        for (var pass = 0; calls is not null && pass < 2; pass++)
        {
            foreach (var entry in calls)
            {
                if (entry.Outcome == IoOutcome.Ok && entry.Keys is not null && (pass == 1 || entry.Is(node, fromSource))
                    && seen.Add(entry))
                {
                    AddLayout(layout, entry, call.Outputs.Span);
                }
            }
        }

        var matched = new bool[keys.Length];
        for (var r = 0; r < keys.Length; r++)
        {
            matched[r] = rows.ContainsKey((keys[r], digests[r]));
        }

        // An output only other calls have, that the service left unwritten for a row this call matches: not written, the
        // row's answer (below), rather than a gap value.
        for (var o = layout.Count - 1; o >= ofRows; o--)
        {
            for (var r = 0; r < keys.Length; r++)
            {
                if (matched[r] && Omitted(rows[(keys[r], digests[r])], call, layout[o].Name))
                {
                    layout.RemoveAt(o);
                    break;
                }
            }
        }

        ReportUnmatched(kind, call.Service, keys, matched, scored);

        // An output the call reads that no recorded row of it has: a gap of that output for every row the recording
        // has, but one whose recorded value was not of whole rows (the node failed on it when recorded, and fails on
        // its absence now), or that the service left unwritten (its answer).
        var lacking = new bool[keys.Length];
        var omitted = false;
        foreach (var name in call.Outputs.Span)
        {
            if (!Has(layout, name))
            {
                for (var r = 0; r < keys.Length; r++)
                {
                    var unwritten = matched[r] && Omitted(rows[(keys[r], digests[r])], call, name);
                    omitted |= unwritten;
                    lacking[r] = matched[r] && !unwritten && !Unsplit(rows[(keys[r], digests[r])], name);
                }

                if (Array.IndexOf(lacking, true) >= 0)
                {
                    AddOutputGap(kind, call.Service, name, node, keys, lacking);
                }
            }
        }

        // A call that reads no output answers by its status alone: with none of its items recorded with these inputs,
        // nothing vouches for it.
        if (call.Outputs.IsEmpty && Array.IndexOf(matched, true) < 0)
        {
            return ServiceStatus.Failed($"{kind} '{call.Service}': none of the call's items is in the replay bundle with these inputs");
        }

        if (layout.Count == 0 && !call.Outputs.IsEmpty && !omitted)
        {
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

            void Fill<T>(Span<T> destination, T gap)
            {
                Array.Clear(lacking);
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
                        lacking[r] = matched[r] && !Unsplit(rows[(keys[r], digests[r])], name); // an unmatched row is a gap of the row already
                    }
                }

                if (Array.IndexOf(lacking, true) >= 0)
                {
                    AddOutputGap(kind, call.Service, name, node, keys, lacking);
                }
            }
        }

        return ServiceStatus.Ok;

        static int PerRow(ServiceEntry entry, Array values) => entry.BatchSize > 0 ? values.Length / entry.BatchSize : 1;

        // Only an output of whole rows splits by row.
        static bool ByRow(ServiceEntry entry, Array values) => entry.BatchSize > 0 && values.Length % entry.BatchSize == 0;

        static void AddLayout(List<(string Name, TensorElementType Type, int PerRow)> layout, ServiceEntry entry, ReadOnlySpan<string> read)
        {
            foreach (var (name, values) in entry.Outputs)
            {
                if (read.IndexOf(name) >= 0 && !Has(layout, name) && ByRow(entry, values.Values))
                {
                    layout.Add((name, values.Type, PerRow(entry, values.Values)));
                }
            }
        }

        static bool Omitted(List<(ServiceEntry Entry, int Row)> recorded, ServiceCall call, string name)
        {
            foreach (var (entry, _) in recorded)
            {
                if (LeftUnwritten(entry, call, name))
                {
                    return true;
                }
            }

            return false;
        }

        static bool Unsplit(List<(ServiceEntry Entry, int Row)> recorded, string name)
        {
            foreach (var (entry, _) in recorded)
            {
                if (entry.Outputs.TryGetValue(name, out var tensor) && !ByRow(entry, tensor.Values))
                {
                    return true;
                }
            }

            return false;
        }

        static bool Has(List<(string Name, TensorElementType Type, int PerRow)> layout, string name)
        {
            foreach (var output in layout)
            {
                if (output.Name == name)
                {
                    return true;
                }
            }

            return false;
        }

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

    private void ReportUnmatched(string kind, string service, EntityKey[] keys, bool[] matched, HashSet<EntityKey> scored)
    {
        GapCounter? missing = null;
        GapCounter? changed = null;
        for (var r = 0; r < keys.Length; r++)
        {
            if (matched[r])
            {
                continue;
            }

            if (scored.Contains(keys[r]))
            {
                (changed ??= Gap(kind, service, null, "inputs_changed")).Add(1, keys[r].ToString());
            }
            else
            {
                (missing ??= Gap(kind, service, null, "not_recorded")).Add(1, keys[r].ToString());
            }
        }
    }

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

    private static ServiceEntry? TakeOwn(List<ServiceEntry>? calls, Predicate<ServiceEntry> match, ServiceCall call) =>
        Take(calls, entry => entry.Limit == call.Limit && entry.AskedFor(call) && match(entry))
        ?? Take(calls, entry => entry.Outcome == IoOutcome.Ok && match(entry) && Answers(entry, call));

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
    /// Writes a recorded value into a staging field; false (nothing written) when it does not fit. The value was
    /// decoded with its recorded type; a field whose type changed takes it only when it converts without loss of
    /// meaning: numeric widening, rounding to float32 as the source itself would. Exact and synthesized calls both
    /// write through here, so they apply one rule.
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

    internal void CountServed(string source, ReadOnlySpan<CompiledFieldBinding> fields, ReadOnlySpan<ScalarValue> values, bool failed)
    {
        lock (_gate)
        {
            for (var f = 0; f < fields.Length; f++)
            {
                AddStats(source, fields[f].Field, 1, failed || values[f].IsMissing ? 1 : 0);
            }
        }
    }

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

    private static void RequireAsked(ReplayServiceCall call, string name, string at)
    {
        if (!call.Requested.Contains(name))
        {
            throw new ReplayBundleException(at, $"output '{name}' was recorded, but the call did not ask for it (requested)",
                "a call records only the outputs it asked for, and lists them in requested");
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

            if (call.Dropped is { } dropped && (dropped <= 0 || outcome != IoOutcome.Ok || ids.Length != call.Limit))
            {
                throw new ReplayBundleException(at + "/dropped", $"{dropped} item(s) dropped by a call ({call.Status}) that returned {ids.Length} of its limit of {call.Limit}",
                    "a call records the items its service added past its limit only when it answered with a full list, and only when there were some");
            }

            var outputs = new Dictionary<string, ReplayTensor>(StringComparer.Ordinal);
            foreach (var (name, values) in call.Outputs)
            {
                outputs[name] = ReplayJson.Tensor(values, $"{at}/outputs/{JsonPointer.Escape(name)}");
            }

            if (call.Node is not null && call.Source is not null)
            {
                throw new ReplayBundleException(at + "/source", "a call made by both a node and a feature source",
                    "a node's call names its node, a feature source's its source");
            }

            if ((call.Source is null) != (call.Entity is null))
            {
                throw new ReplayBundleException(at + "/entity", call.Source is null ? "an entity without a feature source" : "a feature source's call without its entity",
                    "a feature source's call names its source and the entity it computed; a node's names neither");
            }

            for (var r = 0; r < call.Requested.Count; r++)
            {
                if (call.Requested.IndexOf(call.Requested[r]) != r)
                {
                    throw new ReplayBundleException($"{at}/requested/{r}", $"output '{call.Requested[r]}' asked for twice", "name each output the call asked for once");
                }
            }

            foreach (var name in call.Outputs.Keys)
            {
                RequireAsked(call, name, $"{at}/outputs/{JsonPointer.Escape(name)}");
            }

            foreach (var name in call.Texts.Keys)
            {
                RequireAsked(call, name, $"{at}/texts/{JsonPointer.Escape(name)}");
            }

            var entry = new ServiceEntry(call.Seq, ServiceCaller.KeyOf(call.Node, call.Source, call.Entity), call.Source is not null, call.Service, call.BatchSize, call.Limit, outcome, call.Error, call.Threw, keys,
                call.InputDigests.ToArray(), outputs, new Dictionary<string, string>(call.Texts, StringComparer.Ordinal), ids, call.Scores.ToArray(),
                call.OutputTokens, call.Truncated == true, call.Degraded, call.Dropped ?? 0, [.. call.Requested], _epoch);
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

        public override string ToString() => string.Join('/', _parts);
    }

    private sealed class FeatureEntry(
        string source, string entity, int partCount, EntityKey[] keys, IoOutcome outcome, string? error, int failedKeys,
        string[] fieldNames, ScalarValue[][]? values)
    {
        public string Source { get; } = source;

        public string Entity { get; } = entity;

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

    private sealed class CallEpoch
    {
        public int Active;

        public int Last;
    }

    private sealed class ServiceEntry(
        int seq, string? node, bool fromSource, string service, int batchSize, int limit, IoOutcome outcome, string? error, bool threw, EntityKey[]? keys, string[] digests,
        Dictionary<string, ReplayTensor> outputs, Dictionary<string, string> texts, EntityKey[] ids, double[] scores, int? outputTokens, bool truncated,
        string? degraded, int dropped, string[] requested, CallEpoch epoch)
    {
        public int Seq { get; } = seq;

        /// <summary>The outputs the call asked for: one not in <see cref="Outputs"/> or <see cref="Texts"/> the service left unwritten.</summary>
        public string[] Requested { get; } = requested;

        private int _askedAt = -1;
        private bool _asked;

        /// <summary>
        /// Kept for the replay's call being answered, under its lock. Not kept past it: a call's contents are its
        /// caller's, which may reuse the object and its arrays for its next call.
        /// </summary>
        public bool AskedFor(ServiceCall call)
        {
            if (_askedAt != epoch.Active)
            {
                _asked = Same(call.Outputs.Span, Requested);
                _askedAt = epoch.Active;
            }

            return _asked;

            static bool Same(ReadOnlySpan<string> outputs, string[] requested)
            {
                if (outputs.Length != requested.Length)
                {
                    return false;
                }

                foreach (var name in outputs)
                {
                    if (Array.IndexOf(requested, name) < 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>The caller's key (<see cref="ServiceCaller.Key"/>); with <see cref="FromSource"/>, which caller it is.</summary>
        public string? Node { get; } = node;

        public bool FromSource { get; } = fromSource;

        public bool Is(string key, bool source) => FromSource == source && string.Equals(Node, key, StringComparison.Ordinal);

        public string Service { get; } = service;

        public int BatchSize { get; } = batchSize;

        public int Limit { get; } = limit;

        public IoOutcome Outcome { get; } = outcome;

        public string? Error { get; } = error;

        public bool Threw { get; } = threw;

        public EntityKey[]? Keys { get; } = keys;

        public string[] Digests { get; } = digests;

        public Dictionary<string, ReplayTensor> Outputs { get; } = outputs;

        public Dictionary<string, string> Texts { get; } = texts;

        public EntityKey[] Ids { get; } = ids;

        public double[] Scores { get; } = scores;

        public int? OutputTokens { get; } = outputTokens;

        public bool Truncated { get; } = truncated;

        public string? Degraded { get; } = degraded;

        public int Dropped { get; } = dropped;

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
