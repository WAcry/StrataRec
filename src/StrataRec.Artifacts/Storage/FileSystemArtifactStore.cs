using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.Artifacts.Storage;

/// <summary>
/// An <see cref="IArtifactStore"/> over a local directory, laid out like the object store of design 10.1:
/// <c>&lt;root&gt;/blobs/sha256/&lt;64 hex digits&gt;</c>. Used by <c>stratarec build</c> and as a node's local cache.
/// </summary>
/// <remarks>
/// <para>
/// Writes are atomic: the canonical bytes go to a temporary file in the same directory (named
/// <c>.&lt;hex&gt;.&lt;guid&gt;.tmp</c>, so listings can skip dot files), are flushed to disk, and are then renamed
/// into place. Readers therefore see either no blob or a complete one, also across processes sharing the
/// directory. Writes are idempotent: an existing blob whose bytes equal the canonical bytes is left untouched, and
/// one that differs (for example truncated by a disk fault) is replaced. The rename itself is not followed by a
/// directory fsync, so after a power loss a just-written blob may be missing, never partial.
/// </para>
/// <para>
/// Blobs are stored uncompressed. The design's zstd compression for transport and storage (design 9.1 item 4) is
/// a planned addition; the content id will stay the hash of the uncompressed canonical bytes.
/// </para>
/// </remarks>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    /// <summary>Creates a store rooted at <paramref name="rootDirectory"/>; directories are created on first write.</summary>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is empty or whitespace.</exception>
    public FileSystemArtifactStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        BlobDirectory = Path.Combine(RootDirectory, "blobs", "sha256");
    }

    /// <summary>The absolute root directory.</summary>
    public string RootDirectory { get; }

    /// <summary>The directory holding the blobs: <c>&lt;root&gt;/blobs/sha256</c>.</summary>
    public string BlobDirectory { get; }

    /// <summary>The file a blob with this content id is stored in, whether or not it exists.</summary>
    /// <exception cref="ArgumentException"><paramref name="contentId"/> is not a valid content id.</exception>
    public string GetBlobPath(string contentId) =>
        Path.Combine(BlobDirectory, ArtifactStoreGuard.RequireHexDigest(contentId, nameof(contentId)));

    /// <inheritdoc />
    public async ValueTask<byte[]?> TryGetAsync(string contentId, CancellationToken cancellationToken = default)
    {
        var path = GetBlobPath(contentId);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        ArtifactStoreGuard.VerifyIntegrity(contentId, bytes, path);
        return bytes;
    }

    /// <inheritdoc />
    public async ValueTask<string> PutAsync(JsonNode artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonCanonicalizer.Canonicalize(artifact);
        var contentId = ContentHash.FromCanonicalBytes(bytes);
        var hex = contentId[ContentHash.Prefix.Length..];
        var path = Path.Combine(BlobDirectory, hex);
        if (await ContainsExactlyAsync(path, bytes, cancellationToken).ConfigureAwait(false))
        {
            return contentId;
        }

        Directory.CreateDirectory(BlobDirectory);
        var temporary = Path.Combine(BlobDirectory, $".{hex}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                // Replacing is safe: any existing blob either has these exact bytes or is damaged.
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // On Windows a blob open for reading cannot be replaced; that is fine if a concurrent writer
                // already stored the same content.
                if (!await ContainsExactlyAsync(path, bytes, CancellationToken.None).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
        finally
        {
            TryDelete(temporary);
        }

        return contentId;
    }

    private static async ValueTask<bool> ContainsExactlyAsync(string path, byte[] expected, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expected.Length)
            {
                return false;
            }

            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return existing.AsSpan().SequenceEqual(expected);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover dot-prefixed temporary file is harmless.
        }
    }
}
