using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Hosting.Replay;

/// <summary>Reads and writes replay bundles (design 13.4) as JSON files.</summary>
public static class ReplayBundleFile
{
    /// <summary>Serializes a bundle with the artifact serializer (snake_case, nulls omitted).</summary>
    public static string Serialize(ReplayBundle bundle) => JsonSerializer.Serialize(bundle, ArtifactSerializer.Options);

    public static void Save(ReplayBundle bundle, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(bundle), new UTF8Encoding(false));
    }

    /// <summary>Parses a bundle; a document that does not match the schema or format is reported with its JSON pointer.</summary>
    /// <exception cref="ReplayBundleException">The bundle is malformed or of another format.</exception>
    public static ReplayBundle Parse(string json, string? sourceName = null)
    {
        ReplayBundle bundle;
        try
        {
            bundle = ArtifactSerializer.Deserialize<ReplayBundle>(json, sourceName);
        }
        catch (ArtifactFormatException ex)
        {
            throw new ReplayBundleException(ArtifactSerializer.JsonPathToPointer(ex.JsonPath), ex.InnerException?.Message ?? ex.Message,
                "run 'stratarec schema replay_bundle' for the JSON Schema of a bundle");
        }

        if (bundle.Format != ReplayBundle.CurrentFormat)
        {
            throw new ReplayBundleException("/format", $"unsupported format '{bundle.Format}'",
                $"this build reads {ReplayBundle.CurrentFormat}; record the request again with this build");
        }

        return bundle;
    }

    /// <exception cref="ReplayBundleException">The bundle is malformed or of another format.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ReplayBundle Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Bundle files of a path: the file itself, or every <c>*.json</c> file of a directory in ordinal order.</summary>
    public static List<string> Find(string path)
    {
        var files = new List<string>();
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.json"))
            {
                files.Add(file);
            }

            files.Sort(StringComparer.Ordinal);
        }
        else
        {
            files.Add(path);
        }

        return files;
    }
}
