using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;

namespace StrataRec.Hosting.Workspace;

/// <summary>An artifact loaded from a file: the typed model, its JSON document and its content id.</summary>
/// <param name="Value">The strictly deserialized model.</param>
/// <param name="Document">The parsed JSON (comments stripped); what is canonicalized and hashed.</param>
/// <param name="Path">Path relative to the workspace root, used as the diagnostic artifact name.</param>
/// <param name="ContentId">RFC 8785 + sha256 content id (design 9.1).</param>
public sealed record LoadedArtifact<T>(T Value, JsonNode Document, string Path, string ContentId);

/// <summary>
/// A configuration repository checked out on disk (design 10.1): the source of truth that CI validates and builds,
/// and what <c>stratarec run</c> executes locally.
/// </summary>
/// <remarks>
/// <code>
/// config/
/// ├── policy.jsonc                      patchable paths, owners (optional; defaults apply)
/// ├── catalog/&lt;catalog_id&gt;.jsonc
/// ├── bindings/&lt;environment&gt;.jsonc
/// ├── pipelines/&lt;pipeline_id&gt;.jsonc
/// ├── experiments/&lt;experiment_id&gt;.jsonc
/// ├── allocation/&lt;pipeline_id&gt;.&lt;environment&gt;.jsonc   catalog, bindings, tables, experiment layers
/// └── tables/&lt;table_id&gt;.jsonc
/// </code>
/// File names must match the ids inside, so a path always tells you what a file defines. Every problem is reported
/// as a diagnostic with the file and a JSON pointer, never as an exception.
/// </remarks>
public sealed class ConfigWorkspace
{
    private ConfigWorkspace(string root) => Root = root;

    public string Root { get; }

    public LoadedArtifact<PolicyArtifact>? Policy { get; private set; }

    public SortedDictionary<string, LoadedArtifact<FeatureCatalogArtifact>> Catalogs { get; } = new(StringComparer.Ordinal);

    /// <summary>Bindings by environment.</summary>
    public SortedDictionary<string, LoadedArtifact<SourceBindingArtifact>> Bindings { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, LoadedArtifact<PipelineArtifact>> Pipelines { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, LoadedArtifact<ExperimentArtifact>> Experiments { get; } = new(StringComparer.Ordinal);

    /// <summary>Allocations by <c>pipeline_id/environment</c>.</summary>
    public SortedDictionary<string, LoadedArtifact<AllocationArtifact>> Allocations { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, LoadedArtifact<DataTableArtifact>> Tables { get; } = new(StringComparer.Ordinal);

    /// <summary>Loads every artifact under <paramref name="root"/>. Problems go to <paramref name="diagnostics"/>.</summary>
    public static ConfigWorkspace Load(string root, DiagnosticBag diagnostics)
    {
        var workspace = new ConfigWorkspace(Path.GetFullPath(root));
        if (!Directory.Exists(workspace.Root))
        {
            diagnostics.Error("WORKSPACE_MISSING", root, string.Empty, "Configuration directory does not exist.");
            return workspace;
        }

        var policyPath = Path.Combine(workspace.Root, "policy.jsonc");
        if (File.Exists(policyPath))
        {
            workspace.Policy = workspace.Read<PolicyArtifact>(policyPath, diagnostics);
        }

        foreach (var file in workspace.Files("catalog"))
        {
            if (workspace.Read<FeatureCatalogArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.CatalogId, "/catalog_id", diagnostics))
            {
                workspace.Catalogs[a.Value.CatalogId] = a;
            }
        }

        foreach (var file in workspace.Files("bindings"))
        {
            if (workspace.Read<SourceBindingArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.Environment, "/environment", diagnostics))
            {
                workspace.Bindings[a.Value.Environment] = a;
            }
        }

        foreach (var file in workspace.Files("pipelines"))
        {
            if (workspace.Read<PipelineArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.PipelineId, "/pipeline_id", diagnostics))
            {
                workspace.Pipelines[a.Value.PipelineId] = a;
            }
        }

        foreach (var file in workspace.Files("experiments"))
        {
            if (workspace.Read<ExperimentArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.ExperimentId, "/experiment_id", diagnostics))
            {
                workspace.Experiments[a.Value.ExperimentId] = a;
            }
        }

        foreach (var file in workspace.Files("allocation"))
        {
            if (workspace.Read<AllocationArtifact>(file, diagnostics) is { } a
                && workspace.CheckName(a, a.Value.PipelineId + "." + a.Value.Environment, "/environment", diagnostics))
            {
                workspace.Allocations[a.Value.PipelineId + "/" + a.Value.Environment] = a;
            }
        }

        foreach (var file in workspace.Files("tables"))
        {
            if (workspace.Read<DataTableArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.TableId, "/table_id", diagnostics))
            {
                workspace.Tables[a.Value.TableId] = a;
            }
        }

        return workspace;
    }

    /// <summary>
    /// Assembles the compile inputs for one pipeline in one environment: the allocation file decides the catalog,
    /// bindings, tables and which experiments run in which layer. Without an allocation the baseline is
    /// compiled alone against <c>bindings/&lt;environment&gt;.jsonc</c>.
    /// </summary>
    /// <param name="extraExperiments">Experiments to include although the allocation does not list them (draft preview, design 10.7), as id → layer.</param>
    public PlanCompileRequest? CreateCompileRequest(
        string pipelineId,
        string environment,
        DiagnosticBag diagnostics,
        DateOnly? today = null,
        IReadOnlyDictionary<string, string>? extraExperiments = null)
    {
        // Any assembly error aborts: a partial request (an experiment or table silently left out) must never be
        // compiled, loaded over a working plan, or built into a release.
        var errorsBefore = diagnostics.ErrorCount;
        if (!Pipelines.TryGetValue(pipelineId, out var pipeline))
        {
            diagnostics.Error("PIPELINE_UNKNOWN", "workspace", "/pipelines", $"No pipeline '{pipelineId}' in {Root}.",
                NameSuggestions.DidYouMean(pipelineId, Pipelines.Keys) ?? $"create pipelines/{pipelineId}.jsonc");
            return null;
        }

        Allocations.TryGetValue(pipelineId + "/" + environment, out var allocation);
        var allocationLabel = allocation?.Path ?? "allocation";

        LoadedArtifact<FeatureCatalogArtifact>? catalog = null;
        if (allocation?.Value.Catalog is { } catalogId)
        {
            if (!Catalogs.TryGetValue(catalogId, out catalog))
            {
                diagnostics.Error("CATALOG_UNKNOWN", allocationLabel, "/catalog", $"No catalog '{catalogId}'.", NameSuggestions.DidYouMean(catalogId, Catalogs.Keys));
                return null;
            }
        }
        else if (!Catalogs.TryGetValue(pipelineId, out catalog))
        {
            if (Catalogs.Count != 1)
            {
                diagnostics.Error("CATALOG_AMBIGUOUS", allocationLabel, "/catalog",
                    Catalogs.Count == 0 ? "The workspace has no feature catalog." : $"Several catalogs exist and none is named '{pipelineId}'.",
                    "name the catalog in the allocation file (\"catalog\": \"<catalog_id>\")");
                return null;
            }

            foreach (var (_, only) in Catalogs)
            {
                catalog = only;
            }
        }

        var bindingsEnvironment = allocation?.Value.Bindings ?? environment;
        if (!Bindings.TryGetValue(bindingsEnvironment, out var bindings))
        {
            diagnostics.Error("BINDINGS_UNKNOWN", allocationLabel, "/bindings", $"No bindings for environment '{bindingsEnvironment}'.",
                NameSuggestions.DidYouMean(bindingsEnvironment, Bindings.Keys) ?? $"create bindings/{bindingsEnvironment}.jsonc");
            return null;
        }

        var tables = new List<DataTableArtifact>();
        if (allocation is not null && allocation.Value.Tables.Count > 0)
        {
            for (var i = 0; i < allocation.Value.Tables.Count; i++)
            {
                var id = allocation.Value.Tables[i];
                if (Tables.TryGetValue(id, out var table))
                {
                    tables.Add(table.Value);
                }
                else
                {
                    diagnostics.Error("TABLE_UNKNOWN", allocationLabel, "/tables/" + i, $"No table '{id}'.", NameSuggestions.DidYouMean(id, Tables.Keys));
                }
            }
        }
        else
        {
            foreach (var (_, table) in Tables)
            {
                tables.Add(table.Value);
            }
        }

        var artifactIds = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["pipeline:" + pipelineId] = pipeline.ContentId,
            ["catalog:" + catalog!.Value.CatalogId] = catalog.ContentId,
            ["bindings:" + bindingsEnvironment] = bindings.ContentId,
        };
        if (Policy is { } policy)
        {
            artifactIds["policy"] = policy.ContentId;
        }

        foreach (var table in tables)
        {
            artifactIds["table:" + table.TableId] = Tables[table.TableId].ContentId;
        }

        var placements = new List<ExperimentPlacement>();
        var listed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (allocation is not null)
        {
            foreach (var (id, entry) in allocation.Value.Experiments)
            {
                listed[id] = entry.Layer;
            }
        }

        if (extraExperiments is not null)
        {
            foreach (var (id, layer) in extraExperiments)
            {
                listed[id] = layer;
            }
        }

        foreach (var (id, layer) in listed)
        {
            if (!Experiments.TryGetValue(id, out var experiment))
            {
                diagnostics.Error("EXPERIMENT_UNKNOWN", allocationLabel, "/experiments/" + JsonPointer.Escape(id), $"No experiment '{id}'.",
                    NameSuggestions.DidYouMean(id, Experiments.Keys) ?? $"create experiments/{id}.jsonc");
                continue;
            }

            placements.Add(new ExperimentPlacement(experiment.Value, layer));
            artifactIds["experiment:" + id] = experiment.ContentId;
        }

        if (diagnostics.ErrorCount > errorsBefore)
        {
            return null;
        }

        return new PlanCompileRequest
        {
            Pipeline = pipeline.Value,
            Catalog = catalog!.Value,
            Bindings = bindings.Value,
            Tables = tables,
            Policy = Policy?.Value ?? new PolicyArtifact(),
            Experiments = placements,
            ManifestId = "workspace:" + pipelineId + "/" + environment,
            ArtifactIds = artifactIds,
            Today = today,
        };
    }

    private List<string> Files(string directory)
    {
        var path = Path.Combine(Root, directory);
        var files = new List<string>();
        if (!Directory.Exists(path))
        {
            return files;
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            if (file.EndsWith(".jsonc", StringComparison.Ordinal) || file.EndsWith(".json", StringComparison.Ordinal))
            {
                files.Add(file);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private LoadedArtifact<T>? Read<T>(string file, DiagnosticBag diagnostics)
        where T : class
    {
        var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
        JsonNode? document;
        try
        {
            document = Jsonc.Parse(File.ReadAllBytes(file), relative);
        }
        catch (JsoncException ex)
        {
            var where = ex.Line is { } line ? $" (line {line}, column {ex.Column})" : string.Empty;
            diagnostics.Error(ex.Code, relative, string.Empty, ex.Reason + where, ex.Hint);
            return null;
        }
        catch (IOException ex)
        {
            diagnostics.Error("FILE_UNREADABLE", relative, string.Empty, ex.Message);
            return null;
        }

        if (document is null)
        {
            diagnostics.Error("ARTIFACT_EMPTY", relative, string.Empty, "The file contains JSON null.");
            return null;
        }

        try
        {
            var value = ArtifactSerializer.Deserialize<T>(document, relative);
            return new LoadedArtifact<T>(value, document, relative, ContentHash.Compute(document));
        }
        catch (ArtifactFormatException ex)
        {
            diagnostics.Error("ARTIFACT_SCHEMA", relative, ArtifactSerializer.JsonPathToPointer(ex.JsonPath), ex.InnerException?.Message ?? ex.Message,
                $"run 'stratarec schema {SchemaName<T>()}' for the JSON Schema of this file");
            return null;
        }
    }

    private bool CheckName<T>(LoadedArtifact<T> artifact, string id, string pointer, DiagnosticBag diagnostics)
    {
        var name = Path.GetFileName(artifact.Path);
        var stem = name.EndsWith(".jsonc", StringComparison.Ordinal) ? name[..^6] : name[..^5];
        if (string.Equals(stem, id, StringComparison.Ordinal))
        {
            return true;
        }

        diagnostics.Error("FILE_NAME_MISMATCH", artifact.Path, pointer, $"The file defines '{id}' but is named '{name}'.",
            $"rename the file to {id}.jsonc so paths identify artifacts");
        return false;
    }

    internal static string SchemaName<T>() => typeof(T).Name switch
    {
        nameof(PipelineArtifact) => "pipeline",
        nameof(FeatureCatalogArtifact) => "catalog",
        nameof(SourceBindingArtifact) => "bindings",
        nameof(ExperimentArtifact) => "experiment",
        nameof(AllocationArtifact) => "allocation",
        nameof(PolicyArtifact) => "policy",
        nameof(DataTableArtifact) => "table",
        _ => "manifest",
    };
}
