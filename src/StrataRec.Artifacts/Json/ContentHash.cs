using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// Content ids of artifacts: <c>sha256:</c> followed by the 64 lowercase hex digits of the SHA-256 of the
/// RFC 8785 canonical bytes (design 9.1 item 3, 9.2). Equal content always yields an equal id, independent of key
/// order, whitespace and comments, so manifests reference artifacts by id and nodes cache them by id.
/// </summary>
/// <remarks>
/// Only the lowercase form is valid. Accepting uppercase digits would give one artifact two ids, and the id is
/// also the blob's file name in <c>blobs/sha256/&lt;hex&gt;</c> (design 10.1).
/// </remarks>
public static class ContentHash
{
    /// <summary>The algorithm prefix of every content id.</summary>
    public const string Prefix = "sha256:";

    /// <summary>Number of hex digits after the prefix.</summary>
    public const int HexDigestLength = 64;

    /// <summary>Total length of a content id string.</summary>
    public const int ContentIdLength = 71; // Prefix.Length + HexDigestLength

    private const string LowerHex = "0123456789abcdef";

    /// <summary>Computes the content id of a JSON value from its RFC 8785 canonical form.</summary>
    /// <exception cref="JsonCanonicalizationException">The value has no canonical form (see <see cref="JsonCanonicalizer"/>).</exception>
    public static string Compute(JsonNode? node) => FromCanonicalBytes(JsonCanonicalizer.Canonicalize(node));

    /// <summary>
    /// Computes the content id of bytes that are already canonical, e.g. a blob read back from a store. The bytes
    /// are hashed as given; passing non-canonical JSON yields an id no other producer will reproduce.
    /// </summary>
    public static string FromCanonicalBytes(ReadOnlySpan<byte> canonicalUtf8)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(canonicalUtf8, digest);
        Span<char> id = stackalloc char[ContentIdLength];
        Prefix.AsSpan().CopyTo(id);
        for (var i = 0; i < digest.Length; i++)
        {
            id[Prefix.Length + (2 * i)] = LowerHex[digest[i] >> 4];
            id[Prefix.Length + (2 * i) + 1] = LowerHex[digest[i] & 0xF];
        }

        return new string(id);
    }

    /// <summary>True when the SHA-256 of <paramref name="bytes"/> is the digest named by <paramref name="contentId"/>.</summary>
    /// <exception cref="FormatException"><paramref name="contentId"/> is not a valid content id.</exception>
    public static bool Matches(string contentId, ReadOnlySpan<byte> bytes)
    {
        _ = GetHexDigest(contentId);
        return string.Equals(FromCanonicalBytes(bytes), contentId, StringComparison.Ordinal);
    }

    /// <summary>True for a well-formed content id: <c>sha256:</c> plus 64 lowercase hex digits.</summary>
    public static bool IsValid([NotNullWhen(true)] string? contentId) => Describe(contentId) is null;

    /// <summary>Extracts the 64-digit hex digest from a content id without throwing.</summary>
    public static bool TryGetHexDigest([NotNullWhen(true)] string? contentId, [NotNullWhen(true)] out string? hexDigest)
    {
        if (Describe(contentId) is null)
        {
            hexDigest = contentId![Prefix.Length..];
            return true;
        }

        hexDigest = null;
        return false;
    }

    /// <summary>Extracts the 64-digit hex digest from a content id.</summary>
    /// <exception cref="FormatException">The id is malformed; the message says how.</exception>
    public static string GetHexDigest(string contentId)
    {
        ArgumentNullException.ThrowIfNull(contentId);
        var problem = Describe(contentId);
        return problem is null ? contentId[Prefix.Length..] : throw new FormatException(problem);
    }

    /// <summary>Returns why <paramref name="contentId"/> is invalid, or <see langword="null"/> when it is valid.</summary>
    private static string? Describe(string? contentId)
    {
        if (contentId is null)
        {
            return "Content id is null.";
        }

        const string Expected = "expected 'sha256:' followed by 64 lowercase hexadecimal digits";
        if (!contentId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return $"Content id '{contentId}' is invalid: {Expected}.";
        }

        if (contentId.Length != ContentIdLength)
        {
            return $"Content id '{contentId}' is invalid: the digest has {contentId.Length - Prefix.Length} characters; {Expected}.";
        }

        for (var i = Prefix.Length; i < contentId.Length; i++)
        {
            if (contentId[i] is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return $"Content id '{contentId}' is invalid: '{contentId[i]}' at offset {i} is not a lowercase hex digit; {Expected}.";
            }
        }

        return null;
    }
}
