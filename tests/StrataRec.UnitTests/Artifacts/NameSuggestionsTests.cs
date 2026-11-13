using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class NameSuggestionsTests
{
    [Theory]
    [InlineData("diversty_cap", "diversity_cap", 1)]
    [InlineData("ab", "ba", 1)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("same", "same", 0)]
    [InlineData("Order", "order", 1)]
    public void EditDistance_CountsInsertionsDeletionsSubstitutionsAndTranspositions(string a, string b, int expected)
    {
        Assert.Equal(expected, NameSuggestions.EditDistance(a, b));
        Assert.Equal(expected, NameSuggestions.EditDistance(b, a));
    }

    [Fact]
    public void FindClosest_PicksNearestCandidate()
    {
        var candidates = new[] { "safety_filter", "fusion", "diversity_cap", "rank_sort" };

        Assert.Equal("diversity_cap", NameSuggestions.FindClosest("diversty_cap", candidates));
        Assert.Equal("fusion", NameSuggestions.FindClosest("fuison", candidates));
        Assert.Null(NameSuggestions.FindClosest("mmr_diversifier", candidates));
    }

    [Fact]
    public void FindClosest_BreaksTiesOrdinally()
    {
        Assert.Equal("cat", NameSuggestions.FindClosest("bat", new[] { "rat", "cat", "hat" }));
    }

    [Fact]
    public void FindClosest_ShortNamesAllowOneEdit()
    {
        Assert.Equal("ab", NameSuggestions.FindClosest("a", new[] { "ab", "xyz" }));
        Assert.Null(NameSuggestions.FindClosest("ab", new[] { "cd" }));
    }

    [Fact]
    public void FindClosest_IgnoresNullCandidatesAndValidatesArguments()
    {
        Assert.Equal("abc", NameSuggestions.FindClosest("abd", new[] { null!, "abc" }));
        Assert.Throws<ArgumentNullException>(() => NameSuggestions.FindClosest(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => NameSuggestions.FindClosest("a", null!));
    }
}
