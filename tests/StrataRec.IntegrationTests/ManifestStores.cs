using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

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
        foreach (var section in new[] { "sources", "services" })
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

    public static async Task<(string Id, string File)> BuildAsync(string root, string store)
    {
        var file = Path.Combine(store, $"manifest-{Guid.NewGuid():N}.json");
        Assert.Equal(0, await BuildCommand.RunAsync(Tool.Environment, root, "local", null, store, file, 1, TextWriter.Null, CancellationToken.None));
        var id = ContentHash.Compute(ArtifactSerializer.ToNode(Read(file)));
        Assert.True(File.Exists(new FileSystemArtifactStore(store).GetBlobPath(id)), "build stores the manifest under the id of the manifest it writes");
        return (id, file);
    }

    public static ReleaseManifest Read(string file) => ArtifactSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(file), file);

    public static async Task<string> PutAsync(string store, ReleaseManifest manifest) =>
        await new FileSystemArtifactStore(store).PutAsync(ArtifactSerializer.ToNode(manifest));

    public static ReleaseManifest WithPrevious(ReleaseManifest manifest, string? previous) => new()
    {
        PipelineId = manifest.PipelineId,
        Environment = manifest.Environment,
        Seq = manifest.Seq + 1,
        Previous = previous,
        Requires = manifest.Requires,
        Artifacts = manifest.Artifacts,
        Experiments = manifest.Experiments,
        Revoked = manifest.Revoked,
    };

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
