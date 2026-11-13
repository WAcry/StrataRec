using System.Text;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;

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
    public static async ValueTask<ReleaseManifest?> BuildAsync(
        ConfigWorkspace workspace,
        string pipelineId,
        string environment,
        IArtifactStore store,
        DiagnosticBag diagnostics,
        long seq = 1,
        CancellationToken cancellationToken = default)
    {
        var request = workspace.CreateCompileRequest(pipelineId, environment, diagnostics);
        if (request is null)
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
        var ranges = workspace.ArmRanges(pipelineId, environment);
        foreach (var placement in request.Experiments)
        {
            var id = placement.Experiment.ExperimentId;
            experiments[id] = new ManifestExperiment
            {
                Artifact = await store.PutAsync(workspace.Experiments[id].Document, cancellationToken).ConfigureAwait(false),
                Layer = placement.Layer,
                Arms = ranges.TryGetValue(id, out var arms) ? new Dictionary<string, int[]>(arms) : null,
            };
        }

        var policy = workspace.Policy is { } p ? await store.PutAsync(p.Document, cancellationToken).ConfigureAwait(false) : null;
        return new ReleaseManifest
        {
            PipelineId = pipelineId,
            Environment = environment,
            Seq = seq,
            MinAcceptedSeq = 0,
            Requires = RequiredCapabilities(request),
            Artifacts = new ManifestArtifacts
            {
                Catalog = await store.PutAsync(catalog!.Document, cancellationToken).ConfigureAwait(false),
                Bindings = await store.PutAsync(bindings!.Document, cancellationToken).ConfigureAwait(false),
                Pipeline = await store.PutAsync(pipeline.Document, cancellationToken).ConfigureAwait(false),
                Tables = tables,
                Policy = policy,
            },
            Assignment = request.Assignment ?? new AssignmentSpec(),
            Experiments = experiments,
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
        var label = "manifest:" + manifestId;
        var pipeline = await FetchAsync<PipelineArtifact>(store, manifest.Artifacts.Pipeline, label, "/artifacts/pipeline", diagnostics, cancellationToken).ConfigureAwait(false);
        var catalog = await FetchAsync<FeatureCatalogArtifact>(store, manifest.Artifacts.Catalog, label, "/artifacts/catalog", diagnostics, cancellationToken).ConfigureAwait(false);
        var bindings = await FetchAsync<SourceBindingArtifact>(store, manifest.Artifacts.Bindings, label, "/artifacts/bindings", diagnostics, cancellationToken).ConfigureAwait(false);
        var policy = manifest.Artifacts.Policy is { } policyId
            ? await FetchAsync<PolicyArtifact>(store, policyId, label, "/artifacts/policy", diagnostics, cancellationToken).ConfigureAwait(false)
            : new PolicyArtifact();

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

        if (pipeline is null || catalog is null || bindings is null || policy is null || diagnostics.HasErrors)
        {
            return null;
        }

        return new PlanCompileRequest
        {
            Pipeline = pipeline,
            Catalog = catalog,
            Bindings = bindings,
            Tables = tables,
            Policy = policy,
            Experiments = placements,
            Assignment = manifest.Assignment,
            ManifestId = manifestId,
        };
    }

    /// <summary>Arm bucket ranges from a manifest: experiment → arm → [first, last].</summary>
    public static Dictionary<string, IReadOnlyDictionary<string, int[]>> ArmRanges(ReleaseManifest manifest)
    {
        var ranges = new Dictionary<string, IReadOnlyDictionary<string, int[]>>(StringComparer.Ordinal);
        foreach (var (id, entry) in manifest.Experiments)
        {
            if (entry.Arms is not null)
            {
                ranges[id] = entry.Arms;
            }
        }

        return ranges;
    }

    /// <summary>Operators, connectors and clients named by the configuration (design 10.4).</summary>
    private static List<string> RequiredCapabilities(PlanCompileRequest request)
    {
        var required = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, node) in request.Pipeline.Nodes)
        {
            required.Add(node.Op);
        }

        foreach (var placement in request.Experiments)
        {
            foreach (var (_, patch) in placement.Experiment.Arms)
            {
                if (patch["nodes"] is JsonObject nodes)
                {
                    foreach (var (_, node) in nodes)
                    {
                        if (node?["op"] is JsonValue op && op.TryGetValue<string>(out var id))
                        {
                            required.Add(id);
                        }
                    }
                }
            }
        }

        foreach (var (_, source) in request.Bindings.Sources)
        {
            if (source.Connector is { } connector)
            {
                required.Add(connector);
            }
        }

        foreach (var (_, model) in request.Bindings.Models)
        {
            required.Add(model.Client);
        }

        foreach (var (_, source) in request.Bindings.CandidateSources)
        {
            required.Add(source.Client);
        }

        required.Add(Expressions.ExpressionCompiler.FunctionSetCapability);
        return new List<string>(required);
    }

    private static async ValueTask<T?> FetchAsync<T>(IArtifactStore store, string id, string label, string pointer, DiagnosticBag diagnostics, CancellationToken cancellationToken)
        where T : class
    {
        byte[]? bytes;
        try
        {
            bytes = await store.TryGetAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or ArtifactIntegrityException or IOException)
        {
            diagnostics.Error("ARTIFACT_UNAVAILABLE", label, pointer, $"Artifact '{id}' could not be read: {ex.Message}");
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
            diagnostics.Error("ARTIFACT_SCHEMA", label, pointer, ex.Message);
            return null;
        }
    }
}
