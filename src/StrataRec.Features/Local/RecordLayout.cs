using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Local;

/// <summary>
/// Byte layout of one record of a local table (design 6.6, item 1), derived once from the schema.
/// </summary>
/// <remarks>
/// <para>A record is a fixed part followed by its variable-length data, both in the same page, so the record is one
/// contiguous, self-contained byte range: copying it (compaction) needs no fix-ups other than string codes.</para>
/// <code>
/// 0   int64  version       event version (local) or write time (cache)
/// 8   int32  length        bytes of the whole record, fixed part plus variable data, a multiple of 8
/// 12  uint8  flags         bit 0: tombstone
/// 13  uint8[V] validity    bit f set = field f has a value (V = ceil(fields / 8))
/// …   int32  key offset    string-keyed tables only: the UTF-16 key, relative to the record start
///     int32  key length    in chars
/// H   fields               8-byte values first (int64, float64, variable-length references), then 4-byte values
///                          (int32, float32, string codes, vector elements), then bools; each at its natural alignment
/// F   variable data        text (UTF-16), lists (native elements; list&lt;string&gt; as string codes), the string key;
///                          each item 8-byte aligned
/// </code>
/// <para>A variable-length field's slot holds <c>(int32 offset relative to the record start, int32 element count)</c>.
/// A tombstone is just the header (validity all clear), plus the key for string-keyed tables.</para>
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
        StringKeys = schema.KeyType == EntityKeyType.String;
        ValidityBytes = (count + 7) / 8;
        var cursor = ValidityOffset + ValidityBytes;
        if (StringKeys)
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
        Offsets = new int[count];
        Dimensions = new int[count];
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
                Dimensions[f] = type.Kind == StorageKind.Vector ? type.Dimension : 0;
                Offsets[f] = cursor;
                Sizes[f] = size;
                VarSlots[f] = IsVariable(type.Kind) ? varCount++ : -1;
                cursor += size;
            }
        }

        FixedSize = Align(cursor, 8);
        VarFieldCount = varCount;
        VarFields = new int[varCount];
        for (var f = 0; f < count; f++)
        {
            if (VarSlots[f] >= 0)
            {
                VarFields[VarSlots[f]] = f;
            }
        }
    }

    public LocalTableSchema Schema { get; }

    public int FieldCount { get; }

    public bool StringKeys { get; }

    public int ValidityBytes { get; }

    /// <summary>Offset of the (key offset, key length) pair; -1 for int64-keyed tables.</summary>
    public int KeyRefOffset { get; }

    /// <summary>Bytes of the header, which is also the fixed part of a tombstone.</summary>
    public int HeaderSize { get; }

    /// <summary>Bytes of the fixed part of a (non-tombstone) record.</summary>
    public int FixedSize { get; }

    public StorageKind[] Kinds { get; }

    /// <summary>Offset of each field's value (or variable-length reference) from the record start.</summary>
    public int[] Offsets { get; }

    public int[] Dimensions { get; }

    /// <summary>Bytes each field occupies in the fixed part.</summary>
    public int[] Sizes { get; }

    /// <summary>Per field: its index among the variable-length fields, or -1.</summary>
    public int[] VarSlots { get; }

    public int VarFieldCount { get; }

    /// <summary>The field index of each variable-length field.</summary>
    public int[] VarFields { get; }

    public static bool IsVariable(StorageKind kind) => kind is StorageKind.Text or StorageKind.ListInt32 or StorageKind.ListInt64 or StorageKind.ListString;

    /// <summary>Bytes of one element of a variable-length field (UTF-16 chars, native list elements, string codes).</summary>
    public static int ElementSize(StorageKind kind) => kind switch
    {
        StorageKind.Text => 2,
        StorageKind.ListInt32 => 4,
        StorageKind.ListInt64 => 8,
        StorageKind.ListString => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a variable-length kind."),
    };

    public static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    public static long Align(long value, int alignment) => (value + alignment - 1) & ~(long)(alignment - 1);

    private static (int Size, int Align) SizeOf(ColumnType type) => type.Kind switch
    {
        StorageKind.Bool => (1, 1),
        StorageKind.Int32 or StorageKind.Float32 or StorageKind.String => (4, 4),
        StorageKind.Int64 or StorageKind.Float64 => (8, 8),
        StorageKind.Vector => (checked(4 * type.Dimension), 4),
        StorageKind.Text or StorageKind.ListInt32 or StorageKind.ListInt64 or StorageKind.ListString => (8, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported storage type."),
    };
}
