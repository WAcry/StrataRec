using StrataRec.Core.Memory;
using StrataRec.Pipeline.Operators.Standard;

namespace StrataRec.UnitTests.Pipeline;

public sealed class SlidingWindowQuotaTests
{
    [Theory]
    [InlineData(new[] { 0, 0, 1, 1 }, 2, 1, new[] { 0, 2, 1, 3 })]
    [InlineData(new[] { 0, 0, 0, 1 }, 2, 1, new[] { 0, 3, 1 })]
    [InlineData(new[] { 0, -1, 0, 0 }, 3, 1, new[] { 0, 1 })]
    [InlineData(new[] { 0, 0, 0, 0, 1, 1 }, 3, 2, new[] { 0, 1, 4, 2, 3, 5 })]
    [InlineData(new[] { 0, 1, 2, 0, 1, 2 }, 3, 1, new[] { 0, 1, 2, 3, 4, 5 })]
    public void Places_TheEarliestRowUnderQuota(int[] groups, int window, int max, int[] expected)
    {
        Assert.Equal(expected, Place(groups, window, max));
        Assert.Equal(expected, Reference(groups, window, max));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(400)]
    public void Places_WhatTheRescanPlaced_ForAnyDistributionOfGroups(int distinct)
    {
        var random = new Random(distinct);
        for (var trial = 0; trial < 200; trial++)
        {
            var n = random.Next(0, 120);
            var groups = new int[n];
            for (var i = 0; i < n; i++)
            {
                // Runs of one group, shuffled stretches, and rows without a group.
                groups[i] = random.Next(10) == 0 ? -1 : trial % 3 == 0 ? i * distinct / Math.Max(1, n) : random.Next(distinct);
            }

            var window = random.Next(2, 8);
            var max = random.Next(1, window);
            Assert.Equal(Reference(groups, window, max), Place(groups, window, max));
        }
    }

    [Fact]
    public void TwoLongRuns_ArePlacedInLinearithmicTime()
    {
        var groups = new int[20_000];
        for (var i = groups.Length / 2; i < groups.Length; i++)
        {
            groups[i] = 1;
        }

        var placed = Place(groups, 2, 1);

        Assert.Equal(groups.Length, placed.Length);
        for (var i = 1; i < placed.Length; i++)
        {
            Assert.NotEqual(groups[placed[i - 1]], groups[placed[i]]);
        }
    }

    private static int[] Place(int[] groups, int window, int max)
    {
        var dense = Densify(groups);
        var memory = new RequestMemory();
        try
        {
            var placed = new int[groups.Length];
            var count = SlidingWindowQuotaFactory.Place(groups, dense, window, max, placed, memory);
            return placed[..count];
        }
        finally
        {
            memory.Release();
        }
    }

    /// <summary>The number of groups, as <c>RowGroups.Assign</c> gives it: the ids are 0 to count − 1.</summary>
    private static int Densify(int[] groups) => groups.Length == 0 ? 0 : groups.Max() + 1;

    private static int[] Reference(int[] groups, int window, int max)
    {
        var n = groups.Length;
        var used = new bool[n];
        var placed = new List<int>();
        while (placed.Count < n)
        {
            var chosen = -1;
            for (var i = 0; i < n && chosen < 0; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var count = 0;
                for (var j = Math.Max(0, placed.Count - (window - 1)); j < placed.Count; j++)
                {
                    count += groups[placed[j]] >= 0 && groups[placed[j]] == groups[i] ? 1 : 0;
                }

                if (groups[i] < 0 || count < max)
                {
                    chosen = i;
                }
            }

            if (chosen < 0)
            {
                break;
            }

            used[chosen] = true;
            placed.Add(chosen);
        }

        return placed.ToArray();
    }
}
