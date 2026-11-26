using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Hosting.Manifests;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// What a store's pointer publishes for a pipeline in an environment, as <c>build</c>, <c>publish</c> and <c>revoke</c>
/// read it: the manifest it names, and the experiments the revocations just before it removed. The errors of
/// a pointer that cannot be read, or that names a manifest the store cannot read or of another pipeline (review 37), are
/// reported here, once for the three commands.
/// </summary>
internal static class PublishedRelease
{
    /// <summary>
    /// The manifest the pointer at <paramref name="path"/> names, and its id; none when nothing is published. Not read,
    /// after reporting, when the pointer or that manifest cannot be, or the manifest is another pipeline's or environment's:
    /// what the caller needs of it (<paramref name="unknown"/>, as "the next sequence") is then unknown.
    /// </summary>
    /// <param name="otherwise">What else the caller can do then, appended to the hint ("or pass --seq"); null for nothing.</param>
    internal static async Task<(bool Read, (string Id, ReleaseManifest Manifest)? Published)> ReadAsync(
        FileSystemManifestSource pointers, string path, IArtifactStore store, string pipelineId, string environment, string unknown, string? otherwise,
        DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var orElse = otherwise is null ? string.Empty : ", " + otherwise;
        string? current;
        try
        {
            current = await pointers.GetManifestIdAsync(pipelineId, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("POINTER_UNREADABLE", path, string.Empty, $"The pointer could not be read, so {unknown} is unknown: {ex.Message}",
                "fix the pointer ('stratarec publish' writes it)" + orElse);
            return (false, null);
        }

        if (current is null)
        {
            return (true, null);
        }

        var read = new DiagnosticBag();
        if (await ManifestBuilder.ReadAsync(store, current, read, cancellationToken).ConfigureAwait(false) is not { } published)
        {
            diagnostics.AddRange(read.Items);
            diagnostics.Error("POINTER_MANIFEST_UNREADABLE", path, string.Empty,
                $"The pointer names manifest '{current}', which the store cannot read, so {unknown} is unknown.",
                "restore that manifest to the store" + orElse);
            return (false, null);
        }

        // Its sequence, floor and experiments say nothing of this pipeline's releases when it is another's (review 37).
        if (!ManifestBuilder.CheckIdentity(published, current, pipelineId, environment, diagnostics))
        {
            diagnostics.Error("POINTER_MANIFEST_FOREIGN", path, string.Empty,
                $"The pointer names manifest '{current}' of another pipeline or environment, so {unknown} is unknown.",
                "fix the pointer" + orElse);
            return (false, null);
        }

        return (true, (current, published));
    }

    /// <summary>The next sequence after <paramref name="published"/>'s; null after reporting that there is none.</summary>
    internal static long? NextSeq(string id, ReleaseManifest published, DiagnosticBag diagnostics)
    {
        if (published.Seq < BuildCommand.MaxSeq)
        {
            return published.Seq + 1;
        }

        diagnostics.Error("SEQ_EXHAUSTED", "manifest:" + id, "/seq",
            $"The published manifest has sequence {published.Seq}: the next is beyond {BuildCommand.MaxSeq} (2^53 - 1), the largest safe integer, up to which a " +
            "manifest's JSON writes every integer exactly.",
            "sequence numbers end there: a controller that numbers releases by time uses milliseconds or seconds, not finer units");
        return null;
    }
}
