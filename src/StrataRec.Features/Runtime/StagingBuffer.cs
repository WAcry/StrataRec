using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The dense, key-ordered staging area of one connector call (design 4.5, 6.5). Each field is a column buffer indexed
/// by key index, plus a "written" bitmap per field.
/// </summary>
/// <remarks>
/// <para>Validity is per field and starts all-clear: value arrays are pooled and hold whatever a previous request left,
/// so only a value the connector explicitly wrote may be read. A key-level "found" bit is deliberately absent — a
/// connector that fails half-way through a record, or reports a partial result, must not make the record's unwritten
/// fields look valid.</para>
/// <para>Not thread-safe, and it does not need to be: each <c>max_batch</c> chunk gets its own buffer, so concurrent
/// chunks never share bitmaps, string dictionaries or list arenas. A buffer is read only after its call completed
/// within the time budget; a timed-out call may keep writing into it, but it is never read and stays rented until
/// the call really finishes.</para>
/// <para>The "written" bitmaps of all fields share one rented block, field f starting at bit f × <c>_bitsPerField</c>:
/// a fetch with a small <c>max_batch</c> creates a buffer per chunk, and a rent per field per chunk adds up.</para>
/// </remarks>
internal sealed class StagingBuffer : IFeatureBatchWriter
{
    private readonly ColumnBuffer[] _fields;
    private readonly ulong[] _written;
    private readonly int _bitsPerField;

    public StagingBuffer(ReadOnlySpan<CompiledFieldBinding> fields, int keyCount, RequestMemory memory)
    {
        KeyCount = keyCount;
        var rows = Math.Max(1, keyCount);
        var words = Bits.WordCount(rows);
        _bitsPerField = words * 64;
        _fields = new ColumnBuffer[fields.Length];
        _written = memory.RentCleared<ulong>(Math.Max(1, fields.Length * words));
        for (var i = 0; i < fields.Length; i++)
        {
            _fields[i] = ColumnBuffer.Create(fields[i].Type.AsNonNullable(), rows, memory);
        }
    }

    public int KeyCount { get; }

    public ColumnBuffer Field(int fieldIndex) => _fields[fieldIndex];

    /// <summary>True when the connector wrote a value for this field and key (and did not withdraw it).</summary>
    public bool HasValue(int fieldIndex, int keyIndex) => Bits.Get(_written, WrittenBit(fieldIndex, keyIndex));

    public Memory<T> GetValues<T>(int fieldIndex)
        where T : unmanaged => _fields[fieldIndex] switch
        {
            ScalarColumn<T> s => s.Values.AsMemory(0, KeyCount),
            VectorColumn v when typeof(T) == typeof(float) => (Memory<T>)(object)v.Flat.AsMemory(0, KeyCount * v.Dimension),
            var other => throw new InvalidOperationException(
                $"Field {fieldIndex} has type {other.Type}; GetValues<{typeof(T).Name}> does not match it."),
        };

    public void SetValid(int fieldIndex, int keyIndex)
    {
        CheckKey(keyIndex);
        Bits.Set(_written, WrittenBit(fieldIndex, keyIndex));
    }

    public void SetMissing(int fieldIndex, int keyIndex)
    {
        CheckKey(keyIndex);
        Bits.Clear(_written, WrittenBit(fieldIndex, keyIndex));
    }

    public void SetString(int fieldIndex, int keyIndex, string value)
    {
        CheckKey(keyIndex);
        switch (_fields[fieldIndex])
        {
            case DictStringColumn d: d.Set(keyIndex, value); break;
            case TextColumn t: t.Set(keyIndex, value); break;
            default: throw new InvalidOperationException($"Field {fieldIndex} is not a string or text field.");
        }

        Bits.Set(_written, WrittenBit(fieldIndex, keyIndex));
    }

    public void SetInt64List(int fieldIndex, int keyIndex, ReadOnlySpan<long> value)
    {
        CheckKey(keyIndex);
        var bit = WrittenBit(fieldIndex, keyIndex);
        if (_fields[fieldIndex] is not ListColumn<long> list)
        {
            throw WrongField(fieldIndex, nameof(SetInt64List));
        }

        list.Set(keyIndex, value);
        Bits.Set(_written, bit);
    }

    public void SetInt32List(int fieldIndex, int keyIndex, ReadOnlySpan<int> value)
    {
        CheckKey(keyIndex);
        var bit = WrittenBit(fieldIndex, keyIndex);
        if (_fields[fieldIndex] is not ListColumn<int> list)
        {
            throw WrongField(fieldIndex, nameof(SetInt32List));
        }

        list.Set(keyIndex, value);
        Bits.Set(_written, bit);
    }

    public void SetStringList(int fieldIndex, int keyIndex, ReadOnlySpan<string> value)
    {
        CheckKey(keyIndex);
        ((ListColumn<string>)_fields[fieldIndex]).Set(keyIndex, value);
        Bits.Set(_written, WrittenBit(fieldIndex, keyIndex));
    }

    /// <summary>Bit of (field, key) in the shared block. The field is checked here: it would otherwise address another field's bits.</summary>
    private int WrittenBit(int fieldIndex, int keyIndex)
    {
        if ((uint)fieldIndex >= (uint)_fields.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldIndex), fieldIndex, $"The batch has {_fields.Length} fields.");
        }

        return (fieldIndex * _bitsPerField) + keyIndex;
    }

    /// <summary>A list setter that does not match the field's declared type (e.g. int64 values for a
    /// <c>list&lt;int32&gt;</c> field): a connector bug, reported with the type it should have used.</summary>
    private InvalidOperationException WrongField(int fieldIndex, string method) =>
        new($"Field {fieldIndex} has type {_fields[fieldIndex].Type}; {method} does not match it" +
            (_fields[fieldIndex].Type.Kind switch
            {
                StorageKind.ListInt32 => " (use SetInt32List).",
                StorageKind.ListInt64 => " (use SetInt64List).",
                _ => ".",
            }));

    private void CheckKey(int keyIndex)
    {
        // The bitmaps are rented and may be longer than KeyCount; an out-of-range key must not mark a phantom value.
        if ((uint)keyIndex >= (uint)KeyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(keyIndex), keyIndex, $"The batch has {KeyCount} keys.");
        }
    }
}
