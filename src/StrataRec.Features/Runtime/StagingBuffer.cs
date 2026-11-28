using System.Numerics;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <remarks>
/// <para>A chunk's region is read only after its call completed within its budget; a timed-out call may keep writing
/// into its own region, which is never read and stays rented until the call really finishes.</para>
/// </remarks>
internal sealed class StagingArea
{
    private readonly RequestMemory _memory;
    private readonly Field[] _fields;
    private readonly ColumnBuffer?[]? _lists;
    private readonly ulong[] _written;
    private readonly int _bitsPerChunk;
    private readonly int _bitsPerField;

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

    public int ChunkKeyCount(int chunk) => Math.Min(ChunkSize, KeyCount - (chunk * ChunkSize));

    /// <summary>
    /// First bit of (field, chunk) in the "written" bitmap: always a multiple of 64, so concurrent chunks never write
    /// the same word.
    /// </summary>
    public int FirstBit(int fieldIndex, int chunk) => (CheckField(fieldIndex) * _bitsPerField) + (chunk * _bitsPerChunk);

    public bool HasValue(int fieldIndex, int key)
    {
        var chunk = key / ChunkSize;
        return Bits.Get(_written, FirstBit(fieldIndex, chunk) + key - (chunk * ChunkSize));
    }

    /// <summary>
    /// Only for a chunk whose call has completed.
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

        var tail = keys & 63;
        return count + BitOperations.PopCount(tail == 0 ? words[^1] : words[^1] & ((1UL << tail) - 1));
    }

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
    /// False, leaving the row untouched, for a list of another length than a <c>list&lt;E,N&gt;</c> column's.
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

internal sealed class StagingBuffer : IFeatureBatchWriter, IStagedFeatureValues
{
    private readonly StagingArea _area;
    private int _chunk;
    private int _offset;

    private StringDedup? _strings;

    public StagingBuffer(StagingArea area)
    {
        _area = area;
    }

    public StagingBuffer(ReadOnlySpan<CompiledFieldBinding> fields, int keyCount, RequestMemory memory)
        : this(new StagingArea(fields, keyCount, Math.Max(1, keyCount), memory))
    {
        Bind(0);
    }

    public int KeyCount { get; private set; }

    /// <summary>Only while no call is writing through it.</summary>
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
