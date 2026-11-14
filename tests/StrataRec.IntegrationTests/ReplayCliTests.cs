using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.CommandLine;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

/// <summary><c>stratarec run --record</c>, <c>replay</c>, <c>compare</c> and <c>schema replay_bundle</c> on the home_feed sample.</summary>
public sealed class ReplayCliTests
{
    [Fact]
    public async Task Bundles_AreArguments_OrGivenWithB_AndAtLeastOneIsRequired()
    {
        var sample = HomeFeedTestHost.SampleRoot();
        var bundles = Directory.CreateTempSubdirectory("stratarec-bundle-args-").FullName;
        var paths = new List<string>();
        foreach (var name in new[] { "user_1001", "anonymous" })
        {
            paths.Add(Path.Combine(bundles, name + ".json"));
            var recorded = new StringWriter();
            Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, sample, "local", Path.Combine(sample, "requests", name + ".json"), Array.Empty<string>(), false,
                TextWriter.Null, CancellationToken.None, recordPath: paths[^1], errors: recorded));
            Assert.Contains($"; replay it with: stratarec replay -c {sample} {paths[^1]}", recorded.ToString()); // ready to copy
        }

        // Two files as arguments (what a shell glob gives), one as an argument and one with -b: the same two replays.
        foreach (var args in new[] { new[] { "replay", "-c", sample, paths[0], paths[1] }, new[] { "replay", "-c", sample, paths[0], "-b", paths[1] } })
        {
            var output = new StringWriter();
            Assert.Equal(0, await StrataRecCommandLine.InvokeAsync(args, Tool.Environment, output, TextWriter.Null));
            Assert.Contains("2 bundles, 2 replayed, 0 unreadable, 2 reproduced the recorded result exactly; status ok", output.ToString());
        }

        var error = new StringWriter();
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(new[] { "replay", "-c", sample }, Tool.Environment, TextWriter.Null, error));
        Assert.Contains("Give the bundles to replay: 'stratarec replay -c <workspace> <bundle.json or directory>...'.", error.ToString());
        Directory.Delete(bundles, recursive: true);
    }

    [Fact]
    public async Task TheReplayCommandRunPrints_ReadsAPathThatLooksLikeAnOption_AsAPath()
    {
        // Review 34: 'run --record=-b' printed 'stratarec replay -c ws -b', where -b is the bundle option.
        Assert.Equal("stratarec replay -c ws ./-b", RunCommand.ReplayCommandLine("ws", "-b"));
        Assert.Equal("stratarec replay -c ./-ws 'my b.json'", RunCommand.ReplayCommandLine("-ws", "my b.json"));
        Assert.Equal("stratarec replay -c ws /tmp/-b", RunCommand.ReplayCommandLine("ws", "/tmp/-b"));

        // As printed, the path is an argument: a missing file is reported as such, not as an option missing its value.
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(new[] { "replay", "-c", HomeFeedTestHost.SampleRoot(), "./-b" }, Tool.Environment, output, error));
        Assert.DoesNotContain("Required argument", error.ToString());
        Assert.Contains("./-b: not replayed", output.ToString());
    }

    /// <summary>
    /// Review 34: a command line that does not parse is a usage error, exit 2, never an exception (a -b without its value
    /// threw from the validator) nor 1, which says the configuration has errors or, for diff, that something differs.
    /// </summary>
    [Theory]
    [InlineData("Required argument missing for option: '-b'.", "replay", "-b")]
    [InlineData("Required argument missing for option: '-b'.", "compare", "--baseline", "x", "-b")]
    [InlineData("Unrecognized command or argument '--nope'.", "diff", "--to", "x", "--nope")]
    [InlineData("Unrecognized command or argument '--nope'.", "validate", "--nope")]
    public async Task ACommandLineThatDoesNotParse_IsAUsageError(string message, params string[] args)
    {
        var error = new StringWriter();

        var code = await StrataRecCommandLine.InvokeAsync(args, Tool.Environment, TextWriter.Null, error);

        Assert.Equal(2, code);
        Assert.Contains(message, error.ToString());
        // The error and where the usage is, not the whole help after it.
        Assert.EndsWith($"See 'stratarec {args[0]} --help' for the usage.", error.ToString().TrimEnd());
        Assert.DoesNotContain("Usage:", error.ToString());
    }

    [Fact]
    public async Task NoCommand_PrintsTheCommands()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(Array.Empty<string>(), Tool.Environment, output, error));

        Assert.Contains("Commands:", output.ToString() + error.ToString());
        Assert.DoesNotContain("for the usage", error.ToString());
    }

    /// <summary>
    /// replay writes its items as run does, the output columns in their own member: a column named id or rank cannot
    /// collide with the item's (it flattened them into the item and wrote both 'id' members, architecture audit 1).
    /// </summary>
    [Fact]
    public async Task ReplayItems_AreTheItemsRunPrints_SoAColumnNamedIdOrRankCannotCollide()
    {
        var root = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"score\": \"col.score.final\",",
            "\"score\": \"col.score.final\", \"id\": \"col.entity_id + 2000\", \"rank\": \"col.entity_id + 1000\",")).Root;
        var bundle = Path.Combine(Directory.CreateTempSubdirectory("stratarec-bundles-").FullName, "b.json");
        var run = new StringWriter();
        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, root, "local", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), false, run,
            CancellationToken.None, recordPath: bundle));

        var json = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, root, "local", [bundle], Array.Empty<string>(), "json", expectIdentical: true, json,
            CancellationToken.None));

        using var replay = JsonDocument.Parse(json.ToString(), new JsonDocumentOptions { AllowDuplicateProperties = false });
        using var served = JsonDocument.Parse(run.ToString());
        var items = replay.RootElement.GetProperty("items"); // one file: its report alone
        var first = items[0];
        var id = first.GetProperty("id").GetInt64();
        Assert.Equal(1, first.GetProperty("rank").GetInt64());
        Assert.Equal(id + 2000, first.GetProperty("columns").GetProperty("id").GetInt64());
        Assert.Equal(id + 1000, first.GetProperty("columns").GetProperty("rank").GetInt64());
        Assert.Equal(served.RootElement.GetProperty("items").GetRawText(), items.GetRawText());
    }

    [Fact]
    public async Task RecordReplayAndCompare_FromTheCommandLine()
    {
        // The untimed sample, request files included: this test is about the commands, not the budgets.
        var sample = HomeFeedTestHost.SampleRoot();
        var bundles = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        foreach (var name in new[] { "user_1001", "anonymous" })
        {
            Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, sample, "local", Path.Combine(sample, "requests", name + ".json"), Array.Empty<string>(), false, TextWriter.Null,
                CancellationToken.None, recordPath: Path.Combine(bundles, name + ".json")));
        }

        // One bundle, text: the result and the diff against the recording.
        var text = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, sample, "local", [Path.Combine(bundles, "user_1001.json")], Array.Empty<string>(), "text",
            expectIdentical: true, text, CancellationToken.None));
        Assert.Contains("identical to the recorded result", text.ToString());

        // A directory, JSON: every bundle with its report.
        var json = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, sample, "local", [bundles], Array.Empty<string>(), "json", expectIdentical: true, json,
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
        Assert.Equal(1, await ReplayCommand.RunAsync(Tool.Environment, candidate, "local", [bundles], Array.Empty<string>(), "text", expectIdentical: true, TextWriter.Null,
            CancellationToken.None));

        var compare = new StringWriter();
        Assert.Equal(0, await CompareCommand.RunAsync(Tool.Environment, sample, candidate, "local", [bundles], Array.Empty<string>(), 10, "score", "json", compare,
            CancellationToken.None));
        using (var report = JsonDocument.Parse(compare.ToString()))
        {
            var root = report.RootElement;
            Assert.Equal(("recorded", "ok", 2), (root.GetProperty("mode").GetString(), root.GetProperty("status").GetString(), root.GetProperty("bundles").GetInt32()));
            Assert.Equal(2, root.GetProperty("baseline").GetProperty("reproduced_recording").GetInt32());
            Assert.Equal(0, root.GetProperty("candidate").GetProperty("error_rate").GetDouble());
            Assert.NotEqual(0, root.GetProperty("candidate").GetProperty("gaps").GetArrayLength());
            Assert.Equal(2, root.GetProperty("comparison").GetProperty("top_k_overlap").GetProperty("count").GetInt32());
            Assert.Equal(2, root.GetProperty("requests").GetArrayLength());
        }

        var table = new StringWriter();
        Assert.Equal(0, await CompareCommand.RunAsync(Tool.Environment, sample, candidate, "local", [bundles], Array.Empty<string>(), 10, "score", "text", table,
            CancellationToken.None));
        Assert.Contains("Recorded-mode comparison of 2 bundles", table.ToString());
        Assert.Contains("candidate gap", table.ToString());
    }

    [Fact]
    public async Task MalformedBundle_IsAUsageError_NamingThePointer()
    {
        var bundle = Path.Combine(Directory.CreateTempSubdirectory("stratarec-bundles-").FullName, "bad.json");
        File.WriteAllText(bundle, $$"""{ "format": "{{ReplayBundle.CurrentFormat}}", "request": {} }""");

        var output = new StringWriter();
        var exit = await ReplayCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), "local", [bundle], Array.Empty<string>(), "json", false, output, CancellationToken.None);

        Assert.Equal(2, exit);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Contains("/request", report.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BundleWithANullRun_IsReportedWithItsPointer_AndTheOtherBundlesStillReplay()
    {
        var directory = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        File.WriteAllText(Path.Combine(directory, "a-null-run.json"), MinimalBundle("[null]"));
        File.WriteAllText(Path.Combine(directory, "b-valid.json"), MinimalBundle("[]"));

        var output = new StringWriter();
        var exit = await ReplayCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), "local", [directory], Array.Empty<string>(), "json", false, output, CancellationToken.None);

        Assert.Equal(2, exit);
        using var report = JsonDocument.Parse(output.ToString());
        var bundles = report.RootElement.GetProperty("bundles");
        Assert.Equal(2, bundles.GetArrayLength());
        Assert.Contains("/runs/0: Item 0 of 'runs' must be an object, got null.", bundles[0].GetProperty("error").GetString());
        Assert.True(bundles[1].TryGetProperty("diff", out _));
        var summary = report.RootElement.GetProperty("summary");
        Assert.Equal(("partial", 1, 1), (summary.GetProperty("status").GetString(), summary.GetProperty("replayed").GetInt32(), summary.GetProperty("unreadable").GetInt32()));
    }

    [Fact]
    public async Task Replay_OfADirectoryWithoutBundles_FailsAndSaysSo()
    {
        var empty = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        var output = new StringWriter();

        var exit = await ReplayCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), "local", [empty], Array.Empty<string>(), "json", false, output, CancellationToken.None);

        Assert.Equal(2, exit);
        using var report = JsonDocument.Parse(output.ToString());
        var summary = report.RootElement.GetProperty("summary");
        Assert.Equal(("failed", 0, 0), (summary.GetProperty("status").GetString(), summary.GetProperty("bundles").GetInt32(), summary.GetProperty("replayed").GetInt32()));
    }

    /// <summary>
    /// <c>compare</c> passes only when every bundle was compared; otherwise it exits with 2 and still prints its report, so a
    /// CI step cannot pass on metrics over part of the batch, or over nothing.
    /// </summary>
    [Theory]
    [InlineData("", 2, "failed", 0, 0)]
    [InlineData("null", 2, "failed", 0, 1)]
    [InlineData("null valid", 2, "partial", 1, 1)]
    [InlineData("valid", 0, "ok", 1, 0)]
    public async Task Compare_ExitsNonZero_UnlessEveryBundleWasCompared(string bundles, int exitCode, string status, int compared, int unreadable)
    {
        var directory = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        foreach (var bundle in bundles.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            File.WriteAllText(Path.Combine(directory, bundle + ".json"), MinimalBundle(bundle == "null" ? "[null]" : "[]"));
        }

        var output = new StringWriter();
        var exit = await CompareCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), HomeFeedTestHost.SampleRoot(), "local", [directory], Array.Empty<string>(), 10, "score", "json", output,
            CancellationToken.None);

        Assert.Equal(exitCode, exit);
        using var report = JsonDocument.Parse(output.ToString());
        var root = report.RootElement;
        Assert.Equal((status, compared, unreadable), (root.GetProperty("status").GetString(), root.GetProperty("compared").GetInt32(), root.GetProperty("unreadable").GetInt32()));
    }

    /// <summary>A bundle of the home_feed pipeline that ran nothing (its recorded result failed), with the given runs.</summary>
    internal static string MinimalBundle(string runs) => $$"""
        {
          "format": "{{StrataRec.Artifacts.Model.ReplayBundle.CurrentFormat}}",
          "request": { "pipeline_id": "home_feed", "request_id": "r", "seed": 1, "time_ms": 0, "deadline_ms": 100, "assignment": { "source": "none" } },
          "manifest": { "id": "m" },
          "runs": {{runs}},
          "result": { "served_by": "home_feed", "succeeded": false, "error": "request input 'surface' (string) is required" }
        }
        """;

    [Fact]
    public void Schema_DescribesTheReplayBundle()
    {
        var output = new StringWriter();

        Assert.Equal(0, SchemaCommand.Run(Tool.Environment, "replay_bundle", null, output, TextWriter.Null));

        using var schema = JsonDocument.Parse(output.ToString());
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(new[] { "format", "manifest", "request", "result", "runs" }, properties.EnumerateObject().Select(p => p.Name).Order());
        var run = properties.GetProperty("runs").GetProperty("items").GetProperty("properties");
        Assert.True(run.TryGetProperty("feature_calls", out _));
        Assert.True(run.GetProperty("nodes").GetProperty("items").GetProperty("properties").GetProperty("remaining_ms").TryGetProperty("description", out _));
    }
}
