namespace StrataRec.Artifacts.Storage;

/// <summary>
/// A stored blob does not hash to the content id it is stored under: the storage is corrupted or was tampered
/// with. Not transient; retrying the same read returns the same bytes.
/// </summary>
public sealed class ArtifactIntegrityException : Exception
{
    public ArtifactIntegrityException(string expectedContentId, string actualContentId, string? location)
        : base(Format(expectedContentId, actualContentId, location))
    {
        ArgumentNullException.ThrowIfNull(expectedContentId);
        ArgumentNullException.ThrowIfNull(actualContentId);
        ExpectedContentId = expectedContentId;
        ActualContentId = actualContentId;
        Location = location;
    }

    /// <summary>The id the blob was requested and stored under.</summary>
    public string ExpectedContentId { get; }

    /// <summary>The id the bytes actually hash to.</summary>
    public string ActualContentId { get; }

    /// <summary>Where the bytes were read from (a file path or URL), when the store knows it.</summary>
    public string? Location { get; }

    private static string Format(string expected, string actual, string? location) =>
        $"Artifact {expected}{(location is null ? string.Empty : $" at '{location}'")} is corrupted: its bytes hash to {actual}. " +
        "Delete the blob and store the artifact again.";
}
