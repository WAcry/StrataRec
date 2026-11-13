using System.Text.Json;
using StrataRec.Cli.Commands;

namespace StrataRec.IntegrationTests;

/// <summary><c>stratarec run --record</c>, <c>replay</c>, <c>compare</c> and <c>schema replay_bundle</c> on the home_feed sample.</summary>
public sealed class ReplayCliTests
{
    [Fact]
    public async Task RecordReplayAndCompare_FromTheCommandLine()
    {
        var bundles = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        var requests = Directory.CreateTempSubdirectory("stratarec-requests-").FullName;
        foreach (var name in new[] { "user_1001", "anonymous" })
        {
            // The sample requests allow 200 ms. A cold run on a busy two-core CI runner can exceed that, the request fails
            // and `run` exits 1; this test is about the commands, not the deadline.
            var request = Path.Combine(requests, name + ".json");
            var sample = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Samples.HomeFeed, "requests", name + ".json")))!;
            sample["deadline_ms"] = 30_000;
            File.WriteAllText(request, sample.ToJsonString());
            Assert.Equal(0, await RunCommand.RunAsync(Samples.HomeFeed, "local", request, Array.Empty<string>(), false, TextWriter.Null, CancellationToken.None,
                recordPath: Path.Combine(bundles, name + ".json")));
        }

        // One bundle, text: the result and the diff against the recording.
        var text = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Samples.HomeFeed, "local", Path.Combine(bundles, "user_1001.json"), Array.Empty<string>(), "text",
            expectIdentical: true, text, CancellationToken.None));
        Assert.Contains("identical to the recorded result", text.ToString());

        // A directory, JSON: every bundle with its report.
        var json = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Samples.HomeFeed, "local", bundles, Array.Empty<string>(), "json", expectIdentical: true, json,
            CancellationToken.None));
        using (var replay = JsonDocument.Parse(json.ToString()))
        {
            var replayed = replay.RootElement.GetProperty("bundles");
            Assert.Equal(2, replayed.GetArrayLength());
            Assert.All(replayed.EnumerateArray(), b => Assert.True(b.GetProperty("diff").GetProperty("identical").GetBoolean()));
        }

        // A candidate that penalizes reports harder and sends 5 more rows to the model (which the recordings lack).
        var candidate = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "col.item.report_rate * 10.0", "col.item.report_rate * 20.0");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"coarse_k\": 30,", "\"coarse_k\": 35,");
        }).Root;
        Assert.Equal(1, await ReplayCommand.RunAsync(candidate, "local", bundles, Array.Empty<string>(), "text", expectIdentical: true, TextWriter.Null,
            CancellationToken.None));

        var compare = new StringWriter();
        Assert.Equal(0, await CompareCommand.RunAsync(Samples.HomeFeed, candidate, "local", bundles, Array.Empty<string>(), 10, "score", "json", compare,
            CancellationToken.None));
        using (var report = JsonDocument.Parse(compare.ToString()))
        {
            var root = report.RootElement;
            Assert.Equal(("recorded", 2), (root.GetProperty("mode").GetString(), root.GetProperty("bundles").GetInt32()));
            Assert.Equal(2, root.GetProperty("baseline").GetProperty("reproduced_recording").GetInt32());
            Assert.Equal(0, root.GetProperty("candidate").GetProperty("error_rate").GetDouble());
            Assert.NotEqual(0, root.GetProperty("candidate").GetProperty("gaps").GetArrayLength());
            Assert.Equal(2, root.GetProperty("comparison").GetProperty("top_k_overlap").GetProperty("count").GetInt32());
            Assert.Equal(2, root.GetProperty("requests").GetArrayLength());
        }

        var table = new StringWriter();
        Assert.Equal(0, await CompareCommand.RunAsync(Samples.HomeFeed, candidate, "local", bundles, Array.Empty<string>(), 10, "score", "text", table,
            CancellationToken.None));
        Assert.Contains("Recorded-mode comparison of 2 bundle(s)", table.ToString());
        Assert.Contains("candidate gap", table.ToString());
    }

    [Fact]
    public async Task MalformedBundle_IsAUsageError_NamingThePointer()
    {
        var bundle = Path.Combine(Directory.CreateTempSubdirectory("stratarec-bundles-").FullName, "bad.json");
        File.WriteAllText(bundle, """{ "format": "stratarec.replay_bundle/0", "request": {} }""");

        var output = new StringWriter();
        var exit = await ReplayCommand.RunAsync(Samples.HomeFeed, "local", bundle, Array.Empty<string>(), "json", false, output, CancellationToken.None);

        Assert.Equal(2, exit);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Contains("/request", report.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Schema_DescribesTheReplayBundle()
    {
        var output = new StringWriter();

        Assert.Equal(0, SchemaCommand.Run("replay_bundle", null, output, TextWriter.Null));

        using var schema = JsonDocument.Parse(output.ToString());
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(new[] { "format", "manifest", "request", "result", "runs" }, properties.EnumerateObject().Select(p => p.Name).Order());
        var run = properties.GetProperty("runs").GetProperty("items").GetProperty("properties");
        Assert.True(run.TryGetProperty("feature_calls", out _));
        Assert.True(run.GetProperty("nodes").GetProperty("items").GetProperty("properties").GetProperty("remaining_ms").TryGetProperty("description", out _));
    }
}
