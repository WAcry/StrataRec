using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

/// <summary>Copies of the home_feed sample built into artifact stores, as <c>stratarec build --store</c> builds them.</summary>
internal static class ManifestStores
{
    /// <summary>
    /// A copy of the sample, edited by <paramref name="edit"/>, whose bindings name their fixture files absolutely:
    /// <c>memory@1</c> and <c>memory_list@1</c> read their file when created, also by a compile that serves nothing, and a
    /// manifest has no workspace root for relative paths to resolve against (they resolve against the current directory).
    /// </summary>
    public static string Workspace(Action<string>? edit = null) => HomeFeedTestHost.LoadModified(root =>
    {
        var path = Path.Combine(root, "bindings", "local.jsonc");
        var document = (JsonObject)Jsonc.Parse(File.ReadAllBytes(path), path)!;
        foreach (var section in new[] { "sources", "candidate_sources" })
        {
            foreach (var (_, binding) in (JsonObject)document[section]!)
            {
                if (binding?["data_file"] is JsonValue file)
                {
                    binding["data_file"] = Path.Combine(root, file.GetValue<string>());
                }
            }
        }

        File.WriteAllText(path, document.ToJsonString());
        edit?.Invoke(root);
    }).Root;

    public static string NewStore() => Directory.CreateTempSubdirectory("stratarec-store-").FullName;

    /// <summary>Builds <paramref name="root"/> into <paramref name="store"/>: the manifest's content id, and the manifest file build wrote.</summary>
    public static async Task<(string Id, string File)> BuildAsync(string root, string store)
    {
        var file = Path.Combine(store, $"manifest-{Guid.NewGuid():N}.json");
        Assert.Equal(0, await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, file, 1, TextWriter.Null, CancellationToken.None));
        var id = ContentHash.Compute(ArtifactSerializer.ToNode(Read(file)));
        Assert.True(File.Exists(new FileSystemArtifactStore(store).GetBlobPath(id)), "build stores the manifest under the id of the manifest it writes");
        return (id, file);
    }

    public static ReleaseManifest Read(string file) => ArtifactSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(file), file);

    /// <summary>Stores a manifest (as a release controller would) and returns its content id.</summary>
    public static async Task<string> PutAsync(string store, ReleaseManifest manifest) =>
        await new FileSystemArtifactStore(store).PutAsync(ArtifactSerializer.ToNode(manifest));

    /// <summary>The manifest with <c>previous</c> set.</summary>
    public static ReleaseManifest WithPrevious(ReleaseManifest manifest, string? previous) => new()
    {
        PipelineId = manifest.PipelineId,
        Environment = manifest.Environment,
        Seq = manifest.Seq + 1,
        MinAcceptedSeq = manifest.MinAcceptedSeq,
        Previous = previous,
        Requires = manifest.Requires,
        Artifacts = manifest.Artifacts,
        Experiments = manifest.Experiments,
    };

    /// <summary>The errors of a <c>--format json</c> diagnostics report: code, artifact, pointer and hint.</summary>
    public static List<(string Code, string Artifact, string Pointer, string? Hint)> Errors(string json)
    {
        using var report = JsonDocument.Parse(json);
        return report.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() == "error")
            .Select(d => (d.GetProperty("code").GetString()!, d.GetProperty("artifact").GetString()!, d.GetProperty("pointer").GetString()!,
                d.TryGetProperty("hint", out var hint) ? hint.GetString() : null))
            .ToList();
    }
}
