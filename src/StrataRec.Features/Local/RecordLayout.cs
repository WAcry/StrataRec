using System.Buffers.Binary;
using System.Runtime.InteropServices;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Local;

/// <remarks>A record is its fixed part and its variable-length data in one contiguous byte range of one page: copying
/// it (compaction) needs no fix-ups other than string codes.</remarks>
internal sealed class RecordLayout
{
    public const int VersionOffset = 0;
    public const int LengthOffset = 8;
    public const int FlagsOffset = 12;
    public const int ValidityOffset = 13;
    public const byte TombstoneFlag = 1;
    public const byte CacheHitFlag = 2;

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

    public bool HashedKeys { get; }

    public int ValidityBytes { get; }

    /// <summary>Offset of the (key offset, key length in bytes) pair; -1 for a one-part int64 key.</summary>
    public int KeyRefOffset { get; }

    /// <summary>Bytes of the header, which is also the fixed part of a tombstone.</summary>
    public int HeaderSize { get; }

    public int FixedSize { get; }

    public StorageKind[] Kinds { get; }

    public StorageKind[] Elements { get; }

    /// <summary>Bytes of one element of each variable-length field (a list's native element, a string code); 0 for a
    /// fixed-size field.</summary>
    public int[] ElementSizes { get; }

    /// <summary>Offset of each field's value (or variable-length reference) from the record start.</summary>
    public int[] Offsets { get; }

    public int[] Sizes { get; }

    /// <summary>Per field: its index among the variable-length fields, or -1.</summary>
    public int[] VarSlots { get; }

    public int VarFieldCount { get; }

    public int[] VarFields { get; }

    public int[] DictionaryFields { get; }

    public bool IsCoded(int field) => (Kinds[field] == StorageKind.List ? Elements[field] : Kinds[field]) == StorageKind.String;

    /// <summary>
    /// The string codes a coded field (<see cref="IsCoded"/>) of the record at <paramref name="offset"/> holds, in
    /// place; empty when the field has no value (a tombstone has none). What compaction renumbers and the dictionary's
    /// reference counts follow.
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
