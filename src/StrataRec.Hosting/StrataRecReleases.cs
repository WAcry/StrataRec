using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Hosting.Manifests;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>
/// The node side of releases (design 10.1 item 4, 10.3) for <see cref="ServiceCollectionExtensions.AddStrataRec"/> with
/// <see cref="StrataRecOptions.Releases"/>: loads the manifest each pipeline's pointer names before the host starts, then
/// polls the pointers and loads each new manifest in the background (<see cref="ReleaseOptions"/> has the schedule).
/// </summary>
/// <remarks>
/// <para>The startup load is in <see cref="StartingAsync"/> for the reason <see cref="StrataRecStartup"/>'s is: the host
/// takes no request before every plan has loaded. Anything short of every pipeline applied stops the host, since a node
/// that serves part of its pipelines, or a fallback that is never loaded, fails requests silently.</para>
/// <para>The polling starts in <see cref="StartAsync"/>, once every service's starting phase has succeeded, and runs on
/// one task: pipelines are polled one after another, and a load delays the polls after it. Stopping the host cancels it,
/// a load in progress included (the current plan stays), and waits for it to end, before the container disposes the
/// engine.</para>
/// </remarks>
internal sealed partial class StrataRecReleases : IHostedLifecycleService, IAsyncDisposable, IDisposable
{
    private readonly RecEngine _engine;
    private readonly IManifestSource? _source;
    private readonly IArtifactStore? _store;
    private readonly IReleaseStatusReporter? _reporter;
    private readonly string _environment;
    private readonly List<PipelineState> _pipelines = new();
    private readonly HashSet<string> _served = new(StringComparer.Ordinal);
    private readonly ReleaseOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _polling;
    private bool _stopRequested;
    private bool _disposed;

    public StrataRecReleases(
        RecEngine engine, IManifestSource? source, IArtifactStore? store, IReleaseStatusReporter? reporter, string environment,
        IReadOnlyList<string> pipelines, ReleaseOptions options, ILogger<StrataRecReleases> logger)
    {
        _engine = engine;
        _source = source;
        _store = store;
        _reporter = reporter;
        _environment = environment;
        _options = options;
        _logger = logger;
        foreach (var pipeline in pipelines)
        {
            if (_served.Add(pipeline))
            {
                _pipelines.Add(new PipelineState(pipeline));
            }
        }
    }

    private TimeProvider Clock => _options.TimeProvider;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        RequireServices();
        var errors = new DiagnosticBag();
        var statuses = new List<string>(_pipelines.Count);
        foreach (var state in _pipelines)
        {
            var pipelineId = state.PipelineId;
            string? manifestId;
            try
            {
                manifestId = await _source!.GetManifestIdAsync(pipelineId, _environment, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Error("RELEASE_POINTER_UNREADABLE", PipelineLabel(pipelineId), string.Empty,
                    $"The manifest pointer of pipeline '{pipelineId}' in environment '{_environment}' could not be read: {ex.Message}",
                    "check the manifest source the host registered (IManifestSource) and that it can be reached");
                statuses.Add($"{pipelineId}: pointer unreadable");
                continue;
            }

            if (manifestId is null)
            {
                errors.Error("RELEASE_NOT_PUBLISHED", PipelineLabel(pipelineId), string.Empty,
                    $"No manifest is published for pipeline '{pipelineId}' in environment '{_environment}'.",
                    "publish one ('stratarec build --store', then 'stratarec publish'), or remove the pipeline from StrataRecOptions.Pipelines");
                statuses.Add($"{pipelineId}: nothing published");
                continue;
            }

            var result = await AttemptAsync(state, manifestId, retry: false, cancellationToken).ConfigureAwait(false);
            statuses.Add(Describe(result));
            if (!result.Applied)
            {
                var before = errors.ErrorCount;
                foreach (var diagnostic in result.Diagnostics)
                {
                    if (diagnostic.IsError)
                    {
                        errors.Add(diagnostic);
                    }
                }

                if (errors.ErrorCount == before)
                {
                    errors.Error("RELEASE_NOT_APPLIED", ManifestBuilder.Label(manifestId), string.Empty,
                        $"The manifest of pipeline '{pipelineId}' was not applied: {result.Reason}.", "publish a manifest this node can apply");
                }
            }
        }

        // The fallbacks of the plans served now (a fallback that is not served never happens, design 5.6), and the inputs
        // a served fallback would fail requests on, as for a workspace.
        var checks = new DiagnosticBag();
        foreach (var state in _pipelines)
        {
            CheckFallback(state.PipelineId, checks, DiagnosticSeverity.Error);
        }

        LogWarnings(checks.Items);
        foreach (var diagnostic in checks.Items)
        {
            if (diagnostic.IsError)
            {
                errors.Add(diagnostic);
            }
        }

        errors.ThrowIfErrors($"Loading StrataRec releases of environment '{_environment}' ({string.Join("; ", statuses)})");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _polling = Task.Run(() => PollEveryIntervalAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Cancels the polling, a load in progress included, and waits for it to end (or for the host's stop timeout).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        _stopRequested = true;

        // Off this thread: the callbacks of a store or source on the token are theirs to run, not the stopping host's.
        var cancelled = _stopping.CancelAsync();
        try
        {
            if (_polling is { } polling)
            {
                await polling.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's stop timeout: it stops waiting; the polling ends on its own, its load cancelled.
        }

        await cancelled.ConfigureAwait(false);
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// A host disposed without being stopped stops the polling here, and waits for it, before the container disposes the
    /// engine. After a stop the polling is cancelled already: a store that ignores its token does not hold the disposal.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cancelled = _stopping.CancelAsync();
        if (!_stopRequested && _polling is { } polling)
        {
            await polling.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await cancelled.ConfigureAwait(false);
        if (_polling is null || _polling.IsCompleted)
        {
            _stopping.Dispose(); // a polling still running keeps its token
        }
    }

    /// <summary>For a container disposed synchronously.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void RequireServices()
    {
        if (_source is null)
        {
            throw new InvalidOperationException(
                "StrataRecOptions.Releases is set, and the service container has no IManifestSource to read the manifest pointers from. Register one, " +
                "e.g. services.AddSingleton<IManifestSource>(new FileSystemManifestSource(\"/srv/stratarec\")) for the pointers 'stratarec publish' writes.");
        }

        if (_store is null)
        {
            throw new InvalidOperationException(
                "StrataRecOptions.Releases is set, and the service container has no IArtifactStore to read manifests and artifacts from. Register one, " +
                "e.g. services.AddSingleton<IArtifactStore>(new FileSystemArtifactStore(\"/srv/stratarec\")) for the store 'stratarec build --store' uploads to.");
        }
    }

    /// <summary>Every <see cref="ReleaseOptions.PollInterval"/>, each pipeline's pointer; ends when the host stops.</summary>
    private async Task PollEveryIntervalAsync(CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.PollInterval, Clock, stopping).ConfigureAwait(false);
                foreach (var state in _pipelines)
                {
                    await PollPipelineAsync(state, stopping).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The host stops.
        }
    }

    private async Task PollPipelineAsync(PipelineState state, CancellationToken stopping)
    {
        if (Clock.GetUtcNow() < state.PointerRetryAt)
        {
            return;
        }

        string? manifestId;
        try
        {
            manifestId = await _source!.GetManifestIdAsync(state.PipelineId, _environment, stopping).ConfigureAwait(false);
            state.PointerDelay = TimeSpan.Zero;
        }
        catch (Exception ex) when (!stopping.IsCancellationRequested)
        {
            state.PointerDelay = NextDelay(state.PointerDelay);
            state.PointerRetryAt = Clock.GetUtcNow() + state.PointerDelay;
            LogPointerFailed(_logger, ex, state.PipelineId, _environment, (long)state.PointerDelay.TotalMilliseconds);
            return;
        }

        // No pointer (any more): the pipeline keeps the plan it serves. The id tried last: final, or not due yet.
        if (manifestId is null || (manifestId == state.ManifestId && (state.Final || Clock.GetUtcNow() < state.RetryAt)))
        {
            return;
        }

        var result = await AttemptAsync(state, manifestId, retry: true, stopping).ConfigureAwait(false);
        if (result.Applied)
        {
            var warnings = new DiagnosticBag();
            CheckFallback(state.PipelineId, warnings, DiagnosticSeverity.Warning); // applied already: it can only be said
            LogWarnings(warnings.Items);
        }
    }

    /// <summary>
    /// Reads the manifest, loads it, reports the outcome, and decides when it is tried again: never for an outcome that
    /// trying again would not change, with a growing interval otherwise.
    /// </summary>
    /// <param name="retry">False at startup, where anything not applied stops the host instead.</param>
    private async Task<ManifestLoadResult> AttemptAsync(PipelineState state, string manifestId, bool retry, CancellationToken cancellationToken)
    {
        if (manifestId != state.ManifestId)
        {
            state.ManifestId = manifestId;
            state.RetryDelay = TimeSpan.Zero;
        }

        ManifestLoadResult result;
        try
        {
            var diagnostics = new DiagnosticBag();
            var manifest = await ManifestBuilder.ReadAsync(_store!, manifestId, diagnostics, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                CheckIdentity(manifest, state.PipelineId, manifestId, diagnostics);
            }

            if (manifest is null || diagnostics.HasErrors)
            {
                PlanLoadTelemetry.ArtifactsFailed(_logger, state.PipelineId, manifestId, diagnostics);
                result = Unread(state.PipelineId, manifestId, diagnostics);
            }
            else
            {
                result = await _engine.LoadManifestAsync(manifest, manifestId, _store!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A store or source that throws its own exceptions (a transport's), or a defect: logged and tried again.
            var diagnostics = new DiagnosticBag();
            diagnostics.Error("MANIFEST_LOAD_FAILED", ManifestBuilder.Label(manifestId), string.Empty,
                $"Loading the manifest threw {ex.GetType().Name}: {ex.Message}", "the node tries again; check the artifact store and the manifest source");
            result = Unread(state.PipelineId, manifestId, diagnostics);
            LogAttemptFailed(_logger, ex, state.PipelineId, manifestId);
        }

        state.Final = result.Status != ManifestLoadStatus.Rejected;
        if (!state.Final && retry)
        {
            state.RetryDelay = NextDelay(state.RetryDelay);
            state.RetryAt = Clock.GetUtcNow() + state.RetryDelay;
            LogRetrying(_logger, state.PipelineId, manifestId, (long)state.RetryDelay.TotalMilliseconds);
        }
        else if (result.Applied)
        {
            var warnings = new DiagnosticBag();
            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Warning)
                {
                    warnings.Add(diagnostic);
                }
            }

            LogWarnings(warnings.Items);
        }

        await ReportAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The pointer of (pipeline, environment) must name a manifest of that pipeline and environment.</summary>
    private void CheckIdentity(ReleaseManifest manifest, string pipelineId, string manifestId, DiagnosticBag diagnostics)
    {
        if (manifest.PipelineId != pipelineId)
        {
            diagnostics.Error("MANIFEST_OTHER_PIPELINE", ManifestBuilder.Label(manifestId), "/pipeline_id",
                $"The pointer of pipeline '{pipelineId}' names a manifest of pipeline '{manifest.PipelineId}'.",
                "point it at a manifest of its own pipeline: 'stratarec publish' writes the pointer the manifest names");
        }

        if (manifest.Environment != _environment)
        {
            diagnostics.Error("MANIFEST_OTHER_ENVIRONMENT", ManifestBuilder.Label(manifestId), "/environment",
                $"The pointer of environment '{_environment}' names a manifest built for environment '{manifest.Environment}'.",
                $"build the manifest for this environment (stratarec build -e {_environment}) and publish it");
        }
    }

    /// <summary>
    /// A served pipeline whose fallback is not served falls back to nothing (an error at startup, a warning afterwards,
    /// when its plan is applied already); a served fallback is checked for the inputs it would fail requests on.
    /// </summary>
    private void CheckFallback(string pipelineId, DiagnosticBag diagnostics, DiagnosticSeverity notServed)
    {
        if (!_engine.TryGetPipeline(pipelineId, out var loaded) || loaded.FallbackPipeline is not { } fallback || fallback == pipelineId)
        {
            return;
        }

        if (!_served.Contains(fallback))
        {
            diagnostics.Add(new ConfigDiagnostic(notServed, "RELEASE_FALLBACK_NOT_SERVED", PipelineLabel(pipelineId), "/fallback_pipeline",
                $"Pipeline '{pipelineId}' falls back to '{fallback}', which this host does not serve: a request that fails would not fall back.",
                $"add '{fallback}' to StrataRecOptions.Pipelines and publish its manifest for environment '{_environment}'"));
        }
        else if (_engine.TryGetPipeline(fallback, out var served))
        {
            RequestSchema.CheckFallback(loaded.Plan.RequestSchema, served.Plan.RequestSchema, PipelineLabel(pipelineId), diagnostics);
        }
    }

    private async Task ReportAsync(ManifestLoadResult result, CancellationToken cancellationToken)
    {
        if (_reporter is null)
        {
            return;
        }

        try
        {
            await _reporter.ReportAsync(result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogReporterFailed(_logger, ex, result.PipelineId, result.ManifestId);
        }
    }

    private void LogWarnings(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Warning)
            {
                StrataRecStartup.LogWarning(_logger, diagnostic.Code, diagnostic.Artifact, diagnostic.Pointer, diagnostic.Message, diagnostic.Hint ?? "none");
            }
        }
    }

    /// <summary>The next wait before a failed manifest or pointer is tried again: the poll interval, then twice the last, up to the cap.</summary>
    private TimeSpan NextDelay(TimeSpan last)
    {
        if (last <= TimeSpan.Zero)
        {
            return _options.PollInterval;
        }

        return last >= _options.MaxRetryInterval / 2 ? _options.MaxRetryInterval : last * 2;
    }

    /// <summary>A manifest that could not be read, or whose load threw: rejected, with its sequence unknown (0).</summary>
    private ManifestLoadResult Unread(string pipelineId, string manifestId, DiagnosticBag diagnostics)
    {
        long? servedSequence = null;
        string? servedManifest = null;
        if (_engine.TryGetPipeline(pipelineId, out var served))
        {
            servedSequence = served.Sequence;
            servedManifest = served.Plan.ManifestId;
        }

        return new ManifestLoadResult(pipelineId, manifestId, 0, ManifestLoadStatus.Rejected, Array.Empty<string>(), diagnostics.Items, TimeSpan.Zero,
            ManifestLoadResult.RejectedReason(diagnostics.Items))
        {
            ServedSequence = servedSequence,
            ServedManifestId = servedManifest,
        };
    }

    private static string Describe(ManifestLoadResult result) => result.Applied
        ? $"{result.PipelineId}: applied manifest {result.ManifestId} (sequence {result.Sequence})"
        : $"{result.PipelineId}: {StatusName(result.Status)} manifest {result.ManifestId}: {result.Reason}";

    /// <summary>The status as the design writes it (<c>applied</c>, <c>rejected</c>, <c>incompatible</c>, …).</summary>
    private static string StatusName(ManifestLoadStatus status) => status switch
    {
        ManifestLoadStatus.Applied => "applied",
        ManifestLoadStatus.Stale => "stale",
        ManifestLoadStatus.Revoked => "revoked",
        ManifestLoadStatus.Incompatible => "incompatible",
        _ => "rejected",
    };

    private static string PipelineLabel(string pipelineId) => "pipeline:" + pipelineId;

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning,
        Message = "The manifest pointer of pipeline '{PipelineId}' in environment '{Environment}' could not be read; reading it again in {RetryMs} ms")]
    private static partial void LogPointerFailed(ILogger logger, Exception exception, string pipelineId, string environment, long retryMs);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Error,
        Message = "Loading manifest '{ManifestId}' of pipeline '{PipelineId}' threw; the current plan stays")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, string pipelineId, string manifestId);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning,
        Message = "The release status reporter threw on manifest '{ManifestId}' of pipeline '{PipelineId}'; nothing else is affected")]
    private static partial void LogReporterFailed(ILogger logger, Exception exception, string pipelineId, string manifestId);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information,
        Message = "Manifest '{ManifestId}' of pipeline '{PipelineId}' was not applied; trying it again in {RetryMs} ms unless its pointer moves")]
    private static partial void LogRetrying(ILogger logger, string pipelineId, string manifestId, long retryMs);

    /// <summary>What the polling knows of one pipeline; touched by one task at a time (the startup, then the polling).</summary>
    private sealed class PipelineState(string pipelineId)
    {
        public string PipelineId { get; } = pipelineId;

        /// <summary>The manifest tried last.</summary>
        public string? ManifestId { get; set; }

        /// <summary>Its outcome will not change by trying again: it is not tried again.</summary>
        public bool Final { get; set; }

        /// <summary>The last wait before trying it again, and when that is.</summary>
        public TimeSpan RetryDelay { get; set; }

        public DateTimeOffset RetryAt { get; set; }

        /// <summary>The last wait before reading a pointer that failed again, and when that is.</summary>
        public TimeSpan PointerDelay { get; set; }

        public DateTimeOffset PointerRetryAt { get; set; }
    }
}
