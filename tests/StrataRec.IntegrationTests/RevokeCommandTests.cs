using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.CommandLine;
using StrataRec.CommandLine.Commands;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;
using StrataRec.Features.Cache;
using StrataRec.Models;
using StrataRec.Pipeline.Planning;

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
        Assert.Equal(["MmrDiversifier@1"], firstManifest.Experiments["exp_mmr_diversity"].Requires); // only the experiment adds the node
        Assert.DoesNotContain("MmrDiversifier@1", firstManifest.Requires);

        var (code, output, errors) = await Revoke(store, "exp_mmr_diversity");
        Assert.True(code == 0, errors);
        var revocationId = await Pointer(store);
        var revocation = await ReadAsync(blobs, revocationId);
        Assert.Equal((2L, first), (revocation.Seq, revocation.Previous));
        Assert.Equal((firstManifest.Experiments["exp_mmr_diversity"].Artifact, 2L), (revocation.Revoked["exp_mmr_diversity"].Artifact, revocation.Revoked["exp_mmr_diversity"].Seq));
        Assert.Equal(["exp_freshness_calibration", "exp_quality_weight"], revocation.Experiments.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(firstManifest.Artifacts.Pipeline, revocation.Artifacts.Pipeline);
        Assert.Equal(firstManifest.Requires, revocation.Requires);
        Assert.DoesNotContain("MmrDiversifier@1", ManifestBuilder.RequiredCapabilities(revocation)); // a binary without it takes the revocation (design 10.4)
        Assert.Equal(
            $"home_feed (local): revoked exp_mmr_diversity\n  {first} (seq 1) -> {revocationId} (seq 2)\n" +
            $"pointer {Path.Combine(store, "pointers", "local", "home_feed.json")}\n",
            output.ReplaceLineEndings("\n"));
        Assert.Contains("'stratarec build' refuses to release a revoked experiment unchanged", errors);
        Assert.DoesNotContain("0 errors", errors);

        // A node serves it, ignoring the platform's arm of the revoked experiment; the manifest before it is older, so stale.
        await using var engine = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root));
        var loaded = await engine.LoadManifestAsync(revocation, revocationId, blobs, TestContext.Current.CancellationToken);
        Assert.True(loaded.Applied, loaded.Reason);
        var request = ReplayTests.Request("req-revoked", 1001, "home", null);
        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = request.PipelineId, RequestId = request.RequestId, Inputs = request.Inputs, Deadline = request.Deadline, TimeMs = request.TimeMs,
            Assignment = ["exp_mmr_diversity/mmr", "exp_quality_weight/strong"],
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(revocationId, result.ManifestId);
        Assert.Equal(["exp_quality_weight/strong"], result.Experiments.Assigned);
        Assert.Equal("exp_mmr_diversity/mmr", Assert.Single(result.Experiments.Ignored).Id);
        Assert.Equal(ManifestLoadStatus.Stale, (await engine.LoadManifestAsync(firstManifest, first, blobs, TestContext.Current.CancellationToken)).Status);

        // Again: nothing changes, and it succeeds, so a stop can be retried.
        (code, output, errors) = await Revoke(store, "exp_mmr_diversity");
        Assert.True(code == 0, errors);
        Assert.Equal("home_feed (local): exp_mmr_diversity was revoked already (at seq 2)\n", output.ReplaceLineEndings("\n"));
        Assert.Equal(revocationId, await Pointer(store));

        // A second stop: one more revocation, which still lists the first experiment as revoked at seq 2.
        (code, output, errors) = await Revoke(store, "--format", "json", "exp_quality_weight", "exp_mmr_diversity");
        Assert.True(code == 0, errors);
        var second = await Pointer(store);
        Assert.Contains($"\"previous\": {{\n    \"manifest\": \"{revocationId}\",\n    \"seq\": 2\n  }}", output.ReplaceLineEndings("\n"));
        Assert.Contains("\"revoked\": [\n    \"exp_quality_weight\"\n  ]", output.ReplaceLineEndings("\n"));
        Assert.Contains("\"experiment\": \"exp_mmr_diversity\",\n      \"seq\": 2", output.ReplaceLineEndings("\n"));
        var secondManifest = await ReadAsync(blobs, second);
        Assert.Equal(3L, secondManifest.Seq);
        Assert.Equal([("exp_mmr_diversity", 2L), ("exp_quality_weight", 3L)],
            secondManifest.Revoked.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => (r.Key, r.Value.Seq)));
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
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["revoke", "-s", store, "exp_mmr_diversity"], Tool.Environment, TextWriter.Null, TextWriter.Null,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["revoke", "-s", store, "-p", "home_feed"], Tool.Environment, TextWriter.Null, TextWriter.Null, TestContext.Current.CancellationToken));
        var usage = new StringWriter();
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["revoke", "-s", store, "-p", "home_feed", "-e", "../x", "exp_mmr_diversity"], Tool.Environment,
            TextWriter.Null, usage, TestContext.Current.CancellationToken));
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
                        "revoked at seq 2, and no release since brought it back; this build releases it again as it was.", errors);
        Assert.Contains("hint: remove it here until it is fixed (a changed experiment is released), or pass --restore exp_mmr_diversity to release it as it is",
            errors);

        (code, errors) = await Build(root, store, "--restore", "exp_mmr_diversity");
        Assert.True(code == 0, errors);

        // The owner's fix: a changed experiment is released.
        var experiment = Path.Combine(root, "experiments", "exp_mmr_diversity.jsonc");
        File.WriteAllText(experiment, File.ReadAllText(experiment).Replace("\"mmr_lambda\": 0.7", "\"mmr_lambda\": 0.6", StringComparison.Ordinal));
        (code, errors) = await Build(root, store);
        Assert.True(code == 0, errors);

        File.WriteAllText(experiment, File.ReadAllText(experiment).Replace("\"mmr_lambda\": 0.6", "\"mmr_lambda\": 0.7", StringComparison.Ordinal));
        var blobs = new FileSystemArtifactStore(store);

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
        Assert.Equal(3L, (await ReadAsync(blobs, ordinary)).Seq);
        Assert.Empty((await ReadAsync(blobs, ordinary)).Revoked);
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
        await pointers.WritePointerAsync("home_feed", "prod", first, TestContext.Current.CancellationToken); // another environment, and one pointer that cannot be read
        await pointers.WritePointerAsync("other_feed", "prod", first, TestContext.Current.CancellationToken);

        (code, output, errors) = await Status(store);
        Assert.Equal(1, code);
        Assert.Contains("error MANIFEST_OTHER_PIPELINE", errors);
        Assert.Contains($"error POINTER_MANIFEST_FOREIGN {pointers.GetPointerPath("other_feed", "prod")}", errors);
        Assert.Contains("error MANIFEST_OTHER_ENVIRONMENT", errors); // home_feed's prod pointer names the local manifest
        Assert.Equal(
            $"home_feed (local): {revocation}\n" +
            "  seq          2\n" +
            $"  previous     {first}\n" +
            "  experiments  exp_freshness_calibration (layer fusion), exp_quality_weight (layer params)\n" +
            "  revoked      exp_mmr_diversity (by seq 2): 'stratarec build' keeps them out until they change\n" +
            $"  pointer      {pointers.GetPointerPath("home_feed", "local")}\n",
            output.ReplaceLineEndings("\n"));

        (code, output, errors) = await Status(store, "-e", "local", "-f", "json");
        Assert.True(code == 0, errors);
        using var document = System.Text.Json.JsonDocument.Parse(output);
        var published = Assert.Single(document.RootElement.GetProperty("published").EnumerateArray());
        var revoked = Assert.Single(published.GetProperty("revoked").EnumerateArray());
        Assert.Equal(("exp_mmr_diversity", 2L), (revoked.GetProperty("experiment").GetString(), revoked.GetProperty("seq").GetInt64()));
        Assert.Equal("rerank", (await ReadAsync(new FileSystemArtifactStore(store), first)).Experiments["exp_mmr_diversity"].Layer);

        (code, _, errors) = await Status(Path.Combine(store, "missing"));
        Assert.Equal(2, code);
        Assert.Contains("error STORE_NOT_FOUND --store", errors);
    }

    /// <summary>
    /// A pointer that cannot be read, however it cannot, is one error, and what the other pointers publish is still shown
    /// (review 40: an undecodable string, a file name no pointer can have and an environment that cannot be listed each
    /// ended the command with nothing shown); a filter skips the environments it does not ask for.
    /// </summary>
    [Fact]
    public async Task Status_ShowsTheOtherPointers_WhateverIsWrongWithOne()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        await BuildAndPublishAsync(root, store);
        var local = Path.Combine(store, "pointers", "local");
        File.WriteAllText(Path.Combine(local, "a_bad.json"), "{\"manifest\":\"\\uD800\"}");
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(local, "a\\bad.json"), "{}"); // a name Linux allows and a pointer cannot have
        }

        var (code, output, errors) = await Status(store, "-f", "json");
        Assert.Equal(1, code);
        Assert.Contains("is not valid: its 'manifest' is not a valid string", errors);
        Assert.DoesNotContain("Unhandled", errors);
        using (var document = System.Text.Json.JsonDocument.Parse(output))
        {
            Assert.Equal("home_feed", Assert.Single(document.RootElement.GetProperty("published").EnumerateArray()).GetProperty("pipeline_id").GetString());
        }

        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        Assert.Contains("cannot name a pointer file", errors);
        var locked = Directory.CreateDirectory(Path.Combine(store, "pointers", "z_locked")).FullName;
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            (code, output, errors) = await Status(store, "-e", "local");
            Assert.Equal(1, code); // a_bad still
            Assert.DoesNotContain("z_locked", errors); // not asked for
            Assert.Contains("home_feed (local)", output);

            (code, output, errors) = await Status(store);
            Assert.Contains($"error POINTER_UNREADABLE {locked}", errors);
            Assert.Contains("home_feed (local)", output);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Pointers that cannot be traversed are not "nothing published", with -e either (review 42: exit 0, nothing shown).
        var all = Path.Combine(store, "pointers");
        File.SetUnixFileMode(all, UnixFileMode.None);
        try
        {
            (code, output, errors) = await Status(store, "-e", "local", "-p", "home_feed");
            Assert.Equal(1, code);
            Assert.Contains("error POINTER_UNREADABLE", errors);
            Assert.DoesNotContain("nothing is published", output);
        }
        finally
        {
            File.SetUnixFileMode(all, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task<(int Code, string Output, string Errors)> Status(string store, params string[] options)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await StrataRecCommandLine.InvokeAsync(["status", "-s", store, .. options], Tool.Environment, output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    /// <summary>
    /// Each experiment records what its compile needed beyond the baseline, and a revocation drops exactly that, so a binary
    /// without those types refuses the release and takes the revocation (design 10.4). Inferred from the names the
    /// configuration wrote, a revocation kept the connector and store types of feature sources only the revoked experiment
    /// read (review 68), and guessed at models an operator uses by its config's default (reviews 39, 40, 42).
    /// </summary>
    [Fact]
    public async Task EachExperiment_RequiresWhatItsCompileUsed_AndARevocationDropsExactlyThat()
    {
        var root = ManifestStores.Workspace(root =>
        {
            // A model of another client type, and a source behind a distributed cache store: only exp_future uses them.
            PlanFeatureClosureTests.EditJson(root, "bindings/local.jsonc", bindings =>
            {
                bindings["models"]!["random_model"] = JsonNode.Parse("""{ "client": "random@1", "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click"] }""");
                bindings["sources"]!["extra_store"] = JsonNode.Parse("""
                    { "connector": "memory@1", "data": { }, "mode": "cache", "max_bytes": "1MiB",
                      "cache": { "ttl_ms": 1000, "distributed": { "store": "redis@1", "endpoint": "r:6379" } } }
                    """);
            });
            ReplayTests.Edit(root, "catalog/home_feed.jsonc", "\"item.clicks_7d\":",
                "\"item.extra\": { \"type\": \"float32\", \"source\": \"extra_store\", \"field\": \"extra\", \"default\": 1.0 },\n    \"item.clicks_7d\":");
            File.WriteAllText(Path.Combine(root, "experiments", "exp_future.jsonc"), """
                { "experiment_id": "exp_future", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {}, "on": { "nodes": {
                    "ctr_ranker": { "config": { "model": "random_model" } },
                    "fusion": { "config": { "multipliers": { "extra": "col.item.extra" } } } } } } }
                """);
            ReplayTests.Edit(root, "releases/home_feed.local.jsonc", "\"exp_quality_weight\":",
                "\"exp_future\": { \"layer\": \"future\" },\n    \"exp_quality_weight\":");
        });
        var store = ManifestStores.NewStore();
        var blobs = new FileSystemArtifactStore(store);
        var firstId = await BuildAndPublishAsync(root, store);
        var first = await ReadAsync(blobs, firstId);

        Assert.Equal(["random@1", "redis@1"], first.Experiments["exp_future"].Requires);
        Assert.Equal(["MmrDiversifier@1"], first.Experiments["exp_mmr_diversity"].Requires);
        Assert.Empty(first.Experiments["exp_freshness_calibration"].Requires); // its ExpressionScorer@1 is the baseline's too
        Assert.Empty(first.Experiments["exp_quality_weight"].Requires);
        Assert.Contains("linear@1", first.Requires); // the baseline's model
        Assert.Contains("snapshot_files@1", first.Requires); // the baseline's local table
        Assert.DoesNotContain("random@1", first.Requires);
        Assert.DoesNotContain("redis@1", first.Requires);

        // A binary without those types refuses the release, and serves it without the experiment.
        var older = Tool.Environment with { Models = new ModelClientRegistry().Add(new LinearModelClientFactory()), CacheStores = new FeatureCacheStoreRegistry() };
        await using var engine = new RecEngine(CommonOptions.ForRoot(older, root));
        var refused = await engine.LoadManifestAsync(first, firstId, blobs, TestContext.Current.CancellationToken);
        Assert.Equal(ManifestLoadStatus.Incompatible, refused.Status);
        Assert.Equal(["random@1", "redis@1"], refused.MissingCapabilities);

        var (code, _, errors) = await Revoke(store, "exp_future");
        Assert.True(code == 0, errors);
        var revocationId = await Pointer(store);
        var revocation = await ReadAsync(blobs, revocationId);
        Assert.Equal(first.Requires, revocation.Requires);
        Assert.DoesNotContain("exp_future", revocation.Experiments.Keys);
        var loaded = await engine.LoadManifestAsync(revocation, revocationId, blobs, TestContext.Current.CancellationToken);
        Assert.True(loaded.Applied, loaded.Reason);

        // A second stop keeps what the first dropped dropped.
        (code, _, errors) = await Revoke(store, "exp_mmr_diversity");
        Assert.True(code == 0, errors);
        var second = await ReadAsync(blobs, await Pointer(store));
        Assert.Equal(first.Requires, ManifestBuilder.RequiredCapabilities(second));
    }

    /// <summary>
    /// What the published manifest releases is not revoked, whatever it lists as revoked: a controller brought it back
    /// (review 39: build refused the configuration that was published), and a later stop no longer lists it.
    /// </summary>
    [Fact]
    public async Task Build_TakesWhatThePublishedManifestBroughtBack()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var blobs = new FileSystemArtifactStore(store);
        var first = await BuildAndPublishAsync(root, store);
        var firstManifest = await ReadAsync(blobs, first);
        Assert.Equal(0, (await Revoke(store, "exp_mmr_diversity")).Code);
        var revocation = await ReadAsync(blobs, await Pointer(store));

        // Another controller publishes the first release's configuration again, keeping the revocation's list.
        var back = Path.Combine(store, "back.json");
        File.WriteAllText(back, ArtifactSerializer.ToNode(new ReleaseManifest
        {
            PipelineId = firstManifest.PipelineId, Environment = firstManifest.Environment, Seq = 3, Previous = await Pointer(store),
            Requires = firstManifest.Requires, Artifacts = firstManifest.Artifacts, Experiments = firstManifest.Experiments, Revoked = revocation.Revoked,
        }).ToJsonString());
        Assert.Equal(0, await StrataRecCommandLine.InvokeAsync(["publish", "-s", store, back], Tool.Environment, TextWriter.Null, TextWriter.Null, TestContext.Current.CancellationToken));
        var (code, errors) = await Build(root, store);
        Assert.True(code == 0, errors);
        Assert.Contains(": unchanged, the release the pointer names has this configuration", errors);

        // A stop of another experiment lists only that one.
        Assert.Equal(0, (await Revoke(store, "exp_quality_weight")).Code);
        Assert.Equal(["exp_quality_weight"], (await ReadAsync(blobs, await Pointer(store))).Revoked.Keys);
        (code, errors) = await Build(root, store);
        Assert.Equal(1, code);
        Assert.Contains("error EXPERIMENT_REVOKED /experiments/exp_quality_weight", errors);
        Assert.DoesNotContain("error EXPERIMENT_REVOKED /experiments/exp_mmr_diversity", errors);
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
