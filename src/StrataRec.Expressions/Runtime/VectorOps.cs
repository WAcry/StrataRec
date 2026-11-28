using System.Numerics;
using System.Numerics.Tensors;

namespace StrataRec.Expressions.Runtime;

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
