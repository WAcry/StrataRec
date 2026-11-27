using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;

namespace StrataRec.Features.Local;

/// <summary>
/// The single writer of a <see cref="LocalFeatureTable"/>: applies updates, deletes, checkpoints
/// and reloads, and compacts. Every call must come from one thread at a time (the update source's consumer, or the cache's
/// background writer); readers never wait for it.
/// </summary>
/// <remarks>
/// <para>A record is built in private buffers (<see cref="RecordBuilder"/>) and only reaches the pages at
/// <see cref="RecordBuilder.Commit"/>: the record bytes are written into free space at the end of a page, then the index
/// slot is pointed at them with one release write. Published values are immutable, so a reader sees the old record or
/// the new one, both complete. String values are encoded at commit, after any compaction the commit itself triggered, so a code
/// always belongs to the dictionary of the version the record lands in.</para>
/// <para>Compaction runs here, synchronously: when superseded records and the dictionary strings only they referenced
/// make up more than half of the record and string bytes (and at least
/// <see cref="LocalFeatureTableOptions.CompactionMinBytes"/>), or when a write would pass <c>max_bytes</c> and superseded
/// records could be freed. It copies the current record of every key, tombstones included, into a new version
/// built aside (<see cref="BuildCompacted()"/>), which is published whole only when it fits <c>max_bytes</c> (with the
/// pending write, when the write asked for it); otherwise it is dropped and the table stays as it was. Updates wait
/// while it runs.</para>
/// </remarks>
public sealed class LocalTableWriter : IFeatureTableWriter
{
    private readonly LocalFeatureTable _table;
    private readonly RecordLayout _layout;
    private readonly LocalFeatureTableOptions _options;
    private TableVersion _version; // where writes go: the table's current version, or the one a reload is building
    private bool _reloading;
    private long _reloadStarted; // Stopwatch timestamp of BeginReload, for the load duration
    private long _generation;          // of the newest version installed; a version built and discarded does not count
    private long _autoCompactionDeclined; // superseded bytes when an automatic compaction did not fit, see MaybeCompact

    // The record being built. A builder is valid while _open and its token equals _token.
    private int _token;
    private bool _open;
    private bool _ignored;
    private long _recordVersion;
    private long _indexKey;
    private byte[] _key = new byte[64]; // a hashed key's canonical bytes (TableKeys); empty for a one-part int64 key
    private int _keyLength;
    private TableVersion? _probedVersion;
    private IndexTable? _probedIndex;
    private long _oldPosition;
    private int _slot;
    private readonly byte[] _fixed;
    private readonly ulong[] _written;
    private readonly PendingBuffer _pending = new();
    private long[] _newStrings = new long[16]; // scratch set of the record's new strings, see NewStringBytes
    // Each budget trial replaces this lookup. The last successful trial used the dictionary we now encode into.
    private int _lookedUpField = -1;
    private int _lookedUpCode;
    private readonly int[] _pendingStart;
    private readonly int[] _pendingCount;
    private int _openList = -1;

    internal LocalTableWriter(LocalFeatureTable table, RecordLayout layout, LocalFeatureTableOptions options, TableVersion initial)
    {
        _table = table;
        _layout = layout;
        _options = options;
        _version = initial;
        _generation = initial.Generation;
        _fixed = new byte[layout.FixedSize];
        _written = new ulong[Bits.WordCount(Math.Max(1, layout.FieldCount))];
        _pendingStart = new int[layout.FieldCount];
        _pendingCount = new int[layout.FieldCount];
    }

    public LocalTableSchema Schema => _layout.Schema;

    /// <summary>The table written to (a cache's writer finds its queue there).</summary>
    internal LocalFeatureTable Table => _table;

    /// <summary>Sees every version <see cref="BuildCompacted()"/> builds, installed or discarded: lets a test check that a
    /// discarded copy is not kept alive. Null outside tests.</summary>
    internal Action<TableVersion>? CompactedCopyBuilt { get; set; }

    /// <summary>True between <see cref="BeginReload"/> and <see cref="CompleteReload"/> or <see cref="AbortReload"/>.</summary>
    public bool IsReloading => _reloading;

    public RecordBuilder Begin(long key, long version)
    {
        RequireKeyType(EntityKeyType.Int64);
        _keyLength = 0;
        return BeginCore(key, version);
    }

    public RecordBuilder Begin(ReadOnlySpan<char> key, long version)
    {
        RequireKeyType(EntityKeyType.String);
        return BeginCore(SetKey(MemoryMarshal.AsBytes(key)), version);
    }

    public RecordBuilder Begin(in EntityKey key, long version) =>
        key.StringValue is { } s ? Begin(s.AsSpan(), version) : Begin(key.Int64Value, version);

    public RecordBuilder Begin(ReadOnlySpan<EntityKey> key, long version) => BeginCore(SetKey(key), version);

    public bool Delete(long key, long version)
    {
        RequireKeyType(EntityKeyType.Int64);
        _keyLength = 0;
        return DeleteCore(key, version);
    }

    public bool Delete(ReadOnlySpan<char> key, long version)
    {
        RequireKeyType(EntityKeyType.String);
        return DeleteCore(SetKey(MemoryMarshal.AsBytes(key)), version);
    }

    public bool Delete(in EntityKey key, long version) =>
        key.StringValue is { } s ? Delete(s.AsSpan(), version) : Delete(key.Int64Value, version);

    public bool Delete(ReadOnlySpan<EntityKey> key, long version) => DeleteCore(SetKey(key), version);

    public void Checkpoint(UpdatePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        Discard();
        if (_reloading)
        {
            throw new InvalidOperationException("A reload is in progress: its position is set by CompleteReload, not by Checkpoint.");
        }

        _table.SetPosition(position);
    }

    public void BeginReload()
    {
        Discard();
        if (_reloading)
        {
            throw new InvalidOperationException("A reload is already in progress; complete or abort it first.");
        }

        _version = NewVersion(expectedKeys: 0);
        _generation = _version.Generation;
        _autoCompactionDeclined = 0;
        _reloading = true;
        _reloadStarted = Stopwatch.GetTimestamp();
    }

    public void CompleteReload(UpdatePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        Discard();
        if (!_reloading)
        {
            throw new InvalidOperationException("No reload is in progress; call BeginReload first.");
        }

        // The data first, then the position it covers: the position never runs ahead of what readers can see.
        _table.Publish(_version);
        _table.SetPosition(position);
        _reloading = false;
        _table.CountLoad(Stopwatch.GetElapsedTime(_reloadStarted));
    }

    public void AbortReload()
    {
        Discard();
        if (_reloading)
        {
            _version = _table.Pin();
            _autoCompactionDeclined = 0;
            _reloading = false;
        }
    }

    /// <summary>Compacts now: copies the current record of every key (and every tombstone) into a new version and publishes it.</summary>
    /// <exception cref="LocalTableFullException">The compacted copy would not fit <c>max_bytes</c> (laying records out
    /// anew can leave more of the pages' tails empty); it is discarded and the table is unchanged.</exception>
    public void Compact()
    {
        Discard();
        var next = BuildCompacted();
        if (next.Bytes > _table.MaxBytes)
        {
            throw new LocalTableFullException(Schema, _table.MaxBytes, next.Bytes);
        }

        Install(next);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Record builder operations (called through RecordBuilder)
    // ---------------------------------------------------------------------------------------------------------------

    internal bool IsIgnored(int token)
    {
        CheckOpen(token);
        return _ignored;
    }

    internal void SetFixed<T>(int token, int field, StorageKind kind, T value)
        where T : unmanaged
    {
        CheckWrite(token, field, kind);
        Unsafe.WriteUnaligned(ref _fixed[_layout.Offsets[field]], value);
        MarkWritten(field, valid: true);
    }

    /// <summary>A <c>string</c> value. Kept as chars until commit; a string is created only for a value the dictionary does
    /// not hold yet.</summary>
    internal void SetString(int token, int field, ReadOnlySpan<char> value)
    {
        CheckOpen(token);
        CheckField(field);
        if (_layout.Kinds[field] != StorageKind.String)
        {
            throw KindMismatch(field, "SetString");
        }

        _openList = -1;
        _pendingStart[field] = AppendChars(value);
        _pendingCount[field] = value.Length;
        MarkWritten(field, valid: true);
    }

    internal void StartList(int token, int field)
    {
        CheckOpen(token);
        CheckField(field);
        if (_layout.Kinds[field] != StorageKind.List)
        {
            throw KindMismatch(field, "StartList");
        }

        _pending.Reserve(0, 8, out var start); // the items follow contiguously
        _pendingStart[field] = start;
        _pendingCount[field] = 0;
        _openList = field;
        MarkWritten(field, valid: true);
    }

    /// <summary>An item of the open list of fixed-size elements (bool, int32, int64, float32, float64), stored natively.</summary>
    internal void AddItem<T>(int token, int field, T value, string method)
        where T : unmanaged
    {
        CheckListItem(token, field, ColumnValues.ElementKind<T>(), method);
        MemoryMarshal.Write(_pending.Reserve(Unsafe.SizeOf<T>(), Unsafe.SizeOf<T>(), out _), in value);
        _pendingCount[field]++;
    }

    /// <summary>A whole list of fixed-size elements: <see cref="StartList"/> and one copy of the items.</summary>
    internal void SetList<T>(int token, int field, ReadOnlySpan<T> value, string method)
        where T : unmanaged
    {
        StartList(token, field);
        CheckListItem(token, field, ColumnValues.ElementKind<T>(), method);
        MemoryMarshal.AsBytes(value).CopyTo(_pending.Reserve(Unsafe.SizeOf<T>() * value.Length, Unsafe.SizeOf<T>(), out _));
        _pendingCount[field] = value.Length;
    }

    /// <summary>A <c>list&lt;string&gt;</c> item, kept as <c>[int32 length][UTF-16 chars]</c> until commit encodes it.</summary>
    internal void AddStringItem(int token, int field, ReadOnlySpan<char> item)
    {
        CheckListItem(token, field, StorageKind.String, "AddStringItem");
        var staged = _pending.Reserve(RecordLayout.Align(4 + (2 * item.Length), 4), 4, out _);
        var length = item.Length;
        MemoryMarshal.Write(staged, in length);
        item.CopyTo(MemoryMarshal.Cast<byte, char>(staged.Slice(4, 2 * item.Length)));
        _pendingCount[field]++;
    }

    internal void SetMissing(int token, int field)
    {
        CheckOpen(token);
        CheckField(field);
        _openList = -1;
        _pendingCount[field] = 0;
        MarkWritten(field, valid: false);
    }

    internal bool Commit(int token, LocalTableCache? cache = null)
    {
        CheckOpen(token);
        _open = false;
        _token++;
        _openList = -1;
        try
        {
            return !_ignored && CommitRecord(cache);
        }
        finally
        {
            ForgetProbe();
        }
    }

    private bool CommitRecord(LocalTableCache? cache)
    {
        var newKey = _oldPosition == 0;
        var size = RecordSize(OldRecord(out var oldOffset), oldOffset);
        newKey = EnsureBudget(size, newKey, strings: true, cache);
        ReprobeIfMoved(newKey);
        var oldPage = OldRecord(out oldOffset);

        // Unwritten fields keep their old values. String codes are copied as they are: the old
        // record is in this version, so its codes belong to this version's dictionary.
        if (oldPage is not null)
        {
            for (var f = 0; f < _layout.FieldCount; f++)
            {
                if (!Bits.Get(_written, f))
                {
                    oldPage.AsSpan(oldOffset + _layout.Offsets[f], _layout.Sizes[f]).CopyTo(_fixed.AsSpan(_layout.Offsets[f]));
                    SetValidity(f, (oldPage[oldOffset + RecordLayout.ValidityOffset + (f >> 3)] & (1 << (f & 7))) != 0);
                }
            }
        }

        for (var f = 0; f < _layout.FieldCount; f++)
        {
            if (_layout.Kinds[f] == StorageKind.String && Bits.Get(_written, f) && FixedValid(f))
            {
                // NewStringBytes may have looked this value up already, in this very version (nothing was added since).
                var chars = MemoryMarshal.Cast<byte, char>(_pending.Read(_pendingStart[f], 2 * _pendingCount[f]));
                var code = f == _lookedUpField
                    ? (_lookedUpCode >= 0 ? _lookedUpCode : _version.AddString(new string(chars)))
                    : Encode(chars);
                Unsafe.WriteUnaligned(ref _fixed[_layout.Offsets[f]], code);
            }
        }

        BinaryPrimitives.WriteInt64LittleEndian(_fixed.AsSpan(RecordLayout.VersionOffset), _recordVersion);
        BinaryPrimitives.WriteInt32LittleEndian(_fixed.AsSpan(RecordLayout.LengthOffset), size);
        _fixed[RecordLayout.FlagsOffset] = 0;

        var position = _version.Reserve(size, out var page);
        var offset = (int)position;
        _fixed.CopyTo(page.AsSpan(offset));
        var cursor = _layout.FixedSize;
        for (var v = 0; v < _layout.VarFieldCount; v++)
        {
            var f = _layout.VarFields[v];
            var refAt = offset + _layout.Offsets[f];
            if (!FixedValid(f))
            {
                BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(refAt), 0);
                continue;
            }

            var elementSize = _layout.ElementSizes[f];
            int count;
            if (Bits.Get(_written, f))
            {
                count = _pendingCount[f];
                if (_layout.Kinds[f] == StorageKind.List && _layout.IsCoded(f))
                {
                    EncodeStringItems(f, MemoryMarshal.Cast<byte, int>(page.AsSpan(offset + cursor, 4 * count)));
                }
                else
                {
                    _pending.Read(_pendingStart[f], count * elementSize).CopyTo(page.AsSpan(offset + cursor));
                }
            }
            else
            {
                var oldRefAt = oldOffset + _layout.Offsets[f];
                var oldRelative = BinaryPrimitives.ReadInt32LittleEndian(oldPage!.AsSpan(oldRefAt));
                count = BinaryPrimitives.ReadInt32LittleEndian(oldPage.AsSpan(oldRefAt + 4));
                oldPage.AsSpan(oldOffset + oldRelative, count * elementSize).CopyTo(page.AsSpan(offset + cursor));
            }

            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(refAt), cursor);
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(refAt + 4), count);
            cursor += RecordLayout.Align(count * elementSize, 8);
        }

        if (_layout.HashedKeys)
        {
            WriteKey(page, offset, cursor);
        }

        // Release: everything above (record bytes, a new page in the directory, new dictionary strings) is visible to a
        // reader before it can see this position.
        _version.Publish(_slot, _indexKey, position, newKey);
        _version.Reference(page, offset, +1); // before the old record lets go of the strings both hold
        Account(size, oldPage, newKey, tombstone: false);
        MaybeCompact();
        return true;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------------------------

    private RecordBuilder BeginCore(long indexKey, long version)
    {
        Discard();
        _token++;
        _open = true;
        _indexKey = indexKey;
        _recordVersion = version;
        Probe();
        _ignored = _oldPosition != 0 && ExistingVersion() >= version;
        Array.Clear(_fixed);
        Array.Clear(_written);
        _pending.Clear();
        _openList = -1;
        return new RecordBuilder(this, _token);
    }

    private bool DeleteCore(long indexKey, long version)
    {
        try
        {
            return DeleteProbed(indexKey, version);
        }
        finally
        {
            ForgetProbe();
        }
    }

    private bool DeleteProbed(long indexKey, long version)
    {
        Discard();
        _indexKey = indexKey;
        Probe();
        if (_oldPosition != 0 && ExistingVersion() >= version)
        {
            return false;
        }

        var newKey = _oldPosition == 0;
        var size = _layout.HeaderSize + (_layout.HashedKeys ? RecordLayout.Align(_keyLength, 8) : 0);
        EnsureBudget(size, newKey, strings: false);
        ReprobeIfMoved(newKey);
        var oldPage = OldRecord(out var oldOffset);

        var position = _version.Reserve(size, out var page);
        var offset = (int)position;
        var header = page.AsSpan(offset, _layout.HeaderSize);
        header.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(header, version);
        BinaryPrimitives.WriteInt32LittleEndian(header[RecordLayout.LengthOffset..], size);
        header[RecordLayout.FlagsOffset] = RecordLayout.TombstoneFlag;
        if (_layout.HashedKeys)
        {
            WriteKey(page, offset, _layout.HeaderSize);
        }

        _version.Publish(_slot, _indexKey, position, newKey);
        Account(size, oldPage, newKey, tombstone: true);
        MaybeCompact();
        return true;
    }

    /// <summary>Finds the key in the version being written: its position (0 = new key) and slot.</summary>
    private void Probe()
    {
        _oldPosition = _version.ProbeForWrite(_indexKey, _key.AsSpan(0, _keyLength), out _slot);
        _probedVersion = _version;
        _probedIndex = _version.Index;
    }

    /// <summary>
    /// The probe is for one write: once it is committed, discarded or done, the writer must not keep the version it
    /// probed, which a reload or compaction may have replaced since, alive with its pages, index and dictionary.
    /// </summary>
    private void ForgetProbe()
    {
        _probedVersion = null;
        _probedIndex = null;
    }

    /// <summary>
    /// Re-finds the key's slot when a compaction (new version) or an index growth happened since the probe, and grows the
    /// index first when the key is new. A cache eviction may have removed the key being replaced.
    /// </summary>
    private void ReprobeIfMoved(bool newKey)
    {
        if (newKey)
        {
            _version.EnsureIndexRoom();
        }

        if (!ReferenceEquals(_probedVersion, _version) || !ReferenceEquals(_probedIndex, _version.Index))
        {
            Probe();
        }
    }

    private long ExistingVersion() =>
        BinaryPrimitives.ReadInt64LittleEndian(_version.PageOf(_oldPosition).AsSpan((int)_oldPosition + RecordLayout.VersionOffset));

    /// <summary>The key's current record (not a tombstone) in the version being written, or null.</summary>
    private byte[]? OldRecord(out int offset)
    {
        offset = (int)_oldPosition;
        if (_oldPosition == 0)
        {
            return null;
        }

        var page = _version.PageOf(_oldPosition);
        return (page[offset + RecordLayout.FlagsOffset] & RecordLayout.TombstoneFlag) != 0 ? null : page;
    }

    private int RecordSize(byte[]? oldPage, int oldOffset)
    {
        long size = _layout.FixedSize;
        for (var v = 0; v < _layout.VarFieldCount; v++)
        {
            var f = _layout.VarFields[v];
            long count;
            if (Bits.Get(_written, f))
            {
                count = FixedValid(f) ? _pendingCount[f] : 0;
            }
            else if (oldPage is not null && (oldPage[oldOffset + RecordLayout.ValidityOffset + (f >> 3)] & (1 << (f & 7))) != 0)
            {
                count = BinaryPrimitives.ReadInt32LittleEndian(oldPage.AsSpan(oldOffset + _layout.Offsets[f] + 4));
            }
            else
            {
                count = 0;
            }

            size += RecordLayout.Align(count * _layout.ElementSizes[f], 8);
        }

        if (_layout.HashedKeys)
        {
            size += RecordLayout.Align((long)_keyLength, 8);
        }

        if (size > int.MaxValue / 2)
        {
            throw new InvalidOperationException($"A record of {size} bytes is too large for a local table.");
        }

        return (int)size;
    }

    /// <summary>
    /// Enforces <c>max_bytes</c>: the table's size after this write (a new page, index growth, new strings) must fit.
    /// If needed, compaction reclaims superseded records, then a cache write can also evict live keys. Each candidate
    /// is built aside and installed only if it leaves room for the pending write.
    /// </summary>
    /// <returns>Whether the key needs a new index slot in the version that receives the write.</returns>
    private bool EnsureBudget(int recordBytes, bool newKey, bool strings, LocalTableCache? cache = null)
    {
        var required = Required(_version, recordBytes, newKey, strings, out var newStringObjects);
        if (required <= _table.MaxBytes)
        {
            return newKey;
        }

        // An empty table needs at least the record and its distinct strings. Page and dictionary growth would
        // overstate that lower bound: a cache eviction can recover a whole page or shrink a nearly full dictionary.
        if (cache is not null && recordBytes + newStringObjects > _table.MaxBytes)
        {
            throw new LocalTableFullException(Schema, _table.MaxBytes, required);
        }

        var packedBytes = _version.Bytes;
        if (_version.SupersededBytes > 0 && TryCompactForWrite(recordBytes, newKey, strings, ref required, ref packedBytes))
        {
            return newKey;
        }

        if (cache is not null)
        {
            // Plan the eviction while the fully decoded write is still staged. A copy that cannot admit it leaves
            // the old cache and its metrics intact, including the record being replaced.
            // Victims release records and unshared strings. Charging a whole index doubling or the next append page
            // against those bytes can evict every small record, even though rebuilding avoids that growth.
            var avoidableGrowth = _version.IndexGrowthBytes(newKey) + Math.Max(0, _version.PageGrowthBytes(recordBytes) - recordBytes);
            var toFree = BytesToFree(packedBytes, required - avoidableGrowth - _table.MaxBytes);
            var deadBefore = cache.NowMs() - cache.Policy.RetentionMs;
            var compacted = BuildCompacted(deadBefore, toFree, _oldPosition, out var expired, out var evicted);
            newKey = newKey || compacted.ProbeForWrite(_indexKey, _key.AsSpan(0, _keyLength), out _) == 0;
            var afterEviction = Required(compacted, recordBytes, newKey, strings, out _);
            if (afterEviction <= _table.MaxBytes)
            {
                Install(compacted);
                cache.CountEvictions(expired, evicted);
                return newKey;
            }

            required = Math.Min(required, afterEviction);
        }

        throw new LocalTableFullException(Schema, _table.MaxBytes, required);
    }

    /// <summary>
    /// What a cache eviction is to free, counted as its victims are: by the payload they release (records and unshared
    /// strings). It aims for three quarters of <c>max_bytes</c>, and frees at least what the pending write lacks.
    /// </summary>
    /// <param name="packedBytes">The table's size without its superseded records.</param>
    /// <param name="lacking">What the table with the pending write would be over <c>max_bytes</c>.</param>
    private long BytesToFree(long packedBytes, long lacking)
    {
        var max = _table.MaxBytes;
        var target = max / 4 * 3;
        var livePayload = _version.RecordBytes - _version.SupersededBytes + _version.StringBytes - _version.UnreferencedStringBytes;
        if (packedBytes <= max)
        {
            return Math.Max(1, Math.Max(Math.Min(packedBytes - target, livePayload / 4), lacking));
        }

        // The limit was lowered under what the table holds (LocalFeatureTable.Retune). What it is over is of its whole
        // size (pages, index, dictionary), of which the payload is a part: the share of the payload to free is the
        // share of the size to lose. Taken as payload, the excess evicted every record of a table of small ones, where
        // a table created with the lower limit keeps most of them.
        var share = (double)(packedBytes - target) / packedBytes;
        return Math.Max(1, Math.Max((long)(livePayload * share), lacking - (packedBytes - max)));
    }

    /// <summary>
    /// Brings a cache's table under a limit that was lowered under what it holds (<see cref="LocalFeatureTable.Retune"/>),
    /// with no write pending: it evicts as a write under pressure does. An estimate that falls short (an index that does
    /// not get smaller with its keys) frees half of what is left, and a limit no record fits under leaves the table
    /// empty, as a table created with that limit stays. One comparison for a table within its limit.
    /// </summary>
    internal void ShrinkToLimit(LocalTableCache cache)
    {
        var max = _table.MaxBytes;
        if (_version.Bytes <= max)
        {
            return;
        }

        Discard();
        var livePayload = _version.RecordBytes - _version.SupersededBytes + _version.StringBytes - _version.UnreferencedStringBytes;
        var deadBefore = cache.NowMs() - cache.Policy.RetentionMs;
        var toFree = BytesToFree(_version.Bytes, 0);
        for (var attempt = 0; ; attempt++)
        {
            var compacted = BuildCompacted(deadBefore, toFree, 0, out var expired, out var evicted);
            if (compacted.Bytes <= max || toFree == long.MaxValue)
            {
                Install(compacted);
                cache.CountEvictions(expired, evicted);
                return;
            }

            toFree = attempt < 4 && toFree < livePayload ? toFree + ((livePayload - toFree + 1) / 2) : long.MaxValue;
        }
    }

    // A rejected copy can be as large as the original. End its stack lifetime before building an eviction candidate.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryCompactForWrite(int recordBytes, bool newKey, bool strings, ref long required, ref long packedBytes)
    {
        var compacted = BuildCompacted();
        // Its pages are packed and its dictionary now contains only strings still in use.
        var afterCompaction = Required(compacted, recordBytes, newKey, strings, out _);
        if (afterCompaction <= _table.MaxBytes)
        {
            Install(compacted);
            return true;
        }

        required = Math.Min(required, afterCompaction);
        packedBytes = Math.Min(packedBytes, compacted.Bytes); // superseded data need not be freed a second time by evicting live keys
        return false;
    }

    /// <summary>The size <paramref name="version"/> would have after this write.</summary>
    private long Required(TableVersion version, int recordBytes, bool newKey, bool strings, out long newStringObjects)
    {
        newStringObjects = 0;
        var dictionary = strings ? NewStringBytes(version, out newStringObjects) : 0;
        return version.Bytes + version.PageGrowthBytes(recordBytes) + version.IndexGrowthBytes(newKey) + dictionary;
    }

    /// <summary>
    /// What the record's strings add to the dictionary of the version being written: each distinct value of its written
    /// <c>string</c> fields and <c>list&lt;string&gt;</c> items that the dictionary does not hold yet, once however often
    /// the record repeats it, plus the dictionary growths that adding them passes. This is what <see cref="Encode"/> then
    /// does at commit (fields kept from the old record already have codes). Allocates only when a record brings more
    /// strings than any record before it.
    /// </summary>
    private long NewStringBytes(TableVersion version, out long objectBytes)
    {
        objectBytes = 0;
        _lookedUpField = -1;
        var fields = _layout.DictionaryFields;
        var candidates = 0L;
        var single = -1;
        foreach (var f in fields)
        {
            if (Bits.Get(_written, f) && FixedValid(f))
            {
                candidates += _layout.Kinds[f] == StorageKind.String ? 1 : _pendingCount[f];
                single = f;
            }
        }

        if (candidates == 0)
        {
            return 0;
        }

        if (candidates == 1 && _layout.Kinds[single] == StorageKind.String)
        {
            // The common case, one string value: nothing to repeat, so no set; the lookup is kept for Encode.
            var chars = MemoryMarshal.Cast<byte, char>(_pending.Read(_pendingStart[single], 2 * _pendingCount[single]));
            _lookedUpCode = version.FindString(chars);
            _lookedUpField = single;
            if (_lookedUpCode >= 0)
            {
                return 0;
            }

            objectBytes = TableVersion.StringObjectBytes(chars.Length);
            return version.StringAdditionBytes(1, objectBytes);
        }

        // An open-addressing set of the new strings found so far, as (pending offset + 1) << 32 | length; at most half full.
        var size = (int)BitOperations.RoundUpToPowerOf2((ulong)Math.Max(16, 2 * candidates));
        if (_newStrings.Length < size)
        {
            _newStrings = new long[size];
        }

        var set = _newStrings.AsSpan(0, size);
        set.Clear();
        var count = 0;
        foreach (var f in fields)
        {
            if (!Bits.Get(_written, f) || !FixedValid(f))
            {
                continue;
            }

            if (_layout.Kinds[f] == StorageKind.String)
            {
                CountNewString(version, set, _pendingStart[f], _pendingCount[f], ref count, ref objectBytes);
            }
            else
            {
                // A list<string>: its staged items.
                var at = _pendingStart[f];
                for (var i = 0; i < _pendingCount[f]; i++)
                {
                    var length = MemoryMarshal.Read<int>(_pending.Read(at, 4));
                    CountNewString(version, set, at + 4, length, ref count, ref objectBytes);
                    at += RecordLayout.Align(4 + (2 * length), 4);
                }
            }
        }

        return version.StringAdditionBytes(count, objectBytes);
    }

    private void CountNewString(TableVersion version, Span<long> set, int offset, int length, ref int count, ref long objectBytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(_pending.Read(offset, 2 * length));
        if (version.FindString(chars) >= 0)
        {
            return;
        }

        var mask = set.Length - 1;
        for (var slot = string.GetHashCode(chars) & mask; ; slot = (slot + 1) & mask)
        {
            var entry = set[slot];
            if (entry == 0)
            {
                set[slot] = ((long)(offset + 1) << 32) | (uint)length;
                count++;
                objectBytes += TableVersion.StringObjectBytes(length);
                return;
            }

            var seenLength = (int)(uint)entry;
            if (seenLength == length
                && MemoryMarshal.Cast<byte, char>(_pending.Read((int)(entry >> 32) - 1, 2 * seenLength)).SequenceEqual(chars))
            {
                return; // repeated within the record: one dictionary string
            }
        }
    }

    /// <summary>
    /// The automatic compaction: when what a compaction would free, superseded records and the
    /// dictionary strings only they referenced, is more than half of the records and strings. Strings count because a string
    /// costs its record only a 4-byte code: a field rewritten with long new values (a description) supersedes small records
    /// and leaves its old values in the dictionary, which the records alone would not show. It runs after a write that
    /// already succeeded, so a compacted copy that would not fit <c>max_bytes</c> is simply discarded; it is tried again only
    /// once twice as many bytes could be freed, not after every write.
    /// </summary>
    private void MaybeCompact()
    {
        var freeable = _version.SupersededBytes + _version.UnreferencedStringBytes;
        if (freeable > 0 && freeable >= _options.CompactionMinBytes && freeable * 2 > _version.RecordBytes + _version.StringBytes
            && freeable >= 2 * _autoCompactionDeclined)
        {
            var next = BuildCompacted();
            if (next.Bytes <= _table.MaxBytes)
            {
                Install(next);
            }
            else
            {
                _autoCompactionDeclined = freeable;
            }
        }
    }

    /// <summary>
    /// Builds an unpublished version holding the surviving records and tombstones. A caller that finds the result
    /// too large just drops it; <see cref="Install"/> makes it current.
    /// </summary>
    /// <remarks>
    /// Physical order is insertion order, including across sealed pages shared by both versions. Packing the other
    /// records in this order preserves the eviction hand and avoids mixing sizes in index-slot order. String codes
    /// are remapped into a dictionary containing only live strings. Tombstones prevent a late update from reviving
    /// a deleted key.
    /// </remarks>
    private TableVersion BuildCompacted() => BuildCompacted(long.MinValue, 0, 0, out _, out _);

    /// <summary>
    /// The build of a cache write that needs room (<paramref name="bytesToFree"/> above zero): it also leaves out the
    /// records written before <paramref name="dropWrittenBefore"/>, the record at <paramref name="replacedPosition"/>
    /// and the victims that free the rest (<see cref="SelectSurvivors"/>).
    /// </summary>
    private TableVersion BuildCompacted(long dropWrittenBefore, long bytesToFree, long replacedPosition, out int expired, out int evicted)
    {
        var old = _version;
        var index = old.Index;
        var evicting = bytesToFree > 0;
        // Eviction leaves most pages nearly whole, and copying them is most of its cost: without sharing them a write
        // under capacity pressure takes about 70% longer and allocates 2.5 times as much (CacheWriteBenchmarks). A shared
        // page keeps its string codes, which belong to the old dictionary, so only a table without strings shares.
        var sharing = evicting && old.StringCount == 0;
        var slots = ArrayPool<int>.Shared.Rent(index.Count);
        var codeMap = ArrayPool<int>.Shared.Rent(old.StringCount);
        var droppedReferences = evicting ? ArrayPool<int>.Shared.Rent(old.StringCount) : null;
        var pagePlan = sharing ? ArrayPool<long>.Shared.Rent(index.Count) : null;
        try
        {
            var kept = slots.AsSpan(0, index.Count);
            var live = 0;
            for (var s = 0; s < index.SlotCount; s++)
            {
                if (index.Entries[(2 * s) + 1] != 0)
                {
                    kept[live++] = s;
                }
            }

            kept = kept[..live];
            OrderByPosition(kept);
            var liveBytes = old.RecordBytes - old.SupersededBytes;
            var hand = old.CacheHand;
            expired = evicted = 0;
            if (evicting)
            {
                live = SelectSurvivors(kept, droppedReferences!, dropWrittenBefore, bytesToFree, replacedPosition, ref liveBytes, ref hand, out expired, out evicted);
                kept = kept[..live];
            }

            var next = NewVersion(live);
            next.AddRecordBytes(liveBytes);
            var plan = pagePlan.AsSpan(0, sharing ? live : 0);
            if (sharing)
            {
                PlanPages(kept, plan);
            }

            codeMap.AsSpan(0, old.StringCount).Fill(-1);
            var toPack = liveBytes; // the bytes still to copy into the pages being laid out: each is sized for what is to come
            int sharedEnd = 0, tombstones = 0;
            long sharedPage = 0;
            for (var i = 0; i < live; i++)
            {
                var entry = 2 * kept[i];
                var position = index.Entries[entry + 1];
                var page = old.PageOf(position);
                var offset = (int)position;
                if (sharing && plan[i] < 0)
                {
                    sharedPage = next.AttachSealedPage(page);
                    sharedEnd = i - (int)plan[i];
                }
                else if (sharing && plan[i] > 0)
                {
                    toPack = plan[i];
                }

                var tombstone = (page[offset + RecordLayout.FlagsOffset] & RecordLayout.TombstoneFlag) != 0;
                long relocated;
                if (i < sharedEnd)
                {
                    relocated = sharedPage | (uint)offset;
                }
                else
                {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(offset + RecordLayout.LengthOffset));
                    relocated = next.Reserve(length, out var copy, packFor: toPack);
                    toPack -= length;
                    var copyOffset = (int)relocated;
                    page.AsSpan(offset, length).CopyTo(copy.AsSpan(copyOffset));
                    if (!tombstone)
                    {
                        RemapStrings(copy, copyOffset, next, old.Strings, codeMap);
                        next.Reference(copy, copyOffset, +1);
                    }
                }

                if (hand != 0 && position >= hand)
                {
                    next.CacheHand = relocated;
                    hand = 0;
                }

                next.Index.InsertUnpublished(index.Entries[entry], relocated);
                tombstones += tombstone ? 1 : 0;
            }

            next.AddRecords(live - tombstones, tombstones);
            CompactedCopyBuilt?.Invoke(next);
            return next;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(slots);
            ArrayPool<int>.Shared.Return(codeMap);
            if (droppedReferences is not null)
            {
                ArrayPool<int>.Shared.Return(droppedReferences);
            }

            if (pagePlan is not null)
            {
                ArrayPool<long>.Shared.Return(pagePlan);
            }
        }
    }

    /// <summary>
    /// Decides, page by page, whether the survivors of a page are copied or the page is shared with the new version as it
    /// is (at least 15/16 of it still live), in <paramref name="plan"/>: at the first survivor of a shared page, minus
    /// the number of its survivors; at the first survivor of a run of pages to copy, the bytes of the run, which size the
    /// pages it is packed into; zero elsewhere. Either way the survivors stay in insertion order.
    /// </summary>
    private void PlanPages(ReadOnlySpan<int> slots, Span<long> plan)
    {
        var entries = _version.Index.Entries;
        plan.Clear();
        var runStart = 0;
        for (var first = 0; first < slots.Length;)
        {
            var position = entries[(2 * slots[first]) + 1];
            var page = _version.PageOf(position);
            var number = position >> 32;
            var end = first;
            long liveBytes = 0;
            for (; end < slots.Length; end++)
            {
                position = entries[(2 * slots[end]) + 1];
                if (position >> 32 != number)
                {
                    break;
                }

                liveBytes += BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan((int)position + RecordLayout.LengthOffset));
            }

            if (liveBytes * 16 < (long)page.Length * 15)
            {
                plan[runStart] += liveBytes;
            }
            else
            {
                plan[first] = first - end;
                runStart = end;
            }

            first = end;
        }
    }

    /// <summary>Sorts the slots by the position of their records: page order, which is insertion order.</summary>
    private void OrderByPosition(Span<int> slots)
    {
        var scratch = ArrayPool<long>.Shared.Rent(slots.Length);
        try
        {
            var positions = scratch.AsSpan(0, slots.Length);
            if (slots.Length >= 128 && slots.Length <= int.MaxValue / 2 && TryOrderDensePages(slots, MemoryMarshal.Cast<long, int>(positions)))
            {
                return;
            }

            var entries = _version.Index.Entries;
            for (var i = 0; i < slots.Length; i++)
            {
                positions[i] = entries[(2 * slots[i]) + 1];
            }

            positions.Sort(slots);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(scratch);
        }
    }

    // Records of similar width often occupy consecutive cells of their pages. If each occupied cell holds just
    // one record start, scanning the cells yields exact page order in linear time. Sparse pages or a collision
    // fall back to sorting, which is what this saves: without it a write under capacity pressure takes about 70% longer
    // (CacheWriteBenchmarks).
    private bool TryOrderDensePages(Span<int> slots, Span<int> scratch)
    {
        var version = _version;
        if (version.PageCount >= scratch.Length)
        {
            return false;
        }

        var entries = version.Index.Entries;
        var first = entries[(2 * slots[0]) + 1];
        var width = BinaryPrimitives.ReadInt32LittleEndian(version.PageOf(first).AsSpan((int)first + RecordLayout.LengthOffset));
        var starts = scratch[..version.PageCount];
        var cells = scratch[version.PageCount..];
        var total = 0;
        for (var p = 0; p < starts.Length; p++)
        {
            starts[p] = total;
            var count = 1 + ((version.Pages[p].Length - 1) / width);
            if (count > cells.Length - total)
            {
                return false;
            }

            total += count;
        }

        cells = cells[..total];
        cells.Clear();
        foreach (var slot in slots)
        {
            var position = entries[(2 * slot) + 1];
            ref var cell = ref cells[starts[(int)(position >> 32) - 1] + ((int)position / width)];
            if (cell != 0)
            {
                return false;
            }

            cell = slot + 1;
        }

        var i = 0;
        foreach (var cell in cells)
        {
            if (cell != 0)
            {
                slots[i++] = cell - 1;
            }
        }

        return true;
    }

    /// <summary>
    /// Leaves the survivors' slots at the start of the span. Drops expired values, then evicts from the hand in
    /// insertion order until the payload charge reaches the byte target: SIEVE (Zhang et al., "SIEVE is Simpler than
    /// LRU", NSDI 2024), where a record read since the hand last passed it loses its mark and stays one more round.
    /// A full cache write replaces every field of its previous record, whose bytes can therefore make room before any
    /// other key is evicted. A timestamp can cover a whole batch, so victims are selected individually. A shared string
    /// frees space only when the last record referencing it goes.
    /// </summary>
    private int SelectSurvivors(
        Span<int> slots, int[] droppedReferences, long dropWrittenBefore, long bytesToFree, long replacedPosition,
        ref long liveBytes, ref long hand, out int expired, out int evicted)
    {
        var old = _version;
        var entries = old.Index.Entries;
        expired = evicted = 0;
        droppedReferences.AsSpan(0, old.StringCount).Clear();
        var candidates = 0;
        var cursor = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            var position = entries[(2 * slot) + 1];
            var page = old.PageOf(position);
            var offset = (int)position;
            var time = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(offset + RecordLayout.VersionOffset));
            if ((position == replacedPosition || time < dropWrittenBefore) && (page[offset + RecordLayout.FlagsOffset] & RecordLayout.TombstoneFlag) == 0)
            {
                if (position != replacedPosition)
                {
                    expired++;
                }
                liveBytes -= BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(offset + RecordLayout.LengthOffset));
                bytesToFree -= old.Drop(page, offset, droppedReferences);
                continue;
            }

            if (position < hand)
            {
                cursor++;
            }

            slots[candidates++] = slot;
        }

        // Positions are FIFO insertion order, kept across compactions. An append replacing a record is a new arrival.
        // Advance the hand only when pressure actually scans; untouched survivors keep their visited bits.
        if (cursor == candidates)
        {
            cursor = 0;
        }

        for (var pass = 0; pass < 2 && bytesToFree > 0; pass++)
        {
            for (var walked = 0; walked < candidates && bytesToFree > 0; walked++)
            {
                var at = cursor++;
                if (cursor == candidates)
                {
                    cursor = 0;
                }

                var slot = slots[at];
                if (slot < 0)
                {
                    continue;
                }

                var position = entries[(2 * slot) + 1];
                var page = old.PageOf(position);
                var offset = (int)position;
                ref var flags = ref page[offset + RecordLayout.FlagsOffset];
                var seen = Volatile.Read(ref flags);
                if ((seen & RecordLayout.TombstoneFlag) != 0)
                {
                    continue;
                }

                // Bound the scan even if readers keep setting visited. A rejected candidate may consume a hint.
                if (pass == 0 && (seen & RecordLayout.CacheHitFlag) != 0)
                {
                    Volatile.Write(ref flags, (byte)(seen & ~RecordLayout.CacheHitFlag));
                    continue;
                }

                bytesToFree -= old.Drop(page, offset, droppedReferences);
                liveBytes -= BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(offset + RecordLayout.LengthOffset));
                slots[at] = -1;
                hand = position + 1; // the copy maps the next newer survivor, or leaves zero to wrap
                evicted++;
            }
        }

        var kept = 0;
        for (var i = 0; i < candidates; i++)
        {
            if (slots[i] >= 0)
            {
                slots[kept++] = slots[i];
            }
        }

        return kept;
    }

    /// <summary>Makes a version from <see cref="BuildCompacted()"/> the one written to, and current unless a reload is
    /// building it.</summary>
    private void Install(TableVersion next)
    {
        _version = next;
        _generation = next.Generation;
        _autoCompactionDeclined = 0;
        if (!_reloading)
        {
            // Release: the new version's pages, index and dictionary are complete before a reader can pin it.
            _table.Publish(next);
        }

        _table.CountCompaction();
    }

    private void RemapStrings(byte[] page, int offset, TableVersion next, string[] strings, int[] codeMap)
    {
        foreach (var f in _layout.DictionaryFields)
        {
            var codes = _layout.Codes(page, offset, f);
            for (var i = 0; i < codes.Length; i++)
            {
                ref var mapped = ref codeMap[codes[i]];
                if (mapped < 0)
                {
                    mapped = next.AddString(strings[codes[i]]);
                }

                codes[i] = mapped;
            }
        }
    }

    private TableVersion NewVersion(int expectedKeys) =>
        new(_layout, _version.Hasher, _options.PageSize, _generation + 1, expectedKeys);

    /// <summary>Statistics of a published write: its bytes, the bytes of the record or tombstone it replaced (and the
    /// string references the replaced record let go of), and counts.</summary>
    private void Account(int size, byte[]? oldPage, bool newKey, bool tombstone)
    {
        _version.AddRecordBytes(size);
        if (!newKey)
        {
            var replaced = _version.PageOf(_oldPosition);
            _version.AddSuperseded(BinaryPrimitives.ReadInt32LittleEndian(replaced.AsSpan((int)_oldPosition + RecordLayout.LengthOffset)));
            _version.Reference(replaced, (int)_oldPosition, -1); // a tombstone references nothing
        }

        var wasRecord = oldPage is not null;
        var wasTombstone = !newKey && !wasRecord;
        var records = (tombstone ? 0 : 1) - (wasRecord ? 1 : 0);
        var tombstones = (tombstone ? 1 : 0) - (wasTombstone ? 1 : 0);
        _version.AddRecords(records, tombstones);
    }

    private void WriteKey(byte[] page, int offset, int cursor)
    {
        var refAt = offset + _layout.KeyRefOffset;
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(refAt), cursor);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(refAt + 4), _keyLength);
        _key.AsSpan(0, _keyLength).CopyTo(page.AsSpan(offset + cursor, _keyLength));
    }

    private int Encode(ReadOnlySpan<char> value)
    {
        var code = _version.FindString(value);
        return code >= 0 ? code : _version.AddString(new string(value));
    }

    private void EncodeStringItems(int field, Span<int> codes)
    {
        var at = _pendingStart[field];
        for (var i = 0; i < codes.Length; i++)
        {
            var length = MemoryMarshal.Read<int>(_pending.Read(at, 4));
            codes[i] = Encode(MemoryMarshal.Cast<byte, char>(_pending.Read(at + 4, 2 * length)));
            at += RecordLayout.Align(4 + (2 * length), 4);
        }
    }

    /// <summary>Holds a hashed key's canonical bytes for the record being written; returns their hash, the index key.</summary>
    private long SetKey(ReadOnlySpan<byte> key)
    {
        EnsureKeyRoom(key.Length);
        key.CopyTo(_key);
        _keyLength = key.Length;
        return _version.Hash(key);
    }

    /// <summary>
    /// Holds a key given by its parts: its canonical bytes for a hashed key, nothing for a one-part int64 key. Returns the
    /// index key (the int64 itself, or the bytes' hash).
    /// </summary>
    /// <exception cref="ArgumentException">The parts are not a key of the table's layout: wrong count or a part's type.</exception>
    private long SetKey(ReadOnlySpan<EntityKey> key)
    {
        var layout = Schema.Key;
        if (TableKeys.Check(layout, key) is { } problem)
        {
            throw new ArgumentException($"Local table {Schema.Source}/{Schema.Entity}: {problem}.", nameof(key));
        }

        if (!_layout.HashedKeys)
        {
            _keyLength = 0;
            return key[0].Int64Value;
        }

        EnsureKeyRoom(TableKeys.Length(key));
        _keyLength = TableKeys.Encode(key, _key);
        return _version.Hash(_key.AsSpan(0, _keyLength));
    }

    private void EnsureKeyRoom(int length)
    {
        if (length > _key.Length)
        {
            _key = new byte[Math.Max(length, _key.Length * 2)];
        }
    }

    private int AppendChars(ReadOnlySpan<char> value)
    {
        value.CopyTo(MemoryMarshal.Cast<byte, char>(_pending.Reserve(2 * value.Length, 2, out var at)));
        return at;
    }

    private void MarkWritten(int field, bool valid)
    {
        Bits.Set(_written, field);
        SetValidity(field, valid);
    }

    private void SetValidity(int field, bool valid)
    {
        ref var b = ref _fixed[RecordLayout.ValidityOffset + (field >> 3)];
        b = valid ? (byte)(b | (1 << (field & 7))) : (byte)(b & ~(1 << (field & 7)));
    }

    private bool FixedValid(int field) => (_fixed[RecordLayout.ValidityOffset + (field >> 3)] & (1 << (field & 7))) != 0;

    internal void Discard()
    {
        if (_open)
        {
            _open = false;
            _token++;
        }

        ForgetProbe();
    }

    private void CheckOpen(int token)
    {
        if (!_open || token != _token)
        {
            throw new InvalidOperationException(
                "This record builder is no longer open: it was committed, or discarded by a later Begin, Delete, Checkpoint, reload or Compact call.");
        }
    }

    private void CheckWrite(int token, int field, StorageKind kind)
    {
        CheckOpen(token);
        CheckField(field);
        if (_layout.Kinds[field] != kind)
        {
            throw KindMismatch(field, "a " + kind + " setter");
        }

        _openList = -1;
    }

    private void CheckListItem(int token, int field, StorageKind element, string method)
    {
        CheckOpen(token);
        CheckField(field);
        if (_layout.Kinds[field] != StorageKind.List || _layout.Elements[field] != element)
        {
            throw KindMismatch(field, method);
        }

        if (_openList != field)
        {
            throw new InvalidOperationException(
                $"{method}: list field '{Schema.Fields[field].Name}' is not open. Call StartList first, and add a list's items before setting another field.");
        }
    }

    private void CheckField(int field)
    {
        if ((uint)field >= (uint)_layout.FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, $"The table has {_layout.FieldCount} fields.");
        }
    }

    private InvalidOperationException KindMismatch(int field, string method)
    {
        var type = Schema.Fields[field].Type;
        var setter = type.Kind switch
        {
            StorageKind.Bool => "SetBool",
            StorageKind.Int32 => "SetInt32",
            StorageKind.Int64 => "SetInt64",
            StorageKind.Float32 => "SetFloat32",
            StorageKind.Float64 => "SetFloat64",
            StorageKind.String => "SetString",
            _ => type.Element switch
            {
                StorageKind.Bool => "SetBoolList or AddBoolItem",
                StorageKind.Int32 => "SetInt32List or AddInt32Item",
                StorageKind.Int64 => "SetInt64List or AddInt64Item",
                StorageKind.Float32 => "SetFloat32List or AddFloat32Item",
                StorageKind.Float64 => "SetFloat64List or AddFloat64Item",
                _ => "SetStringList or AddStringItem",
            },
        };
        return new InvalidOperationException($"Field {field} ('{Schema.Fields[field].Name}') is {type}; {method} does not match it (use {setter}).");
    }

    private void RequireKeyType(EntityKeyType keyType)
    {
        if (Schema.KeyType != keyType)
        {
            throw new InvalidOperationException(
                $"Local table {Schema.Source}/{Schema.Entity} is keyed by {Schema.Key.TypeText}, not {EntityKeyLayout.TypeName(keyType)}" +
                (Schema.Key.PartCount > 1 ? "; write a key of several parts with Begin/Delete(ReadOnlySpan<EntityKey>, long)." : "."));
        }
    }

    /// <summary>
    /// Where the record being built stages its variable-length values until commit: the chars of <c>string</c> values and
    /// the elements of lists. Its cursor moves only in <see cref="Reserve"/>, which aligns it,
    /// makes room (growing, and so replacing, the array) and returns the reserved bytes. Callers write through that span
    /// and read back by offset, so none can hold the array across a growth or take an offset past its end.
    /// </summary>
    private sealed class PendingBuffer
    {
        private byte[] _bytes = new byte[256];
        private int _used;

        /// <summary>Starts a new record: what was staged before is dropped.</summary>
        public void Clear() => _used = 0;

        /// <summary>
        /// <paramref name="bytes"/> bytes at the next multiple of <paramref name="alignment"/> (a power of two), starting
        /// at <paramref name="offset"/>. Room is made even for 0 bytes, so the offset of an empty value is inside the array.
        /// Reservations of one list are contiguous, since each element's size is a multiple of its alignment.
        /// </summary>
        public Span<byte> Reserve(int bytes, int alignment, out int offset)
        {
            var start = RecordLayout.Align(_used, alignment);
            var end = (long)start + bytes;
            if (end > _bytes.Length)
            {
                if (end > Array.MaxLength)
                {
                    throw new InvalidOperationException($"A record's variable-length values take more than {Array.MaxLength} bytes.");
                }

                Array.Resize(ref _bytes, (int)Math.Min(Array.MaxLength, Math.Max(2L * _bytes.Length, end)));
            }

            _used = (int)end;
            offset = start;
            return _bytes.AsSpan(start, bytes);
        }

        public ReadOnlySpan<byte> Read(int offset, int length) => _bytes.AsSpan(offset, length);
    }
}

/// <summary>
/// One update being built: the source decodes each field straight into it, then commits. Unwritten fields
/// keep the key's old values. A value-type handle, so building a record allocates nothing.
/// </summary>
/// <remarks>
/// <para>Field indices are the schema's (<see cref="LocalTableSchema.FieldIndex"/>), resolved once per source, not per
/// message. A setter of the wrong type for the field throws and names the right one.</para>
/// <para>When the table already has a record of the key with this version or a newer one, the builder is
/// <see cref="IsIgnored"/>: setters still accept values (a source need not check), and <see cref="Commit"/> returns false.</para>
/// <para>The builder is valid until it is committed or until the writer's next <c>Begin</c>, <c>Delete</c>,
/// <c>Checkpoint</c> or reload call, which discards it; using it afterwards throws.</para>
/// </remarks>
public readonly struct RecordBuilder
{
    private readonly LocalTableWriter? _writer;
    private readonly int _token;

    internal RecordBuilder(LocalTableWriter writer, int token)
    {
        _writer = writer;
        _token = token;
    }

    /// <summary>True when the update is stale (its version is not greater than the key's current one) and will not be written.</summary>
    public bool IsIgnored => Writer.IsIgnored(_token);

    public void SetBool(int field, bool value) => Writer.SetFixed(_token, field, StorageKind.Bool, value);

    public void SetInt32(int field, int value) => Writer.SetFixed(_token, field, StorageKind.Int32, value);

    public void SetInt64(int field, long value) => Writer.SetFixed(_token, field, StorageKind.Int64, value);

    /// <summary>A <c>float32</c> value, stored as given: NaN and ±∞ are kept, and the read path replaces them like backfill does.</summary>
    public void SetFloat32(int field, float value) => Writer.SetFixed(_token, field, StorageKind.Float32, value);

    public void SetFloat64(int field, double value) => Writer.SetFixed(_token, field, StorageKind.Float64, value);

    /// <summary>
    /// A <c>string</c> value, stored as a code into the table's string dictionary: a value the dictionary holds is not
    /// stored again, and every read hands out the dictionary's instance, so a fill allocates no string, whatever the length.
    /// </summary>
    public void SetString(int field, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Writer.SetString(_token, field, value.AsSpan());
    }

    /// <summary>A <c>string</c> value from chars, e.g. decoded into a reused buffer: no string is made for a value the
    /// dictionary already holds.</summary>
    public void SetString(int field, ReadOnlySpan<char> value) => Writer.SetString(_token, field, value);

    /// <summary>A <c>list&lt;bool&gt;</c> value.</summary>
    /// <remarks>One setter per element type, as for scalars: the field's declared element type decides, and a setter of
    /// another element type throws and names the right one. A fixed-length field (<c>list&lt;float32,64&gt;</c>) is set
    /// the same way; a list of another length is stored as given, and a read replaces it like a request's other writers
    /// do, so one bad record of a load does not stop the load.</remarks>
    public void SetBoolList(int field, ReadOnlySpan<bool> value) => Writer.SetList(_token, field, value, nameof(SetBoolList));

    /// <summary>A <c>list&lt;int32&gt;</c> value.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    public void SetInt32List(int field, ReadOnlySpan<int> value) => Writer.SetList(_token, field, value, nameof(SetInt32List));

    /// <summary>A <c>list&lt;int64&gt;</c> value.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    public void SetInt64List(int field, ReadOnlySpan<long> value) => Writer.SetList(_token, field, value, nameof(SetInt64List));

    /// <summary>A <c>list&lt;float32&gt;</c> value, stored as given: a list holding NaN or ±∞ is kept, and the read path
    /// replaces it like backfill does.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    public void SetFloat32List(int field, ReadOnlySpan<float> value) => Writer.SetList(_token, field, value, nameof(SetFloat32List));

    /// <summary>A <c>list&lt;float64&gt;</c> value, stored as given (see <see cref="SetFloat32List"/>).</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    public void SetFloat64List(int field, ReadOnlySpan<double> value) => Writer.SetList(_token, field, value, nameof(SetFloat64List));

    /// <summary>A <c>list&lt;string&gt;</c> value; no element may be null.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    public void SetStringList(int field, ReadOnlySpan<string> value)
    {
        Writer.StartList(_token, field);
        foreach (var item in value)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(value));
            Writer.AddStringItem(_token, field, item);
        }
    }

    /// <summary>A list of fixed-size elements of the field's element storage (the cache's records hold them so).</summary>
    internal void SetList<T>(int field, ReadOnlySpan<T> value)
        where T : unmanaged => Writer.SetList(_token, field, value, "SetList");

    /// <summary>Starts a list value (empty until items are added), for a source that decodes items one at a time; add
    /// its items with the adder of the field's element type before setting another field.</summary>
    public void StartList(int field) => Writer.StartList(_token, field);

    public void AddBoolItem(int field, bool item) => Writer.AddItem(_token, field, item, nameof(AddBoolItem));

    public void AddInt32Item(int field, int item) => Writer.AddItem(_token, field, item, nameof(AddInt32Item));

    public void AddInt64Item(int field, long item) => Writer.AddItem(_token, field, item, nameof(AddInt64Item));

    public void AddFloat32Item(int field, float item) => Writer.AddItem(_token, field, item, nameof(AddFloat32Item));

    public void AddFloat64Item(int field, double item) => Writer.AddItem(_token, field, item, nameof(AddFloat64Item));

    public void AddStringItem(int field, ReadOnlySpan<char> item) => Writer.AddStringItem(_token, field, item);

    /// <summary>The field has no value from this version on (a JSON <c>null</c>).</summary>
    public void SetMissing(int field) => Writer.SetMissing(_token, field);

    /// <summary>
    /// Writes a promoted value (as configuration and tests hold them): int64 into <c>int32</c> storage must fit, float64
    /// into <c>float32</c> narrows, a missing value clears the field.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An integer (or list element) outside the int32 range of an int32 field, or
    /// a list of another length than a <c>list&lt;E,N&gt;</c> field's.</exception>
    public void Set(int field, in Core.Values.ScalarValue value)
    {
        if (value.IsMissing)
        {
            SetMissing(field);
            return;
        }

        var type = Writer.Schema.Fields[field].Type;
        switch (type.Kind)
        {
            case StorageKind.Bool: SetBool(field, value.AsBool()); break;
            case StorageKind.Int32:
                var wide = value.AsInt64();
                if (!Int32Range.Fits(wide))
                {
                    throw OutsideInt32(field, value.ToString());
                }

                SetInt32(field, (int)wide);
                break;
            case StorageKind.Int64: SetInt64(field, value.AsInt64()); break;
            case StorageKind.Float32: SetFloat32(field, (float)value.AsFloat64()); break;
            case StorageKind.Float64: SetFloat64(field, value.AsFloat64()); break;
            case StorageKind.String: SetString(field, value.AsString()); break;
            case StorageKind.List:
                if (!ColumnValues.FitsLength(value.ListLength, type))
                {
                    var f = Writer.Schema.Fields[field];
                    throw new ArgumentOutOfRangeException(nameof(value), value.ToString(),
                        $"Field '{f.Name}' is {f.Type}: the value is {ColumnValues.Misfit(value, type)}; fix the data or declare the field as {ColumnType.List(type.Element)}.");
                }

                SetList(field, type, value);
                break;
            default: throw new InvalidOperationException($"Unsupported field type {type}.");
        }
    }

    private void SetList(int field, ColumnType type, in Core.Values.ScalarValue value)
    {
        switch (type.Element)
        {
            case StorageKind.Bool: SetBoolList(field, value.AsBoolList()); break;
            case StorageKind.Int32:
                var longs = value.AsInt64List();
                if (!Int32Range.AllFit(longs))
                {
                    throw OutsideInt32(field, value.ToString());
                }

                StartList(field);
                foreach (var item in longs)
                {
                    AddInt32Item(field, (int)item);
                }

                break;
            case StorageKind.Int64: SetInt64List(field, value.AsInt64List()); break;
            case StorageKind.Float32:
                StartList(field);
                foreach (var item in value.AsFloat64List())
                {
                    AddFloat32Item(field, (float)item);
                }

                break;
            case StorageKind.Float64: SetFloat64List(field, value.AsFloat64List()); break;
            default: SetStringList(field, value.AsStringList()); break;
        }
    }

    /// <summary>Writes the record. Returns false when the update was ignored as stale.</summary>
    /// <exception cref="LocalTableFullException">The table would pass <c>max_bytes</c> even after compaction.</exception>
    public bool Commit() => Writer.Commit(_token);

    /// <summary>Commits a complete cache record; the writer may evict other keys to make room for it.</summary>
    internal bool CommitCached(LocalTableCache cache) => Writer.Commit(_token, cache);

    private LocalTableWriter Writer => _writer ?? throw new InvalidOperationException("This RecordBuilder was not created by IFeatureTableWriter.Begin.");

    private ArgumentOutOfRangeException OutsideInt32(int field, string value)
    {
        var f = Writer.Schema.Fields[field];
        return new ArgumentOutOfRangeException(nameof(value), value,
            $"Field '{f.Name}' is {f.Type}: {value} is outside the int32 range [{int.MinValue}, {int.MaxValue}]; " +
            $"fix the data or declare the field as {f.Type.Promoted()}.");
    }
}
