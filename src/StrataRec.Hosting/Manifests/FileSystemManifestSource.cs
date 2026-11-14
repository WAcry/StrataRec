using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Json;

namespace StrataRec.Hosting.Manifests;

/// <summary>
/// Manifest pointers as files in a directory, next to the blobs of a <see cref="Artifacts.Storage.FileSystemArtifactStore"/>
/// with the same root (design 10.1): <c>&lt;root&gt;/pointers/&lt;environment&gt;/&lt;pipeline_id&gt;.json</c>, holding
/// <c>{ "manifest": "sha256:…" }</c>. <c>stratarec publish</c> writes them (<see cref="WritePointerAsync"/>); a node reads
/// them (<see cref="GetManifestIdAsync"/>). The format is read and written here only.
/// </summary>
/// <remarks>
/// A pointer is strict JSON: an object whose <c>manifest</c> is a content id. Other members are ignored, so a release
/// controller may record more (who published, when) without breaking nodes that do not know them. A pointer is replaced
/// atomically (a temporary file in the same directory, renamed over it), so a reader sees the old pointer or the new one.
/// </remarks>
public sealed class FileSystemManifestSource : IManifestSource
{
    private const string ManifestMember = "manifest";

    /// <exception cref="ArgumentException"><paramref name="root"/> is empty or whitespace.</exception>
    public FileSystemManifestSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The absolute root directory; pointers are under <c>pointers/</c>.</summary>
    public string Root { get; }

    /// <summary>
    /// The pointer file of a pipeline in an environment, whether or not it exists. Both names become path segments, so
    /// each must be a plain file name.
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty, <c>.</c> or <c>..</c>, or holds a path separator or a character
    /// a file name cannot have.</exception>
    public string GetPointerPath(string pipelineId, string environment) =>
        Path.Combine(Root, "pointers", Segment(environment, nameof(environment)), Segment(pipelineId, nameof(pipelineId)) + ".json");

    /// <summary>
    /// The (pipeline, environment) of every pointer file under the root, sorted by environment, then pipeline (ordinal).
    /// What is listed is a file name a pointer can have; whether its content is a valid pointer is
    /// <see cref="GetManifestIdAsync"/>'s to say. Lock and temporary files, and anything else whose name starts with a dot,
    /// are skipped.
    /// </summary>
    public IReadOnlyList<(string PipelineId, string Environment)> ListPointers()
    {
        var pointers = new List<(string PipelineId, string Environment)>();
        var directory = Path.Combine(Root, "pointers");
        if (!Directory.Exists(directory))
        {
            return pointers;
        }

        foreach (var environmentDirectory in Directory.EnumerateDirectories(directory))
        {
            var environment = Path.GetFileName(environmentDirectory);
            if (environment.StartsWith('.'))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(environmentDirectory, "*.json"))
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith('.'))
                {
                    pointers.Add((name[..^".json".Length], environment));
                }
            }
        }

        pointers.Sort(static (a, b) =>
        {
            var byEnvironment = string.CompareOrdinal(a.Environment, b.Environment);
            return byEnvironment != 0 ? byEnvironment : string.CompareOrdinal(a.PipelineId, b.PipelineId);
        });
        return pointers;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The pointer file is not a valid pointer; the message names the file.</exception>
    public async ValueTask<string?> GetManifestIdAsync(string pipelineId, string environment, CancellationToken cancellationToken)
    {
        var path = GetPointerPath(pipelineId, environment);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return Parse(bytes, path);
    }

    /// <summary>
    /// Points the pipeline in the environment at <paramref name="manifestId"/>, replacing the pointer atomically. Checks
    /// nothing about the manifest: <c>stratarec publish</c> does, before it calls this.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="manifestId"/> is not a content id, or a name is not a plain file
    /// name.</exception>
    public async Task WritePointerAsync(string pipelineId, string environment, string manifestId, CancellationToken cancellationToken = default)
    {
        if (!ContentHash.IsValid(manifestId))
        {
            throw new ArgumentException($"'{manifestId}' is not a content id (sha256:<64 lowercase hex digits>).", nameof(manifestId));
        }

        var path = GetPointerPath(pipelineId, environment);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes($"{{ \"{ManifestMember}\": \"{manifestId}\" }}\n");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary); // nothing there once moved
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover dot-prefixed temporary file is harmless.
            }
        }
    }

    private static string Parse(byte[] bytes, string path)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(bytes); // strict: no comments, no trailing commas
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(path, $"it is a JSON {document.RootElement.ValueKind.ToString().ToLowerInvariant()}, not an object");
            }

            if (!document.RootElement.TryGetProperty(ManifestMember, out var manifest) || manifest.ValueKind != JsonValueKind.String)
            {
                throw Invalid(path, $"it has no string member '{ManifestMember}'");
            }

            id = manifest.GetString();
        }
        catch (JsonException ex)
        {
            throw Invalid(path, ex.Message, ex);
        }

        return ContentHash.IsValid(id) ? id : throw Invalid(path, $"'{id}' is not a manifest content id (sha256:<64 lowercase hex digits>)");
    }

    private static InvalidDataException Invalid(string path, string reason, Exception? inner = null) =>
        new($"The manifest pointer {path} is not valid: {reason}. A pointer is {{ \"{ManifestMember}\": \"sha256:<hex>\" }}, as 'stratarec publish' writes it.", inner);

    private static string Segment(string name, string parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, parameter);
        if (name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{name}' cannot name a pointer file: it must be a plain file name, without path separators.", parameter);
        }

        return name;
    }
}
