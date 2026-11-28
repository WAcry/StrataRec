using StrataRec.Core.Diagnostics;

namespace StrataRec.Artifacts.Json;

internal static class NameSuggestions
{
    public static string? FindClosest(string name, IEnumerable<string> candidates) => Spelling.Closest(name, candidates);

    public static int EditDistance(string a, string b) => Spelling.EditDistance(a, b);
}
