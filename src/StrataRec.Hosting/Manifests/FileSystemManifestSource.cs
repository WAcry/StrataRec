using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Json;

namespace StrataRec.Hosting.Manifests;

/// <summary>
/// Manifest pointers as files in a directory, next to the blobs of a
/// <see cref="Artifacts.Storage.FileSystemArtifactStore"/> with the same root:
/// <c>&lt;root&gt;/pointers/&lt;environment&gt;/&lt;pipeline_id&gt;.json</c>, holding <c>{ "manifest": "sha256:…"
/// }</c>. <c>stratarec publish</c> writes them (<see cref="WritePointerAsync"/>); a node reads them
/// (<see cref="GetManifestIdAsync"/>).
/// </summary>
/// <remarks>
/// A pointer is strict JSON: an object whose <c>manifest</c> is a content id. Other members are ignored. A pointer is
/// replaced atomically, so a reader sees the old pointer or the new one.
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
    /// How to name a pipeline or an environment whose pointer this source keeps (<see cref="GetPointerPath"/>).
    /// </summary>
    public const string NameHint =
        "pipeline ids and environment names are the file names of pointers: use letters, digits, '_', '-' and '.', and do not start with '.'; " +
        "to move a release an earlier version published under such a name, choose a new environment name nothing uses yet, " +
        "copy releases/<pipeline>.<old name>.jsonc to releases/<pipeline>.<new name>.jsonc with its \"environment\" set to the new name " +
        "and bindings for that environment, leave out of it each experiment the old release revoked (in the store, " +
        "pointers/<old name>/<pipeline>.json names the manifest sha256:<hex>, blobs/sha256/<hex> holds it, and those experiments are " +
        "under its \"revoked\" and not under its \"experiments\"), then build and publish it and point the hosts at the new name";

    /// <summary>
    /// The pointer file of a pipeline in an environment, whether or not it exists. Both names become path segments, so
    /// each must be a plain file name, and one that does not start with '.' (<see cref="ListPointers"/> skips those).
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty, starts with '.', or holds a path separator or a character a
    /// file name cannot have.</exception>
    public string GetPointerPath(string pipelineId, string environment) =>
        Path.Combine(Root, "pointers", Segment(environment, nameof(environment)), Segment(pipelineId, nameof(pipelineId)) + ".json");

    /// <summary>
    /// The pointer files under the root, of <paramref name="environment"/> or of every environment, sorted by
    /// environment, then pipeline (ordinal). Whether a file's content is a valid pointer is
    /// <see cref="GetManifestIdAsync"/>'s to say; what cannot be listed is listed with its
    /// <see cref="PointerFile.Error"/>, so one bad entry does not hide the others: a directory that cannot be read, a
    /// file name no pointer can have. Lock and temporary files, and anything else whose name starts with a dot, are
    /// skipped.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="environment"/> is not a name a pointer can have.</exception>
    public IReadOnlyList<PointerFile> ListPointers(string? environment = null)
    {
        var pointers = new List<PointerFile>();
        var directory = Path.Combine(Root, "pointers");
        var environments = new List<string>();
        if (environment is not null)
        {
            environments.Add(Path.Combine(directory, Segment(environment, nameof(environment))));
        }
        else
        {
            // Enumerated, not asked whether it exists: a directory that cannot be traversed does not exist to Exists, and
            // would read as nothing published.
            try
            {
                environments.AddRange(Directory.EnumerateDirectories(directory));
            }
            catch (DirectoryNotFoundException)
            {
                if (NotADirectory(directory) is { } file)
                {
                    pointers.Add(new PointerFile(string.Empty, string.Empty, directory, file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pointers.Add(new PointerFile(string.Empty, string.Empty, directory, ex.Message));
            }
        }

        foreach (var environmentDirectory in environments)
        {
            var name = Path.GetFileName(environmentDirectory);
            if (name.StartsWith('.'))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(environmentDirectory, "*.json"))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.StartsWith('.'))
                    {
                        continue;
                    }

                    var pipeline = fileName[..^".json".Length];
                    pointers.Add(new PointerFile(pipeline, name, file, PointerName(pipeline) ?? PointerName(name)));
                }
            }
            catch (DirectoryNotFoundException)
            {
                if (NotADirectory(environmentDirectory) is { } file)
                {
                    pointers.Add(new PointerFile(string.Empty, name, environmentDirectory, file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pointers.Add(new PointerFile(string.Empty, name, environmentDirectory, ex.Message));
            }
        }

        pointers.Sort(static (a, b) =>
        {
            var byEnvironment = string.CompareOrdinal(a.Environment, b.Environment);
            return byEnvironment != 0 ? byEnvironment : string.CompareOrdinal(a.PipelineId, b.PipelineId);
        });
        return pointers;
    }

    /// <summary>
    /// The system says "not found" for both (ENOTDIR), and a store whose pointers cannot be listed is not one that
    /// publishes nothing.
    /// </summary>
    private string? NotADirectory(string directory)
    {
        for (var path = directory; path.Length >= Root.Length; path = Path.GetDirectoryName(path) ?? string.Empty)
        {
            if (File.Exists(path))
            {
                return $"'{path}' is a file where a directory of pointers belongs";
            }
        }

        return null;
    }

    private static string? PointerName(string name)
    {
        try
        {
            Segment(name, nameof(name));
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
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
                File.Delete(temporary);
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
            using var document = JsonDocument.Parse(bytes);
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
        catch (InvalidOperationException ex)
        {
            // A string JsonDocument accepts and cannot decode, as an unpaired surrogate escape.
            throw Invalid(path, $"its '{ManifestMember}' is not a valid string ({ex.Message})", ex);
        }

        return ContentHash.IsValid(id) ? id : throw Invalid(path, $"'{id}' is not a manifest content id (sha256:<64 lowercase hex digits>)");
    }

    private static InvalidDataException Invalid(string path, string reason, Exception? inner = null) =>
        new($"The manifest pointer {path} is not valid: {reason}. A pointer is {{ \"{ManifestMember}\": \"sha256:<hex>\" }}, as 'stratarec publish' writes it.", inner);

    private static string Segment(string name, string parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, parameter);
        if (name.StartsWith('.'))
        {
            throw new ArgumentException(
                $"'{name}' cannot name a pointer file: it starts with '.', which marks the temporary and hidden files that listing pointers skips.", parameter);
        }

        if (name.Contains('/') || name.Contains('\\') || name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{name}' cannot name a pointer file: it must be a plain file name, without path separators.", parameter);
        }

        return name;
    }
}

/// <summary>
/// A pointer file <see cref="FileSystemManifestSource.ListPointers"/> found: its pipeline and environment, and its path.
/// <see cref="Error"/> says why it cannot be read as a pointer of that pipeline: its directory could not be listed (the
/// pipeline is then empty, and the path the directory's), or its name is not one a pointer can have; null otherwise.
/// </summary>
public readonly record struct PointerFile(string PipelineId, string Environment, string Path, string? Error);
