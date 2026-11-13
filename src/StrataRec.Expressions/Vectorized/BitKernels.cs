using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using StrataRec.Expressions.Ir;

namespace StrataRec.Expressions.Vectorized;

/// <summary>
/// SIMD kernels producing or consuming packed bitmaps (bool values and validity): comparisons, selection by mask,
/// packing of byte booleans. Lanes at and beyond <c>n</c> in the last word are zero on output.
/// </summary>
internal static class BitKernels
{
    /// <summary>For each 4-bit pattern, a 4×64-bit lane mask (bit j set → lane j all ones).</summary>
    private static readonly Vector256<ulong>[] Masks4 = BuildMasks();

    private static Vector256<ulong>[] BuildMasks()
    {
        var masks = new Vector256<ulong>[16];
        for (var k = 0; k < 16; k++)
        {
            masks[k] = Vector256.Create(
                (k & 1) != 0 ? ulong.MaxValue : 0,
                (k & 2) != 0 ? ulong.MaxValue : 0,
                (k & 4) != 0 ? ulong.MaxValue : 0,
                (k & 8) != 0 ? ulong.MaxValue : 0);
        }

        return masks;
    }

    /// <summary>The comparison with swapped operands: <c>s op x</c> ⇔ <c>x Flip(op) s</c> (NaN-safe).</summary>
    public static IrOp Flip(IrOp op) => op switch
    {
        IrOp.Lt => IrOp.Gt,
        IrOp.Le => IrOp.Ge,
        IrOp.Gt => IrOp.Lt,
        IrOp.Ge => IrOp.Le,
        _ => op,
    };

    public static void Compare(IrOp op, ReadOnlySpan<double> a, ReadOnlySpan<double> b, double bScalar, bool bIsScalar, Span<ulong> dst, int n)
    {
        switch (op)
        {
            case IrOp.Eq: Compare<EqOp>(a, b, bScalar, bIsScalar, dst, n); break;
            case IrOp.Ne: Compare<NeOp>(a, b, bScalar, bIsScalar, dst, n); break;
            case IrOp.Lt: Compare<LtOp>(a, b, bScalar, bIsScalar, dst, n); break;
            case IrOp.Le: Compare<LeOp>(a, b, bScalar, bIsScalar, dst, n); break;
            case IrOp.Gt: Compare<GtOp>(a, b, bScalar, bIsScalar, dst, n); break;
            default: Compare<GeOp>(a, b, bScalar, bIsScalar, dst, n); break;
        }
    }

    public static void Compare(IrOp op, ReadOnlySpan<long> a, ReadOnlySpan<long> b, long bScalar, bool bIsScalar, Span<ulong> dst, int n)
    {
        switch (op)
        {
            case IrOp.Eq: Compare<EqOp>(a, b, bScalar, bIsScalar, dst, n); break;
            case IrOp.Ne: Compare<NeOp>(a, b, bScalar, bIsScalar, dst, n); break;
            case IrOp.Lt: Compare<LtOp>(a, b, bScalar, bIsScalar, dst, n); break;
            case IrOp.Le: Compare<LeOp>(a, b, bScalar, bIsScalar, dst, n); break;
            case IrOp.Gt: Compare<GtOp>(a, b, bScalar, bIsScalar, dst, n); break;
            default: Compare<GeOp>(a, b, bScalar, bIsScalar, dst, n); break;
        }
    }

    private static void Compare<TOp>(ReadOnlySpan<double> a, ReadOnlySpan<double> b, double bs, bool bScalar, Span<ulong> dst, int n)
        where TOp : struct, ICompareOp
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vbs = Vector256.Create(bs);
            for (; i + 64 <= n; i += 64)
            {
                ulong w = 0;
                for (var j = 0; j < 64; j += 4)
                {
                    var va = Vector256.LoadUnsafe(ref ra, (nuint)(i + j));
                    var vb = bScalar ? vbs : Vector256.LoadUnsafe(ref rb, (nuint)(i + j));
                    w |= (ulong)TOp.Vec(va, vb).ExtractMostSignificantBits() << j;
                }

                dst[i >> 6] = w;
            }
        }

        for (; i < n; i += 64)
        {
            ulong w = 0;
            var count = Math.Min(64, n - i);
            for (var j = 0; j < count; j++)
            {
                if (TOp.Scalar(a[i + j], bScalar ? bs : b[i + j]))
                {
                    w |= 1UL << j;
                }
            }

            dst[i >> 6] = w;
        }
    }

    private static void Compare<TOp>(ReadOnlySpan<long> a, ReadOnlySpan<long> b, long bs, bool bScalar, Span<ulong> dst, int n)
        where TOp : struct, ICompareOp
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vbs = Vector256.Create(bs);
            for (; i + 64 <= n; i += 64)
            {
                ulong w = 0;
                for (var j = 0; j < 64; j += 4)
                {
                    var va = Vector256.LoadUnsafe(ref ra, (nuint)(i + j));
                    var vb = bScalar ? vbs : Vector256.LoadUnsafe(ref rb, (nuint)(i + j));
                    w |= (ulong)TOp.Vec(va, vb).ExtractMostSignificantBits() << j;
                }

                dst[i >> 6] = w;
            }
        }

        for (; i < n; i += 64)
        {
            ulong w = 0;
            var count = Math.Min(64, n - i);
            for (var j = 0; j < count; j++)
            {
                if (TOp.Scalar(a[i + j], bScalar ? bs : b[i + j]))
                {
                    w |= 1UL << j;
                }
            }

            dst[i >> 6] = w;
        }
    }

    /// <summary><c>dst[i] = mask[i] ? a[i] : b[i]</c> where either side may be a broadcast scalar.</summary>
    public static void Select(WordView mask, ReadOnlySpan<double> a, double aS, bool aScalar, ReadOnlySpan<double> b, double bS, bool bScalar, Span<double> dst, int n)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        ref var rd = ref MemoryMarshal.GetReference(dst);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vas = Vector256.Create(aS);
            var vbs = Vector256.Create(bS);
            for (; i + 64 <= n; i += 64)
            {
                var w = mask[i >> 6];
                for (var j = 0; j < 64; j += 4)
                {
                    var m = Masks4[(int)((w >> j) & 0xF)].AsDouble();
                    var va = aScalar ? vas : Vector256.LoadUnsafe(ref ra, (nuint)(i + j));
                    var vb = bScalar ? vbs : Vector256.LoadUnsafe(ref rb, (nuint)(i + j));
                    Vector256.ConditionalSelect(m, va, vb).StoreUnsafe(ref rd, (nuint)(i + j));
                }
            }
        }

        for (; i < n; i++)
        {
            dst[i] = mask.Get(i) ? (aScalar ? aS : a[i]) : (bScalar ? bS : b[i]);
        }
    }

    public static void Select(WordView mask, ReadOnlySpan<long> a, long aS, bool aScalar, ReadOnlySpan<long> b, long bS, bool bScalar, Span<long> dst, int n)
    {
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        ref var rd = ref MemoryMarshal.GetReference(dst);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vas = Vector256.Create(aS);
            var vbs = Vector256.Create(bS);
            for (; i + 64 <= n; i += 64)
            {
                var w = mask[i >> 6];
                for (var j = 0; j < 64; j += 4)
                {
                    var m = Masks4[(int)((w >> j) & 0xF)].AsInt64();
                    var va = aScalar ? vas : Vector256.LoadUnsafe(ref ra, (nuint)(i + j));
                    var vb = bScalar ? vbs : Vector256.LoadUnsafe(ref rb, (nuint)(i + j));
                    Vector256.ConditionalSelect(m, va, vb).StoreUnsafe(ref rd, (nuint)(i + j));
                }
            }
        }

        for (; i < n; i++)
        {
            dst[i] = mask.Get(i) ? (aScalar ? aS : a[i]) : (bScalar ? bS : b[i]);
        }
    }

    /// <summary>Packs byte booleans (non-zero = true; garbage-safe) into bits.</summary>
    public static void Pack(ReadOnlySpan<byte> values, Span<ulong> dst, int n)
    {
        ref var rv = ref MemoryMarshal.GetReference(values);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 64 <= n; i += 64)
            {
                var lo = Vector256.Equals(Vector256.LoadUnsafe(ref rv, (nuint)i), Vector256<byte>.Zero).ExtractMostSignificantBits();
                var hi = Vector256.Equals(Vector256.LoadUnsafe(ref rv, (nuint)(i + 32)), Vector256<byte>.Zero).ExtractMostSignificantBits();
                dst[i >> 6] = ~((ulong)lo | ((ulong)hi << 32));
            }
        }

        for (; i < n; i += 64)
        {
            ulong w = 0;
            var count = Math.Min(64, n - i);
            for (var j = 0; j < count; j++)
            {
                if (values[i + j] != 0)
                {
                    w |= 1UL << j;
                }
            }

            dst[i >> 6] = w;
        }
    }

    /// <summary><c>dst bit i = codes[i] == code</c> over int32 dictionary codes.</summary>
    public static void EqualsCode(ReadOnlySpan<int> codes, int code, Span<ulong> dst, int n)
    {
        ref var rc = ref MemoryMarshal.GetReference(codes);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vc = Vector256.Create(code);
            for (; i + 64 <= n; i += 64)
            {
                ulong w = 0;
                for (var j = 0; j < 64; j += 8)
                {
                    w |= (ulong)Vector256.Equals(Vector256.LoadUnsafe(ref rc, (nuint)(i + j)), vc).ExtractMostSignificantBits() << j;
                }

                dst[i >> 6] = w;
            }
        }

        for (; i < n; i += 64)
        {
            ulong w = 0;
            var count = Math.Min(64, n - i);
            for (var j = 0; j < count; j++)
            {
                if (codes[i + j] == code)
                {
                    w |= 1UL << j;
                }
            }

            dst[i >> 6] = w;
        }
    }

    /// <summary>Builds a bitmap from a per-lane predicate result array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetBit(Span<ulong> dst, int lane, bool value)
    {
        if (value)
        {
            dst[lane >> 6] |= 1UL << lane;
        }
        else
        {
            dst[lane >> 6] &= ~(1UL << lane);
        }
    }
}

internal interface ICompareOp
{
    static abstract bool Scalar(double a, double b);

    static abstract bool Scalar(long a, long b);

    static abstract Vector256<double> Vec(Vector256<double> a, Vector256<double> b);

    static abstract Vector256<long> Vec(Vector256<long> a, Vector256<long> b);
}

internal readonly struct EqOp : ICompareOp
{
    public static bool Scalar(double a, double b) => a == b;

    public static bool Scalar(long a, long b) => a == b;

    public static Vector256<double> Vec(Vector256<double> a, Vector256<double> b) => Vector256.Equals(a, b);

    public static Vector256<long> Vec(Vector256<long> a, Vector256<long> b) => Vector256.Equals(a, b);
}

internal readonly struct NeOp : ICompareOp
{
    public static bool Scalar(double a, double b) => a != b;

    public static bool Scalar(long a, long b) => a != b;

    public static Vector256<double> Vec(Vector256<double> a, Vector256<double> b) => ~Vector256.Equals(a, b);

    public static Vector256<long> Vec(Vector256<long> a, Vector256<long> b) => ~Vector256.Equals(a, b);
}

internal readonly struct LtOp : ICompareOp
{
    public static bool Scalar(double a, double b) => a < b;

    public static bool Scalar(long a, long b) => a < b;

    public static Vector256<double> Vec(Vector256<double> a, Vector256<double> b) => Vector256.LessThan(a, b);

    public static Vector256<long> Vec(Vector256<long> a, Vector256<long> b) => Vector256.LessThan(a, b);
}

internal readonly struct LeOp : ICompareOp
{
    public static bool Scalar(double a, double b) => a <= b;

    public static bool Scalar(long a, long b) => a <= b;

    public static Vector256<double> Vec(Vector256<double> a, Vector256<double> b) => Vector256.LessThanOrEqual(a, b);

    public static Vector256<long> Vec(Vector256<long> a, Vector256<long> b) => Vector256.LessThanOrEqual(a, b);
}

internal readonly struct GtOp : ICompareOp
{
    public static bool Scalar(double a, double b) => a > b;

    public static bool Scalar(long a, long b) => a > b;

    public static Vector256<double> Vec(Vector256<double> a, Vector256<double> b) => Vector256.GreaterThan(a, b);

    public static Vector256<long> Vec(Vector256<long> a, Vector256<long> b) => Vector256.GreaterThan(a, b);
}

internal readonly struct GeOp : ICompareOp
{
    public static bool Scalar(double a, double b) => a >= b;

    public static bool Scalar(long a, long b) => a >= b;

    public static Vector256<double> Vec(Vector256<double> a, Vector256<double> b) => Vector256.GreaterThanOrEqual(a, b);

    public static Vector256<long> Vec(Vector256<long> a, Vector256<long> b) => Vector256.GreaterThanOrEqual(a, b);
}
