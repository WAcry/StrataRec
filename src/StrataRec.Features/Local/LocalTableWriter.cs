using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Local;

/// <summary>
/// The single writer of a <see cref="LocalFeatureTable"/> (design 6.6, item 3): applies updates, deletes, checkpoints
/// and reloads, and compacts. Every call must come from one thread at a time (the update source's consumer, or the cache's
/// background writer); readers never wait for it.
/// </summary>
/// <remarks>
/// <para>A record is built in private buffers (<see cref="RecordBuilder"/>) and only reaches the pages at
/// <see cref="RecordBuilder.Commit"/>: the record bytes are written into free space at the end of a page, then the index
/// slot is pointed at them with one release write. Records are never modified, so a reader sees the old record or the new
/// one, both complete. String values are encoded at commit, after any compaction the commit itself triggered, so a code
/// always belongs to the dictionary of the version the record lands in.</para>
/// <para>Compaction runs here, synchronously: when superseded records make up more than half of the record bytes (and at
/// least <see cref="LocalFeatureTableOptions.CompactionMinBytes"/>), or when a write would pass <c>max_bytes</c> and
/// superseded records could be freed. It copies the current record of every key, tombstones included, into a new version
/// built aside (<see cref="BuildCompacted"/>), which is published whole only when it fits <c>max_bytes</c> (with the
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
    private char[] _key = new char[32];
    private int _keyLength;
    private TableVersion? _probedVersion;
    private IndexTable? _probedIndex;
    private long _oldPosition;
    private int _slot;
    private readonly byte[] _fixed;
    private readonly ulong[] _written;
    private readonly PendingBuffer _pending = new();
    private long[] _newStrings = new long[16]; // scratch set of the record's new strings, see NewStringBytes
    private int _lookedUpField = -1;           // a string field NewStringBytes looked up: its code in _lookedUpIn, or -1 if new
    private int _lookedUpCode;
    private TableVersion? _lookedUpIn;
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
        SetKey(key);
        return BeginCore(_version.Hash(key), version);
    }

    public RecordBuilder Begin(in EntityKey key, long version) =>
        key.StringValue is { } s ? Begin(s.AsSpan(), version) : Begin(key.Int64Value, version);

    public bool Delete(long key, long version)
    {
        RequireKeyType(EntityKeyType.Int64);
        _keyLength = 0;
        return DeleteCore(key, version);
    }

    public bool Delete(ReadOnlySpan<char> key, long version)
    {
        RequireKeyType(EntityKeyType.String);
        SetKey(key);
        return DeleteCore(_version.Hash(key), version);
    }

    public bool Delete(in EntityKey key, long version) =>
        key.StringValue is { } s ? Delete(s.AsSpan(), version) : Delete(key.Int64Value, version);

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

    /// <summary>
    /// Compacts now: copies the current record of every key (and every tombstone) into a new version and publishes it.
    /// A <c>cache</c> table can drop the records written before <paramref name="dropWrittenBefore"/> on the way (expired
    /// beyond any use; phase 2 decides the cutoff).
    /// </summary>
    /// <exception cref="LocalTableFullException">The compacted copy would not fit <c>max_bytes</c> (laying records out
    /// anew can leave more of the pages' tails empty); it is discarded and the table is unchanged.</exception>
    public void Compact(long dropWrittenBefore = long.MinValue)
    {
        Discard();
        if (dropWrittenBefore != long.MinValue && Schema.Mode != LocalTableMode.Cache)
        {
            throw new InvalidOperationException("Only cache tables drop records by write time; a local table's versions are event versions.");
        }

        var next = BuildCompacted(dropWrittenBefore);
        if (next.Bytes > _options.MaxBytes)
        {
            throw new LocalTableFullException(Schema, _options.MaxBytes, next.Bytes);
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

    internal Span<float> VectorBuffer(int token, int field)
    {
        CheckWrite(token, field, StorageKind.Vector);
        MarkWritten(field, valid: true);
        return MemoryMarshal.Cast<byte, float>(_fixed.AsSpan(_layout.Offsets[field], 4 * _layout.Dimensions[field]));
    }

    internal void SetVector(int token, int field, ReadOnlySpan<float> value)
    {
        CheckWrite(token, field, StorageKind.Vector);
        var dimension = _layout.Dimensions[field];
        if (value.Length != dimension)
        {
            throw new ArgumentException(
                $"Field '{Schema.Fields[field].Name}' is {Schema.Fields[field].Type}: expected {dimension} elements, got {value.Length}.", nameof(value));
        }

        value.CopyTo(MemoryMarshal.Cast<byte, float>(_fixed.AsSpan(_layout.Offsets[field], 4 * dimension)));
        MarkWritten(field, valid: true);
    }

    /// <summary>A <c>string</c> or <c>text</c> value. Kept as chars until commit; a string is created only for a value the
    /// dictionary does not hold yet.</summary>
    internal void SetString(int token, int field, ReadOnlySpan<char> value)
    {
        CheckOpen(token);
        CheckField(field);
        var kind = _layout.Kinds[field];
        if (kind is not (StorageKind.String or StorageKind.Text))
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
        if (_layout.Kinds[field] is not (StorageKind.ListInt32 or StorageKind.ListInt64 or StorageKind.ListString))
        {
            throw KindMismatch(field, "StartList");
        }

        _pending.Reserve(0, 8, out var start); // the items follow contiguously
        _pendingStart[field] = start;
        _pendingCount[field] = 0;
        _openList = field;
        MarkWritten(field, valid: true);
    }

    internal void AddInt32Item(int token, int field, int value)
    {
        CheckListItem(token, field, StorageKind.ListInt32, "AddInt32Item");
        MemoryMarshal.Write(_pending.Reserve(4, 4, out _), in value);
        _pendingCount[field]++;
    }

    internal void AddInt64Item(int token, int field, long value)
    {
        CheckListItem(token, field, StorageKind.ListInt64, "AddInt64Item");
        MemoryMarshal.Write(_pending.Reserve(8, 8, out _), in value);
        _pendingCount[field]++;
    }

    /// <summary>A <c>list&lt;string&gt;</c> item, kept as <c>[int32 length][UTF-16 chars]</c> until commit encodes it.</summary>
    internal void AddStringItem(int token, int field, ReadOnlySpan<char> item)
    {
        CheckListItem(token, field, StorageKind.ListString, "AddStringItem");
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

    internal bool Commit(int token)
    {
        CheckOpen(token);
        _open = false;
        _token++;
        _openList = -1;
        if (_ignored)
        {
            return false;
        }

        var newKey = _oldPosition == 0;
        var size = RecordSize(OldRecord(out var oldOffset), oldOffset);
        EnsureBudget(size, newKey, strings: true);
        ReprobeIfMoved(newKey);
        var oldPage = OldRecord(out oldOffset);

        // Unwritten fields keep their old values (design 6.6 local item 3). String codes are copied as they are: the old
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
                var code = f == _lookedUpField && ReferenceEquals(_lookedUpIn, _version)
                    ? (_lookedUpCode >= 0 ? _lookedUpCode : _version.AddString(new string(chars)))
                    : Encode(chars);
                Unsafe.WriteUnaligned(ref _fixed[_layout.Offsets[f]], code);
            }
        }

        _lookedUpIn = null; // do not keep a version alive past its commit

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

            var kind = _layout.Kinds[f];
            var elementSize = RecordLayout.ElementSize(kind);
            int count;
            if (Bits.Get(_written, f))
            {
                count = _pendingCount[f];
                if (kind == StorageKind.ListString)
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

        if (_layout.StringKeys)
        {
            WriteKey(page, offset, cursor);
        }

        // Release: everything above (record bytes, a new page in the directory, new dictionary strings) is visible to a
        // reader before it can see this position.
        _version.Publish(_slot, _indexKey, position, newKey);
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
        _lookedUpField = -1;
        _lookedUpIn = null;
        return new RecordBuilder(this, _token);
    }

    private bool DeleteCore(long indexKey, long version)
    {
        Discard();
        _indexKey = indexKey;
        Probe();
        if (_oldPosition != 0 && ExistingVersion() >= version)
        {
            return false;
        }

        var newKey = _oldPosition == 0;
        var size = _layout.HeaderSize + (_layout.StringKeys ? RecordLayout.Align(2 * _keyLength, 8) : 0);
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
        if (_layout.StringKeys)
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
    /// Re-finds the key's slot when a compaction (new version) or an index growth happened since the probe, and grows the
    /// index first when the key is new. The key's current record survives a compaction, so it is found again.
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

            size += RecordLayout.Align(count * RecordLayout.ElementSize(_layout.Kinds[f]), 8);
        }

        if (_layout.StringKeys)
        {
            size += RecordLayout.Align(2L * _keyLength, 8);
        }

        if (size > int.MaxValue / 2)
        {
            throw new InvalidOperationException($"A record of {size} bytes is too large for a local table.");
        }

        return (int)size;
    }

    /// <summary>
    /// Enforces <c>max_bytes</c>: the table's size after this write (a new page, index growth, new strings) must fit.
    /// When it would not and superseded records could be freed, a compacted copy is built aside and the write charged
    /// against it; the copy is installed only if the write then fits. Otherwise the write fails and the table is
    /// unchanged: same version, same generation. The charge is exactly what the write then adds, so the limit holds to the
    /// byte.
    /// </summary>
    private void EnsureBudget(int recordBytes, bool newKey, bool strings)
    {
        var required = Required(_version, recordBytes, newKey, strings);
        if (required <= _options.MaxBytes)
        {
            return;
        }

        if (_version.SupersededBytes > 0)
        {
            // Charged against the compacted copy: its dictionary keeps only strings still in use, so a string this record
            // shares with a superseded record only is new again; and its pages are laid out anew.
            var compacted = BuildCompacted(long.MinValue);
            var afterCompaction = Required(compacted, recordBytes, newKey, strings);
            if (afterCompaction <= _options.MaxBytes)
            {
                Install(compacted);
                return;
            }

            required = Math.Min(required, afterCompaction);
        }

        throw new LocalTableFullException(Schema, _options.MaxBytes, required);
    }

    /// <summary>The size <paramref name="version"/> would have after this write.</summary>
    private long Required(TableVersion version, int recordBytes, bool newKey, bool strings) =>
        version.Bytes + version.PageGrowthBytes(recordBytes) + version.IndexGrowthBytes(newKey) + (strings ? NewStringBytes(version) : 0);

    /// <summary>
    /// What the record's strings add to the dictionary of the version being written: each distinct value of its written
    /// <c>string</c> fields and <c>list&lt;string&gt;</c> items that the dictionary does not hold yet, once however often
    /// the record repeats it, plus the dictionary growths that adding them passes. This is what <see cref="Encode"/> then
    /// does at commit (fields kept from the old record already have codes). Allocates only when a record brings more
    /// strings than any record before it.
    /// </summary>
    private long NewStringBytes(TableVersion version)
    {
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
            _lookedUpIn = version;
            return _lookedUpCode >= 0 ? 0 : version.StringAdditionBytes(1, TableVersion.StringObjectBytes(chars.Length));
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
        var objectBytes = 0L;
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
            else if (_layout.Kinds[f] == StorageKind.ListString)
            {
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
    /// The automatic compaction (design 6.6 local item 4): when superseded records are more than half of the record bytes.
    /// It runs after a write that already succeeded, so a compacted copy that would not fit <c>max_bytes</c> is simply
    /// discarded; it is tried again only once twice as many bytes are superseded, not after every write.
    /// </summary>
    private void MaybeCompact()
    {
        var superseded = _version.SupersededBytes;
        if (superseded > 0 && superseded >= _options.CompactionMinBytes && superseded * 2 > _version.RecordBytes
            && superseded >= 2 * _autoCompactionDeclined)
        {
            var next = BuildCompacted(long.MinValue);
            if (next.Bytes <= _options.MaxBytes)
            {
                Install(next);
            }
            else
            {
                _autoCompactionDeclined = superseded;
            }
        }
    }

    /// <summary>
    /// The one compaction routine (design 6.6, item 4): builds, aside, a version holding the current record (or
    /// tombstone) of every key, and returns it unpublished. Nothing else changes, so a caller that finds the result too
    /// large just drops it; <see cref="Install"/> makes it current.
    /// </summary>
    /// <remarks>
    /// Records are self-contained byte ranges, so each is copied as is; only string codes are re-encoded into the new
    /// version's dictionary, which therefore keeps only strings still in use. Tombstones are kept: dropping one would let
    /// an older update of the deleted key, delivered late, bring it back. Records are copied in their old page order and
    /// packed: each new page is just large enough for the records still to come (up to the largest page size). Copying in
    /// index-slot order instead would mix record sizes and can leave more of each page's tail empty, which can make the
    /// copy larger than the original.
    /// </remarks>
    private TableVersion BuildCompacted(long dropWrittenBefore)
    {
        var old = _version;
        var oldIndex = old.Index;
        var entries = oldIndex.Entries;
        var positions = new long[oldIndex.Count];
        var slots = new int[oldIndex.Count];
        var live = 0;
        for (var s = 0; s < oldIndex.SlotCount; s++)
        {
            var position = entries[(2 * s) + 1];
            if (position != 0)
            {
                positions[live] = position;
                slots[live] = s;
                live++;
            }
        }

        Array.Sort(positions, slots, 0, live); // a position is (page + 1) << 32 | offset: page order
        var next = NewVersion(live);
        var codeMap = new int[old.StringCount];
        Array.Fill(codeMap, -1);
        var strings = old.Strings;
        var remaining = old.RecordBytes - old.SupersededBytes;
        for (var i = 0; i < live; i++)
        {
            var page = old.PageOf(positions[i]);
            var offset = (int)positions[i];
            var length = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(offset + RecordLayout.LengthOffset));
            var tombstone = (page[offset + RecordLayout.FlagsOffset] & RecordLayout.TombstoneFlag) != 0;
            if (!tombstone && dropWrittenBefore != long.MinValue
                && BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(offset + RecordLayout.VersionOffset)) < dropWrittenBefore)
            {
                remaining -= length;
                continue;
            }

            var copyPosition = next.Reserve(length, out var copy, packFor: remaining);
            remaining -= length;
            var copyOffset = (int)copyPosition;
            page.AsSpan(offset, length).CopyTo(copy.AsSpan(copyOffset));
            if (!tombstone)
            {
                RemapStrings(copy, copyOffset, next, strings, codeMap);
            }

            next.Index.InsertUnpublished(entries[2 * slots[i]], copyPosition);
            next.AddRecordBytes(length);
            next.AddRecords(tombstone ? 0 : 1, tombstone ? 1 : 0);
        }

        return next;
    }

    /// <summary>Makes a version from <see cref="BuildCompacted"/> the one written to, and current unless a reload is
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
        for (var f = 0; f < _layout.FieldCount; f++)
        {
            var kind = _layout.Kinds[f];
            if (kind is not (StorageKind.String or StorageKind.ListString)
                || (page[offset + RecordLayout.ValidityOffset + (f >> 3)] & (1 << (f & 7))) == 0)
            {
                continue;
            }

            var at = offset + _layout.Offsets[f];
            Span<int> codes;
            if (kind == StorageKind.String)
            {
                codes = MemoryMarshal.Cast<byte, int>(page.AsSpan(at, 4));
            }
            else
            {
                var relative = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at));
                var count = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at + 4));
                codes = MemoryMarshal.Cast<byte, int>(page.AsSpan(offset + relative, 4 * count));
            }

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

    /// <summary>Statistics of a published write: its bytes, the bytes of the record or tombstone it replaced, and counts.</summary>
    private void Account(int size, byte[]? oldPage, bool newKey, bool tombstone)
    {
        _version.AddRecordBytes(size);
        if (!newKey)
        {
            var replaced = _version.PageOf(_oldPosition);
            _version.AddSuperseded(BinaryPrimitives.ReadInt32LittleEndian(replaced.AsSpan((int)_oldPosition + RecordLayout.LengthOffset)));
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
        _key.AsSpan(0, _keyLength).CopyTo(MemoryMarshal.Cast<byte, char>(page.AsSpan(offset + cursor, 2 * _keyLength)));
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

    private void SetKey(ReadOnlySpan<char> key)
    {
        if (key.Length > _key.Length)
        {
            _key = new char[Math.Max(key.Length, _key.Length * 2)];
        }

        key.CopyTo(_key);
        _keyLength = key.Length;
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

    private void Discard()
    {
        if (_open)
        {
            _open = false;
            _token++;
        }
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

    private void CheckListItem(int token, int field, StorageKind kind, string method)
    {
        CheckOpen(token);
        CheckField(field);
        if (_layout.Kinds[field] != kind)
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
            StorageKind.String or StorageKind.Text => "SetString",
            StorageKind.Vector => "SetVector",
            StorageKind.ListInt32 => "SetInt32List",
            StorageKind.ListInt64 => "SetInt64List",
            _ => "SetStringList",
        };
        return new InvalidOperationException($"Field {field} ('{Schema.Fields[field].Name}') is {type}; {method} does not match it (use {setter}).");
    }

    private void RequireKeyType(EntityKeyType keyType)
    {
        if (Schema.KeyType != keyType)
        {
            throw new InvalidOperationException(
                $"Local table {Schema.Source}/{Schema.Entity} is keyed by {(Schema.KeyType == EntityKeyType.Int64 ? "int64" : "string")}, not " +
                $"{(keyType == EntityKeyType.Int64 ? "int64" : "string")}.");
        }
    }

    /// <summary>
    /// Where the record being built stages its variable-length values until commit: the chars of <c>string</c> and
    /// <c>text</c> values and the elements of lists. Its cursor moves only in <see cref="Reserve"/>, which aligns it,
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
/// One update being built (design 6.6): the source decodes each field straight into it, then commits. Unwritten fields
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

    /// <summary>A <c>string</c> (dictionary-encoded) or <c>text</c> value.</summary>
    public void SetString(int field, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Writer.SetString(_token, field, value.AsSpan());
    }

    /// <summary>A <c>string</c> or <c>text</c> value from chars, e.g. decoded into a reused buffer.</summary>
    public void SetString(int field, ReadOnlySpan<char> value) => Writer.SetString(_token, field, value);

    /// <summary>A <c>vec&lt;float32,N&gt;</c> value of exactly N elements.</summary>
    public void SetVector(int field, ReadOnlySpan<float> value) => Writer.SetVector(_token, field, value);

    /// <summary>The vector field's storage in the record being built, for decoding in place; the field counts as present,
    /// so write all N elements.</summary>
    public Span<float> VectorBuffer(int field) => Writer.VectorBuffer(_token, field);

    public void SetInt32List(int field, ReadOnlySpan<int> value)
    {
        Writer.StartList(_token, field);
        foreach (var item in value)
        {
            Writer.AddInt32Item(_token, field, item);
        }
    }

    public void SetInt64List(int field, ReadOnlySpan<long> value)
    {
        Writer.StartList(_token, field);
        foreach (var item in value)
        {
            Writer.AddInt64Item(_token, field, item);
        }
    }

    public void SetStringList(int field, ReadOnlySpan<string> value)
    {
        Writer.StartList(_token, field);
        foreach (var item in value)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(value));
            Writer.AddStringItem(_token, field, item);
        }
    }

    /// <summary>Starts a list value (empty until items are added); add its items before setting another field.</summary>
    public void StartList(int field) => Writer.StartList(_token, field);

    public void AddInt32Item(int field, int item) => Writer.AddInt32Item(_token, field, item);

    public void AddInt64Item(int field, long item) => Writer.AddInt64Item(_token, field, item);

    public void AddStringItem(int field, ReadOnlySpan<char> item) => Writer.AddStringItem(_token, field, item);

    /// <summary>The field has no value from this version on (a JSON <c>null</c>).</summary>
    public void SetMissing(int field) => Writer.SetMissing(_token, field);

    /// <summary>
    /// Writes a promoted value (as configuration and tests hold them): int64 into <c>int32</c> storage must fit, float64
    /// into <c>float32</c> narrows, a missing value clears the field.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An integer (or list element) outside the int32 range of an int32 field.</exception>
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
            case StorageKind.String or StorageKind.Text: SetString(field, value.AsString()); break;
            case StorageKind.Vector: SetVector(field, value.AsVector()); break;
            case StorageKind.ListInt64: SetInt64List(field, value.AsInt64List()); break;
            case StorageKind.ListInt32:
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
            case StorageKind.ListString: SetStringList(field, value.AsStringList()); break;
            default: throw new InvalidOperationException($"Unsupported field type {type}.");
        }
    }

    /// <summary>Writes the record. Returns false when the update was ignored as stale.</summary>
    /// <exception cref="LocalTableFullException">The table would pass <c>max_bytes</c> even after compaction.</exception>
    public bool Commit() => Writer.Commit(_token);

    private LocalTableWriter Writer => _writer ?? throw new InvalidOperationException("This RecordBuilder was not created by IFeatureTableWriter.Begin.");

    private ArgumentOutOfRangeException OutsideInt32(int field, string value)
    {
        var f = Writer.Schema.Fields[field];
        return new ArgumentOutOfRangeException(nameof(value), value,
            $"Field '{f.Name}' is {f.Type}: {value} is outside the int32 range [{int.MinValue}, {int.MaxValue}]; " +
            $"fix the data or declare the field as {f.Type.Promoted()}.");
    }
}
