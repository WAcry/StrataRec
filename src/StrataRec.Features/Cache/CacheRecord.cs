using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Hashing;
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
/// 0   uint32  format          2026111601: the format version 2026-11-16.1 (the date it changed, that day's sequence)
/// 4   uint8   flags           bit 0: absent (the connector had no value for the key: every field missing)
/// 5   uint8   (zero)
/// 6   uint16  field count     of the table
/// 8   int64   write time      Unix milliseconds: when the fetch that produced the values started
/// 16  uint64  fingerprint     XXH3-64 of the field set (<see cref="Fingerprint"/>)
/// 24  uint8[V] validity       bit f set = field f has a value (V = ceil(fields / 8))
/// …   values of the fields that have one, in field order: bool 1 byte; int32, float32 4; int64, float64 8; string int32
///     byte length + UTF-8; a list int32 count + its elements, each as the scalar of its element type is (a
///     bool 1 byte, 0 or 1; list&lt;string&gt; items int32 byte length + UTF-8), a fixed-length list included
/// </code>
/// <para>2026-11-16.1 removed the vector encoding (N float32 values without a count): a vector is a
/// <c>list&lt;float32,N&gt;</c>, encoded as every list is, and a record holds what the connector wrote, whose length the
/// reader checks against the type as it does a connector's answer. Folding <c>text</c> into <c>string</c> left the format
/// as it was: both were already an int32 byte length and UTF-8, and a field once declared <c>text</c> is declared
/// <c>string</c> now, a field set of another fingerprint, so its old records are misses rather than misread.</para>
/// <para>The fingerprint covers the format, the key type and every field's name and storage type in field order (the
/// table's order, by name), so two instances whose catalogs bind different fields to the source never decode each
/// other's records: a reader that finds another fingerprint treats the record as a miss. Values are not aligned: the
/// record is a compact wire form, copied into aligned storage when read.</para>
/// </remarks>
internal static class CacheRecord
{
    /// <summary>The format version 2026-11-16.1 as a number: date, then that day's sequence in two digits.</summary>
    public const uint Format = 2026111601;
    public const byte AbsentFlag = 1;
    private const int FlagsOffset = 4;
    private const int FieldCountOffset = 6;
    private const int WrittenOffset = 8;
    private const int FingerprintOffset = 16;
    internal const int ValidityOffset = 24;

    /// <summary>Writes the header (the validity bits are the caller's), as <see cref="ReadHeader"/> reads it.</summary>
    public static void WriteHeader(Span<byte> header, ushort fieldCount, long writtenMs, ulong fingerprint, bool absent)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, Format);
        header[FlagsOffset] = absent ? AbsentFlag : (byte)0;
        header[FlagsOffset + 1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header[FieldCountOffset..], fieldCount);
        BinaryPrimitives.WriteInt64LittleEndian(header[WrittenOffset..], writtenMs);
        BinaryPrimitives.WriteUInt64LittleEndian(header[FingerprintOffset..], fingerprint);
    }

    /// <summary>Bytes of a record before its values: the header and the validity bits.</summary>
    public static int PrefixSize(int fieldCount) => ValidityOffset + ((fieldCount + 7) / 8);

    /// <summary>The fingerprint of a table's field set, as records written for it carry.</summary>
    /// <remarks>
    /// Over the canonical encoding of <see cref="StableHash"/>, where every string is tagged and length-prefixed: field
    /// names are free text (a tab or a newline is legal), so a separator-joined text would let two different field sets
    /// spell the same bytes, e.g. <c>["a\tfloat64\nb", "c"]</c> and <c>["a", "b\tfloat64\nc"]</c>, and read each
    /// other's records.
    /// </remarks>
    public static ulong Fingerprint(LocalTableSchema schema)
    {
        using var hash = new StableHash.Builder();
        hash.Add("stratarec.cache.record");
        hash.Add((long)Format);
        hash.Add(schema.KeyType == EntityKeyType.String ? "string" : "int64");
        hash.Add((long)schema.Fields.Count);
        foreach (var field in schema.Fields)
        {
            hash.Add(field.Name);
            hash.Add(field.Type.ToString());
        }

        return hash.Finish();
    }

    /// <summary>Reads the header; <paramref name="writtenMs"/> and <paramref name="absent"/> are meaningful when <see cref="CacheRecordHeader.Ok"/>.</summary>
    public static CacheRecordHeader ReadHeader(ReadOnlySpan<byte> record, int fieldCount, ulong fingerprint, out long writtenMs, out bool absent)
    {
        writtenMs = 0;
        absent = false;
        if (record.Length < PrefixSize(fieldCount))
        {
            return record.Length >= ValidityOffset && BinaryPrimitives.ReadUInt32LittleEndian(record) != Format
                ? CacheRecordHeader.Mismatch
                : CacheRecordHeader.Corrupt;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Format
            || BinaryPrimitives.ReadUInt16LittleEndian(record[FieldCountOffset..]) != fieldCount
            || BinaryPrimitives.ReadUInt64LittleEndian(record[FingerprintOffset..]) != fingerprint)
        {
            return CacheRecordHeader.Mismatch;
        }

        writtenMs = BinaryPrimitives.ReadInt64LittleEndian(record[WrittenOffset..]);
        absent = (record[FlagsOffset] & AbsentFlag) != 0;
        return CacheRecordHeader.Ok;
    }

    /// <summary>
    /// Whether field <paramref name="field"/> of a record whose header was found <see cref="CacheRecordHeader.Ok"/> has a
    /// value: its validity bit, which is what <see cref="Read"/> walks (an absent record has none set).
    /// </summary>
    public static bool HasValue(ReadOnlySpan<byte> record, int field) => (record[ValidityOffset + (field >> 3)] & (1 << (field & 7))) != 0;

    /// <summary>The write time of a record whose header was found <see cref="CacheRecordHeader.Ok"/>.</summary>
    public static long WrittenMs(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadInt64LittleEndian(record[WrittenOffset..]);

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
                case StorageKind.String:
                    if (!TryLength(rest, 1, out var bytes))
                    {
                        return false;
                    }

                    visitor.String(f, rest.Slice(4, bytes));
                    size = 4 + bytes;
                    break;
                case StorageKind.List:
                    if (!TryList(rest, layout.Elements[f], f, ref visitor, out size))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }

            at += size;
        }

        return at == record.Length;
    }

    /// <summary>Walks a list value at the start of <paramref name="rest"/>; false when it runs past it or is not one.</summary>
    private static bool TryList<TVisitor>(ReadOnlySpan<byte> rest, StorageKind element, int f, ref TVisitor visitor, out int size)
        where TVisitor : struct, ICacheRecordVisitor
    {
        size = 0;
        int bytes;
        switch (element)
        {
            case StorageKind.Bool:
                if (!TryLength(rest, 1, out bytes) || rest.Slice(4, bytes).IndexOfAnyExcept((byte)0, (byte)1) >= 0)
                {
                    return false; // a byte other than 0 or 1 is not a bool this codec wrote, and would not compare as one
                }

                visitor.List(f, MemoryMarshal.Cast<byte, bool>(rest.Slice(4, bytes)));
                break;
            case StorageKind.Int32:
                if (!TryLength(rest, 4, out bytes))
                {
                    return false;
                }

                visitor.List(f, MemoryMarshal.Cast<byte, int>(rest.Slice(4, bytes)));
                break;
            case StorageKind.Int64:
                if (!TryLength(rest, 8, out bytes))
                {
                    return false;
                }

                visitor.List(f, MemoryMarshal.Cast<byte, long>(rest.Slice(4, bytes)));
                break;
            case StorageKind.Float32:
                if (!TryLength(rest, 4, out bytes))
                {
                    return false;
                }

                visitor.List(f, MemoryMarshal.Cast<byte, float>(rest.Slice(4, bytes)));
                break;
            case StorageKind.Float64:
                if (!TryLength(rest, 8, out bytes))
                {
                    return false;
                }

                visitor.List(f, MemoryMarshal.Cast<byte, double>(rest.Slice(4, bytes)));
                break;
            default:
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
                return true;
        }

        size = 4 + bytes;
        return true;
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

        public void String(int field, ReadOnlySpan<byte> utf8)
        {
        }

        public void List<T>(int field, ReadOnlySpan<T> value)
            where T : unmanaged
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

    /// <summary>A <c>string</c> value. The span is the record's own bytes.</summary>
    void String(int field, ReadOnlySpan<byte> utf8);

    /// <summary>A list of fixed-size elements: <c>bool</c>, <c>int</c>, <c>long</c>, <c>float</c> or <c>double</c>, as the
    /// field's element type is stored. The span is the record's own bytes: copy what is kept.</summary>
    void List<T>(int field, ReadOnlySpan<T> value)
        where T : unmanaged;

    void StringListStart(int field, int count);

    void StringListItem(int field, ReadOnlySpan<byte> utf8);

    void StringListEnd(int field);
}

/// <summary>The storage kind of each field of a table (and a list's element kind), as the codec walks them; built once per table.</summary>
internal sealed class CacheRecordLayout
{
    public CacheRecordLayout(LocalTableSchema schema)
    {
        FieldCount = schema.FieldCount;
        Kinds = new StorageKind[FieldCount];
        Elements = new StorageKind[FieldCount];
        var size = CacheRecord.PrefixSize(FieldCount);
        for (var f = 0; f < FieldCount; f++)
        {
            var type = schema.Fields[f].Type;
            Kinds[f] = type.Kind;
            Elements[f] = type.Element;
            size += type.Kind switch
            {
                StorageKind.Bool => 1,
                StorageKind.Int32 or StorageKind.Float32 => 4,
                StorageKind.Int64 or StorageKind.Float64 => 8,
                // A fixed-length list of fixed-size elements (an embedding) has a known size: its count and N elements.
                StorageKind.List when type.IsFixedLength && type.Element != StorageKind.String =>
                    4 + (type.FixedLength * (type.Element switch
                    {
                        StorageKind.Bool => 1,
                        StorageKind.Int32 or StorageKind.Float32 => 4,
                        _ => 8,
                    })),
                _ => 4 + 16, // a length and a short value
            };
        }

        Fingerprint = CacheRecord.Fingerprint(schema);

        TypicalSize = size;
    }

    /// <summary>The size of a record with every field present and short variable-length values: what a batch of records
    /// is sized by up front, so that encoding it rarely grows the array.</summary>
    public int TypicalSize { get; }

    public int FieldCount { get; }

    public StorageKind[] Kinds { get; }

    /// <summary>Each list field's element kind; 0 for other fields.</summary>
    public StorageKind[] Elements { get; }

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
        CacheRecord.WriteHeader(header, checked((ushort)_layout.FieldCount), writtenMs, fingerprint, absent);
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

    public void String(int field, string value)
    {
        Present(field, StorageKind.String);
        WriteString(value);
    }

    /// <summary>A list of fixed-size elements, stored as the field's element type is (see <see cref="ICacheRecordVisitor.List"/>).</summary>
    public void List<T>(int field, ReadOnlySpan<T> value)
        where T : unmanaged
    {
        PresentList(field, ColumnValues.ElementKind<T>());
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value.Length);
        MemoryMarshal.AsBytes(value).CopyTo(Reserve(Unsafe.SizeOf<T>() * value.Length));
    }

    public void StringList(int field, ReadOnlySpan<string> value)
    {
        PresentList(field, StorageKind.String);
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value.Length);
        foreach (var item in value)
        {
            WriteString(item);
        }
    }

    /// <summary>The row of a list column (of the field's element type) as the field's value.</summary>
    public void List(int field, ListColumn column, int row)
    {
        switch (column)
        {
            case ListColumn<bool> c: List(field, c.Get(row)); break;
            case ListColumn<int> c: List(field, c.Get(row)); break;
            case ListColumn<long> c: List(field, c.Get(row)); break;
            case ListColumn<float> c: List(field, c.Get(row)); break;
            case ListColumn<double> c: List(field, c.Get(row)); break;
            case ListColumn<string> c: StringList(field, c.Get(row)); break;
            default: throw new NotSupportedException($"Unsupported column {column.GetType().Name}.");
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

    private void PresentList(int field, StorageKind element)
    {
        Present(field, StorageKind.List);
        if (_layout.Elements[field] != element)
        {
            throw new ArgumentException($"Field {field} is a list of {_layout.Elements[field]}, not of {element}.", nameof(field));
        }
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
