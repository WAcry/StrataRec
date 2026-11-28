using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

namespace StrataRec.Features.Runtime;

/// <remarks>
/// <para>A pinned version still sees an update its writer makes in place, so looking a key up twice can give two
/// records. Every consumer of a read takes the records of its single lookup: rows that share a key (a foreign key) get
/// the same record, and the recording holds exactly the records the fill used, never a later lookup's.</para>
/// </remarks>
public sealed partial class FeatureFetcher
{
    private TableVersion?[]? _pins; // by group index, rented on the first local read
    private FetchKeys _localKeys;

    private bool ReadsLocally(FetchGroup group) => group.Supply.Mode switch
    {
        SupplyMode.Fetch or SupplyMode.Service => false,
        SupplyMode.Local or SupplyMode.Cache => _hook is not { Replays: true },
    };

    /// <summary>
    /// A local group is read synchronously by the node that needs it, for the rows live then, so it is never
    /// prefetched; nor under replay, whose answer must take the same rows' keys.
    /// </summary>
    private static bool Prefetches(FetchGroup group) => group.Supply.Mode switch
    {
        SupplyMode.Fetch or SupplyMode.Service => true,
        SupplyMode.Local or SupplyMode.Cache => false,
    };

    private TableVersion Pin(FetchGroup group)
    {
        var pins = _pins ??= _memory.RentCleared<TableVersion?>(Math.Max(1, _plan.Groups.Count));
        return pins[group.Index] ??= group.Supply.Table!.Table.Pin();
    }

    /// <summary>
    /// A cache read that misses leaves its misses resolving (<see cref="TakeCacheRead"/>).
    /// </summary>
    private void ReadLocalGroups(IndexList groups, bool[] member, RowScope scope, CancellationToken cancellationToken)
    {
        for (var g = 0; g < groups.Count; g++)
        {
            var group = _plan.Groups[groups.Items[g]];
            if (!ReadsLocally(group))
            {
                continue;
            }

            // Over with the read: a cache read that goes on resolving keeps a copy of the features.
            var selected = _memory.RentScratch<int>(group.FeatureIndices.Length);
            try
            {
                var count = SelectNeeded(group, member, skipInFlight: false, scope, selected);
                if (count > 0 && !FailIfKeyFailed(group, selected.AsSpan(0, count)))
                {
                    switch (group.Supply.Mode)
                    {
                        case SupplyMode.Local:
                            ReadLocal(group, selected.AsSpan(0, count), scope);
                            break;
                        case SupplyMode.Cache:
                            ReadCached(group, selected.AsSpan(0, count), scope, cancellationToken);
                            break;
                        case SupplyMode.Fetch:
                            throw new InvalidOperationException("A fetched group is not read locally.");
                    }
                }
            }
            finally
            {
                _memory.ReturnScratch(selected);
            }
        }
    }

    private void ReadLocal(FetchGroup group, ReadOnlySpan<int> features, RowScope scope)
    {
        var local = group.Supply.Table!;
        if (!local.Table.IsLoaded)
        {
            FailNotLoaded(local, features);
            return;
        }

        var started = _hook is null ? 0 : Stopwatch.GetTimestamp();
        var version = Pin(group);
        var batch = LookUp(group, features, version, scope);
        try
        {
            if (batch.Rows.Length > 0)
            {
                Fill(group, features, version, batch.Rows, batch.RowRecords);
                if (batch.KeyRecords.Length > 0)
                {
                    // Recorded before its metrics: a host's listener that throws on them fails the fill, as on any metric
                    // the request thread records, but the read it served is in the recording.
                    if (_hook is { Replays: false } recorder)
                    {
                        RecordRead(recorder, group, features, version, batch, started);
                    }

                    CountKeys(local.Table, batch.Found, batch.KeyRecords.Length - batch.Found);
                    if (CountsValues)
                    {
                        CountTableValues(group, features, version, batch.KeyRecords);
                    }
                }
            }
        }
        finally
        {
            batch.Return(_memory);
        }

        SetReadState(group, features);
    }

    private LocalReadBatch LookUp(FetchGroup group, ReadOnlySpan<int> features, TableVersion version, RowScope scope)
    {
        var scoped = RowsOf(scope);
        var capacity = Math.Max(1, scoped.Length);
        var rows = _memory.RentScratch<int>(capacity);
        var entries = _memory.RentScratch<int>(capacity); // per row: its key's entry in the index, or -1 without a key
        ref var keys = ref _localKeys;
        var layout = group.KeyLayout;
        var sharedBuffer = default(KeyPartBuffer);
        Span<EntityKey> shared = sharedBuffer;
        var hasShared = TrySharedKey(group, shared);
        var varyingBuffer = default(KeyPartBuffer);
        Span<EntityKey> varying = varyingBuffer;
        varying = varying[..layout.VaryingCount];
        var one = group.OneVaryingFeature;
        var indexed = false;
        var count = 0;
        foreach (var row in scoped)
        {
            if (!NeedsFill(features, row))
            {
                continue;
            }

            EntityKey key = default;
            var lookup = !hasShared ? KeyLookup.Missing : one != int.MinValue ? PartKey(one, row, out key) : RowKey(group, row, varying);
            if (lookup == KeyLookup.NotReady)
            {
                continue;
            }

            var entry = -1;
            if (lookup == KeyLookup.Found)
            {
                if (!indexed)
                {
                    keys.Reset(_memory, layout, shared[..(layout.PartCount - layout.VaryingCount)], scoped.Length, key);
                    indexed = true;
                }

                entry = one != int.MinValue ? keys.GetOrAdd(key) : keys.GetOrAdd(varying);
            }

            rows[count] = row;
            entries[count++] = entry;
        }

        var keyCount = indexed ? keys.Count : 0;
        var keyArray = _memory.RentScratch<LocalRecordRef>(Math.Max(1, keyCount));
        var keyRecords = keyArray.AsSpan(0, keyCount);
        var found = keyCount == 0 ? 0
            : keys.Plain is not { } plain ? version.Lookup(keys.Batch(_memory), keyRecords)
            : plain.KeyType == EntityKeyType.String ? version.Lookup(plain.StringKeys!, keyRecords)
            : version.Lookup(plain.Int64Keys, keyRecords);
        var rowArray = _memory.RentScratch<LocalRecordRef>(capacity);
        var rowRecords = rowArray.AsSpan(0, count);
        for (var i = 0; i < count; i++)
        {
            rowRecords[i] = entries[i] < 0 ? default : keyRecords[entries[i]];
        }

        return new LocalReadBatch(rows, entries, rowArray, count, indexed ? keys : default, keyArray, keyCount, found);
    }

    /// <summary>
    /// Features unfilled on every row take one multi-field fill; a feature already filled on some of the rows is filled
    /// on the others only, so a value a node has already read never changes within the request.
    /// </summary>
    private void Fill(
        FetchGroup group, ReadOnlySpan<int> features, TableVersion version, ReadOnlySpan<int> rows, ReadOnlySpan<LocalRecordRef> records)
    {
        // Every buffer here is scratch, given back before this returns: the fill writes into the table's slots and keeps nothing.
        var local = group.Supply.Table!;
        var words = _table.GetFilled(_plan.Features[features[0]].Slot).Length;
        var rowBits = _memory.RentScratch<ulong>(words);
        var reads = _memory.RentScratch<LocalFieldRead>(features.Length);
        var readFeatures = _memory.RentScratch<int>(features.Length);
        try
        {
            var bits = rowBits.AsSpan(0, words);
            bits.Clear();
            foreach (var row in rows)
            {
                Bits.Set(bits, row);
            }

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
                FillTogether(version, records, rows, reads.AsSpan(0, readCount), readFeatures.AsSpan(0, readCount), bits);
            }

            if (partial > 0)
            {
                FillEach(group, features, version, rows, records, bits);
            }
        }
        finally
        {
            _memory.ReturnScratch(rowBits);
            _memory.ReturnScratch(reads);
            _memory.ReturnScratch(readFeatures);
        }
    }

    private void FillTogether(
        TableVersion version, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ReadOnlySpan<LocalFieldRead> reads,
        ReadOnlySpan<int> features, ReadOnlySpan<ulong> bits)
    {
        LocalFillCounts[]? rented = null;
        var counts = reads.Length <= 128 ? stackalloc LocalFillCounts[reads.Length] : (rented = _memory.RentScratch<LocalFillCounts>(reads.Length)).AsSpan(0, reads.Length);
        try
        {
            version.Fill(records, rows, reads, counts);
            for (var r = 0; r < reads.Length; r++)
            {
                var info = _plan.Features[features[r]];
                Bits.Or(_table.GetFilled(info.Slot), bits);
                AddReplaced(info, counts[r]);
            }
        }
        finally
        {
            if (rented is not null)
            {
                _memory.ReturnScratch(rented);
            }
        }
    }

    private void FillEach(
        FetchGroup group, ReadOnlySpan<int> features, TableVersion version, ReadOnlySpan<int> rows, ReadOnlySpan<LocalRecordRef> records,
        ReadOnlySpan<ulong> bits)
    {
        var local = group.Supply.Table!;
        var subsetRows = _memory.RentScratch<int>(rows.Length);
        var subsetRecords = _memory.RentScratch<LocalRecordRef>(rows.Length);
        try
        {
            foreach (var f in features)
            {
                var info = _plan.Features[f];
                var filled = _table.GetFilled(info.Slot);
                if (!Overlaps(filled, bits))
                {
                    continue;
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
                AddReplaced(info, replaced);
            }
        }
        finally
        {
            _memory.ReturnScratch(subsetRows);
            _memory.ReturnScratch(subsetRecords);
        }
    }

    private void AddReplaced(FeatureRuntimeInfo info, LocalFillCounts replaced)
    {
        AddNonFinite(info, replaced.NonFinite);
        if (replaced.OutOfRange > 0)
        {
            _counters.AddOutOfRange(replaced.OutOfRange);
        }
    }

    private void SetReadState(FetchGroup group, ReadOnlySpan<int> features)
    {
        var error = KeyState(group, out var keyError) == FeatureState.Degraded ? keyError : null;
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
    /// A missing context key gives every feature its absent value, as for a fetch.
    /// </summary>
    /// <returns>A cache read that missed, resolving: <see cref="CompleteCachedContextAsync"/> completes it; null when the
    /// features are stored.</returns>
    private CacheRead? ReadLocalContext(FetchGroup group, ReadOnlySpan<int> features, CancellationToken cancellationToken)
    {
        var local = group.Supply.Table!;
        var cache = local.Table.Cache;
        if (cache is null && !local.Table.IsLoaded)
        {
            FailNotLoaded(local, features); // a cache table needs no load: an empty cache only misses
            return null;
        }

        if (!TryContextKey(group, out var key))
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
        var record = default(LocalRecordRef);
        version.Lookup(key, new Span<LocalRecordRef>(ref record));
        if (cache is not null)
        {
            return ReadCachedContext(group, features, version, key, record, started, cancellationToken);
        }

        StoreLocalContext(group, features, version, key, record, started);
        CountKeys(local.Table, record.Found ? 1 : 0, record.Found ? 0 : 1);
        if (CountsValues)
        {
            CountTableValues(group, features, version, new ReadOnlySpan<LocalRecordRef>(in record));
        }

        return null;
    }

    private void StoreLocalContext(FetchGroup group, ReadOnlySpan<int> features, TableVersion version, in EntityKeyBatch key, in LocalRecordRef record, long started)
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
            Record(recorder, group, key.Copy(), fields, new LocalReadValues(version, new[] { record }, TableFields(group, array)), started);
        }
    }

    /// <summary>A table that is not loaded fails the features: an empty table would serve every key as missing.</summary>
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

    private void CountTableValues(FetchGroup group, ReadOnlySpan<int> features, TableVersion version, ReadOnlySpan<LocalRecordRef> keyRecords)
    {
        var tableFields = group.Supply.Table!.TableFields;
        foreach (var f in features)
        {
            var info = _plan.Features[f];
            var present = version.CountValues(tableFields[info.FieldIndex], keyRecords);
            CountValues(info, present, keyRecords.Length - present);
        }
    }

    /// <summary>
    /// The recording outlives the read, and the next read resets the key index: the keys and records are copied.
    /// </summary>
    private void RecordRead(
        IFeatureCallHook recorder, FetchGroup group, ReadOnlySpan<int> features, TableVersion version, in LocalReadBatch batch, long started)
    {
        var array = features.ToArray();
        var call = batch.Keys.Copy();
        Record(recorder, group, call, FieldBindings(group, array), new LocalReadValues(version, batch.KeyRecords.ToArray(), TableFields(group, array)), started);
    }

    private static void Record(
        IFeatureCallHook recorder, FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, LocalReadValues values, long started)
    {
        var shared = ImmutableCollectionsMarshal.AsImmutableArray(fields);
        var sequence = recorder.Begin(group, batch, shared);
        recorder.Record(
            new FeatureCallInfo(group, batch, shared, sequence, true, FetchStatus.Ok, IoOutcome.Ok, Stopwatch.GetElapsedTime(started)), values);
    }

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
    /// (<see cref="Keys"/>, not set when no row has a key; entry <c>k</c> is the key of <see cref="KeyRecords"/>[k]) and what
    /// their single lookup returned, <see cref="Found"/> of them held by the table. Its arrays are scratch, given back by
    /// <see cref="Return"/>.
    /// </summary>
    private readonly ref struct LocalReadBatch
    {
        private readonly int[] _rows;
        private readonly int[] _rowKeys;
        private readonly LocalRecordRef[] _rowRecords;
        private readonly LocalRecordRef[] _keyRecords;

        public LocalReadBatch(
            int[] rows, int[] rowKeys, LocalRecordRef[] rowRecords, int rowCount, FetchKeys keys, LocalRecordRef[] keyRecords, int keyCount, int found)
        {
            _rows = rows;
            _rowKeys = rowKeys;
            _rowRecords = rowRecords;
            _keyRecords = keyRecords;
            Rows = rows.AsSpan(0, rowCount);
            RowKeys = rowKeys.AsSpan(0, rowCount);
            RowRecords = rowRecords.AsSpan(0, rowCount);
            Keys = keys;
            KeyRecords = keyRecords.AsSpan(0, keyCount);
            Found = found;
        }

        public ReadOnlySpan<int> Rows { get; }

        /// <summary>Per row, its key's entry in <see cref="Keys"/> (and <see cref="KeyRecords"/>), or -1 for a row without a key.</summary>
        public ReadOnlySpan<int> RowKeys { get; }

        public ReadOnlySpan<LocalRecordRef> RowRecords { get; }

        public FetchKeys Keys { get; }

        public ReadOnlySpan<LocalRecordRef> KeyRecords { get; }

        public int Found { get; }

        /// <summary>Gives the arrays back; the batch is not read after this.</summary>
        public void Return(RequestMemory memory)
        {
            memory.ReturnScratch(_rows);
            memory.ReturnScratch(_rowKeys);
            memory.ReturnScratch(_rowRecords);
            memory.ReturnScratch(_keyRecords);
        }
    }

    /// <summary>What a recorded local read returned: the stored values (non-finite ones as stored, like a connector's).</summary>
    private sealed class LocalReadValues(TableVersion version, LocalRecordRef[] records, int[] tableFields) : IStagedFeatureValues
    {
        public ScalarValue Get(int fieldIndex, int keyIndex) => version.Read(tableFields[fieldIndex], records[keyIndex]);
    }
}
