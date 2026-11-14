using System.Text.Json;
using StrataRec.CommandLine.Commands;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Candidate explanations end to end (design 13.3): <c>stratarec run --explain</c> on the home_feed sample says where a
/// survivor ended and which node dropped an item with the values it read; a replay explains the replayed run; a request its
/// fallback pipeline served explains that run; the engine rejects ids it cannot explain before anything runs.
/// </summary>
public sealed class ExplainEndToEndTests
{
    [Fact]
    public async Task RunExplain_SaysWhereASurvivorEnded_AndWhichNodeDroppedAnItem_WithTheValuesItRead()
    {
        // Item 17 is reported at a rate of 0.08: safety_filter (report_rate < 0.05) drops it. Item 1 leads the feed.
        var sample = HomeFeedTestHost.SampleRoot();
        var output = new StringWriter();

        var code = await RunCommand.RunAsync(Tool.Environment, sample, "local", Path.Combine(sample, "requests", "user_1001.json"), Array.Empty<string>(), false,
            output, CancellationToken.None, explain: new[] { "17,1", "999" });

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("items")[0].GetProperty("id").GetInt64());
        var explanation = root.GetProperty("explanation");
        Assert.Equal("home_feed", explanation.GetProperty("pipeline").GetString());
        var candidates = explanation.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(new long[] { 17, 1, 999 }, candidates.Select(c => c.GetProperty("id").GetInt64()));

        var dropped = candidates[0];
        Assert.Equal(("dropped", "safety_filter"), (dropped.GetProperty("verdict").GetString(), dropped.GetProperty("dropped_by").GetString()));
        var recall = Step(dropped, "recall");
        Assert.Equal(("entered", 17), (recall.GetProperty("change").GetString(), recall.GetProperty("rank").GetInt32()));
        Assert.Equal("two_tower", recall.GetProperty("values").GetProperty("recall.primary_source").GetString());
        var filter = Step(dropped, "safety_filter");
        Assert.Equal(("ran", "dropped", false, 17), (filter.GetProperty("step").GetString(), filter.GetProperty("change").GetString(),
            filter.GetProperty("alive").GetBoolean(), filter.GetProperty("rank_before").GetInt32()));
        Assert.Equal(0.08f, (float)filter.GetProperty("values").GetProperty("item.report_rate").GetDouble()); // a float32 feature
        Assert.True(filter.GetProperty("values").GetProperty("item.is_policy_safe").GetBoolean());
        Assert.False(filter.GetProperty("terms").GetProperty("predicate").GetBoolean());
        Assert.Equal("dropped: (col.item.is_policy_safe ?? true) && col.item.report_rate < 0.05 is false", filter.GetProperty("reason").GetString());
        Assert.All(dropped.GetProperty("steps").EnumerateArray().SkipWhile(s => s.GetProperty("node").GetString() != "safety_filter").Skip(1),
            s => Assert.Equal(("absent", true, false), (s.GetProperty("change").GetString(), s.GetProperty("present").GetBoolean(), s.GetProperty("alive").GetBoolean())));

        var survivor = candidates[1];
        Assert.Equal(("returned", 1), (survivor.GetProperty("verdict").GetString(), survivor.GetProperty("rank").GetInt32()));
        Assert.All(survivor.GetProperty("steps").EnumerateArray(), s => Assert.Equal("ran", s.GetProperty("step").GetString()));
        var fusion = Step(survivor, "fusion");
        var ctr = fusion.GetProperty("values").GetProperty("score.ctr").GetDouble();
        Assert.Equal(ctr, fusion.GetProperty("terms").GetProperty("base").GetDouble()); // the base utility is col.score.ctr
        Assert.True(fusion.GetProperty("terms").TryGetProperty("multiplier.quality", out _));
        Assert.StartsWith("score.final = ", fusion.GetProperty("reason").GetString());
        var sort = Step(survivor, "rank_sort");
        Assert.Equal((1000, 1), (sort.GetProperty("terms").GetProperty("k").GetInt32(), sort.GetProperty("rank").GetInt32()));
        Assert.StartsWith("kept: score.final ", sort.GetProperty("reason").GetString());

        Assert.Equal("not_recalled", candidates[2].GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task RunExplain_AnIdThatIsNotOfThePipelinesKeyType_IsAUsageError()
    {
        var sample = HomeFeedTestHost.SampleRoot();
        var errors = new StringWriter();

        var code = await RunCommand.RunAsync(Tool.Environment, sample, "local", Path.Combine(sample, "requests", "user_1001.json"), Array.Empty<string>(), false,
            TextWriter.Null, CancellationToken.None, errors: errors, explain: new[] { "17,abc" });

        Assert.Equal(2, code);
        Assert.Contains("--explain: 'abc' is not an entity id of pipeline 'home_feed', whose ids are int64s", errors.ToString());
    }

    [Fact]
    public async Task ReplayExplain_ExplainsTheReplayedRun_InTextAndJson()
    {
        var sample = HomeFeedTestHost.SampleRoot();
        var bundle = Path.Combine(Directory.CreateTempSubdirectory("stratarec-explain-replay-").FullName, "user_1001.json");
        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, sample, "local", Path.Combine(sample, "requests", "user_1001.json"), Array.Empty<string>(), false,
            TextWriter.Null, CancellationToken.None, recordPath: bundle));

        var text = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, sample, "local", [bundle], Array.Empty<string>(), "text", expectIdentical: true, text,
            CancellationToken.None, explain: new[] { "17" }));
        var lines = text.ToString();
        Assert.Contains("  explain   pipeline home_feed", lines);
        Assert.Contains("  explain   17: dropped by safety_filter", lines);
        Assert.Contains("safety_filter: dropped (was rank 17); item.is_policy_safe true, item.report_rate 0.07999999821186066; dropped: ", lines);

        var json = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, sample, "local", [bundle], Array.Empty<string>(), "json", expectIdentical: true, json,
            CancellationToken.None, explain: new[] { "17" }));
        using var document = JsonDocument.Parse(json.ToString());
        var candidate = document.RootElement.GetProperty("explanation").GetProperty("candidates")[0];
        Assert.Equal(("dropped", "safety_filter"), (candidate.GetProperty("verdict").GetString(), candidate.GetProperty("dropped_by").GetString()));
    }

    [Fact]
    public async Task ARequestItsFallbackPipelineServed_ExplainsTheRunThatServed()
    {
        // primary's only source fails; backup recalls the popular list (5, 8, 11, ...), of which it returns 5.
        var root = ReviewRegressionTests.FallbackWorkspace(brokenLatencyMs: 0);
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
        ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", new[] { "primary" }).ThrowIfErrors("load");

        var result = await engine.ExecuteAsync(Request("primary", EntityKey.FromInt64(8), EntityKey.FromInt64(1)));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("backup", result.ServedBy);
        var explanation = Assert.IsType<RunExplanation>(result.Explanation);
        Assert.Equal("backup", explanation.PipelineId);
        Assert.Equal((ExplainVerdict.Returned, (int?)2), (explanation.Candidates[0].Verdict, explanation.Candidates[0].Rank));
        Assert.Equal(ExplainVerdict.NotRecalled, explanation.Candidates[1].Verdict);
        Assert.Equal(new[] { "recall" }, explanation.Candidates[0].Steps.Select(s => s.Node));

        var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            RecResultJson.Write(writer, result);
        }

        using var document = JsonDocument.Parse(json.ToArray());
        Assert.Equal("backup", document.RootElement.GetProperty("explanation").GetProperty("pipeline").GetString());
    }

    [Fact]
    public async Task IdsTheEngineCannotExplain_AreAnInvalidRequest_CheckedBeforeAnythingRuns()
    {
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment);
        Assert.True(engine.Load(host.Request).Applied);

        var tooMany = await engine.ExecuteAsync(Request("home_feed", Enumerable.Range(1, DebugOptions.MaxExplainIds + 1).Select(i => EntityKey.FromInt64(i)).ToArray()));
        var otherType = await engine.ExecuteAsync(Request("home_feed", EntityKey.FromString("17")));
        var none = await engine.ExecuteAsync(Request("home_feed"));

        Assert.Equal((RecErrorKind.InvalidRequest, (string?)null), (tooMany.ErrorKind, tooMany.ErrorPointer));
        Assert.Equal("explain_ids has 101 ids, but a request explains at most 100 candidates; ask about fewer ids per request.", tooMany.Error);
        Assert.Equal(RecErrorKind.InvalidRequest, otherType.ErrorKind);
        Assert.Equal("explain id '17' is a string, but pipeline 'home_feed' has int64 entity ids; give its ids as int64s.", otherType.Error);
        Assert.Empty(tooMany.Experiments.Assigned); // nothing was assigned
        Assert.Null(tooMany.Explanation);
        Assert.True(none.Succeeded, none.Error);
        Assert.Null(none.Explanation);
    }

    private static JsonElement Step(JsonElement candidate, string node) =>
        candidate.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("node").GetString() == node);

    private static RecRequest Request(string pipeline, params EntityKey[] explain) => new()
    {
        PipelineId = pipeline,
        RequestId = "r",
        Inputs = pipeline == "home_feed" ? HomeFeedTestHost.Inputs(1001, "home") : new RequestInputs(),
        Deadline = HomeFeedTestHost.Deadline,
        Debug = explain.Length == 0 ? null : new DebugOptions { ExplainIds = explain },
    };
}
