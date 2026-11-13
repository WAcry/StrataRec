using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The dense, key-ordered staging area of one fetch (design 4.5, 6.5). Each field is a column buffer indexed by
/// key index; a "found" bitmap records which keys the response contained.
/// </summary>
internal sealed class StagingBuffer : IFeatureBatchWriter
{
    private readonly ColumnBuffer[] _fields;
    private readonly ulong[] _found;

    public StagingBuffer(IReadOnlyList<CompiledFieldBinding> fields, int keyCount, RequestMemory memory)
    {
        KeyCount = keyCount;
        _fields = new ColumnBuffer[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            _fields[i] = ColumnBuffer.Create(fields[i].Type.AsNonNullable(), Math.Max(1, keyCount), memory);
        }

        _found = memory.RentCleared<ulong>(Bits.WordCount(Math.Max(1, keyCount)));
    }

    public int KeyCount { get; }

    public ColumnBuffer Field(int fieldIndex) => _fields[fieldIndex];

    /// <summary>True when the key was found and the field has a value.</summary>
    public bool HasValue(int fieldIndex, int keyIndex) => Bits.Get(_found, keyIndex) && _fields[fieldIndex].IsValid(keyIndex);

    public Memory<T> GetValues<T>(int fieldIndex)
        where T : unmanaged => _fields[fieldIndex] switch
        {
            ScalarColumn<T> s => s.Values.AsMemory(0, KeyCount),
            VectorColumn v when typeof(T) == typeof(float) => (Memory<T>)(object)v.Flat.AsMemory(0, KeyCount * v.Dimension),
            var other => throw new InvalidOperationException(
                $"Field {fieldIndex} has type {other.Type}; GetValues<{typeof(T).Name}> does not match it."),
        };

    public void SetFound(int keyIndex) => Bits.Set(_found, keyIndex);

    public void SetMissing(int fieldIndex, int keyIndex) => _fields[fieldIndex].SetMissing(keyIndex);

    public void SetString(int fieldIndex, int keyIndex, string value)
    {
        switch (_fields[fieldIndex])
        {
            case DictStringColumn d: d.Set(keyIndex, value); break;
            case TextColumn t: t.Set(keyIndex, value); break;
            default: throw new InvalidOperationException($"Field {fieldIndex} is not a string or text field.");
        }
    }

    public void SetInt64List(int fieldIndex, int keyIndex, ReadOnlySpan<long> value) =>
        ((ListColumn<long>)_fields[fieldIndex]).Set(keyIndex, value);

    public void SetStringList(int fieldIndex, int keyIndex, ReadOnlySpan<string> value) =>
        ((ListColumn<string>)_fields[fieldIndex]).Set(keyIndex, value);
}

/// <summary>Writes into a window <c>[offset, offset + count)</c> of a larger staging buffer (for max_batch splitting).</summary>
internal sealed class StagingWindow(StagingBuffer inner, int offset, int count) : IFeatureBatchWriter
{
    public int KeyCount => count;

    public Memory<T> GetValues<T>(int fieldIndex)
        where T : unmanaged
    {
        var all = inner.GetValues<T>(fieldIndex);
        var perKey = all.Length / Math.Max(1, inner.KeyCount);
        return all.Slice(offset * perKey, count * perKey);
    }

    public void SetFound(int keyIndex) => inner.SetFound(offset + keyIndex);

    public void SetMissing(int fieldIndex, int keyIndex) => inner.SetMissing(fieldIndex, offset + keyIndex);

    public void SetString(int fieldIndex, int keyIndex, string value) => inner.SetString(fieldIndex, offset + keyIndex, value);

    public void SetInt64List(int fieldIndex, int keyIndex, ReadOnlySpan<long> value) => inner.SetInt64List(fieldIndex, offset + keyIndex, value);

    public void SetStringList(int fieldIndex, int keyIndex, ReadOnlySpan<string> value) => inner.SetStringList(fieldIndex, offset + keyIndex, value);
}
