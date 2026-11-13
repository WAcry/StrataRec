using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrataRec.Core.Diagnostics;
using StrataRec.Features.Local;

namespace StrataRec.Hosting.LocalTables;

/// <summary>Options of a <see cref="LocalTableHost"/>.</summary>
public sealed class LocalTableHostOptions
{
    /// <summary>
    /// False creates tables without ever loading them: for engines that only replay (a replay answers local reads from
    /// the recording, so it needs no data and no snapshot files) and for tools that compile without serving. Requests
    /// that read such a table fail the features with an error saying it is not loaded.
    /// </summary>
    public bool LoadTables { get; init; } = true;

    /// <summary>Delay before the first restart of a failed update source; it doubles with every failure in a row.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay between restarts.</summary>
    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long <see cref="LocalTableHost.DisposeAsync"/> waits for cancelled update sources to return: their runs and
    /// the callbacks they registered on the cancellation token, together.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Owns the engine's local feature tables (design 6.6): creates one for each local (source, entity) a plan reads, shares
/// it with every plan whose <see cref="LocalTableSpec.Identity"/> is the same, runs its update source in the background,
/// restarts the source after a failure, and stops the source when no plan holds the table any more. The one component
/// with the table lifecycle; plans hold tables through a <see cref="LocalTableLease"/>.
/// </summary>
/// <remarks>
/// <para><b>Readiness.</b> A table is ready once its first load is complete (<see cref="LocalFeatureTable.IsLoaded"/>).
/// <see cref="LocalTableLease.WaitLoadedAsync"/> starts the sources of the leased tables and waits for that; the engine
/// serves a new plan only afterwards, so the old plan keeps serving while new tables load.</para>
/// <para><b>Failures.</b> If the first load fails, the table is never handed out again: the plans waiting for it get a
/// <see cref="LocalTableLoadException"/> naming the binding and the bad file, and the next plan with the same identity
/// starts a new table, so loading again really retries. A source that fails after the first load leaves the table
/// serving what it has; the failure is logged and counted, and the source runs again from the table's
/// <see cref="LocalFeatureTable.Position"/> after a delay that doubles with each failure in a row (reset when a run made
/// progress), up to <see cref="LocalTableHostOptions.MaxRestartDelay"/>.</para>
/// <para><b>Release.</b> A table no lease holds has its source cancelled. Requests of an older plan may still read it:
/// they pinned a version, which stays valid, so stopping a source never needs to wait for requests.</para>
/// <para><b>Stopping never runs source code on the caller's thread.</b> A source is cancelled with
/// <see cref="CancellationTokenSource.CancelAsync"/>: the callbacks it registered on its token run on the thread pool,
/// not on the thread that releases a lease (a plan swap) or disposes the host. Their task and the source's run make up
/// the entry's <see cref="Entry.Completion"/>, which shutdown waits for within one
/// <see cref="LocalTableHostOptions.ShutdownTimeout"/>; past it, the entry stays alive until both end, and a callback's
/// exception is logged, never thrown at the caller.</para>
/// <para>Thread-safe. Sources run off the request path, each on its own long-running task until its first await.</para>
/// </remarks>
public sealed partial class LocalTableHost : IAsyncDisposable, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byIdentity = new(StringComparer.Ordinal);
    private readonly HashSet<Entry> _entries = new(); // every entry whose source may still run, for shutdown
    private readonly ILogger _logger;
    private bool _disposed;

    public LocalTableHost(LocalTableHostOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        Options = options ?? new LocalTableHostOptions();
        if (Options.RestartDelay < TimeSpan.Zero || Options.MaxRestartDelay < Options.RestartDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Restart delays must be non-negative and MaxRestartDelay at least RestartDelay.");
        }

        _logger = loggerFactory?.CreateLogger<LocalTableHost>() ?? NullLogger<LocalTableHost>.Instance;
    }

    public LocalTableHostOptions Options { get; }

    /// <summary>Tables some lease holds.</summary>
    public int TableCount
    {
        get
        {
            lock (_gate)
            {
                return _byIdentity.Count;
            }
        }
    }

    /// <summary>A lease for the tables of one plan: compile the plan with it, wait for its tables, and dispose it when the
    /// plan is no longer served (or was never served).</summary>
    public LocalTableLease OpenLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return new LocalTableLease(this);
    }

    /// <summary>
    /// Cancels every update source and waits for them to return, also those of tables released earlier that are still
    /// stopping, at most <see cref="LocalTableHostOptions.ShutdownTimeout"/> for all of them.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Entry> entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = new List<Entry>(_entries);
            _byIdentity.Clear();
        }

        var stopping = new List<Task>(entries.Count);
        foreach (var entry in entries)
        {
            entry.Stop();
            stopping.Add(entry.Completion); // never faults: see Entry.StoppedAsync
        }

        try
        {
            await Task.WhenAll(stopping).WaitAsync(Options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var running = 0;
            foreach (var completion in stopping)
            {
                running += completion.IsCompleted ? 0 : 1;
            }

            LogShutdownTimeout(_logger, running, Options.ShutdownTimeout.TotalMilliseconds);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The held table of the spec's identity with one more reference, or a new one.</summary>
    internal Entry Acquire(LocalTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_byIdentity.TryGetValue(spec.Identity, out var held) && !held.FirstLoadFailed)
            {
                held.References++;
                return held;
            }

            // New, or the held one failed its first load: a fresh table, so another load really retries. The failed one
            // stays with the leases that hold it until they let go.
            var entry = new Entry(this, spec, new LocalFeatureTable(spec.Schema, spec.Options)) { References = 1 };
            _byIdentity[spec.Identity] = entry;
            _entries.Add(entry);
            return entry;
        }
    }

    internal void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.References > 0)
            {
                return;
            }

            if (_byIdentity.TryGetValue(entry.Spec.Identity, out var held) && held == entry)
            {
                _byIdentity.Remove(entry.Spec.Identity);
            }
        }

        if (entry.Stop())
        {
            LogStopped(_logger, entry.Spec.Name);
        }
    }

    private void Finished(Entry entry)
    {
        lock (_gate)
        {
            _entries.Remove(entry);
        }
    }

    /// <summary>One table, its source's run and its readiness.</summary>
    internal sealed class Entry
    {
        private readonly LocalTableHost _host;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _run;
        private bool _stopping;

        public Entry(LocalTableHost host, LocalTableSpec spec, LocalFeatureTable table)
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
                    static (_, state) => ((Entry)state!).Loaded(), this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
            var callbacks = _stop.CancelAsync();
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
                var version = Table.Pin();
                LogLoaded(_host._logger, Spec.Name, Spec.Bootstrap.SourceType, version.RecordCount, version.Bytes);
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
                    LogFirstLoadFailed(_host._logger, failure, Spec.Name, Spec.Artifact, Spec.Pointer, error.Message);
                    _ready.TrySetException(error);
                    return; // never handed out again (see Acquire), so never restarted
                }

                failures = Table.Completions == before ? failures + 1 : 1; // a run that made progress resets the backoff
                var delay = Backoff(failures);
                LogRestarting(_host._logger, failure, Spec.Name, Table.Position.ToString(), delay.TotalMilliseconds, failures);
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

/// <summary>
/// The tables of one plan (design 6.6): an <see cref="ILocalTableProvider"/> to compile the plan with, which takes a
/// reference on each table from its <see cref="LocalTableHost"/>. Disposing the lease drops the references; a table no
/// lease holds is stopped.
/// </summary>
public sealed class LocalTableLease : ILocalTableProvider, IDisposable
{
    private readonly LocalTableHost _host;
    private readonly List<LocalTableHost.Entry> _entries = new();
    private bool _disposed;

    internal LocalTableLease(LocalTableHost host) => _host = host;

    /// <summary>The leased tables, in the order the plan acquired them.</summary>
    public IReadOnlyList<LocalFeatureTable> Tables
    {
        get
        {
            lock (_entries)
            {
                var tables = new List<LocalFeatureTable>(_entries.Count);
                foreach (var entry in _entries)
                {
                    tables.Add(entry.Table);
                }

                return tables;
            }
        }
    }

    public LocalFeatureTable Acquire(LocalTableSpec spec)
    {
        lock (_entries)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = _host.Acquire(spec);
            _entries.Add(entry);
            return entry.Table;
        }
    }

    /// <summary>
    /// Starts the update sources of the leased tables that do not run yet, and waits until every table has completed
    /// its first load, or one of them has failed. Returns the failures, each naming its binding and the bad file; empty
    /// when every table is loaded (at once when the lease holds none, or the host does not load tables).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, also before the call
    /// when there is nothing to wait for; the loads go on.</exception>
    public async Task<IReadOnlyList<LocalTableLoadException>> WaitLoadedAsync(CancellationToken cancellationToken = default)
    {
        List<LocalTableHost.Entry> entries;
        lock (_entries)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entries = new List<LocalTableHost.Entry>(_entries);
        }

        cancellationToken.ThrowIfCancellationRequested(); // whether or not there is anything to wait for

        if (entries.Count == 0 || !_host.Options.LoadTables)
        {
            return Array.Empty<LocalTableLoadException>();
        }

        var pending = new List<Task>(entries.Count);
        foreach (var entry in entries)
        {
            entry.Start();
            if (!entry.Ready.IsCompletedSuccessfully)
            {
                pending.Add(entry.Ready);
            }
        }

        // Stop at the first failure: the plan cannot be served, and a large table still loading should not delay the error.
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!done.IsCompletedSuccessfully)
            {
                break;
            }

            pending.Remove(done);
        }

        var failures = new List<LocalTableLoadException>();
        foreach (var entry in entries)
        {
            if (entry.Ready.Exception?.InnerException is LocalTableLoadException failed)
            {
                failures.Add(failed);
            }
            else if (entry.Ready.IsCanceled)
            {
                failures.Add(new LocalTableLoadException(entry.Spec, new OperationCanceledException("the engine's table host was stopped before the load completed")));
            }
        }

        return failures;
    }

    public void Dispose()
    {
        List<LocalTableHost.Entry> entries;
        lock (_entries)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = new List<LocalTableHost.Entry>(_entries);
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            _host.Release(entry);
        }
    }
}
