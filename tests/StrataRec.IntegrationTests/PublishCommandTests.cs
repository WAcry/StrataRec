using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.CommandLine;
using StrataRec.CommandLine.Commands;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>stratarec publish</c> on builds of the home_feed sample: the pointer a node reads, moved only forward and only to a
/// manifest whose artifacts a node can read; and a node loading what the pointer names.
/// </summary>
public sealed class PublishCommandTests
{
    [Fact]
    public async Task Publish_PointsTheManifestsPipelineAndEnvironmentAtIt_AndANodeServesIt()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);
        var blobs = new FileSystemArtifactStore(store);
        File.Delete(blobs.GetBlobPath(id)); // published by file: stored again first

        var (code, output, errors) = await Publish(store, file);

        Assert.True(code == 0, errors);
        var pointer = Path.Combine(store, "pointers", "local", "home_feed.json");
        Assert.Equal($"home_feed (local): (none) -> {id} (seq 1)\npointer {pointer}\n", output.ReplaceLineEndings("\n"));
        Assert.True(File.Exists(blobs.GetBlobPath(id)));

        // A node: the pointer, the manifest, its plan.
        var named = await new FileSystemManifestSource(store).GetManifestIdAsync("home_feed", "local", CancellationToken.None);
        Assert.Equal(id, named);
        var diagnostics = new DiagnosticBag();
        var manifest = await ManifestBuilder.ReadAsync(blobs, named!, diagnostics, TestContext.Current.CancellationToken);
        Assert.NotNull(manifest);
        await using var engine = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root));
        var loaded = await engine.LoadManifestAsync(manifest!, id, blobs, TestContext.Current.CancellationToken);
        Assert.True(loaded.Applied, loaded.Reason);
        var result = await engine.ExecuteAsync(ReplayTests.Request("req-published", 1001, "home", Array.Empty<string>()), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(id, result.ManifestId);
        Assert.NotEmpty(result.Items);
    }

    [Fact]
    public async Task Publish_MovesThePointerOnlyToAGreaterSeq()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (first, _) = await ManifestStores.BuildAsync(root, store);
        Assert.Equal(0, (await Publish(store, first)).Code);

        // The same manifest again: published already, nothing written, so a pipeline step can run twice.
        var pointer = Path.Combine(store, "pointers", "local", "home_feed.json");
        var written = File.GetLastWriteTimeUtc(pointer);
        var (code, again, errors) = await Publish(store, first, "json");
        Assert.True(code == 0, errors);
        using (var unchanged = JsonDocument.Parse(again))
        {
            Assert.False(unchanged.RootElement.GetProperty("changed").GetBoolean());
        }

        Assert.Equal(written, File.GetLastWriteTimeUtc(pointer));

        // Another manifest at the same sequence: not newer.
        var sameSeq = await BuildAsync(root, store, seq: 1, finalTopK: 7);
        (code, _, errors) = await Publish(store, sameSeq, "json");
        Assert.Equal(1, code);
        var error = Assert.Single(ManifestStores.Errors(errors));
        Assert.Equal(("PUBLISH_NOT_NEWER", "manifest:" + sameSeq, "/seq"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("--seq 2", error.Hint);
        Assert.Contains($"Sequence 1 is not greater than 1, the sequence of '{first}', which the pointer", errors);

        // Seq 2: published, from 1 to 2.
        var second = await BuildAsync(root, store, seq: 2, finalTopK: 8);
        (code, var output, errors) = await Publish(store, second, "json");
        Assert.True(code == 0, errors);
        using var report = JsonDocument.Parse(output);
        Assert.Equal((first, 1, second, 2),
            (report.RootElement.GetProperty("previous").GetProperty("manifest").GetString(), report.RootElement.GetProperty("previous").GetProperty("seq").GetInt32(),
             report.RootElement.GetProperty("manifest").GetString(), report.RootElement.GetProperty("seq").GetInt32()));
        Assert.Equal(second, await new FileSystemManifestSource(store).GetManifestIdAsync("home_feed", "local", CancellationToken.None));
    }

    [Fact]
    public async Task Publish_RefusesAManifestWhoseArtifactsANodeCannotRead()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);
        var manifest = ManifestStores.Read(file);
        File.Delete(new FileSystemArtifactStore(store).GetBlobPath(manifest.Experiments["exp_mmr_diversity"].Artifact));

        var (code, _, errors) = await Publish(store, id, "json");

        Assert.Equal(1, code);
        var error = Assert.Single(ManifestStores.Errors(errors));
        Assert.Equal(("ARTIFACT_MISSING", "manifest:" + id, "/experiments/exp_mmr_diversity/artifact"), (error.Code, error.Artifact, error.Pointer));
        Assert.False(Directory.Exists(Path.Combine(store, "pointers")));
    }

    [Fact]
    public async Task Publish_OfSomethingThatIsNoManifest_IsAUsageError()
    {
        var store = ManifestStores.NewStore();

        var (code, _, errors) = await Publish(store, Path.Combine(store, "no-such.json"), "json");
        Assert.Equal(2, code);
        Assert.Equal("CONFIG_REF_UNKNOWN", Assert.Single(ManifestStores.Errors(errors)).Code);

        (code, _, errors) = await Publish(store, "sha256:" + new string('0', 64), "json");
        Assert.Equal(1, code);
        Assert.Equal("MANIFEST_UNKNOWN", Assert.Single(ManifestStores.Errors(errors)).Code);
    }

    /// <summary>
    /// Without --seq, build makes the next release of what the store's pointer names (design 10.2): one more seq, and it as
    /// previous, which diff compares with; the same configuration is no new release, so build and publish run again change
    /// nothing, and a revocation published stays published, still listing what it revoked. A changed configuration is a
    /// release that lists no revocations. A pointer build cannot read stops it, unless --seq says the number.
    /// </summary>
    [Fact]
    public async Task Build_WithoutSeq_IsTheNextReleaseOfWhatIsPublished()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (first, firstManifest) = await BuildNext(root, store);
        Assert.Equal((1L, (string?)null), (firstManifest.Seq, firstManifest.Previous));

        // Published as a revocation of an experiment the release file no longer lists would be.
        var revocationManifest = new ReleaseManifest
        {
            PipelineId = firstManifest.PipelineId, Environment = firstManifest.Environment, Seq = 3, Previous = first,
            Requires = firstManifest.Requires, Artifacts = firstManifest.Artifacts, Experiments = firstManifest.Experiments,
            Revoked = new() { ["exp_gone"] = new RevokedExperiment { Artifact = "sha256:" + new string('0', 64), Seq = 3 } },
        };
        var revocationId = await ManifestStores.PutAsync(store, revocationManifest);
        Assert.Equal(0, (await Publish(store, revocationId)).Code);

        // The same configuration: the published manifest, and publishing it changes nothing.
        var errors = new StringWriter();
        var file = Path.Combine(store, "again.json");
        Assert.Equal(0, await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, file, null, TextWriter.Null, CancellationToken.None, errors));
        Assert.Equal(revocationId, ManifestBuilder.ContentId(ManifestStores.Read(file)));
        Assert.Contains("(seq 3, ", errors.ToString());
        Assert.Contains(": unchanged, the release the pointer names has this configuration", errors.ToString());
        Assert.DoesNotContain("publish it with", errors.ToString()); // it is
        Assert.Contains("is published already", (await Publish(store, file)).Output);

        // Changed: seq 4, the published manifest as previous (diff --to compares with it), no revocations.
        var path = Path.Combine(root, "pipelines", "home_feed.jsonc");
        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), "\"final_top_k\": \\d+,", "\"final_top_k\": 7,"));
        var (next, nextManifest) = await BuildNext(root, store);
        Assert.Equal((4L, revocationId), (nextManifest.Seq, nextManifest.Previous));
        Assert.Empty(nextManifest.Revoked);
        Assert.Equal(0, (await Publish(store, next)).Code);
        var diff = new StringWriter();
        Assert.Equal(1, await StrataRecCommandLine.InvokeAsync(["diff", "--to", next, "-s", store], Tool.Environment, diff, TextWriter.Null, TestContext.Current.CancellationToken));
        Assert.Contains("final_top_k", diff.ToString());

        // A pointer that cannot be read: the next number is unknown; --seq says it.
        File.WriteAllText(Path.Combine(store, "pointers", "local", "home_feed.json"), "{ not json");
        errors = new StringWriter();
        Assert.Equal(1, await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, null, null, TextWriter.Null, CancellationToken.None, errors));
        Assert.Contains("error POINTER_UNREADABLE", errors.ToString());
        Assert.Contains("hint: fix the pointer ('stratarec publish' writes it), or pass --seq", errors.ToString());
        Assert.Equal(0, await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, null, 9, TextWriter.Null, CancellationToken.None, TextWriter.Null));
    }

    /// <summary>
    /// A pointer names a manifest of its own pipeline and environment; one that names another's (copied or edited by hand)
    /// says nothing of this pipeline's sequence, so build without --seq and publish refuse it rather than number or compare
    /// by it. Sequence numbers end at 2^53 - 1, the largest integer a manifest's JSON writes exactly (review 37).
    /// </summary>
    [Fact]
    public async Task Build_AndPublish_RefuseAPointerToAnotherPipelinesManifest_AndSeqsEndAt2To53()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (_, firstManifest) = await BuildNext(root, store);
        var pointers = new FileSystemManifestSource(store);

        // home_feed's local pointer names a prod manifest of seq 50.
        var foreign = await ManifestStores.PutAsync(store, Renumbered(firstManifest, "prod", 50));
        await pointers.WritePointerAsync("home_feed", "local", foreign, TestContext.Current.CancellationToken);
        var errors = new StringWriter();
        Assert.Equal(1, await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, null, null, TextWriter.Null, CancellationToken.None, errors));
        Assert.Contains("error MANIFEST_OTHER_ENVIRONMENT", errors.ToString());
        Assert.Contains("error POINTER_MANIFEST_FOREIGN", errors.ToString());
        Assert.Contains("hint: fix the pointer, or pass --seq", errors.ToString());

        // A release of seq 2 is not compared with seq 50 of another environment, nor published over it.
        var second = await BuildAsync(root, store, 2, 7);
        var (code, _, publishErrors) = await Publish(store, second);
        Assert.Equal(1, code);
        Assert.Contains("error POINTER_MANIFEST_FOREIGN", publishErrors);
        Assert.Contains("hint: fix the pointer, or delete the pointer to publish without comparing with the manifest it names", publishErrors);
        Assert.Equal(foreign, await pointers.GetManifestIdAsync("home_feed", "local", CancellationToken.None));
        File.Delete(pointers.GetPointerPath("home_feed", "local"));
        Assert.Equal(0, (await Publish(store, second)).Code);

        // Published at 2^53 - 1: there is no next.
        Assert.Equal(0, (await Publish(store, await ManifestStores.PutAsync(store, Renumbered(firstManifest, "local", BuildCommand.MaxSeq)))).Code);
        errors = new StringWriter();
        Assert.Equal(1, await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, null, null, TextWriter.Null, CancellationToken.None, errors));
        Assert.Contains("error SEQ_EXHAUSTED", errors.ToString());

        // --seq takes 1 to 2^53 - 1; what is no long at all is the parser's usage error, not a crash (review 38).
        foreach (var seq in new[] { "0", "9007199254740992" })
        {
            var usage = new StringWriter();
            Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["build", "-c", root, "-s", store, "--seq", seq], Tool.Environment, TextWriter.Null, usage, TestContext.Current.CancellationToken));
            Assert.Contains("--seq must be from 1 to 9007199254740991", usage.ToString());
        }

        foreach (var arguments in new[] { new[] { "--seq", "abc" }, ["--seq", "9223372036854775808"], ["--seq=abc"] })
        {
            var usage = new StringWriter();
            Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["build", "-c", root, "-s", store, .. arguments], Tool.Environment, TextWriter.Null, usage, TestContext.Current.CancellationToken));
            Assert.Contains("Cannot parse argument", usage.ToString());
        }
    }

    /// <summary>
    /// An environment named with a leading '.' is refused alike by build, publish and status, with the hint: build and
    /// publish took it and wrote <c>pointers/.preview/home_feed.json</c>, which listing pointers skips as a hidden or
    /// temporary entry, so <c>status -e .preview</c> listed nothing and exited 0.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentStartingWithADot_IsRefusedByBuildPublishAndStatus()
    {
        var root = ManifestStores.Workspace(r => File.WriteAllText(Path.Combine(r, "bindings", ".preview.jsonc"),
            File.ReadAllText(Path.Combine(r, "bindings", "local.jsonc")).Replace("\"environment\":\"local\"", "\"environment\":\".preview\"", StringComparison.Ordinal)));
        var store = ManifestStores.NewStore();
        const string Hint = "do not start with '.'";

        var errors = new StringWriter();
        Assert.Equal(1, await BuildCommand.RunAsync(Tool.Environment, root, ".preview", null, store, null, null, TextWriter.Null, CancellationToken.None, errors));
        Assert.Contains("error POINTER_UNREADABLE", errors.ToString());
        Assert.Contains("'.preview' cannot name a pointer file: it starts with '.'", errors.ToString());
        Assert.Contains(Hint, errors.ToString());
        Assert.Contains("hint: pass --seq to build without reading the pointer; pipeline ids and environment names", errors.ToString());

        // With --seq the store's pointers are not read, and build writes the manifest; publishing it is refused.
        var file = Path.Combine(store, "preview.json");
        errors = new StringWriter();
        Assert.True(0 == await BuildCommand.RunAsync(Tool.Environment, root, ".preview", null, store, file, 1, TextWriter.Null, CancellationToken.None, errors), errors.ToString());
        var (code, _, publishErrors) = await Publish(store, file);
        Assert.Equal(1, code);
        Assert.Contains("error MANIFEST_POINTER_NAME", publishErrors);
        Assert.Contains(Hint, publishErrors);
        Assert.False(Directory.Exists(Path.Combine(store, "pointers", ".preview")));

        var status = new StringWriter();
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(["status", "-s", store, "-e", ".preview"], Tool.Environment, TextWriter.Null, status,
            TestContext.Current.CancellationToken));
        Assert.Contains("error POINTER_NAME_INVALID --env", status.ToString());
        Assert.Contains(Hint, status.ToString());

        // Review 79: earlier versions published under such names; the hint says how to move such a release. Review 81: to an
        // unused name, with the old release file. Review 83: leaving out what the old release revoked, read from the files
        // that hold it, since no version's status lists a pointer under such a name.
        Assert.Contains("to move a release an earlier version published under such a name, choose a new environment name nothing uses yet, " +
            "copy releases/<pipeline>.<old name>.jsonc to releases/<pipeline>.<new name>.jsonc", status.ToString());
        Assert.Contains("leave out of it each experiment the old release revoked (in the store, pointers/<old name>/<pipeline>.json names the manifest " +
            "sha256:<hex>, blobs/sha256/<hex> holds it, and those experiments are under its \"revoked\" and not under its \"experiments\"), " +
            "then build and publish it and point the hosts at the new name", status.ToString());
        Assert.DoesNotContain("'stratarec status'", status.ToString());
    }

    private static ReleaseManifest Renumbered(ReleaseManifest manifest, string environment, long seq) => new()
    {
        PipelineId = manifest.PipelineId, Environment = environment, Seq = seq, Previous = manifest.Previous,
        Requires = manifest.Requires, Artifacts = manifest.Artifacts, Experiments = manifest.Experiments,
    };

    private static async Task<(string Id, ReleaseManifest Manifest)> BuildNext(string root, string store)
    {
        var file = Path.Combine(store, $"manifest-{Guid.NewGuid():N}.json");
        var errors = new StringWriter();
        Assert.True(0 == await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, file, null, TextWriter.Null, CancellationToken.None, errors),
            errors.ToString());
        var manifest = ManifestStores.Read(file);
        var id = ManifestBuilder.ContentId(manifest);
        Assert.EndsWith($"publish it with: stratarec publish -s {InitCommand.Quote(store)} {id}", errors.ToString().TrimEnd());
        Assert.Equal(0, (await Publish(store, id)).Code);
        return (id, manifest);
    }

    /// <summary>Builds the workspace copy at <paramref name="root"/> with its <c>final_top_k</c> set, so the manifest differs; its id.</summary>
    private static async Task<string> BuildAsync(string root, string store, long seq, int finalTopK)
    {
        var path = Path.Combine(root, "pipelines", "home_feed.jsonc");
        var text = File.ReadAllText(path);
        var edited = System.Text.RegularExpressions.Regex.Replace(text, "\"final_top_k\": \\d+,", $"\"final_top_k\": {finalTopK},");
        Assert.NotEqual(text, edited);
        File.WriteAllText(path, edited);
        var file = Path.Combine(store, $"manifest-{Guid.NewGuid():N}.json");
        var errors = new StringWriter();
        Assert.True(0 == await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, file, seq, TextWriter.Null, CancellationToken.None, errors),
            errors.ToString());
        return ManifestBuilder.ContentId(ManifestStores.Read(file));
    }

    private static async Task<(int Code, string Output, string Errors)> Publish(string store, string manifest, string format = "text")
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await StrataRecCommandLine.InvokeAsync(["publish", "-s", store, manifest, "--format", format], Tool.Environment, output, errors);
        return (code, output.ToString(), errors.ToString());
    }
}
