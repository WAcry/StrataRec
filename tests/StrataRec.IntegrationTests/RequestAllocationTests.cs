using System.Globalization;
using System.Text;
using StrataRec.Pipeline.Execution;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Managed allocation on the request path must not grow with the number of candidates (design 4.8, 15): recall
/// merge, key lookups, feature fetches and operator scratch all use the request's pooled buffers.
/// </summary>
public sealed class RequestAllocationTests
{
    [Fact]
    public void HomeFeedRequest_AllocationDoesNotGrowWithTheCandidateCount()
    {
        var small = Measure(250);
        var large = Measure(4_000);

        // Before the rented key index, 3,750 more candidates cost ~2.1 MB per request (dictionaries, key lists, a string
        // per key); now both sizes allocate the same.
        Assert.True(large - small < 4 * 1024, $"250 candidates: {small} B per request, 4,000 candidates: {large} B per request");
    }

    /// <summary>
    /// Allocated bytes of one request on this thread, best of three after a warm-up. The sample's in-memory sources
    /// have no latency, so the whole request runs synchronously here.
    /// </summary>
    private static long Measure(int candidates)
    {
        var plan = HomeFeedTestHost.LoadModified(root => Scale(root, candidates)).Plan();
        var executor = new PipelineExecutor();
        var request = HomeFeedTestHost.NewRequest();
        for (var i = 0; i < 3; i++)
        {
            var warm = executor.ExecuteAsync(plan, request).AsTask().GetAwaiter().GetResult();
            Assert.True(warm.Succeeded, warm.Error);
        }

        var best = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var run = executor.ExecuteAsync(plan, request);
            var completed = run.IsCompletedSuccessfully;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(completed, "the request did not complete synchronously; its allocations would be missed");
            Assert.Equal(10, run.Result.Items.Count);
            best = Math.Min(best, allocated);
        }

        return best;
    }

    /// <summary>
    /// Rewrites the sample for <paramref name="candidates"/> recalled items. Everything that legitimately scales with
    /// the rows a stage keeps is held constant: one fetch chunk (max_batch), and coarse_k stays 30, so the model, the
    /// logged rows and the decoded category strings are the same at both sizes.
    /// </summary>
    private static void Scale(string root, int candidates)
    {
        var random = new Random(20261113);
        var categories = new[] { "music", "food", "sports", "travel", "tech", "news" };
        var items = new StringBuilder("{");
        for (var id = 1; id <= candidates; id++)
        {
            items.Append(id == 1 ? "\n" : ",\n").Append(CultureInfo.InvariantCulture, $$"""
                "{{id}}": { "i_age_h": {{random.NextDouble() * 500:F1}}, "i_author_id": {{100 + random.Next(300)}}, "i_category": "{{categories[random.Next(categories.Length)]}}",
                  "i_clk_7d": {{random.Next(1000)}}.0, "i_imp_7d": {{1000 + random.Next(20000)}}.0, "i_report_rate": {{random.NextDouble() * 0.06:F4}}, "i_safe": {{(random.Next(20) != 0 ? "true" : "false")}},
                  "i_emb": [0.1, 0.2, 0.3, 0.4] }
                """);
        }

        File.WriteAllText(Path.Combine(root, "fixtures", "items.json"), items.Append("\n}").ToString());

        var authors = new StringBuilder("{");
        for (var id = 100; id < 400; id++)
        {
            authors.Append(id == 100 ? "\n" : ",\n").Append(CultureInfo.InvariantCulture, $"\"{id}\": {{ \"a_followers\": {random.Next(1_000_000)}.0 }}");
        }

        File.WriteAllText(Path.Combine(root, "fixtures", "authors.json"), authors.Append("\n}").ToString());

        // Two thirds from the personalized source, the rest from the popular list; the lists overlap.
        var twoTower = candidates * 2 / 3;
        var popular = candidates - twoTower;
        File.WriteAllText(Path.Combine(root, "fixtures", "recall_two_tower.json"), RecallList(1, twoTower));
        File.WriteAllText(Path.Combine(root, "fixtures", "recall_popular.json"), RecallList(candidates - popular - (popular / 2) + 1, popular));

        var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
        File.WriteAllText(pipeline, File.ReadAllText(pipeline)
            .Replace("\"min\": 0, \"max\": 1000 }", "\"min\": 0, \"max\": 1000000 }", StringComparison.Ordinal)
            .Replace("\"two_tower\": 40, \"popular\": 20", $"\"two_tower\": {twoTower}, \"popular\": {popular}", StringComparison.Ordinal));
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        File.WriteAllText(bindings, File.ReadAllText(bindings).Replace("\"max_batch\": 25", "\"max_batch\": 10000", StringComparison.Ordinal));
    }

    private static string RecallList(int firstId, int count)
    {
        var list = new StringBuilder("{ \"items\": [");
        for (var i = 0; i < count; i++)
        {
            list.Append(i == 0 ? string.Empty : ", ").Append(CultureInfo.InvariantCulture, $"{{ \"id\": {firstId + i}, \"score\": {1.0 - (i / (double)count):F5} }}");
        }

        return list.Append("] }").ToString();
    }
}
