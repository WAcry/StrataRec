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

internal enum CacheRecordHeader : byte
{
    Ok = 1,

    /// <summary>Another format version, or another field set (an instance on another catalog version wrote it): a miss.</summary>
    Mismatch,

    /// <summary>Too short, or not a record at all: a miss.</summary>
    Corrupt,
}

internal static class CacheRecord
{
    public const uint Format = 2026111601;
    public const byte AbsentFlag = 1;
    private const int FlagsOffset = 4;
    private const int FieldCountOffset = 6;
    private const int WrittenOffset = 8;
    private const int FingerprintOffset = 16;
    internal const int ValidityOffset = 24;

    /// <summary>Writes the header (the validity bits are the caller's).</summary>
    public static void WriteHeader(Span<byte> header, ushort fieldCount, long writtenMs, ulong fingerprint, bool absent)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, Format);
        header[FlagsOffset] = absent ? AbsentFlag : (byte)0;
        header[FlagsOffset + 1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header[FieldCountOffset..], fieldCount);
        BinaryPrimitives.WriteInt64LittleEndian(header[WrittenOffset..], writtenMs);
        BinaryPrimitives.WriteUInt64LittleEndian(header[FingerprintOffset..], fingerprint);
    }

    public static int PrefixSize(int fieldCount) => ValidityOffset + ((fieldCount + 7) / 8);

    /// <remarks>
    /// Over the canonical encoding of <see cref="StableHash"/>, where every string is tagged and length-prefixed: field
    /// names are free text (a tab or a newline is legal), so a separator-joined text would let two different field sets
    /// spell the same bytes, and read each other's records.
    /// </remarks>
    public static ulong Fingerprint(LocalTableSchema schema)
    {
        using var hash = new StableHash.Builder();
        hash.Add("stratarec.cache.record");
        hash.Add((long)Format);
        hash.Add(schema.Key.TypeText);
        hash.Add((long)schema.Fields.Count);
        foreach (var field in schema.Fields)
        {
            hash.Add(field.Name);
            hash.Add(field.Type.ToString());
        }

        return hash.Finish();
    }

    /// <summary><paramref name="writtenMs"/> and <paramref name="absent"/> are meaningful when
    /// <see cref="CacheRecordHeader.Ok"/>.</summary>
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
    /// Whether field <paramref name="field"/> of a record whose header was found <see cref="CacheRecordHeader.Ok"/> has
    /// a value.
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

    public static bool IsWellFormed(ReadOnlySpan<byte> record, CacheRecordLayout layout)
    {
        var none = default(NoVisitor);
        return Read(record, layout, ref none);
    }

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

internal interface ICacheRecordVisitor
{
    void Missing(int field);

    void Bool(int field, bool value);

    void Int32(int field, int value);

    void Int64(int field, long value);

    void Float32(int field, float value);

    void Float64(int field, double value);

    /// <summary>The span is the record's own bytes.</summary>
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
                StorageKind.List when type.IsFixedLength && type.Element != StorageKind.String =>
                    4 + (type.FixedLength * (type.Element switch
                    {
                        StorageKind.Bool => 1,
                        StorageKind.Int32 or StorageKind.Float32 => 4,
                        _ => 8,
                    })),
                _ => 4 + 16,
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
/// A record is begun, its fields are added in field order (a field not added is missing), and it is ended, which gives
/// its offset and length; slices are taken from <see cref="Bytes"/> only once every record is written, since the array
/// is replaced when it grows.
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
