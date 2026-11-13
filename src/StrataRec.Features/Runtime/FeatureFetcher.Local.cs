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
/// version. For the features a node needs, one <see cref="LocalReadBatch"/>: the rows' distinct keys, one
/// <see cref="TableVersion.Lookup(ReadOnlySpan{long}, Span{LocalRecordRef})"/> of them, and each row's record taken from
/// it; then one multi-field <see cref="TableVersion.Fill(ReadOnlySpan{LocalRecordRef}, ReadOnlySpan{int}, ReadOnlySpan{LocalFieldRead}, Span{int})"/>
/// into the slots and one bitmap of the rows OR-ed into each slot's fill bits. Buffers are rented from the request
/// memory and the key index is the fetcher's, so the read allocates nothing (a <c>text</c> value becomes a string).</para>
/// <para><b>One batch, one answer per key.</b> A pinned version still sees an update its writer makes in place (the
/// index slot is re-pointed to the new record), so looking a key up twice can give two records. Every consumer of a
/// read takes the records of its single lookup: rows that share a key (a foreign key) get the same record, and the
/// recording holds exactly the records the fill used, never a later lookup's.</para>
/// <para><b>Semantics</b> are the fetched features' (design 6.7): a key the table does not hold, a record without the
/// field, a missing foreign or context key, and a non-finite value all give <see cref="Catalog.FeatureDefinition.Absent"/>;
/// a failed key fails the group's features and a degraded one degrades them. A table that is not loaded fails the
/// features, never serves them as missing.</para>
/// <para><b>Replay</b> (design 13.4). A recording request records each read as a feature call through the
/// <see cref="IFeatureCallHook"/>, with the keys and the values it read; a replaying request answers local groups like
/// fetched ones, from the recording through the staging path, so a replay never reads the table. Both sides take the
/// keys of the same rows, in live-row order and deduplicated by an <see cref="EntityKeyIndex"/> (first-seen order), so a
/// replay on the same configuration matches the recorded call exactly.</para>
/// </remarks>
public sealed partial class FeatureFetcher
{
    private TableVersion?[]? _pins; // by group index, rented on the first local read
    private readonly EntityKeyIndex? _localKeys; // the distinct keys of the current local read; null if no group is read locally

    /// <summary>
    /// Whether this request reads the group from its table. A replay answers a local group through the fetch path instead,
    /// from the recording (which it never prefetches either: see <see cref="Prefetches"/>).
    /// </summary>
    private bool ReadsLocally(FetchGroup group) => group.Supply.Mode switch
    {
        SupplyMode.Fetch => false,
        SupplyMode.Local or SupplyMode.Cache => _hook is not { Replays: true },
    };

    /// <summary>
    /// Whether a prefetch (design 5.4) may start the group. A local group is read synchronously by the node that needs
    /// it, for the rows live then, so it is never prefetched; nor under replay, whose answer must take the same rows' keys.
    /// </summary>
    /// <remarks>A cache group is read like a local one (its misses are resolved before the read completes), so the same
    /// holds for it: never prefetched. Its misses therefore wait for the node that needs them; see design 6.6.</remarks>
    private static bool Prefetches(FetchGroup group) => group.Supply.Mode switch
    {
        SupplyMode.Fetch => true,
        SupplyMode.Local or SupplyMode.Cache => false,
    };

    /// <summary>The group's table version for this request, pinned on its first read.</summary>
    private TableVersion Pin(FetchGroup group)
    {
        var pins = _pins ??= _memory.RentCleared<TableVersion?>(Math.Max(1, _plan.Groups.Count));
        return pins[group.Index] ??= group.Supply.Table!.Table.Pin();
    }

    /// <summary>
    /// Reads every local and cache group of <paramref name="groups"/> for the members of <paramref name="member"/> still
    /// unfilled. A cache read that misses leaves its misses resolving (<see cref="TakeCacheRead"/>).
    /// </summary>
    private void ReadLocalGroups(IndexList groups, bool[] member, CancellationToken cancellationToken)
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
                switch (group.Supply.Mode)
                {
                    case SupplyMode.Local:
                        ReadLocal(group, selected.AsSpan(0, count));
                        break;
                    case SupplyMode.Cache:
                        ReadCached(group, selected.AsSpan(0, count), cancellationToken);
                        break;
                    case SupplyMode.Fetch:
                        throw new InvalidOperationException("A fetched group is not read locally.");
                }
            }
        }
    }

    /// <summary>One read of a candidate (or foreign-key) group: the live rows that miss any of <paramref name="features"/>.</summary>
    private void ReadLocal(FetchGroup group, ReadOnlySpan<int> features)
    {
        var local = group.Supply.Table!;
        if (!local.Table.IsLoaded)
        {
            FailNotLoaded(local, features);
            return;
        }

        var started = _hook is null ? 0 : Stopwatch.GetTimestamp();
        var version = Pin(group);
        var batch = LookUp(group, features, version);
        if (batch.Rows.Length > 0)
        {
            Fill(group, features, version, batch.Rows, batch.RowRecords);
            if (batch.KeyRecords.Length > 0)
            {
                CountKeys(local.Table, batch.Found, batch.KeyRecords.Length - batch.Found);
                if (_hook is { Replays: false } recorder)
                {
                    RecordRead(recorder, group, features, version, batch, started);
                }
            }
        }

        SetReadState(group, features);
    }

    /// <summary>
    /// The batch of one read: the live rows that miss any of <paramref name="features"/> and whose key is computed, their
    /// distinct keys in first-seen order, one lookup of those keys, and each row's record from that lookup. A row without
    /// a key (a missing foreign key) gets no record, so every field takes its absent value; a key of the other type than
    /// the table's is looked up and not found. A row whose key is not computed yet is left for a later read, as with a
    /// fetch.
    /// </summary>
    private LocalReadBatch LookUp(FetchGroup group, ReadOnlySpan<int> features, TableVersion version)
    {
        var live = _table.Rows.Order;
        var capacity = Math.Max(1, live.Length);
        var rows = _memory.Rent<int>(capacity);
        var entries = _memory.Rent<int>(capacity); // per row: its key's entry in the index, or -1 without a key
        var keys = _localKeys!;
        var indexed = false;
        var count = 0;
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

            var entry = -1;
            if (lookup == KeyLookup.Found)
            {
                if (!indexed)
                {
                    // Sized to the live rows, an upper bound on the distinct keys: the index never grows. All keys of a
                    // group have one type (the key column's).
                    keys.Reset(key.IsString ? EntityKeyType.String : EntityKeyType.Int64, live.Length);
                    indexed = true;
                }

                entry = keys.GetOrAdd(key, keys.Count, out _);
            }

            rows[count] = row;
            entries[count++] = entry;
        }

        var keyCount = indexed ? keys.Count : 0;
        var keyRecords = _memory.Rent<LocalRecordRef>(Math.Max(1, keyCount)).AsSpan(0, keyCount);
        var found = keyCount == 0 ? 0
            : keys.KeyType == EntityKeyType.String ? version.Lookup(keys.StringKeys!, keyRecords) // non-null keys where nullable ones are accepted
            : version.Lookup(keys.Int64Keys, keyRecords);
        var rowRecords = _memory.Rent<LocalRecordRef>(capacity).AsSpan(0, count);
        for (var i = 0; i < count; i++)
        {
            rowRecords[i] = entries[i] < 0 ? default : keyRecords[entries[i]];
        }

        return new LocalReadBatch(rows.AsSpan(0, count), entries.AsSpan(0, count), rowRecords, indexed ? keys : null, keyRecords, found);
    }

    /// <summary>
    /// Fills <paramref name="features"/> for <paramref name="rows"/> from their <paramref name="records"/> and marks them
    /// filled. Features unfilled on every row (the usual case: a node's features are read together) take one multi-field
    /// fill; a feature already filled on some of the rows (an earlier read of it, before rows were appended) is filled on
    /// the others only, so a value a node has already read never changes within the request.
    /// </summary>
    private void Fill(
        FetchGroup group, ReadOnlySpan<int> features, TableVersion version, ReadOnlySpan<int> rows, ReadOnlySpan<LocalRecordRef> records)
    {
        var local = group.Supply.Table!;
        var words = _table.GetFilled(_plan.Features[features[0]].Slot).Length;
        var rowBits = _memory.RentCleared<ulong>(words);
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
    /// <returns>The cache reads that missed, resolving: <see cref="CompleteCachedContextAsync"/> completes them.</returns>
    private CacheRead? ReadLocalContext(IndexList closure, bool[] member, CancellationToken cancellationToken)
    {
        CacheRead? first = null;
        CacheRead? last = null;
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

            if (ReadLocalContext(group, selected.AsSpan(0, count), cancellationToken) is { } read)
            {
                if (last is null)
                {
                    first = read;
                }
                else
                {
                    last.Next = read;
                }

                last = read;
            }
        }

        return first;
    }

    private CacheRead? ReadLocalContext(FetchGroup group, ReadOnlySpan<int> features, CancellationToken cancellationToken)
    {
        var local = group.Supply.Table!;
        var cache = local.Table.Cache;
        if (cache is null && !local.Table.IsLoaded)
        {
            FailNotLoaded(local, features); // a cache table needs no load: an empty cache only misses
            return null;
        }

        var value = group.ContextKeyId >= 0 ? _context[group.ContextKeyId] : ScalarValue.Missing;
        if (value.IsMissing)
        {
            foreach (var f in features)
            {
                _state[f] = FeatureState.Ok;
                StoreContext(_plan.Features[f], ScalarValue.Missing);
            }

            return null;
        }

        var started = _hook is null ? 0 : Stopwatch.GetTimestamp();
        var version = Pin(group);
        var key = ToKey(value);
        var record = version.Find(key); // a key of the other type is not found
        if (cache is not null)
        {
            return ReadCachedContext(group, features, version, key, record, started, cancellationToken);
        }

        CountKeys(local.Table, record.Found ? 1 : 0, record.Found ? 0 : 1);
        StoreLocalContext(group, features, version, key, record, started);
        return null;
    }

    /// <summary>Publishes the values of a context key's record (or of no record) and records the read.</summary>
    private void StoreLocalContext(FetchGroup group, ReadOnlySpan<int> features, TableVersion version, in EntityKey key, in LocalRecordRef record, long started)
    {
        var local = group.Supply.Table!;
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
    /// Records a candidate read as one feature call: the batch's distinct keys in first-seen order (the keys a replay's
    /// fetch of the same rows takes), and every read feature's stored value in the record the batch's lookup returned for
    /// each key. The recording outlives the read, and the next read resets the key index: the keys and records are copied.
    /// </summary>
    private void RecordRead(
        IFeatureCallHook recorder, FetchGroup group, ReadOnlySpan<int> features, TableVersion version, in LocalReadBatch batch, long started)
    {
        var keys = new EntityKey[batch.KeyRecords.Length];
        for (var k = 0; k < keys.Length; k++)
        {
            keys[k] = batch.Keys!.KeyAt(k);
        }

        var array = features.ToArray();
        var call = new EntityKeyBatch { Shape = group.Shape, CandidateKeys = keys };
        Record(recorder, group, call, FieldBindings(group, array), new LocalReadValues(version, batch.KeyRecords.ToArray(), TableFields(group, array)), started);
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
            fields[i] = group.Supply.Table!.TableFields[_plan.Features[features[i]].FieldIndex];
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

    /// <summary>
    /// One local read (see the type's remarks): the rows to fill and the record of each, the distinct keys
    /// (<see cref="Keys"/>, null when no row has a key; entry <c>k</c> is the key of <see cref="KeyRecords"/>[k]) and what
    /// their single lookup returned, <see cref="Found"/> of them held by the table.
    /// </summary>
    private readonly ref struct LocalReadBatch
    {
        public LocalReadBatch(
            ReadOnlySpan<int> rows, ReadOnlySpan<int> rowKeys, ReadOnlySpan<LocalRecordRef> rowRecords, EntityKeyIndex? keys,
            ReadOnlySpan<LocalRecordRef> keyRecords, int found)
        {
            Rows = rows;
            RowKeys = rowKeys;
            RowRecords = rowRecords;
            Keys = keys;
            KeyRecords = keyRecords;
            Found = found;
        }

        public ReadOnlySpan<int> Rows { get; }

        /// <summary>Per row, its key's entry in <see cref="Keys"/> (and <see cref="KeyRecords"/>), or -1 for a row without a key.</summary>
        public ReadOnlySpan<int> RowKeys { get; }

        public ReadOnlySpan<LocalRecordRef> RowRecords { get; }

        public EntityKeyIndex? Keys { get; }

        public ReadOnlySpan<LocalRecordRef> KeyRecords { get; }

        public int Found { get; }
    }

    /// <summary>What a recorded local read returned: the stored values (non-finite ones as stored, like a connector's).</summary>
    private sealed class LocalReadValues(TableVersion version, LocalRecordRef[] records, int[] tableFields) : IStagedFeatureValues
    {
        public ScalarValue Get(int fieldIndex, int keyIndex) => version.Read(tableFields[fieldIndex], records[keyIndex]);
    }
}
