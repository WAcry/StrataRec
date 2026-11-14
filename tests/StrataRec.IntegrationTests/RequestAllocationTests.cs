using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Managed allocation on the request path must not grow with the number of candidates (design 4.8, 15): recall
/// merge, key lookups, feature fetches and operator scratch all use the request's pooled buffers. Metrics are recorded
/// with tags cached per plan, so an attached <see cref="MeterListener"/> adds nothing either (design 13.1 rule 5).
/// </summary>
/// <remarks>In the listener collection: another test's listener would run its callbacks on the measuring thread.</remarks>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class RequestAllocationTests
{
    [Fact]
    public void HomeFeedRequest_AllocationDoesNotGrowWithTheCandidateCount()
    {
        var small = Measure(Plan(250));
        var large = Measure(Plan(4_000));

        // Before the rented key index, 3,750 more candidates cost ~2.1 MB per request (dictionaries, key lists, a string
        // per key); now both sizes allocate the same.
        Assert.True(large - small < 4 * 1024, $"250 candidates: {small} B per request, 4,000 candidates: {large} B per request");
    }

    [Fact]
    public void AnExplainedRequest_CostsWhatItsTargetsAndNodesCost_NotWhatItsCandidatesCost()
    {
        // Design 13.3: targets are found by key once and followed, so explaining three ids allocates the same at 250 and at
        // 4,000 candidates (steps, values and reasons: targets × nodes). The ids are early in both recall lists.
        var smallPlan = Plan(250);
        var largePlan = Plan(4_000);
        var plain = Measure(smallPlan);
        var small = Measure(smallPlan, explain: true);
        var large = Measure(largePlan, explain: true);

        Assert.True(small > plain, $"explaining allocated nothing: {plain} B without, {small} B with");
        Assert.True(large - small < 4 * 1024, $"explained, 250 candidates: {small} B per request, 4,000 candidates: {large} B per request");
    }

    [Fact]
    public void HomeFeedRequest_WithAMeterListener_AllocatesNothingMoreAndDoesNotGrow()
    {
        var smallPlan = Plan(250);
        var largePlan = Plan(4_000);
        var quiet = Measure(smallPlan);

        long measurements = 0;
        long small, large;
        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == StrataRecTelemetry.Name)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measurements));
            listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measurements));
            listener.Start();
            small = Measure(smallPlan);
            large = Measure(largePlan);
        }

        Assert.True(measurements > 0);
        Assert.True(small <= quiet, $"without a listener: {quiet} B per request, with one: {small} B");
        Assert.True(large - small < 4 * 1024, $"with a listener, 250 candidates: {small} B per request, 4,000 candidates: {large} B per request");
    }

    [Fact]
    public void EngineRequest_WithAMeterListener_AllocatesNothingMore()
    {
        var host = HomeFeedTestHost.LoadModified(root => Scale(root, 250));
        var engine = new RecEngine(host.Environment);
        Assert.True(engine.Load(host.Request).Applied);
        var request = new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-alloc",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Assignment = new[] { "exp_quality_weight/control", "exp_mmr_diversity/control" },
            Deadline = HomeFeedTestHost.Deadline,
            TimeMs = 1_767_225_600_000,
        };
        var quiet = MeasureBest(() => engine.ExecuteAsync(request), r => r.Items.Count);

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();
        var observed = MeasureBest(() => engine.ExecuteAsync(request), r => r.Items.Count);

        Assert.True(observed <= quiet, $"without a listener: {quiet} B per request, with one: {observed} B");
    }

    private static ExecutionPlan Plan(int candidates) => HomeFeedTestHost.LoadModified(root => Scale(root, candidates)).Plan();

    private static long Measure(ExecutionPlan plan, bool explain = false)
    {
        var executor = new PipelineExecutor();
        var request = HomeFeedTestHost.NewRequest();
        if (explain)
        {
            request = new PipelineRequest
            {
                RequestId = request.RequestId,
                Inputs = request.Inputs,
                Deadline = request.Deadline,
                TimeMs = request.TimeMs,
                ExplainIds = new[] { EntityKey.FromInt64(1), EntityKey.FromInt64(2), EntityKey.FromInt64(3) },
            };
        }

        return MeasureBest(() => executor.ExecuteAsync(plan, request), r => r.Items.Count);
    }

    /// <summary>
    /// Allocated bytes of one request on this thread, best of three after a warm-up. The sample's in-memory sources
    /// have no latency, so the whole request runs synchronously here.
    /// </summary>
    private static long MeasureBest<T>(Func<ValueTask<T>> run, Func<T, int> items)
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(10, items(run().AsTask().GetAwaiter().GetResult()));
        }

        var best = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var pending = run();
            var completed = pending.IsCompletedSuccessfully;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(completed, "the request did not complete synchronously; its allocations would be missed");
            Assert.Equal(10, items(pending.Result));
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

        // author_store is a local table loaded from a snapshot directory (one JSONL part).
        var authors = new StringBuilder();
        for (var id = 100; id < 400; id++)
        {
            authors.Append(CultureInfo.InvariantCulture, $"{{\"key\": {id}, \"version\": 1, \"fields\": {{\"a_followers\": {random.Next(1_000_000)}.0}}}}\n");
        }

        File.WriteAllText(Path.Combine(root, "fixtures", "author_snapshot", "part-0000.jsonl"), authors.ToString());

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
