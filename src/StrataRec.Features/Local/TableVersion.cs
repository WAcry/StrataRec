using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Features.Local;

/// <summary>Hashes a string key to the 64-bit value the index stores (injectable so tests can force collisions).</summary>
internal delegate ulong StringKeyHasher(ReadOnlySpan<char> key);

/// <summary>
/// A found record of a pinned <see cref="TableVersion"/>, or none (<see cref="Found"/> false). It points straight at
/// the record's bytes, so reading its features after the one index lookup touches nothing else.
/// </summary>
/// <remarks>Only meaningful for the version whose lookup produced it; keep it no longer than the request that pinned the
/// version (it holds the page alive).</remarks>
public readonly struct LocalRecordRef
{
    internal readonly byte[]? Page;
    internal readonly int Offset;

    internal LocalRecordRef(byte[] page, int offset)
    {
        Page = page;
        Offset = offset;
    }

    public bool Found => Page is not null;
}

/// <summary>One field to read in a multi-field <see cref="TableVersion.Fill(ReadOnlySpan{LocalRecordRef}, ReadOnlySpan{int}, ReadOnlySpan{LocalFieldRead}, Span{int})"/>:
/// the table field, the column of its storage type to write, and what absent rows get (the feature's default, or missing).</summary>
public readonly struct LocalFieldRead
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

/// <summary>
/// One generation of a <see cref="LocalFeatureTable"/>: pages of records, the index over them and the string dictionary
/// (design 6.6). A request pins the table's current version once and does every lookup against it; a compaction or reload
/// builds a new version and swaps it in, and the old one is left to the garbage collector.
/// </summary>
/// <remarks>
/// <para><b>Reading is lock-free; see <see cref="LocalFeatureTable"/> for the whole protocol.</b> The reader side in short:
/// load the index object once (any index object is a consistent snapshot), read a slot's position with acquire
/// semantics, and only then read the slot's key, the page directory and the record bytes. A page directory loaded
/// earlier is reused while the page number fits it; a position that does not fit it was published after a directory
/// growth, so the directory is loaded again. The string dictionary is loaded after the lookups, with the same rule.</para>
/// <para>The writer side lives here too (internal members, used only by <see cref="LocalTableWriter"/> on the table's
/// single writer thread): page allocation, the index, the dictionary and the size accounting.</para>
/// </remarks>
public sealed class TableVersion
{
    internal const ulong Fibonacci = 0x9E3779B97F4A7C15UL;
    private const int InitialDirectoryLength = 8;
    private const int InitialStringsLength = 16;
    private const int MinPageSize = 4096;

    private readonly RecordLayout _layout;
    private readonly StringKeyHasher _hasher;
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
    private readonly StringCodeMap _codes;

    internal TableVersion(RecordLayout layout, StringKeyHasher hasher, int maxPageSize, long generation, int expectedKeys, long expectedBytes)
    {
        _layout = layout;
        _hasher = hasher;
        _maxPageSize = maxPageSize;
        Generation = generation;
        _index = new IndexTable(IndexTable.SlotsFor(expectedKeys));
        _pages = new byte[InitialDirectoryLength][];
        _strings = new string[InitialStringsLength];
        _codes = new StringCodeMap();
        // A compaction knows how much it will copy; a fresh table starts with a small page.
        var wanted = expectedBytes <= MinPageSize ? MinPageSize : BitOperations.RoundUpToPowerOf2((ulong)expectedBytes);
        _nextPageSize = (int)Math.Min((ulong)maxPageSize, (ulong)wanted);
        _bytes = IndexBytes(_index) + (_pages.Length * 8L) + (_strings.Length * 8L) + _codes.Bytes;
    }

    /// <summary>Increases by one with every version a table builds (compaction, reload).</summary>
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

    // ---------------------------------------------------------------------------------------------------------------
    // Reader API
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The record of an int64 key, or none. A string-keyed table never finds an int64 key.</summary>
    public LocalRecordRef Find(long key)
    {
        if (_layout.StringKeys)
        {
            return default;
        }

        var pages = Volatile.Read(ref _pages);
        var position = ProbeInt64(Volatile.Read(ref _index), key);
        return position == 0 ? default : Resolve(position, ref pages);
    }

    /// <summary>The record of a string key, or none. An int64-keyed table never finds a string key.</summary>
    public LocalRecordRef Find(ReadOnlySpan<char> key)
    {
        if (!_layout.StringKeys)
        {
            return default;
        }

        var pages = Volatile.Read(ref _pages);
        var position = ProbeString(Volatile.Read(ref _index), _hasher(key), key, ref pages, out _);
        return position == 0 ? default : Resolve(position, ref pages);
    }

    public LocalRecordRef Find(in EntityKey key) => key.StringValue is { } s ? Find(s.AsSpan()) : Find(key.Int64Value);

    /// <summary>
    /// Looks every key up once: <c>records[i]</c> becomes the record of <c>keys[i]</c> or none. All features of the
    /// table are then read from these references (<see cref="Fill(ReadOnlySpan{LocalRecordRef}, ReadOnlySpan{int}, ReadOnlySpan{LocalFieldRead}, Span{int})"/>). Returns the number of keys found.
    /// </summary>
    /// <remarks>
    /// Keys are taken in blocks of <see cref="LookupBlock"/>, in two passes. The first reads every key's home slot and
    /// nothing else, so no branch waits on a cache miss and the misses of the whole block overlap. The second finishes
    /// each probe (the slots are in the cache now; a probe that goes past the home slot mispredicts a branch but no
    /// longer stalls on memory) and resolves the record, whose header miss again overlaps with the other keys'.
    /// </remarks>
    public int Lookup(ReadOnlySpan<long> keys, Span<LocalRecordRef> records)
    {
        records = records[..keys.Length];
        if (_layout.StringKeys)
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

    /// <summary>String-key form of <see cref="Lookup(ReadOnlySpan{long}, Span{LocalRecordRef})"/>; a null key (a row with
    /// no key) is not found.</summary>
    public int Lookup(ReadOnlySpan<string?> keys, Span<LocalRecordRef> records)
    {
        records = records[..keys.Length];
        if (!_layout.StringKeys)
        {
            records.Clear();
            return 0;
        }

        var index = Volatile.Read(ref _index);
        var pages = Volatile.Read(ref _pages);
        var found = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var position = key is null ? 0 : ProbeString(index, _hasher(key), key, ref pages, out _);
            records[i] = position == 0 ? default : Resolve(position, ref pages);
            found += records[i].Found ? 1 : 0;
        }

        return found;
    }

    /// <summary>Mixed-key form, for keys already held as <see cref="EntityKey"/>s; a key of the other type is not found.</summary>
    public int Lookup(ReadOnlySpan<EntityKey> keys, Span<LocalRecordRef> records)
    {
        records = records[..keys.Length];
        var index = Volatile.Read(ref _index);
        var pages = Volatile.Read(ref _pages);
        var found = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            long position;
            if (keys[i].StringValue is { } s)
            {
                position = _layout.StringKeys ? ProbeString(index, _hasher(s), s, ref pages, out _) : 0;
            }
            else
            {
                position = _layout.StringKeys ? 0 : ProbeInt64(index, keys[i].Int64Value);
            }

            records[i] = position == 0 ? default : Resolve(position, ref pages);
            found += records[i].Found ? 1 : 0;
        }

        return found;
    }

    /// <summary>
    /// Writes field <paramref name="field"/> of each record into row <c>rows[i]</c> of <paramref name="target"/>, a
    /// column of the field's storage type (the slot buffer of a candidate table).
    /// </summary>
    /// <param name="records">References from <see cref="Lookup(ReadOnlySpan{long}, Span{LocalRecordRef})"/> on this version, one per row.</param>
    /// <param name="rows">Physical rows of <paramref name="target"/>, one per record.</param>
    /// <param name="absent">
    /// What a row gets when its key has no record or the record has no value for the field: the feature's default, or
    /// <see cref="ScalarValue.Missing"/> (validity bit cleared). Never a zero (design 6.7).
    /// </param>
    /// <param name="filled">The slot's fill bitmap; when not empty, every row in <paramref name="rows"/> is marked filled.</param>
    /// <returns>
    /// The number of NaN/±∞ values replaced by <paramref name="absent"/>. Like backfill (design 4.3), stored
    /// <c>float32</c>/<c>float64</c> values that are not finite are not served; vector components are copied as stored.
    /// </returns>
    /// <remarks>
    /// <para>Allocates nothing except where the column type requires it: a <c>text</c> value becomes a new string, and a
    /// string column's dictionary grows by the strings it has not seen. When <paramref name="absent"/> is missing, the
    /// target gets a validity bitmap (rented from its request memory) before the first row, even if every row turns out
    /// present: the loop then never calls out, which keeps its state in registers.</para>
    /// <para>Rows and records are checked once per call (rows inside the target, records from a version of this layout);
    /// the per-row loop itself is unchecked.</para>
    /// </remarks>
    public int Fill(
        int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ColumnBuffer target, in ScalarValue absent, Span<ulong> filled = default)
    {
        var maxRow = CheckRows(records, rows);
        CheckTarget(field, target, maxRow);
        var stringMemo = _layout.Kinds[field] == StorageKind.String ? NewStringMemo(stackalloc int[StringMemoLength]) : default;
        var replaced = FillField(field, records, rows, maxRow, target, absent, stringMemo);
        if (!filled.IsEmpty)
        {
            foreach (var row in rows)
            {
                Bits.Set(filled, row);
            }
        }

        return replaced;
    }

    /// <summary>
    /// Fills several fields of the same records: what a request does for all features of a table after one lookup. Same
    /// semantics as <see cref="Fill(int, ReadOnlySpan{LocalRecordRef}, ReadOnlySpan{int}, ColumnBuffer, in ScalarValue, Span{ulong})"/>
    /// per field, but the rows are taken in blocks of <see cref="FillBlock"/> and every field is filled for a block before
    /// the next, so the block's records are read from the L1 cache by every field instead of from memory once per field.
    /// </summary>
    /// <param name="nonFinite">When not empty, <c>nonFinite[r]</c> receives the number of NaN/±∞ values of <c>reads[r]</c> that
    /// were replaced by its absent value.</param>
    /// <remarks>Fill bits are left to the caller: every row in <paramref name="rows"/> is filled for every field, so one
    /// bitmap of the rows can be OR-ed into each slot's fill bitmap.</remarks>
    public void Fill(ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ReadOnlySpan<LocalFieldRead> reads, Span<int> nonFinite = default)
    {
        var maxRow = CheckRows(records, rows);
        if (!nonFinite.IsEmpty)
        {
            if (nonFinite.Length < reads.Length)
            {
                throw new ArgumentException($"{nonFinite.Length} counters for {reads.Length} reads.", nameof(nonFinite));
            }

            nonFinite[..reads.Length].Clear();
        }

        // The part of a record the reads touch: the header (validity bits) up to the end of the last field read.
        var readEnd = RecordLayout.ValidityOffset + _layout.ValidityBytes;
        var stringReads = 0;
        foreach (var read in reads)
        {
            CheckTarget(read.Field, read.Target, maxRow);
            readEnd = Math.Max(readEnd, _layout.Offsets[read.Field] + _layout.Sizes[read.Field]);
            stringReads += _layout.Kinds[read.Field] == StorageKind.String ? 1 : 0;
        }

        // One memo per string read, up to MaxStringMemos; a read past those gets a fresh memo per block.
        var memos = stringReads == 0 ? default : NewStringMemo(stackalloc int[Math.Min(stringReads, MaxStringMemos) * StringMemoLength]);
        Span<int> blockMemo = stringReads > MaxStringMemos ? stackalloc int[StringMemoLength] : default;
        for (var start = 0; start < rows.Length; start += FillBlock)
        {
            var count = Math.Min(FillBlock, rows.Length - start);
            var blockRecords = records.Slice(start, count);
            var blockRows = rows.Slice(start, count);
            Touch(blockRecords, readEnd);
            var stringRead = 0;
            for (var r = 0; r < reads.Length; r++)
            {
                scoped Span<int> memo = default;
                if (_layout.Kinds[reads[r].Field] == StorageKind.String)
                {
                    memo = stringRead < MaxStringMemos ? memos.Slice(stringRead * StringMemoLength, StringMemoLength) : NewStringMemo(blockMemo);
                    stringRead++;
                }

                var replaced = FillField(reads[r].Field, blockRecords, blockRows, maxRow, reads[r].Target, reads[r].Absent, memo);
                if (!nonFinite.IsEmpty)
                {
                    nonFinite[r] += replaced;
                }
            }
        }
    }

    /// <summary>
    /// Rows per block of the multi-field <see cref="Fill(ReadOnlySpan{LocalRecordRef}, ReadOnlySpan{int}, ReadOnlySpan{LocalFieldRead}, Span{int})"/>:
    /// the headers and scalar values of 128 records (two or three cache lines each) fit the L1 data cache.
    /// </summary>
    public const int FillBlock = 128;

    private const int StringMemoSize = 256;
    private const int StringMemoLength = 2 * StringMemoSize;
    private const int MaxStringMemos = 4;

    /// <summary>Clears a string memo (<see cref="StringMemoLength"/> ints per read): no table code is mapped yet.</summary>
    private static Span<int> NewStringMemo(Span<int> memo)
    {
        memo.Fill(-1);
        return memo;
    }

    /// <summary>
    /// Reads one byte of every cache line of <c>[0, end)</c> of each record, and nothing depends on the bytes but the
    /// returned sum: the misses of all records of a block are then in flight at once, where the field loops, whose branches
    /// wait on the loaded validity bits, would overlap only a few. The sum is returned, and the method not inlined, so the
    /// loads are not removed as dead code; callers ignore it.
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

    private int FillField(
        int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, int maxRow, ColumnBuffer target, in ScalarValue absent, Span<int> stringMemo)
    {
        switch (_layout.Kinds[field])
        {
            case StorageKind.Bool: FillScalar<bool>(field, records, rows, maxRow, (ScalarColumn<bool>)target, absent); return 0;
            case StorageKind.Int32: FillScalar<int>(field, records, rows, maxRow, (ScalarColumn<int>)target, absent); return 0;
            case StorageKind.Int64: FillScalar<long>(field, records, rows, maxRow, (ScalarColumn<long>)target, absent); return 0;
            case StorageKind.Float32: return FillFloat32(field, records, rows, maxRow, (ScalarColumn<float>)target, absent);
            case StorageKind.Float64: return FillFloat64(field, records, rows, maxRow, (ScalarColumn<double>)target, absent);
            case StorageKind.String: FillString(field, records, rows, maxRow, (DictStringColumn)target, absent, stringMemo); return 0;
            case StorageKind.Text: FillText(field, records, rows, (TextColumn)target, absent); return 0;
            case StorageKind.Vector: FillVector(field, records, rows, maxRow, (VectorColumn)target, absent); return 0;
            case StorageKind.ListInt32: FillList<int>(field, records, rows, (ListColumn<int>)target, absent); return 0;
            case StorageKind.ListInt64: FillList<long>(field, records, rows, (ListColumn<long>)target, absent); return 0;
            case StorageKind.ListString: FillStringList(field, records, rows, (ListColumn<string>)target, absent); return 0;
            default: throw new InvalidOperationException($"Unsupported field kind {_layout.Kinds[field]}.");
        }
    }

    /// <summary>
    /// Everything the unchecked fill loops rely on, checked once per call: one record per row, rows not negative, and
    /// every record's fixed part inside its page (a reference from another table's version could otherwise point past
    /// the end). Returns the largest row, or -1 when there are none; each target is checked against it.
    /// </summary>
    private int CheckRows(ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows)
    {
        if (records.Length != rows.Length)
        {
            throw new ArgumentException($"{records.Length} records for {rows.Length} rows; pass one record per row.", nameof(rows));
        }

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
        if (target.Type.Kind != kind || (kind == StorageKind.Vector && target.Type.Dimension != _layout.Dimensions[field]))
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

    /// <summary>True when the record exists and has a value for the field.</summary>
    public bool HasValue(int field, in LocalRecordRef record)
    {
        CheckField(field);
        return record.Page is { } page && IsValid(page, record.Offset, field);
    }

    /// <summary>The record's version: the event version (<c>local</c>) or the write time (<c>cache</c>).</summary>
    public long VersionOf(in LocalRecordRef record) => record.Page is { } page
        ? BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(record.Offset + RecordLayout.VersionOffset))
        : throw new ArgumentException("The record was not found.", nameof(record));

    /// <summary>
    /// The field's stored value as a promoted scalar (int32 → int64, float32 → float64, list&lt;int32&gt; → list&lt;int64&gt;),
    /// or missing. For cold paths (context features, replay recording, tests): vectors, lists and text are copied. Values
    /// are as stored: NaN and ±∞ are not replaced.
    /// </summary>
    public ScalarValue Read(int field, in LocalRecordRef record)
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
            case StorageKind.Float32: return ScalarValue.FromFloat64(BinaryPrimitives.ReadSingleLittleEndian(page.AsSpan(at)));
            case StorageKind.Float64: return ScalarValue.FromFloat64(BinaryPrimitives.ReadDoubleLittleEndian(page.AsSpan(at)));
            case StorageKind.String: return ScalarValue.FromString(StringOf(BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at))));
            case StorageKind.Text: return ScalarValue.FromText(new string(MemoryMarshal.Cast<byte, char>(VarBytes(page, record.Offset, field, 2))));
            case StorageKind.Vector:
                return ScalarValue.FromVector(MemoryMarshal.Cast<byte, float>(page.AsSpan(at, 4 * _layout.Dimensions[field])).ToArray());
            case StorageKind.ListInt32:
                return ScalarValue.FromInt64List(Int32Range.ToInt64Array(MemoryMarshal.Cast<byte, int>(VarBytes(page, record.Offset, field, 4))));
            case StorageKind.ListInt64:
                return ScalarValue.FromInt64List(MemoryMarshal.Cast<byte, long>(VarBytes(page, record.Offset, field, 8)).ToArray());
            case StorageKind.ListString:
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

    // ---------------------------------------------------------------------------------------------------------------
    // Reader internals
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Position of an int64 key (0 = none). Terminates because an index is never more than half full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ProbeInt64(IndexTable index, long key) => ProbeInt64(index.Entries, index.Mask, index.Home((ulong)key), key);

    /// <summary>Position of an int64 key, probing from <paramref name="slot"/> on.</summary>
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
    /// Position of a string key (0 = none) and the slot it was found in, or the empty slot that ends its probe sequence.
    /// The index holds the key's 64-bit hash; a hash match is confirmed against the key stored in the record, so two keys
    /// with the same hash are told apart.
    /// </summary>
    internal long ProbeString(IndexTable index, ulong hash, ReadOnlySpan<char> key, ref byte[][] pages, out int slot)
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

    /// <summary>The record at a published position, or none for a tombstone.</summary>
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

    /// <summary>The UTF-16 key stored in a string-keyed table's record or tombstone.</summary>
    internal ReadOnlySpan<char> KeyOf(byte[] page, int offset)
    {
        var refAt = offset + _layout.KeyRefOffset;
        var keyOffset = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(refAt));
        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(refAt + 4));
        return MemoryMarshal.Cast<byte, char>(page.AsSpan(offset + keyOffset, keyLength * 2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValid(byte[] page, int offset, int field) =>
        (page[offset + RecordLayout.ValidityOffset + (field >> 3)] & (1 << (field & 7))) != 0;

    /// <summary>Bounds-checked bytes of a variable-length field.</summary>
    private ReadOnlySpan<byte> VarBytes(byte[] page, int offset, int field, int elementSize)
    {
        var at = offset + _layout.Offsets[field];
        var relative = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at));
        var count = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at + 4));
        return page.AsSpan(offset + relative, count * elementSize);
    }

    /// <summary>A string by code. Codes in published records are always in the dictionary a later load returns.</summary>
    private string StringOf(int code)
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

    // The typed loops below index records, rows and column arrays without bounds checks: CheckRows and CheckTarget
    // checked once per call that every row is in [0, Capacity) and every record's fixed part is inside its page, and
    // each loop checks its column array against the capacity (RequireLength). Variable-length data is still read
    // through bounds-checked spans.

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

    /// <summary>
    /// String codes are translated from the table's dictionary to the column's. A small direct-mapped memo (table code to
    /// column code, <see cref="StringMemoLength"/> ints from <see cref="NewStringMemo"/>) skips the column dictionary's hash
    /// lookup for repeated values, the common case for low-cardinality strings. It belongs to one target column and lives
    /// for the whole fill call, across blocks.
    /// </summary>
    private void FillString(
        int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, int maxRow, DictStringColumn column, in ScalarValue absent, Span<int> memo)
    {
        var memoTable = memo[..StringMemoSize];
        var memoColumn = memo.Slice(StringMemoSize, StringMemoSize);
        var strings = Volatile.Read(ref _strings);
        var codes = column.Codes;
        RequireLength(codes.Length, maxRow + 1L);
        ref var code0 = ref MemoryMarshal.GetArrayDataReference(codes);
        ref var record0 = ref MemoryMarshal.GetReference(records);
        ref var row0 = ref MemoryMarshal.GetReference(rows);
        var words = TargetValidity(column, absent.IsMissing, maxRow);
        var at = _layout.Offsets[field];
        var validityAt = RecordLayout.ValidityOffset + (field >> 3);
        var bit = 1 << (field & 7);
        var absentCode = absent.IsMissing ? -1 : column.GetOrAddCode(absent.AsString());
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
                    var slot = code & (StringMemoSize - 1);
                    if (memoTable[slot] != code)
                    {
                        if ((uint)code >= (uint)strings.Length)
                        {
                            strings = Volatile.Read(ref _strings); // grown after this call's first load; see StringOf
                        }

                        memoTable[slot] = code;
                        memoColumn[slot] = column.GetOrAddCode(strings[code]);
                    }

                    Unsafe.Add(ref code0, row) = memoColumn[slot];
                    MarkValid(words, row);
                    continue;
                }
            }

            if (absentCode < 0)
            {
                MarkMissing(words!, row);
            }
            else
            {
                Unsafe.Add(ref code0, row) = absentCode;
                MarkValid(words, row);
            }
        }
    }

    private void FillText(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, TextColumn column, in ScalarValue absent)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (records[i].Page is { } page && IsValid(page, records[i].Offset, field))
            {
                column.Set(row, new string(MemoryMarshal.Cast<byte, char>(VarBytes(page, records[i].Offset, field, 2))));
            }
            else
            {
                WriteAbsent(column, row, absent);
            }
        }
    }

    private void FillVector(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, int maxRow, VectorColumn column, in ScalarValue absent)
    {
        var dimension = _layout.Dimensions[field];
        var flat = column.Flat;
        RequireLength(flat.Length, (maxRow + 1L) * dimension);
        ref var flat0 = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetArrayDataReference(flat));
        ref var record0 = ref MemoryMarshal.GetReference(records);
        ref var row0 = ref MemoryMarshal.GetReference(rows);
        var words = TargetValidity(column, absent.IsMissing, maxRow);
        var at = _layout.Offsets[field];
        var validityAt = RecordLayout.ValidityOffset + (field >> 3);
        var bit = 1 << (field & 7);
        var bytes = 4 * dimension;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = Unsafe.Add(ref row0, i);
            ref readonly var record = ref Unsafe.Add(ref record0, i);
            if (record.Page is { } page)
            {
                ref var start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(page), record.Offset);
                if ((Unsafe.Add(ref start, validityAt) & bit) != 0)
                {
                    Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref flat0, (nint)row * bytes), ref Unsafe.Add(ref start, at), (uint)bytes);
                    MarkValid(words, row);
                    continue;
                }
            }

            if (absent.IsMissing)
            {
                MarkMissing(words!, row);
            }
            else
            {
                ColumnValues.Set(column, row, absent);
            }
        }
    }

    /// <summary>
    /// A target column's validity bitmap for an unchecked fill loop, its length checked once against the largest row.
    /// When absent rows are missing the bitmap is made up front, so the loop never calls out to allocate it (a call in the
    /// loop makes the JIT keep the loop's state on the stack); such a column has a bitmap even if no row turns out
    /// missing. Otherwise it is the column's bitmap, or null while every row is valid.
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

    private void FillList<T>(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ListColumn<T> column, in ScalarValue absent)
        where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (records[i].Page is { } page && IsValid(page, records[i].Offset, field))
            {
                column.Set(row, MemoryMarshal.Cast<byte, T>(VarBytes(page, records[i].Offset, field, size)));
            }
            else
            {
                WriteAbsent(column, row, absent);
            }
        }
    }

    private void FillStringList(int field, ReadOnlySpan<LocalRecordRef> records, ReadOnlySpan<int> rows, ListColumn<string> column, in ScalarValue absent)
    {
        var strings = Volatile.Read(ref _strings);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (records[i].Page is { } page && IsValid(page, records[i].Offset, field))
            {
                var codes = MemoryMarshal.Cast<byte, int>(VarBytes(page, records[i].Offset, field, 4));
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
            }
            else
            {
                WriteAbsent(column, row, absent);
            }
        }
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

    // ---------------------------------------------------------------------------------------------------------------
    // Writer side (single writer thread; see LocalTableWriter)
    // ---------------------------------------------------------------------------------------------------------------

    internal IndexTable Index => _index;

    internal byte[][] Pages => _pages;

    internal string[] Strings => _strings;

    internal int StringCount => _stringCount;

    internal int PageCount => _pageCount;

    internal long Hash(ReadOnlySpan<char> key) => (long)_hasher(key);

    internal StringKeyHasher Hasher => _hasher;

    internal int MaxPageSize => _maxPageSize;

    /// <summary>Writer-side probe of the current index: the key's position (0 = none) and its slot, or the empty slot
    /// where it would go.</summary>
    internal long ProbeForWrite(long indexKey, ReadOnlySpan<char> stringKey, out int slot)
    {
        var index = _index;
        if (_layout.StringKeys)
        {
            var pages = _pages;
            return ProbeString(index, (ulong)indexKey, stringKey, ref pages, out slot);
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

    /// <summary>Bytes the index would add by growing for one more key, or 0 when it has room.</summary>
    internal long IndexGrowthBytes(bool newKey) =>
        newKey && _index.Count + 1 > _index.MaxCount ? IndexBytes(_index) : 0;

    /// <summary>Bytes a new page for a record of <paramref name="recordBytes"/> would add, or 0 when it fits the current page.</summary>
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
    /// Makes room for one more key: when the index would pass half full, builds a twice larger index and publishes it
    /// whole. Readers holding the old index keep a consistent (if no longer updated) snapshot.
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

        // Release: the new index's entries are complete before any reader can load it.
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
    /// Space for a record of <paramref name="bytes"/> (a multiple of 8) at the end of the current page, or of a new page.
    /// A new page is added to the directory before any record in it is published: in place when the directory has room
    /// (a reader that later sees a position in it reads the element after the position's acquire), otherwise in a grown
    /// copy of the directory that is published whole.
    /// </summary>
    internal long Reserve(int bytes, out byte[] page)
    {
        if (_currentPage is null || _pageUsed + bytes > _currentPage.Length)
        {
            AddPage(bytes);
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

    /// <summary>Bytes adding a string of <paramref name="length"/> chars would cost (dictionary growth included).</summary>
    internal long StringGrowthBytes(int length) =>
        StringObjectBytes(length) + (_stringCount == _strings.Length ? _strings.Length * 8L : 0) + _codes.GrowthBytes();

    /// <summary>
    /// Adds a string to the dictionary and returns its code. The element is stored before any record using the code is
    /// published; a full dictionary is copied into a grown array that is published whole.
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
            AddBytes(strings.Length * 8L);
        }
        else
        {
            Volatile.Write(ref strings[_stringCount], value);
        }

        var code = _stringCount++;
        var mapBefore = _codes.Bytes;
        _codes.Add(code, value, _strings);
        _stringBytes += StringObjectBytes(value.Length);
        AddBytes(StringObjectBytes(value.Length) + _codes.Bytes - mapBefore);
        return code;
    }

    private void AddPage(int recordBytes)
    {
        var page = new byte[NewPageSize(recordBytes)];
        if (_nextPageSize < _maxPageSize)
        {
            _nextPageSize = Math.Min(_maxPageSize, _nextPageSize * 2);
        }

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

    /// <summary>The next page's size; a record larger than the largest page gets a page of its own.</summary>
    private int NewPageSize(int recordBytes) => Math.Max(_nextPageSize, recordBytes);

    private void AddBytes(long delta) => Volatile.Write(ref _bytes, _bytes + delta);

    private static long IndexBytes(IndexTable index) => index.Entries.Length * 8L;

    private static long StringObjectBytes(int length) => RecordLayout.Align(22L + (2L * length), 8) + 8;
}

/// <summary>
/// The open-addressing index of a <see cref="TableVersion"/>: slot <c>s</c> is <c>Entries[2s]</c> (the int64 key, or
/// the 64-bit hash of a string key) and <c>Entries[2s + 1]</c> (the record's position, 0 = empty). Key and position share
/// a cache line, so a lookup costs one miss in the index. Entries are never removed (deletes are tombstone records), and
/// the table is kept at most half full, so every probe sequence ends at an empty slot.
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
/// The writer's string → code map over a version's dictionary (readers only ever go from code to string). Keyed by
/// chars, so a source decoding a value that is already in the dictionary needs no string.
/// </summary>
internal sealed class StringCodeMap
{
    private int[] _slots = new int[16]; // code + 1; 0 = empty
    private int _count;

    public long Bytes => _slots.Length * 4L;

    public long GrowthBytes() => _count + 1 > _slots.Length / 2 ? _slots.Length * 4L : 0;

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

/// <summary>The default string-key hash: XXH3-64 of the key's UTF-16 code units.</summary>
internal static class StringKeyHashes
{
    public static readonly StringKeyHasher Default = static key => XxHash3.HashToUInt64(MemoryMarshal.AsBytes(key));
}
