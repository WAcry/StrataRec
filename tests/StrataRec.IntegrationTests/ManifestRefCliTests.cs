using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Cli.Commands;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Configurations named by a release manifest in an artifact store (<c>--manifest</c> with <c>--store</c>, or a ref of any
/// kind): <c>explain-plan</c>, <c>replay</c> (also on the manifest each bundle recorded) and <c>compare</c>, and every
/// error of a ref, with its hint.
/// </summary>
public sealed class ManifestRefCliTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    public async Task ExplainPlan_OfAManifest_ById_OrByFile_PrintsThePlanOfItsWorkspace(string format)
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);

        foreach (var arms in new[] { Array.Empty<string>(), new[] { "exp_mmr_diversity/mmr,exp_quality_weight/strong" } })
        {
            var workspace = await Explain(root, null, null, arms);
            var byId = await Explain(null, id, store, arms);
            var byFile = await Explain(null, file, store, arms);

            Assert.Equal(Normalized(workspace, "workspace:home_feed/local"), Normalized(byId, id));
            Assert.Equal(Normalized(byId, id), Normalized(byFile, id)); // a file has the id build gave its manifest
        }

        async Task<string> Explain(string? config, string? manifest, string? from, string[] arms)
        {
            var output = new StringWriter();
            var errors = new StringWriter();
            Assert.True(0 == await ExplainPlanCommand.RunAsync(config, "local", null, format, arms, output, CancellationToken.None, manifest, from, errors), errors.ToString());
            return output.ToString();
        }

        // Everything but the configuration's name and the compile time.
        string Normalized(string output, string manifestId)
        {
            if (format == "text")
            {
                var lines = output.Split('\n');
                Assert.StartsWith($"pipeline home_feed  ({manifestId}, compiled in ", lines[0]);
                return string.Join('\n', lines.Skip(1));
            }

            var document = JsonNode.Parse(output)!.AsObject();
            Assert.Equal(manifestId, document["manifest"]!.GetValue<string>());
            document.Remove("manifest");
            document.Remove("compile_ms");
            return document.ToJsonString();
        }
    }

    [Fact]
    public async Task ExplainPlan_WithArms_ShowsTheTapsAndTheOutputOfTheCombination()
    {
        var output = new StringWriter();

        Assert.Equal(0, await ExplainPlanCommand.RunAsync(HomeFeedTestHost.SampleRoot(), "local", null, "json", new[] { "exp_freshness_calibration/calibrated" }, output,
            CancellationToken.None));

        var plan = JsonNode.Parse(output.ToString())!;
        Assert.Equal("""["exp_freshness_calibration/calibrated"]""", plan["combination"]!.ToJsonString());
        var served = plan["taps"]!["served"]!;
        Assert.Equal("""{"output":true}""", served["attach"]!.ToJsonString());
        Assert.Equal("col.score.final@fusion", served["columns"]!["pre_calibration"]!.GetValue<string>()); // the arm's tap variant
        Assert.Contains("item.age_hours", served["features"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(1.0, plan["funnel_groups"]!["all"]!["rate"]!.GetValue<double>());
        Assert.Equal("final_top_k", plan["output"]!["limit_param"]!.GetValue<string>());
        Assert.Equal("col.score.final", plan["output"]!["columns"]!["score"]!.GetValue<string>());
        Assert.Equal("ctr_backup", Assert.Single(plan["fallbacks"]!.AsArray())!["node"]!.GetValue<string>());
        Assert.Contains(plan["timeline"]!.AsArray(), n => n!["node"]!.GetValue<string>() == "freshness_calibration");
    }

    [Fact]
    public async Task Replay_OfARequestThatFailedOver_RunsTheFallbackPipelineOnTheManifestItRecorded()
    {
        // Two pipelines released separately: 'primary' (its source fails) falls back to 'backup' (review 24 P2).
        var root = ReviewRegressionTests.FallbackWorkspace(brokenLatencyMs: 0);
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        File.WriteAllText(bindings, File.ReadAllText(bindings).Replace("\"fixtures/recall_popular.json\"",
            JsonSerializer.Serialize(Path.Combine(root, "fixtures", "recall_popular.json")), StringComparison.Ordinal));
        var store = ManifestStores.NewStore();
        var manifests = new List<(string File, string Id)>();
        foreach (var pipeline in new[] { "primary", "backup" })
        {
            var file = Path.Combine(store, pipeline + ".json");
            Assert.Equal(0, await BuildCommand.RunAsync(root, "local", pipeline, store, file, 1, TextWriter.Null, CancellationToken.None));
            manifests.Add((file, ContentHash.Compute(ArtifactSerializer.ToNode(ManifestStores.Read(file)))));
        }

        // A node serving both releases records a request that failed over.
        var bundles = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        ReplayBundle recording;
        await using (var engine = new RecEngine(CommonOptions.PlanEnvironment(root)))
        {
            foreach (var (file, id) in manifests)
            {
                var request = await ManifestBuilder.LoadAsync(ManifestStores.Read(file), id, new FileSystemArtifactStore(store), new DiagnosticBag());
                Assert.True((await engine.LoadAsync(request!, 1)).Applied);
            }

            var result = await engine.ExecuteAsync(new RecRequest
            {
                PipelineId = "primary", RequestId = "req-failover", Inputs = new Core.Values.RequestInputs(), Deadline = TimeSpan.FromSeconds(30),
                Debug = new DebugOptions { Record = true },
            });
            Assert.Equal("backup", result.ServedBy);
            recording = result.Recording!;
            Assert.Equal(new[] { manifests[0].Id, manifests[1].Id }, recording.Runs.Select(r => r.ManifestId));
            ReplayBundleFile.Save(recording, Path.Combine(bundles, "failed-over.json"));
        }

        // Only the store: each run on the manifest it recorded. The primary's manifest named: the fallback still on its own.
        foreach (var manifest in new[] { null, manifests[0].Id })
        {
            var output = new StringWriter();
            Assert.Equal(0, await ReplayCommand.RunAsync(null, "local", bundles, Array.Empty<string>(), "json", expectIdentical: true, output, CancellationToken.None,
                manifest: manifest, store: store));
            using var report = JsonDocument.Parse(output.ToString());
            var bundle = report.RootElement.GetProperty("bundles")[0];
            Assert.True(bundle.GetProperty("diff").GetProperty("identical").GetBoolean(), output.ToString());
            Assert.Equal("backup", bundle.GetProperty("served_by").GetString());
        }

        // compare resolves each side the same way: the primary's manifest on both sides fails over as recorded.
        Assert.Equal(0, await CompareCommand.RunAsync(manifests[0].Id, null, "local", bundles, Array.Empty<string>(), 5, "source", "json", TextWriter.Null,
            CancellationToken.None, candidateManifest: manifests[0].Id, store: store));

        // A store without the fallback's manifest: not replayed, naming the pipeline, its manifest and why.
        File.Delete(new FileSystemArtifactStore(store).GetBlobPath(manifests[1].Id));
        var errors = new StringWriter();
        Assert.Equal(2, await ReplayCommand.RunAsync(null, "local", bundles, Array.Empty<string>(), "text", expectIdentical: false, TextWriter.Null, CancellationToken.None,
            store: store, errors: errors));
        Assert.Contains($"pipeline 'backup' ran on manifest {manifests[1].Id}, which does not load: MANIFEST_UNKNOWN", errors.ToString());

        // An engine without the fallback pipeline says so instead of passing the primary's failure off as the replay.
        await using var primaryOnly = new RecEngine(CommonOptions.PlanEnvironment(root), bindingOptions: new Hosting.Bindings.BindingHostOptions { Load = false });
        var primary = await ManifestBuilder.LoadAsync(ManifestStores.Read(manifests[0].File), manifests[0].Id, new FileSystemArtifactStore(store), new DiagnosticBag());
        Assert.True((await primaryOnly.LoadAsync(primary!, 1)).Applied);
        var outcome = await primaryOnly.ReplayAsync(recording);
        Assert.False(outcome.Result.Succeeded);
        Assert.Contains(outcome.Report.Notes, n => n.Contains("this engine has not loaded 'backup'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replay_WithOnlyAStore_ReplaysEachBundleOnTheManifestItRecorded_AndReportsWorkspaceBundles()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);
        var manifestBundles = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;

        // Requests served by an engine that loaded the manifest, as a node does (its bindings loaded from the workspace's files).
        var request = await ManifestBuilder.LoadAsync(ManifestStores.Read(file), id,
            new FileSystemArtifactStore(store), new DiagnosticBag());
        await using (var engine = new RecEngine(CommonOptions.PlanEnvironment(root)))
        {
            Assert.True((await engine.LoadAsync(request!, 1)).Applied);
            foreach (var (name, userId, arms) in new[] { ("a-user", 1001L, new[] { "exp_mmr_diversity/mmr", "exp_quality_weight/strong" }), ("b-other", 1002L, Array.Empty<string>()) })
            {
                var result = await engine.ExecuteAsync(ReplayTests.Request("req-" + name, userId, "home", arms));
                Assert.True(result.Succeeded, result.Error);
                Assert.Equal(id, result.Recording!.Manifest.Id);
                ReplayBundleFile.Save(result.Recording, Path.Combine(manifestBundles, name + ".json"));
            }
        }

        var output = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(null, "local", manifestBundles, Array.Empty<string>(), "json", expectIdentical: true, output, CancellationToken.None,
            store: store));
        using (var report = JsonDocument.Parse(output.ToString()))
        {
            Assert.All(report.RootElement.GetProperty("bundles").EnumerateArray(), b =>
            {
                Assert.True(b.GetProperty("diff").GetProperty("identical").GetBoolean());
                Assert.Equal(id, b.GetProperty("manifest").GetProperty("replayed").GetString());
                Assert.Empty(b.GetProperty("manifest").GetProperty("changed_artifacts").EnumerateArray());
            });
        }

        // A bundle recorded on the workspace names no manifest: it is not replayed, says how to replay it, and the others still replay.
        var mixed = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        foreach (var bundleFile in Directory.GetFiles(manifestBundles))
        {
            File.Copy(bundleFile, Path.Combine(mixed, Path.GetFileName(bundleFile)));
        }

        Assert.Equal(0, await RunCommand.RunAsync(root, "local", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), false, TextWriter.Null,
            CancellationToken.None, recordPath: Path.Combine(mixed, "c-workspace.json")));
        output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(2, await ReplayCommand.RunAsync(null, "local", mixed, Array.Empty<string>(), "json", expectIdentical: true, output, CancellationToken.None,
            store: store, errors: errors));
        using (var report = JsonDocument.Parse(output.ToString()))
        {
            var summary = report.RootElement.GetProperty("summary");
            Assert.Equal(("partial", 3, 2, 2, 0, 1),
                (summary.GetProperty("status").GetString(), summary.GetProperty("bundles").GetInt32(), summary.GetProperty("replayed").GetInt32(),
                 summary.GetProperty("identical").GetInt32(), summary.GetProperty("unreadable").GetInt32(), summary.GetProperty("unresolved").GetInt32()));
            var bundles = report.RootElement.GetProperty("bundles");
            var error = bundles[2].GetProperty("error").GetString()!;
            Assert.Contains("c-workspace.json: recorded on a workspace ('workspace:home_feed/local')", error);
            Assert.Contains("hint: replay it on its workspace: -c <workspace directory> -e local", error);
            Assert.Contains(error, errors.ToString());
        }

        // -c and --manifest replace the recorded configuration, as -c always did.
        Assert.Equal(0, await ReplayCommand.RunAsync(root, "local", mixed, Array.Empty<string>(), "text", expectIdentical: true, TextWriter.Null, CancellationToken.None,
            store: store));
        output = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(null, "local", mixed, Array.Empty<string>(), "json", expectIdentical: true, output, CancellationToken.None,
            manifest: id, store: store));
        using (var report = JsonDocument.Parse(output.ToString()))
        {
            Assert.All(report.RootElement.GetProperty("bundles").EnumerateArray(), b => Assert.Equal(id, b.GetProperty("manifest").GetProperty("replayed").GetString()));
        }
    }

    [Fact]
    public async Task ReplayEngines_LoadEachRecordedManifestOnce_ForEveryBundleRecordedOnIt()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, _) = await ManifestStores.BuildAsync(root, store);
        var bundle = ReplayBundleFile.Parse(ReplayCliTests.MinimalBundle("[]").Replace("\"id\": \"m\"", $"\"id\": \"{id}\"", StringComparison.Ordinal));
        Assert.Equal(id, bundle.Manifest.Id);

        await using var engines = (await ReplayEngines.CreateAsync(null, store, "local", "text", TextWriter.Null, CancellationToken.None))!;
        var first = await engines.ForAsync(bundle, CancellationToken.None);
        var second = await engines.ForAsync(bundle, CancellationToken.None);

        Assert.NotNull(first.Engine);
        Assert.Same(first.Engine, second.Engine);
    }

    [Fact]
    public async Task Compare_TakesManifestsOnBothSides()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);
        var bundles = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        Assert.Equal(0, await RunCommand.RunAsync(root, "local", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), false, TextWriter.Null,
            CancellationToken.None, recordPath: Path.Combine(bundles, "user_1001.json")));

        var output = new StringWriter();
        Assert.Equal(0, await CompareCommand.RunAsync(id, null, "local", bundles, Array.Empty<string>(), 10, "score", "json", output, CancellationToken.None,
            candidateManifest: file, store: store));

        using var report = JsonDocument.Parse(output.ToString());
        var root2 = report.RootElement;
        Assert.Equal((id, file), (root2.GetProperty("baseline").GetProperty("config").GetString(), root2.GetProperty("candidate").GetProperty("config").GetString()));
        Assert.Equal((id, id), (root2.GetProperty("baseline").GetProperty("manifest").GetString(), root2.GetProperty("candidate").GetProperty("manifest").GetString()));
        Assert.Equal(1, root2.GetProperty("comparison").GetProperty("identical").GetInt32());
    }

    [Fact]
    public async Task UsageErrors_NameTheOptionAndHowToFixIt()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);
        var missing = Path.Combine(store, "no-such-file.json");

        await Expect(2, "CONFIG_CONFLICT", "--manifest", "pass one: -c <workspace directory>", config: root, manifest: id, store: store);
        await Expect(2, "STORE_REQUIRED", "--manifest", "pass --store <dir>", manifest: id);
        await Expect(2, "STORE_REQUIRED", "--manifest", "pass --store <dir>", manifest: file);
        await Expect(2, "CONFIG_REF_UNKNOWN", "--manifest", "a workspace directory goes with -c", manifest: missing, store: store);
        await Expect(2, "MANIFEST_IS_DIRECTORY", "--manifest", "pass a workspace directory with -c", manifest: root, store: store);
        await Expect(2, "MANIFEST_ID_INVALID", "--manifest", "copy the id 'stratarec build' printed", manifest: id.ToUpperInvariant().Replace("SHA256:", "sha256:"), store: store);

        // compare's baseline is a ref of any kind: one that names nothing is a usage error too.
        var errors = new StringWriter();
        Assert.Equal(2, await CompareCommand.RunAsync(missing, root, "local", store, Array.Empty<string>(), 10, "score", "json", TextWriter.Null, CancellationToken.None,
            errors: errors));
        Assert.Contains(ManifestStores.Errors(errors.ToString()),
            e => e is ("CONFIG_REF_UNKNOWN", "--baseline", "", { } hint) && hint.Contains("a workspace directory, a manifest content id"));
        errors = new StringWriter();
        Assert.Equal(2, await ReplayCommand.RunAsync(root, "local", store, Array.Empty<string>(), "json", false, TextWriter.Null, CancellationToken.None, manifest: id,
            store: store, errors: errors));
        Assert.Contains(ManifestStores.Errors(errors.ToString()), e => e.Code == "CONFIG_CONFLICT");
    }

    [Fact]
    public async Task ManifestErrors_SayWhatIsWrongWithTheStoreOrTheManifest()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);
        var blobs = new FileSystemArtifactStore(store);

        // An id the store does not have.
        var unknown = "sha256:" + new string('0', 64);
        await Expect(1, "MANIFEST_UNKNOWN", "manifest:" + unknown, "check the id", manifest: unknown, store: store);

        // A manifest pinned to another pipeline than --pipeline asks for.
        await Expect(1, "MANIFEST_OTHER_PIPELINE", "manifest:" + id, "omit --pipeline", manifest: id, store: store, pipeline: "other_feed", pointer: "/pipeline_id");

        // A manifest file that is not a manifest: its artifacts lack the pipeline, catalog and bindings.
        var notAManifest = Path.Combine(store, "not-a-manifest.json");
        File.WriteAllText(notAManifest, """{ "pipeline_id": "home_feed", "environment": "local", "artifacts": {} }""");
        await Expect(1, "MANIFEST_SCHEMA", notAManifest, "release manifest", manifest: notAManifest, store: store, pointer: "/artifacts");

        // A damaged, then a missing artifact blob: reported by the manifest loader at the manifest's pointer.
        var manifest = ManifestStores.Read(file);
        File.WriteAllText(blobs.GetBlobPath(manifest.Artifacts.Pipeline), "{}");
        await Expect(1, "ARTIFACT_UNAVAILABLE", "manifest:" + id, "the stored blob is damaged", manifest: id, store: store, pointer: "/artifacts/pipeline");
        File.Delete(blobs.GetBlobPath(manifest.Artifacts.Pipeline));
        await Expect(1, "ARTIFACT_MISSING", "manifest:" + id, "stratarec build", manifest: id, store: store, pointer: "/artifacts/pipeline");

        // A damaged manifest blob.
        File.WriteAllText(blobs.GetBlobPath(id), "{}");
        await Expect(1, "MANIFEST_UNREADABLE", "manifest:" + id, "the blob is damaged", manifest: id, store: store);
    }

    private static async Task Expect(
        int exitCode, string code, string artifact, string hint, string? config = null, string? manifest = null, string? store = null, string? pipeline = null, string pointer = "")
    {
        var errors = new StringWriter();
        Assert.Equal(exitCode, await ExplainPlanCommand.RunAsync(config, "local", pipeline, "json", Array.Empty<string>(), TextWriter.Null, CancellationToken.None,
            manifest, store, errors));
        var error = Assert.Single(ManifestStores.Errors(errors.ToString()));
        Assert.Equal((code, artifact, pointer), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains(hint, error.Hint);
    }
}
