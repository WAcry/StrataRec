using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Threading;
using StrataRec.Features.Bindings;
using StrataRec.Features.Local;

namespace StrataRec.Hosting.Bindings;

/// <summary>Options of a <see cref="BindingHost"/>.</summary>
public sealed class BindingHostOptions
{
    /// <summary>
    /// False creates tables and instances without ever loading them: for engines that only replay (a replay answers local
    /// reads and external calls from the recording, so it needs no data, no model files and no connections) and for tools
    /// that compile without serving. Requests that read such a table fail the features with an error saying it is not
    /// loaded; model signatures that need a loaded client are not checked.
    /// </summary>
    public bool Load { get; init; } = true;

    /// <summary>Delay before the first restart of a failed update source; it doubles with every failure in a row.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay between restarts.</summary>
    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest an instance's load (<see cref="ILoadable.LoadAsync"/>) may take, and the longest a plan load waits for
    /// the model signatures it asked for. A model file is read, or a connection made, within it, or the plans that need
    /// the instance are rejected. A local table's first load has no such bound: a large snapshot may take minutes.
    /// </summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long <see cref="BindingHost.DisposeAsync"/> waits, all together, for cancelled update sources to return (their
    /// runs and the callbacks they registered on the cancellation token) and for released instances to be disposed.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The host's secret store (design 6.4 item 4), which instances resolve their credentials through when they load.</summary>
    public ISecretResolver? Secrets { get; init; }
}

/// <summary>
/// Owns what the engine's plans bind (design 6.4, 6.6): the local feature tables, and the instances bindings name (model
/// clients, candidate sources, feature connectors). Each is created once per key and shared with every plan whose spec
/// has the same key (<see cref="LocalTableSpec.Key"/>, <see cref="BindingSpec.Key"/>: the identity of the configuration,
/// and the factory or connector object the data comes from, so engines with different registries never share), so a
/// plan swap takes over what is loaded instead of loading it again; each is loaded off the request path before a plan
/// that needs it is served, and stopped or disposed once nothing uses it. The one component with these lifecycles;
/// plans hold what they bind through one <see cref="BindingLease"/> each.
/// </summary>
/// <remarks>
/// <para><b>Readiness.</b> A table is ready once its first load is complete (<see cref="LocalFeatureTable.IsLoaded"/>); an
/// instance once its <see cref="ILoadable.LoadAsync"/> returned (at once when it has none). <see cref="BindingLease.WaitLoadedAsync"/>
/// starts the loads of what the lease holds and waits for them; the engine serves a new plan only afterwards, so the old
/// plan keeps serving while new tables and models load.</para>
/// <para><b>Failures.</b> If the first load fails, the table or instance is never handed out again: the plans waiting for
/// it get a failure naming the binding (and for a table, the bad file), and the next plan with the same identity starts
/// a new one, so loading again really retries. A source that fails after the first load leaves the table
/// serving what it has; the failure is logged and counted, and the source runs again from the table's
/// <see cref="LocalFeatureTable.Position"/> after a delay that doubles with each failure in a row (reset when a run made
/// progress), up to <see cref="BindingHostOptions.MaxRestartDelay"/>. An instance's load is bounded by
/// <see cref="BindingHostOptions.LoadTimeout"/>.</para>
/// <para><b>Release.</b> A table no lease holds has its source cancelled. Requests of an older plan may still read it:
/// they pinned a version, which stays valid, so stopping a source never needs to wait for requests. An instance is
/// different: a request of a replaced plan, or a call it abandoned at a timeout, may still be inside it, so the lease
/// lets go of its instances only once it is retired and its last request has ended (<see cref="BindingLease"/>). An
/// instance no lease holds then has its load cancelled and is disposed once that load returned.</para>
/// <para><b>Stopping and disposing never run their code on the caller's thread.</b> A source, or an instance's load, is
/// cancelled with <see cref="CancellationTokenSource.CancelAsync"/>: the callbacks it registered on its token run on the
/// thread pool, not on the thread that releases a lease (a plan swap, or the end of a request on a client's thread) or
/// disposes the host. Their task and the source's run make up the entry's <see cref="TableEntry.Completion"/>; an
/// instance is disposed on the thread pool once its load and those callbacks have returned
/// (<see cref="ClientEntry.Disposed"/>), whichever of the release and the shutdown cancelled first. Shutdown waits for both within one
/// <see cref="BindingHostOptions.ShutdownTimeout"/>; past it, the entries stay alive until they end, and an exception
/// of a callback or a disposal is logged, never thrown at the caller. Diagnostics never change a lifecycle: a logger
/// that throws leaves every wait in place.</para>
/// <para>Thread-safe. Sources run off the request path, each on its own long-running task until its first await;
/// instance loads run on the thread pool.</para>
/// </remarks>
public sealed partial class BindingHost : IAsyncDisposable, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<TableKey, TableEntry> _tables = new();
    private readonly HashSet<TableEntry> _entries = new(); // every entry whose source may still run, for shutdown
    private readonly Dictionary<BindingKey, ClientEntry> _clients = new();
    private readonly HashSet<ClientEntry> _clientEntries = new(); // every instance not yet disposed, for shutdown
    private readonly ILogger _logger;
    private bool _disposed;

    public BindingHost(BindingHostOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        Options = options ?? new BindingHostOptions();
        if (Options.RestartDelay < TimeSpan.Zero || Options.MaxRestartDelay < Options.RestartDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Restart delays must be non-negative and MaxRestartDelay at least RestartDelay.");
        }

        if (Options.LoadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "LoadTimeout must be positive.");
        }

        _logger = loggerFactory?.CreateLogger<BindingHost>() ?? NullLogger<BindingHost>.Instance;
    }

    public BindingHostOptions Options { get; }

    /// <summary>Tables some lease holds.</summary>
    public int TableCount
    {
        get
        {
            lock (_gate)
            {
                return _tables.Count;
            }
        }
    }

    /// <summary>Instances (model clients, candidate sources, connectors) some lease holds.</summary>
    public int InstanceCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>A lease for what one plan binds: compile the plan with it, wait for its loads, and dispose it when the plan
    /// is no longer served (or was never served).</summary>
    public BindingLease OpenLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return new BindingLease(this);
    }

    /// <summary>
    /// Cancels every update source and waits for them to return, also those of tables released earlier that are still
    /// stopping; cancels every instance's load and waits for the released instances to be disposed. At most
    /// <see cref="BindingHostOptions.ShutdownTimeout"/> for all of them. An instance a lease still holds (a request of its
    /// plan is still running, or a call it abandoned) is not disposed under it: it is disposed when that lease lets go, if
    /// that happens within the wait, and reported otherwise.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<TableEntry> entries;
        List<ClientEntry> clients;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = new List<TableEntry>(_entries);
            clients = new List<ClientEntry>(_clientEntries);
            _tables.Clear();
        }

        var stopping = new List<Task>(entries.Count);
        foreach (var entry in entries)
        {
            entry.Stop();
            stopping.Add(entry.Completion); // never faults: see TableEntry.StoppedAsync
        }

        var disposing = new List<Task>(clients.Count);
        foreach (var client in clients)
        {
            _ = client.CancelLoad(); // the disposal waits for its callbacks; Disposed below waits for the disposal
            disposing.Add(client.Disposed); // never faults: see ClientEntry.DisposeAsync
        }

        var all = new List<Task>(stopping);
        all.AddRange(disposing);
        try
        {
            await Task.WhenAll(all).WaitAsync(Options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var running = Pending(stopping);
            var held = Pending(disposing);
            try
            {
                if (running > 0)
                {
                    LogShutdownTimeout(_logger, running, Options.ShutdownTimeout.TotalMilliseconds);
                }

                if (held > 0)
                {
                    LogInstancesShutdownTimeout(_logger, held, Options.ShutdownTimeout.TotalMilliseconds);
                }
            }
            catch (Exception)
            {
                // A failing logger cannot report itself.
            }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static int Pending(List<Task> tasks)
    {
        var pending = 0;
        foreach (var task in tasks)
        {
            pending += task.IsCompleted ? 0 : 1;
        }

        return pending;
    }

    /// <summary>The held table of the spec's key with one more reference, or a new one.</summary>
    internal TableEntry Acquire(LocalTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tables.TryGetValue(spec.Key, out var held) && !held.FirstLoadFailed)
            {
                held.References++;
                return held;
            }

            // New, or the held one failed its first load: a fresh table, so another load really retries. The failed one
            // stays with the leases that hold it until they let go.
            var entry = new TableEntry(this, spec, new LocalFeatureTable(spec.Schema, spec.Options)) { References = 1 };
            _tables[spec.Key] = entry;
            _entries.Add(entry);
            return entry;
        }
    }

    internal void Release(TableEntry entry)
    {
        lock (_gate)
        {
            if (--entry.References > 0)
            {
                return;
            }

            if (_tables.TryGetValue(entry.Spec.Key, out var held) && held == entry)
            {
                _tables.Remove(entry.Spec.Key);
            }
        }

        if (entry.Stop())
        {
            try
            {
                LogStopped(_logger, entry.Spec.Name);
            }
            catch (Exception)
            {
                // A failing logger cannot report itself, and must not fail the release.
            }
        }
    }

    private void Finished(TableEntry entry)
    {
        lock (_gate)
        {
            _entries.Remove(entry);
        }
    }


    /// <summary>One table, its source's run and its readiness.</summary>
    internal sealed class TableEntry
    {
        private readonly BindingHost _host;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ObservedCancellationSource _stop = new(CancellationSite.LocalTable);
        private readonly object _gate = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _run;
        private bool _stopping;

        public TableEntry(BindingHost host, LocalTableSpec spec, LocalFeatureTable table)
        {
            _host = host;
            Spec = spec;
            Table = table;
        }

        public LocalTableSpec Spec { get; }

        public LocalFeatureTable Table { get; }

        /// <summary>Leases holding the table; guarded by the host's lock.</summary>
        public int References { get; set; }

        /// <summary>Completes when the first load is complete; faults with <see cref="LocalTableLoadException"/> when it
        /// failed; is cancelled when the table was released or the host stopped before.</summary>
        public Task Ready => _ready.Task;

        public bool FirstLoadFailed => _ready.Task.IsFaulted || _ready.Task.IsCanceled;

        /// <summary>
        /// Completes once the entry is stopped (<see cref="Stop"/>), its source's run has returned and the callbacks the
        /// source registered on its token have run. Never faults.
        /// </summary>
        public Task Completion => _stopped.Task;

        /// <summary>Starts the source unless it runs already (or was stopped).</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_run is not null || _stopping)
                {
                    return;
                }

                _ = Table.WhenLoaded.ContinueWith(
                    static (_, state) => ((TableEntry)state!).Loaded(), this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _run = RunAsync();
            }
        }

        /// <summary>
        /// Cancels the source without running any of its code here (see the host's remarks); true when it was running.
        /// <see cref="Completion"/> tells when it has stopped.
        /// </summary>
        public bool Stop()
        {
            Task? run;
            lock (_gate)
            {
                if (_stopping)
                {
                    return false;
                }

                _stopping = true;
                run = _run;
            }

            // The token observes the cancellation now; the callbacks registered on it run on the thread pool.
            var callbacks = _stop.CancelObservedAsync();
            _ready.TrySetCanceled();
            _ = StoppedAsync(run ?? Task.CompletedTask, callbacks);
            return run is not null;
        }

        /// <summary>
        /// Waits for the run and the cancellation callbacks, then leaves the host's set of entries. The entry (and its
        /// token source, never disposed) stays alive while either runs, past a shutdown that gave up waiting. A callback's
        /// exception is logged; the run handles its own failures, so anything escaping it is only observed. Diagnostics
        /// never change when the entry counts as stopped: a logger that throws still leaves the wait for the run in place.
        /// </summary>
        private async Task StoppedAsync(Task run, Task callbacks)
        {
            try
            {
                try
                {
                    await callbacks.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        LogCancellationCallbacksFailed(_host._logger, callbacks.Exception ?? ex, Spec.Name);
                    }
                    catch (Exception)
                    {
                        // A failing logger cannot report itself.
                    }
                }

                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // RunAsync catches what the source throws; this is a failing logger, which cannot report itself.
                }
            }
            finally
            {
                _host.Finished(this);
                _stopped.TrySetResult();
            }
        }

        /// <summary>The first load completed: logged before the plans waiting for it are released, so the event precedes theirs.</summary>
        private void Loaded()
        {
            if (!_ready.Task.IsCompleted)
            {
                try
                {
                    var version = Table.Pin();
                    LogLoaded(_host._logger, Spec.Name, Spec.Bootstrap.SourceType, version.RecordCount, version.Bytes);
                }
                catch (Exception)
                {
                    // A failing logger cannot report itself, and must not keep the plans waiting for a table that loaded.
                }

                _ready.TrySetResult();
            }
        }

        /// <summary>Runs the source until it is done or stopped, restarting it after failures once the table is loaded.</summary>
        private async Task RunAsync()
        {
            var token = _stop.Token;
            var failures = 0;
            while (true)
            {
                var before = Table.Completions;
                Exception failure;
                try
                {
                    // A source may load synchronously before its first await (snapshot_files@1 does), so it starts on a
                    // thread of its own rather than on the caller's or a pool thread.
                    await Task.Factory.StartNew(
                            () => Spec.Bootstrap.RunAsync(Table.Position, Table.Writer, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                        .Unwrap().ConfigureAwait(false);
                    if (Table.IsLoaded)
                    {
                        return; // the source has nothing more to do (a snapshot without reload)
                    }

                    failure = new InvalidOperationException(
                        $"{Spec.Bootstrap.SourceType} returned without loading the table: it completed no reload and wrote no checkpoint.");
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                // A source that failed inside a reload may have left it open; the next run starts from the last complete one.
                Table.Writer.AbortReload();
                StrataRecMetrics.LocalLoadFailures.Add(1, Table.MetricTags);
                if (!Table.IsLoaded)
                {
                    var error = new LocalTableLoadException(Spec, failure);
                    try
                    {
                        LogFirstLoadFailed(_host._logger, failure, Spec.Name, Spec.Artifact, Spec.Pointer, error.Message);
                    }
                    catch (Exception)
                    {
                        // A failing logger cannot report itself, and must not keep the plans waiting from the failure.
                    }

                    _ready.TrySetException(error);
                    return; // never handed out again (see Acquire), so never restarted
                }

                failures = Table.Completions == before ? failures + 1 : 1; // a run that made progress resets the backoff
                var delay = Backoff(failures);
                try
                {
                    LogRestarting(_host._logger, failure, Spec.Name, Table.Position.ToString(), delay.TotalMilliseconds, failures);
                }
                catch (Exception)
                {
                    // A failing logger cannot report itself, and must not end the restarts: the table would go stale unnoticed.
                }

                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private TimeSpan Backoff(int failures)
        {
            var options = _host.Options;
            var delay = options.RestartDelay;
            for (var i = 1; i < failures && delay < options.MaxRestartDelay; i++)
            {
                delay += delay;
            }

            return delay < options.MaxRestartDelay ? delay : options.MaxRestartDelay;
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "Local table {Table} loaded by {SourceType}: {Records} record(s), {Bytes} byte(s)")]
    private static partial void LogLoaded(ILogger logger, string table, string sourceType, int records, long bytes);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Error,
        Message = "Local table {Table} ({Artifact}{Pointer}) failed its first load; plans that read it are not served: {Error}")]
    private static partial void LogFirstLoadFailed(ILogger logger, Exception exception, string table, string artifact, string pointer, string error);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning,
        Message = "The update source of local table {Table} failed; the table keeps serving its data, and the source restarts from position {Position} in {DelayMs} ms (failure {Failures} in a row)")]
    private static partial void LogRestarting(ILogger logger, Exception exception, string table, string position, double delayMs, int failures);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Information, Message = "Local table {Table} is no longer used by any plan; its update source is stopped")]
    private static partial void LogStopped(ILogger logger, string table);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Warning,
        Message = "{Running} local table update source(s) did not return (or their cancellation callbacks did not finish) within {TimeoutMs} ms of shutdown")]
    private static partial void LogShutdownTimeout(ILogger logger, int running, double timeoutMs);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Warning,
        Message = "A cancellation callback of the update source of local table {Table} threw; the source is stopped regardless")]
    private static partial void LogCancellationCallbacksFailed(ILogger logger, Exception exception, string table);
}
