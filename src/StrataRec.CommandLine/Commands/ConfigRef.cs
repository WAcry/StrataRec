using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Workspace;

namespace StrataRec.CommandLine.Commands;

/// <summary>What a <see cref="ConfigRef"/> names.</summary>
internal enum ConfigRefKind
{
    /// <summary>A workspace directory; <c>-e</c> and <c>-p</c> select the release and pipeline in it.</summary>
    Workspace = 1,

    /// <summary>A release manifest's content id, <c>sha256:&lt;hex&gt;</c>, read from the artifact store.</summary>
    ManifestId,

    /// <summary>A release manifest file; its artifacts are read from the artifact store.</summary>
    ManifestFile,
}

/// <summary>
/// A configuration named on the command line: a workspace directory, the content id of a release manifest in an artifact
/// store, or a manifest file whose artifacts are in the store. <c>run</c>, <c>explain-plan</c>,
/// <c>diff</c>, <c>replay</c> and <c>compare</c> resolve every configuration here, so a ref means the same to each of
/// them, and a manifest compiles to the plan a node loading it would serve, under the manifest's content id.
/// </summary>
/// <remarks>
/// <para>Only <c>run</c> loads bindings (local tables, model files, snapshots), as a node does; the others need little on
/// disk but the store: only the fixtures that read their data file when created (<c>memory@1</c>, <c>memory_list@1</c>).
/// A manifest's relative paths resolve against the current directory (<see cref="PlanEnvironment"/>), a workspace's
/// against its root.</para>
/// <para>Problems with the command line itself (two configurations, a manifest without a store, a ref that names
/// nothing) are usage errors, reported by <see cref="Parse"/> and <see cref="Select"/>; problems with what a ref names (an
/// id the store lacks, a damaged blob, a manifest that does not compile) are reported when it loads.</para>
/// </remarks>
internal sealed class ConfigRef
{
    private const string StoreHint = "pass --store <dir>, the artifact store 'stratarec build --store' uploaded the manifest and its artifacts to";

    private readonly Pipeline.Planning.PlanEnvironment _provided;

    private ConfigRef(ConfigRefKind kind, string text, string? store, Pipeline.Planning.PlanEnvironment provided)
    {
        Kind = kind;
        Text = text;
        Store = store;
        _provided = provided;
    }

    public ConfigRefKind Kind { get; }

    /// <summary>The ref as given: a directory, a content id or a file path.</summary>
    public string Text { get; }

    /// <summary>The artifact store directory a manifest's artifacts are read from; null for a workspace.</summary>
    public string? Store { get; }

    public bool IsManifest => Kind != ConfigRefKind.Workspace;

    /// <summary>
    /// Locates the diagnostics of a workspace in its files (labelled with <paramref name="prefix"/> and a space when the
    /// command prefixes them); null for a manifest, whose artifacts are not files.
    /// </summary>
    public DiagnosticLocator? Locator(string prefix = "") => IsManifest ? null : new DiagnosticLocator().Add(prefix, Text);

    /// <summary>
    /// The line to print after the errors of a manifest that did not load when one names a path in the current directory:
    /// a manifest has no root, so the relative paths of its bindings resolved there, which a reader who sees only the
    /// absolute path may not expect (decisions). Null when no error names one.
    /// </summary>
    public static string? CurrentDirectoryNote(IReadOnlyList<ConfigDiagnostic> diagnostics) =>
        CurrentDirectoryNote(diagnostics, static d => d.IsError ? d.Message : null);

    /// <summary>The same for an error's text.</summary>
    public static string? CurrentDirectoryNote(string error) => CurrentDirectoryNote([error], static text => text);

    private static string? CurrentDirectoryNote<T>(IReadOnlyList<T> errors, Func<T, string?> message)
    {
        var directory = Environment.CurrentDirectory;
        foreach (var error in errors)
        {
            if (message(error) is { } text && text.Contains(directory, StringComparison.Ordinal))
            {
                return $"relative paths in a manifest's bindings resolve against the current directory, {directory}: run the command where the node runs";
            }
        }

        return null;
    }

    public override string ToString() => Text;

    /// <summary>A workspace directory (<c>-c</c>); whether it exists is reported when it loads, as a workspace error.</summary>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    public static ConfigRef Workspace(string directory, Pipeline.Planning.PlanEnvironment provided) => new(ConfigRefKind.Workspace, directory, null, provided);

    /// <summary>
    /// A ref of any kind (<c>diff --from/--to</c>, <c>compare --baseline</c>): a content id, an existing directory (a
    /// workspace) or an existing file (a manifest). Returns null after reporting a usage error under <paramref name="option"/>.
    /// </summary>
    public static ConfigRef? Parse(string text, string? store, string option, DiagnosticBag diagnostics, Pipeline.Planning.PlanEnvironment provided)
    {
        if (!text.StartsWith(ContentHash.Prefix, StringComparison.Ordinal) && Directory.Exists(text))
        {
            return Workspace(text, provided);
        }

        return ParseManifest(text, store, option, diagnostics, workspaceAllowed: true, provided);
    }

    /// <summary>
    /// The one configuration of a command that takes <c>-c &lt;workspace&gt;</c> or <c>--manifest &lt;id|file&gt;</c>, not
    /// both; neither is <paramref name="fallback"/> (the default workspace <c>config</c>), or null when the command has
    /// another meaning for neither (replay on the recorded manifests). Returns null after reporting a usage error.
    /// </summary>
    public static (ConfigRef? Ref, bool Ok) Select(
        string? config, string? manifest, string? store, string? fallback, DiagnosticBag diagnostics, Pipeline.Planning.PlanEnvironment provided)
    {
        if (config is not null && manifest is not null)
        {
            diagnostics.Error("CONFIG_CONFLICT", "--manifest", string.Empty, $"Both -c '{config}' and --manifest '{manifest}' name the configuration.",
                "pass one: -c <workspace directory>, or --manifest <sha256:id|manifest file> with --store <dir>");
            return (null, false);
        }

        if (manifest is not null)
        {
            var parsed = ParseManifest(manifest, store, "--manifest", diagnostics, workspaceAllowed: false, provided);
            return (parsed, parsed is not null);
        }

        var directory = config ?? fallback;
        return (directory is null ? null : Workspace(directory, provided), true);
    }

    /// <summary>
    /// A manifest named on its own (<c>publish</c>): its content id or its file, read with <paramref name="store"/>. Returns
    /// null after reporting a usage error under <paramref name="option"/>.
    /// </summary>
    public static ConfigRef? Manifest(string text, string store, string option, DiagnosticBag diagnostics, Pipeline.Planning.PlanEnvironment provided)
    {
        if (!text.StartsWith(ContentHash.Prefix, StringComparison.Ordinal) && !File.Exists(text))
        {
            diagnostics.Error("CONFIG_REF_UNKNOWN", option, string.Empty,
                Directory.Exists(text) ? $"'{text}' is a directory, not a manifest." : $"'{text}' is neither a file nor a manifest id.",
                "pass the manifest's content id (sha256:<64 hex digits>, as 'stratarec build' prints it) or the manifest file 'stratarec build --manifest' wrote");
            return null;
        }

        return ParseManifest(text, store, option, diagnostics, workspaceAllowed: false, provided);
    }

    private static ConfigRef? ParseManifest(
        string text, string? store, string option, DiagnosticBag diagnostics, bool workspaceAllowed, Pipeline.Planning.PlanEnvironment provided)
    {
        ConfigRefKind kind;
        if (text.StartsWith(ContentHash.Prefix, StringComparison.Ordinal))
        {
            try
            {
                _ = ContentHash.GetHexDigest(text);
            }
            catch (FormatException ex)
            {
                diagnostics.Error("MANIFEST_ID_INVALID", option, string.Empty, ex.Message,
                    "copy the id 'stratarec build' printed (manifest sha256:...), or pass the manifest file");
                return null;
            }

            kind = ConfigRefKind.ManifestId;
        }
        else if (File.Exists(text))
        {
            kind = ConfigRefKind.ManifestFile;
        }
        else if (Directory.Exists(text))
        {
            diagnostics.Error("MANIFEST_IS_DIRECTORY", option, string.Empty, $"'{text}' is a directory, not a manifest.",
                "pass a workspace directory with -c, and a manifest as its content id (sha256:<hex>) or its file");
            return null;
        }
        else
        {
            diagnostics.Error("CONFIG_REF_UNKNOWN", option, string.Empty,
                workspaceAllowed
                    ? $"'{text}' is neither a directory, a file nor a manifest id."
                    : $"'{text}' is neither a file nor a manifest id.",
                workspaceAllowed
                    ? "pass a workspace directory, a manifest content id (sha256:<64 hex digits>) or a manifest file"
                    : "pass a manifest content id (sha256:<64 hex digits>) or a manifest file; a workspace directory goes with -c");
            return null;
        }

        if (store is null)
        {
            diagnostics.Error("STORE_REQUIRED", option, string.Empty,
                kind == ConfigRefKind.ManifestId
                    ? $"'{text}' is a manifest id, and no --store says where to look it up."
                    : $"'{text}' is a manifest file, and no --store says where its artifacts are.",
                StoreHint);
            return null;
        }

        return new ConfigRef(kind, text, store, provided);
    }

    /// <summary>
    /// The engine environment of the ref: relative paths in bindings resolve against a workspace's root. A manifest has no
    /// root, so they resolve against the current directory, as for a host that sets no base directory; only bindings that
    /// read a local file when created (the <c>memory@1</c> and <c>memory_list@1</c> fixtures) need one.
    /// </summary>
    public Pipeline.Planning.PlanEnvironment PlanEnvironment() => CommonOptions.ForRoot(_provided, Kind == ConfigRefKind.Workspace ? Text : null);

    /// <summary>
    /// The compile request of one pipeline: in a workspace, the one <paramref name="pipeline"/> names (or the only one) in
    /// the release of <paramref name="environment"/>; for a manifest, its pipeline, which <paramref name="pipeline"/> must
    /// then match. Returns null after reporting errors.
    /// </summary>
    public async Task<PlanCompileRequest?> CreateRequestAsync(string environment, string? pipeline, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        if (Kind == ConfigRefKind.Workspace)
        {
            var errorsBefore = diagnostics.ErrorCount;
            var workspace = ConfigWorkspace.Load(Text, diagnostics);
            return diagnostics.ErrorCount > errorsBefore ? null : CommonOptions.CreateRequest(workspace, environment, pipeline, diagnostics);
        }

        if (await ReadManifestAsync(diagnostics, cancellationToken).ConfigureAwait(false) is not { } read)
        {
            return null;
        }

        var (manifest, id, store) = read;

        if (pipeline is not null && pipeline != manifest.PipelineId)
        {
            diagnostics.Error("MANIFEST_OTHER_PIPELINE", Label(id), "/pipeline_id", $"The manifest releases pipeline '{manifest.PipelineId}', not '{pipeline}'.",
                $"omit --pipeline, or pass a manifest of pipeline '{pipeline}'");
            return null;
        }

        // As a node checks it: a manifest that needs what this binary lacks is reported as such, not as the
        // errors of compiling it without them.
        if (ManifestBuilder.CheckCapabilities(manifest, id, _provided, diagnostics).Count > 0)
        {
            return null;
        }

        return await ManifestBuilder.LoadAsync(manifest, id, store, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compiles the ref's pipeline without loading what it binds (as validate and build do). Null after reporting errors.</summary>
    public async Task<PlanCompilation?> CompileAsync(string environment, string? pipeline, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var request = await CreateRequestAsync(environment, pipeline, diagnostics, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return null;
        }

        var compilation = PlanCompiler.Compile(request, PlanEnvironment());
        diagnostics.AddRange(compilation.Diagnostics);
        return compilation;
    }

    /// <summary>
    /// An engine for replay, with no pipeline loaded yet (<see cref="LoadIntoAsync"/>). It loads no table and no client,
    /// since a replay answers every external call and local read from the recording: their model files,
    /// snapshot files and secrets are not needed, only the data files of fixtures that read them when created, at paths
    /// that resolve as <see cref="PlanEnvironment"/> says.
    /// </summary>
    public RecEngine CreateReplayEngine() => new(PlanEnvironment(), bindingOptions: new BindingHostOptions { Load = false });

    /// <summary>
    /// Loads the ref into <paramref name="engine"/>: every pipeline of a workspace's release, or the manifest's pipeline as
    /// a node loads it (<see cref="RecEngine.LoadManifestAsync"/>: its capabilities checked, at its sequence number). Returns
    /// the manifest's pipeline (empty for a workspace), or null after reporting errors.
    /// </summary>
    public async Task<string?> LoadIntoAsync(RecEngine engine, string environment, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        // Whether this load failed is its own diagnostics' verdict, not the bag's count: an error the bag holds already
        // is not added again.
        var pipeline = string.Empty;
        var failed = false;
        if (Kind == ConfigRefKind.Workspace)
        {
            var loaded = ServiceCollectionExtensions.LoadWorkspace(engine, Text, environment);
            failed = loaded.HasErrors;
            diagnostics.AddRange(loaded.Items);
        }
        else
        {
            var read = new DiagnosticBag();
            if (await ReadManifestAsync(read, cancellationToken).ConfigureAwait(false) is { } manifest)
            {
                var result = await engine.LoadManifestAsync(manifest.Manifest, manifest.Id, manifest.Store, cancellationToken).ConfigureAwait(false);
                read.AddRange(result.Diagnostics);
                pipeline = manifest.Manifest.PipelineId;
            }

            failed = read.HasErrors;
            diagnostics.AddRange(read.Items);
        }

        return failed ? null : pipeline;
    }

    /// <summary>
    /// Reads the manifest and its content id: from the store for an id, from the file otherwise, with the id computed as
    /// <c>stratarec build</c> computes it (the RFC 8785 hash of the manifest as the artifact model serializes it), so a
    /// file written by build has the id build printed. Null after reporting errors.
    /// </summary>
    public async Task<(ReleaseManifest Manifest, string Id, IArtifactStore Store)?> ReadManifestAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        if (Kind == ConfigRefKind.Workspace || Store is null)
        {
            throw new InvalidOperationException("A workspace has no manifest.");
        }

        var store = new FileSystemArtifactStore(Store);
        if (Kind == ConfigRefKind.ManifestId)
        {
            // As a node reads it; an id the store lacks says which store, and that a file would do.
            var read = new DiagnosticBag();
            var stored = await ManifestBuilder.ReadAsync(store, Text, read, cancellationToken).ConfigureAwait(false);
            foreach (var d in read.Items)
            {
                diagnostics.Add(d.Code == "MANIFEST_UNKNOWN"
                    ? d with
                    {
                        Message = $"The store {store.RootDirectory} has no manifest '{Text}'.",
                        Hint = "check the id ('stratarec build' prints it) and that --store is the store the manifest was built into; or pass the manifest file",
                    }
                    : d);
            }

            return stored is null ? null : (stored, Text, store);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(Text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("MANIFEST_UNREADABLE", Text, string.Empty, ex.Message);
            return null;
        }

        if (ManifestBuilder.Parse(json, Text, diagnostics) is not { } manifest)
        {
            return null;
        }

        try
        {
            return (manifest, ManifestBuilder.ContentId(manifest), store);
        }
        catch (JsonCanonicalizationException ex)
        {
            // Not written exactly, so no id: a sequence beyond 2^53 a tool other than build wrote (review 38).
            diagnostics.Error("MANIFEST_NOT_CANONICAL", Text, ex.Pointer.ToString(),
                $"The manifest has no content id: its canonical JSON (RFC 8785) cannot hold it exactly ({ex.Reason}).",
                $"write integers of at most {BuildCommand.MaxSeq} (2^53 - 1) here, as 'stratarec build' does");
            return null;
        }
    }

    /// <summary>The diagnostic artifact of a manifest, as <see cref="ManifestBuilder.LoadAsync"/> labels its errors.</summary>
    private static string Label(string id) => "manifest:" + id;
}
