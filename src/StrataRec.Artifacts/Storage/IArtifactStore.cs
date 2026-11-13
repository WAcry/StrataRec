using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.Artifacts.Storage;

/// <summary>
/// Content-addressed storage for artifacts (design 9.2, 10.1): blobs are immutable and named by their
/// <see cref="ContentHash"/>, so a node downloads each artifact once and any copy can be verified against its name.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be safe for concurrent use. <see cref="PutAsync"/> is idempotent: storing equal content
/// twice yields the same id and leaves one blob. <see cref="TryGetAsync"/> must verify the SHA-256 of the bytes it
/// read against the id and throw <see cref="ArtifactIntegrityException"/> on mismatch, so a corrupted or tampered
/// blob is never handed to the loader.
/// </para>
/// <para>
/// Teams on other configuration systems integrate by implementing this interface (local files, S3-compatible
/// storage, HTTP; design 10.1 item 4).
/// </para>
/// </remarks>
public interface IArtifactStore
{
    /// <summary>Reads the canonical bytes stored under <paramref name="contentId"/>.</summary>
    /// <param name="contentId">A content id, <c>sha256:</c> plus 64 lowercase hex digits.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The blob, owned by the caller, or <see langword="null"/> when the store does not have it.</returns>
    /// <exception cref="ArgumentException"><paramref name="contentId"/> is not a valid content id.</exception>
    /// <exception cref="ArtifactIntegrityException">The stored bytes do not hash to <paramref name="contentId"/>.</exception>
    ValueTask<byte[]?> TryGetAsync(string contentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Canonicalizes <paramref name="artifact"/> (RFC 8785), stores the canonical UTF-8 bytes under their SHA-256 and
    /// returns the content id.
    /// </summary>
    /// <param name="artifact">The artifact document; not modified, and must not be mutated during the call.</param>
    /// <param name="cancellationToken">Cancels the write; a cancelled write leaves no partial blob.</param>
    /// <exception cref="JsonCanonicalizationException">The document has no canonical form.</exception>
    ValueTask<string> PutAsync(JsonNode artifact, CancellationToken cancellationToken = default);
}
