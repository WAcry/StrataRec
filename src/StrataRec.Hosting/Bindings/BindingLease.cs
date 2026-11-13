using System.Globalization;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Features.Bindings;
using StrataRec.Features.Local;
using StrataRec.Models;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting.Bindings;

/// <summary>A table or an instance of a plan that did not load: where, why and how to fix it, as a diagnostic.</summary>
/// <param name="Code"><c>LOCAL_TABLE_LOAD_FAILED</c> or <c>BINDING_LOAD_FAILED</c>.</param>
/// <param name="Exception">A <see cref="LocalTableLoadException"/> or a <see cref="BindingLoadException"/>.</param>
public sealed record BindingLoadFailure(string Code, string Artifact, string Pointer, string Message, string? Hint, Exception Exception);

/// <summary>
/// Everything one plan binds (design 6.4, 6.6), taken from its <see cref="BindingHost"/>: the plan compiles with it
/// (<see cref="IPlanBindings"/>), the engine waits for its loads before serving the plan, and each request of the plan
/// enters it, so the plan's instances outlive the plan by as long as anything of its requests can still call them.
/// </summary>
/// <remarks>
/// <para><b>One reference per identity.</b> Acquiring what the lease already holds (the plan compiles twice when it waited
/// for model signatures, and its validation passes compile its nodes again) returns it without another reference.</para>
/// <para><b>Retiring.</b> <see cref="Dispose"/> retires the lease: when its plan is replaced, when its load failed or was
/// cancelled, when the engine is disposed. Its tables are released at once (requests read the versions they pinned).
/// Its instances are released once the lease is retired and its last user has left: a request enters with
/// <see cref="TryEnter"/> and leaves with <see cref="RequestEnded"/>; each run of it joins, and its request memory leaves
/// once the run and every call it started have ended, so a call abandoned at a timeout keeps its client alive until it
/// returns. A signature lookup is a user too, from the moment it is asked until the client answered, so retiring the
/// lease (which cancels the lookup) never disposes a client inside its <see cref="IModelClient.GetSignatureAsync"/>. One
/// count holds them all, so no order of these events releases an instance a user still holds.</para>
/// <para><b>Model signatures</b> (design 6.9). <see cref="Signature"/> asks a client once per (model, version) and keeps
/// the answer for the lease's compiles. An answer that needs the client loaded or I/O is pending during the first compile;
/// <see cref="CompileAsync{T}"/> then waits for every pending answer, concurrently and within
/// <see cref="BindingHostOptions.LoadTimeout"/>, and compiles the plan again, which counts.</para>
/// </remarks>
public sealed class BindingLease : IPlanBindings, IRequestEndObserver, IDisposable
{
    /// <summary>The bit of <see cref="_state"/> that says the lease is retired; the bits below count its requests.</summary>
    private const int Retired = 1 << 30;

    private readonly BindingHost _host;
    private readonly object _gate = new();
    private readonly List<BindingHost.TableEntry> _tables = new();
    private readonly Dictionary<string, BindingHost.TableEntry> _tablesByIdentity = new(StringComparer.Ordinal);
    private readonly List<BindingHost.ClientEntry> _clients = new();
    private readonly Dictionary<BindingKey, BindingHost.ClientEntry> _clientsByKey = new();
    private readonly Dictionary<object, BindingHost.ClientEntry> _clientsByInstance = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IModelClient, Dictionary<(string Model, string? Version), Task<SignatureLookup>>> _signatures =
        new(ReferenceEqualityComparer.Instance);
    private readonly CancellationTokenSource _lookups = new(); // cancels the signature lookups; never disposed (see TableEntry)
    private bool _lookupsWaited;
    private bool _lookupsCancelled;
    private bool _answeredPending;
    private bool _disposed;
    private int _state;
    private int _clientsReleased;

    internal BindingLease(BindingHost host) => _host = host;

    /// <summary>The leased tables, in the order the plan acquired them.</summary>
    public IReadOnlyList<LocalFeatureTable> Tables
    {
        get
        {
            lock (_gate)
            {
                var tables = new List<LocalFeatureTable>(_tables.Count);
                foreach (var entry in _tables)
                {
                    tables.Add(entry.Table);
                }

                return tables;
            }
        }
    }

    /// <summary>The leased instances, in the order the plan acquired them.</summary>
    public IReadOnlyList<object> Instances
    {
        get
        {
            lock (_gate)
            {
                var instances = new List<object>(_clients.Count);
                foreach (var entry in _clients)
                {
                    instances.Add(entry.Instance);
                }

                return instances;
            }
        }
    }

    public LocalFeatureTable Acquire(LocalTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tablesByIdentity.TryGetValue(spec.Identity, out var held))
            {
                return held.Table;
            }

            var entry = _host.Acquire(spec);
            _tables.Add(entry);
            _tablesByIdentity[spec.Identity] = entry;
            return entry.Table;
        }
    }

    public T Acquire<T>(BindingSpec spec, Func<BindingContext, T> create)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(spec);
        var key = spec.Key;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clientsByKey.TryGetValue(key, out var held))
            {
                return (T)held.Instance;
            }
        }

        // Outside the lease's lock: the host may call the factory. One compile at a time uses a lease, so nobody adds the
        // same key meanwhile; a disposal meanwhile is handled below.
        var entry = _host.AcquireClient(spec, create);
        bool retired;
        lock (_gate)
        {
            retired = _disposed;
            if (!retired)
            {
                _clients.Add(entry);
                _clientsByKey[key] = entry;
                _clientsByInstance[entry.Instance] = entry;
            }
        }

        if (retired)
        {
            _host.Release(entry);
            throw new ObjectDisposedException(nameof(BindingLease));
        }

        return (T)entry.Instance;
    }

    public SignatureLookup Signature(IModelClient client, string model, string? version)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!_host.Options.Load && client is ILoadable)
        {
            return SignatureLookup.Unchecked; // an engine that loads nothing (replay) cannot ask it
        }

        Task<SignatureLookup>? answer;
        BindingHost.ClientEntry? entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clientsByInstance.TryGetValue(client, out entry))
            {
                throw new ArgumentException("The client was not acquired through this lease.", nameof(client));
            }

            if (!_signatures.TryGetValue(client, out var versions))
            {
                versions = new Dictionary<(string, string?), Task<SignatureLookup>>();
                _signatures[client] = versions;
            }

            versions.TryGetValue((model, version), out answer);
        }

        if (answer is null)
        {
            // A lookup is a user of the lease, as a run is: retiring the lease cancels it, but the instance is disposed
            // only after the client returned. One compile at a time asks.
            if (!TryEnter())
            {
                throw new ObjectDisposedException(nameof(BindingLease));
            }

            answer = LookupAsync(entry, client, model, version);
            lock (_gate)
            {
                _signatures[client][(model, version)] = answer;
            }
        }

        if (answer.IsCompleted)
        {
            return answer.Result; // LookupAsync never faults
        }

        if (Volatile.Read(ref _lookupsWaited))
        {
            return new SignatureLookup(SignatureStatus.Failed, null,
                string.Create(CultureInfo.InvariantCulture, $"The client did not return the signature within {_host.Options.LoadTimeout.TotalMilliseconds} ms."),
                "check the model server, or raise BindingHostOptions.LoadTimeout");
        }

        Volatile.Write(ref _answeredPending, true); // this compile skipped a check, so it cannot be the one that counts
        return SignatureLookup.Pending;
    }

    /// <summary>
    /// Compiles a plan with this lease as an engine load does: once, and once more when a check of the first compile was
    /// answered <see cref="SignatureStatus.Pending"/>, after waiting for the lookups (concurrently, within
    /// <see cref="BindingHostOptions.LoadTimeout"/>); the second compile has every answer (or a timeout error) and is the
    /// one returned. Every lookup runs off the compile's thread, so a plan that checks model signatures normally compiles
    /// twice; it compiles once only when every answer was already in when its check asked, and then every check ran.
    /// </summary>
    /// <param name="compile">Compiles the plan with the bindings it is given (this lease).</param>
    /// <param name="cancellationToken">Cancels the wait and the lookups; the instances' loads go on.</param>
    public async Task<T> CompileAsync<T>(Func<IPlanBindings, T> compile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compile);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        // The caller giving up cancels the lookups through the one path that waits for their callbacks (CancelLookups).
        using var giveUp = cancellationToken.Register(static lease => ((BindingLease)lease!).CancelLookups(), this);

        // Whether the compile skipped a check decides, not whether lookups are still pending afterwards: a lookup that was
        // pending for one pass (the baseline) may have answered before the next (an arm), and that compile checked some
        // passes and not others.
        Volatile.Write(ref _answeredPending, false);
        var compilation = compile(this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Volatile.Read(ref _answeredPending))
        {
            return compilation;
        }

        var pending = PendingLookups();
        if (pending.Count > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(_host.Options.LoadTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The second compile reports what is still unanswered.
            }
        }

        Volatile.Write(ref _lookupsWaited, true);
        compilation = compile(this);
        cancellationToken.ThrowIfCancellationRequested();
        return compilation;
    }

    /// <summary>
    /// Starts the loads of the leased tables and instances that have not started, and waits until every one has completed
    /// its first load, or one of them has failed. Returns the failures, each naming its binding (and a table's bad file);
    /// empty when everything is loaded (at once when the lease holds nothing to load, or the host loads nothing).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, also before the call
    /// when there is nothing to wait for; the loads go on.</exception>
    public async Task<IReadOnlyList<BindingLoadFailure>> WaitLoadedAsync(CancellationToken cancellationToken = default)
    {
        List<BindingHost.TableEntry> tables;
        List<BindingHost.ClientEntry> clients;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            tables = new List<BindingHost.TableEntry>(_tables);
            clients = new List<BindingHost.ClientEntry>(_clients);
        }

        cancellationToken.ThrowIfCancellationRequested(); // whether or not there is anything to wait for

        var pending = new List<Task>(tables.Count + clients.Count);
        if (_host.Options.Load)
        {
            foreach (var entry in tables)
            {
                entry.Start();
                if (!entry.Ready.IsCompletedSuccessfully)
                {
                    pending.Add(entry.Ready);
                }
            }
        }

        var ready = new Task[clients.Count];
        for (var i = 0; i < clients.Count; i++)
        {
            ready[i] = clients[i].Ready();
            if (!ready[i].IsCompletedSuccessfully)
            {
                pending.Add(ready[i]);
            }
        }

        // Stop at the first failure: the plan cannot be served, and a large table still loading should not delay the error.
        // Every load that ended other than successfully is a failure below, whatever it ended with, so stopping early
        // always has a failure to show and the plan is never served without its loads.
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!done.IsCompletedSuccessfully)
            {
                break;
            }

            pending.Remove(done);
        }

        var failures = new List<BindingLoadFailure>();
        if (_host.Options.Load)
        {
            foreach (var entry in tables)
            {
                if (TableFailure(entry) is { } failure)
                {
                    failures.Add(new BindingLoadFailure("LOCAL_TABLE_LOAD_FAILED", failure.Artifact, failure.Pointer, failure.Message, failure.Hint, failure));
                }
            }
        }

        for (var i = 0; i < clients.Count; i++)
        {
            if (ClientFailure(clients[i].Spec, ready[i]) is { } failure)
            {
                failures.Add(new BindingLoadFailure("BINDING_LOAD_FAILED", failure.Artifact, failure.Pointer, failure.Message, failure.Hint, failure));
            }
        }

        return failures;
    }

    /// <summary>The failure of a table's first load that has ended other than successfully, whatever it ended with; null otherwise.</summary>
    private static LocalTableLoadException? TableFailure(BindingHost.TableEntry entry) => entry.Ready switch
    {
        { IsCompletedSuccessfully: true } or { IsCompleted: false } => null,
        { IsCanceled: true } => new LocalTableLoadException(entry.Spec, new OperationCanceledException("the engine's binding host was stopped before the load completed")),
        var faulted => faulted.Exception!.InnerException as LocalTableLoadException
                       ?? new LocalTableLoadException(entry.Spec, faulted.Exception.InnerException ?? faulted.Exception),
    };

    /// <summary>The failure of an instance's load that has ended other than successfully, whatever it ended with; null otherwise.</summary>
    private static BindingLoadException? ClientFailure(BindingSpec spec, Task ready) => ready switch
    {
        { IsCompletedSuccessfully: true } or { IsCompleted: false } => null,
        { IsCanceled: true } => new BindingLoadException(spec, "the engine's binding host was stopped before the load completed", string.Empty, null, null),
        var faulted => faulted.Exception!.InnerException as BindingLoadException
                       ?? new BindingLoadException(spec, ExceptionText.Of(faulted.Exception.InnerException ?? faulted.Exception), string.Empty, null,
                           faulted.Exception.InnerException),
    };

    /// <summary>
    /// A request of the lease's plan starts: false once the lease is retired (its plan was replaced, so the caller reads
    /// the current plan again, or the engine was disposed). A true return obliges one <see cref="RequestEnded"/>.
    /// </summary>
    public bool TryEnter()
    {
        var state = Volatile.Read(ref _state);
        while (true)
        {
            if ((state & Retired) != 0)
            {
                return false;
            }

            var seen = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (seen == state)
            {
                return true;
            }

            state = seen;
        }
    }

    /// <summary>
    /// One more user while the caller holds an entry (<see cref="TryEnter"/>), so it cannot fail: a run of the entered
    /// request, whose request memory ends it (<see cref="RequestEnded"/>) once the run and every call it started have
    /// ended, which may be after the request returned (a call abandoned at a timeout runs on inside its client).
    /// </summary>
    internal IRequestEndObserver Join()
    {
        var state = Interlocked.Increment(ref _state);
        System.Diagnostics.Debug.Assert((state & (Retired - 1)) > 1, "Join without an entry.");
        return this;
    }

    /// <summary>An entry, a run or a signature lookup has ended: the last one of a retired lease releases its instances.</summary>
    public void RequestEnded()
    {
        var state = Interlocked.Decrement(ref _state);
        System.Diagnostics.Debug.Assert((state & (Retired - 1)) != Retired - 1, "RequestEnded without TryEnter.");
        if (state == Retired)
        {
            ReleaseClients();
        }
    }


    /// <summary>
    /// Retires the lease: no request enters any more; its tables are released now, its instances once its last request has
    /// ended (now, when none runs). Pending signature lookups are cancelled.
    /// </summary>
    public void Dispose()
    {
        List<BindingHost.TableEntry> tables;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            tables = new List<BindingHost.TableEntry>(_tables);
            _tables.Clear();
            _tablesByIdentity.Clear();
        }

        CancelLookups(); // before the lease is marked retired, so the callbacks of the cancellation can count as a user
        foreach (var entry in tables)
        {
            _host.Release(entry);
        }

        var previous = Interlocked.Or(ref _state, Retired);
        if (previous == 0)
        {
            ReleaseClients();
        }
    }

    /// <summary>The instances go back to the host: exactly once, when the lease is retired and no request of it runs.</summary>
    private void ReleaseClients()
    {
        if (Interlocked.Exchange(ref _clientsReleased, 1) != 0)
        {
            return;
        }

        List<BindingHost.ClientEntry> clients;
        lock (_gate)
        {
            clients = new List<BindingHost.ClientEntry>(_clients);
            _clients.Clear();
            _clientsByKey.Clear();
            _clientsByInstance.Clear();
        }

        foreach (var entry in clients)
        {
            _host.Release(entry); // disposal runs on the pool, never on this (possibly a client's) thread
        }
    }

    /// <summary>
    /// Cancels the signature lookups, once: when the lease retires, or when the compile's caller gives up first. The
    /// callbacks the clients registered on the lookups' token are their code, and may go on using the instance after the
    /// lookup returned: the cancellation enters the lease as a user and leaves once they have run (on the pool, never
    /// here), so no instance is disposed under one. Their exceptions are logged.
    /// </summary>
    private void CancelLookups()
    {
        lock (_gate)
        {
            // Under the lock, and before Dispose marks the lease retired (Dispose calls this first): whoever cancels first
            // always enters.
            if (_lookupsCancelled || !TryEnter())
            {
                return;
            }

            _lookupsCancelled = true;
        }

        _ = EndAfterAsync(_lookups.CancelAsync());
    }

    /// <summary>Leaves the lease once the lookups' cancellation callbacks have run; a failure of theirs is logged.</summary>
    private async Task EndAfterAsync(Task callbacks)
    {
        try
        {
            await callbacks.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.LookupCancellationCallbacksFailed(callbacks.Exception ?? ex);
        }
        finally
        {
            RequestEnded();
        }
    }

    private List<Task> PendingLookups()
    {
        var pending = new List<Task>();
        lock (_gate)
        {
            foreach (var (_, versions) in _signatures)
            {
                foreach (var (_, answer) in versions)
                {
                    if (!answer.IsCompleted)
                    {
                        pending.Add(answer);
                    }
                }
            }
        }

        return pending;
    }

    /// <summary>
    /// The client's answer once it can give one: after its load; never faults. The client's method runs on the pool, its
    /// synchronous part included, so a method that blocks before it returns its task holds up neither the compile nor the
    /// bounded wait for the answers (<see cref="CompileAsync{T}"/>); its answer is then pending, and late. The caller
    /// entered the lease for the lookup, and this ends that entry once the client returned.
    /// </summary>
    private async Task<SignatureLookup> LookupAsync(BindingHost.ClientEntry entry, IModelClient client, string model, string? version)
    {
        try
        {
            try
            {
                await entry.Ready().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return SignatureLookup.Unchecked; // the load failed: the plan is rejected with that failure, at the binding
            }

            var token = _lookups.Token;
            if (token.IsCancellationRequested)
            {
                return SignatureLookup.Failure(new OperationCanceledException(token)); // nobody waits for the answer any more
            }

            try
            {
                var signature = await Task.Run(() => client.GetSignatureAsync(model, version, token).AsTask(), CancellationToken.None).ConfigureAwait(false);
                return new SignatureLookup(SignatureStatus.Known, signature);
            }
            catch (Exception ex)
            {
                return SignatureLookup.Failure(ex);
            }
        }
        finally
        {
            RequestEnded();
        }
    }
}
