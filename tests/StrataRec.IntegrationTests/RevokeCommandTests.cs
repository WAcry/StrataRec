using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.CommandLine;
using StrataRec.CommandLine.Commands;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>stratarec revoke</c>, the emergency stop of design 10.6, on builds of the home_feed sample: what is published, at
/// once, without the experiment, as a revocation nodes serve and that refuses every earlier manifest; <c>build</c>
/// keeping the stopped experiment out of the releases that follow; and <c>status</c> showing the stops in force.
/// </summary>
public sealed class RevokeCommandTests
{
    [Fact]
    public async Task Revoke_PublishesTheReleaseWithoutTheExperiment_AsARevocation_ThatANodeServes()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var first = await BuildAndPublishAsync(root, store);
        var blobs = new FileSystemArtifactStore(store);
        var firstManifest = await ReadAsync(blobs, first);
        Assert.Contains("MmrDiversifier@1", firstManifest.Requires); // only the experiment adds the node

        var (code, output, errors) = await Revoke(store, "exp_mmr_diversity");
        Assert.True(code == 0, errors);
        var revocationId = await Pointer(store);
        var revocation = await ReadAsync(blobs, revocationId);
        Assert.Equal((2L, 2L, first), (revocation.Seq, revocation.MinAcceptedSeq, revocation.Previous));
        Assert.Equal(["exp_freshness_calibration", "exp_quality_weight"], revocation.Experiments.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(firstManifest.Artifacts.Pipeline, revocation.Artifacts.Pipeline);
        Assert.DoesNotContain("MmrDiversifier@1", revocation.Requires); // a binary without it takes the revocation (design 10.4)
        Assert.Equal(
            $"home_feed (local): revoked exp_mmr_diversity\n  {first} (seq 1) -> {revocationId} (seq 2, min_accepted_seq 2)\n" +
            $"pointer {Path.Combine(store, "pointers", "local", "home_feed.json")}\n",
            output.ReplaceLineEndings("\n"));
        Assert.Contains("'stratarec build' refuses to release a revoked experiment unchanged", errors);
        Assert.DoesNotContain("0 errors", errors);

        // A node serves it, ignoring the platform's arm of the revoked experiment, and refuses the manifest before it.
        await using var engine = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root));
        var loaded = await engine.LoadManifestAsync(revocation, revocationId, blobs);
        Assert.True(loaded.Applied, loaded.Reason);
        var request = ReplayTests.Request("req-revoked", 1001, "home", null);
        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = request.PipelineId, RequestId = request.RequestId, Inputs = request.Inputs, Deadline = request.Deadline, TimeMs = request.TimeMs,
            Assignment = ["exp_mmr_diversity/mmr", "exp_quality_weight/strong"],
        });
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(revocationId, result.ManifestId);
        Assert.Equal(["exp_quality_weight/strong"], result.Experiments.Assigned);
        Assert.Equal("exp_mmr_diversity/mmr", Assert.Single(result.Experiments.Ignored).Id);
        Assert.Equal(ManifestLoadStatus.Revoked, (await engine.LoadManifestAsync(firstManifest, first, blobs)).Status);

        // Again: nothing changes, and it succeeds, so a stop can be retried.
        (code, output, errors) = await Revoke(store, "exp_mmr_diversity");
        Assert.True(code == 0, errors);
        Assert.Equal($"home_feed (local): exp_mmr_diversity was revoked already, by {revocationId} (seq 2)\n", output.ReplaceLineEndings("\n"));
        Assert.Equal(revocationId, await Pointer(store));

        // A second stop: one more revocation; the first experiment is still known as revoked, by the first.
        (code, output, errors) = await Revoke(store, "--format", "json", "exp_quality_weight", "exp_mmr_diversity");
        Assert.True(code == 0, errors);
        var second = await Pointer(store);
        Assert.Contains($"\"previous\": {{\n    \"manifest\": \"{revocationId}\",\n    \"seq\": 2\n  }}", output.ReplaceLineEndings("\n"));
        Assert.Contains("\"revoked\": [\n    \"exp_quality_weight\"\n  ]", output.ReplaceLineEndings("\n"));
        Assert.Contains($"\"experiment\": \"exp_mmr_diversity\",\n      \"manifest\": \"{revocationId}\",\n      \"seq\": 2", output.ReplaceLineEndings("\n"));
        Assert.Equal((3L, 3L), ((await ReadAsync(blobs, second)).Seq, (await ReadAsync(blobs, second)).MinAcceptedSeq));
    }

    [Fact]
    public async Task Revoke_SaysWhatItCannotDo()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();

        var (code, _, errors) = await Revoke(store, "exp_mmr_diversity");
        Assert.Equal(1, code);
        Assert.Contains("error NOTHING_PUBLISHED", errors);
        Assert.Contains("hint: check --store, --pipeline and --env; 'stratarec publish' writes the pointer", errors);
        Assert.False(Directory.Exists(Path.Combine(store, "pointers")), "nothing is written");

        var first = await BuildAndPublishAsync(root, store);
        (code, _, errors) = await Revoke(store, "exp_mmr_diversty", "exp_quality_weight");
        Assert.Equal(1, code);
        Assert.Contains($"error EXPERIMENT_NOT_RELEASED manifest:{first}/experiments: Manifest '{first}' (seq 1), which pipeline 'home_feed' in environment " +
                        "'local' publishes, does not release experiment 'exp_mmr_diversty'.", errors);
        Assert.Contains("hint: did you mean 'exp_mmr_diversity'?", errors);
        Assert.Equal(first, await Pointer(store)); // nothing revoked, not even the experiment that is released

        (code, _, errors) = await Revoke(store, "exp_none");
        Assert.Contains("hint: it releases exp_freshness_calibration, exp_mmr_diversity, exp_quality_weight", errors);

        // Usage errors.
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["revoke", "-s", store, "exp_mmr_diversity"], Tool.Environment, TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["revoke", "-s", store, "-p", "home_feed"], Tool.Environment, TextWriter.Null, TextWriter.Null));
        var usage = new StringWriter();
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["revoke", "-s", store, "-p", "home_feed", "-e", "../x", "exp_mmr_diversity"], Tool.Environment,
            TextWriter.Null, usage));
        Assert.Contains("error POINTER_NAME_INVALID --env", usage.ToString());
    }

    /// <summary>
    /// While a revocation is published, build refuses to release what it revoked unchanged, at the release file's entry, so
    /// a routine release does not undo the stop; a changed experiment (the owner's fix) and <c>--restore</c> release it, and
    /// an ordinary release ends the check: what it left out was left out on purpose.
    /// </summary>
    [Fact]
    public async Task Build_KeepsARevokedExperimentOut_UntilItChangesOrIsRestored()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var first = await BuildAndPublishAsync(root, store);
        Assert.Equal(0, (await Revoke(store, "exp_mmr_diversity")).Code);
        var revocation = await Pointer(store);

        var (code, errors) = await Build(root, store);
        Assert.Equal(1, code);
        Assert.Contains("releases/home_feed.local.jsonc:11:5: error EXPERIMENT_REVOKED /experiments/exp_mmr_diversity: Experiment 'exp_mmr_diversity' was " +
                        $"revoked by manifest '{revocation}' (seq 2), and no release since brought it back; this build releases it again as it was.", errors);
        Assert.Contains("hint: remove it here until it is fixed (a changed experiment is released), or pass --restore exp_mmr_diversity to release it as it is",
            errors);

        (code, errors) = await Build(root, store, "--restore", "exp_mmr_diversity");
        Assert.True(code == 0, errors);

        // The owner's fix: a changed experiment is released.
        var experiment = Path.Combine(root, "experiments", "exp_mmr_diversity.jsonc");
        File.WriteAllText(experiment, File.ReadAllText(experiment).Replace("\"mmr_lambda\": 0.7", "\"mmr_lambda\": 0.6", StringComparison.Ordinal));
        (code, errors) = await Build(root, store);
        Assert.True(code == 0, errors);

        // A revocation whose history cannot be read keeps nothing out, and says so.
        var blobs = new FileSystemArtifactStore(store);
        var firstBlob = blobs.GetBlobPath(first);
        var saved = File.ReadAllBytes(firstBlob);
        File.Delete(firstBlob);
        File.WriteAllText(experiment, File.ReadAllText(experiment).Replace("\"mmr_lambda\": 0.6", "\"mmr_lambda\": 0.7", StringComparison.Ordinal));
        (code, errors) = await Build(root, store);
        Assert.True(code == 0, errors);
        Assert.Contains($"warning REVOCATIONS_UNKNOWN manifest:{revocation}/previous", errors);
        File.WriteAllBytes(firstBlob, saved);

        // Left out of the release file, the configuration is the revocation's: build says so, and the stop stays.
        var releaseFile = Path.Combine(root, "releases", "home_feed.local.jsonc");
        var withIt = File.ReadAllText(releaseFile);
        File.WriteAllText(releaseFile, withIt.Replace("\"exp_mmr_diversity\":         { \"layer\": \"rerank\" },", string.Empty, StringComparison.Ordinal));
        (code, errors) = await Build(root, store);
        Assert.True(code == 0, errors);
        Assert.Contains(": unchanged, the release the pointer names has this configuration", errors);

        // An ordinary release after it ends the check: what it left out was left out on purpose, and adding it back releases it.
        var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
        File.WriteAllText(pipeline, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(pipeline), "\"final_top_k\": \\d+,", "\"final_top_k\": 7,"));
        var ordinary = await BuildAndPublishAsync(root, store);
        Assert.Equal((3L, 2L), ((await ReadAsync(blobs, ordinary)).Seq, (await ReadAsync(blobs, ordinary)).MinAcceptedSeq));
        File.WriteAllText(releaseFile, withIt);
        (code, errors) = await Build(root, store);
        Assert.True(code == 0, errors);
    }

    /// <summary>
    /// <c>status</c> shows what nodes will load from the store, and the stops in force; a pointer that cannot be read is
    /// reported, and the other pointers are still listed.
    /// </summary>
    [Fact]
    public async Task Status_ShowsWhatTheStorePublishes_AndTheStopsInForce()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (code, output, errors) = await Status(store);
        Assert.True(code == 0, errors);
        Assert.Equal($"nothing is published in {store}\n", output.ReplaceLineEndings("\n"));

        var first = await BuildAndPublishAsync(root, store);
        Assert.Equal(0, (await Revoke(store, "exp_mmr_diversity")).Code);
        var revocation = await Pointer(store);
        var pointers = new FileSystemManifestSource(store);
        await pointers.WritePointerAsync("home_feed", "prod", first); // another environment, and one pointer that cannot be read
        await pointers.WritePointerAsync("other_feed", "prod", first);

        (code, output, errors) = await Status(store);
        Assert.Equal(1, code);
        Assert.Contains("error MANIFEST_OTHER_PIPELINE", errors);
        Assert.Contains($"error POINTER_MANIFEST_FOREIGN {pointers.GetPointerPath("other_feed", "prod")}", errors);
        Assert.Contains("error MANIFEST_OTHER_ENVIRONMENT", errors); // home_feed's prod pointer names the local manifest
        Assert.Equal(
            $"home_feed (local): {revocation}\n" +
            "  seq          2, min_accepted_seq 2: a revocation, nodes refuse every earlier manifest\n" +
            $"  previous     {first}\n" +
            "  experiments  exp_freshness_calibration (layer fusion), exp_quality_weight (layer params)\n" +
            "  revoked      exp_mmr_diversity (by seq 2): 'stratarec build' keeps them out until they change\n" +
            $"  pointer      {pointers.GetPointerPath("home_feed", "local")}\n",
            output.ReplaceLineEndings("\n"));

        (code, output, errors) = await Status(store, "-e", "local", "-f", "json");
        Assert.True(code == 0, errors);
        using var document = System.Text.Json.JsonDocument.Parse(output);
        var published = Assert.Single(document.RootElement.GetProperty("published").EnumerateArray());
        Assert.True(published.GetProperty("revocation").GetBoolean());
        var revoked = Assert.Single(published.GetProperty("revoked").EnumerateArray());
        Assert.Equal(("exp_mmr_diversity", revocation), (revoked.GetProperty("experiment").GetString(), revoked.GetProperty("manifest").GetString()));
        Assert.Equal("rerank", (await ReadAsync(new FileSystemArtifactStore(store), first)).Experiments["exp_mmr_diversity"].Layer);

        (code, _, errors) = await Status(Path.Combine(store, "missing"));
        Assert.Equal(2, code);
        Assert.Contains("error STORE_NOT_FOUND --store", errors);
    }

    private static async Task<(int Code, string Output, string Errors)> Status(string store, params string[] options)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await StrataRecCommandLine.InvokeAsync(["status", "-s", store, .. options], Tool.Environment, output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    /// <summary>
    /// A model or candidate source binding is required of the binary only while the pipeline or a released experiment names
    /// it: the revocation of the one experiment that used a new client type is a manifest a binary without the type takes
    /// (design 10.4; review 39: every type the bindings listed stayed required, and the rolled-back binary refused it).
    /// </summary>
    [Fact]
    public async Task Revoke_RequiresOnlyTheClientsWhatIsLeftNames()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var blobs = new FileSystemArtifactStore(store);
        var first = await ReadAsync(blobs, await BuildAndPublishAsync(root, store));

        // The bindings gain a model of a new client type, and one arm of exp_quality_weight names it.
        var bindings = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(blobs.GetBlobPath(first.Artifacts.Bindings)))!;
        ((System.Text.Json.Nodes.JsonObject)bindings["models"]!)["future_model"] = new System.Text.Json.Nodes.JsonObject { ["client"] = "future_client@1" };
        var experiment = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            await File.ReadAllTextAsync(blobs.GetBlobPath(first.Experiments["exp_quality_weight"].Artifact)))!;
        var arm = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonObject)experiment["arms"]!)["strong"]!;
        arm["params"] ??= new System.Text.Json.Nodes.JsonObject();
        ((System.Text.Json.Nodes.JsonObject)arm["params"]!)["ranker_model"] = "future_model";
        var experiments = new Dictionary<string, ManifestExperiment>(first.Experiments)
        {
            ["exp_quality_weight"] = new() { Artifact = await blobs.PutAsync(experiment), Layer = first.Experiments["exp_quality_weight"].Layer },
        };
        var manifest = new ReleaseManifest
        {
            PipelineId = first.PipelineId, Environment = first.Environment, Seq = 2, MinAcceptedSeq = 0, Previous = await Pointer(store),
            Requires = [.. first.Requires, "future_client@1"],
            Artifacts = new ManifestArtifacts
            {
                Pipeline = first.Artifacts.Pipeline, Catalog = first.Artifacts.Catalog, Bindings = await blobs.PutAsync(bindings), Tables = first.Artifacts.Tables,
                Policy = first.Artifacts.Policy,
            },
            Experiments = experiments,
        };
        var id = await ManifestStores.PutAsync(store, manifest);

        var diagnostics = new DiagnosticBag();
        var withoutQuality = await ManifestBuilder.RevokeAsync(manifest, id, ["exp_quality_weight"], blobs, diagnostics);
        var withoutMmr = await ManifestBuilder.RevokeAsync(manifest, id, ["exp_mmr_diversity"], blobs, diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        Assert.DoesNotContain("future_client@1", withoutQuality!.Requires);
        Assert.Contains("linear@1", withoutQuality.Requires); // the baseline's model still is
        Assert.Contains("future_client@1", withoutMmr!.Requires);

        // A binary without the type takes the revocation.
        await using var engine = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root));
        var revocationId = await ManifestStores.PutAsync(store, withoutQuality);
        var loaded = await engine.LoadManifestAsync(withoutQuality, revocationId, blobs);
        Assert.True(loaded.Applied, loaded.Reason);
    }

    /// <summary>
    /// What the published manifest releases is not revoked, whatever revocation before it removed it: a controller brought it
    /// back (review 39: build refused the configuration that was published). A part of the history that cannot be read
    /// keeps out what the part that was read revoked, and says the rest is not known.
    /// </summary>
    [Fact]
    public async Task Build_TakesWhatThePublishedManifestBroughtBack_AndKeepsOutWhatItKnowsRevoked()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var blobs = new FileSystemArtifactStore(store);
        var first = await BuildAndPublishAsync(root, store);
        var firstManifest = await ReadAsync(blobs, first);
        Assert.Equal(0, (await Revoke(store, "exp_mmr_diversity")).Code);
        var revocation = await Pointer(store);

        // Another controller publishes the first release's configuration again, as a revocation of its own.
        var back = Path.Combine(store, "back.json");
        File.WriteAllText(back, ArtifactSerializer.ToNode(new ReleaseManifest
        {
            PipelineId = firstManifest.PipelineId, Environment = firstManifest.Environment, Seq = 3, MinAcceptedSeq = 3, Previous = revocation,
            Requires = firstManifest.Requires, Artifacts = firstManifest.Artifacts, Experiments = firstManifest.Experiments,
        }).ToJsonString());
        Assert.Equal(0, await StrataRecCommandLine.InvokeAsync(["publish", "-s", store, back], Tool.Environment, TextWriter.Null, TextWriter.Null));
        var (code, errors) = await Build(root, store);
        Assert.True(code == 0, errors);
        Assert.Contains(": unchanged, the release the pointer names has this configuration", errors);

        // Two stops, and the manifest before both cannot be read: the second's experiment is still kept out.
        Assert.Equal(0, (await Revoke(store, "exp_mmr_diversity")).Code);
        Assert.Equal(0, (await Revoke(store, "exp_quality_weight")).Code);
        var before = await Pointer(store);
        var beforeBoth = (await ReadAsync(blobs, (await ReadAsync(blobs, before)).Previous!)).Previous!;
        File.Delete(blobs.GetBlobPath(beforeBoth));
        (code, errors) = await Build(root, store);
        Assert.Equal(1, code);
        Assert.Contains($"warning REVOCATIONS_UNKNOWN manifest:{before}/previous: The published manifest revokes earlier ones, and manifest '{beforeBoth}' " +
                        "in its history cannot be read", errors);
        Assert.Contains("error EXPERIMENT_REVOKED /experiments/exp_quality_weight", errors);
        Assert.DoesNotContain("error EXPERIMENT_REVOKED /experiments/exp_mmr_diversity", errors); // its stop is in the part not read
    }

    /// <summary>
    /// A stop takes the pointer's lock as publish does, and names an experiment once however often it is given; a blob the
    /// process may not read is a diagnostic at the manifest's pointer (review 39: an unhandled exception).
    /// </summary>
    [Fact]
    public async Task Revoke_TakesThePointersTurn_NamesEachExperimentOnce_AndSaysWhatItCannotRead()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var first = await BuildAndPublishAsync(root, store);
        var pointers = new FileSystemManifestSource(store);
        var lockPath = Path.Combine(store, "pointers", "local", ".home_feed.json.lock");
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var (busy, _, busyErrors) = await Revoke(store, "exp_mmr_diversity");
            Assert.Equal(1, busy);
            Assert.Contains("error PUBLISH_BUSY", busyErrors);
            Assert.Equal(first, await Pointer(store));
        }

        var (code, output, errors) = await Revoke(store, "exp_mmr_diversity", "exp_mmr_diversity");
        Assert.True(code == 0, errors);
        Assert.StartsWith("home_feed (local): revoked exp_mmr_diversity\n", output.ReplaceLineEndings("\n"));

        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // permissions do not bind the test's process
        }

        var blobs = new FileSystemArtifactStore(store);
        var pipelineBlob = blobs.GetBlobPath((await ReadAsync(blobs, await Pointer(store))).Artifacts.Pipeline);
        File.SetUnixFileMode(pipelineBlob, UnixFileMode.None);
        try
        {
            (code, _, errors) = await Revoke(store, "exp_quality_weight");
            Assert.Equal(1, code);
            Assert.Contains("error ARTIFACT_UNAVAILABLE", errors);
            Assert.Contains("hint: this process may not read the store's blob: check its permissions", errors);
            Assert.DoesNotContain("Unhandled", errors);
        }
        finally
        {
            File.SetUnixFileMode(pipelineBlob, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<string> BuildAndPublishAsync(string root, string store)
    {
        var file = Path.Combine(store, $"manifest-{Guid.NewGuid():N}.json");
        var errors = new StringWriter();
        Assert.True(0 == await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, file, null, TextWriter.Null, CancellationToken.None, errors),
            errors.ToString());
        Assert.Equal(0, await StrataRecCommandLine.InvokeAsync(["publish", "-s", store, file], Tool.Environment, TextWriter.Null, TextWriter.Null));
        return await Pointer(store);
    }

    private static async Task<(int Code, string Errors)> Build(string root, string store, params string[] options)
    {
        var errors = new StringWriter();
        var code = await StrataRecCommandLine.InvokeAsync(["build", "-c", root, "-s", store, "-m", Path.Combine(store, $"m-{Guid.NewGuid():N}.json"), .. options],
            Tool.Environment, TextWriter.Null, errors);
        return (code, errors.ToString());
    }

    private static async Task<(int Code, string Output, string Errors)> Revoke(string store, params string[] arguments)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await StrataRecCommandLine.InvokeAsync(["revoke", "-s", store, "-p", "home_feed", .. arguments], Tool.Environment, output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    private static async Task<string> Pointer(string store) =>
        (await new FileSystemManifestSource(store).GetManifestIdAsync("home_feed", "local", CancellationToken.None))!;

    private static async Task<ReleaseManifest> ReadAsync(IArtifactStore store, string id)
    {
        var diagnostics = new DiagnosticBag();
        return await ManifestBuilder.ReadAsync(store, id, diagnostics) ?? throw new InvalidOperationException(string.Join("\n", diagnostics.Items));
    }
}
