using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.CommandLine;
using StrataRec.CommandLine.Commands;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

public sealed class ManifestRefCliTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    public async Task ExplainPlan_OfAManifest_ById_OrByFile_PrintsThePlanOfItsWorkspace(string format)
    {
        var root = ManifestStores.Workspace(InteractionWarningTests.Unreviewed);
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);

        foreach (var arms in new[] { Array.Empty<string>(), new[] { "exp_mmr_diversity/mmr,exp_quality_weight/strong" } })
        {
            var workspace = await Explain(root, null, null, arms);
            var byId = await Explain(null, id, store, arms);
            var byFile = await Explain(null, file, store, arms);

            Assert.Equal(Normalized(workspace, "workspace:home_feed/local"), Normalized(byId, id));
            Assert.Equal(Normalized(byId, id), Normalized(byFile, id));
            if (format == "text")
            {
                Assert.DoesNotContain(workspace.Split('\n'), line => line.TrimEnd('\r').EndsWith(' '));
                Assert.Matches(@"exp_mmr_diversity\.jsonc:\d+:\d+: warning INTERACTION_READ ", workspace);
                Assert.Contains("warning INTERACTION_READ experiment:exp_mmr_diversity/arms/mmr/", byId);
            }
        }

        async Task<string> Explain(string? config, string? manifest, string? from, string[] arms)
        {
            var output = new StringWriter();
            var errors = new StringWriter();
            Assert.True(0 == await ExplainPlanCommand.RunAsync(Tool.Environment, config, "local", null, format, arms, output, CancellationToken.None, manifest, from, errors), errors.ToString());
            return output.ToString();
        }

        string Normalized(string output, string manifestId)
        {
            if (format == "text")
            {
                var lines = output.Split('\n');
                Assert.StartsWith($"pipeline home_feed  ({manifestId}, compiled in ", lines[0]);
                return string.Join('\n', lines.Skip(1).TakeWhile(l => !l.Contains(" INTERACTION_READ ", StringComparison.Ordinal)));
            }

            var document = JsonNode.Parse(output)!.AsObject();
            Assert.Equal(manifestId, document["manifest"]!.GetValue<string>());
            document.Remove("manifest");
            document.Remove("compile_ms");
            return document.ToJsonString();
        }
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("json", false)]
    [InlineData("text", true)]
    [InlineData("json", true)]
    public async Task ExplainPlan_OfAPlanWithoutExperiments_ShowsWhatEachNodeReadsAndWrites(string format, bool ofAManifest)
    {
        var plain = await Explain(ManifestStores.Workspace(root =>
        {
            var release = Path.Combine(root, "releases", "home_feed.local.jsonc");
            File.WriteAllLines(release, File.ReadAllLines(release).Where(line => !line.Contains("\"layer\"", StringComparison.Ordinal)));
        }));
        var surveyed = await Explain(ManifestStores.Workspace());

        if (format == "json")
        {
            var plan = JsonNode.Parse(plain)!;
            Assert.Equal("[]", plan["combination"]!.ToJsonString());
            var nodes = plan["timeline"]!.AsArray().ToDictionary(n => n!["node"]!.GetValue<string>(), n => n!);
            Assert.Equal("""["item.is_policy_safe","item.report_rate"]""", nodes["safety_filter"]["reads"]!.ToJsonString());
            Assert.Equal("[]", nodes["safety_filter"]["writes"]!.ToJsonString());
            Assert.Equal("[]", plan["arms"]!.ToJsonString());
            Assert.Null(JsonNode.Parse(surveyed)!["combination"]);
        }
        else
        {
            Assert.Contains("timeline for baseline:", plain);
            Assert.Matches(@"<- reads\s+item\.is_policy_safe, item\.report_rate\r?\n\s+-> writes\s+\(no columns\)", plain);
            Assert.Matches(@"<- reads\s+item\.smoothed_ctr\r?\n\s+-> writes\s+score\.coarse", plain);
            Assert.DoesNotContain("timeline for", surveyed);
            Assert.DoesNotContain("<- reads", surveyed);
        }

        async Task<string> Explain(string root)
        {
            var output = new StringWriter();
            var errors = new StringWriter();
            var store = ofAManifest ? ManifestStores.NewStore() : null;
            var manifest = store is null ? null : (await ManifestStores.BuildAsync(root, store)).Id;
            Assert.True(
                0 == await ExplainPlanCommand.RunAsync(Tool.Environment, manifest is null ? root : null, "local", null, format, [], output, CancellationToken.None, manifest, store, errors),
                errors.ToString());
            return output.ToString();
        }
    }

    [Fact]
    public async Task ExplainPlan_WithArms_ShowsTheTapsAndTheOutputOfTheCombination()
    {
        var output = new StringWriter();

        Assert.Equal(0, await ExplainPlanCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), "local", null, "json", new[] { "exp_freshness_calibration/calibrated" }, output,
            CancellationToken.None));

        var plan = JsonNode.Parse(output.ToString())!;
        Assert.Equal("""["exp_freshness_calibration/calibrated"]""", plan["combination"]!.ToJsonString());

        var nodes = plan["timeline"]!.AsArray().ToDictionary(n => n!["node"]!.GetValue<string>(), n => n!);
        Assert.Equal("""["item.is_policy_safe","item.report_rate"]""", nodes["safety_filter"]["reads"]!.ToJsonString());
        Assert.Equal("[]", nodes["safety_filter"]["writes"]!.ToJsonString());
        Assert.Equal("""["item.age_hours","score.final"]""", nodes["freshness_calibration"]["reads"]!.ToJsonString());
        Assert.Equal("""["score.final"]""", nodes["freshness_calibration"]["writes"]!.ToJsonString());
        var served = plan["taps"]!["served"]!;
        Assert.Equal("""{"output":true}""", served["attach"]!.ToJsonString());
        Assert.Equal("col.score.final@fusion", served["columns"]!["pre_calibration"]!.GetValue<string>());
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
        var root = ExperimentRegressionTests.FallbackWorkspace(brokenLatencyMs: 0);
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        File.WriteAllText(bindings, File.ReadAllText(bindings).Replace("\"fixtures/recall_popular.json\"",
            JsonSerializer.Serialize(Path.Combine(root, "fixtures", "recall_popular.json")), StringComparison.Ordinal));
        var store = ManifestStores.NewStore();
        var manifests = new List<(string File, string Id)>();
        foreach (var pipeline in new[] { "primary", "backup" })
        {
            var file = Path.Combine(store, pipeline + ".json");
            Assert.Equal(0, await BuildCommand.RunAsync(Tool.Environment, root, "local", pipeline, store, file, 1, TextWriter.Null, CancellationToken.None));
            manifests.Add((file, ContentHash.Compute(ArtifactSerializer.ToNode(ManifestStores.Read(file)))));
        }

        var bundles = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        ReplayBundle recording;
        await using (var engine = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root)))
        {
            foreach (var (file, id) in manifests)
            {
                var request = await ManifestBuilder.LoadAsync(ManifestStores.Read(file), id, new FileSystemArtifactStore(store), new DiagnosticBag(), TestContext.Current.CancellationToken);
                Assert.True((await engine.LoadAsync(request!, 1, TestContext.Current.CancellationToken)).Applied);
            }

            var result = await engine.ExecuteAsync(new RecRequest
            {
                PipelineId = "primary", RequestId = "req-failover", Inputs = new Core.Values.RequestInputs(), Deadline = TimeSpan.FromSeconds(30),
                Debug = new DebugOptions { Record = true },
            }, TestContext.Current.CancellationToken);
            Assert.Equal("backup", result.ServedBy);
            recording = result.Recording!;
            Assert.Equal(new[] { manifests[0].Id, manifests[1].Id }, recording.Runs.Select(r => r.ManifestId));
            ReplayBundleFile.Save(recording, Path.Combine(bundles, "failed-over.json"));
        }

        foreach (var manifest in new[] { null, manifests[0].Id })
        {
            var output = new StringWriter();
            Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, null, "local", [bundles], Array.Empty<string>(), "json", expectIdentical: true, output, CancellationToken.None,
                manifest: manifest, store: store));
            using var report = JsonDocument.Parse(output.ToString());
            var bundle = report.RootElement.GetProperty("bundles")[0];
            Assert.True(bundle.GetProperty("diff").GetProperty("identical").GetBoolean(), output.ToString());
            Assert.Equal("backup", bundle.GetProperty("served_by").GetString());
        }

        Assert.Equal(0, await CompareCommand.RunAsync(Tool.Environment, manifests[0].Id, null, "local", [bundles], Array.Empty<string>(), 5, "source", "json", TextWriter.Null,
            CancellationToken.None, candidateManifest: manifests[0].Id, store: store));

        var cut = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        var withoutPrimary = ReplayBundleFile.Load(Path.Combine(bundles, "failed-over.json"));
        withoutPrimary.Runs.RemoveAt(0);
        ReplayBundleFile.Save(withoutPrimary, Path.Combine(cut, "no-primary.json"));
        foreach (var manifest in new[] { null, manifests[0].Id })
        {
            var output = new StringWriter();
            Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, null, "local", [cut], Array.Empty<string>(), "json", expectIdentical: false, output, CancellationToken.None,
                manifest: manifest, store: store));
            using var report = JsonDocument.Parse(output.ToString());
            var bundle = report.RootElement.GetProperty("bundles")[0];
            Assert.Equal(manifests[0].Id, bundle.GetProperty("manifest").GetProperty("replayed").GetString());
            Assert.Contains(bundle.GetProperty("gaps").EnumerateArray(), g => g.GetProperty("kind").GetString() == "recall" && g.GetProperty("source").GetString() == "broken");
        }

        File.Delete(new FileSystemArtifactStore(store).GetBlobPath(manifests[1].Id));
        var errors = new StringWriter();
        Assert.Equal(2, await ReplayCommand.RunAsync(Tool.Environment, null, "local", [bundles], Array.Empty<string>(), "text", expectIdentical: false, TextWriter.Null, CancellationToken.None,
            store: store, errors: errors));
        Assert.Contains($"pipeline 'backup' ran on manifest {manifests[1].Id}, which does not load: MANIFEST_UNKNOWN", errors.ToString());

        await using var primaryOnly = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root), bindingOptions: new Hosting.Bindings.BindingHostOptions { Load = false });
        var primary = await ManifestBuilder.LoadAsync(ManifestStores.Read(manifests[0].File), manifests[0].Id, new FileSystemArtifactStore(store), new DiagnosticBag(),
            TestContext.Current.CancellationToken);
        Assert.True((await primaryOnly.LoadAsync(primary!, 1, TestContext.Current.CancellationToken)).Applied);
        var outcome = await primaryOnly.ReplayAsync(recording, cancellationToken: TestContext.Current.CancellationToken);
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

        var request = await ManifestBuilder.LoadAsync(ManifestStores.Read(file), id,
            new FileSystemArtifactStore(store), new DiagnosticBag(), TestContext.Current.CancellationToken);
        await using (var engine = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root)))
        {
            Assert.True((await engine.LoadAsync(request!, 1, TestContext.Current.CancellationToken)).Applied);
            foreach (var (name, userId, arms) in new[] { ("a-user", 1001L, new[] { "exp_mmr_diversity/mmr", "exp_quality_weight/strong" }), ("b-other", 1002L, Array.Empty<string>()) })
            {
                var result = await engine.ExecuteAsync(ReplayTests.Request("req-" + name, userId, "home", arms), TestContext.Current.CancellationToken);
                Assert.True(result.Succeeded, result.Error);
                Assert.Equal(id, result.Recording!.Manifest.Id);
                ReplayBundleFile.Save(result.Recording, Path.Combine(manifestBundles, name + ".json"));
            }
        }

        var output = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, null, "local", [manifestBundles], Array.Empty<string>(), "json", expectIdentical: true, output, CancellationToken.None,
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

        var mixed = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        foreach (var bundleFile in Directory.GetFiles(manifestBundles))
        {
            File.Copy(bundleFile, Path.Combine(mixed, Path.GetFileName(bundleFile)));
        }

        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, root, "local", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), false, TextWriter.Null,
            CancellationToken.None, recordPath: Path.Combine(mixed, "c-workspace.json")));
        output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(2, await ReplayCommand.RunAsync(Tool.Environment, null, "local", [mixed], Array.Empty<string>(), "json", expectIdentical: true, output, CancellationToken.None,
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

        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, root, "local", [mixed], Array.Empty<string>(), "text", expectIdentical: true, TextWriter.Null, CancellationToken.None,
            store: store));
        output = new StringWriter();
        Assert.Equal(0, await ReplayCommand.RunAsync(Tool.Environment, null, "local", [mixed], Array.Empty<string>(), "json", expectIdentical: true, output, CancellationToken.None,
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

        await using var engines = (await ReplayEngines.CreateAsync(Tool.Environment, null, store, "local", "text", TextWriter.Null, CancellationToken.None))!;
        var first = await engines.ForAsync(bundle, CancellationToken.None);
        var second = await engines.ForAsync(bundle, CancellationToken.None);

        Assert.NotNull(first.Engine);
        Assert.Same(first.Engine, second.Engine);
    }

    [Fact]
    public async Task ReplayEngines_RefuseABundleObjectOfThePreviousFormat_BeforeReadingItsRuns()
    {
        var store = ManifestStores.NewStore();
        var json = ReplayCliTests.MinimalBundle("[]").Replace(ReplayBundle.CurrentFormat, "stratarec.replay_bundle/2026-11-20.1", StringComparison.Ordinal);
        var bundle = ArtifactSerializer.Deserialize<ReplayBundle>(json);

        await using var engines = (await ReplayEngines.CreateAsync(Tool.Environment, null, store, "local", "text", TextWriter.Null, CancellationToken.None))!;
        var error = await Assert.ThrowsAsync<StrataRec.Pipeline.Replay.ReplayBundleException>(() => engines.ForAsync(bundle, CancellationToken.None));

        Assert.Equal("/format", error.Pointer);
        Assert.Contains("record the request again", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_TakesManifestsOnBothSides()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);
        var bundles = Directory.CreateTempSubdirectory("stratarec-bundles-").FullName;
        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, root, "local", Path.Combine(root, "requests", "user_1001.json"), Array.Empty<string>(), false, TextWriter.Null,
            CancellationToken.None, recordPath: Path.Combine(bundles, "user_1001.json")));

        var output = new StringWriter();
        Assert.Equal(0, await CompareCommand.RunAsync(Tool.Environment, id, null, "local", [bundles], Array.Empty<string>(), 10, "score", "json", output, CancellationToken.None,
            candidateManifest: file, store: store));

        using var report = JsonDocument.Parse(output.ToString());
        var root2 = report.RootElement;
        Assert.Equal((id, file), (root2.GetProperty("baseline").GetProperty("config").GetString(), root2.GetProperty("candidate").GetProperty("config").GetString()));
        Assert.Equal((id, id), (root2.GetProperty("baseline").GetProperty("manifest").GetString(), root2.GetProperty("candidate").GetProperty("manifest").GetString()));
        Assert.Equal(1, root2.GetProperty("comparison").GetProperty("identical").GetInt32());
    }

    [Fact]
    public async Task Run_OnAManifest_ServesWhatItsWorkspaceServes_AndSaysHowToReplayIt()
    {
        var root = ManifestStores.Workspace(AbsoluteSnapshot);
        var store = ManifestStores.NewStore();
        var (id, manifestFile) = await ManifestStores.BuildAsync(root, store);
        var request = Path.Combine(root, "requests", "user_1001.json");
        var bundle = Path.Combine(store, "recorded.json");

        var (code, workspace, errors) = await Run(request, config: root);
        Assert.True(code == 0, errors);
        (code, var released, errors) = await Run(request, manifest: id, store: store, record: bundle);
        Assert.True(code == 0, errors);

        using var fromWorkspace = JsonDocument.Parse(workspace);
        using var fromManifest = JsonDocument.Parse(released);
        Assert.Equal(id, fromManifest.RootElement.GetProperty("manifest").GetString());
        Assert.Equal(fromWorkspace.RootElement.GetProperty("items").GetRawText(), fromManifest.RootElement.GetProperty("items").GetRawText());
        Assert.Contains($"replay it with: stratarec replay -s {store} {bundle}", errors);
        var replayErrors = new StringWriter();
        Assert.True(0 == await ReplayCommand.RunAsync(Tool.Environment, null, "local", [bundle], Array.Empty<string>(), "json", true, TextWriter.Null,
            CancellationToken.None, store: store, errors: replayErrors), replayErrors.ToString());

        var copy = Path.Combine(ManifestStores.NewStore(), "release.json");
        File.Copy(manifestFile, copy);
        var fileBundle = Path.Combine(store, "recorded-file.json");
        (code, _, errors) = await Run(request, manifest: copy, store: store, record: fileBundle);
        Assert.True(code == 0, errors);
        Assert.Contains($"replay it with: stratarec replay --manifest {copy} -s {store} {fileBundle}", errors);
        replayErrors = new StringWriter();
        Assert.True(0 == await ReplayCommand.RunAsync(Tool.Environment, null, "local", [fileBundle], Array.Empty<string>(), "json", true, TextWriter.Null,
            CancellationToken.None, manifest: copy, store: store, errors: replayErrors), replayErrors.ToString());
    }

    [Fact]
    public async Task Run_OnAManifest_SaysWhatItCannotDo()
    {
        var root = ManifestStores.Workspace(AbsoluteSnapshot);
        var store = ManifestStores.NewStore();
        var (id, _) = await ManifestStores.BuildAsync(root, store);
        var other = Path.Combine(store, "other.json");
        File.WriteAllText(other, File.ReadAllText(Path.Combine(root, "requests", "user_1001.json")).Replace("\"home_feed\"", "\"other_feed\"", StringComparison.Ordinal));

        var (code, _, errors) = await Run(other, manifest: id, store: store);
        Assert.Equal(2, code);
        Assert.Contains("REQUEST_INVALID", errors);
        Assert.Contains("The request runs pipeline 'other_feed', and the manifest releases pipeline 'home_feed'.", errors);

        var schema = Path.Combine(store, "schema.json");
        File.WriteAllText(schema, File.ReadAllText(Path.Combine(root, "requests", "user_1001.json")).Replace("\"../schemas/request.schema.json\"", "1", StringComparison.Ordinal));
        (code, _, errors) = await Run(schema, manifest: id, store: store);
        Assert.Equal(2, code);
        Assert.Contains("write \"$schema\": \"../schemas/request.schema.json\"", errors);

        // The sample's own bindings name the snapshot relative to the workspace.
        var relative = ManifestStores.Workspace();
        var (relativeId, _) = await ManifestStores.BuildAsync(relative, store);
        (code, _, errors) = await Run(Path.Combine(root, "requests", "user_1001.json"), manifest: relativeId, store: store);
        Assert.Equal(1, code);
        Assert.Contains("author_snapshot", errors);
        Assert.Contains($"relative paths in a manifest's bindings resolve against the current directory, {Environment.CurrentDirectory}", errors);

        var fallback = ExperimentRegressionTests.FallbackWorkspace(brokenLatencyMs: 0);
        var bindings = Path.Combine(fallback, "bindings", "local.jsonc");
        File.WriteAllText(bindings, File.ReadAllText(bindings).Replace("\"fixtures/recall_popular.json\"",
            JsonSerializer.Serialize(Path.Combine(fallback, "fixtures", "recall_popular.json")), StringComparison.Ordinal));
        var primary = Path.Combine(store, "primary.json");
        Assert.Equal(0, await BuildCommand.RunAsync(Tool.Environment, fallback, "local", "primary", store, primary, 1, TextWriter.Null, CancellationToken.None));
        var failing = Path.Combine(store, "primary-request.json");
        File.WriteAllText(failing, """{ "pipeline_id": "primary", "request_id": "r", "deadline_ms": 30000 }""");
        (code, _, errors) = await Run(failing, manifest: primary, store: store);
        Assert.Equal(1, code);
        Assert.Contains("warning RUN_FALLBACK_NOT_LOADED", errors);
        Assert.Contains("Pipeline 'primary' falls back to 'backup', which a run on one manifest does not load", errors);
    }

    private static void AbsoluteSnapshot(string root)
    {
        var path = Path.Combine(root, "bindings", "local.jsonc");
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace("\"fixtures/author_snapshot\"", JsonSerializer.Serialize(Path.Combine(root, "fixtures", "author_snapshot")), StringComparison.Ordinal)
            .Replace("\"fixtures/item_country_snapshot\"", JsonSerializer.Serialize(Path.Combine(root, "fixtures", "item_country_snapshot")), StringComparison.Ordinal));
    }

    private static async Task<(int Code, string Output, string Errors)> Run(
        string request, string? config = null, string? manifest = null, string? store = null, string? record = null)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await RunCommand.RunAsync(Tool.Environment, config, "local", request, Array.Empty<string>(), false, output, CancellationToken.None,
            recordPath: record, errors: errors, manifest: manifest, store: store);
        return (code, output.ToString(), errors.ToString());
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

        var errors = new StringWriter();
        Assert.Equal(2, await CompareCommand.RunAsync(Tool.Environment, missing, root, "local", [store], Array.Empty<string>(), 10, "score", "json", TextWriter.Null, CancellationToken.None,
            errors: errors));
        Assert.Contains(ManifestStores.Errors(errors.ToString()),
            e => e is ("CONFIG_REF_UNKNOWN", "--baseline", "", { } hint) && hint.Contains("a workspace directory, a manifest content id"));
        errors = new StringWriter();
        Assert.Equal(2, await ReplayCommand.RunAsync(Tool.Environment, root, "local", [store], Array.Empty<string>(), "json", false, TextWriter.Null, CancellationToken.None, manifest: id,
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

        var unknown = "sha256:" + new string('0', 64);
        await Expect(1, "MANIFEST_UNKNOWN", "manifest:" + unknown, "check the id", manifest: unknown, store: store);

        await Expect(1, "MANIFEST_OTHER_PIPELINE", "manifest:" + id, "omit --pipeline", manifest: id, store: store, pipeline: "other_feed", pointer: "/pipeline_id");

        var notAManifest = Path.Combine(store, "not-a-manifest.json");
        File.WriteAllText(notAManifest, """{ "pipeline_id": "home_feed", "environment": "local", "artifacts": {} }""");
        await Expect(1, "MANIFEST_SCHEMA", notAManifest, "release manifest", manifest: notAManifest, store: store, pointer: "/artifacts");

        var beyond = Path.Combine(store, "beyond-2-53.json");
        File.WriteAllText(beyond, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), "\"seq\": \\d+", "\"seq\": 9007199254740993"));
        await Expect(1, "MANIFEST_NOT_CANONICAL", beyond, "write integers of at most 9007199254740991", manifest: beyond, store: store, pointer: "/seq");
        var publishErrors = new StringWriter();
        Assert.Equal(1, await StrataRecCommandLine.InvokeAsync(["publish", "-s", store, beyond], Tool.Environment, TextWriter.Null, publishErrors, TestContext.Current.CancellationToken));
        Assert.Contains($"error MANIFEST_NOT_CANONICAL {beyond}/seq", publishErrors.ToString());
        Assert.DoesNotContain("Unhandled", publishErrors.ToString());

        var manifest = ManifestStores.Read(file);
        File.WriteAllText(blobs.GetBlobPath(manifest.Artifacts.Pipeline), "{}");
        await Expect(1, "ARTIFACT_UNAVAILABLE", "manifest:" + id, "the stored blob is damaged", manifest: id, store: store, pointer: "/artifacts/pipeline");
        File.Delete(blobs.GetBlobPath(manifest.Artifacts.Pipeline));
        await Expect(1, "ARTIFACT_MISSING", "manifest:" + id, "stratarec build", manifest: id, store: store, pointer: "/artifacts/pipeline");

        File.WriteAllText(blobs.GetBlobPath(id), "{}");
        await Expect(1, "MANIFEST_UNREADABLE", "manifest:" + id, "the blob is damaged", manifest: id, store: store);
    }

    [Fact]
    public async Task AManifestThatNeedsWhatTheBinaryLacks_IsIncompatible_AsOnANode()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (_, file) = await ManifestStores.BuildAsync(root, store);
        var built = ManifestStores.Read(file);
        var needs = new ReleaseManifest
        {
            PipelineId = built.PipelineId,
            Environment = built.Environment,
            Seq = built.Seq,
            Requires = [.. built.Requires, "CategoryBoost@1"],
            Artifacts = built.Artifacts,
            Experiments = built.Experiments,
        };
        var id = await ManifestStores.PutAsync(store, needs);

        await Expect(1, "MANIFEST_INCOMPATIBLE", "manifest:" + id, "'stratarec operators' lists what a binary provides", manifest: id, store: store, pointer: "/requires");
        var errors = new StringWriter();
        Assert.Equal(1, await ExplainPlanCommand.RunAsync(Tool.Environment, null, "local", null, "text", Array.Empty<string>(), TextWriter.Null, CancellationToken.None,
            id, store, errors));
        Assert.Contains("The manifest requires a capability this binary does not provide: CategoryBoost@1.", errors.ToString());

        errors = new StringWriter();
        Assert.Equal(1, await ReplayCommand.RunAsync(Tool.Environment, null, "local", [store], Array.Empty<string>(), "json", false, TextWriter.Null, CancellationToken.None,
            manifest: id, store: store, errors: errors));
        Assert.Contains(ManifestStores.Errors(errors.ToString()), e => e is ("MANIFEST_INCOMPATIBLE", _, "/requires", _));
    }

    private static async Task Expect(
        int exitCode, string code, string artifact, string hint, string? config = null, string? manifest = null, string? store = null, string? pipeline = null, string pointer = "")
    {
        var errors = new StringWriter();
        Assert.Equal(exitCode, await ExplainPlanCommand.RunAsync(Tool.Environment, config, "local", pipeline, "json", Array.Empty<string>(), TextWriter.Null, CancellationToken.None,
            manifest, store, errors));
        var error = Assert.Single(ManifestStores.Errors(errors.ToString()));
        Assert.Equal((code, artifact, pointer), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains(hint, error.Hint);
    }
    [Fact]
    public async Task TheManifest_RequiresOnlyTheConnectorsOfTheSourcesThePlanReads()
    {
        var root = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "catalog/home_feed.jsonc", "\"item.clicks_7d\":",
                "\"item.ghost\": { \"type\": \"float32\", \"source\": \"ghost_store\", \"field\": \"ghost\" },\n    \"item.clicks_7d\":");
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"item_store\":", "\"ghost_store\": { \"connector\": \"not_installed@1\" },\n    \"item_store\":");
        }).Root;
        var store = ManifestStores.NewStore();
        var file = Path.Combine(store, "manifest.json");

        Assert.Equal(0, await BuildCommand.RunAsync(Tool.Environment, root, "local", "home_feed", store, file, 1, TextWriter.Null, CancellationToken.None));

        var requires = ManifestStores.Read(file).Requires;
        Assert.DoesNotContain("not_installed@1", requires);
        Assert.Contains("memory@1", requires); // item_store's: the plan reads its features
        var manifest = ManifestStores.Read(file);
        var diagnostics = new DiagnosticBag();
        var request = await ManifestBuilder.LoadAsync(manifest, ContentHash.Compute(ArtifactSerializer.ToNode(manifest)), new FileSystemArtifactStore(store), diagnostics,
            TestContext.Current.CancellationToken);
        Assert.True(request is not null, string.Join("\n", diagnostics.Items));
        await using var engine = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root));
        var load = await engine.LoadAsync(request, 1, TestContext.Current.CancellationToken);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
    }
}
