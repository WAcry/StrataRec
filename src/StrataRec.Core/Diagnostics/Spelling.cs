namespace StrataRec.Core.Diagnostics;

/// <summary>
/// "Did you mean.?" for misspelled names, so an error can say how to fix it (<c>diversty_cap</c> for
/// <c>diversity_cap</c>).
/// </summary>
public static class Spelling
{
    /// <summary>
    /// Returns the candidate closest to <paramref name="name"/> by <see cref="EditDistance"/>, or
    /// <see langword="null"/> when none is close enough to be a plausible typo. Ties resolve to the ordinally
    /// smallest candidate, so the suggestion is deterministic.
    /// </summary>
    /// <remarks>
    /// A candidate qualifies when its distance is at most a third of <paramref name="name"/>'s length, clamped to
    /// [1, 3]: one edit for short names, up to three for long ones.
    /// </remarks>
    public static string? Closest(string name, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(candidates);
        var maxDistance = Math.Clamp(name.Length / 3, 1, 3);
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (candidate is null || Math.Abs(candidate.Length - name.Length) > maxDistance)
            {
                continue;
            }

            var distance = EditDistance(name, candidate);
            if (distance > maxDistance)
            {
                continue;
            }

            if (distance < bestDistance || (distance == bestDistance && string.CompareOrdinal(candidate, best) < 0))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Optimal string alignment distance: the number of single-character insertions, deletions, substitutions and
    /// adjacent transpositions (each edited once) needed to turn <paramref name="a"/> into <paramref name="b"/>.
    /// Comparison is ordinal.
    /// </summary>
    public static int EditDistance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous2 = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, previous2[j - 2] + 1);
                }

                current[j] = value;
            }

            (previous2, previous, current) = (previous, current, previous2);
        }

        return previous[b.Length];
    }

    /// <summary>The hint <c>did you mean 'x'?</c> for the candidate closest to <paramref name="name"/>, or null.</summary>
    public static string? DidYouMean(string name, IEnumerable<string> candidates) =>
        Closest(name, candidates) is { } closest ? $"did you mean '{closest}'?" : null;
}
