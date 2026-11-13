using System.Globalization;
using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Local;
using StrataRec.Hosting;
using StrataRec.Hosting.LocalTables;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The sample's local <c>author_store</c> (design 6.6) through the engine: readiness and plan swaps, load failures,
/// restarts, replay, and the same results as fetching the same records.
/// </summary>
public sealed class LocalTableEngineTests
{
    private const string Snapshot = "fixtures/author_snapshot";

    [Fact]
    public async Task APlanThatReadsTheSameTable_TakesOverTheLoadedOne_WithoutLoadingAgain()
    {
        var logs = new ListLoggerFactory();
        var host = HomeFeedTestHost.LoadModified(_ => { });
        await using var engine = new RecEngine(host.Environment, loggerFactory: logs);
        var first = AuthorTable(Load(engine, host.Root));

        // Another configuration of the pipeline: everything but the table's binding and fields changes nothing for it.
        ReplayTests.Edit(host.Root, "pipelines/home_feed.jsonc", "\"coarse_k\": 30,", "\"coarse_k\": 25,");
        var second = AuthorTable(Load(engine, host.Root));

        Assert.Same(first, second);
        Assert.Equal(1, engine.LocalTables.TableCount);
        Assert.Single(logs.Entries, e => e.EventId.Id == 1201); // loaded once
        Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-reuse"))).Succeeded);
    }

    [Fact]
    public async Task APlanThatChangesTheTablesBinding_LoadsANewTable_AndTheOldOneIsReleased()
    {
        var host = HomeFeedTestHost.LoadModified(_ => { });
        await using var engine = new RecEngine(host.Environment);
        var first = AuthorTable(Load(engine, host.Root));

        ReplayTests.Edit(host.Root, "bindings/local.jsonc", "\"max_bytes\": \"16MiB\"", "\"max_bytes\": \"32MiB\"");
        var second = AuthorTable(Load(engine, host.Root));

        Assert.NotSame(first, second);
        Assert.True(second.IsLoaded);
        Assert.Equal(32L << 20, second.Options.MaxBytes);
        Assert.Equal(1, engine.LocalTables.TableCount); // the replaced plan's table is no longer held
        Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-rebuilt"))).Succeeded);
    }

    [Fact]
    public async Task APlanWhoseTableFailsItsFirstLoad_IsNotServed_AndTheErrorNamesTheBindingAndTheLine()
    {
        var host = HomeFeedTestHost.LoadModified(root => BreakLine(root, Snapshot, 3));
        await using var engine = new RecEngine(host.Environment);

        var load = await engine.LoadAsync(host.Request, host.Workspace.ArmRanges("home_feed", "local"));

        Assert.False(load.Applied);
        Assert.False(engine.TryGetPipeline("home_feed", out _));
        var error = Assert.Single(load.Diagnostics, d => d.IsError);
        Assert.Equal(("LOCAL_TABLE_LOAD_FAILED", "bindings:local", "/sources/author_store"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("Local table author_store/author (source 'author_store' in bindings:local) failed to load from snapshot_files@1", error.Message);
        Assert.Contains("part-0000.jsonl:3", error.Message);
        Assert.NotNull(error.Hint);
        Assert.Equal(0, engine.LocalTables.TableCount); // the failed plan's table is released with it
    }

    [Fact]
    public async Task WhileANewPlansTableFails_TheCurrentPlanKeepsServing_AndLoadingAgainRetriesOnceTheDataIsFixed()
    {
        var host = HomeFeedTestHost.LoadModified(_ => { });
        await using var engine = new RecEngine(host.Environment);
        var current = AuthorTable(Load(engine, host.Root));
        var before = await engine.ExecuteAsync(MetricsTests.Request("req-before"));

        // A new release reads a new snapshot directory, whose third line is broken.
        CopySnapshot(host.Root, Snapshot, "fixtures/author_snapshot_v2");
        BreakLine(host.Root, "fixtures/author_snapshot_v2", 3);
        ReplayTests.Edit(host.Root, "bindings/local.jsonc", Snapshot + "\"", "fixtures/author_snapshot_v2\"");
        var failed = await engine.LoadAsync(Compile(host.Root), host.Workspace.ArmRanges("home_feed", "local"));

        Assert.False(failed.Applied);
        Assert.Contains(failed.Diagnostics, d => d.Code == "LOCAL_TABLE_LOAD_FAILED" && d.Message.Contains("author_snapshot_v2", StringComparison.Ordinal));
        Assert.Same(current, AuthorTable(engine));
        var during = await engine.ExecuteAsync(MetricsTests.Request("req-during"));
        Assert.True(during.Succeeded, during.Error);
        Assert.Equal(Items(before), Items(during));

        // Fixed: the same release loads now (a failed table is never reused, so this really loads again).
        CopySnapshot(host.Root, Snapshot, "fixtures/author_snapshot_v2");
        var retried = AuthorTable(Load(engine, host.Root));

        Assert.NotSame(current, retried);
        Assert.True(retried.IsLoaded);
        Assert.Equal(1, engine.LocalTables.TableCount);
    }

    [Fact]
    public async Task AnUpdateSourceThatFailsAfterTheFirstLoad_IsRestarted_AndTheTableServesThroughout()
    {
        var logs = new ListLoggerFactory();
        var host = HomeFeedTestHost.LoadModified(root =>
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"uri\": \"fixtures/author_snapshot\"", "\"uri\": \"fixtures/author_snapshot\", \"reload\": \"PT0.01S\""));
        var tables = new LocalTableHost(new LocalTableHostOptions { RestartDelay = TimeSpan.FromMilliseconds(5), MaxRestartDelay = TimeSpan.FromMilliseconds(20) }, logs);
        await using var engine = new RecEngine(host.Environment, loggerFactory: logs, localTables: tables);
        var table = AuthorTable(Load(engine, host.Root));
        Assert.Equal(186443.0, Followers(table, 100));

        // A snapshot job publishes a manifest the source cannot read: the source fails, and is restarted with backoff.
        File.WriteAllText(Path.Combine(host.Root, Snapshot, "snapshot.json"), "{ \"format\": \"jsonl\", ");
        await Eventually(() => logs.Entries.Count(e => e.EventId.Id == 1203) >= 2, "two restarts of the failed source");
        Assert.Equal(186443.0, Followers(table, 100)); // still serving its last complete load
        Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-failing-source"))).Succeeded);

        // The next good snapshot is picked up by the restarted source.
        File.WriteAllText(Path.Combine(host.Root, Snapshot, "part-0001.jsonl"), "{\"key\": 100, \"version\": 2, \"fields\": {\"a_followers\": 5.0}}\n");
        File.WriteAllText(Path.Combine(host.Root, Snapshot, "snapshot.json"), """{ "format": "jsonl", "files": ["part-0001.jsonl"] }""");
        await Eventually(() => Followers(table, 100) == 5.0, "the fixed snapshot to be loaded");

        Assert.Same(table, AuthorTable(engine));
        Assert.Equal(1, tables.TableCount);
    }

    [Fact]
    public async Task AReplay_ReproducesTheRecording_AfterTheTableChanged_AndOnAnEngineThatLoadsNoTables()
    {
        var (recording, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await recording.ExecuteAsync(ReplayTests.Request("req-local-replay", 1001, "home", ReplayTests.ControlArms));
        Assert.True(recorded.Succeeded, recorded.Error);
        var authorCall = Assert.Single(recorded.Recording!.Runs[0].FeatureCalls, c => c.Source == "author_store");
        Assert.Equal("ok", authorCall.Status);
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));

        // Same configuration, different data: every author has ten times the followers.
        var changed = HomeFeedTestHost.LoadModified(root => ScaleFollowers(root, 10));
        var (changedEngine, _) = MetricsTests.LoadEngine(changed);
        var live = await changedEngine.ExecuteAsync(ReplayTests.Request("req-local-live", 1001, "home", ReplayTests.ControlArms));
        Assert.NotEqual(Items(recorded), Items(live)); // the table matters to the result
        var replayed = await changedEngine.ReplayAsync(bundle);

        Assert.True(replayed.Report.Diff.Identical, ReplayTests.Describe(replayed.Report));
        Assert.Empty(replayed.Report.Gaps);

        // An engine for replays only (stratarec replay): its tables are never loaded, and never needed.
        var sample = HomeFeedTestHost.Load();
        await using var replayOnly = new RecEngine(sample.Environment, localTables: new LocalTableHost(new LocalTableHostOptions { LoadTables = false }));
        Assert.True(replayOnly.Load(sample.Request, sample.Workspace.ArmRanges("home_feed", "local")).Applied);
        var unloaded = await replayOnly.ReplayAsync(bundle);

        Assert.True(unloaded.Report.Diff.Identical, ReplayTests.Describe(unloaded.Report));
        Assert.Empty(unloaded.Report.Gaps);
    }

    [Theory]
    [InlineData(1001L, "home", false)]
    [InlineData(null, "explore", false)]
    [InlineData(1002L, "home", true)]
    public async Task LocalAndFetchedAuthors_ServeTheSameResults_MissingAuthorsIncluded(long? userId, string surface, bool treatment)
    {
        // The same records through memory@1. Author 111 is in neither: its followers take the default either way.
        var fetched = HomeFeedTestHost.LoadModified(root =>
        {
            File.WriteAllText(Path.Combine(root, "fixtures", "authors.json"), MemoryJson(Path.Combine(root, Snapshot, "part-0000.jsonl")));
            SampleBindings.Rebind(root, "author_store", """{ "connector": "memory@1", "data_file": "fixtures/authors.json", "timeout_ms": 5000 }""");
        });
        var (local, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var (remote, _) = MetricsTests.LoadEngine(fetched);
        var arms = treatment ? new[] { "exp_freshness_calibration/calibrated", "exp_mmr_diversity/mmr", "exp_quality_weight/strong" } : ReplayTests.ControlArms;

        var fromTable = await local.ExecuteAsync(ReplayTests.Request("req-same", userId, surface, arms));
        var fromFetch = await remote.ExecuteAsync(ReplayTests.Request("req-same", userId, surface, arms));

        Assert.True(fromTable.Succeeded, fromTable.Error);
        Assert.True(fromFetch.Succeeded, fromFetch.Error);
        Assert.Equal(Items(fromFetch), Items(fromTable));

        // Recorded alike too: one call with the same keys and the stored values (missing where there is no record).
        Assert.Equal(AuthorCalls(fromFetch), AuthorCalls(fromTable));
    }

    private static PlanCompileRequest Compile(string root)
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return request!;
    }

    private static RecEngine Load(RecEngine engine, string root)
    {
        var workspace = ConfigWorkspace.Load(root, new DiagnosticBag());
        var load = engine.Load(Compile(root), workspace.ArmRanges("home_feed", "local"));
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static LocalFeatureTable AuthorTable(RecEngine engine)
    {
        Assert.True(engine.TryGetPipeline("home_feed", out var pipeline));
        return Assert.Single(pipeline.LocalTables!.Tables, t => t.Schema.Source == "author_store");
    }

    private static double Followers(LocalFeatureTable table, long author)
    {
        var version = table.Pin();
        return version.Read(table.Schema.FieldIndex("a_followers"), version.Find(EntityKey.FromInt64(author))).AsFloat64();
    }

    /// <summary>Replaces line <paramref name="line"/> (1-based) of the snapshot's data file with a value of the wrong type.</summary>
    private static void BreakLine(string root, string snapshot, int line)
    {
        var path = Path.Combine(root, snapshot, "part-0000.jsonl");
        var lines = File.ReadAllLines(path);
        lines[line - 1] = "{\"key\": 102, \"version\": 1, \"fields\": {\"a_followers\": \"many\"}}";
        File.WriteAllLines(path, lines);
    }

    private static void CopySnapshot(string root, string from, string to)
    {
        Directory.CreateDirectory(Path.Combine(root, to));
        foreach (var file in Directory.GetFiles(Path.Combine(root, from)))
        {
            File.Copy(file, Path.Combine(root, to, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void ScaleFollowers(string root, double factor)
    {
        var path = Path.Combine(root, Snapshot, "part-0000.jsonl");
        var lines = File.ReadAllLines(path).Select(line =>
        {
            using var record = JsonDocument.Parse(line);
            var followers = record.RootElement.GetProperty("fields").GetProperty("a_followers").GetDouble() * factor;
            return string.Create(CultureInfo.InvariantCulture,
                $"{{\"key\": {record.RootElement.GetProperty("key").GetInt64()}, \"version\": 1, \"fields\": {{\"a_followers\": {followers:F1}}}}}");
        });
        File.WriteAllLines(path, lines);
    }

    /// <summary>The snapshot's records as a <c>memory@1</c> data file.</summary>
    private static string MemoryJson(string snapshotFile)
    {
        var json = new StringBuilder("{");
        foreach (var line in File.ReadAllLines(snapshotFile))
        {
            using var record = JsonDocument.Parse(line);
            json.Append(json.Length == 1 ? string.Empty : ",")
                .Append(CultureInfo.InvariantCulture, $"\"{record.RootElement.GetProperty("key").GetInt64()}\": ")
                .Append(record.RootElement.GetProperty("fields").GetRawText());
        }

        return json.Append('}').ToString();
    }

    private static List<string> AuthorCalls(RecResult result) => result.Recording!.Runs[0].FeatureCalls
        .Where(c => c.Source == "author_store")
        .Select(c => $"{c.Shape} {c.Status} keys={c.Keys} fields={string.Join(",", c.Fields.Select(f => f.Name + ":" + f.Type))} " +
            string.Join(";", c.Values!.OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value.GetRawText()}")))
        .ToList();

    private static List<(long, int, string)> Items(RecResult result) =>
        result.Items.Select(i => (i.Id.Int64Value, i.Rank, string.Join(";", i.Columns.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")))).ToList();

    /// <summary>Waits for a condition another thread brings about (a background source); fails after a generous bound.</summary>
    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            await Task.Delay(5);
        }
    }
}
