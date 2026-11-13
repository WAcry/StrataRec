using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Local;

namespace StrataRec.Features.Cache;

/// <summary>What <see cref="CacheRecord.ReadHeader"/> found.</summary>
internal enum CacheRecordHeader : byte
{
    /// <summary>A record of this format and field set.</summary>
    Ok = 1,

    /// <summary>Another format version, or another field set (an instance on another catalog version wrote it): a miss.</summary>
    Mismatch,

    /// <summary>Too short, or not a record at all: a miss.</summary>
    Corrupt,
}

/// <summary>
/// The value encoding of a cache record (design 6.6 <c>cache</c>): what the engine stores per (source, entity, key) in a
/// distributed cache, and what it hands its in-process table's writer. The one owner of the format: encoding, the
/// fingerprint of the field set, and decoding (with every length checked, so bytes from the network never throw).
/// </summary>
/// <remarks>
/// <para>Layout, little-endian (as are the platforms .NET runs on):</para>
/// <code>
/// 0   uint8   format          1
/// 1   uint8   flags           bit 0: absent (the connector had no value for the key: every field missing)
/// 2   uint16  field count     of the table
/// 4   int64   write time      Unix milliseconds: when the fetch that produced the values started
/// 12  uint64  fingerprint     XXH3-64 of the field set (<see cref="Fingerprint"/>)
/// 20  uint8[V] validity       bit f set = field f has a value (V = ceil(fields / 8))
/// …   values of the fields that have one, in field order: bool 1 byte; int32, float32 4; int64, float64 8; vector
///     dimension × float32; string and text int32 byte length + UTF-8; list&lt;int32|int64&gt; int32 count + elements;
///     list&lt;string&gt; int32 count + (int32 byte length + UTF-8) per item
/// </code>
/// <para>The fingerprint covers the format, the key type and every field's name and storage type in field order (the
/// table's order, by name), so two instances whose catalogs bind different fields to the source never decode each
/// other's records: a reader that finds another fingerprint treats the record as a miss. Values are not aligned: the
/// record is a compact wire form, copied into aligned storage when read.</para>
/// </remarks>
internal static class CacheRecord
{
    public const byte Format = 1;
    public const byte AbsentFlag = 1;
    internal const int ValidityOffset = 20;

    /// <summary>Bytes of a record before its values: the header and the validity bits.</summary>
    public static int PrefixSize(int fieldCount) => ValidityOffset + ((fieldCount + 7) / 8);

    /// <summary>The fingerprint of a table's field set, as records written for it carry.</summary>
    public static ulong Fingerprint(LocalTableSchema schema)
    {
        var text = new StringBuilder("stratarec.cache.record/").Append(Format).Append('\n')
            .Append(schema.KeyType == EntityKeyType.String ? "string" : "int64").Append('\n');
        foreach (var field in schema.Fields)
        {
            text.Append(field.Name).Append('\t').Append(field.Type.ToString()).Append('\n');
        }

        return XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>Reads the header; <paramref name="writtenMs"/> and <paramref name="absent"/> are meaningful when <see cref="CacheRecordHeader.Ok"/>.</summary>
    public static CacheRecordHeader ReadHeader(ReadOnlySpan<byte> record, int fieldCount, ulong fingerprint, out long writtenMs, out bool absent)
    {
        writtenMs = 0;
        absent = false;
        if (record.Length < PrefixSize(fieldCount))
        {
            return record.Length >= ValidityOffset && record[0] != Format ? CacheRecordHeader.Mismatch : CacheRecordHeader.Corrupt;
        }

        if (record[0] != Format || BinaryPrimitives.ReadUInt16LittleEndian(record[2..]) != fieldCount
            || BinaryPrimitives.ReadUInt64LittleEndian(record[12..]) != fingerprint)
        {
            return CacheRecordHeader.Mismatch;
        }

        writtenMs = BinaryPrimitives.ReadInt64LittleEndian(record[4..]);
        absent = (record[1] & AbsentFlag) != 0;
        return CacheRecordHeader.Ok;
    }

    /// <summary>The write time of a record whose header was found <see cref="CacheRecordHeader.Ok"/>.</summary>
    public static long WrittenMs(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadInt64LittleEndian(record[4..]);

    /// <summary>
    /// Walks the values of a record whose header is <see cref="CacheRecordHeader.Ok"/>, calling <paramref name="visitor"/>
    /// once per field in field order. Returns false, possibly after some calls, when a length runs past the record or
    /// the record has bytes left over: callers that must not see half a record run <see cref="IsWellFormed"/> first.
    /// </summary>
    public static bool Read<TVisitor>(ReadOnlySpan<byte> record, CacheRecordLayout layout, ref TVisitor visitor)
        where TVisitor : struct, ICacheRecordVisitor
    {
        var fields = layout.FieldCount;
        var validity = record.Slice(ValidityOffset, (fields + 7) / 8);
        var at = PrefixSize(fields);
        for (var f = 0; f < fields; f++)
        {
            if ((validity[f >> 3] & (1 << (f & 7))) == 0)
            {
                visitor.Missing(f);
                continue;
            }

            var rest = record[at..];
            int size;
            switch (layout.Kinds[f])
            {
                case StorageKind.Bool:
                    if (rest.Length < 1)
                    {
                        return false;
                    }

                    visitor.Bool(f, rest[0] != 0);
                    size = 1;
                    break;
                case StorageKind.Int32:
                    if (rest.Length < 4)
                    {
                        return false;
                    }

                    visitor.Int32(f, BinaryPrimitives.ReadInt32LittleEndian(rest));
                    size = 4;
                    break;
                case StorageKind.Int64:
                    if (rest.Length < 8)
                    {
                        return false;
                    }

                    visitor.Int64(f, BinaryPrimitives.ReadInt64LittleEndian(rest));
                    size = 8;
                    break;
                case StorageKind.Float32:
                    if (rest.Length < 4)
                    {
                        return false;
                    }

                    visitor.Float32(f, BinaryPrimitives.ReadSingleLittleEndian(rest));
                    size = 4;
                    break;
                case StorageKind.Float64:
                    if (rest.Length < 8)
                    {
                        return false;
                    }

                    visitor.Float64(f, BinaryPrimitives.ReadDoubleLittleEndian(rest));
                    size = 8;
                    break;
                case StorageKind.Vector:
                    size = 4 * layout.Dimensions[f];
                    if (rest.Length < size)
                    {
                        return false;
                    }

                    visitor.Vector(f, MemoryMarshal.Cast<byte, float>(rest[..size]));
                    break;
                case StorageKind.String or StorageKind.Text:
                    if (!TryLength(rest, 1, out var bytes))
                    {
                        return false;
                    }

                    visitor.String(f, rest.Slice(4, bytes));
                    size = 4 + bytes;
                    break;
                case StorageKind.ListInt32:
                    if (!TryLength(rest, 4, out bytes))
                    {
                        return false;
                    }

                    visitor.Int32List(f, MemoryMarshal.Cast<byte, int>(rest.Slice(4, bytes)));
                    size = 4 + bytes;
                    break;
                case StorageKind.ListInt64:
                    if (!TryLength(rest, 8, out bytes))
                    {
                        return false;
                    }

                    visitor.Int64List(f, MemoryMarshal.Cast<byte, long>(rest.Slice(4, bytes)));
                    size = 4 + bytes;
                    break;
                case StorageKind.ListString:
                    if (rest.Length < 4)
                    {
                        return false;
                    }

                    var count = BinaryPrimitives.ReadInt32LittleEndian(rest);
                    if (count < 0)
                    {
                        return false;
                    }

                    visitor.StringListStart(f, count);
                    size = 4;
                    for (var i = 0; i < count; i++)
                    {
                        if (!TryLength(rest[size..], 1, out var item))
                        {
                            return false;
                        }

                        visitor.StringListItem(f, rest.Slice(size + 4, item));
                        size += 4 + item;
                    }

                    visitor.StringListEnd(f);
                    break;
                default:
                    return false;
            }

            at += size;
        }

        return at == record.Length;
    }

    /// <summary>True when <see cref="Read"/> would walk the whole record: every length within it, nothing left over.</summary>
    public static bool IsWellFormed(ReadOnlySpan<byte> record, CacheRecordLayout layout)
    {
        var none = default(NoVisitor);
        return Read(record, layout, ref none);
    }

    /// <summary>An int32 count of <paramref name="elementSize"/>-byte elements at the start of <paramref name="rest"/>, as a
    /// byte length that fits what follows it.</summary>
    private static bool TryLength(ReadOnlySpan<byte> rest, int elementSize, out int bytes)
    {
        bytes = 0;
        if (rest.Length < 4)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(rest);
        if (count < 0 || (long)count * elementSize > rest.Length - 4)
        {
            return false;
        }

        bytes = count * elementSize;
        return true;
    }

    private struct NoVisitor : ICacheRecordVisitor
    {
        public void Missing(int field)
        {
        }

        public void Bool(int field, bool value)
        {
        }

        public void Int32(int field, int value)
        {
        }

        public void Int64(int field, long value)
        {
        }

        public void Float32(int field, float value)
        {
        }

        public void Float64(int field, double value)
        {
        }

        public void Vector(int field, ReadOnlySpan<float> value)
        {
        }

        public void String(int field, ReadOnlySpan<byte> utf8)
        {
        }

        public void Int32List(int field, ReadOnlySpan<int> value)
        {
        }

        public void Int64List(int field, ReadOnlySpan<long> value)
        {
        }

        public void StringListStart(int field, int count)
        {
        }

        public void StringListItem(int field, ReadOnlySpan<byte> utf8)
        {
        }

        public void StringListEnd(int field)
        {
        }
    }
}

/// <summary>Receives the fields of a record from <see cref="CacheRecord.Read"/>, in field order (a struct, so calls are not virtual).</summary>
internal interface ICacheRecordVisitor
{
    void Missing(int field);

    void Bool(int field, bool value);

    void Int32(int field, int value);

    void Int64(int field, long value);

    void Float32(int field, float value);

    void Float64(int field, double value);

    void Vector(int field, ReadOnlySpan<float> value);

    /// <summary>A <c>string</c> or <c>text</c> value.</summary>
    void String(int field, ReadOnlySpan<byte> utf8);

    void Int32List(int field, ReadOnlySpan<int> value);

    void Int64List(int field, ReadOnlySpan<long> value);

    void StringListStart(int field, int count);

    void StringListItem(int field, ReadOnlySpan<byte> utf8);

    void StringListEnd(int field);
}

/// <summary>The storage kind and dimension of each field of a table, as the codec walks them; built once per table.</summary>
internal sealed class CacheRecordLayout
{
    public CacheRecordLayout(LocalTableSchema schema)
    {
        FieldCount = schema.FieldCount;
        Kinds = new StorageKind[FieldCount];
        Dimensions = new int[FieldCount];
        for (var f = 0; f < FieldCount; f++)
        {
            var type = schema.Fields[f].Type;
            Kinds[f] = type.Kind;
            Dimensions[f] = type.Kind == StorageKind.Vector ? type.Dimension : 0;
        }

        Fingerprint = CacheRecord.Fingerprint(schema);
    }

    public int FieldCount { get; }

    public StorageKind[] Kinds { get; }

    public int[] Dimensions { get; }

    public ulong Fingerprint { get; }
}

/// <summary>
/// Encodes records one after another into one growing array: the records a read writes back share it, so a batch costs
/// a few arrays, not one per key. A record is begun, its fields are added in field order (a field not added is missing),
/// and it is ended, which gives its offset and length; slices are taken from <see cref="Bytes"/> only once every record is
/// written, since the array is replaced when it grows.
/// </summary>
internal sealed class CacheRecordWriter
{
    private readonly CacheRecordLayout _layout;
    private byte[] _bytes;
    private int _length;
    private int _start;
    private int _next;

    public CacheRecordWriter(CacheRecordLayout layout, int capacity)
    {
        _layout = layout;
        _bytes = new byte[Math.Max(64, capacity)];
    }

    /// <summary>The array the records are in, valid once the last record is ended.</summary>
    public byte[] Bytes => _bytes;

    public void Begin(long writtenMs, ulong fingerprint, bool absent)
    {
        _start = _length;
        _next = 0;
        var prefix = CacheRecord.PrefixSize(_layout.FieldCount);
        var header = Reserve(prefix);
        header.Clear();
        header[0] = CacheRecord.Format;
        header[1] = absent ? CacheRecord.AbsentFlag : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], checked((ushort)_layout.FieldCount));
        BinaryPrimitives.WriteInt64LittleEndian(header[4..], writtenMs);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..], fingerprint);
    }

    public void Bool(int field, bool value)
    {
        Present(field, StorageKind.Bool);
        Reserve(1)[0] = value ? (byte)1 : (byte)0;
    }

    public void Int32(int field, int value)
    {
        Present(field, StorageKind.Int32);
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);
    }

    public void Int64(int field, long value)
    {
        Present(field, StorageKind.Int64);
        BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);
    }

    public void Float32(int field, float value)
    {
        Present(field, StorageKind.Float32);
        BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);
    }

    public void Float64(int field, double value)
    {
        Present(field, StorageKind.Float64);
        BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);
    }

    public void Vector(int field, ReadOnlySpan<float> value)
    {
        Present(field, StorageKind.Vector);
        if (value.Length != _layout.Dimensions[field])
        {
            throw new ArgumentException($"Field {field} has {_layout.Dimensions[field]} dimensions, not {value.Length}.", nameof(value));
        }

        MemoryMarshal.AsBytes(value).CopyTo(Reserve(4 * value.Length));
    }

    /// <summary>A <c>string</c> or <c>text</c> value.</summary>
    public void String(int field, string value)
    {
        Present(field, _layout.Kinds[field] == StorageKind.Text ? StorageKind.Text : StorageKind.String);
        WriteString(value);
    }

    public void Int32List(int field, ReadOnlySpan<int> value)
    {
        Present(field, StorageKind.ListInt32);
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value.Length);
        MemoryMarshal.AsBytes(value).CopyTo(Reserve(4 * value.Length));
    }

    public void Int64List(int field, ReadOnlySpan<long> value)
    {
        Present(field, StorageKind.ListInt64);
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value.Length);
        MemoryMarshal.AsBytes(value).CopyTo(Reserve(8 * value.Length));
    }

    public void StringList(int field, ReadOnlySpan<string> value)
    {
        Present(field, StorageKind.ListString);
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value.Length);
        foreach (var item in value)
        {
            WriteString(item);
        }
    }

    /// <summary>Ends the record begun last; its bytes are <c>Bytes[offset..offset + length]</c> once every record is written.</summary>
    public (int Offset, int Length) End() => (_start, _length - _start);

    private void Present(int field, StorageKind kind)
    {
        if (field < _next || (uint)field >= (uint)_layout.FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, "Fields are added once each, in field order.");
        }

        if (_layout.Kinds[field] != kind)
        {
            throw new ArgumentException($"Field {field} is {_layout.Kinds[field]}, not {kind}.", nameof(field));
        }

        _next = field + 1;
        _bytes[_start + CacheRecord.ValidityOffset + (field >> 3)] |= (byte)(1 << (field & 7));
    }

    private void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), bytes);
        Encoding.UTF8.GetBytes(value, Reserve(bytes));
    }

    private Span<byte> Reserve(int bytes)
    {
        if (_length + bytes > _bytes.Length)
        {
            var next = new byte[Math.Max(_bytes.Length * 2, _length + bytes)];
            _bytes.AsSpan(0, _length).CopyTo(next);
            _bytes = next;
        }

        var span = _bytes.AsSpan(_length, bytes);
        _length += bytes;
        return span;
    }
}
