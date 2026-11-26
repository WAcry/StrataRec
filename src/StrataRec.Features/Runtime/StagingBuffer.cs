using System.Numerics;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The dense, key-ordered staging area of one fetch. Every <c>max_batch</c> chunk of the fetch writes
/// its keys' values here through a <see cref="StagingBuffer"/>; key index k of the fetch is row k − c × ChunkSize of
/// chunk c = k / ChunkSize.
/// </summary>
/// <remarks>
/// <para>Validity is per field and key and starts all-clear: value arrays are pooled and hold whatever a previous request
/// left, so only a value the connector explicitly wrote may be read. A key-level "found" bit is deliberately absent — a
/// connector that fails half-way through a record, or reports a partial result, must not make the record's unwritten
/// fields look valid.</para>
/// <para>Chunks run concurrently on connector threads, and a timed-out chunk may keep writing after the request stopped
/// waiting for it, while the other chunks are read. No two chunks ever write the same memory: fixed-size values and
/// strings live in one array per field indexed by the fetch's key index, where chunks own disjoint ranges;
/// each chunk's "written" bits start at a 64-bit word boundary, because two writers of one bitmap word lose each other's
/// bits; and a list field gets one buffer per chunk, because its arena grows. Strings are staged as the references the
/// connector wrote, and backfill copies the references. A chunk's region is read only
/// after its call completed within its budget; a timed-out call may keep writing into its own region, which is never
/// read and stays rented until the call really finishes.</para>
/// <para>One area per fetch rather than one buffer per chunk: a fetch with a small <c>max_batch</c> has hundreds of
/// chunks, and their column objects and rents were most of a request's allocation.</para>
/// </remarks>
internal sealed class StagingArea
{
    private readonly RequestMemory _memory;
    private readonly Field[] _fields;
    private readonly ColumnBuffer?[]? _lists; // list fields: [field × ChunkCount + chunk], created by the chunk's first write
    private readonly ulong[] _written;
    private readonly int _bitsPerChunk;
    private readonly int _bitsPerField;

    /// <param name="chunkSize">Keys per chunk (<c>Batching.Size</c> of the keys and the source's <c>max_batch</c>); the last
    /// chunk may be shorter.</param>
    public StagingArea(ReadOnlySpan<CompiledFieldBinding> fields, int keyCount, int chunkSize, RequestMemory memory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        _memory = memory;
        KeyCount = keyCount;
        ChunkSize = chunkSize;
        ChunkCount = Math.Max(1, Batching.Count(keyCount, chunkSize));
        _bitsPerChunk = Bits.WordCount(Math.Max(1, Math.Min(chunkSize, keyCount))) * 64;
        _bitsPerField = checked(ChunkCount * _bitsPerChunk);
        _written = memory.RentCleared<ulong>(Math.Max(1, checked(fields.Length * (_bitsPerField / 64))));
        _fields = new Field[fields.Length];
        var rows = Math.Max(1, keyCount);
        var hasLists = false;
        for (var i = 0; i < fields.Length; i++)
        {
            var type = fields[i].Type.AsNonNullable();
            ColumnBuffer? column = null;
            if (type.IsList)
            {
                hasLists = true;
            }
            else
            {
                column = ColumnBuffer.Create(type, rows, memory);
            }

            _fields[i] = new Field(type, column);
        }

        if (hasLists)
        {
            _lists = memory.RentCleared<ColumnBuffer?>(checked(fields.Length * ChunkCount));
        }
    }

    public int KeyCount { get; }

    public int ChunkSize { get; }

    public int ChunkCount { get; }

    public int FieldCount => _fields.Length;

    /// <summary>Number of keys of chunk <paramref name="chunk"/>.</summary>
    public int ChunkKeyCount(int chunk) => Math.Min(ChunkSize, KeyCount - (chunk * ChunkSize));

    /// <summary>
    /// First bit of (field, chunk) in the "written" bitmap: always a multiple of 64, so concurrent chunks never write
    /// the same word.
    /// </summary>
    public int FirstBit(int fieldIndex, int chunk) => (CheckField(fieldIndex) * _bitsPerField) + (chunk * _bitsPerChunk);

    /// <summary>True when the connector wrote a value for this field and fetch key (and did not withdraw it).</summary>
    public bool HasValue(int fieldIndex, int key)
    {
        var chunk = key / ChunkSize;
        return Bits.Get(_written, FirstBit(fieldIndex, chunk) + key - (chunk * ChunkSize));
    }

    /// <summary>
    /// How many keys of <paramref name="chunk"/> the connector wrote a value of the field for (and did not withdraw): one
    /// popcount per 64 keys of the chunk's region of the "written" bitmap. Only for a chunk whose call has completed.
    /// </summary>
    public int CountWritten(int fieldIndex, int chunk)
    {
        var first = FirstBit(fieldIndex, chunk) >> 6;
        var keys = ChunkKeyCount(chunk);
        if (keys <= 0)
        {
            return 0;
        }

        var words = _written.AsSpan(first, Bits.WordCount(keys));
        var count = 0;
        for (var w = 0; w < words.Length - 1; w++)
        {
            count += BitOperations.PopCount(words[w]);
        }

        // Bits past the chunk's keys are never set (the writer checks every key), but the last word is masked anyway.
        var tail = keys & 63;
        return count + BitOperations.PopCount(tail == 0 ? words[^1] : words[^1] & ((1UL << tail) - 1));
    }

    /// <summary>The staged value of a fetch key, or missing when the connector wrote none.</summary>
    public ScalarValue Get(int fieldIndex, int key)
    {
        if (!HasValue(fieldIndex, key))
        {
            return ScalarValue.Missing;
        }

        var field = _fields[fieldIndex];
        if (field.Column is null)
        {
            var chunk = key / ChunkSize;
            return ColumnValues.Get(_lists![(fieldIndex * ChunkCount) + chunk]!, key - (chunk * ChunkSize));
        }

        return ColumnValues.Get(field.Column, key);
    }

    /// <summary>
    /// Copies the staged value of a fetch key (which must be present) into a row of a table column of the field's type.
    /// False, leaving the row untouched, for a list of another length than a <c>list&lt;E,N&gt;</c> column's: staging holds
    /// what the connector wrote (recordings and the cache keep it as written), and the type applies where a value is
    /// stored, as for every other writer.
    /// </summary>
    public bool TryCopyTo(int fieldIndex, int key, ColumnBuffer target, int row)
    {
        var field = _fields[fieldIndex];
        if (field.Column is null)
        {
            var chunk = key / ChunkSize;
            var list = (ListColumn)_lists![(fieldIndex * ChunkCount) + chunk]!;
            var local = key - (chunk * ChunkSize);
            if (!ColumnValues.FitsLength(list.LengthOf(local), target.Type))
            {
                return false;
            }

            target.CopyFrom(list, local, row);
        }
        else
        {
            target.CopyFrom(field.Column, key, row);
        }

        return true;
    }

    internal ColumnType TypeOf(int fieldIndex) => _fields[CheckField(fieldIndex)].Type;

    /// <summary>Storage of a fixed-size or string field, indexed by fetch key; null for a list field.</summary>
    internal ColumnBuffer? ColumnOf(int fieldIndex) => _fields[CheckField(fieldIndex)].Column;

    /// <summary>The list buffer of one chunk, created on its first write (from the chunk's own connector thread).</summary>
    internal ListColumn<T> ListOf<T>(int fieldIndex, int chunk, bool create)
    {
        ref var slot = ref _lists![(fieldIndex * ChunkCount) + chunk];
        if (slot is null)
        {
            if (!create)
            {
                throw new InvalidOperationException($"Chunk {chunk} wrote no value of list field {fieldIndex}.");
            }

            slot = new ListColumn<T>(_fields[fieldIndex].Type, ChunkKeyCount(chunk), _memory);
        }

        return (ListColumn<T>)slot;
    }

    /// <summary>The list buffer of one chunk, or null when the chunk wrote no value of the field; indexed by the chunk-local key.</summary>
    internal ColumnBuffer? ListBuffer(int fieldIndex, int chunk) => _lists![(CheckField(fieldIndex) * ChunkCount) + chunk];

    internal void SetBit(int bit) => Bits.Set(_written, bit);

    internal void ClearBit(int bit) => Bits.Clear(_written, bit);

    internal bool GetBit(int bit) => Bits.Get(_written, bit);

    private int CheckField(int fieldIndex)
    {
        if ((uint)fieldIndex >= (uint)_fields.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldIndex), fieldIndex, $"The batch has {_fields.Length} fields.");
        }

        return fieldIndex;
    }

    private readonly record struct Field(ColumnType Type, ColumnBuffer? Column);
}

/// <summary>
/// One chunk's window of a <see cref="StagingArea"/>: the writer a connector call decodes into, and what a recording of
/// the call reads. Key indices are local to the chunk.
/// </summary>
/// <remarks>
/// A view, not a buffer. The fetch points one view at its chunks in turn while their connectors answer synchronously,
/// so a chunk answered at once costs no object; a call that goes asynchronous keeps its view until it really completes,
/// and the fetch takes a new one for its next chunk. Not thread-safe, and it does not need to be: one call writes
/// through a view at a time.
/// </remarks>
internal sealed class StagingBuffer : IFeatureBatchWriter, IStagedFeatureValues
{
    private readonly StagingArea _area;
    private int _chunk;
    private int _offset;

    /// <summary>
    /// The values <see cref="Intern"/> handed out to the call writing now (a writer is written by one call at a time, so it
    /// needs no lock); made by the first call that interns, and cleared when the writer is bound to the next chunk, whose
    /// call starts with none of the previous call's values and all of <see cref="StringDedup.MaxValues"/>.
    /// </summary>
    private StringDedup? _strings;

    public StagingBuffer(StagingArea area)
    {
        _area = area;
    }

    /// <summary>A staging buffer of its own for one call of <paramref name="keyCount"/> keys (context fetches, tests).</summary>
    public StagingBuffer(ReadOnlySpan<CompiledFieldBinding> fields, int keyCount, RequestMemory memory)
        : this(new StagingArea(fields, keyCount, Math.Max(1, keyCount), memory))
    {
        Bind(0);
    }

    public int KeyCount { get; private set; }

    /// <summary>Points the view at a chunk. Only while no call is writing through it.</summary>
    public void Bind(int chunk)
    {
        _strings?.Clear(); // each call interns on its own: one call's distinct values must not use up the next call's
        _chunk = chunk;
        _offset = chunk * _area.ChunkSize;
        KeyCount = _area.ChunkKeyCount(chunk);
    }

    /// <summary>
    /// Storage of a field for this chunk (tests): fixed-size and string fields are shared by the fetch's
    /// chunks and indexed by the fetch key (<c>chunk × ChunkSize + key</c>, the key itself for a buffer of its own); a
    /// list field's buffer is this chunk's and indexed by the chunk-local key.
    /// </summary>
    internal ColumnBuffer Field(int fieldIndex) => _area.ColumnOf(fieldIndex) ?? _area.ListBuffer(fieldIndex, _chunk)
        ?? throw new InvalidOperationException($"Chunk {_chunk} wrote no value of list field {fieldIndex}.");

    /// <summary>True when the connector wrote a value for this field and key (and did not withdraw it).</summary>
    public bool HasValue(int fieldIndex, int keyIndex) => _area.GetBit(_area.FirstBit(fieldIndex, _chunk) + keyIndex);

    public ScalarValue Get(int fieldIndex, int keyIndex)
    {
        CheckKey(keyIndex);
        return _area.Get(fieldIndex, _offset + keyIndex);
    }

    public Memory<T> GetValues<T>(int fieldIndex)
        where T : unmanaged => _area.ColumnOf(fieldIndex) switch
        {
            ScalarColumn<T> s => s.Values.AsMemory(_offset, KeyCount),
            _ => throw new InvalidOperationException(
                $"Field {fieldIndex} has type {_area.TypeOf(fieldIndex)}; GetValues<{typeof(T).Name}> does not match it."),
        };

    public void SetValid(int fieldIndex, int keyIndex)
    {
        CheckKey(keyIndex);
        _area.SetBit(_area.FirstBit(fieldIndex, _chunk) + keyIndex);
    }

    public void SetMissing(int fieldIndex, int keyIndex)
    {
        CheckKey(keyIndex);
        _area.ClearBit(_area.FirstBit(fieldIndex, _chunk) + keyIndex);
    }

    public void SetString(int fieldIndex, int keyIndex, string value)
    {
        CheckKey(keyIndex);
        var bit = _area.FirstBit(fieldIndex, _chunk) + keyIndex;
        if (_area.ColumnOf(fieldIndex) is not StringColumn strings)
        {
            throw new InvalidOperationException($"Field {fieldIndex} has type {_area.TypeOf(fieldIndex)}; SetString does not match it.");
        }

        // A null would reach a request's column, read as an empty string where it should be missing; it is the connector's bug.
        ArgumentNullException.ThrowIfNull(value);
        strings.Set(_offset + keyIndex, value);
        _area.SetBit(bit);
    }

    public string Intern(ReadOnlySpan<char> value) => (_strings ??= new StringDedup()).Intern(value);

    public string InternUtf8(ReadOnlySpan<byte> utf8) => (_strings ??= new StringDedup()).InternUtf8(utf8);

    public void SetBoolList(int fieldIndex, int keyIndex, ReadOnlySpan<bool> value) =>
        SetList(fieldIndex, keyIndex, value, StorageKind.Bool, nameof(SetBoolList));

    public void SetInt32List(int fieldIndex, int keyIndex, ReadOnlySpan<int> value) =>
        SetList(fieldIndex, keyIndex, value, StorageKind.Int32, nameof(SetInt32List));

    public void SetInt64List(int fieldIndex, int keyIndex, ReadOnlySpan<long> value) =>
        SetList(fieldIndex, keyIndex, value, StorageKind.Int64, nameof(SetInt64List));

    public void SetFloat32List(int fieldIndex, int keyIndex, ReadOnlySpan<float> value) =>
        SetList(fieldIndex, keyIndex, value, StorageKind.Float32, nameof(SetFloat32List));

    public void SetFloat64List(int fieldIndex, int keyIndex, ReadOnlySpan<double> value) =>
        SetList(fieldIndex, keyIndex, value, StorageKind.Float64, nameof(SetFloat64List));

    public void SetStringList(int fieldIndex, int keyIndex, ReadOnlySpan<string> value)
    {
        // A null element would reach a request's list column, where elements are never missing; it is the connector's bug.
        foreach (var item in value)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(value));
        }

        SetList(fieldIndex, keyIndex, value, StorageKind.String, nameof(SetStringList));
    }

    /// <summary>
    /// Writes a list into this chunk's buffer of the field. A setter that does not match the field's declared element type
    /// (e.g. int64 values for a <c>list&lt;int32&gt;</c> field) is a connector bug, reported with the setter it should have used.
    /// </summary>
    private void SetList<T>(int fieldIndex, int keyIndex, ReadOnlySpan<T> value, StorageKind element, string method)
    {
        CheckKey(keyIndex);
        var type = _area.TypeOf(fieldIndex);
        if (!type.IsList || type.Element != element)
        {
            throw new InvalidOperationException($"Field {fieldIndex} has type {type}; {method} does not match it" + (type.IsList ? type.Element switch
            {
                StorageKind.Bool => " (use SetBoolList).",
                StorageKind.Int32 => " (use SetInt32List).",
                StorageKind.Int64 => " (use SetInt64List).",
                StorageKind.Float32 => " (use SetFloat32List).",
                StorageKind.Float64 => " (use SetFloat64List).",
                _ => " (use SetStringList).",
            } : "."));
        }

        var bit = _area.FirstBit(fieldIndex, _chunk) + keyIndex;
        _area.ListOf<T>(fieldIndex, _chunk, create: true).Set(keyIndex, value);
        _area.SetBit(bit);
    }

    private void CheckKey(int keyIndex)
    {
        // The arrays are rented and longer than the chunk; an out-of-range key must not touch another chunk's values or
        // mark a phantom value.
        if ((uint)keyIndex >= (uint)KeyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(keyIndex), keyIndex, $"The batch has {KeyCount} keys.");
        }
    }
}
