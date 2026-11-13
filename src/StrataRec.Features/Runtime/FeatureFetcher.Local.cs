using System.Diagnostics;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The request read path of local groups (design 6.6 "读取路径"): everything a request does with a local table is here.
/// </summary>
/// <remarks>
/// <para><b>When.</b> A local group is read synchronously by <see cref="EnsureAsync"/> (or the context prefetch) when a
/// reader needs its features, for the rows live then: never prefetched, never held by a fetch barrier, never staged.
/// Within a wave, fetches are started first and local reads run while they are in flight.</para>
/// <para><b>How.</b> The table is pinned once per request (<see cref="Pin"/>): every read of the request uses that
/// version. For the features a node needs: one <see cref="TableVersion.Lookup(ReadOnlySpan{long}, Span{LocalRecordRef})"/>
/// for the rows' keys and one multi-field <see cref="TableVersion.Fill(ReadOnlySpan{LocalRecordRef}, ReadOnlySpan{int}, ReadOnlySpan{LocalFieldRead}, Span{int})"/>
/// into the slots, then one bitmap of the rows OR-ed into each slot's fill bits. Buffers are rented from the request
/// memory, so the read allocates nothing (a <c>text</c> value becomes a string).</para>
/// <para><b>Semantics</b> are the fetched features' (design 6.7): a key the table does not hold, a record without the
/// field, a missing foreign or context key, and a non-finite value all give <see cref="Catalog.FeatureDefinition.Absent"/>;
/// a failed key fails the group's features and a degraded one degrades them. A table that is not loaded fails the
/// features, never serves them as missing.</para>
/// <para><b>Replay</b> (design 13.4). A recording request records each read as a feature call through the
/// <see cref="IFeatureCallHook"/>, with the keys and the values it read; a replaying request answers local groups like
/// fetched ones, from the recording through the staging path, so a replay never reads the table. Both sides take the
/// keys of the same rows, in live-row order and deduplicated, so a replay on the same configuration matches the recorded
/// call exactly.</para>
/// </remarks>
public sealed partial class FeatureFetcher
{
    private TableVersion?[]? _pins; // by group index, rented on the first local read

    /// <summary>A local group read from its table; under replay, local groups are answered by the fetch path instead.</summary>
    private bool ReadsLocally(FetchGroup group) => group.Local is not null && _hook is not { Replays: true };

    /// <summary>The group's table version for this request, pinned on its first read.</summary>
    private TableVersion Pin(FetchGroup group)
    {
        var pins = _pins ??= _memory.RentCleared<TableVersion?>(Math.Max(1, _plan.Groups.Count));
        return pins[group.Index] ??= group.Local!.Table.Pin();
    }

    /// <summary>Reads every local group of <paramref name="groups"/> for the members of <paramref name="member"/> still unfilled.</summary>
    private void ReadLocalGroups(IndexList groups, bool[] member)
    {
        for (var g = 0; g < groups.Count; g++)
        {
            var group = _plan.Groups[groups.Items[g]];
            if (!ReadsLocally(group))
            {
                continue;
            }

            var selected = _memory.Rent<int>(group.FeatureIndices.Length);
            var count = SelectNeeded(group, member, skipInFlight: false, selected);
            if (count > 0 && !FailIfKeyFailed(group, selected.AsSpan(0, count)))
            {
                ReadLocal(group, selected.AsSpan(0, count));
            }
        }
    }

    /// <summary>One read of a candidate (or foreign-key) group: the live rows that miss any of <paramref name="features"/>.</summary>
    private void ReadLocal(FetchGroup group, ReadOnlySpan<int> features)
    {
        var local = group.Local!;
        if (!local.Table.IsLoaded)
        {
            FailNotLoaded(local, features);
            return;
        }

        var started = _hook is null ? 0 : Stopwatch.GetTimestamp();
        var version = Pin(group);

        // The rows to fill and their keys. Rows with a key are written from the front, in live order; rows without one
        // (a missing foreign key, or a key of the other type) from the back, and then moved behind the keyed ones: they
        // get no record, so every field takes its absent value. A row whose key is not computed yet is left for a later
        // read, as with a fetch.
        var live = _table.Rows.Order;
        var capacity = Math.Max(1, live.Length);
        var rows = _memory.Rent<int>(capacity);
        var records = _memory.Rent<LocalRecordRef>(capacity);
        var stringKeys = local.Table.Schema.KeyType == EntityKeyType.String;
        var int64Keys = stringKeys ? null : _memory.Rent<long>(capacity);
        var textKeys = stringKeys ? _memory.Rent<string?>(capacity) : null;
        var keyed = 0;
        var unkeyed = 0;
        foreach (var row in live)
        {
            if (!NeedsFill(features, row))
            {
                continue;
            }

            var lookup = TryCandidateKey(group, row, out var key);
            if (lookup == KeyLookup.NotReady)
            {
                continue;
            }

            if (lookup == KeyLookup.Found && key.IsString == stringKeys)
            {
                rows[keyed] = row;
                if (stringKeys)
                {
                    textKeys![keyed] = key.StringValue;
                }
                else
                {
                    int64Keys![keyed] = key.Int64Value;
                }

                keyed++;
            }
            else
            {
                rows[capacity - 1 - unkeyed++] = row;
            }
        }

        var count = keyed + unkeyed;
        if (count > 0)
        {
            rows.AsSpan(capacity - unkeyed, unkeyed).CopyTo(rows.AsSpan(keyed));
            var found = stringKeys
                ? version.Lookup(textKeys.AsSpan(0, keyed), records.AsSpan(0, keyed))
                : version.Lookup(int64Keys.AsSpan(0, keyed), records.AsSpan(0, keyed));
            records.AsSpan(keyed, unkeyed).Clear();
            Fill(group, features, version, rows.AsSpan(0, count), records.AsSpan(0, count), out var rowBits);
            if (keyed > 0)
            {
                CountKeys(local.Table, found, keyed - found);
            }

            if (_hook is { Replays: false } recorder && keyed > 0)
            {
                RecordRead(recorder, group, features, version, rowBits, started);
            }
        }

        SetReadState(group, features);
    }

    /// <summary>
    /// Fills <paramref name="features"/> for <paramref name="rows"/> from their <paramref name="records"/> and marks them
    /// filled. Features unfilled on every row (the usual case: a node's features are read together) take one multi-field
    /// fill; a feature already filled on some of the rows (an earlier read of it, before rows were appended) is filled on
    /// the others only, so a value a node has already read never changes within the request.
    /// </summary>
    /// <param name="rowBits">The rows as a bitmap of the slots' fill-bitmap length.</param>
    private void Fill(
        FetchGroup group, ReadOnlySpan<int> features, TableVersion version, ReadOnlySpan<int> rows, ReadOnlySpan<LocalRecordRef> records,
        out ulong[] rowBits)
    {
        var local = group.Local!;
        var words = _table.GetFilled(_plan.Features[features[0]].Slot).Length;
        rowBits = _memory.RentCleared<ulong>(words);
        foreach (var row in rows)
        {
            Bits.Set(rowBits, row);
        }

        var bits = rowBits.AsSpan(0, words);
        var reads = _memory.Rent<LocalFieldRead>(features.Length);
        var readFeatures = _memory.Rent<int>(features.Length);
        var readCount = 0;
        var partial = 0;
        foreach (var f in features)
        {
            var info = _plan.Features[f];
            if (Overlaps(_table.GetFilled(info.Slot), bits))
            {
                partial++;
                continue;
            }

            reads[readCount] = new LocalFieldRead(local.TableFields[info.FieldIndex], _table.GetOrCreateSlot(info.Slot), info.Definition.Absent);
            readFeatures[readCount++] = f;
        }

        if (readCount > 0)
        {
            Span<int> nonFinite = readCount <= 128 ? stackalloc int[readCount] : _memory.Rent<int>(readCount).AsSpan(0, readCount);
            version.Fill(records, rows, reads.AsSpan(0, readCount), nonFinite);
            for (var r = 0; r < readCount; r++)
            {
                var info = _plan.Features[readFeatures[r]];
                Bits.Or(_table.GetFilled(info.Slot), bits);
                AddNonFinite(info, nonFinite[r]);
            }
        }

        if (partial == 0)
        {
            return;
        }

        var subsetRows = _memory.Rent<int>(rows.Length);
        var subsetRecords = _memory.Rent<LocalRecordRef>(rows.Length);
        foreach (var f in features)
        {
            var info = _plan.Features[f];
            var filled = _table.GetFilled(info.Slot);
            if (!Overlaps(filled, bits))
            {
                continue; // filled above
            }

            var n = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                if (!Bits.Get(filled, rows[i]))
                {
                    subsetRows[n] = rows[i];
                    subsetRecords[n++] = records[i];
                }
            }

            var replaced = version.Fill(
                local.TableFields[info.FieldIndex], subsetRecords.AsSpan(0, n), subsetRows.AsSpan(0, n), _table.GetOrCreateSlot(info.Slot),
                info.Definition.Absent, filled);
            AddNonFinite(info, replaced);
        }
    }

    /// <summary>The features' state after a read: degraded with a degraded key (design 6.7), otherwise ok.</summary>
    private void SetReadState(FetchGroup group, ReadOnlySpan<int> features)
    {
        var keyState = group.KeyFeatureIndex >= 0 ? _state[group.KeyFeatureIndex] : FeatureState.Ok;
        var error = keyState == FeatureState.Degraded
            ? _errors[group.KeyFeatureIndex] ?? $"key feature '{_plan.Features[group.KeyFeatureIndex].Definition.Name}' is {keyState}"
            : null;
        foreach (var f in features)
        {
            if (_state[f] == FeatureState.Failed)
            {
                continue;
            }

            if (error is not null)
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
    /// Reads the local context groups of <paramref name="closure"/> (one key each, the request input), at the start of
    /// the request with the other context features. A missing context key gives every feature its absent value, as for
    /// a fetch.
    /// </summary>
    private void ReadLocalContext(IndexList closure, bool[] member)
    {
        var seen = _memory.RentCleared<bool>(Math.Max(1, _plan.Groups.Count));
        for (var c = 0; c < closure.Count; c++)
        {
            var info = _plan.Features[closure.Items[c]];
            if (!info.IsContext || info.IsDerived || info.GroupIndex < 0 || seen[info.GroupIndex])
            {
                continue;
            }

            var group = _plan.Groups[info.GroupIndex];
            if (!ReadsLocally(group))
            {
                continue;
            }

            seen[info.GroupIndex] = true;
            var selected = _memory.Rent<int>(group.FeatureIndices.Length);
            var count = 0;
            foreach (var f in group.FeatureIndices)
            {
                if (member[f])
                {
                    selected[count++] = f;
                }
            }

            ReadLocalContext(group, selected.AsSpan(0, count));
        }
    }

    private void ReadLocalContext(FetchGroup group, ReadOnlySpan<int> features)
    {
        var local = group.Local!;
        if (!local.Table.IsLoaded)
        {
            FailNotLoaded(local, features);
            return;
        }

        var value = group.ContextKeyId >= 0 ? _context[group.ContextKeyId] : ScalarValue.Missing;
        if (value.IsMissing)
        {
            foreach (var f in features)
            {
                _state[f] = FeatureState.Ok;
                StoreContext(_plan.Features[f], ScalarValue.Missing);
            }

            return;
        }

        var started = _hook is null ? 0 : Stopwatch.GetTimestamp();
        var version = Pin(group);
        var key = ToKey(value);
        var record = version.Find(key); // a key of the other type is not found
        CountKeys(local.Table, record.Found ? 1 : 0, record.Found ? 0 : 1);
        foreach (var f in features)
        {
            var info = _plan.Features[f];
            _state[f] = FeatureState.Ok;
            StoreContext(info, version.Read(local.TableFields[info.FieldIndex], record));
        }

        if (_hook is { Replays: false } recorder)
        {
            var array = features.ToArray();
            var fields = FieldBindings(group, array);
            var batch = new EntityKeyBatch { Shape = group.Shape, ContextKey = key };
            Record(recorder, group, batch, fields, new LocalReadValues(version, new[] { record }, TableFields(group, array)), started);
        }
    }

    /// <summary>A table that is not loaded fails the features (design 6.7 layer 2): an empty table would serve every key as missing.</summary>
    private void FailNotLoaded(LocalGroupTable local, ReadOnlySpan<int> features)
    {
        var error = $"local table {local.Spec.Name} (source '{local.Spec.Schema.Source}' in {local.Spec.Artifact}) is not loaded; " +
            "a plan the engine serves waits for its tables, a plan compiled without the engine's table host has none";
        foreach (var f in features)
        {
            _state[f] = FeatureState.Failed;
            _errors[f] = error;
        }
    }

    /// <summary>Read hit ratio (design 13.1): keys the table held, and keys it did not. Not counted by a replay, which never gets here.</summary>
    private static void CountKeys(LocalFeatureTable table, int hits, int misses)
    {
        if (hits > 0)
        {
            StrataRecMetrics.LocalKeys.Add(hits, table.HitTags);
        }

        if (misses > 0)
        {
            StrataRecMetrics.LocalKeys.Add(misses, table.MissTags);
        }
    }

    /// <summary>
    /// Records a candidate read as one feature call: the distinct keys of the read rows in live-row order (the keys a
    /// replay's fetch of the same rows takes), and every read feature's stored value for each key.
    /// </summary>
    private void RecordRead(IFeatureCallHook recorder, FetchGroup group, ReadOnlySpan<int> features, TableVersion version, ulong[] rowBits, long started)
    {
        var keys = new List<EntityKey>();
        var distinct = new HashSet<EntityKey>();
        foreach (var row in _table.Rows.Order)
        {
            if (Bits.Get(rowBits, row) && TryCandidateKey(group, row, out var key) == KeyLookup.Found && distinct.Add(key))
            {
                keys.Add(key);
            }
        }

        var records = new LocalRecordRef[keys.Count];
        for (var k = 0; k < records.Length; k++)
        {
            records[k] = version.Find(keys[k]);
        }

        var array = features.ToArray();
        var batch = new EntityKeyBatch { Shape = group.Shape, CandidateKeys = keys.ToArray() };
        Record(recorder, group, batch, FieldBindings(group, array), new LocalReadValues(version, records, TableFields(group, array)), started);
    }

    private static void Record(
        IFeatureCallHook recorder, FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, LocalReadValues values, long started)
    {
        var sequence = recorder.Begin(group, batch, fields);
        recorder.Record(
            new FeatureCallInfo(group, batch, fields, sequence, true, FetchStatus.Ok, IoOutcome.Ok, Stopwatch.GetElapsedTime(started)), values);
    }

    /// <summary>The table field of each of <paramref name="features"/>, in order.</summary>
    private int[] TableFields(FetchGroup group, int[] features)
    {
        var fields = new int[features.Length];
        for (var i = 0; i < features.Length; i++)
        {
            fields[i] = group.Local!.TableFields[_plan.Features[features[i]].FieldIndex];
        }

        return fields;
    }

    private static bool Overlaps(ReadOnlySpan<ulong> filled, ReadOnlySpan<ulong> rows)
    {
        for (var w = 0; w < rows.Length; w++)
        {
            if ((filled[w] & rows[w]) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a recorded local read returned: the stored values (non-finite ones as stored, like a connector's).</summary>
    private sealed class LocalReadValues(TableVersion version, LocalRecordRef[] records, int[] tableFields) : IStagedFeatureValues
    {
        public ScalarValue Get(int fieldIndex, int keyIndex) => version.Read(tableFields[fieldIndex], records[keyIndex]);
    }
}
