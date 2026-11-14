using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting;

namespace StrataRec.Cli.Commands;

/// <summary>
/// The engines a replay runs on: the one configuration named on the command line, or, given only an artifact store, the
/// release manifest each bundle recorded (<see cref="ReplayManifestRecord.Id"/>), so a production request replays on
/// exactly what served it. Each recorded manifest is loaded once and shared by every bundle recorded on it.
/// </summary>
/// <remarks>
/// A bundle recorded on a workspace carries a local label (<c>workspace:&lt;pipeline&gt;/&lt;env&gt;</c>), not a content
/// id: nothing in a store identifies that workspace, or its state when the request ran. Such a bundle is reported as
/// not replayed, with the command that replays it, and the other bundles still replay.
/// </remarks>
internal sealed class ReplayEngines : IAsyncDisposable
{
    private const string WorkspaceLabel = "workspace:";

    private readonly RecEngine? _fixed;
    private readonly string? _store;
    private readonly string _environment;
    private readonly TextWriter _errors;
    private readonly Dictionary<string, (RecEngine? Engine, string? Problem, string? Hint)> _recorded = new(StringComparer.Ordinal);

    private ReplayEngines(RecEngine? fixedEngine, string? store, string environment, TextWriter errors)
    {
        _fixed = fixedEngine;
        _store = store;
        _environment = environment;
        _errors = errors;
    }

    /// <summary>
    /// Engines for <paramref name="reference"/>, loaded now, or, when it is null, for the manifests the bundles recorded,
    /// read from <paramref name="store"/> as bundles ask for them. Null after printing the errors of a configuration that
    /// does not load.
    /// </summary>
    public static async Task<ReplayEngines?> CreateAsync(
        ConfigRef? reference, string? store, string environment, string format, TextWriter errors, CancellationToken cancellationToken)
    {
        if (reference is null)
        {
            return new ReplayEngines(null, store, environment, errors);
        }

        var engine = await ReplayCommand.LoadEngineAsync(reference, environment, format, errors, cancellationToken).ConfigureAwait(false);
        return engine is null ? null : new ReplayEngines(engine, null, environment, errors);
    }

    /// <summary>The engine to replay <paramref name="bundle"/> on, or why there is none and how to replay it instead.</summary>
    public async Task<(RecEngine? Engine, string? Problem, string? Hint)> ForAsync(ReplayBundle bundle, CancellationToken cancellationToken)
    {
        if (_fixed is not null)
        {
            return (_fixed, null, null);
        }

        var id = bundle.Manifest.Id;
        if (!_recorded.TryGetValue(id, out var engine))
        {
            engine = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            _recorded[id] = engine;
        }

        return engine;
    }

    private async Task<(RecEngine? Engine, string? Problem, string? Hint)> LoadAsync(string id, CancellationToken cancellationToken)
    {
        if (!ContentHash.IsValid(id))
        {
            var slash = id.LastIndexOf('/');
            if (id.StartsWith(WorkspaceLabel, StringComparison.Ordinal) && slash > WorkspaceLabel.Length)
            {
                return (null, $"recorded on a workspace ('{id}'), not on a manifest the store holds",
                    $"replay it on its workspace: -c <workspace directory> -e {id[(slash + 1)..]}");
            }

            return (null, $"recorded on '{id}', which is not a manifest content id", "replay it with -c <workspace directory> or --manifest <sha256:id|manifest file>");
        }

        var diagnostics = new DiagnosticBag();
        var reference = ConfigRef.Parse(id, _store, "manifest", diagnostics);
        var engine = reference is null ? null : await reference.LoadReplayEngineAsync(_environment, diagnostics, cancellationToken).ConfigureAwait(false);
        if (engine is not null)
        {
            return (engine, null, null);
        }

        // Printed once per manifest; each bundle recorded on it says why it was not replayed.
        _errors.WriteLine($"manifest {id}, recorded by a bundle, does not load from {_store}:");
        Output.Diagnostics(diagnostics.Items, "text", _errors);
        foreach (var d in diagnostics.Items)
        {
            if (d.IsError)
            {
                return (null, $"recorded on manifest {id}, which does not load: {d.Code} {d.Artifact}{d.Pointer}: {d.Message}",
                    d.Hint ?? "replay it with -c <workspace directory> or --manifest <sha256:id|manifest file>");
            }
        }

        return (null, $"recorded on manifest {id}, which does not load", "replay it with -c <workspace directory> or --manifest <sha256:id|manifest file>");
    }

    public async ValueTask DisposeAsync()
    {
        if (_fixed is not null)
        {
            await _fixed.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var (_, (engine, _, _)) in _recorded)
        {
            if (engine is not null)
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
