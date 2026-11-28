using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// Exact integers from JSON numbers, including the forms JSON writes an integer in besides digits (<c>3.0</c>,
/// <c>3e2</c>, <c>-25E-1</c>).
/// </summary>
public static class JsonIntegers
{
    private const int MaxDigits = 19;

    /// <summary>
    /// The integer a JSON number value denotes, when it has no fractional part and fits a long. Plain integer text is
    /// read without allocating.
    /// </summary>
    public static bool TryGetInt64(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue number || number.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        return number.TryGetValue(out value) || TryParse(number.ToJsonString(), out value);
    }

    /// <summary>The integer the JSON number text denotes, when it has no fractional part and fits a long.</summary>
    public static bool TryParse(ReadOnlySpan<char> number, out long value)
    {
        value = 0;
        if (!TryScan(number, out var negative, out var digits, out var scale))
        {
            return false;
        }

        if (digits.Length == 0)
        {
            return true;
        }

        if (scale < 0 || digits.Length + scale > MaxDigits)
        {
            return false;
        }

        ulong magnitude = 0;
        foreach (var c in digits)
        {
            magnitude = magnitude * 10 + (ulong)(c - '0'); // at most 19 digits: no overflow of ulong
        }

        for (var i = 0; i < scale; i++)
        {
            magnitude *= 10;
        }

        if (negative)
        {
            if (magnitude > 1UL << 63)
            {
                return false;
            }

            value = magnitude == 1UL << 63 ? long.MinValue : -(long)magnitude;
            return true;
        }

        if (magnitude > long.MaxValue)
        {
            return false;
        }

        value = (long)magnitude;
        return true;
    }

    /// <summary>True when the JSON number text has no fractional part, whatever its size.</summary>
    public static bool IsIntegral(ReadOnlySpan<char> number) => TryScan(number, out _, out var digits, out var scale) && (digits.Length == 0 || scale >= 0);

    /// <summary>
    /// Reads JSON number text as sign × <paramref name="digits"/> × 10^<paramref name="scale"/>, with the digits' leading
    /// and trailing zeros removed (none for zero), so the number is an integer exactly when the scale is not negative.
    /// </summary>
    private static bool TryScan(ReadOnlySpan<char> number, out bool negative, out string digits, out long scale)
    {
        negative = false;
        digits = string.Empty;
        scale = 0;
        var i = 0;
        if (i < number.Length && number[i] == '-')
        {
            negative = true;
            i++;
        }

        var integerStart = i;
        while (i < number.Length && char.IsAsciiDigit(number[i]))
        {
            i++;
        }

        var integer = number[integerStart..i];
        if (integer.Length > 1 && integer[0] == '0')
        {
            return false;
        }

        var fraction = ReadOnlySpan<char>.Empty;
        if (i < number.Length && number[i] == '.')
        {
            var fractionStart = ++i;
            while (i < number.Length && char.IsAsciiDigit(number[i]))
            {
                i++;
            }

            fraction = number[fractionStart..i];
            if (fraction.Length == 0)
            {
                return false;
            }
        }

        long exponent = 0;
        if (i < number.Length && number[i] is 'e' or 'E')
        {
            i++;
            var exponentNegative = i < number.Length && number[i] == '-';
            if (i < number.Length && number[i] is '+' or '-')
            {
                i++;
            }

            if (i == number.Length)
            {
                return false;
            }

            // A fixed cap could be offset by a long run of zeros.
            var cap = number.Length + 19L;
            while (i < number.Length && char.IsAsciiDigit(number[i]))
            {
                exponent = Math.Min(exponent * 10 + (number[i] - '0'), cap);
                i++;
            }

            exponent = exponentNegative ? -exponent : exponent;
        }

        if (i != number.Length || integer.Length == 0)
        {
            return false;
        }

        var all = string.Concat(integer, fraction);
        var first = 0;
        while (first < all.Length && all[first] == '0')
        {
            first++;
        }

        if (first == all.Length)
        {
            return true;
        }

        var last = all.Length - 1;
        while (all[last] == '0')
        {
            last--;
        }

        digits = all[first..(last + 1)];
        scale = exponent - fraction.Length + (all.Length - 1 - last);
        return true;
    }
}
