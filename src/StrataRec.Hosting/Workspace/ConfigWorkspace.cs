using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting.Workspace;

/// <summary>An artifact loaded from a file: the typed model, its JSON document and its content id.</summary>
/// <param name="Value">The strictly deserialized model.</param>
/// <param name="Document">The parsed JSON (comments and the editor's top-level <c>$schema</c> stripped); what is canonicalized and hashed.</param>
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
/// ├── policy.jsonc                      owners: who reviews experiments touching what (optional; review only)
/// ├── catalog/&lt;catalog_id&gt;.jsonc
/// ├── bindings/&lt;environment&gt;.jsonc
/// ├── pipelines/&lt;pipeline_id&gt;.jsonc
/// ├── experiments/&lt;experiment_id&gt;.jsonc
/// ├── releases/&lt;pipeline_id&gt;.&lt;environment&gt;.jsonc     catalog, bindings, tables, experiment layers
/// ├── tables/&lt;table_id&gt;.jsonc
/// └── schemas/&lt;kind&gt;.schema.json        JSON Schemas the files name in "$schema" for editors (not read here)
/// </code>
/// File names must match the ids inside, so a path always tells you what a file defines; a file may end in
/// <c>.jsonc</c> or <c>.json</c>, and one artifact defined by two files is an error. A file's top-level
/// <c>$schema</c> is taken out before it is bound and hashed (<see cref="ArtifactSchemas.TryRemoveSchemaMember"/>).
/// Every problem is reported as a diagnostic with the file and a JSON pointer, never as an exception.
/// </remarks>
public sealed class ConfigWorkspace
{
    /// <summary>The policy's file names, in the order <see cref="Files"/> sorts those of a directory (ordinal).</summary>
    private static readonly string[] PolicyFiles = ["policy.json", "policy.jsonc"];

    private ConfigWorkspace(string root) => Root = root;

    public string Root { get; }

    public LoadedArtifact<PolicyArtifact>? Policy { get; private set; }

    public SortedDictionary<string, LoadedArtifact<FeatureCatalogArtifact>> Catalogs { get; } = new(StringComparer.Ordinal);

    /// <summary>Bindings by environment.</summary>
    public SortedDictionary<string, LoadedArtifact<SourceBindingArtifact>> Bindings { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, LoadedArtifact<PipelineArtifact>> Pipelines { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, LoadedArtifact<ExperimentArtifact>> Experiments { get; } = new(StringComparer.Ordinal);

    /// <summary>Release files by <c>pipeline_id/environment</c>.</summary>
    public SortedDictionary<string, LoadedArtifact<ReleaseArtifact>> Releases { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, LoadedArtifact<DataTableArtifact>> Tables { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads every artifact under <paramref name="root"/>. Problems go to <paramref name="diagnostics"/>: besides each file's
    /// own, two files that define the same artifact (<c>pipelines/home_feed.json</c> and <c>pipelines/home_feed.jsonc</c>),
    /// and a workspace without a pipeline. Validate, build, the other commands and a host serving the workspace all read it
    /// through here, so none of them uses one of two definitions, or serves nothing, without saying so.
    /// </summary>
    public static ConfigWorkspace Load(string root, DiagnosticBag diagnostics)
    {
        var workspace = new ConfigWorkspace(Path.GetFullPath(root));
        if (!Directory.Exists(workspace.Root))
        {
            diagnostics.Error("WORKSPACE_MISSING", root, string.Empty, "Configuration directory does not exist.");
            return workspace;
        }

        foreach (var name in PolicyFiles)
        {
            var path = Path.Combine(workspace.Root, name);
            if (File.Exists(path) && workspace.Read<PolicyArtifact>(path, diagnostics) is { } a)
            {
                if (workspace.Policy is { } first)
                {
                    Duplicate(first.Path, a.Path, "the policy", string.Empty, diagnostics);
                }
                else
                {
                    workspace.Policy = a;
                }
            }
        }

        foreach (var file in workspace.Files("catalog"))
        {
            if (workspace.Read<FeatureCatalogArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.CatalogId, "/catalog_id", diagnostics))
            {
                Index(workspace.Catalogs, a.Value.CatalogId, a, $"catalog '{a.Value.CatalogId}'", "/catalog_id", diagnostics);
            }
        }

        foreach (var file in workspace.Files("bindings"))
        {
            if (workspace.Read<SourceBindingArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.Environment, "/environment", diagnostics))
            {
                Index(workspace.Bindings, a.Value.Environment, a, $"the bindings of environment '{a.Value.Environment}'", "/environment", diagnostics);
            }
        }

        var pipelineFiles = workspace.Files("pipelines");
        foreach (var file in pipelineFiles)
        {
            if (workspace.Read<PipelineArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.PipelineId, "/pipeline_id", diagnostics))
            {
                Index(workspace.Pipelines, a.Value.PipelineId, a, $"pipeline '{a.Value.PipelineId}'", "/pipeline_id", diagnostics);
            }
        }

        if (pipelineFiles.Count == 0)
        {
            // Nothing to validate, build or serve: without this, validate passed and a host started that answered nothing.
            diagnostics.Error("WORKSPACE_NO_PIPELINE", workspace.Root, string.Empty,
                $"The workspace {workspace.Root} has no pipeline: {Path.Combine(workspace.Root, "pipelines")} holds no .json or .jsonc file.",
                "add pipelines/<pipeline_id>.jsonc, with catalog/, bindings/ and releases/ beside it ('stratarec init' writes a workspace to start from), " +
                "or point -c (StrataRecOptions.ConfigRoot for a host) at the workspace directory");
        }

        foreach (var file in workspace.Files("experiments"))
        {
            if (workspace.Read<ExperimentArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.ExperimentId, "/experiment_id", diagnostics))
            {
                Index(workspace.Experiments, a.Value.ExperimentId, a, $"experiment '{a.Value.ExperimentId}'", "/experiment_id", diagnostics);
            }
        }

        foreach (var file in workspace.Files("releases"))
        {
            if (workspace.Read<ReleaseArtifact>(file, diagnostics) is { } a
                && workspace.CheckName(a, a.Value.PipelineId + "." + a.Value.Environment, "/environment", diagnostics))
            {
                Index(workspace.Releases, a.Value.PipelineId + "/" + a.Value.Environment, a,
                    $"the release of pipeline '{a.Value.PipelineId}' in environment '{a.Value.Environment}'", "/environment", diagnostics);
            }
        }

        foreach (var file in workspace.Files("tables"))
        {
            if (workspace.Read<DataTableArtifact>(file, diagnostics) is { } a && workspace.CheckName(a, a.Value.TableId, "/table_id", diagnostics))
            {
                Index(workspace.Tables, a.Value.TableId, a, $"table '{a.Value.TableId}'", "/table_id", diagnostics);
            }
        }

        return workspace;
    }

    /// <summary>
    /// Adds an artifact under its id, unless another file defined it already: then the later file is an error naming
    /// both, and the first stays (nothing is compiled or served from a workspace with errors). A file is named by its id
    /// with either extension, so <c>home_feed.json</c> and <c>home_feed.jsonc</c> both define <c>home_feed</c>; taking the
    /// last, as assigning by id did, validated a workspace with one of them while the other was silently ignored.
    /// </summary>
    private static void Index<T>(
        SortedDictionary<string, LoadedArtifact<T>> index, string id, LoadedArtifact<T> artifact, string what, string pointer, DiagnosticBag diagnostics)
    {
        if (index.TryGetValue(id, out var first))
        {
            Duplicate(first.Path, artifact.Path, what, pointer, diagnostics);
            return;
        }

        index[id] = artifact;
    }

    private static void Duplicate(string first, string second, string what, string pointer, DiagnosticBag diagnostics) =>
        diagnostics.Error("ARTIFACT_DUPLICATE", second, pointer, $"Both {first} and {second} define {what}; only one file may.",
            $"keep one of the two files and delete the other ({first} or {second})");

    /// <summary>
    /// The pipelines to compile or load for <paramref name="ids"/>: those, then the <c>fallback_pipeline</c> of each that is
    /// not among them, since a fallback that is not served silently never happens (design 5.6). A fallback not in the
    /// workspace is an error at the pipeline's <c>/fallback_pipeline</c>, and the pipeline is added to
    /// <paramref name="unresolved"/>. Loading and <c>validate</c> both use it, so they agree on what is served.
    /// </summary>
    public List<string> WithFallbacks(IEnumerable<string> ids, DiagnosticBag diagnostics, out HashSet<string> unresolved)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var all = new List<string>(ids);
        unresolved = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < all.Count; i++)
        {
            if (Pipelines.TryGetValue(all[i], out var loaded) && loaded.Value.FallbackPipeline is { } fallback && !all.Contains(fallback))
            {
                if (Pipelines.ContainsKey(fallback))
                {
                    all.Add(fallback);
                }
                else
                {
                    diagnostics.Error("FALLBACK_PIPELINE_UNKNOWN", loaded.Path, "/fallback_pipeline", $"Fallback pipeline '{fallback}' is not in the workspace.",
                        NameSuggestions.DidYouMean(fallback, Pipelines.Keys) ?? $"create pipelines/{fallback}.jsonc or remove fallback_pipeline");
                    unresolved.Add(all[i]);
                }
            }
        }

        return all;
    }

    /// <summary>
    /// Warns, for each of <paramref name="ids"/> with a fallback pipeline, about inputs the fallback would fail requests
    /// on (<see cref="RequestSchema.CheckFallback"/>); <paramref name="schemaOf"/> gives a pipeline's compiled schema,
    /// null when it did not compile (its errors are reported already).
    /// </summary>
    public void CheckFallbackInputs(IEnumerable<string> ids, Func<string, RequestSchema?> schemaOf, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(schemaOf);
        foreach (var id in ids)
        {
            if (Pipelines.TryGetValue(id, out var loaded) && loaded.Value.FallbackPipeline is { } fallback && fallback != id
                && schemaOf(id) is { } primary && schemaOf(fallback) is { } served)
            {
                RequestSchema.CheckFallback(primary, served, loaded.Path, diagnostics);
            }
        }
    }

    /// <summary>
    /// Assembles the compile inputs for one pipeline in one environment: the release file decides the catalog,
    /// bindings, tables and which experiments run in which layer. Without a release file the baseline is
    /// compiled alone against <c>bindings/&lt;environment&gt;.jsonc</c>.
    /// </summary>
    /// <param name="extraExperiments">Experiments to include although the release file does not list them (draft preview, design 10.7), as id → layer.</param>
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

        Releases.TryGetValue(pipelineId + "/" + environment, out var release);
        var releaseLabel = release?.Path ?? "releases/" + pipelineId + "." + environment + ".jsonc";

        LoadedArtifact<FeatureCatalogArtifact>? catalog = null;
        if (release?.Value.Catalog is { } catalogId)
        {
            if (!Catalogs.TryGetValue(catalogId, out catalog))
            {
                diagnostics.Error("CATALOG_UNKNOWN", releaseLabel, "/catalog", $"No catalog '{catalogId}'.", NameSuggestions.DidYouMean(catalogId, Catalogs.Keys));
                return null;
            }
        }
        else if (!Catalogs.TryGetValue(pipelineId, out catalog))
        {
            if (Catalogs.Count != 1)
            {
                diagnostics.Error("CATALOG_AMBIGUOUS", releaseLabel, "/catalog",
                    Catalogs.Count == 0 ? "The workspace has no feature catalog." : $"Several catalogs exist and none is named '{pipelineId}'.",
                    "name the catalog in the release file (\"catalog\": \"<catalog_id>\")");
                return null;
            }

            foreach (var (_, only) in Catalogs)
            {
                catalog = only;
            }
        }

        var bindingsEnvironment = release?.Value.Bindings ?? environment;
        if (!Bindings.TryGetValue(bindingsEnvironment, out var bindings))
        {
            diagnostics.Error("BINDINGS_UNKNOWN", releaseLabel, "/bindings", $"No bindings for environment '{bindingsEnvironment}'.",
                NameSuggestions.DidYouMean(bindingsEnvironment, Bindings.Keys) ?? $"create bindings/{bindingsEnvironment}.jsonc");
            return null;
        }

        var tables = new List<DataTableArtifact>();
        if (release is not null && release.Value.Tables.Count > 0)
        {
            for (var i = 0; i < release.Value.Tables.Count; i++)
            {
                var id = release.Value.Tables[i];
                if (Tables.TryGetValue(id, out var table))
                {
                    tables.Add(table.Value);
                }
                else
                {
                    diagnostics.Error("TABLE_UNKNOWN", releaseLabel, "/tables/" + i, $"No table '{id}'.", NameSuggestions.DidYouMean(id, Tables.Keys));
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
        foreach (var table in tables)
        {
            artifactIds["table:" + table.TableId] = Tables[table.TableId].ContentId;
        }

        var placements = new List<ExperimentPlacement>();
        var listed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (release is not null)
        {
            foreach (var (id, entry) in release.Value.Experiments)
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
                diagnostics.Error("EXPERIMENT_UNKNOWN", releaseLabel, "/experiments/" + JsonPointer.Escape(id), $"No experiment '{id}'.",
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
            // No pointer to a value that does not parse: the position is the reader's.
            diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, ex.Code, relative, string.Empty, ex.Reason, ex.Hint) { Line = ex.Line, Column = ex.Column });
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

        // "$schema" is the editor's: not part of the artifact, so neither bound nor hashed.
        var kind = ArtifactSerializer.ArtifactName(typeof(T));
        if (!ArtifactSchemas.TryRemoveSchemaMember(document, kind is null ? null : ArtifactSchemas.Reference(relative, kind), out var schemaError))
        {
            diagnostics.Error("ARTIFACT_SCHEMA", relative, schemaError.Pointer, schemaError.Message, schemaError.Hint);
            return null;
        }

        T value;
        try
        {
            value = ArtifactSerializer.Deserialize<T>(document, relative);
        }
        catch (ArtifactFormatException ex)
        {
            diagnostics.Error("ARTIFACT_SCHEMA", relative, ex.Pointer, ex.Reason,
                ex.Hint ?? $"run 'stratarec schema {kind}' for the JSON Schema of this file");
            return null;
        }

        // The content id hashes the RFC 8785 form, and every later hash (of a plan's nodes, of the store's blobs, of a
        // manifest) is of what this document holds: a value that form cannot hold exactly (an integer beyond 2^53 that would
        // read back as another) is this file's error, here, not an exception out of validate, build or a host's startup.
        string contentId;
        try
        {
            contentId = ContentHash.Compute(document);
        }
        catch (JsonCanonicalizationException ex)
        {
            diagnostics.Error("ARTIFACT_NOT_CANONICAL", relative, ex.Pointer.ToString(),
                $"{ex.Reason} An artifact is identified by the hash of its canonical JSON (RFC 8785), which must hold every value exactly.",
                ex.Hint ?? "write the value so that RFC 8785 holds it exactly");
            return null;
        }

        return new LoadedArtifact<T>(value, document, relative, contentId);
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

    /// <summary>
    /// The workspace-relative path of the file a diagnostic's artifact label names, for tools that print diagnostics with
    /// a file, line and column: <c>pipeline:home_feed</c> is <c>pipelines/home_feed.jsonc</c> (a file is named by the id
    /// inside it, which loading checks), <c>policy</c> is <c>policy.jsonc</c>, and a label that is a path (as loading and
    /// release errors use) is that file. The whole label is matched first, since an id may contain spaces; only when it
    /// names no file is the arm combination an error was found in (<c>pipeline:home_feed with exp_a/x + exp_b/y</c>)
    /// taken off. Null for a label that names no file of the workspace (a manifest, the workspace as a whole) or a file
    /// that does not exist: never another file.
    /// </summary>
    /// <param name="root">The workspace directory.</param>
    /// <param name="artifact">The diagnostic's <see cref="ConfigDiagnostic.Artifact"/>.</param>
    /// <param name="exact">True when <paramref name="artifact"/> is only the file's label, so the file says all it says.</param>
    public static string? FileOf(string root, string artifact, out bool exact)
    {
        exact = true;
        if (FileOfLabel(root, artifact) is { } file)
        {
            return file;
        }

        exact = false;
        return WithoutCombination(artifact) is { } label ? FileOfLabel(root, label) : null;
    }

    /// <summary>
    /// The label before <c> with exp_a/x + exp_b/y</c>, the arms of a combination as the plan compiler appends them, or
    /// null when the label does not end so. Every arm id is <c>experiment/arm</c> without spaces.
    /// </summary>
    private static string? WithoutCombination(string artifact)
    {
        const string With = " with ";
        var at = artifact.LastIndexOf(With, StringComparison.Ordinal);
        if (at <= 0)
        {
            return null;
        }

        foreach (var arm in artifact[(at + With.Length)..].Split(" + "))
        {
            var slash = arm.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == arm.Length - 1 || arm.IndexOf('/', slash + 1) >= 0 || arm.Contains(' ', StringComparison.Ordinal))
            {
                return null;
            }
        }

        return artifact[..at];
    }

    private static string? FileOfLabel(string root, string label)
    {
        var colon = label.IndexOf(':', StringComparison.Ordinal);
        string stem;
        if (colon > 0)
        {
            var directory = label[..colon] switch
            {
                "pipeline" => "pipelines",
                "experiment" => "experiments",
                "catalog" => "catalog",
                "bindings" => "bindings",
                "table" => "tables",
                _ => null,
            };
            var id = label[(colon + 1)..];
            if (directory is null || id.Length == 0 || id.IndexOfAny(['/', '\\']) >= 0 || id is "." or "..")
            {
                return null;
            }

            stem = directory + "/" + id;
        }
        else if (label == "policy")
        {
            stem = "policy";
        }
        else
        {
            return label.EndsWith(".jsonc", StringComparison.Ordinal) || label.EndsWith(".json", StringComparison.Ordinal)
                ? Existing(root, label)
                : null;
        }

        return Existing(root, stem + ".jsonc") ?? Existing(root, stem + ".json");

        static string? Existing(string root, string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var top = Path.GetFullPath(root);
            return full.StartsWith(Path.TrimEndingDirectorySeparator(top) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full)
                ? relative
                : null;
        }
    }
}
