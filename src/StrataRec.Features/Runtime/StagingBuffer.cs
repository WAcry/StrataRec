using System.Numerics;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The dense, key-ordered staging area of one fetch (design 4.5, 6.5). Every <c>max_batch</c> chunk of the fetch writes
/// its keys' values here through a <see cref="StagingBuffer"/>; key index k of the fetch is row k − c × ChunkSize of
/// chunk c = k / ChunkSize.
/// </summary>
/// <remarks>
/// <para>Validity is per field and key and starts all-clear: value arrays are pooled and hold whatever a previous request
/// left, so only a value the connector explicitly wrote may be read. A key-level "found" bit is deliberately absent — a
/// connector that fails half-way through a record, or reports a partial result, must not make the record's unwritten
/// fields look valid.</para>
/// <para>Chunks run concurrently on connector threads, and a timed-out chunk may keep writing after the request stopped
/// waiting for it, while the other chunks are read. No two chunks ever write the same memory: fixed-size values,
/// vectors and strings live in one array per field indexed by the fetch's key index, where chunks own disjoint ranges;
/// each chunk's "written" bits start at a 64-bit word boundary, because two writers of one bitmap word lose each other's
/// bits; and a list field gets one buffer per chunk, because its arena grows. Strings are staged as plain references and
/// dictionary-encoded at backfill: a dictionary is not safe for concurrent writers either. A chunk's region is read only
/// after its call completed within its budget; a timed-out call may keep writing into its own region, which is never
/// read and stays rented until the call really finishes.</para>
/// <para>One area per fetch rather than one buffer per chunk: a fetch with a small <c>max_batch</c> has hundreds of
/// chunks, and their column objects, string dictionaries and rents were most of a request's allocation.</para>
/// </remarks>
internal sealed class StagingArea
{
    private readonly RequestMemory _memory;
    private readonly Field[] _fields;
    private readonly ColumnBuffer?[]? _lists; // list fields: [field × ChunkCount + chunk], created by the chunk's first write
    private readonly ulong[] _written;
    private readonly int _bitsPerChunk;
    private readonly int _bitsPerField;

    /// <param name="chunkSize">Keys per chunk (the source's <c>max_batch</c>); the last chunk may be shorter.</param>
    public StagingArea(ReadOnlySpan<CompiledFieldBinding> fields, int keyCount, int chunkSize, RequestMemory memory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        _memory = memory;
        KeyCount = keyCount;
        ChunkSize = chunkSize;
        ChunkCount = Math.Max(1, (keyCount + chunkSize - 1) / chunkSize);
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
            switch (type.Kind)
            {
                case StorageKind.ListInt32 or StorageKind.ListInt64 or StorageKind.ListString:
                    hasLists = true;
                    break;
                case StorageKind.String:
                    column = new TextColumn(ColumnType.Text, rows, memory);
                    break;
                default:
                    column = ColumnBuffer.Create(type, rows, memory);
                    break;
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

        return field.Type.Kind == StorageKind.String
            ? ScalarValue.FromString(((TextColumn)field.Column).Get(key))
            : ColumnValues.Get(field.Column, key);
    }

    /// <summary>Copies the staged value of a fetch key (which must be present) into a row of a table column of the field's type.</summary>
    public void CopyTo(int fieldIndex, int key, ColumnBuffer target, int row)
    {
        var field = _fields[fieldIndex];
        if (field.Column is null)
        {
            var chunk = key / ChunkSize;
            target.CopyFrom(_lists![(fieldIndex * ChunkCount) + chunk]!, key - (chunk * ChunkSize), row);
        }
        else if (field.Type.Kind == StorageKind.String && target is DictStringColumn strings)
        {
            strings.Set(row, ((TextColumn)field.Column).Get(key));
        }
        else
        {
            target.CopyFrom(field.Column, key, row);
        }
    }

    internal ColumnType TypeOf(int fieldIndex) => _fields[CheckField(fieldIndex)].Type;

    /// <summary>Storage of a fixed-size, vector, string or text field, indexed by fetch key; null for a list field.</summary>
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
        _chunk = chunk;
        _offset = chunk * _area.ChunkSize;
        KeyCount = _area.ChunkKeyCount(chunk);
    }

    /// <summary>
    /// Storage of a field for this chunk (tests): fixed-size, vector, string and text fields are shared by the fetch's
    /// chunks and indexed by the fetch key (<c>chunk × ChunkSize + key</c>, the key itself for a buffer of its own); a
    /// list field's buffer is this chunk's and indexed by the chunk-local key.
    /// </summary>
    internal ColumnBuffer Field(int fieldIndex) => _area.ColumnOf(fieldIndex) ?? _area.TypeOf(fieldIndex).Kind switch
    {
        StorageKind.ListInt32 => _area.ListOf<int>(fieldIndex, _chunk, create: false),
        StorageKind.ListInt64 => _area.ListOf<long>(fieldIndex, _chunk, create: false),
        _ => _area.ListOf<string>(fieldIndex, _chunk, create: false),
    };

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
            VectorColumn v when typeof(T) == typeof(float) =>
                (Memory<T>)(object)v.Flat.AsMemory(_offset * v.Dimension, KeyCount * v.Dimension),
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
        if (_area.ColumnOf(fieldIndex) is not TextColumn text)
        {
            throw new InvalidOperationException($"Field {fieldIndex} is not a string or text field.");
        }

        // A null would reach the table's string dictionary at backfill, on the request's thread; it is the connector's bug.
        ArgumentNullException.ThrowIfNull(value);
        text.Set(_offset + keyIndex, value);
        _area.SetBit(bit);
    }

    public void SetInt64List(int fieldIndex, int keyIndex, ReadOnlySpan<long> value)
    {
        CheckKey(keyIndex);
        var bit = _area.FirstBit(fieldIndex, _chunk) + keyIndex;
        List<long>(fieldIndex, StorageKind.ListInt64, nameof(SetInt64List)).Set(keyIndex, value);
        _area.SetBit(bit);
    }

    public void SetInt32List(int fieldIndex, int keyIndex, ReadOnlySpan<int> value)
    {
        CheckKey(keyIndex);
        var bit = _area.FirstBit(fieldIndex, _chunk) + keyIndex;
        List<int>(fieldIndex, StorageKind.ListInt32, nameof(SetInt32List)).Set(keyIndex, value);
        _area.SetBit(bit);
    }

    public void SetStringList(int fieldIndex, int keyIndex, ReadOnlySpan<string> value)
    {
        CheckKey(keyIndex);
        var bit = _area.FirstBit(fieldIndex, _chunk) + keyIndex;
        List<string>(fieldIndex, StorageKind.ListString, nameof(SetStringList)).Set(keyIndex, value);
        _area.SetBit(bit);
    }

    /// <summary>
    /// This chunk's buffer of a list field. A list setter that does not match the field's declared type (e.g. int64 values
    /// for a <c>list&lt;int32&gt;</c> field) is a connector bug, reported with the setter it should have used.
    /// </summary>
    private ListColumn<T> List<T>(int fieldIndex, StorageKind kind, string method)
    {
        var type = _area.TypeOf(fieldIndex);
        if (type.Kind != kind)
        {
            throw new InvalidOperationException($"Field {fieldIndex} has type {type}; {method} does not match it" + type.Kind switch
            {
                StorageKind.ListInt32 => " (use SetInt32List).",
                StorageKind.ListInt64 => " (use SetInt64List).",
                StorageKind.ListString => " (use SetStringList).",
                _ => ".",
            });
        }

        return _area.ListOf<T>(fieldIndex, _chunk, create: true);
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
