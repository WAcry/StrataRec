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
        var manifest = await ManifestBuilder.ReadAsync(blobs, named!, diagnostics);
        Assert.NotNull(manifest);
        await using var engine = new RecEngine(CommonOptions.ForRoot(Tool.Environment, root));
        var loaded = await engine.LoadManifestAsync(manifest!, id, blobs);
        Assert.True(loaded.Applied, loaded.Reason);
        var result = await engine.ExecuteAsync(ReplayTests.Request("req-published", 1001, "home", Array.Empty<string>()));
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

        // The same manifest again, and another one at the same sequence: not newer.
        var (code, _, errors) = await Publish(store, first, "json");
        Assert.Equal(1, code);
        var error = Assert.Single(ManifestStores.Errors(errors));
        Assert.Equal(("PUBLISH_NOT_NEWER", "manifest:" + first, "/seq"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("--seq 2", error.Hint);

        var sameSeq = await BuildAsync(root, store, seq: 1, finalTopK: 7);
        (code, _, errors) = await Publish(store, sameSeq, "json");
        Assert.Equal(1, code);
        Assert.Equal("PUBLISH_NOT_NEWER", Assert.Single(ManifestStores.Errors(errors)).Code);
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
