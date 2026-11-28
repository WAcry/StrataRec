using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Keys;

namespace StrataRec.Features.Local;

/// <remarks>
/// <para>The canonical bytes are the parts in declared order: an int64 as 8 bytes little-endian; a string as its UTF-16
/// code units, preceded by its length in code units (int32 little-endian) unless it is the last part, whose length is
/// the rest of the key. The encoding lives in memory only; it may change with the binary.</para>
/// </remarks>
internal static class TableKeys
{
    public const int StackBytes = 256;

    public static bool IsHashed(EntityKeyLayout layout) => layout.PartCount != 1 || layout[0].Type != EntityKeyType.Int64;

    public static int Length(ReadOnlySpan<EntityKey> parts)
    {
        var length = 0L;
        var last = parts.Length - 1;
        for (var p = 0; p < parts.Length; p++)
        {
            length += parts[p].StringValue is { } s ? (2L * s.Length) + (p == last ? 0 : 4) : 8;
        }

        return length <= int.MaxValue / 2 ? (int)length : throw new ArgumentException($"A key of {length} bytes is too long for a local table.", nameof(parts));
    }

    /// <summary>Writes the canonical bytes of a key into <paramref name="destination"/> (at least <see cref="Length"/> long);
    /// returns how many.</summary>
    public static int Encode(ReadOnlySpan<EntityKey> parts, Span<byte> destination)
    {
        var at = 0;
        var last = parts.Length - 1;
        for (var p = 0; p < parts.Length; p++)
        {
            if (parts[p].StringValue is not { } value)
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination[at..], parts[p].Int64Value);
                at += 8;
                continue;
            }

            var chars = value.AsSpan();
            if (p != last)
            {
                BinaryPrimitives.WriteInt32LittleEndian(destination[at..], chars.Length);
                at += 4;
            }

            MemoryMarshal.AsBytes(chars).CopyTo(destination[at..]);
            at += 2 * chars.Length;
        }

        return at;
    }

    public static string? Check(EntityKeyLayout layout, ReadOnlySpan<EntityKey> parts)
    {
        if (parts.Length != layout.PartCount)
        {
            return $"a key of {parts.Length} part(s); the table is keyed by {layout.PartCount}: {Describe(layout)}";
        }

        for (var p = 0; p < parts.Length; p++)
        {
            if (parts[p].IsString != (layout[p].Type == EntityKeyType.String))
            {
                return $"part {p} ({layout[p].Reference}) is {(parts[p].IsString ? "a string" : "an int64")}; the table is keyed by {Describe(layout)}";
            }
        }

        return null;
    }

    public static int StringMask(EntityKeyLayout layout)
    {
        var mask = 0;
        for (var p = 0; p < layout.PartCount; p++)
        {
            mask |= layout[p].Type == EntityKeyType.String ? 1 << p : 0;
        }

        return mask;
    }

    /// <summary><see cref="Check"/> for a hot loop: whether <paramref name="parts"/>, as many as the layout has, are each of
    /// its part's type, the layout given by its <see cref="StringMask"/>.</summary>
    public static bool Matches(int stringMask, ReadOnlySpan<EntityKey> parts)
    {
        var mask = 0;
        for (var p = 0; p < parts.Length; p++)
        {
            mask |= parts[p].IsString ? 1 << p : 0;
        }

        return mask == stringMask;
    }

    public static string Describe(EntityKeyLayout layout) => layout.TypeText;
}

[InlineArray(EntityKeyLayout.MaxParts)]
internal struct KeyPartBuffer
{
    private EntityKey _element;
}
