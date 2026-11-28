namespace StrataRec.Core.Columns;

/// <summary>
/// The range of float32 storage (<c>float32</c>, <c>list&lt;float32&gt;</c>) for float64 values, the expression
/// type of every float.
/// </summary>
/// <remarks>
/// A finite double beyond float32's largest value would be stored as an infinity. A value given in configuration or by
/// the request is refused. A computed one is stored, becomes ±∞, and the non-finite rule replaces it. NaN and ±∞ are
/// what they are in either width.
/// </remarks>
public static class Float32Range
{
    /// <summary>The range as configuration errors print it.</summary>
    public const string Text = "[-3.4028235E+38, 3.4028235E+38]";

    /// <summary>True unless a finite <paramref name="value"/> would round to an infinity in float32.</summary>
    public static bool Fits(double value) => !double.IsFinite(value) || float.IsFinite((float)value);

    /// <summary>True when every element fits (see <see cref="Fits"/>).</summary>
    public static bool AllFit(ReadOnlySpan<double> values)
    {
        foreach (var v in values)
        {
            if (!Fits(v))
            {
                return false;
            }
        }

        return true;
    }
}
