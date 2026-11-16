using System.Text;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting.Manifests;

/// <summary>
/// Builds release manifests from a workspace and turns manifests back into compile requests (design 10.1–10.3).
/// Artifacts are stored by content id, so a manifest pins exactly what runs.
/// </summary>
public static class ManifestBuilder
{
    /// <summary>
    /// Uploads every artifact a pipeline/environment needs to <paramref name="store"/> and returns the manifest
    /// (<c>stratarec build</c>). Returns null after reporting errors.
    /// </summary>
    /// <param name="provided">The binary's components: the build compiles the baseline and each experiment with them to
    /// learn what each requires (<see cref="PlanCompiler.Requirements"/>).</param>
    public static async ValueTask<ReleaseManifest?> BuildAsync(
        ConfigWorkspace workspace,
        string pipelineId,
        string environment,
        PlanEnvironment provided,
        IArtifactStore store,
        DiagnosticBag diagnostics,
        long seq = 1,
        long minAcceptedSeq = 0,
        string? previous = null,
        CancellationToken cancellationToken = default)
    {
        var request = workspace.CreateCompileRequest(pipelineId, environment, diagnostics);
        if (request is null || PlanCompiler.Requirements(request, provided, diagnostics) is not { } requirements)
        {
            return null;
        }

        var pipeline = workspace.Pipelines[pipelineId];
        LoadedArtifact<FeatureCatalogArtifact>? catalog = null;
        foreach (var (_, c) in workspace.Catalogs)
        {
            if (ReferenceEquals(c.Value, request.Catalog))
            {
                catalog = c;
            }
        }

        LoadedArtifact<SourceBindingArtifact>? bindings = null;
        foreach (var (_, b) in workspace.Bindings)
        {
            if (ReferenceEquals(b.Value, request.Bindings))
            {
                bindings = b;
            }
        }

        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in request.Tables)
        {
            tables[table.TableId] = await store.PutAsync(workspace.Tables[table.TableId].Document, cancellationToken).ConfigureAwait(false);
        }

        var experiments = new Dictionary<string, ManifestExperiment>(StringComparer.Ordinal);
        foreach (var placement in request.Experiments)
        {
            var id = placement.Experiment.ExperimentId;
            experiments[id] = new ManifestExperiment
            {
                Artifact = await store.PutAsync(workspace.Experiments[id].Document, cancellationToken).ConfigureAwait(false),
                Layer = placement.Layer,
                Requires = new List<string>(requirements.Experiments[id]),
            };
        }

        return new ReleaseManifest
        {
            PipelineId = pipelineId,
            Environment = environment,
            Seq = seq,
            MinAcceptedSeq = minAcceptedSeq,
            Previous = previous,
            Requires = new List<string>(requirements.Baseline),
            Artifacts = new ManifestArtifacts
            {
                Catalog = await store.PutAsync(catalog!.Document, cancellationToken).ConfigureAwait(false),
                Bindings = await store.PutAsync(bindings!.Document, cancellationToken).ConfigureAwait(false),
                Pipeline = await store.PutAsync(pipeline.Document, cancellationToken).ConfigureAwait(false),
                Tables = tables,
            },
            Experiments = experiments,
        };
    }

    /// <summary>
    /// The emergency stop of design 10.6: <paramref name="manifest"/> without <paramref name="experiments"/>, as the next
    /// release (its <c>seq</c> one more, <paramref name="manifestId"/> its <c>previous</c>) that revokes every earlier one
    /// (<c>min_accepted_seq</c> its own <c>seq</c>), so a node that restarts does not bring them back from a last-known-good
    /// manifest. Nothing is compiled or validated: removing experiments keeps a valid release valid (design 11.6), which is
    /// what makes the stop take seconds. The artifacts are read as a node reads them. What the release requires loses what
    /// the removed experiments required (<see cref="ManifestExperiment.Requires"/>), so removing the experiment that needs a
    /// capability a rolled-back binary lacks makes the release one it serves (design 10.4). Null after reporting errors.
    /// </summary>
    /// <param name="experiments">Experiments of the manifest; a name it does not release changes nothing.</param>
    public static async ValueTask<ReleaseManifest?> RevokeAsync(
        ReleaseManifest manifest,
        string manifestId,
        IReadOnlyCollection<string> experiments,
        IArtifactStore store,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken = default)
    {
        if (await LoadAsync(manifest, manifestId, store, diagnostics, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var revoked = new HashSet<string>(experiments, StringComparer.Ordinal);
        var entries = new Dictionary<string, ManifestExperiment>(StringComparer.Ordinal);
        foreach (var (name, entry) in manifest.Experiments)
        {
            if (!revoked.Contains(name))
            {
                entries[name] = entry;
            }
        }

        var seq = manifest.Seq + 1;
        return new ReleaseManifest
        {
            PipelineId = manifest.PipelineId,
            Environment = manifest.Environment,
            Seq = seq,
            MinAcceptedSeq = seq,
            Previous = manifestId,
            Requires = manifest.Requires,
            Artifacts = manifest.Artifacts,
            Experiments = entries,
        };
    }

    /// <summary>
    /// Downloads a manifest's artifacts and assembles the compile request (design 10.3 step 1). Missing or corrupt
    /// blobs are errors; nothing is half-loaded.
    /// </summary>
    public static async ValueTask<PlanCompileRequest?> LoadAsync(
        ReleaseManifest manifest,
        string manifestId,
        IArtifactStore store,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken = default)
    {
        var label = Label(manifestId);
        var pipeline = await FetchAsync<PipelineArtifact>(store, manifest.Artifacts.Pipeline, label, "/artifacts/pipeline", diagnostics, cancellationToken).ConfigureAwait(false);
        var catalog = await FetchAsync<FeatureCatalogArtifact>(store, manifest.Artifacts.Catalog, label, "/artifacts/catalog", diagnostics, cancellationToken).ConfigureAwait(false);
        var bindings = await FetchAsync<SourceBindingArtifact>(store, manifest.Artifacts.Bindings, label, "/artifacts/bindings", diagnostics, cancellationToken).ConfigureAwait(false);

        // Content hashes prove integrity, not identity: a valid blob of another pipeline or experiment must not be served
        // under this manifest's names.
        if (pipeline is not null && pipeline.PipelineId != manifest.PipelineId)
        {
            diagnostics.Error("MANIFEST_IDENTITY", label, "/artifacts/pipeline", $"The pipeline artifact defines '{pipeline.PipelineId}', not '{manifest.PipelineId}'.");
        }

        var tables = new List<DataTableArtifact>();
        foreach (var (name, id) in manifest.Artifacts.Tables)
        {
            var pointer = "/artifacts/tables/" + JsonPointer.Escape(name);
            if (await FetchAsync<DataTableArtifact>(store, id, label, pointer, diagnostics, cancellationToken).ConfigureAwait(false) is { } table)
            {
                if (table.TableId != name)
                {
                    diagnostics.Error("MANIFEST_IDENTITY", label, pointer, $"The table artifact defines '{table.TableId}', not '{name}'.");
                }

                tables.Add(table);
            }
        }

        var placements = new List<ExperimentPlacement>();
        foreach (var (name, entry) in manifest.Experiments)
        {
            var pointer = "/experiments/" + JsonPointer.Escape(name) + "/artifact";
            if (await FetchAsync<ExperimentArtifact>(store, entry.Artifact, label, pointer, diagnostics, cancellationToken).ConfigureAwait(false) is { } experiment)
            {
                if (experiment.ExperimentId != name)
                {
                    diagnostics.Error("MANIFEST_IDENTITY", label, pointer, $"The experiment artifact defines '{experiment.ExperimentId}', not '{name}'.");
                }

                placements.Add(new ExperimentPlacement(experiment, entry.Layer));
            }
        }

        if (pipeline is null || catalog is null || bindings is null || diagnostics.HasErrors)
        {
            return null;
        }

        var artifactIds = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["pipeline:" + pipeline.PipelineId] = manifest.Artifacts.Pipeline,
            ["catalog:" + catalog.CatalogId] = manifest.Artifacts.Catalog,
            ["bindings:" + bindings.Environment] = manifest.Artifacts.Bindings,
        };
        foreach (var (name, id) in manifest.Artifacts.Tables)
        {
            artifactIds["table:" + name] = id;
        }

        foreach (var (name, entry) in manifest.Experiments)
        {
            artifactIds["experiment:" + name] = entry.Artifact;
        }

        return new PlanCompileRequest
        {
            Pipeline = pipeline,
            Catalog = catalog,
            Bindings = bindings,
            Tables = tables,
            Experiments = placements,
            ManifestId = manifestId,
            ArtifactIds = artifactIds,
        };
    }

    /// <summary>
    /// The content id of a manifest, as <c>stratarec build</c> stores it: the RFC 8785 hash of the manifest as the artifact
    /// model serializes it, so a manifest file that was reformatted or commented keeps the id of the blob build stored.
    /// </summary>
    public static string ContentId(ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return ContentHash.Compute(ArtifactSerializer.ToNode(manifest));
    }

    /// <summary>
    /// Reads the manifest stored under <paramref name="manifestId"/> (design 10.3 step 1): what a node does with the id a
    /// pointer names. Returns null after reporting <c>MANIFEST_ID_INVALID</c>, <c>MANIFEST_UNREADABLE</c> (the store
    /// failed, or the blob is damaged), <c>MANIFEST_UNKNOWN</c> (the store does not have it) or <c>MANIFEST_SCHEMA</c>, at
    /// the artifact <c>manifest:&lt;id&gt;</c>. Cancellation and failures of the store other than I/O propagate.
    /// </summary>
    public static async ValueTask<ReleaseManifest?> ReadAsync(
        IArtifactStore store, string manifestId, DiagnosticBag diagnostics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifestId);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var label = Label(manifestId);
        if (!ContentHash.IsValid(manifestId))
        {
            diagnostics.Error("MANIFEST_ID_INVALID", label, string.Empty, $"'{manifestId}' is not a manifest content id.",
                "a manifest id is sha256:<64 lowercase hex digits>, as 'stratarec build' prints it");
            return null;
        }

        byte[]? bytes;
        try
        {
            bytes = await store.TryGetAsync(manifestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArtifactIntegrityException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("MANIFEST_UNREADABLE", label, string.Empty, $"The manifest could not be read: {ex.Message}",
                ex is ArtifactIntegrityException
                    ? "the blob is damaged: upload the manifest again (stratarec build --store), or read it from a copy of the store"
                    : "check that the artifact store can be read");
            return null;
        }

        if (bytes is null)
        {
            diagnostics.Error("MANIFEST_UNKNOWN", label, string.Empty, $"The artifact store has no manifest '{manifestId}'.",
                "check the id and that the store is the one 'stratarec build --store' uploaded the manifest to");
            return null;
        }

        return Parse(Encoding.UTF8.GetString(bytes), label, diagnostics);
    }

    /// <summary>
    /// Deserializes a manifest's JSON; <paramref name="source"/> labels its diagnostics (a file path, or
    /// <c>manifest:&lt;id&gt;</c>). Returns null after reporting <c>MANIFEST_SCHEMA</c> at the pointer at fault.
    /// </summary>
    public static ReleaseManifest? Parse(string json, string source, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(diagnostics);
        try
        {
            return ArtifactSerializer.Deserialize<ReleaseManifest>(json, source);
        }
        catch (ArtifactFormatException ex)
        {
            diagnostics.Error("MANIFEST_SCHEMA", source, ex.Pointer, ex.Reason,
                ex.Hint ?? "a release manifest has pipeline_id, environment, artifacts and experiments, as 'stratarec build' writes it (design 10.2)");
            return null;
        }
    }

    /// <summary>
    /// The capabilities a manifest requires that <paramref name="environment"/> does not provide (design 10.4 item 2),
    /// sorted (ordinal) and without duplicates; empty when the binary can serve it.
    /// </summary>
    public static List<string> MissingCapabilities(ReleaseManifest manifest, PlanEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(environment);
        var provided = new HashSet<string>(environment.Capabilities(), StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var capability in RequiredCapabilities(manifest))
        {
            if (!provided.Contains(capability))
            {
                missing.Add(capability);
            }
        }

        return missing;
    }

    /// <summary>Every capability a manifest requires, the baseline's and each experiment's, sorted (ordinal) and without
    /// duplicates (design 10.4).</summary>
    public static SortedSet<string> RequiredCapabilities(ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var required = new SortedSet<string>(manifest.Requires, StringComparer.Ordinal);
        foreach (var (_, experiment) in manifest.Experiments)
        {
            required.UnionWith(experiment.Requires);
        }

        return required;
    }

    /// <summary>
    /// Checks that <paramref name="environment"/> provides every capability the manifest requires, and reports
    /// <c>MANIFEST_INCOMPATIBLE</c> at <c>manifest:&lt;id&gt;/requires</c> naming each one it lacks otherwise. Returns the
    /// missing capabilities (<see cref="MissingCapabilities"/>). The engine (<see cref="RecEngine.LoadManifestAsync"/>) and
    /// the command line check here, so a node and <c>stratarec</c> report an incompatible manifest the same way.
    /// </summary>
    public static List<string> CheckCapabilities(ReleaseManifest manifest, string manifestId, PlanEnvironment environment, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var missing = MissingCapabilities(manifest, environment);
        if (missing.Count > 0)
        {
            diagnostics.Error("MANIFEST_INCOMPATIBLE", Label(manifestId), "/requires",
                $"The manifest requires {(missing.Count == 1 ? "a capability" : "capabilities")} this binary does not provide: {string.Join(", ", missing)}.",
                "serve it with a binary that registers them ('stratarec operators' lists what a binary provides), or release a manifest built without them (design 10.4)");
        }

        return missing;
    }

    /// <summary>
    /// Checks that the manifest a pointer names is one of the pointer's pipeline and environment, and reports
    /// <c>MANIFEST_OTHER_PIPELINE</c> at <c>/pipeline_id</c> or <c>MANIFEST_OTHER_ENVIRONMENT</c> at <c>/environment</c>
    /// otherwise. A content hash proves integrity, not identity: a node must not serve another pipeline's plan under this
    /// one's name, and <c>stratarec build</c> and <c>publish</c> must not number a release after another pipeline's (review
    /// 37). Returns whether it is.
    /// </summary>
    public static bool CheckIdentity(ReleaseManifest manifest, string manifestId, string pipelineId, string environment, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var errors = diagnostics.ErrorCount;
        if (manifest.PipelineId != pipelineId)
        {
            diagnostics.Error("MANIFEST_OTHER_PIPELINE", Label(manifestId), "/pipeline_id",
                $"The pointer of pipeline '{pipelineId}' names a manifest of pipeline '{manifest.PipelineId}'.",
                "point it at a manifest of its own pipeline: 'stratarec publish' writes the pointer the manifest names");
        }

        if (manifest.Environment != environment)
        {
            diagnostics.Error("MANIFEST_OTHER_ENVIRONMENT", Label(manifestId), "/environment",
                $"The pointer of environment '{environment}' names a manifest built for environment '{manifest.Environment}'.",
                $"point it at a manifest built for this environment (stratarec build -e {environment}), as 'stratarec publish' does");
        }

        return diagnostics.ErrorCount == errors;
    }

    /// <summary>The diagnostic artifact of a manifest: <c>manifest:&lt;id&gt;</c>.</summary>
    internal static string Label(string manifestId) => "manifest:" + manifestId;

    private static async ValueTask<T?> FetchAsync<T>(IArtifactStore store, string id, string label, string pointer, DiagnosticBag diagnostics, CancellationToken cancellationToken)
        where T : class
    {
        byte[]? bytes;
        try
        {
            bytes = await store.TryGetAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or ArtifactIntegrityException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("ARTIFACT_UNAVAILABLE", label, pointer, $"Artifact '{id}' could not be read: {ex.Message}",
                ex switch
                {
                    ArtifactIntegrityException =>
                        "the stored blob is damaged: upload the artifact again ('stratarec build --store' rewrites it), or read it from a copy of the store",
                    UnauthorizedAccessException => "this process may not read the store's blob: check its permissions",
                    _ => null,
                });
            return null;
        }

        if (bytes is null)
        {
            diagnostics.Error("ARTIFACT_MISSING", label, pointer, $"Artifact '{id}' is not in the store.", "run 'stratarec build' to upload it");
            return null;
        }

        try
        {
            return ArtifactSerializer.Deserialize<T>(Encoding.UTF8.GetString(bytes), id);
        }
        catch (ArtifactFormatException ex)
        {
            diagnostics.Error("ARTIFACT_SCHEMA", label, pointer,
                $"Artifact '{id}' does not match its schema{(ex.Pointer.Length > 0 ? " at " + ex.Pointer : string.Empty)}: {ex.Reason}", ex.Hint);
            return null;
        }
    }
}
