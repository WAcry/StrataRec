using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Json;
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
        // The format is checked before the structure: another version's layout would otherwise be reported as a schema
        // error of this one, although the file is valid in the version it names.
        if (DeclaredFormat(json) is { } declared && declared != ReplayBundle.CurrentFormat)
        {
            throw UnsupportedFormat(declared);
        }

        ReplayBundle bundle;
        try
        {
            bundle = ArtifactSerializer.Deserialize<ReplayBundle>(json, sourceName);
        }
        catch (ArtifactFormatException ex)
        {
            throw new ReplayBundleException(ex.Pointer, ex.Reason,
                ex.Hint ?? "run 'stratarec schema replay_bundle' for the JSON Schema of a bundle");
        }

        if (bundle.Format != ReplayBundle.CurrentFormat)
        {
            throw UnsupportedFormat(bundle.Format);
        }

        return bundle;
    }

    private static ReplayBundleException UnsupportedFormat(string format) =>
        new("/format", $"unsupported format '{format}'", $"this build reads {ReplayBundle.CurrentFormat}; record the request again with this build");

    /// <summary>The top-level <c>format</c> string, or null when the document has none or cannot be read that far.</summary>
    private static string? DeclaredFormat(string json)
    {
        try
        {
            // A text can keep its file's byte order mark (U+FEFF, EF BB BF here), which the full parse skips too.
            ReadOnlySpan<byte> utf8 = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(utf8.StartsWith("\uFEFF"u8) ? utf8[3..] : utf8, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isFormat = reader.ValueTextEquals("format");
                reader.Read();
                if (isFormat)
                {
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }

                reader.Skip();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Malformed JSON, or a format string that is not valid Unicode: the full parse reports it with its position.
        }

        return null;
    }

    /// <exception cref="ReplayBundleException">The bundle is malformed or of another format.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ReplayBundle Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Bundle files of several paths (<see cref="Find(string)"/> of each, in the order given).</summary>
    public static List<string> Find(IReadOnlyList<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            files.AddRange(Find(path));
        }

        return files;
    }

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
