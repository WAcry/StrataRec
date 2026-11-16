using System.CommandLine;
using StrataRec.Artifacts.Json;
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
/// <c>seq</c> is one more than that of the manifest the store's pointer names (<c>stratarec publish</c> writes it), and it
/// names that manifest as <c>previous</c>, which
/// <c>stratarec diff --to</c> compares with; the first release is 1. So build and publish follow each other without
/// anyone keeping count. A configuration that is the published one's, but for those fields, is no new release: build
/// writes the published manifest, which publish then finds published already, so a pipeline can run both again.</para>
/// <para>An experiment that <c>stratarec revoke</c> removed stays out (design 10.6): while the published manifest lists it
/// as revoked (<see cref="ReleaseManifest.Revoked"/>), a build that releases it unchanged fails at the release file's entry,
/// so a release that follows the stop (a routine one, from CI) does not bring the stopped experiment back. A changed
/// experiment is the owner's fix and is released; <c>--restore</c> releases one unchanged. A build's manifest lists no
/// revocations: it released none unchanged, and what it leaves out, the release file now leaves out.</para>
/// <para>A release controller that numbers releases itself passes <c>--seq</c>, and the store's pointers are not read.</para>
/// </remarks>
internal static class BuildCommand
{
    /// <summary>
    /// The largest sequence number build makes, 2^53 − 1, the largest safe integer: a manifest is canonical JSON (RFC 8785),
    /// which writes integers as doubles, so every integer up to it is written exactly and some beyond it are not (review 37).
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
            Description = "The manifest's sequence number (design 10.2), from 1 to 2^53 - 1 (the largest safe integer). By default the next release: one more than the manifest the store's " +
                          "pointer for the pipeline and environment names ('stratarec publish' writes it); 1 when none is published.",
        };
        seq.Validators.Add(static result =>
        {
            if (CommonOptions.Converted<long>(result) is < 1 or > MaxSeq)
            {
                result.AddError($"--seq must be from 1 to {MaxSeq} (2^53 - 1, the largest safe integer: a manifest's JSON writes every integer up to it exactly).");
            }
        });
        var restore = new Option<string[]>("--restore")
        {
            Description = "Release this experiment again although 'stratarec revoke' removed it and it has not changed since (without it, build refuses to). Repeatable.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("build", "Validate, upload artifacts by content hash and emit a release manifest. Exits with 1 when the configuration has errors, " +
            "releases a revoked experiment unchanged, or the store or the --manifest file cannot be written.") { config, environment, pipeline, store, manifest, seq, restore };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(pipeline),
            parse.GetValue(store)!, parse.GetValue(manifest), parse.GetValue(seq), parse.InvocationConfiguration.Output, cancellationToken,
            parse.InvocationConfiguration.Error, parse.GetValue(restore)));
        return command;
    }

    /// <param name="errors">Where diagnostics and the manifest id go; standard error by default.</param>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    /// <param name="seq">The manifest's sequence number; null for the next release (see remarks).</param>
    /// <param name="restore">Revoked experiments to release again unchanged (see remarks).</param>
    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string config, string environment, string? pipeline, string storeRoot, string? manifestPath, long? seq, TextWriter output,
        CancellationToken cancellationToken, TextWriter? errors = null, string[]? restore = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        // An expired experiment cannot enter a new release (design 11.7): build compiles with today's date, as validate does.
        var (workspace, request, compilation) = CommonOptions.Compile(provided, config, environment, pipeline, diagnostics, DateOnly.FromDateTime(DateTime.UtcNow));
        if (request is null || compilation is not { Succeeded: true } || diagnostics.HasErrors)
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
            return 1;
        }

        var store = new FileSystemArtifactStore(storeRoot);
        var pipelineId = request.Pipeline.PipelineId;
        (string Id, ReleaseManifest Manifest)? published = null;
        long next = 1;
        if (seq is null)
        {
            var (read, pointed) = await ReadPublishedAsync(storeRoot, store, pipelineId, environment, diagnostics, cancellationToken).ConfigureAwait(false);
            if (!read)
            {
                Output.Diagnostics(diagnostics.Items, "text", errors);
                return 1;
            }

            if (pointed is { } last)
            {
                if (PublishedRelease.NextSeq(last.Id, last.Manifest, diagnostics) is not { } following)
                {
                    Output.Diagnostics(diagnostics.Items, "text", errors);
                    return 1;
                }

                next = following;
                if (!KeepsRevokedOut(workspace, request, environment, last.Manifest, restore ?? Array.Empty<string>(), diagnostics))
                {
                    Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
                    return 1;
                }
            }

            published = pointed;
        }

        var compiledWith = CommonOptions.ForRoot(provided, workspace.Root);
        ReleaseManifest? manifest;
        var manifestId = string.Empty;
        var unchanged = false;
        try
        {
            manifest = published is { } current
                ? await ManifestBuilder.BuildAsync(workspace, pipelineId, environment, compiledWith, store, diagnostics, next, current.Id, cancellationToken)
                    .ConfigureAwait(false)
                : await ManifestBuilder.BuildAsync(workspace, pipelineId, environment, compiledWith, store, diagnostics, seq ?? 1, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            unchanged = manifest is not null && published is { } was && ManifestBuilder.ContentId(Renumbered(manifest, was.Manifest)) == was.Id;
            if (unchanged)
            {
                (manifestId, manifest) = published!.Value; // stored when it was built
            }
            else if (manifest is not null)
            {
                manifestId = await store.PutAsync(ArtifactSerializer.ToNode(manifest), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.StoreUnwritable(ex, diagnostics);
            manifest = null;
        }

        if (manifest is null)
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
            return 1;
        }

        var text = ArtifactSerializer.ToNode(manifest).ToJsonString(ArtifactSerializer.Options);
        if (manifestPath is null)
        {
            output.WriteLine(text);
        }
        else if (!Output.WriteFile("--manifest", manifestPath, path => File.WriteAllText(path, text + "\n"), diagnostics))
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
            return 1;
        }

        if (diagnostics.Items.Count > 0)
        {
            // The warnings 'validate' prints: a release is where they matter most.
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
        }

        errors.WriteLine($"manifest {manifestId} (seq {manifest.Seq}, {Output.Count(ManifestBuilder.RequiredCapabilities(manifest).Count, "capability", "capabilities")}, " +
                         $"{Output.Count(manifest.Experiments.Count, "experiment")})" +
                         (unchanged ? ": unchanged, the release the pointer names has this configuration" : string.Empty));
        if (!unchanged)
        {
            errors.WriteLine($"publish it with: stratarec publish -s {InitCommand.Quote(storeRoot)} {manifestId}");
        }

        return 0;
    }

    /// <summary>
    /// <paramref name="manifest"/> with the release fields of <paramref name="like"/>: equal ids then mean the same
    /// configuration. Its revocations are a release field too: a build of what a revocation serves is no new release, and
    /// the revocation stays published, still keeping out what it removed.
    /// </summary>
    private static ReleaseManifest Renumbered(ReleaseManifest manifest, ReleaseManifest like) => new()
    {
        PipelineId = manifest.PipelineId,
        Environment = manifest.Environment,
        Seq = like.Seq,
        Previous = like.Previous,
        Requires = manifest.Requires,
        Artifacts = manifest.Artifacts,
        Experiments = manifest.Experiments,
        Revoked = like.Revoked,
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
        string path;
        try
        {
            path = pointers.GetPointerPath(pipelineId, environment);
        }
        catch (ArgumentException ex)
        {
            diagnostics.Error("POINTER_UNREADABLE", storeRoot, string.Empty,
                $"Pipeline '{pipelineId}' in environment '{environment}' has no pointer file name, so the next sequence is unknown: {ex.Message}",
                "name environments with letters, digits, '_', '-' and '.', or pass --seq");
            return (false, null);
        }

        return await PublishedRelease.ReadAsync(pointers, path, store, pipelineId, environment, "the next sequence", "or pass --seq", diagnostics, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this build keeps out the experiments the published manifest lists as revoked (see remarks): false, after
    /// reporting at the release file's entry, when it releases one of them as it was and <paramref name="restore"/> does
    /// not name it.
    /// </summary>
    private static bool KeepsRevokedOut(
        ConfigWorkspace workspace, PlanCompileRequest request, string environment, ReleaseManifest published, string[] restore, DiagnosticBag diagnostics)
    {
        var releaseFile = workspace.Releases.TryGetValue(request.Pipeline.PipelineId + "/" + environment, out var release)
            ? release.Path
            : "releases/" + request.Pipeline.PipelineId + "." + environment + ".jsonc";
        var kept = true;
        foreach (var placement in request.Experiments)
        {
            var name = placement.Experiment.ExperimentId;
            if (published.TryGetStop(name, out var revocation)
                && Array.IndexOf(restore, name) < 0
                && ContentHash.Compute(workspace.Experiments[name].Document) == revocation.Artifact)
            {
                diagnostics.Error("EXPERIMENT_REVOKED", releaseFile, "/experiments/" + JsonPointer.Escape(name),
                    $"Experiment '{name}' was revoked at seq {revocation.Seq}, and no release since brought it back; " +
                    "this build releases it again as it was.",
                    $"remove it here until it is fixed (a changed experiment is released), or pass --restore {name} to release it as it is");
                kept = false;
            }
        }

        return kept;
    }
}
