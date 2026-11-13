using System.Buffers;
using System.Numerics.Tensors;

namespace StrataRec.Expressions.Runtime;

/// <summary>
/// Transcendental kernels shared by every evaluator.
/// </summary>
/// <remarks>
/// <para>Vectorized <see cref="TensorPrimitives"/> implementations of <c>exp</c>, <c>log</c>, <c>pow</c> and
/// <c>sigmoid</c> may differ from <see cref="Math"/> in the last bit. So that a value computed once per request
/// (hoisted, constant-folded) is bit-identical to the same value computed per row, the scalar entry points run the
/// same SIMD kernel on a small broadcast span, and span entry points shorter than <see cref="Width"/> fall back to the
/// scalar entry point. Every element therefore always goes through the vector code path.</para>
/// <para><c>log1p</c> uses the accurate form <c>log(u) * (x / (u - 1))</c> with <c>u = 1 + x</c> (Goldberg), built on the
/// same <c>log</c> kernel; .NET's <c>LogP1</c> is <c>log(1 + x)</c>, which loses all precision for tiny x.</para>
/// </remarks>
internal static class MathKernels
{
    /// <summary>At least the widest vector (Vector512 of double), so scalar calls take the SIMD path.</summary>
    public const int Width = 8;

    public static double Exp(double x)
    {
        Span<double> s = stackalloc double[Width];
        s.Fill(x);
        TensorPrimitives.Exp(s, s);
        return s[0];
    }

    public static double Log(double x)
    {
        Span<double> s = stackalloc double[Width];
        s.Fill(x);
        TensorPrimitives.Log(s, s);
        return s[0];
    }

    public static double Sigmoid(double x)
    {
        Span<double> s = stackalloc double[Width];
        s.Fill(x);
        TensorPrimitives.Sigmoid(s, s);
        return s[0];
    }

    public static double Pow(double x, double y)
    {
        Span<double> s = stackalloc double[Width];
        Span<double> t = stackalloc double[Width];
        s.Fill(x);
        t.Fill(y);
        TensorPrimitives.Pow(s, t, s);
        return s[0];
    }

    public static double Log1p(double x)
    {
        var u = 1.0 + x;
        return Log1pFinish(x, u, Log(u));
    }

    private static double Log1pFinish(double x, double u, double logU)
    {
        if (u == 1.0)
        {
            return x;
        }

        if (double.IsPositiveInfinity(u))
        {
            return u;
        }

        return logU * (x / (u - 1.0));
    }

    public static void Exp(ReadOnlySpan<double> x, Span<double> destination)
    {
        if (x.Length >= Width)
        {
            TensorPrimitives.Exp(x, destination);
            return;
        }

        for (var i = 0; i < x.Length; i++)
        {
            destination[i] = Exp(x[i]);
        }
    }

    public static void Log(ReadOnlySpan<double> x, Span<double> destination)
    {
        if (x.Length >= Width)
        {
            TensorPrimitives.Log(x, destination);
            return;
        }

        for (var i = 0; i < x.Length; i++)
        {
            destination[i] = Log(x[i]);
        }
    }

    public static void Sigmoid(ReadOnlySpan<double> x, Span<double> destination)
    {
        if (x.Length >= Width)
        {
            TensorPrimitives.Sigmoid(x, destination);
            return;
        }

        for (var i = 0; i < x.Length; i++)
        {
            destination[i] = Sigmoid(x[i]);
        }
    }

    public static void Pow(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> destination)
    {
        if (x.Length >= Width)
        {
            TensorPrimitives.Pow(x, y, destination);
            return;
        }

        for (var i = 0; i < x.Length; i++)
        {
            destination[i] = Pow(x[i], y[i]);
        }
    }

    public static void Pow(ReadOnlySpan<double> x, double y, Span<double> destination)
    {
        if (x.Length >= Width)
        {
            TensorPrimitives.Pow(x, y, destination);
            return;
        }

        for (var i = 0; i < x.Length; i++)
        {
            destination[i] = Pow(x[i], y);
        }
    }

    public static void Pow(double x, ReadOnlySpan<double> y, Span<double> destination)
    {
        if (y.Length >= Width)
        {
            TensorPrimitives.Pow(x, y, destination);
            return;
        }

        for (var i = 0; i < y.Length; i++)
        {
            destination[i] = Pow(x, y[i]);
        }
    }

    /// <summary>Vector log1p; <paramref name="destination"/> must not overlap <paramref name="x"/>.</summary>
    public static void Log1p(ReadOnlySpan<double> x, Span<double> destination)
    {
        var n = x.Length;
        if (n < Width)
        {
            for (var i = 0; i < n; i++)
            {
                destination[i] = Log1p(x[i]);
            }

            return;
        }

        var logBuffer = ArrayPool<double>.Shared.Rent(n);
        try
        {
            var u = destination[..n];
            var logU = logBuffer.AsSpan(0, n);
            TensorPrimitives.Add(x, 1.0, u);
            TensorPrimitives.Log(u, logU);
            for (var i = 0; i < n; i++)
            {
                u[i] = Log1pFinish(x[i], u[i], logU[i]);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(logBuffer);
        }
    }
}
