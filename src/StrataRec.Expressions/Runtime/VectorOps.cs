using System.Numerics;
using System.Numerics.Tensors;

namespace StrataRec.Expressions.Runtime;

/// <summary>
/// <c>dot</c>, <c>cosine</c> and <c>l2_norm</c> over float lists, in the precision their operands are
/// declared in: float32 when every operand is declared <c>list&lt;float32&gt;</c> (a column, a context value, or a choice
/// between such lists), float64 when any is declared <c>list&lt;float64&gt;</c> or is a literal. The binder decides
/// (<see cref="Ir.Float32Lists"/>); each function's <c>T</c> is that precision, and both backends pass lists of it.
/// </summary>
/// <remarks>
/// <para>Why by declared type: embeddings are float32, and scoring a request's embedding against every candidate's is
/// the common case, so it must cost what TensorPrimitives' float32 kernels cost (twice the lanes of float64 per SIMD
/// register, no widening): one float64 kernel for every list measured up to 2.4x slower than that on 5,000 rows. The
/// float32 sum rounds at the precision the embeddings have; a user who wants float64 declares <c>list&lt;float64&gt;</c>.</para>
/// <para>Same bits in every backend: TensorPrimitives' result depends only on the values and their count, not on where a
/// span lies in memory (checked for 1-4,096 elements at every offset within a 512-bit vector), and a list's values are
/// the same everywhere. The vectorized backend reads float32 storage in place; the scalar one holds every list as
/// float64 (a float32 element is exactly a double) and narrows a float32 list back, exactly, to call the same function.
/// A float32 list in a float64 call is widened, exactly, in both.</para>
/// <para>Lists of different lengths have no dot product or cosine (false: the caller's result is missing). An empty dot
/// product is 0 and an empty norm 0; an empty cosine is NaN, and so is a cosine with a zero norm (0 / 0). The cosine is
/// dot / (|a| |b|) in float64 over the three results of <c>T</c>, as MmrDiversifier@1 computes it.</para>
/// </remarks>
internal static class VectorOps
{
    public static bool TryDot<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, out double dot)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        if (a.Length != b.Length)
        {
            dot = 0;
            return false;
        }

        dot = a.IsEmpty ? 0 : double.CreateTruncating(TensorPrimitives.Dot(a, b));
        return true;
    }

    public static double L2Norm<T>(ReadOnlySpan<T> a)
        where T : unmanaged, IFloatingPointIeee754<T> =>
        a.IsEmpty ? 0 : double.CreateTruncating(TensorPrimitives.Norm(a));

    public static bool TryCosine<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, out double cosine)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        if (a.Length != b.Length)
        {
            cosine = 0;
            return false;
        }

        if (a.IsEmpty)
        {
            cosine = double.NaN;
            return true;
        }

        var dot = double.CreateTruncating(TensorPrimitives.Dot(a, b));
        cosine = dot / (double.CreateTruncating(TensorPrimitives.Norm(a)) * double.CreateTruncating(TensorPrimitives.Norm(b)));
        return true;
    }
}
