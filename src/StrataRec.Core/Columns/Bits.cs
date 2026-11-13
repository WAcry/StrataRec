using System.Numerics;
using System.Runtime.CompilerServices;

namespace StrataRec.Core.Columns;

/// <summary>
/// Helpers for bitmaps stored as <c>ulong</c> words (bit <c>i</c> lives in word <c>i &gt;&gt; 6</c>, LSB first).
/// Used for validity, fill state and liveness (design 4.5). A 5,000-row bitmap is 79 words.
/// </summary>
public static class Bits
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WordCount(int bitCount) => (bitCount + 63) >> 6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Get(ReadOnlySpan<ulong> words, int index) => (words[index >> 6] & (1UL << index)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(Span<ulong> words, int index) => words[index >> 6] |= 1UL << index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear(Span<ulong> words, int index) => words[index >> 6] &= ~(1UL << index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Assign(Span<ulong> words, int index, bool value)
    {
        if (value)
        {
            Set(words, index);
        }
        else
        {
            Clear(words, index);
        }
    }

    /// <summary>Sets bits [start, start + count).</summary>
    public static void SetRange(Span<ulong> words, int start, int count)
    {
        for (var i = start; i < start + count;)
        {
            var bit = i & 63;
            if (bit == 0 && i + 64 <= start + count)
            {
                words[i >> 6] = ulong.MaxValue;
                i += 64;
            }
            else
            {
                Set(words, i);
                i++;
            }
        }
    }

    /// <summary>
    /// Sets the bit of every index in <paramref name="indices"/> (any order, repeats allowed). Consecutive indices in one
    /// word are gathered in a register and stored once, so a run of ascending rows does not wait on its own stores.
    /// </summary>
    public static void SetEach(Span<ulong> words, ReadOnlySpan<int> indices)
    {
        var i = 0;
        while (i < indices.Length)
        {
            var word = indices[i] >> 6;
            var bits = 0UL;
            do
            {
                bits |= 1UL << indices[i];
                i++;
            }
            while (i < indices.Length && indices[i] >> 6 == word);

            words[word] |= bits;
        }
    }

    /// <summary>Clears bits [start, start + count).</summary>
    public static void ClearRange(Span<ulong> words, int start, int count)
    {
        for (var i = start; i < start + count;)
        {
            var bit = i & 63;
            if (bit == 0 && i + 64 <= start + count)
            {
                words[i >> 6] = 0;
                i += 64;
            }
            else
            {
                Clear(words, i);
                i++;
            }
        }
    }

    public static int PopCount(ReadOnlySpan<ulong> words)
    {
        var count = 0;
        foreach (var w in words)
        {
            count += BitOperations.PopCount(w);
        }

        return count;
    }

    /// <summary>destination &amp;= source.</summary>
    public static void And(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] &= source[i];
        }
    }

    /// <summary>destination |= source.</summary>
    public static void Or(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] |= source[i];
        }
    }

    /// <summary>destination &amp;= ~source.</summary>
    public static void AndNot(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] &= ~source[i];
        }
    }

    /// <summary>True when every bit in [0, bitCount) is set.</summary>
    public static bool AllSet(ReadOnlySpan<ulong> words, int bitCount)
    {
        var full = bitCount >> 6;
        for (var i = 0; i < full; i++)
        {
            if (words[i] != ulong.MaxValue)
            {
                return false;
            }
        }

        var rem = bitCount & 63;
        if (rem == 0)
        {
            return true;
        }

        var mask = (1UL << rem) - 1;
        return (words[full] & mask) == mask;
    }
}
