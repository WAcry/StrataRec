using System.Buffers.Binary;
using System.Runtime.InteropServices;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Local;

/// <summary>
/// Byte layout of one record of a local table, derived once from the schema.
/// </summary>
/// <remarks>
/// <para>A record is a fixed part followed by its variable-length data, both in the same page, so the record is one
/// contiguous, self-contained byte range: copying it (compaction) needs no fix-ups other than string codes.</para>
/// <code>
/// 0   int64  version       event version (local) or write time (cache)
/// 8   int32  length        bytes of the whole record, fixed part plus variable data, a multiple of 8
/// 12  uint8  flags         bit 0: tombstone
/// 13  uint8[V] validity    bit f set = field f has a value (V = ceil(fields / 8))
/// …   int32  key offset    hashed keys only: the key's canonical bytes (<see cref="TableKeys"/>), relative to the record start
///     int32  key length    in bytes
/// H   fields               8-byte values first (int64, float64, variable-length references), then 4-byte values
///                          (int32, float32, string codes), then bools; each at its natural alignment
/// F   variable data        lists (native elements, a bool one byte; list&lt;string&gt; as string codes), the key's bytes;
///                          each item 8-byte aligned
/// </code>
/// <para>A variable-length field's slot holds <c>(int32 offset relative to the record start, int32 element count)</c>.
/// A tombstone is just the header (validity all clear), plus the key's bytes for hashed keys.</para>
/// <para>A one-part int64 key is the index's key itself and is not stored. Every other key (one string part, or several
/// parts) is hashed: the index holds the XXH3-64 of its canonical bytes and the record the bytes, which a lookup compares.
/// A one-string key's bytes are its UTF-16 code units, as before keys had several parts.</para>
/// <para>A fixed-length list (<c>list&lt;float32,64&gt;</c>) is a list here like any other: the table stores what its writer
/// wrote, and a read checks the length (<see cref="TableVersion"/>'s fill, as a request's other writers do), so one layout
/// and one copy loop serve every list.</para>
/// </remarks>
internal sealed class RecordLayout
{
    public const int VersionOffset = 0;
    public const int LengthOffset = 8;
    public const int FlagsOffset = 12;
    public const int ValidityOffset = 13;
    public const byte TombstoneFlag = 1;

    public RecordLayout(LocalTableSchema schema)
    {
        Schema = schema;
        var count = schema.FieldCount;
        FieldCount = count;
        HashedKeys = TableKeys.IsHashed(schema.Key);
        ValidityBytes = (count + 7) / 8;
        var cursor = ValidityOffset + ValidityBytes;
        if (HashedKeys)
        {
            cursor = Align(cursor, 4);
            KeyRefOffset = cursor;
            cursor += 8;
        }
        else
        {
            KeyRefOffset = -1;
        }

        HeaderSize = Align(cursor, 8);
        Kinds = new StorageKind[count];
        Elements = new StorageKind[count];
        ElementSizes = new int[count];
        Offsets = new int[count];
        Sizes = new int[count];
        VarSlots = new int[count];
        var varCount = 0;
        cursor = HeaderSize;

        // Three passes by alignment keep every value naturally aligned without padding between fields.
        for (var pass = 0; pass < 3; pass++)
        {
            for (var f = 0; f < count; f++)
            {
                var type = schema.Fields[f].Type;
                var (size, align) = SizeOf(type);
                if ((pass == 0 && align != 8) || (pass == 1 && align != 4) || (pass == 2 && align != 1))
                {
                    continue;
                }

                Kinds[f] = type.Kind;
                Elements[f] = type.Element;
                ElementSizes[f] = VariableElementSize(type);
                Offsets[f] = cursor;
                Sizes[f] = size;
                VarSlots[f] = ElementSizes[f] > 0 ? varCount++ : -1;
                cursor += size;
            }
        }

        FixedSize = Align(cursor, 8);
        VarFieldCount = varCount;
        VarFields = new int[varCount];
        var dictionaryFields = new List<int>();
        for (var f = 0; f < count; f++)
        {
            if (VarSlots[f] >= 0)
            {
                VarFields[VarSlots[f]] = f;
            }

            if (IsCoded(f))
            {
                dictionaryFields.Add(f);
            }
        }

        DictionaryFields = dictionaryFields.ToArray();
    }

    public LocalTableSchema Schema { get; }

    public int FieldCount { get; }

    /// <summary>True unless the key is one int64 part: records hold the key's canonical bytes and the index their hash.</summary>
    public bool HashedKeys { get; }

    public int ValidityBytes { get; }

    /// <summary>Offset of the (key offset, key length in bytes) pair; -1 for a one-part int64 key.</summary>
    public int KeyRefOffset { get; }

    /// <summary>Bytes of the header, which is also the fixed part of a tombstone.</summary>
    public int HeaderSize { get; }

    /// <summary>Bytes of the fixed part of a (non-tombstone) record.</summary>
    public int FixedSize { get; }

    public StorageKind[] Kinds { get; }

    /// <summary>Each list field's element kind; 0 for other fields.</summary>
    public StorageKind[] Elements { get; }

    /// <summary>Bytes of one element of each variable-length field (a list's native element, a string code); 0 for a
    /// fixed-size field.</summary>
    public int[] ElementSizes { get; }

    /// <summary>Offset of each field's value (or variable-length reference) from the record start.</summary>
    public int[] Offsets { get; }

    /// <summary>Bytes each field occupies in the fixed part.</summary>
    public int[] Sizes { get; }

    /// <summary>Per field: its index among the variable-length fields, or -1.</summary>
    public int[] VarSlots { get; }

    public int VarFieldCount { get; }

    /// <summary>The field index of each variable-length field.</summary>
    public int[] VarFields { get; }

    /// <summary>The fields whose values are codes into the table's string dictionary (<c>string</c>, <c>list&lt;string&gt;</c>).</summary>
    public int[] DictionaryFields { get; }

    /// <summary>True for a field whose values are codes into the table's string dictionary: <c>string</c>, <c>list&lt;string&gt;</c>.</summary>
    public bool IsCoded(int field) => (Kinds[field] == StorageKind.List ? Elements[field] : Kinds[field]) == StorageKind.String;

    /// <summary>
    /// The string codes a coded field (<see cref="IsCoded"/>) of the record at <paramref name="offset"/> holds, in place: one
    /// for a <c>string</c>, the items of a <c>list&lt;string&gt;</c>; empty when the field has no value (a tombstone has
    /// none). What compaction renumbers and the dictionary's reference counts follow.
    /// </summary>
    public Span<int> Codes(byte[] page, int offset, int field)
    {
        if ((page[offset + ValidityOffset + (field >> 3)] & (1 << (field & 7))) == 0)
        {
            return default;
        }

        var at = offset + Offsets[field];
        if (Kinds[field] == StorageKind.String)
        {
            return MemoryMarshal.Cast<byte, int>(page.AsSpan(at, 4));
        }

        var relative = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at));
        var count = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(at + 4));
        return MemoryMarshal.Cast<byte, int>(page.AsSpan(offset + relative, 4 * count));
    }

    /// <summary>Bytes of one element of a variable-length type (native list elements, string codes); 0 for a fixed-size
    /// type.</summary>
    private static int VariableElementSize(ColumnType type) => type.Kind switch
    {
        StorageKind.List => type.Element switch
        {
            StorageKind.Bool => 1,
            StorageKind.Int32 or StorageKind.Float32 or StorageKind.String => 4,
            _ => 8,
        },
        _ => 0,
    };

    public static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    public static long Align(long value, int alignment) => (value + alignment - 1) & ~(long)(alignment - 1);

    private static (int Size, int Align) SizeOf(ColumnType type) => type.Kind switch
    {
        StorageKind.Bool => (1, 1),
        StorageKind.Int32 or StorageKind.Float32 or StorageKind.String => (4, 4),
        StorageKind.Int64 or StorageKind.Float64 => (8, 8),
        StorageKind.List => (8, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported storage type."),
    };
}
