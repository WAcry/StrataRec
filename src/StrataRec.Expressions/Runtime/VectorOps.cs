using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StrataRec.Expressions.Runtime;

/// <summary>
/// <c>dot</c>, <c>cosine</c> and <c>l2_norm</c> over float lists (design 7.6): <c>float</c> (list&lt;float32&gt; storage) or
/// <c>double</c> elements (list&lt;float64&gt; storage, request-level values and literals, which hold every float as
/// float64), in any mix.
/// </summary>
/// <remarks>
/// <para>Every function computes in float64 over the values the elements read as (a float32 element is exactly a double),
/// with one accumulation order whatever the element types: the same values give the same bits whether a list is a float32
/// column, a float64 one or a request-level value. So the scalar backend, which reads every list as float64, and the
/// vectorized one, which reads storage in place, agree, and no list is widened into a buffer first: float32 elements are
/// widened in registers (<see cref="Vector.Widen(Vector{float}, out Vector{double}, out Vector{double})"/>). A float32
/// embedding costs about twice TensorPrimitives' float32 dot, for a sum that does not round at float32 precision.</para>
/// <para>Lists of different lengths have no dot product or cosine (false: the caller's result is missing). An empty dot
/// product is 0 and an empty norm 0; an empty cosine is NaN, and so is a cosine with a zero norm (0 / 0).</para>
/// </remarks>
internal static class VectorOps
{
    public static bool TryDot<TA, TB>(ReadOnlySpan<TA> a, ReadOnlySpan<TB> b, out double dot)
        where TA : unmanaged
        where TB : unmanaged
    {
        if (a.Length != b.Length)
        {
            dot = 0;
            return false;
        }

        dot = Dot(a, b);
        return true;
    }

    public static double L2Norm<T>(ReadOnlySpan<T> a)
        where T : unmanaged => Math.Sqrt(Dot(a, a));

    /// <summary>dot / (|a| |b|), each in the order of <see cref="Dot{TA, TB}"/>.</summary>
    public static bool TryCosine<TA, TB>(ReadOnlySpan<TA> a, ReadOnlySpan<TB> b, out double cosine)
        where TA : unmanaged
        where TB : unmanaged
    {
        if (a.Length != b.Length)
        {
            cosine = 0;
            return false;
        }

        cosine = a.IsEmpty ? double.NaN : Dot(a, b) / (Math.Sqrt(Dot(a, a)) * Math.Sqrt(Dot(b, b)));
        return true;
    }

    /// <summary>
    /// The dot product of two lists of the same length, in float64. Blocks of <see cref="Vector{T}.Count"/> floats (two
    /// <see cref="Vector{T}"/> of doubles) go to four accumulators, two blocks at a time, then one block, then the tail
    /// element by element; the order depends only on the length, never on the element types.
    /// </summary>
    private static double Dot<TA, TB>(ReadOnlySpan<TA> a, ReadOnlySpan<TB> b)
        where TA : unmanaged
        where TB : unmanaged
    {
        var n = a.Length;
        var block = Vector<float>.Count;
        Vector<double> s0 = Vector<double>.Zero, s1 = Vector<double>.Zero, s2 = Vector<double>.Zero, s3 = Vector<double>.Zero;
        var i = 0;
        for (; i <= n - (2 * block); i += 2 * block)
        {
            Load(a, i, out var a0, out var a1);
            Load(b, i, out var b0, out var b1);
            s0 += a0 * b0;
            s1 += a1 * b1;
            Load(a, i + block, out a0, out a1);
            Load(b, i + block, out b0, out b1);
            s2 += a0 * b0;
            s3 += a1 * b1;
        }

        if (i <= n - block)
        {
            Load(a, i, out var a0, out var a1);
            Load(b, i, out var b0, out var b1);
            s0 += a0 * b0;
            s1 += a1 * b1;
            i += block;
        }

        var sum = Vector.Sum((s0 + s2) + (s1 + s3));
        for (; i < n; i++)
        {
            sum += At(a, i) * At(b, i);
        }

        return sum;
    }

    /// <summary>Elements <c>[i, i + Vector&lt;float&gt;.Count)</c> as two vectors of doubles, the first half in
    /// <paramref name="low"/>; the caller has checked they are in the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load<T>(ReadOnlySpan<T> span, int i, out Vector<double> low, out Vector<double> high)
        where T : unmanaged
    {
        // typeof(T) tests are constants once the method is compiled for T.
        if (typeof(T) == typeof(float))
        {
            ref var first = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<T, float>(span));
            Vector.Widen(Vector.LoadUnsafe(ref first, (nuint)i), out low, out high);
        }
        else
        {
            ref var first = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<T, double>(span));
            low = Vector.LoadUnsafe(ref first, (nuint)i);
            high = Vector.LoadUnsafe(ref first, (nuint)(i + Vector<double>.Count));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double At<T>(ReadOnlySpan<T> span, int i)
        where T : unmanaged =>
        typeof(T) == typeof(float) ? MemoryMarshal.Cast<T, float>(span)[i] : MemoryMarshal.Cast<T, double>(span)[i];
}
