namespace StrataRec.Core.Columns;

/// <summary>
/// Range checks and conversions between int64 values (the expression type of every integer) and int32 storage
/// (<c>int32</c>, <c>list&lt;int32&gt;</c>, design 4.3).
/// </summary>
/// <remarks>
/// Narrowing never wraps: a writer first checks that the value fits and otherwise applies the out-of-range rule
/// (the column's default, or missing, and a count). The checks are branch-free OR reductions so the in-range path of a
/// whole column or list costs one compare per element.
/// </remarks>
public static class Int32Range
{
    /// <summary>True when <paramref name="value"/> is in <c>[int.MinValue, int.MaxValue]</c>.</summary>
    public static bool Fits(long value) => OutsideBits(value) == 0;

    /// <summary>True when every element fits in int32.</summary>
    public static bool AllFit(ReadOnlySpan<long> values)
    {
        ulong outside = 0;
        foreach (var v in values)
        {
            outside |= OutsideBits(v);
        }

        return outside == 0;
    }

    /// <summary>Copies values that are known to fit (see <see cref="AllFit"/>) into int32 storage.</summary>
    public static void Narrow(ReadOnlySpan<long> source, Span<int> destination)
    {
        destination = destination[..source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = (int)source[i];
        }
    }

    /// <summary>Copies int32 storage into int64 values (exact).</summary>
    public static void Widen(ReadOnlySpan<int> source, Span<long> destination)
    {
        destination = destination[..source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = source[i];
        }
    }

    /// <summary>Copies int32 storage into a new int64 array (cold paths: <see cref="Values.ScalarValue"/> reads).</summary>
    public static long[] ToInt64Array(ReadOnlySpan<int> source)
    {
        if (source.IsEmpty)
        {
            return Array.Empty<long>();
        }

        var result = new long[source.Length];
        Widen(source, result);
        return result;
    }

    /// <summary>Nonzero exactly when <paramref name="value"/> is outside the int32 range: shifting by 2^31 maps the
    /// range onto <c>[0, 2^32)</c>, so any bit above 31 (including the wrap of very large values) means "outside".</summary>
    private static ulong OutsideBits(long value) => unchecked((ulong)(value - int.MinValue)) >> 32;
}
