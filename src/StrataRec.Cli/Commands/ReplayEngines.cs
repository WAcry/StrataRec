using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting;

namespace StrataRec.Cli.Commands;

/// <summary>
/// The engines bundles replay on (design 13.4). A bundle's runs name every pipeline its request ran, the primary and, when
/// it failed over, the fallback pipeline, each on the manifest it recorded (<see cref="ReplayRunRecord.ManifestId"/>); a
/// replay needs a configuration for each of them, or it does not replay what happened.
/// </summary>
/// <remarks>
/// <para>Where each pipeline's configuration comes from depends on what the command line named:</para>
/// <list type="bullet">
/// <item><description>a workspace (<c>-c</c>): every pipeline from it;</description></item>
/// <item><description>a manifest (<c>--manifest</c>): its pipeline from it, which must be the bundle's own; any other pipeline
/// the request ran (its fallback) on the manifest it recorded, from the store, so the named manifest replaces one
/// pipeline's configuration and nothing else;</description></item>
/// <item><description>nothing, only <c>--store</c>: every pipeline on the manifest it recorded, so a production request
/// replays on exactly what served it.</description></item>
/// </list>
/// <para>A pipeline without a configuration (a recording made on a workspace has a local label, not a content id, and
/// nothing in a store identifies that workspace; a workspace without the fallback pipeline) leaves the bundle unresolved,
/// with the run, the pipeline and the command that replays it; the other bundles still replay. Engines are cached by the
/// configurations they hold, so the bundles of one release share one, and a request whose fallback pipeline ran on another
/// manifest than another request's gets its own.</para>
/// </remarks>
internal sealed class ReplayEngines : IAsyncDisposable
{
    private const string WorkspaceLabel = "workspace:";
    private const string OtherConfigurationHint = "replay it with -c <workspace directory> or --manifest <sha256:id|manifest file>";

    private readonly ConfigRef? _named;
    private readonly RecEngine? _workspace;
    private readonly string? _namedPipeline;
    private readonly string? _store;
    private readonly string _environment;
    private readonly TextWriter _errors;
    private readonly Dictionary<string, Resolution> _engines = new(StringComparer.Ordinal);

    private ReplayEngines(ConfigRef? named, RecEngine? workspace, string? namedPipeline, string? store, string environment, TextWriter errors)
    {
        _named = named;
        _workspace = workspace;
        _namedPipeline = namedPipeline;
        _store = store;
        _environment = environment;
        _errors = errors;
    }

    /// <summary>
    /// Engines for <paramref name="reference"/> (loaded now, so its errors are reported before any bundle), or, when it is
    /// null, for the manifests the bundles recorded, read from <paramref name="store"/> as bundles ask for them. Null after
    /// printing the errors of a configuration that does not load.
    /// </summary>
    public static async Task<ReplayEngines?> CreateAsync(
        ConfigRef? reference, string? store, string environment, string format, TextWriter errors, CancellationToken cancellationToken)
    {
        if (reference is null)
        {
            return new ReplayEngines(null, null, null, store, environment, errors);
        }

        var diagnostics = new DiagnosticBag();
        var engine = reference.CreateReplayEngine();
        var pipeline = await reference.LoadIntoAsync(engine, environment, diagnostics, cancellationToken).ConfigureAwait(false);
        if (pipeline is null)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            Output.Diagnostics(diagnostics.Items, format, errors);
            return null;
        }

        if (!reference.IsManifest)
        {
            return new ReplayEngines(reference, engine, null, store ?? reference.Store, environment, errors);
        }

        // The named manifest alone serves the bundles whose request did not fail over to another pipeline.
        var engines = new ReplayEngines(reference, null, pipeline, reference.Store, environment, errors);
        engines._engines[Key([(pipeline, reference.Text)])] = new Resolution(engine, null, null);
        return engines;
    }

    /// <summary>The engine to replay <paramref name="bundle"/> on, or why there is none and how to replay it instead.</summary>
    public async Task<(RecEngine? Engine, string? Problem, string? Hint)> ForAsync(ReplayBundle bundle, CancellationToken cancellationToken)
    {
        var configurations = new List<(string Pipeline, string Source)>();
        foreach (var run in Runs(bundle))
        {
            var problem = Configuration(bundle, run, out var source, out var hint);
            if (problem is not null)
            {
                return (null, problem, hint);
            }

            if (source is not null && !Contains(configurations, run.Pipeline))
            {
                configurations.Add((run.Pipeline, source));
            }
        }

        if (_workspace is not null)
        {
            return (_workspace, null, null);
        }

        var key = Key(configurations);
        if (!_engines.TryGetValue(key, out var resolution))
        {
            resolution = await LoadAsync(configurations, cancellationToken).ConfigureAwait(false);
            _engines[key] = resolution;
        }

        return (resolution.Engine, resolution.Problem, resolution.Hint);
    }

    /// <summary>
    /// The pipelines the bundle's request ran, each with the manifest it recorded and where the bundle says so; a bundle
    /// without runs stands for its primary run on the bundle's manifest.
    /// </summary>
    private static List<Run> Runs(ReplayBundle bundle)
    {
        var runs = new List<Run>(Math.Max(1, bundle.Runs.Count));
        for (var i = 0; i < bundle.Runs.Count; i++)
        {
            var run = bundle.Runs[i];
            runs.Add(new Run(run.PipelineId, run.ManifestId, run.Role == "fallback_pipeline", $"/runs/{i}/manifest_id"));
        }

        if (runs.Count == 0)
        {
            runs.Add(new Run(bundle.Request.PipelineId, bundle.Manifest.Id, false, "/manifest/id"));
        }

        return runs;
    }

    /// <summary>
    /// Where the configuration of <paramref name="run"/>'s pipeline comes from: a manifest id or the named manifest, or
    /// null when the named workspace serves it. Returns why there is none.
    /// </summary>
    private string? Configuration(ReplayBundle bundle, Run run, out string? source, out string? hint)
    {
        source = null;
        hint = null;
        if (_workspace is not null)
        {
            if (_workspace.TryGetPipeline(run.Pipeline, out _))
            {
                return null;
            }

            hint = $"replay it on a workspace that has pipeline '{run.Pipeline}', or with --store only (each run on the manifest it recorded)";
            return run.Fallback
                ? $"its fallback pipeline '{run.Pipeline}' ran ({run.Pointer}), and workspace '{_named}' does not have it"
                : $"recorded on pipeline '{run.Pipeline}', which workspace '{_named}' does not have";
        }

        if (_namedPipeline is not null)
        {
            if (run.Pipeline == _namedPipeline)
            {
                source = _named!.Text;
                return null;
            }

            if (run.Pipeline == bundle.Request.PipelineId)
            {
                hint = $"pass a manifest of pipeline '{run.Pipeline}', or replay it with --store only (on the manifest it recorded)";
                return $"recorded on pipeline '{run.Pipeline}', and --manifest releases pipeline '{_namedPipeline}'";
            }
        }

        // Its recorded manifest: every run without a named configuration, and another pipeline's run (a fallback) when one is.
        var id = run.Manifest;
        if (ContentHash.IsValid(id))
        {
            source = id;
            return null;
        }

        var slash = id.LastIndexOf('/');
        if (id.StartsWith(WorkspaceLabel, StringComparison.Ordinal) && slash > WorkspaceLabel.Length)
        {
            hint = $"replay it on its workspace: -c <workspace directory> -e {id[(slash + 1)..]}";
            return run.Fallback
                ? $"its fallback pipeline '{run.Pipeline}' ran on a workspace ('{id}', {run.Pointer}), not on a manifest the store holds"
                : $"recorded on a workspace ('{id}'), not on a manifest the store holds";
        }

        hint = OtherConfigurationHint;
        return run.Fallback
            ? $"its fallback pipeline '{run.Pipeline}' ran on '{id}' ({run.Pointer}), which is not a manifest content id"
            : $"recorded on '{id}', which is not a manifest content id";
    }

    private static bool Contains(List<(string Pipeline, string Source)> configurations, string pipeline)
    {
        foreach (var (p, _) in configurations)
        {
            if (p == pipeline)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The cache key of an engine: its pipelines and where each came from, in order.</summary>
    private static string Key(List<(string Pipeline, string Source)> configurations)
    {
        var parts = new string[configurations.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = configurations[i].Pipeline + "=" + configurations[i].Source;
        }

        Array.Sort(parts, StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    /// <summary>One engine holding every configuration of <paramref name="configurations"/>; errors are printed once per engine.</summary>
    private async Task<Resolution> LoadAsync(List<(string Pipeline, string Source)> configurations, CancellationToken cancellationToken)
    {
        RecEngine? engine = null;
        var diagnostics = new DiagnosticBag();
        try
        {
            foreach (var (pipeline, source) in configurations)
            {
                var reference = source == _named?.Text ? _named : ConfigRef.Parse(source, _store, "manifest", diagnostics);
                if (reference is null)
                {
                    return Failed(pipeline, source, diagnostics);
                }

                engine ??= reference.CreateReplayEngine();
                if (await reference.LoadIntoAsync(engine, _environment, diagnostics, cancellationToken).ConfigureAwait(false) is null)
                {
                    return Failed(pipeline, source, diagnostics);
                }
            }

            var loaded = engine;
            engine = null;
            return new Resolution(loaded, null, null);
        }
        finally
        {
            if (engine is not null)
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Resolution Failed(string pipeline, string source, DiagnosticBag diagnostics)
    {
        _errors.WriteLine($"manifest {source} of pipeline '{pipeline}', recorded by a bundle, does not load from {_store}:");
        Output.Diagnostics(diagnostics.Items, "text", _errors);
        foreach (var d in diagnostics.Items)
        {
            if (d.IsError)
            {
                return new Resolution(null, $"pipeline '{pipeline}' ran on manifest {source}, which does not load: {d.Code} {d.Artifact}{d.Pointer}: {d.Message}",
                    d.Hint ?? OtherConfigurationHint);
            }
        }

        return new Resolution(null, $"pipeline '{pipeline}' ran on manifest {source}, which does not load", OtherConfigurationHint);
    }

    public async ValueTask DisposeAsync()
    {
        if (_workspace is not null)
        {
            await _workspace.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var (_, resolution) in _engines)
        {
            if (resolution.Engine is not null)
            {
                await resolution.Engine.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private readonly record struct Resolution(RecEngine? Engine, string? Problem, string? Hint);

    /// <param name="Fallback">The run of the pipeline the request failed over to (<c>fallback_pipeline</c>).</param>
    /// <param name="Pointer">Where the bundle records the manifest.</param>
    private readonly record struct Run(string Pipeline, string Manifest, bool Fallback, string Pointer);
}
