using StrataRec.Artifacts.Json;

namespace StrataRec.Artifacts.Storage;

/// <summary>Argument and integrity checks shared by the <see cref="IArtifactStore"/> implementations.</summary>
internal static class ArtifactStoreGuard
{
    /// <summary>Validates a content id and returns its hex digest; rejecting malformed ids also prevents path traversal.</summary>
    public static string RequireHexDigest(string contentId, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(contentId, parameterName);
        try
        {
            return ContentHash.GetHexDigest(contentId);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(ex.Message, parameterName, ex);
        }
    }

    /// <summary>Throws <see cref="ArtifactIntegrityException"/> unless <paramref name="bytes"/> hash to <paramref name="contentId"/>.</summary>
    public static void VerifyIntegrity(string contentId, ReadOnlySpan<byte> bytes, string? location)
    {
        var actual = ContentHash.FromCanonicalBytes(bytes);
        if (!string.Equals(actual, contentId, StringComparison.Ordinal))
        {
            throw new ArtifactIntegrityException(contentId, actual, location);
        }
    }
}
