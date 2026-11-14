using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Hosting.Manifests;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// What a store's pointer publishes for a pipeline in an environment, as <c>build</c>, <c>publish</c> and <c>revoke</c>
/// read it: the manifest it names, and the experiments the revocations just before it removed (design 10.6). The errors of
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

    /// <summary>
    /// Whether <paramref name="manifest"/> revokes every earlier one: its <c>min_accepted_seq</c> is its own <c>seq</c>, which
    /// is how a controller revokes content (design 10.2), and an ordinary release keeps the floor of the one before.
    /// </summary>
    internal static bool IsRevocation(ReleaseManifest manifest) => manifest.MinAcceptedSeq == manifest.Seq;

    /// <summary>
    /// The experiments removed by the revocations that end at the published manifest (<paramref name="id"/>): from it back
    /// along <c>previous</c> while each is a revocation, every experiment the one before had and it lacks, with the artifact
    /// it had and the revocation that removed it. An ordinary release ends the walk: what it left out was left out on
    /// purpose, and what it released was released again. <c>Unread</c> names a manifest the walk needed and
    /// could not read, so the experiments it revoked are not known.
    /// </summary>
    internal static async Task<(Dictionary<string, Revocation> Revoked, string? Unread)> RevokedAsync(
        IArtifactStore store, string id, ReleaseManifest manifest, CancellationToken cancellationToken)
    {
        var revoked = new Dictionary<string, Revocation>(StringComparer.Ordinal);
        while (IsRevocation(manifest) && manifest.Previous is { } previous)
        {
            var before = await ManifestBuilder.ReadAsync(store, previous, new DiagnosticBag(), cancellationToken).ConfigureAwait(false);
            if (before is null)
            {
                return (revoked, previous);
            }

            if (before.Seq >= manifest.Seq || before.PipelineId != manifest.PipelineId || before.Environment != manifest.Environment)
            {
                break; // not this pipeline's history: a manifest another tool wrote
            }

            foreach (var (name, entry) in before.Experiments)
            {
                if (!manifest.Experiments.ContainsKey(name))
                {
                    revoked.TryAdd(name, new Revocation(id, manifest.Seq, entry.Artifact));
                }
            }

            (id, manifest) = (previous, before);
        }

        return (revoked, null);
    }

    /// <summary>The next sequence after <paramref name="published"/>'s; null after reporting that there is none.</summary>
    internal static long? NextSeq(string id, ReleaseManifest published, DiagnosticBag diagnostics)
    {
        if (published.Seq < BuildCommand.MaxSeq)
        {
            return published.Seq + 1;
        }

        diagnostics.Error("SEQ_EXHAUSTED", "manifest:" + id, "/seq",
            $"The published manifest has sequence {published.Seq}: the next is beyond {BuildCommand.MaxSeq} (2^53 - 1), the largest a manifest's JSON writes exactly.",
            "sequence numbers end there: a controller that numbers releases by time uses milliseconds or seconds, not finer units");
        return null;
    }
}

/// <summary>An experiment a revocation removed: the revoking manifest, its sequence, and the experiment's artifact then.</summary>
internal sealed record Revocation(string ManifestId, long Seq, string Artifact);
