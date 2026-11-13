using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.Artifacts.Storage;

/// <summary>
/// A process-local <see cref="IArtifactStore"/> for tests, draft previews and single-process tools. Thread-safe.
/// </summary>
/// <remarks>
/// Blobs are kept as private copies: <see cref="TryGetAsync"/> returns a fresh array each time, so callers cannot
/// corrupt the store by mutating what they received. Reads are still verified against the id, as the interface
/// requires.
/// </remarks>
public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <summary>Number of distinct blobs stored.</summary>
    public int Count => _blobs.Count;

    /// <inheritdoc />
    public ValueTask<byte[]?> TryGetAsync(string contentId, CancellationToken cancellationToken = default)
    {
        ArtifactStoreGuard.RequireHexDigest(contentId, nameof(contentId));
        cancellationToken.ThrowIfCancellationRequested();
        if (!_blobs.TryGetValue(contentId, out var stored))
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        ArtifactStoreGuard.VerifyIntegrity(contentId, stored, location: null);
        return ValueTask.FromResult<byte[]?>((byte[])stored.Clone());
    }

    /// <inheritdoc />
    public ValueTask<string> PutAsync(JsonNode artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonCanonicalizer.Canonicalize(artifact);
        var contentId = ContentHash.FromCanonicalBytes(bytes);
        _blobs.TryAdd(contentId, bytes);
        return ValueTask.FromResult(contentId);
    }
}
