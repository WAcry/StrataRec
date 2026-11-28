using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Local;

internal delegate ulong KeyHasher(ReadOnlySpan<byte> key);

/// <summary>
/// A found record of a pinned <see cref="TableVersion"/>, or none (<see cref="Found"/> false).
/// </summary>
/// <remarks>Only meaningful for the version whose lookup produced it: another version of the same table reads its
/// string codes through a dictionary a compaction renumbered. Keep it no longer than that request (it holds the page
/// alive).</remarks>
internal readonly struct LocalRecordRef
{
    internal readonly byte[]? Page;
    internal readonly int Offset;

    internal LocalRecordRef(byte[] page, int offset)
    {
        Page = page;
        Offset = offset;
    }

    public bool Found => Page is not null;

    /// <summary>Only the first fresh hit since the eviction hand passed needs to write the visited bit.
    /// A race with the writer can lose a hint. The payload, timestamp and other flags are immutable.</summary>
    internal void MarkCacheHit()
    {
        ref var flags = ref Page![Offset + RecordLayout.FlagsOffset];
        var seen = Volatile.Read(ref flags);
        if ((seen & RecordLayout.CacheHitFlag) == 0)
        {
            Volatile.Write(ref flags, (byte)(seen | RecordLayout.CacheHitFlag));
        }
    }
}

internal readonly struct LocalFieldRead
{
    public LocalFieldRead(int field, ColumnBuffer target, in ScalarValue absent)
    {
        Field = field;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Absent = absent;
    }

    public int Field { get; }

    public ColumnBuffer Target { get; }

    public ScalarValue Absent { get; }
}

/// <param name="NonFinite">NaN/±∞ values (and float lists holding one): <c>column_nonfinite_total</c>.</param>
/// <param name="OutOfRange">Lists of another length than a <c>list&lt;E,N&gt;</c> target's: <c>column_out_of_range_total</c>.</param>
internal readonly record struct LocalFillCounts(int NonFinite, int OutOfRange);

/// <summary>
/// A request pins the table's current version once and does every lookup against it; a compaction or reload builds a
/// new version and swaps it in.
/// </summary>
/// <remarks>
/// <para><b>Reading is lock-free.</b></para>
/// <para>The reader API is internal: features of a local table are read through the feature catalog.</para>
/// </remarks>
public sealed class TableVersion
{
    internal const ulong Fibonacci = 0x9E3779B97F4A7C15UL;
    private const int InitialDirectoryLength = 8;
    private const int InitialStringsLength = 16;
    private const int MinPageSize = 4096;

    private readonly RecordLayout _layout;
    private readonly KeyHasher _hasher;
    private readonly int _maxPageSize;

    // Published state: replaced (index, directory, dictionary on growth) or extended (a new slot, page or string) by the
    // writer and read by readers without locks. See LocalFeatureTable for the ordering rules.
    private IndexTable _index;
    private byte[][] _pages;
    private string[] _strings;

    // Statistics: written by the writer with Volatile.Write, read by anyone.
    private long _bytes;
    private long _recordBytes;
    private long _supersededBytes;
    private int _records;
    private int _tombstones;

    // Writer-only state.
    private int _pageCount;
    private byte[]? _currentPage;
    private int _pageUsed;
    private int _nextPageSize;
    private int _stringCount;
    private long _stringBytes;
    private long _unreferencedStringBytes;
    private int[] _references; // per code: references from current records, or Unreferenced; grown with _strings
    private readonly StringCodeMap _codes;

    private const int Unreferenced = -1;

    /// <summary>Bytes a dictionary slot is charged: the string reference and its reference count.</summary>
    private const int StringSlotBytes = 12;

    internal TableVersion(RecordLayout layout, KeyHasher hasher, int maxPageSize, long generation, int expectedKeys)
    {
        _layout = layout;
        _hasher = hasher;
        _maxPageSize = maxPageSize;
        Generation = generation;
        _index = new IndexTable(IndexTable.SlotsFor(expectedKeys));
        _pages = new byte[InitialDirectoryLength][];
        _strings = new string[InitialStringsLength];
        _references = new int[InitialStringsLength];
        _codes = new StringCodeMap();
        _nextPageSize = Math.Min(maxPageSize, MinPageSize);
        _bytes = IndexBytes(_index) + (_pages.Length * 8L) + ((long)_strings.Length * StringSlotBytes) + _codes.Bytes;
    }

    /// <summary>Increases with every version a table installs (a compaction or reload); a compacted copy discarded for not
    /// fitting <c>max_bytes</c> does not count.</summary>
    public long Generation { get; }

    /// <summary>Entities with a record (tombstones excluded).</summary>
    public int RecordCount => Volatile.Read(ref _records);

    /// <summary>Deleted keys still held so an older update cannot bring them back.</summary>
    public int TombstoneCount => Volatile.Read(ref _tombstones);

    /// <summary>Memory the version holds: pages, index, page directory and string dictionary (what <c>max_bytes</c> limits).</summary>
    public long Bytes => Volatile.Read(ref _bytes);

    /// <summary>Bytes of records in the pages, current and superseded.</summary>
    public long RecordBytes => Volatile.Read(ref _recordBytes);

    /// <summary>Bytes of records that a newer record or tombstone replaced: what a compaction would free.</summary>
    public long SupersededBytes => Volatile.Read(ref _supersededBytes);

    internal RecordLayout Layout => _layout;

    internal LocalRecordRef Find(long key)
    {
        if (_layout.HashedKeys)
        {
            return default;
        }

        var pages = Volatile.Read(ref _pages);
        var position = ProbeInt64(Volatile.Read(ref _index), key);
        return position == 0 ? default : Resolve(position, ref pages);
    }

    internal LocalRecordRef Find(ReadOnlySpan<char> key) =>
        _layout.Schema.Key.PartCount == 1 && _layout.HashedKeys ? FindEncoded(MemoryMarshal.AsBytes(key)) : default;

    internal LocalRecordRef Find(in EntityKey key) => key.StringValue is { } s ? Find(s.AsSpan()) : Find(key.Int64Value);

    internal LocalRecordRef Find(ReadOnlySpan<EntityKey> parts)
    {
        var layout = _layout.Schema.Key;
        if (TableKeys.Check(layout, parts) is not null)
        {
            return default;
        }

        if (!_layout.HashedKeys)
        {
            return Find(parts[0].Int64Value);
        }

        var length = TableKeys.Length(parts);
        byte[]? rented = null;
        var bytes = length <= TableKeys.StackBytes ? stackalloc byte[TableKeys.StackBytes] : rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            return FindEncoded(bytes[..TableKeys.Encode(parts, bytes)]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal LocalRecordRef FindEncoded(ReadOnlySpan<byte> key)
    {
        if (!_layout.HashedKeys)
        {
            return default;
        }

        var pages = Volatile.Read(ref _pages);
        var position = ProbeHashed(Volatile.Read(ref _index), _hasher(key), key, ref pages, out _);
        return position == 0 ? default : Resolve(position, ref pages);
    }

    /// <summary>
    /// Looks every key up once: <c>records[i]</c> becomes the record of <c>keys[i]</c> or none. Returns the number of
    /// keys found.
    /// </summary>
    /// <remarks>
    /// Keys are taken in blocks of <see cref="LookupBlock"/>, in two passes. The first reads every key's home slot and
    /// nothing else, so no branch waits on a cache miss and the misses of the whole block overlap.
    /// </remarks>
    internal int Lookup(ReadOnlySpan<long> keys, Span<LocalRecordRef> records)
    {
        records = records[..keys.Length];
        if (_layout.HashedKeys)
        {
            records.Clear();
            return 0;
        }

        var index = Volatile.Read(ref _index);
        var pages = Volatile.Read(ref _pages);
        var entries = index.Entries;
        var mask = index.Mask;
        Span<long> homePositions = stackalloc long[LookupBlock];
        Span<long> homeKeys = stackalloc long[LookupBlock];
        var found = 0;
        for (var start = 0; start < keys.Length; start += LookupBlock)
        {
            var block = keys.Slice(start, Math.Min(LookupBlock, keys.Length - start));
            for (var i = 0; i < block.Length; i++)
            {
                var slot = index.Home((ulong)block[i]);
                // Acquire, then the key: the same order as ProbeInt64.
                homePositions[i] = Volatile.Read(ref entries[(2 * slot) + 1]);
                homeKeys[i] = entries[2 * slot];
            }

            var blockRecords = records.Slice(start, block.Length);
            for (var i = 0; i < block.Length; i++)
            {
                var key = block[i];
                var position = homePositions[i];
                if (position != 0 && homeKeys[i] != key)
                {
                    position = ProbeInt64(entries, mask, (index.Home((ulong)key) + 1) & mask, key);
                }

                if (position == 0)
                {
                    blockRecords[i] = default;
                    continue;
                }

                blockRecords[i] = Resolve(position, ref pages);
                found += blockRecords[i].Found ? 1 : 0;
            }
        }

        return found;
    }

    /// <summary>Keys per block of the int64 <see cref="Lookup(ReadOnlySpan{long}, Span{LocalRecordRef})"/>: enough
    /// independent home-slot misses to keep the core's miss buffers busy, little enough stack.</summary>
    private const int LookupBlock = 64;

    /// <summary>One-string-part form of <see cref="Lookup(ReadOnlySpan{long}, Span{LocalRecordRef})"/>; a null key (a
    /// row with no key) is not found.</summary>
    internal int Lookup(ReadOnlySpan<string?> keys, Span<LocalRecordRef> records)
    {
        records = records[..keys.Length];
        if (!_layout.HashedKeys || _layout.Schema.Key.PartCount != 1)
        {
            records.Clear();
            return 0;
        }

        var index = Volatile.Read(ref _index);
        var pages = Volatile.Read(ref _pages);
        Span<ulong> hashes = stackalloc ulong[LookupBlock];
        var found = 0;
        for (var start = 0; start < keys.Length; start += LookupBlock)
        {
            var block = keys.Slice(start, Math.Min(LookupBlock, keys.Length - start));
            for (var i = 0; i < block.Length; i++)
            {
                hashes[i] = block[i] is { } key ? _hasher(MemoryMarshal.AsBytes(key.AsSpan())) : 0;
            }

            found += ProbeBlock(index, ref pages, new StringKeyBlock(block), hashes[..block.Length], records.Slice(start, block.Length));
        }

        return found;
    }

    internal int Lookup(ReadOnlySpan<EntityKey> keys, Span<LocalRecordRef> records)
    {
        records = records[..keys.Length];
        var index = Volatile.Read(ref _index);
        var pages = Volatile.Read(ref _pages);
        var oneString = _layout.HashedKeys && _layout.Schema.Key.PartCount == 1;
        var found = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            long position;
            if (keys[i].StringValue is { } s)
            {
                var key = MemoryMarshal.AsBytes(s.AsSpan());
                position = oneString ? ProbeHashed(index, _hasher(key), key, ref pages, out _) : 0;
            }
            else
            {
                position = _layout.HashedKeys ? 0 : ProbeInt64(index, keys[i].Int64Value);
            }

            records[i] = position == 0 ? default : Resolve(position, ref pages);
            found += records[i].Found ? 1 : 0;
        }

        return found;
    }

    /// <summary>
    /// Form for keys of any layout, as a batch hands them over (a shared part once for every key); a key not of the
    /// table's layout is not found.
    /// </summary>
    internal int Lookup(in EntityKeyBatch keys, Span<LocalRecordRef> records)
    {
        records = records[..keys.Count];
        var layout = _layout.Schema.Key;
        if (keys.PartCount != layout.PartCount)
        {
            records.Clear();
            return 0;
        }

        var index = Volatile.Read(ref _index);
        var pages = Volatile.Read(ref _pages);
        var found = 0;
        if (!_layout.HashedKeys)
        {
            for (var i = 0; i < records.Length; i++)
            {
                var key = keys.Key(i, 0);
                var position = key.IsString ? 0 : ProbeInt64(index, key.Int64Value);
                records[i] = position == 0 ? default : Resolve(position, ref pages);
                found += records[i].Found ? 1 : 0;
            }

            return found;
        }

        // Where each part of key 0 is in the batch's buffer, and the distance between keys (0 for a shared part).
        var partCount = layout.PartCount;
        var buffer = keys.Buffer;
        Span<int> at = stackalloc int[partCount];
        Span<int> step = stackalloc int[partCount];
        for (var p = 0; p < partCount; p++)
        {
            (at[p], step[p]) = keys.PartAt(p);
        }

        var stringMask = TableKeys.StringMask(layout);
        var tuple = default(KeyPartBuffer);
        Span<EntityKey> parts = tuple;
        parts = parts[..partCount];
        Span<ulong> hashes = stackalloc ulong[LookupBlock];
        Span<int> starts = stackalloc int[LookupBlock];
        Span<int> lengths = stackalloc int[LookupBlock];
        byte[]? rented = null;
        Span<byte> arena = stackalloc byte[LookupBlock * 32];
        try
        {
            for (var start = 0; start < records.Length; start += LookupBlock)
            {
                var count = Math.Min(LookupBlock, records.Length - start);
                var used = 0;
                for (var i = 0; i < count; i++)
                {
                    for (var p = 0; p < partCount; p++)
                    {
                        parts[p] = buffer[at[p] + (step[p] * (start + i))];
                    }

                    if (!TableKeys.Matches(stringMask, parts))
                    {
                        (starts[i], lengths[i], hashes[i]) = (-1, 0, 0);
                        continue;
                    }

                    var length = TableKeys.Length(parts);
                    if (used + length > arena.Length)
                    {
                        var larger = ArrayPool<byte>.Shared.Rent(Math.Max(2 * arena.Length, used + length));
                        arena[..used].CopyTo(larger);
                        if (rented is not null)
                        {
                            ArrayPool<byte>.Shared.Return(rented);
                        }

                        arena = rented = larger;
                    }

                    var key = arena.Slice(used, TableKeys.Encode(parts, arena[used..]));
                    (starts[i], lengths[i], hashes[i]) = (used, key.Length, _hasher(key));
                    used += key.Length;
                }

                found += ProbeBlock(index, ref pages, new EncodedKeyBlock(arena, starts, lengths), hashes[..count], records.Slice(start, count));
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        return found;
    }

    private int ProbeBlock<TKeys>(IndexTable index, ref byte[][] pages, in TKeys keys, ReadOnlySpan<ulong> hashes, Span<LocalRecordRef> records)
        where TKeys : IKeyBlock, allows ref struct
    {
        var entries = index.Entries;
        Span<long> homePositions = stackalloc long[LookupBlock];
        Span<long> homeHashes = stackalloc long[LookupBlock];
        for (var i = 0; i < hashes.Length; i++)
        {
            var slot = index.Home(hashes[i]);
            // Acquire, then the hash: the same order as ProbeHashed.
            homePositions[i] = Volatile.Read(ref entries[(2 * slot) + 1]);
            homeHashes[i] = entries[2 * slot];
        }

        var found = 0;
        for (var i = 0; i < hashes.Length; i++)
        {
            if (!keys.TryGet(i, out var key))
            {
                records[i] = default;
                continue;
            }

            var position = homePositions[i];
            if (position != 0 && (homeHashes[i] != (long)hashes[i] || !KeyOf(PageOf(position, ref pages), (int)position).SequenceEqual(key)))
            {
                position = ProbeHashed(index, hashes[i], key, ref pages, out _);
            }

            records[i] = position == 0 ? default : Resolve(position, ref pages);
            found += records[i].Found ? 1 : 0;
        }

        return found;
    }

    private interface IKeyBlock
    {
        /// <summary>Key <paramref name="i"/>'s bytes; false when it has none (no key, or not of the table's layout), which
        /// is not found.</summary>
        bool TryGet(int i, out ReadOnlySpan<byte> key);
    }

    private readonly ref struct StringKeyBlock(ReadOnlySpan<string?> keys) : IKeyBlock
    {
        private readonly ReadOnlySpan<string?> _keys = keys;

        public bool TryGet(int i, out ReadOnlySpan<byte> key)
        {
            key = MemoryMarshal.AsBytes(_keys[i].AsSpan());
            return _keys[i] is not null;
        }
    }

    private readonly ref struct EncodedKeyBlock(ReadOnlySpan<byte> arena, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths) : IKeyBlock
    {
        private readonly ReadOnlySpan<byte> _arena = arena;
        private readonly ReadOnlySpan<int> _starts = starts;
        private readonly ReadOnlySpan<int> _lengths = lengths;

        public bool TryGet(int i, out ReadOnlySpan<byte> key)
        {
            var start = _starts[i];
            key = start < 0 ? default : _arena.Slice(start, _lengths[i]);
            return start >= 0;
        }
    }

    internal LocalFillCounts Fill(
        int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ColumnBuffer target, in ScalarValue absent, Span<ulong> filled = default)
    {
        var read = new LocalFieldRead(field, target, absent);
        Span<LocalFillCounts> counts = stackalloc LocalFillCounts[1];
        FillCore(records, rows, new ReadOnlySpan<LocalFieldRead>(in read), counts, filled);
        return counts[0];
    }

    /// <remarks>Fill bits are left to the caller: every row in <paramref name="rows"/> is filled for every field, so one
    /// bitmap of the rows can be OR-ed into each slot's fill bitmap.</remarks>
    internal void Fill(ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ReadOnlySpan<LocalFieldRead> reads, Span<LocalFillCounts> counts = default)
    {
        if (!counts.IsEmpty && counts.Length < reads.Length)
        {
            throw new ArgumentException($"{counts.Length} counters for {reads.Length} reads.", nameof(counts));
        }

        FillCore(records, rows, reads, counts, filled: default);
    }

    /// <summary>
    /// The caller's spans can change under the loops: <paramref name="rows"/> may be a view of a target column's own
    /// array, may overlap <paramref name="counts"/>, or may be changed by another thread between the check and the
    /// loop.
    /// </summary>
    private void FillCore(
        ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ReadOnlySpan<LocalFieldRead> reads, Span<LocalFillCounts> counts, Span<ulong> filled)
    {
        if (records.Length != rows.Length)
        {
            throw new ArgumentException($"{records.Length} records for {rows.Length} rows; pass one record per row.", nameof(rows));
        }

        var count = rows.Length;
        var rowCopy = ArrayPool<int>.Shared.Rent(count);
        var recordCopy = ArrayPool<LocalRecordRef>.Shared.Rent(count);
        var readCopy = ArrayPool<LocalFieldRead>.Shared.Rent(reads.Length);
        try
        {
            rows.CopyTo(rowCopy);
            records.CopyTo(recordCopy);
            reads.CopyTo(readCopy);
            FillChecked(recordCopy.AsSpan(0, count), rowCopy.AsSpan(0, count), readCopy.AsSpan(0, reads.Length), counts);
            if (!filled.IsEmpty)
            {
                foreach (var row in rowCopy.AsSpan(0, count))
                {
                    Bits.Set(filled, row);
                }
            }
        }
        finally
        {
            // The copies hold page and column references: cleared, so the pool keeps no version or request memory alive.
            recordCopy.AsSpan(0, count).Clear();
            readCopy.AsSpan(0, reads.Length).Clear();
            ArrayPool<int>.Shared.Return(rowCopy);
            ArrayPool<LocalRecordRef>.Shared.Return(recordCopy);
            ArrayPool<LocalFieldRead>.Shared.Return(readCopy);
        }
    }

    private void FillChecked(ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ReadOnlySpan<LocalFieldRead> reads, Span<LocalFillCounts> counts)
    {
        var maxRow = CheckRows(records, rows);
        if (!counts.IsEmpty)
        {
            counts[..reads.Length].Clear();
        }

        var readEnd = RecordLayout.ValidityOffset + _layout.ValidityBytes;
        foreach (var read in reads)
        {
            CheckTarget(read.Field, read.Target, maxRow);
            readEnd = Math.Max(readEnd, _layout.Offsets[read.Field] + _layout.Sizes[read.Field]);
        }

        for (var start = 0; start < rows.Length; start += FillBlock)
        {
            var count = Math.Min(FillBlock, rows.Length - start);
            var blockRecords = records.Slice(start, count);
            var blockRows = rows.Slice(start, count);
            Touch(blockRecords, readEnd);
            for (var r = 0; r < reads.Length; r++)
            {
                var replaced = FillField(reads[r].Field, blockRecords, blockRows, maxRow, reads[r].Target, reads[r].Absent);
                if (!counts.IsEmpty)
                {
                    counts[r] = new LocalFillCounts(counts[r].NonFinite + replaced.NonFinite, counts[r].OutOfRange + replaced.OutOfRange);
                }
            }
        }
    }

    /// <summary>
    /// Rows per block of the multi-field <see cref="Fill(ReadOnlySpan{LocalRecordRef}, ReadOnlySpan{int}, ReadOnlySpan{LocalFieldRead}, Span{LocalFillCounts})"/>:
    /// the headers and scalar values of 128 records (two or three cache lines each) fit the L1 data cache.
    /// </summary>
    internal const int FillBlock = 128;

    /// <summary>
    /// The sum is returned, and the method not inlined, so the loads are not removed as dead code; callers ignore it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Touch(ReadOnlySpan<LocalRecordRef> records, int end)
    {
        // CheckRows checked that the fixed part, which contains [0, end), is inside the page.
        var sum = 0;
        ref var record0 = ref MemoryMarshal.GetReference(records);
        for (var i = 0; i < records.Length; i++)
        {
            ref readonly var record = ref Unsafe.Add(ref record0, i);
            if (record.Page is { } page)
            {
                ref var start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(page), record.Offset);
                for (var at = 0; at < end; at += 64)
                {
                    sum += Unsafe.Add(ref start, at);
                }

                sum += Unsafe.Add(ref start, end - 1);
            }
        }

        return sum;
    }

    private LocalFillCounts FillField(
        int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, int maxRow, ColumnBuffer target, in ScalarValue absent)
    {
        switch (_layout.Kinds[field])
        {
            case StorageKind.Bool: FillScalar<bool>(field, records, rows, maxRow, (ScalarColumn<bool>)target, absent); return default;
            case StorageKind.Int32: FillScalar<int>(field, records, rows, maxRow, (ScalarColumn<int>)target, absent); return default;
            case StorageKind.Int64: FillScalar<long>(field, records, rows, maxRow, (ScalarColumn<long>)target, absent); return default;
            case StorageKind.Float32: return new(FillFloat32(field, records, rows, maxRow, (ScalarColumn<float>)target, absent), 0);
            case StorageKind.Float64: return new(FillFloat64(field, records, rows, maxRow, (ScalarColumn<double>)target, absent), 0);
            case StorageKind.String: FillString(field, records, rows, maxRow, (StringColumn)target, absent); return default;
            case StorageKind.List:
                switch (_layout.Elements[field])
                {
                    case StorageKind.Bool: return FillList(field, records, rows, (ListColumn<bool>)target, absent);
                    case StorageKind.Int32: return FillList(field, records, rows, (ListColumn<int>)target, absent);
                    case StorageKind.Int64: return FillList(field, records, rows, (ListColumn<long>)target, absent);
                    case StorageKind.Float32: return FillList(field, records, rows, (ListColumn<float>)target, absent);
                    case StorageKind.Float64: return FillList(field, records, rows, (ListColumn<double>)target, absent);
                    default: return new(0, FillStringList(field, records, rows, (ListColumn<string>)target, absent));
                }

            default: throw new InvalidOperationException($"Unsupported field kind {_layout.Kinds[field]}.");
        }
    }

    /// <summary>
    /// Everything the unchecked fill loops rely on, checked once per call on <see cref="FillCore"/>'s copies: rows not
    /// negative, and every record's fixed part inside its page. Returns the largest row, or -1 when there are none;
    /// each target is checked against it.
    /// </summary>
    private int CheckRows(ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows)
    {
        var maxRow = -1;
        var minRow = 0;
        var fixedSize = (uint)_layout.FixedSize;
        for (var i = 0; i < rows.Length; i++)
        {
            maxRow = Math.Max(maxRow, rows[i]);
            minRow = Math.Min(minRow, rows[i]);
            if (records[i].Page is { } page && (uint)records[i].Offset + fixedSize > (uint)page.Length)
            {
                throw new ArgumentException(
                    $"Record {i} does not belong to this table version; pass references from this version's Lookup.", nameof(records));
            }
        }

        if (minRow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), minRow, "Rows are physical row indices of the target columns and cannot be negative.");
        }

        return maxRow;
    }

    private void CheckTarget(int field, ColumnBuffer target, int maxRow)
    {
        CheckField(field);
        ArgumentNullException.ThrowIfNull(target);
        var kind = _layout.Kinds[field];
        // The target's fixed length may differ from the field's: the fill checks each list against the target's type.
        if (target.Type.Kind != kind || target.Type.Element != _layout.Elements[field])
        {
            throw new ArgumentException(
                $"Field '{_layout.Schema.Fields[field].Name}' is {_layout.Schema.Fields[field].Type}; the target column is {target.Type}.", nameof(target));
        }

        if (maxRow >= target.Capacity)
        {
            throw new ArgumentOutOfRangeException(
                "rows", maxRow, $"Row {maxRow} is past the end of the column for field '{_layout.Schema.Fields[field].Name}' ({target.Capacity} rows).");
        }
    }

    /// <summary>The unchecked loops index <paramref name="length"/>-long arrays by row; a column type whose array is shorter
    /// than its capacity would break that, so it fails here instead.</summary>
    private static void RequireLength(int length, long needed)
    {
        if (length < needed)
        {
            throw new InvalidOperationException($"A column array holds {length} elements but {needed} are needed for its capacity.");
        }
    }

    internal bool HasValue(int field, in LocalRecordRef record)
    {
        CheckField(field);
        return record.Page is { } page && IsValid(page, record.Offset, field);
    }

    internal int CountValues(int field, ReadOnlySpan<LocalRecordRef> records)
    {
        CheckField(field);
        var count = 0;
        foreach (ref readonly var record in records)
        {
            if (record.Page is { } page && IsValid(page, record.Offset, field))
            {
                count++;
            }
        }

        return count;
    }

    internal long VersionOf(in LocalRecordRef record) => record.Page is { } page
        ? BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(record.Offset + RecordLayout.VersionOffset))
        : throw new ArgumentException("The record was not found.", nameof(record));

    /// <summary>
    /// For cold paths (context features, replay recording, tests): lists are copied. Values are as stored: NaN and ±∞
    /// are not replaced, and a list's length is not checked (the reader applies the type).
    /// </summary>
    internal ScalarValue Read(int field, in LocalRecordRef record)
    {
        if (!HasValue(field, record))
        {
            return ScalarValue.Missing;
        }

        var page = record.Page!;
        var at = record.Offset + _layout.Offsets[field];
        switch (_layout.Kinds[field])
        {
            case StorageKind.Bool: return ScalarValue.FromBool(page[at] != 0);
            case StorageKind.Int32: return ScalarValue.FromInt64(BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at)));
            case StorageKind.Int64: return ScalarValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(at)));
            case StorageKind.Float32: return ScalarValue.FromFloat32(BinaryPrimitives.ReadSingleLittleEndian(page.AsSpan(at)));
            case StorageKind.Float64: return ScalarValue.FromFloat64(BinaryPrimitives.ReadDoubleLittleEndian(page.AsSpan(at)));
            case StorageKind.String: return ScalarValue.FromString(StringOf(BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at))));
            case StorageKind.List when _layout.Elements[field] != StorageKind.String:
                var elements = VarBytes(page, record.Offset, field, _layout.ElementSizes[field]);
                return _layout.Elements[field] switch
                {
                    StorageKind.Bool => ColumnValues.ListValue(MemoryMarshal.Cast<byte, bool>(elements)),
                    StorageKind.Int32 => ColumnValues.ListValue(MemoryMarshal.Cast<byte, int>(elements)),
                    StorageKind.Int64 => ColumnValues.ListValue(MemoryMarshal.Cast<byte, long>(elements)),
                    StorageKind.Float32 => ColumnValues.ListValue(MemoryMarshal.Cast<byte, float>(elements)),
                    _ => ColumnValues.ListValue(MemoryMarshal.Cast<byte, double>(elements)),
                };
            case StorageKind.List:
                var codes = MemoryMarshal.Cast<byte, int>(VarBytes(page, record.Offset, field, 4));
                var strings = new string[codes.Length];
                for (var i = 0; i < codes.Length; i++)
                {
                    strings[i] = StringOf(codes[i]);
                }

                return ScalarValue.FromStringList(strings);
            default:
                throw new InvalidOperationException($"Unsupported field kind {_layout.Kinds[field]}.");
        }
    }

    /// <summary>Position of an int64 key (0 = none). Terminates because an index is never more than half full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ProbeInt64(IndexTable index, long key) => ProbeInt64(index.Entries, index.Mask, index.Home((ulong)key), key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ProbeInt64(long[] entries, int mask, int slot, long key)
    {
        for (; ; slot = (slot + 1) & mask)
        {
            // Acquire: the key, page and record bytes this position refers to were written before it was published.
            var position = Volatile.Read(ref entries[(2 * slot) + 1]);
            if (position == 0 || entries[2 * slot] == key)
            {
                return position;
            }
        }
    }

    /// <summary>
    /// Position of a hashed key (0 = none) given by its canonical bytes, and the slot it was found in, or the empty
    /// slot that ends its probe sequence.
    /// </summary>
    internal long ProbeHashed(IndexTable index, ulong hash, ReadOnlySpan<byte> key, ref byte[][] pages, out int slot)
    {
        var entries = index.Entries;
        var mask = index.Mask;
        for (slot = index.Home(hash); ; slot = (slot + 1) & mask)
        {
            var position = Volatile.Read(ref entries[(2 * slot) + 1]);
            if (position == 0)
            {
                return 0;
            }

            if (entries[2 * slot] == (long)hash && KeyOf(PageOf(position, ref pages), (int)position).SequenceEqual(key))
            {
                return position;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LocalRecordRef Resolve(long position, ref byte[][] pages)
    {
        var page = PageOf(position, ref pages);
        var offset = (int)position;
        if ((page[offset + RecordLayout.FlagsOffset] & RecordLayout.TombstoneFlag) != 0)
        {
            return default;
        }

        // A record is never shorter than the fixed part, so every fixed-size read of Fill stays inside the page.
        if ((uint)offset + (uint)_layout.FixedSize > (uint)page.Length)
        {
            throw new InvalidOperationException($"Corrupt local table position {position:X}: the record does not fit its page.");
        }

        return new LocalRecordRef(page, offset);
    }

    /// <summary>
    /// The page of a published position. <paramref name="pages"/> is the caller's directory snapshot: a page number past
    /// its end was added (in a grown directory) after the snapshot was taken, and since the position was read with acquire
    /// semantics, loading the directory again yields one that contains it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] PageOf(long position, ref byte[][] pages)
    {
        var pageIndex = (int)(position >> 32) - 1;
        if ((uint)pageIndex >= (uint)pages.Length)
        {
            pages = Volatile.Read(ref _pages);
        }

        return pages[pageIndex];
    }

    internal ReadOnlySpan<byte> KeyOf(byte[] page, int offset)
    {
        var refAt = offset + _layout.KeyRefOffset;
        var keyOffset = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(refAt));
        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(refAt + 4));
        return page.AsSpan(offset + keyOffset, keyLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValid(byte[] page, int offset, int field) =>
        (page[offset + RecordLayout.ValidityOffset + (field >> 3)] & (1 << (field & 7))) != 0;

    private ReadOnlySpan<byte> VarBytes(byte[] page, int offset, int field, int elementSize)
    {
        var at = offset + _layout.Offsets[field];
        var relative = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at));
        var count = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at + 4));
        return page.AsSpan(offset + relative, count * elementSize);
    }

    /// <summary>
    /// The elements of a list field of fixed-size elements, or none when the record has no value: the record's own
    /// bytes, valid while the version is pinned.
    /// </summary>
    internal ReadOnlySpan<T> List<T>(int field, LocalRecordRef record)
        where T : unmanaged
    {
        if (!HasValue(field, record))
        {
            return default;
        }

        if (_layout.Kinds[field] != StorageKind.List || _layout.ElementSizes[field] != Unsafe.SizeOf<T>() || _layout.Elements[field] == StorageKind.String)
        {
            throw new ArgumentException($"Field {field} is not a list of {typeof(T).Name}.", nameof(field));
        }

        return MemoryMarshal.Cast<byte, T>(VarBytes(record.Page!, record.Offset, field, Unsafe.SizeOf<T>()));
    }

    internal ReadOnlySpan<int> StringCodes(int field, LocalRecordRef record)
    {
        if (!HasValue(field, record))
        {
            return default;
        }

        if (_layout.Kinds[field] != StorageKind.List || _layout.Elements[field] != StorageKind.String)
        {
            throw new ArgumentException($"Field {field} is not a list of strings.", nameof(field));
        }

        return MemoryMarshal.Cast<byte, int>(VarBytes(record.Page!, record.Offset, field, 4));
    }

    /// <summary>Codes in published records are always in the dictionary a later load returns.</summary>
    internal string StringOf(int code)
    {
        var strings = Volatile.Read(ref _strings);
        return strings[code];
    }

    private void CheckField(int field)
    {
        if ((uint)field >= (uint)_layout.FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, $"The table has {_layout.FieldCount} fields.");
        }
    }

    private void FillScalar<T>(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, int maxRow, ScalarColumn<T> column, in ScalarValue absent)
        where T : unmanaged
    {
        var values = column.Values;
        RequireLength(values.Length, maxRow + 1L);
        ref var value0 = ref MemoryMarshal.GetArrayDataReference(values);
        ref var record0 = ref MemoryMarshal.GetReference(records);
        ref var row0 = ref MemoryMarshal.GetReference(rows);
        var words = TargetValidity(column, absent.IsMissing, maxRow);
        var at = _layout.Offsets[field];
        var validityAt = RecordLayout.ValidityOffset + (field >> 3);
        var bit = 1 << (field & 7);
        var absentMissing = absent.IsMissing;
        var fallback = absentMissing ? default : Typed<T>(absent);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = Unsafe.Add(ref row0, i);
            ref readonly var record = ref Unsafe.Add(ref record0, i);
            if (record.Page is { } page)
            {
                ref var start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(page), record.Offset);
                if ((Unsafe.Add(ref start, validityAt) & bit) != 0)
                {
                    Unsafe.Add(ref value0, row) = Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref start, at));
                    MarkValid(words, row);
                    continue;
                }
            }

            if (absentMissing)
            {
                MarkMissing(words!, row);
            }
            else
            {
                Unsafe.Add(ref value0, row) = fallback;
                MarkValid(words, row);
            }
        }
    }

    private int FillFloat32(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, int maxRow, ScalarColumn<float> column, in ScalarValue absent)
    {
        var values = column.Values;
        RequireLength(values.Length, maxRow + 1L);
        ref var value0 = ref MemoryMarshal.GetArrayDataReference(values);
        ref var record0 = ref MemoryMarshal.GetReference(records);
        ref var row0 = ref MemoryMarshal.GetReference(rows);
        var words = TargetValidity(column, absent.IsMissing, maxRow);
        var at = _layout.Offsets[field];
        var validityAt = RecordLayout.ValidityOffset + (field >> 3);
        var bit = 1 << (field & 7);
        var absentMissing = absent.IsMissing;
        var fallback = absentMissing ? 0f : (float)absent.AsFloat64();
        var replaced = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = Unsafe.Add(ref row0, i);
            ref readonly var record = ref Unsafe.Add(ref record0, i);
            if (record.Page is { } page)
            {
                ref var start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(page), record.Offset);
                if ((Unsafe.Add(ref start, validityAt) & bit) != 0)
                {
                    var value = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref start, at));
                    if (float.IsFinite(value))
                    {
                        Unsafe.Add(ref value0, row) = value;
                        MarkValid(words, row);
                        continue;
                    }

                    replaced++;
                }
            }

            if (absentMissing)
            {
                MarkMissing(words!, row);
            }
            else
            {
                Unsafe.Add(ref value0, row) = fallback;
                MarkValid(words, row);
            }
        }

        return replaced;
    }

    private int FillFloat64(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, int maxRow, ScalarColumn<double> column, in ScalarValue absent)
    {
        var values = column.Values;
        RequireLength(values.Length, maxRow + 1L);
        ref var value0 = ref MemoryMarshal.GetArrayDataReference(values);
        ref var record0 = ref MemoryMarshal.GetReference(records);
        ref var row0 = ref MemoryMarshal.GetReference(rows);
        var words = TargetValidity(column, absent.IsMissing, maxRow);
        var at = _layout.Offsets[field];
        var validityAt = RecordLayout.ValidityOffset + (field >> 3);
        var bit = 1 << (field & 7);
        var absentMissing = absent.IsMissing;
        var fallback = absentMissing ? 0d : absent.AsFloat64();
        var replaced = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = Unsafe.Add(ref row0, i);
            ref readonly var record = ref Unsafe.Add(ref record0, i);
            if (record.Page is { } page)
            {
                ref var start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(page), record.Offset);
                if ((Unsafe.Add(ref start, validityAt) & bit) != 0)
                {
                    var value = Unsafe.ReadUnaligned<double>(ref Unsafe.Add(ref start, at));
                    if (double.IsFinite(value))
                    {
                        Unsafe.Add(ref value0, row) = value;
                        MarkValid(words, row);
                        continue;
                    }

                    replaced++;
                }
            }

            if (absentMissing)
            {
                MarkMissing(words!, row);
            }
            else
            {
                Unsafe.Add(ref value0, row) = fallback;
                MarkValid(words, row);
            }
        }

        return replaced;
    }

    private void FillString(
        int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, int maxRow, StringColumn column, in ScalarValue absent)
    {
        var strings = Volatile.Read(ref _strings);
        var values = column.Values;
        RequireLength(values.Length, maxRow + 1L);
        ref var value0 = ref MemoryMarshal.GetArrayDataReference(values);
        ref var record0 = ref MemoryMarshal.GetReference(records);
        ref var row0 = ref MemoryMarshal.GetReference(rows);
        var words = TargetValidity(column, absent.IsMissing, maxRow);
        var at = _layout.Offsets[field];
        var validityAt = RecordLayout.ValidityOffset + (field >> 3);
        var bit = 1 << (field & 7);
        var fallback = absent.IsMissing ? null : absent.AsString();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = Unsafe.Add(ref row0, i);
            ref readonly var record = ref Unsafe.Add(ref record0, i);
            if (record.Page is { } page)
            {
                ref var start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(page), record.Offset);
                if ((Unsafe.Add(ref start, validityAt) & bit) != 0)
                {
                    var code = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref start, at));
                    if ((uint)code >= (uint)strings.Length)
                    {
                        strings = Volatile.Read(ref _strings); // grown after this call's first load; see StringOf
                    }

                    Unsafe.Add(ref value0, row) = strings[code];
                    MarkValid(words, row);
                    continue;
                }
            }

            if (fallback is null)
            {
                MarkMissing(words!, row);
            }
            else
            {
                Unsafe.Add(ref value0, row) = fallback;
                MarkValid(words, row);
            }
        }
    }

    /// <summary>
    /// When absent rows are missing the bitmap is made up front, so the loop never calls out to allocate it (a call in
    /// the loop makes the JIT keep the loop's state on the stack); such a column has a bitmap even if no row turns out
    /// missing.
    /// </summary>
    private static ulong[]? TargetValidity(ColumnBuffer column, bool absentMissing, int maxRow)
    {
        var words = absentMissing ? column.EnsureValidity() : column.Validity;
        if (words is not null)
        {
            RequireLength(words.Length, (maxRow >> 6) + 1L);
        }

        return words;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkValid(ulong[]? words, int row)
    {
        if (words is not null)
        {
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(words), row >> 6) |= 1UL << row;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkMissing(ulong[] words, int row) =>
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(words), row >> 6) &= ~(1UL << row);

    private LocalFillCounts FillList<T>(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ListColumn<T> column, in ScalarValue absent)
        where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var type = column.Type;
        int nonFinite = 0, outOfRange = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (records[i].Page is { } page && IsValid(page, records[i].Offset, field))
            {
                var values = MemoryMarshal.Cast<byte, T>(VarBytes(page, records[i].Offset, field, size));
                if (!ColumnValues.FitsLength(values.Length, type))
                {
                    outOfRange++;
                }
                else if (AllFinite(values))
                {
                    column.Set(row, values);
                    continue;
                }
                else
                {
                    nonFinite++;
                }
            }

            WriteAbsent(column, row, absent);
        }

        return new LocalFillCounts(nonFinite, outOfRange);
    }

    private static bool AllFinite<T>(ReadOnlySpan<T> values)
        where T : unmanaged =>
        typeof(T) == typeof(float) ? ColumnValues.AllFinite(MemoryMarshal.Cast<T, float>(values))
        : typeof(T) != typeof(double) || ColumnValues.AllFinite(MemoryMarshal.Cast<T, double>(values));

    private int FillStringList(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ListColumn<string> column, in ScalarValue absent)
    {
        var strings = Volatile.Read(ref _strings);
        var type = column.Type;
        var outOfRange = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (records[i].Page is { } page && IsValid(page, records[i].Offset, field))
            {
                var codes = MemoryMarshal.Cast<byte, int>(VarBytes(page, records[i].Offset, field, 4));
                if (ColumnValues.FitsLength(codes.Length, type))
                {
                    var target = column.SetUninitialized(row, codes.Length);
                    for (var j = 0; j < codes.Length; j++)
                    {
                        var code = codes[j];
                        if ((uint)code >= (uint)strings.Length)
                        {
                            strings = Volatile.Read(ref _strings);
                        }

                        target[j] = strings[code];
                    }

                    continue;
                }

                outOfRange++;
            }

            WriteAbsent(column, row, absent);
        }

        return outOfRange;
    }

    private static void WriteAbsent(ColumnBuffer column, int row, in ScalarValue absent)
    {
        if (absent.IsMissing)
        {
            column.SetMissing(row);
        }
        else
        {
            ColumnValues.Set(column, row, absent);
        }
    }

    private static T Typed<T>(in ScalarValue value)
        where T : unmanaged
    {
        if (typeof(T) == typeof(bool))
        {
            var b = value.AsBool();
            return Unsafe.As<bool, T>(ref b);
        }

        if (typeof(T) == typeof(int))
        {
            var i = checked((int)value.AsInt64());
            return Unsafe.As<int, T>(ref i);
        }

        if (typeof(T) == typeof(long))
        {
            var l = value.AsInt64();
            return Unsafe.As<long, T>(ref l);
        }

        throw new NotSupportedException($"No scalar default for {typeof(T).Name}.");
    }

    // Writer side (single writer thread; see LocalTableWriter)

    // Writer-only position of the next SIEVE candidate in this version. Zero starts at the oldest insertion.
    internal long CacheHand { get; set; }

    internal IndexTable Index => _index;

    internal byte[][] Pages => _pages;

    internal string[] Strings => _strings;

    internal int StringCount => _stringCount;

    internal long StringBytes => _stringBytes;

    /// <summary>
    /// What the dictionary's strings that no current record references are charged: values only superseded records
    /// held, which a compaction drops with those records.
    /// </summary>
    internal long UnreferencedStringBytes => _unreferencedStringBytes;

    internal int PageCount => _pageCount;

    internal long Hash(ReadOnlySpan<byte> key) => (long)_hasher(key);

    internal KeyHasher Hasher => _hasher;

    internal int MaxPageSize => _maxPageSize;

    /// <summary>Writer-side probe of the current index: the key's position (0 = none) and its slot, or the empty slot
    /// where it would go.</summary>
    internal long ProbeForWrite(long indexKey, ReadOnlySpan<byte> hashedKey, out int slot)
    {
        var index = _index;
        if (_layout.HashedKeys)
        {
            var pages = _pages;
            return ProbeHashed(index, (ulong)indexKey, hashedKey, ref pages, out slot);
        }

        var entries = index.Entries;
        for (slot = index.Home((ulong)indexKey); ; slot = (slot + 1) & index.Mask)
        {
            var position = entries[(2 * slot) + 1];
            if (position == 0 || entries[2 * slot] == indexKey)
            {
                return position;
            }
        }
    }

    internal byte[] PageOf(long position) => _pages[(int)(position >> 32) - 1];

    internal long IndexGrowthBytes(bool newKey) =>
        newKey && _index.Count + 1 > _index.MaxCount ? IndexBytes(_index) : 0;

    internal long PageGrowthBytes(int recordBytes)
    {
        if (_currentPage is not null && _pageUsed + recordBytes <= _currentPage.Length)
        {
            return 0;
        }

        var directory = _pageCount == _pages.Length ? _pages.Length * 8L : 0;
        return NewPageSize(recordBytes) + directory;
    }

    /// <summary>
    /// Readers holding the old index keep a consistent (if no longer updated) snapshot.
    /// </summary>
    internal void EnsureIndexRoom()
    {
        var old = _index;
        if (old.Count + 1 <= old.MaxCount)
        {
            return;
        }

        var next = new IndexTable(old.SlotCount * 2);
        var entries = old.Entries;
        for (var s = 0; s < old.SlotCount; s++)
        {
            var position = entries[(2 * s) + 1];
            if (position != 0)
            {
                next.InsertUnpublished(entries[2 * s], position);
            }
        }

        Volatile.Write(ref _index, next);
        AddBytes(IndexBytes(next) - IndexBytes(old));
    }

    /// <summary>
    /// Publishes a key's record at <paramref name="slot"/> of the current index (found by <see cref="ProbeForWrite"/>
    /// after <see cref="EnsureIndexRoom"/>). For a new key the slot's key is written first and the position last, with
    /// release semantics, so a reader that sees the position sees the key and the record.
    /// </summary>
    internal void Publish(int slot, long indexKey, long position, bool newKey)
    {
        var index = _index;
        if (newKey)
        {
            index.Entries[2 * slot] = indexKey;
            index.Count++;
        }

        Volatile.Write(ref index.Entries[(2 * slot) + 1], position);
    }

    /// <summary>
    /// Space for a record of <paramref name="bytes"/> (a multiple of 8) at the end of the current page, or of a new
    /// page. A new page is added to the directory before any record in it is published.
    /// </summary>
    /// <param name="packFor">Set by a compaction: the bytes of this record and of every record it will still
    /// copy.</param>
    internal long Reserve(int bytes, out byte[] page, long packFor = 0)
    {
        if (_currentPage is null || _pageUsed + bytes > _currentPage.Length)
        {
            if (packFor > 0)
            {
                AddPage(new byte[(int)Math.Max(bytes, Math.Min(_maxPageSize, packFor))]);
            }
            else
            {
                AddPage(new byte[NewPageSize(bytes)]);
                if (_nextPageSize < _maxPageSize)
                {
                    _nextPageSize = Math.Min(_maxPageSize, _nextPageSize * 2);
                }
            }
        }

        page = _currentPage!;
        var offset = _pageUsed;
        _pageUsed += bytes;
        return ((long)_pageCount << 32) | (uint)offset; // page number + 1 in the high half: 0 is never a position
    }

    internal void AddRecordBytes(long bytes) => Volatile.Write(ref _recordBytes, _recordBytes + bytes);

    internal void AddSuperseded(long bytes) => Volatile.Write(ref _supersededBytes, _supersededBytes + bytes);

    internal void AddRecords(int records, int tombstones)
    {
        Volatile.Write(ref _records, _records + records);
        Volatile.Write(ref _tombstones, _tombstones + tombstones);
    }

    /// <summary>The code of a string, or -1 when the dictionary does not hold it.</summary>
    internal int FindString(ReadOnlySpan<char> value) => _codes.Find(value, _strings);

    /// <summary>
    /// Bytes that adding <paramref name="count"/> new strings, whose <see cref="StringObjectBytes"/> sum to
    /// <paramref name="objectBytes"/>, costs: the strings, plus every growth of the dictionary array and of its code
    /// map that the additions pass. <see cref="AddString"/> adds exactly this when called <paramref name="count"/>
    /// times.
    /// </summary>
    internal long StringAdditionBytes(int count, long objectBytes)
    {
        if (count == 0)
        {
            return 0;
        }

        var bytes = objectBytes;
        for (long capacity = _strings.Length; _stringCount + (long)count > capacity; capacity *= 2)
        {
            bytes += capacity * StringSlotBytes;
        }

        return bytes + _codes.GrowthBytes(count);
    }

    /// <summary>
    /// Adds a string to the dictionary; the record that brings it references it next (<see cref="Reference"/>). The
    /// element is stored before any record using the code is published.
    /// </summary>
    internal int AddString(string value)
    {
        var strings = _strings;
        if (_stringCount == strings.Length)
        {
            var grown = new string[strings.Length * 2];
            Array.Copy(strings, grown, _stringCount);
            grown[_stringCount] = value;
            Volatile.Write(ref _strings, grown);
            Array.Resize(ref _references, grown.Length);
            AddBytes((long)strings.Length * StringSlotBytes);
        }
        else
        {
            Volatile.Write(ref strings[_stringCount], value);
        }

        var code = _stringCount++;
        _references[code] = 0;
        var mapBefore = _codes.Bytes;
        _codes.Add(code, value, _strings);
        _stringBytes += StringObjectBytes(value.Length);
        AddBytes(StringObjectBytes(value.Length) + _codes.Bytes - mapBefore);
        return code;
    }

    /// <summary>
    /// Counts the string references of a record: <paramref name="delta"/> +1 when it becomes its key's current record
    /// (after its codes are written), −1 when a newer record or a tombstone supersedes it. Only the writer calls it;
    /// readers never see the counts.
    /// </summary>
    internal void Reference(byte[] page, int offset, int delta)
    {
        foreach (var field in _layout.DictionaryFields)
        {
            foreach (var code in _layout.Codes(page, offset, field))
            {
                ref var count = ref _references[code];
                if (delta > 0)
                {
                    if (count == Unreferenced)
                    {
                        count = 0;
                        _unreferencedStringBytes -= StringObjectBytes(_strings[code].Length);
                    }

                    count++;
                }
                else
                {
                    Debug.Assert(count > 0, "A superseded record referenced a string no current record held.");
                    if (--count == 0)
                    {
                        count = Unreferenced;
                        _unreferencedStringBytes += StringObjectBytes(_strings[code].Length);
                    }
                }
            }
        }
    }

    /// <summary>
    /// What dropping the record at <paramref name="offset"/> (a current record of this version) frees after the records
    /// already dropped: its bytes, and each dictionary string whose last reference it holds. <paramref name="dropped"/>
    /// counts, per code, the references of the records dropped so far, and this record's are added.
    /// </summary>
    internal long Drop(byte[] page, int offset, int[] dropped)
    {
        long bytes = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(offset + RecordLayout.LengthOffset));
        foreach (var field in _layout.DictionaryFields)
        {
            foreach (var code in _layout.Codes(page, offset, field))
            {
                if (++dropped[code] == _references[code])
                {
                    bytes += StringObjectBytes(_strings[code].Length);
                }
            }
        }

        return bytes;
    }

    /// <summary>Indexes an old page whose string codes need no remapping. Only new pages may receive appends.</summary>
    internal long AttachSealedPage(byte[] page)
    {
        AddPage(page);
        _pageUsed = page.Length;
        return (long)_pageCount << 32;
    }

    private void AddPage(byte[] page)
    {
        var directory = _pages;
        if (_pageCount == directory.Length)
        {
            var grown = new byte[directory.Length * 2][];
            Array.Copy(directory, grown, _pageCount);
            grown[_pageCount] = page;
            Volatile.Write(ref _pages, grown);
            AddBytes(directory.Length * 8L);
        }
        else
        {
            Volatile.Write(ref directory[_pageCount], page);
        }

        _pageCount++;
        _currentPage = page;
        _pageUsed = 0;
        AddBytes(page.Length);
    }

    private int NewPageSize(int recordBytes) => Math.Max(_nextPageSize, recordBytes);

    private void AddBytes(long delta) => Volatile.Write(ref _bytes, _bytes + delta);

    private static long IndexBytes(IndexTable index) => index.Entries.Length * 8L;

    /// <summary>What one dictionary string of <paramref name="length"/> chars is charged: the string object and a reference.</summary>
    internal static long StringObjectBytes(int length) => RecordLayout.Align(22L + (2L * length), 8) + 8;
}

/// <summary>
/// The open-addressing index of a <see cref="TableVersion"/>: slot <c>s</c> is <c>Entries[2s]</c> (the int64 key, or
/// the 64-bit hash of a string key) and <c>Entries[2s + 1]</c> (the record's position, 0 = empty). Entries are never
/// removed (deletes are tombstone records), and the table is kept at most half full, so every probe sequence ends at an
/// empty slot.
/// </summary>
internal sealed class IndexTable
{
    public const int MinSlots = 16;
    public const int MaxSlots = 1 << 29;

    public IndexTable(int slots)
    {
        if (slots > MaxSlots)
        {
            throw new InvalidOperationException($"A local table holds at most {MaxSlots / 2} keys.");
        }

        Entries = new long[2 * slots];
        Mask = slots - 1;
        Shift = 64 - BitOperations.Log2((uint)slots);
        MaxCount = slots / 2;
    }

    public long[] Entries { get; }

    public int Mask { get; }

    public int Shift { get; }

    public int MaxCount { get; }

    public int SlotCount => Mask + 1;

    /// <summary>Keys in the index (writer-only).</summary>
    public int Count { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Home(ulong key) => (int)((key * TableVersion.Fibonacci) >> Shift);

    public static int SlotsFor(int keys) =>
        keys <= MinSlots / 2 ? MinSlots : (int)Math.Min(MaxSlots * 2L, BitOperations.RoundUpToPowerOf2((ulong)keys * 2));

    /// <summary>Inserts a key known to be absent into an index no reader can see yet.</summary>
    public void InsertUnpublished(long key, long position)
    {
        var slot = Home((ulong)key);
        while (Entries[(2 * slot) + 1] != 0)
        {
            slot = (slot + 1) & Mask;
        }

        Entries[2 * slot] = key;
        Entries[(2 * slot) + 1] = position;
        Count++;
    }
}

/// <summary>
/// The writer's string → code map over a version's dictionary (readers only ever go from code to string).
/// </summary>
internal sealed class StringCodeMap
{
    private int[] _slots = new int[16]; // code + 1; 0 = empty
    private int _count;

    public long Bytes => _slots.Length * 4L;

    public long GrowthBytes(int additions)
    {
        long bytes = 0;
        for (long slots = _slots.Length; _count + (long)additions > slots / 2; slots *= 2)
        {
            bytes += slots * 4;
        }

        return bytes;
    }

    public int Find(ReadOnlySpan<char> value, string[] strings)
    {
        var mask = _slots.Length - 1;
        for (var slot = Home(value, mask); ; slot = (slot + 1) & mask)
        {
            var code = _slots[slot] - 1;
            if (code < 0)
            {
                return -1;
            }

            if (strings[code].AsSpan().SequenceEqual(value))
            {
                return code;
            }
        }
    }

    public void Add(int code, string value, string[] strings)
    {
        if (_count + 1 > _slots.Length / 2)
        {
            var old = _slots;
            _slots = new int[old.Length * 2];
            foreach (var entry in old)
            {
                if (entry != 0)
                {
                    Insert(entry, strings[entry - 1]);
                }
            }
        }

        Insert(code + 1, value);
        _count++;
    }

    private void Insert(int entry, string value)
    {
        var mask = _slots.Length - 1;
        var slot = Home(value, mask);
        while (_slots[slot] != 0)
        {
            slot = (slot + 1) & mask;
        }

        _slots[slot] = entry;
    }

    private static int Home(ReadOnlySpan<char> value, int mask) =>
        (int)(((ulong)(uint)string.GetHashCode(value) * TableVersion.Fibonacci) >> 32) & mask;
}

internal static class KeyHashes
{
    public static readonly KeyHasher Default = static key => XxHash3.HashToUInt64(key);
}
