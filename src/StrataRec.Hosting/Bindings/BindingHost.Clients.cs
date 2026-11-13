using System.Globalization;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Features.Bindings;

namespace StrataRec.Hosting.Bindings;

/// <summary>The instances bindings name (model clients, candidate sources, connectors): sharing, loading and disposal.</summary>
public sealed partial class BindingHost
{
    /// <summary>
    /// The held instance of the spec's identity and factory with one more reference, or a new one from
    /// <paramref name="create"/>, called outside the host's lock (a factory only reads settings, but it is not ours to
    /// trust with a lock every lease takes).
    /// </summary>
    /// <exception cref="BindingException">The factory rejected the settings; nothing is held.</exception>
    internal ClientEntry AcquireClient<T>(BindingSpec spec, Func<BindingContext, T> create)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(spec);
        var key = spec.Key;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(key, out var held) && !held.Failed)
            {
                held.References++;
                return held;
            }
        }

        var created = new ClientEntry(this, spec, create(spec.Context(Options.Secrets)));
        ClientEntry? acquired = null;
        lock (_gate)
        {
            _clientEntries.Add(created); // even a loser is disposed, and shutdown waits for that
            if (!_disposed)
            {
                if (_clients.TryGetValue(key, out var held) && !held.Failed)
                {
                    held.References++; // another plan created it meanwhile: use that one, never loaded, and drop ours
                    acquired = held;
                }
                else
                {
                    // New, or the held one failed its load: a fresh instance, so another load really retries. The failed
                    // one stays with the leases that hold it until they let go.
                    created.References = 1;
                    _clients[key] = created;
                    acquired = created;
                }
            }
        }

        if (acquired != created)
        {
            created.Release();
        }

        return acquired ?? throw new ObjectDisposedException(nameof(BindingHost));
    }

    /// <summary>One lease let go; the last one releases the instance: its load is cancelled, and it is disposed once that returned.</summary>
    internal void Release(ClientEntry entry)
    {
        lock (_gate)
        {
            if (--entry.References > 0)
            {
                return;
            }

            var key = entry.Spec.Key;
            if (_clients.TryGetValue(key, out var held) && held == entry)
            {
                _clients.Remove(key);
            }
        }

        entry.Release();
    }

    private void Finished(ClientEntry entry)
    {
        lock (_gate)
        {
            _clientEntries.Remove(entry);
        }
    }

    /// <summary>One instance, its load and its disposal.</summary>
    internal sealed class ClientEntry
    {
        private readonly BindingHost _host;
        private readonly CancellationTokenSource _stop = new(); // cancels the load; never disposed (see TableEntry)
        private readonly object _gate = new();
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _run;
        private Task? _ready;
        private bool _released;

        public ClientEntry(BindingHost host, BindingSpec spec, object instance)
        {
            _host = host;
            Spec = spec;
            Instance = instance;
        }

        public BindingSpec Spec { get; }

        public object Instance { get; }

        /// <summary>Leases holding the instance; guarded by the host's lock.</summary>
        public int References { get; set; }

        /// <summary>The load failed (or timed out, or was cancelled): never handed out again.</summary>
        public bool Failed
        {
            get
            {
                lock (_gate)
                {
                    return _ready is { IsFaulted: true } or { IsCanceled: true };
                }
            }
        }

        /// <summary>Completes once the instance is disposed (after its load returned), or was not disposable. Never faults.</summary>
        public Task Disposed => _disposed.Task;

        /// <summary>
        /// Starts the load once (on the thread pool: a load may do its I/O synchronously) and returns its bounded outcome:
        /// completed when the instance can serve; faulted with <see cref="BindingLoadException"/> when the load failed or
        /// took longer than <see cref="BindingHostOptions.LoadTimeout"/>; cancelled when the instance was released or the
        /// host stopped first. Completed at once for an instance without a load, or when the host loads nothing.
        /// </summary>
        public Task Ready()
        {
            lock (_gate)
            {
                if (_ready is not null)
                {
                    return _ready;
                }

                if (Instance is not ILoadable loadable || !_host.Options.Load)
                {
                    return _ready = Task.CompletedTask;
                }

                if (_released)
                {
                    return _ready = Task.FromCanceled(new CancellationToken(canceled: true));
                }

                var token = _stop.Token;
                _run = Task.Run(() => loadable.LoadAsync(token).AsTask(), CancellationToken.None);
                return _ready = BoundAsync(_run);
            }
        }

        /// <summary>Cancels the load (shutdown); the instance stays with the leases that hold it.</summary>
        public void CancelLoad() => _ = _stop.CancelAsync(); // callbacks on the pool, not here

        /// <summary>No lease holds the instance any more: cancels its load and disposes it once that returned, on the pool.</summary>
        public void Release()
        {
            Task? run;
            lock (_gate)
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                run = _run;
            }

            CancelLoad();
            _ = Task.Run(() => DisposeAsync(run), CancellationToken.None);
        }

        private async Task BoundAsync(Task run)
        {
            var timeout = _host.Options.LoadTimeout;
            try
            {
                await run.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!run.IsCompleted)
            {
                throw Failure(new BindingLoadException(Spec,
                    string.Create(CultureInfo.InvariantCulture, $"it did not finish loading within {timeout.TotalMilliseconds} ms"),
                    string.Empty, "check what it loads (the file, the endpoint), or raise BindingHostOptions.LoadTimeout", null));
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                throw new OperationCanceledException("the instance was released, or the host stopped, before its load completed");
            }
            catch (Exception ex)
            {
                throw Failure(ex is BindingException binding
                    ? new BindingLoadException(Spec, binding.Reason, binding.Pointer, binding.Hint, binding)
                    : new BindingLoadException(Spec, ExceptionText.Of(ex), string.Empty, null, ex));
            }

            try
            {
                LogInstanceLoaded(_host._logger, Spec.Label, Spec.Artifact);
            }
            catch (Exception)
            {
                // A failing logger cannot report itself, and does not fail the load.
            }
        }

        private BindingLoadException Failure(BindingLoadException failure)
        {
            try
            {
                LogInstanceLoadFailed(_host._logger, failure.InnerException, Spec.Label, failure.Artifact, failure.Pointer, failure.Message);
            }
            catch (Exception)
            {
                // A failing logger cannot report itself.
            }

            return failure;
        }

        /// <summary>
        /// Waits for the load to return, then disposes the instance: never under a load still running, and never on the
        /// caller's thread. An instance whose load never returns is never disposed, which shutdown reports. A disposal's
        /// exception is logged.
        /// </summary>
        private async Task DisposeAsync(Task? run)
        {
            try
            {
                if (run is not null)
                {
                    try
                    {
                        await run.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Reported where it was awaited (Ready); disposing it is all that is left.
                    }
                }

                var disposed = true;
                try
                {
                    switch (Instance)
                    {
                        case IAsyncDisposable asyncDisposable:
                            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                            break;
                        case IDisposable disposable:
                            disposable.Dispose();
                            break;
                        default:
                            disposed = false;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        LogInstanceDisposeFailed(_host._logger, ex, Spec.Label, Spec.Artifact);
                    }
                    catch (Exception)
                    {
                        // A failing logger cannot report itself.
                    }

                    disposed = false;
                }

                if (disposed)
                {
                    try
                    {
                        LogInstanceDisposed(_host._logger, Spec.Label, Spec.Artifact);
                    }
                    catch (Exception)
                    {
                        // A failing logger cannot report itself.
                    }
                }
            }
            finally
            {
                _host.Finished(this);
                _disposed.TrySetResult();
            }
        }
    }

    [LoggerMessage(EventId = 1207, Level = LogLevel.Information, Message = "The {Instance} of {Artifact} is loaded")]
    private static partial void LogInstanceLoaded(ILogger logger, string instance, string artifact);

    [LoggerMessage(EventId = 1208, Level = LogLevel.Error,
        Message = "The {Instance} ({Artifact}{Pointer}) failed to load; plans that use it are not served: {Error}")]
    private static partial void LogInstanceLoadFailed(ILogger logger, Exception? exception, string instance, string artifact, string pointer, string error);

    [LoggerMessage(EventId = 1209, Level = LogLevel.Information,
        Message = "The {Instance} of {Artifact} is no longer used by any plan or request; it is disposed")]
    private static partial void LogInstanceDisposed(ILogger logger, string instance, string artifact);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Warning,
        Message = "{Held} instance(s) were not disposed within {TimeoutMs} ms of shutdown: a request or an abandoned call still uses them, or their load or disposal did not return")]
    private static partial void LogInstancesShutdownTimeout(ILogger logger, int held, double timeoutMs);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Warning, Message = "Disposing the {Instance} of {Artifact} threw")]
    private static partial void LogInstanceDisposeFailed(ILogger logger, Exception exception, string instance, string artifact);
}

/// <summary>
/// An instance a binding names failed to load (<see cref="ILoadable"/>), or did not finish within the load timeout: the
/// binding, the setting at fault when the instance said (a <see cref="BindingException"/>), and how to fix it.
/// </summary>
public sealed class BindingLoadException : Exception
{
    internal BindingLoadException(BindingSpec spec, string reason, string settingPointer, string? hint, Exception? innerException)
        : base($"The {spec.Label} failed to load: {reason}", innerException)
    {
        Artifact = spec.Artifact;
        Pointer = spec.Pointer + settingPointer;
        Hint = hint;
    }

    /// <summary>The bindings artifact, e.g. <c>bindings:local</c>.</summary>
    public string Artifact { get; }

    /// <summary>The binding's pointer, or its setting's, e.g. <c>/models/ctr_backup/uri</c>.</summary>
    public string Pointer { get; }

    public string? Hint { get; }
}
