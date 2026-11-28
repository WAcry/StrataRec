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

    /// <summary>The host's secret store, which instances resolve their credentials through when they load.</summary>
    public ISecretResolver? Secrets { get; init; }
}

/// <summary>
/// Owns what the engine's plans bind: the local feature tables, and the instances bindings name (service clients,
/// feature connectors).
/// </summary>
/// <remarks>
/// <para>If the first load fails, the table or instance is never handed out again: the plans waiting for it get a
/// failure naming the binding (and for a table, the bad file), and the next plan with the same identity starts a new
/// one. A source that fails after the first load leaves the table serving what it has; the failure is logged and
/// counted, and the source runs again from the table's <see cref="LocalFeatureTable.Position"/> after a delay that
/// doubles with each failure in a row (reset when a run made progress), up to
/// <see cref="BindingHostOptions.MaxRestartDelay"/>.</para>
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

    /// <summary>Instances (service clients, connectors) some lease holds.</summary>
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
            stopping.Add(entry.Completion);
        }

        var disposing = new List<Task>(clients.Count);
        foreach (var client in clients)
        {
            _ = client.CancelLoad(); // the disposal waits for its callbacks; Disposed below waits for the disposal
            disposing.Add(client.Disposed);
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

    internal TableEntry Acquire(LocalTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tables.TryGetValue(spec.Key, out var held) && !held.FirstLoadFailed)
            {
                held.Table.CheckRetune(spec.Options);
                held.References++;
                return held;
            }

            // The failed one stays with the leases that hold it until they let go.
            var entry = new TableEntry(this, spec, new LocalFeatureTable(spec.Schema, spec.Options)) { References = 1 };
            _tables[spec.Key] = entry;
            _entries.Add(entry);
            return entry;
        }
    }

    /// <summary>
    /// Under the host's lock, so two plans published at once leave the limits of one of them whole.
    /// </summary>
    internal void Serve(TableEntry entry, LocalFeatureTableOptions options)
    {
        lock (_gate)
        {
            entry.Table.Retune(options); // checked when the table was acquired
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

            var callbacks = _stop.CancelObservedAsync();
            _ready.TrySetCanceled();
            _ = StoppedAsync(run ?? Task.CompletedTask, callbacks);
            return run is not null;
        }

        /// <summary>
        /// The entry (and its token source, never disposed) stays alive while either runs, past a shutdown that gave up
        /// waiting. Diagnostics never change when the entry counts as stopped: a logger that throws still leaves the
        /// wait for the run in place.
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
                }

                _ready.TrySetResult();
            }
        }

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
                        return;
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
                    }

                    _ready.TrySetException(error);
                    return;
                }

                failures = Table.Completions == before ? failures + 1 : 1;
                var delay = Backoff(failures);
                try
                {
                    LogRestarting(_host._logger, failure, Spec.Name, Table.Position.ToString(), delay.TotalMilliseconds, failures);
                }
                catch (Exception)
                {
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
