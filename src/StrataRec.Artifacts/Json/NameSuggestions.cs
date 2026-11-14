using StrataRec.Core.Diagnostics;

namespace StrataRec.Artifacts.Json;

/// <summary>"Did you mean ...?" suggestions for misspelled keys: <see cref="Spelling"/>, under the name artifacts use.</summary>
internal static class NameSuggestions
{
    /// <inheritdoc cref="Spelling.Closest"/>
    public static string? FindClosest(string name, IEnumerable<string> candidates) => Spelling.Closest(name, candidates);

    /// <inheritdoc cref="Spelling.EditDistance"/>
    public static int EditDistance(string a, string b) => Spelling.EditDistance(a, b);
}
