using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace StrataRec.Expressions.Vectorized;

/// <summary>
/// Kernels that load column storage into lanes: SIMD widening of narrow storage (<c>float32</c> → <c>float64</c>,
/// <c>int32</c> → <c>int64</c>), gathers of values and validity bits for the gather row domain, and the row range check
/// of an evaluation.
/// </summary>
/// <remarks>
/// <para>A scalar <c>float</c> → <c>double</c> loop is latency-bound on x64 (<c>cvtss2sd</c> merges into its
/// destination register, chaining every iteration), about 1 ns per row; widening 8 lanes at a time removes that chain.
/// Widening is exact, so results are identical to the scalar conversion.</para>
/// <para>Gathers index without per-element bounds checks: <see cref="VectorProgram"/> checks every row against the
/// table's physical row count once per call, and each gather checks once that its source covers that many rows.</para>
/// </remarks>
internal static class LoadKernels
{
    /// <summary>Rows gathered per chunk before widening.</summary>
    private const int Chunk = 256;

    public static void Widen(ReadOnlySpan<float> source, Span<double> destination)
    {
        var n = source.Length;
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(destination[..n]);
            for (; i + 8 <= n; i += 8)
            {
                var (lower, upper) = Vector256.Widen(Vector256.LoadUnsafe(ref src, (nuint)i));
                lower.StoreUnsafe(ref dst, (nuint)i);
                upper.StoreUnsafe(ref dst, (nuint)(i + 4));
            }
        }

        for (; i < n; i++)
        {
            destination[i] = source[i];
        }
    }

    public static void Widen(ReadOnlySpan<int> source, Span<long> destination)
    {
        var n = source.Length;
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(destination[..n]);
            for (; i + 8 <= n; i += 8)
            {
                var (lower, upper) = Vector256.Widen(Vector256.LoadUnsafe(ref src, (nuint)i));
                lower.StoreUnsafe(ref dst, (nuint)i);
                upper.StoreUnsafe(ref dst, (nuint)(i + 4));
            }
        }

        for (; i < n; i++)
        {
            destination[i] = source[i];
        }
    }

    /// <summary><c>destination[i] = source[map[i]]</c>; every map entry must be in <c>[0, physical)</c>.</summary>
    public static void Gather<T>(ReadOnlySpan<T> source, ReadOnlySpan<int> map, int physical, Span<T> destination)
    {
        RequireRows(source.Length, physical);
        var n = map.Length;
        ref var src = ref MemoryMarshal.GetReference(source);
        ref var rows = ref MemoryMarshal.GetReference(map);
        ref var dst = ref MemoryMarshal.GetReference(destination[..n]);
        for (var i = 0; i < n; i++)
        {
            Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, Unsafe.Add(ref rows, i));
        }
    }

    /// <summary><c>destination[i] = source[map[i]]</c>, widened; every map entry must be in <c>[0, physical)</c>.</summary>
    public static void GatherWiden(ReadOnlySpan<float> source, ReadOnlySpan<int> map, int physical, Span<double> destination)
    {
        Span<float> buffer = stackalloc float[Chunk];
        for (var start = 0; start < map.Length; start += Chunk)
        {
            var count = Math.Min(Chunk, map.Length - start);
            Gather(source, map.Slice(start, count), physical, buffer);
            Widen(buffer[..count], destination.Slice(start, count));
        }
    }

    /// <summary><c>destination[i] = source[map[i]]</c>, widened; every map entry must be in <c>[0, physical)</c>.</summary>
    public static void GatherWiden(ReadOnlySpan<int> source, ReadOnlySpan<int> map, int physical, Span<long> destination)
    {
        Span<int> buffer = stackalloc int[Chunk];
        for (var start = 0; start < map.Length; start += Chunk)
        {
            var count = Math.Min(Chunk, map.Length - start);
            Gather(source, map.Slice(start, count), physical, buffer);
            Widen(buffer[..count], destination.Slice(start, count));
        }
    }

    /// <summary>
    /// <c>destination bit i = source bit map[i]</c>; bits at and beyond <c>map.Length</c> in the last word are zero.
    /// Every map entry must be in <c>[0, physical)</c>.
    /// </summary>
    public static void GatherBits(ReadOnlySpan<ulong> source, ReadOnlySpan<int> map, int physical, Span<ulong> destination)
    {
        RequireRows((long)source.Length * 64, physical);
        var n = map.Length;
        ref var src = ref MemoryMarshal.GetReference(source);
        ref var rows = ref MemoryMarshal.GetReference(map);
        var i = 0;
        for (; i + 64 <= n; i += 64)
        {
            // Four independent accumulators so the ORs do not form one 64-long dependency chain.
            ulong w0 = 0, w1 = 0, w2 = 0, w3 = 0;
            for (var j = 0; j < 64; j += 4)
            {
                var r0 = Unsafe.Add(ref rows, i + j);
                var r1 = Unsafe.Add(ref rows, i + j + 1);
                var r2 = Unsafe.Add(ref rows, i + j + 2);
                var r3 = Unsafe.Add(ref rows, i + j + 3);
                w0 |= ((Unsafe.Add(ref src, r0 >> 6) >> r0) & 1) << j;
                w1 |= ((Unsafe.Add(ref src, r1 >> 6) >> r1) & 1) << (j + 1);
                w2 |= ((Unsafe.Add(ref src, r2 >> 6) >> r2) & 1) << (j + 2);
                w3 |= ((Unsafe.Add(ref src, r3 >> 6) >> r3) & 1) << (j + 3);
            }

            destination[i >> 6] = w0 | w1 | w2 | w3;
        }

        if (i < n)
        {
            ulong w = 0;
            for (var j = 0; i + j < n; j++)
            {
                var r = Unsafe.Add(ref rows, i + j);
                w |= ((Unsafe.Add(ref src, r >> 6) >> r) & 1) << j;
            }

            destination[i >> 6] = w;
        }
    }

    /// <summary>True when every row is in <c>[0, physical)</c>.</summary>
    public static bool AllInRange(ReadOnlySpan<int> rows, int physical)
    {
        var limit = (uint)physical;
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var max = Vector256.Create(limit);
            ref var r = ref Unsafe.As<int, uint>(ref MemoryMarshal.GetReference(rows));
            for (; i + 8 <= rows.Length; i += 8)
            {
                if (Vector256.GreaterThanOrEqualAny(Vector256.LoadUnsafe(ref r, (nuint)i), max))
                {
                    return false;
                }
            }
        }

        for (; i < rows.Length; i++)
        {
            if ((uint)rows[i] >= limit)
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireRows(long available, int physical)
    {
        if (available < physical)
        {
            throw new InvalidOperationException($"A column buffer holds {available} rows but the table has {physical} physical rows.");
        }
    }
}
