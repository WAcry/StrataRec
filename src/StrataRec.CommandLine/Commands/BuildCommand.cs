using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec build</c> (design 10.1 item 2): validates, uploads canonical artifacts to a content-addressed store and
/// writes the release manifest. Nothing is uploaded when validation fails.
/// </summary>
/// <remarks>
/// <para>Without <c>--seq</c>, the manifest is the next release of its pipeline in its environment (design 10.2): its
/// <c>seq</c> is one more than that of the manifest the store's pointer names (<c>stratarec publish</c> writes it), it
/// keeps that manifest's <c>min_accepted_seq</c>, as an ordinary release does, and names it as <c>previous</c>, which
/// <c>stratarec diff --to</c> compares with; the first release is 1. So build and publish follow each other without
/// anyone keeping count. A configuration that is the published one's, but for those fields, is no new release: build
/// writes the published manifest, which publish then finds published already, so a pipeline can run both again.</para>
/// <para>A release controller that numbers releases itself passes <c>--seq</c>, and the store's pointers are not read.</para>
/// </remarks>
internal static class BuildCommand
{
    /// <summary>
    /// The largest sequence number, 2^53 − 1: a manifest is canonical JSON (RFC 8785), which writes integers as doubles, so
    /// a larger one is not written exactly (review 37).
    /// </summary>
    internal const long MaxSeq = 9_007_199_254_740_991;

    public static Command Create(PlanEnvironment provided)
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var pipeline = CommonOptions.Pipeline();
        var store = new Option<string>("--store", "-s") { Description = "Artifact store directory (blobs/sha256/...).", Required = true };
        var manifest = new Option<string?>("--manifest", "-m") { Description = "Write the manifest here instead of stdout." };
        var seq = new Option<long?>("--seq")
        {
            Description = "The manifest's sequence number (design 10.2), from 1 to 2^53 - 1. By default the next release: one more than the manifest the store's " +
                          "pointer for the pipeline and environment names ('stratarec publish' writes it), keeping its min_accepted_seq; 1 when none is published.",
        };
        seq.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<long?>() is < 1 or > MaxSeq)
            {
                result.AddError($"--seq must be from 1 to {MaxSeq} (2^53 - 1, the largest integer a manifest's JSON writes exactly).");
            }
        });
        var command = new Command("build", "Validate, upload artifacts by content hash and emit a release manifest. Exits with 1 when the configuration has errors.") { config, environment, pipeline, store, manifest, seq };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(pipeline),
            parse.GetValue(store)!, parse.GetValue(manifest), parse.GetValue(seq), parse.InvocationConfiguration.Output, cancellationToken,
            parse.InvocationConfiguration.Error));
        return command;
    }

    /// <param name="errors">Where diagnostics and the manifest id go; standard error by default.</param>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    /// <param name="seq">The manifest's sequence number; null for the next release (see remarks).</param>
    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string config, string environment, string? pipeline, string storeRoot, string? manifestPath, long? seq, TextWriter output,
        CancellationToken cancellationToken, TextWriter? errors = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        var (workspace, request, compilation) = CommonOptions.Compile(provided, config, environment, pipeline, diagnostics);
        if (request is null || compilation is not { Succeeded: true } || diagnostics.HasErrors)
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
            return 1;
        }

        var store = new FileSystemArtifactStore(storeRoot);
        var pipelineId = request.Pipeline.PipelineId;
        (string Id, ReleaseManifest Manifest)? published = null;
        if (seq is null)
        {
            var (read, pointed) = await ReadPublishedAsync(storeRoot, store, pipelineId, environment, diagnostics, cancellationToken).ConfigureAwait(false);
            if (!read)
            {
                Output.Diagnostics(diagnostics.Items, "text", errors);
                return 1;
            }

            published = pointed;
        }

        var manifest = published is { } current
            ? await ManifestBuilder.BuildAsync(workspace, pipelineId, environment, store, diagnostics, current.Manifest.Seq + 1, current.Manifest.MinAcceptedSeq,
                current.Id, cancellationToken).ConfigureAwait(false)
            : await ManifestBuilder.BuildAsync(workspace, pipelineId, environment, store, diagnostics, seq ?? 1, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
            return 1;
        }

        string manifestId;
        var unchanged = published is { } was && ManifestBuilder.ContentId(Renumbered(manifest, was.Manifest)) == was.Id;
        if (unchanged)
        {
            (manifestId, manifest) = published!.Value; // stored when it was built
        }
        else
        {
            manifestId = await store.PutAsync(ArtifactSerializer.ToNode(manifest), cancellationToken).ConfigureAwait(false);
        }

        var text = ArtifactSerializer.ToNode(manifest).ToJsonString(ArtifactSerializer.Options);
        if (manifestPath is null)
        {
            output.WriteLine(text);
        }
        else
        {
            await File.WriteAllTextAsync(manifestPath, text + "\n", cancellationToken).ConfigureAwait(false);
        }

        errors.WriteLine($"manifest {manifestId} (seq {manifest.Seq}, {Output.Count(manifest.Requires.Count, "capability", "capabilities")}, " +
                         $"{Output.Count(manifest.Experiments.Count, "experiment")})" +
                         (unchanged ? ": unchanged, the release the pointer names has this configuration" : string.Empty));
        if (!unchanged)
        {
            errors.WriteLine($"publish it with: stratarec publish -s {InitCommand.Quote(storeRoot)} {manifestId}");
        }

        return 0;
    }

    /// <summary><paramref name="manifest"/> with the release fields of <paramref name="like"/>: equal ids then mean the same configuration.</summary>
    private static ReleaseManifest Renumbered(ReleaseManifest manifest, ReleaseManifest like) => new()
    {
        PipelineId = manifest.PipelineId,
        Environment = manifest.Environment,
        Seq = like.Seq,
        MinAcceptedSeq = like.MinAcceptedSeq,
        Previous = like.Previous,
        Requires = manifest.Requires,
        Artifacts = manifest.Artifacts,
        Experiments = manifest.Experiments,
    };

    /// <summary>
    /// The manifest the store's pointer names for the pipeline in the environment, and its id; none when nothing is
    /// published. Not read, after reporting, when the pointer or that manifest cannot be: the next sequence is then
    /// unknown, and publishing would fail on it too.
    /// </summary>
    private static async Task<(bool Read, (string Id, ReleaseManifest Manifest)? Published)> ReadPublishedAsync(
        string storeRoot, FileSystemArtifactStore store, string pipelineId, string environment, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var pointers = new FileSystemManifestSource(storeRoot);
        string? current;
        try
        {
            current = await pointers.GetManifestIdAsync(pipelineId, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Error("POINTER_UNREADABLE", storeRoot, string.Empty,
                $"The pointer of pipeline '{pipelineId}' in environment '{environment}' could not be read, so the next sequence is unknown: {ex.Message}",
                "fix the pointer ('stratarec publish' writes it), or pass --seq");
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
            diagnostics.Error("POINTER_MANIFEST_UNREADABLE", pointers.GetPointerPath(pipelineId, environment), string.Empty,
                $"The pointer names manifest '{current}', which the store cannot read, so the next sequence is unknown.",
                "restore that manifest to the store, or pass --seq");
            return (false, null);
        }

        // Its sequence, floor and id say nothing of this pipeline's releases when it is another's (review 37).
        if (!ManifestBuilder.CheckIdentity(published, current, pipelineId, environment, diagnostics))
        {
            diagnostics.Error("POINTER_MANIFEST_FOREIGN", pointers.GetPointerPath(pipelineId, environment), string.Empty,
                $"The pointer names manifest '{current}' of another pipeline or environment, so the next sequence is unknown.",
                "fix the pointer, or pass --seq");
            return (false, null);
        }

        if (published.Seq >= MaxSeq)
        {
            diagnostics.Error("SEQ_EXHAUSTED", "manifest:" + current, "/seq",
                $"The published manifest has sequence {published.Seq}: the next is beyond {MaxSeq} (2^53 - 1), the largest a manifest's JSON writes exactly.",
                "sequence numbers end there: a controller that numbers releases by time uses milliseconds or seconds, not finer units");
            return (false, null);
        }

        return (true, (current, published));
    }
}
