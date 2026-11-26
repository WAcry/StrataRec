using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Threading;
using StrataRec.Hosting.Manifests;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>
/// The node side of releases for <see cref="ServiceCollectionExtensions.AddStrataRec"/> with
/// <see cref="StrataRecOptions.Releases"/>: loads the manifest each pipeline's pointer names before the host starts, then
/// polls the pointers and loads each new manifest in the background (<see cref="ReleaseOptions"/> has the schedule).
/// </summary>
/// <remarks>
/// <para>The startup load is in <see cref="StartingAsync"/> for the reason <see cref="StrataRecStartup"/>'s is: the host
/// takes no request before every plan has loaded. Anything short of every pipeline applied stops the host, since a node
/// that serves part of its pipelines, or a fallback that is never loaded, fails requests silently.</para>
/// <para>The polling starts in <see cref="StartAsync"/>, once every service's starting phase has succeeded, with one task
/// per pipeline: a load, or a pointer read that hangs until its <see cref="ReleaseOptions.PointerTimeout"/>, delays only
/// its own pipeline's polls, so an emergency revocation of one pipeline never waits for another (review: a blocked report
/// of one pipeline held a newer release of another for 25 polls). Loads of different pipelines may run at the same time,
/// as the engine allows. The background outcomes go to the reporter through a bounded queue and one task of their own
/// (<see cref="IReleaseStatusReporter"/>); at startup each is reported before the next pipeline loads, so a host that
/// fails to start has reported why. Stopping the host cancels all of it, a load in progress included (the current plan
/// stays), and waits for it to end, before the container disposes the engine.</para>
/// <para>The source, the store and the reporter get tokens of the framework's (<see cref="CancellationSite.Release"/>), never
/// the host's: their callbacks run on the thread pool when the host cancels, and what they throw is counted, not thrown
/// on the host's timer. A stop waits for the callbacks of its cancellation as for the polling, within the host's stop
/// timeout: the container disposes the extensions next, and a callback cleaning up must not find them disposed (review
/// 36). Neither a logger nor a metric listener that throws changes what the polling does: the record is lost, the
/// schedule and the reports are not.</para>
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
    private readonly ObservedCancellationSource _stopping = new(CancellationSite.Release);
    private readonly Channel<ManifestLoadResult>? _reports; // background outcomes waiting for the reporter
    private readonly List<ObservedCancellationSource> _calls = new(); // calls in flight, or whose callbacks still run (see BeginCall); its own lock
    private Task? _polling;
    private bool _stopRequested;
    private bool _cancelled; // under _calls: no call starts any more
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

        if (reporter is not null)
        {
            // Room for a few outcomes of every pipeline; past that, the oldest is the least worth reporting.
            var queue = new BoundedChannelOptions(Math.Max(16, 4 * _pipelines.Count))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            };
            _reports = Channel.CreateBounded<ManifestLoadResult>(queue, ReportDropped);
        }
    }

    private TimeProvider Clock => _options.TimeProvider;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        RequireServices();

        // From here on the token the extensions get: cancelled with the host's, its callbacks on the pool.
        using var startup = new ObservedCancellationSource(CancellationSite.Release, cancellationToken);
        cancellationToken = startup.Token;
        var errors = new DiagnosticBag();
        var statuses = new List<string>(_pipelines.Count);
        foreach (var state in _pipelines)
        {
            var pipelineId = state.PipelineId;
            // Within PointerTimeout, as in the background: a read that hangs fails the start instead of holding it.
            var call = BeginCall(_options.PointerTimeout, cancellationToken) ?? throw new OperationCanceledException(cancellationToken);
            string? manifestId;
            try
            {
                manifestId = await _source!.GetManifestIdAsync(pipelineId, _environment, call.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var why = call.IsCancellationRequested
                    ? $"it did not answer within {_options.PointerTimeout.TotalMilliseconds:0} ms (ReleaseOptions.PointerTimeout)"
                    : ExceptionText.Of(ex);
                errors.Error("RELEASE_POINTER_UNREADABLE", PipelineLabel(pipelineId), string.Empty,
                    $"The manifest pointer of pipeline '{pipelineId}' in environment '{_environment}' could not be read: {why}",
                    _source is FileSystemManifestSource && ex is ArgumentException
                        ? FileSystemManifestSource.NameHint // a name the file system source refuses: one an earlier version accepted
                        : "check the manifest source the host registered (IManifestSource) and that it can be reached");
                statuses.Add($"{pipelineId}: pointer unreadable");
                continue;
            }
            finally
            {
                EndCall(call);
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
            await ReportAsync(result, cancellationToken).ConfigureAwait(false);
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

        // The fallbacks of the plans served now (a fallback that is not served never happens), and the inputs
        // a served fallback would fail requests on, as for a workspace.
        var checks = new DiagnosticBag();
        foreach (var state in _pipelines)
        {
            CheckFallback(state.PipelineId, checks, DiagnosticSeverity.Error);
            CheckCalls(state.PipelineId, checks, DiagnosticSeverity.Error);
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
        var stopping = _stopping.Token;
        var tasks = new Task[_pipelines.Count + (_reports is null ? 0 : 1)];
        for (var i = 0; i < _pipelines.Count; i++)
        {
            var state = _pipelines[i];
            tasks[i] = Task.Run(() => PollEveryIntervalAsync(state, stopping), CancellationToken.None);
        }

        if (_reports is not null)
        {
            tasks[^1] = Task.Run(() => ReportQueuedAsync(stopping), CancellationToken.None);
        }

        _polling = Task.WhenAll(tasks);
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Cancels the polling, a load in progress included, and waits for it to end and for the callbacks the extensions
    /// registered on its token to run, or for the host's stop timeout, which bounds the stop whatever they do.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        _stopRequested = true;
        var stopped = Stopped(CancelAll());
        await stopped.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // At the host's stop timeout it stops waiting: the polling ends on its own, its load cancelled.
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
        var callbacks = CancelAll();
        if (!_stopRequested)
        {
            await Stopped(callbacks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Disposed once nothing holds its token: now, or when a polling that outlived the host's stop timeout ends.
        if (_polling is { IsCompleted: false } running)
        {
            _ = running.ContinueWith(static (_, stopping) => ((IDisposable)stopping!).Dispose(), _stopping, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            _stopping.Dispose();
        }
    }

    /// <summary>
    /// Cancels the calls in flight, then the polling. Each call's token is cancelled here, not through its link to the
    /// polling's, so the returned task covers its callbacks too: a source's cleanup on the token of a pointer read ends
    /// before the container disposes the source. No call starts after this.
    /// </summary>
    private Task CancelAll()
    {
        var callbacks = new List<Task>();
        lock (_calls)
        {
            _cancelled = true;
            foreach (var call in _calls)
            {
                callbacks.Add(call.CancelObservedAsync());
            }
        }

        callbacks.Add(_stopping.CancelObservedAsync());
        return Task.WhenAll(callbacks);
    }

    /// <summary>
    /// The token of one call into an extension (a pointer read, a report): cancelled after <paramref name="timeout"/>, with
    /// <paramref name="linkedTo"/>, or by a stop (<see cref="CancelAll"/>). Null once the stop has begun: the call is not
    /// made. End it with <see cref="EndCall"/> once the call has returned. A stop waits for the callbacks of every call
    /// that is not over, including one that has returned while its timeout's callbacks still run (<see cref="EndCall"/>).
    /// </summary>
    private ObservedCancellationSource? BeginCall(TimeSpan timeout, CancellationToken linkedTo)
    {
        lock (_calls)
        {
            if (_cancelled)
            {
                return null;
            }

            var call = new ObservedCancellationSource(CancellationSite.Release, linkedTo);
            call.CancelAfterObserved(timeout);
            _calls.Add(call);
            return call;
        }
    }

    /// <summary>
    /// Ends a call that has returned. A call cancelled before (its timeout, mostly) may have returned while the callbacks
    /// of that cancellation still run, a source's cleanup on its token, say: it stays listed until they have run, so a
    /// stop still waits for them before the container disposes the source (the call was dropped on return,
    /// and the stop finished first). Not waited for here, which would hold the polling.
    /// </summary>
    private void EndCall(ObservedCancellationSource call)
    {
        call.Dispose(); // stops its timer, so no cancellation starts after this; the base disposal waits for callbacks
        var callbacks = call.WhenCallbacksRunAsync();
        if (callbacks.IsCompleted)
        {
            Forget(call);
        }
        else
        {
            _ = callbacks.ContinueWith(
                static (_, state) => { var (releases, call) = ((StrataRecReleases, ObservedCancellationSource))state!; releases.Forget(call); },
                (this, call), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void Forget(ObservedCancellationSource call)
    {
        lock (_calls)
        {
            _calls.Remove(call);
        }
    }

    /// <summary>
    /// Completes when the polling has ended and the callbacks of the stopping cancellation (<paramref name="callbacks"/>,
    /// the winning cancellation's) have run. Only their end counts here: what they threw is the source's to observe, and a
    /// task faulted with it again would go unobserved.
    /// </summary>
    private Task Stopped(Task callbacks)
    {
        var ended = callbacks.IsCompleted
            ? Task.CompletedTask
            : callbacks.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return _polling is { } polling ? Task.WhenAll(polling, ended) : ended;
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

    /// <summary>Every <see cref="ReleaseOptions.PollInterval"/>, the pipeline's pointer; ends when the host stops.</summary>
    private async Task PollEveryIntervalAsync(PipelineState state, CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.PollInterval, Clock, stopping).ConfigureAwait(false);
                try
                {
                    await PollPipelineAsync(state, stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stopping.IsCancellationRequested)
                {
                    // A defect: the polling must outlive it, or the node would serve the plan and never take another
                    // release. The attempt it cut short is tried again, as a rejected one is (a new id left
                    // final, its manifest was never loaded).
                    state.Final = false;
                    state.RetryDelay = NextDelay(state.RetryDelay);
                    state.RetryAt = Clock.GetUtcNow() + state.RetryDelay;
                    PollFailed(ex, state);
                }
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // The host stops: whatever the cancelled work threw on its way out (a cancellation, or a transport's own
            // exception) ends the polling, and is no fault for anyone to observe after the stop gave up waiting.
        }
    }

    private async Task PollPipelineAsync(PipelineState state, CancellationToken stopping)
    {
        if (Clock.GetUtcNow() < state.PointerRetryAt)
        {
            return;
        }

        if (BeginCall(_options.PointerTimeout, stopping) is not { } call)
        {
            return; // stopping
        }

        string? manifestId;
        try
        {
            manifestId = await _source!.GetManifestIdAsync(state.PipelineId, _environment, call.Token).ConfigureAwait(false);
            state.PointerDelay = TimeSpan.Zero;
        }
        catch (Exception ex) when (!stopping.IsCancellationRequested && !Volatile.Read(ref _cancelled))
        {
            state.PointerDelay = NextDelay(state.PointerDelay);
            state.PointerRetryAt = Clock.GetUtcNow() + state.PointerDelay;
            PointerFailed(ex, state);
            return;
        }
        finally
        {
            EndCall(call);
        }

        // No pointer (any more): the pipeline keeps the plan it serves. The id tried last: final, or not due yet.
        if (manifestId is null || (manifestId == state.ManifestId && (state.Final || Clock.GetUtcNow() < state.RetryAt)))
        {
            return;
        }

        var result = await AttemptAsync(state, manifestId, retry: true, stopping).ConfigureAwait(false);
        _reports?.Writer.TryWrite(result); // bounded, dropping the oldest: never waits
        if (result.Applied)
        {
            var warnings = new DiagnosticBag();
            CheckFallbacksAround(state.PipelineId, warnings); // applied already: it can only be said
            LogWarnings(warnings.Items);
        }
    }

    /// <summary>
    /// The checks a plan that loaded after startup changes, as warnings: its own fallback (<see cref="CheckFallback"/>), and
    /// every served pipeline that falls back to it, since a fallback's new plan may require an input those do not give
    /// (found otherwise only when one of them first falls back, failing the request).
    /// </summary>
    private void CheckFallbacksAround(string pipelineId, DiagnosticBag diagnostics)
    {
        CheckFallback(pipelineId, diagnostics, DiagnosticSeverity.Warning);
        CheckCalls(pipelineId, diagnostics, DiagnosticSeverity.Warning);
        foreach (var other in _pipelines)
        {
            if (other.PipelineId == pipelineId || !_engine.TryGetPipeline(other.PipelineId, out var loaded))
            {
                continue;
            }

            if (loaded.FallbackPipeline == pipelineId)
            {
                CheckFallback(other.PipelineId, diagnostics, DiagnosticSeverity.Warning);
            }

            if (loaded.Plan.Calls.Contains(pipelineId))
            {
                CheckCalls(other.PipelineId, diagnostics, DiagnosticSeverity.Warning, only: pipelineId); // its calls of the new plan
            }
        }
    }

    /// <summary>
    /// The pipelines a served pipeline's nodes call: one this host does not serve fails every such call (an
    /// error at startup, a warning afterwards, as for a fallback), and a served one is checked against what each call
    /// needs of it, which a compile without the other pipelines could not (the call would fail when made).
    /// </summary>
    /// <param name="only">Check the calls of this pipeline alone: the one whose plan changed.</param>
    private void CheckCalls(string pipelineId, DiagnosticBag diagnostics, DiagnosticSeverity notServed, string? only = null)
    {
        if (!_engine.TryGetPipeline(pipelineId, out var loaded) || loaded.Plan.Calls.IsEmpty)
        {
            return;
        }

        var reported = new HashSet<(string, string)>();
        foreach (var node in loaded.Plan.Nodes)
        {
            foreach (var call in node.Calls)
            {
                var called = call.PipelineId;
                if (only is not null && called != only)
                {
                    continue;
                }

                var pointer = node.Pointer + "/config" + call.ConfigKey;
                if (!_served.Contains(called))
                {
                    if (reported.Add((pointer, called)))
                    {
                        diagnostics.Add(new ConfigDiagnostic(notServed, "RELEASE_CALLED_NOT_SERVED", PipelineLabel(pipelineId), pointer,
                            $"Node '{node.NodeId}' of pipeline '{pipelineId}' calls '{called}', which this host does not serve: every such call fails.",
                            $"add '{called}' to StrataRecOptions.Pipelines and publish its manifest for environment '{_environment}'"));
                    }
                }
                else if (_engine.TryGetPipeline(called, out var served) && call.MismatchOf(served.Plan) is { } mismatch && reported.Add((pointer, mismatch)))
                {
                    diagnostics.Warning("RELEASE_CALL_MISMATCH", PipelineLabel(pipelineId), pointer,
                        $"Node '{node.NodeId}' of pipeline '{pipelineId}' calls '{called}', which it does not fit as served: {mismatch}. Every such call fails.",
                        $"release '{pipelineId}' and '{called}' so that they agree");
                }
            }
        }
    }

    /// <summary>
    /// Reads the manifest, loads it, and decides when it is tried again: never for an outcome that trying again would not
    /// change, with a growing interval otherwise. The caller reports the outcome.
    /// </summary>
    /// <param name="retry">False at startup, where anything not applied stops the host instead.</param>
    private async Task<ManifestLoadResult> AttemptAsync(PipelineState state, string manifestId, bool retry, CancellationToken cancellationToken)
    {
        if (manifestId != state.ManifestId)
        {
            // Nothing of the last manifest's outcome carries over, whatever ends this attempt.
            state.ManifestId = manifestId;
            state.Final = false;
            state.RetryDelay = TimeSpan.Zero;
            state.RetryAt = default;
        }

        var started = Clock.GetUtcNow();
        ManifestLoadResult result;
        try
        {
            var diagnostics = new DiagnosticBag();
            var manifest = await ManifestBuilder.ReadAsync(_store!, manifestId, diagnostics, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                ManifestBuilder.CheckIdentity(manifest, manifestId, state.PipelineId, _environment, diagnostics);
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
                $"Loading the manifest threw {ex.GetType().Name}: {ExceptionText.Of(ex)}", "the node tries again; check the artifact store and the manifest source");
            result = Unread(state.PipelineId, manifestId, diagnostics);
            AttemptFailed(ex, state.PipelineId, manifestId);
        }

        result = result with { AttemptedAt = started };
        state.Final = result.Status != ManifestLoadStatus.Rejected;
        if (!state.Final && retry)
        {
            state.RetryDelay = NextDelay(state.RetryDelay);
            state.RetryAt = Clock.GetUtcNow() + state.RetryDelay;
            Retrying(state);
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

        return result;
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

    /// <summary>Reports the background outcomes in the order they were queued, one at a time; ends when the host stops.</summary>
    private async Task ReportQueuedAsync(CancellationToken stopping)
    {
        try
        {
            while (await _reports!.Reader.WaitToReadAsync(stopping).ConfigureAwait(false))
            {
                while (_reports.Reader.TryRead(out var result))
                {
                    await ReportAsync(result, stopping).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // The host stops: the reports still waiting are dropped with it.
        }
    }

    /// <summary>
    /// Tells the reporter one outcome, cancelling its token after <see cref="ReleaseOptions.ReportTimeout"/>, and returns when
    /// the call does: the reporter is never called concurrently.
    /// </summary>
    private async Task ReportAsync(ManifestLoadResult result, CancellationToken cancellationToken)
    {
        if (_reporter is null)
        {
            return;
        }

        if (BeginCall(_options.ReportTimeout, cancellationToken) is not { } call)
        {
            return; // stopping
        }

        try
        {
            await _reporter.ReportAsync(result, call.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !Volatile.Read(ref _cancelled))
        {
            if (call.IsCancellationRequested)
            {
                ReportTimedOut(result);
            }
            else
            {
                ReporterFailed(ex, result);
            }
        }
        finally
        {
            EndCall(call);
        }
    }

    private void LogWarnings(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Warning)
            {
                try
                {
                    StrataRecStartup.LogWarning(_logger, diagnostic.Code, diagnostic.Artifact, diagnostic.Pointer, diagnostic.Message, diagnostic.Hint ?? "none");
                }
                catch (Exception)
                {
                    // As for the records below.
                }
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
        ManifestLoadStatus.Incompatible => "incompatible",
        _ => "rejected",
    };

    private static string PipelineLabel(string pipelineId) => "pipeline:" + pipelineId;

    // Every record of the polling is written through one of these, which drop what a logger throws: the record is lost,
    // and nothing else changes (the schedule of retries, the report, the polling itself).
    private void PointerFailed(Exception exception, PipelineState state)
    {
        try
        {
            LogPointerFailed(_logger, exception, state.PipelineId, _environment, (long)state.PointerDelay.TotalMilliseconds);
        }
        catch (Exception)
        {
            // See above.
        }
    }

    private void AttemptFailed(Exception exception, string pipelineId, string manifestId)
    {
        try
        {
            LogAttemptFailed(_logger, exception, pipelineId, manifestId);
        }
        catch (Exception)
        {
            // See above.
        }
    }

    private void Retrying(PipelineState state)
    {
        try
        {
            LogRetrying(_logger, state.PipelineId, state.ManifestId!, (long)state.RetryDelay.TotalMilliseconds);
        }
        catch (Exception)
        {
            // See above.
        }
    }

    private void ReporterFailed(Exception exception, ManifestLoadResult result)
    {
        try
        {
            LogReporterFailed(_logger, exception, result.PipelineId, result.ManifestId);
        }
        catch (Exception)
        {
            // See above.
        }
    }

    private void ReportTimedOut(ManifestLoadResult result)
    {
        try
        {
            LogReportTimedOut(_logger, result.PipelineId, result.ManifestId, (long)_options.ReportTimeout.TotalMilliseconds);
        }
        catch (Exception)
        {
            // See above.
        }
    }

    /// <summary>The queue was full: the oldest waiting outcome gives way (called by the channel, under its lock).</summary>
    private void ReportDropped(ManifestLoadResult result)
    {
        try
        {
            LogReportDropped(_logger, result.PipelineId, result.ManifestId);
        }
        catch (Exception)
        {
            // See above.
        }
    }

    private void PollFailed(Exception exception, PipelineState state)
    {
        try
        {
            LogPollFailed(_logger, exception, state.PipelineId);
        }
        catch (Exception)
        {
            // See above.
        }
    }

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

    [LoggerMessage(EventId = 1013, Level = LogLevel.Error,
        Message = "Polling the manifest pointer of pipeline '{PipelineId}' threw; polling it again at the next interval")]
    private static partial void LogPollFailed(ILogger logger, Exception exception, string pipelineId);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Warning,
        Message = "The status report of manifest '{ManifestId}' of pipeline '{PipelineId}' was dropped: the release status reporter is behind")]
    private static partial void LogReportDropped(ILogger logger, string pipelineId, string manifestId);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning,
        Message = "The release status reporter did not finish reporting manifest '{ManifestId}' of pipeline '{PipelineId}' within {TimeoutMs} ms; its token was cancelled")]
    private static partial void LogReportTimedOut(ILogger logger, string pipelineId, string manifestId, long timeoutMs);

    /// <summary>What the polling knows of one pipeline; touched by one task at a time (the startup, then its polling).</summary>
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
