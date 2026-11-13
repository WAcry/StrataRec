using Microsoft.ML.OnnxRuntime;
using StrataRec.Core.Bindings;

namespace StrataRec.Models.Onnx;

/// <summary>
/// <c>local_onnx@1</c>: ONNX models run in process with ONNX Runtime (CPU). A binding names one model: <c>uri</c> is the
/// file a call without a version runs, and <c>versions</c> maps version ids to files (see <see cref="OnnxSettings"/>).
/// </summary>
/// <remarks>
/// <para><b>Load.</b> Every file is opened when an engine loads the first plan that uses the binding
/// (<see cref="LoadAsync"/>), never on the request path, and each distinct file is one session, shared by the versions that
/// name it. A version's signature comes from its session's metadata: float32 and int64 tensors map to
/// <see cref="TensorElementType"/>; any other type fails the load at the setting that names the file. The engine shares
/// the client, so its sessions, by binding identity: a configuration change that leaves the binding as it is keeps them.</para>
/// <para><b>Calls</b> run on the thread pool, so the caller's bound (the node's budget) cuts a slow run off; the run itself
/// is terminated in ONNX Runtime once the call is cancelled. Each uses <c>intra_op_threads</c> threads (default 1: many
/// requests scoring at once already use the cores). Tensors wrap the caller's buffers, inputs and outputs alike.</para>
/// </remarks>
public sealed class OnnxModelClient : IModelClient, ILoadable, IDisposable
{
    private static readonly Func<object?, ModelStatus> RunOnPool = static state => ((Invocation)state!).Run();

    private readonly string _model;
    private readonly OnnxSettings _settings;
    private readonly object _gate = new();
    private Sessions? _sessions;
    private bool _disposed;

    internal OnnxModelClient(string model, OnnxSettings settings)
    {
        _model = model;
        _settings = settings;
    }

    public string ClientType => OnnxModelClientFactory.Type;

    /// <summary>Opens every model file of the binding, once.</summary>
    /// <exception cref="BindingException">A file is missing, not a model, or has an unsupported tensor type; at its setting.</exception>
    public ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions is not null)
            {
                return ValueTask.CompletedTask;
            }
        }

        // Synchronous file I/O: the engine runs loads on the thread pool, off any request.
        var opened = new Dictionary<string, OnnxSession>(StringComparer.Ordinal);
        var options = new SessionOptions
        {
            IntraOpNumThreads = _settings.IntraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        try
        {
            var model = Open(_settings.Model, opened, options, cancellationToken);
            var versions = new Dictionary<string, OnnxSession>(StringComparer.Ordinal);
            foreach (var (version, file) in _settings.Versions)
            {
                versions[version] = Open(file, opened, options, cancellationToken);
            }

            var sessions = new Sessions(options, model, versions, opened);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sessions = sessions;
            }

            return ValueTask.CompletedTask;
        }
        catch
        {
            foreach (var (_, session) in opened)
            {
                session.Dispose();
            }

            options.Dispose();
            throw;
        }
    }

    /// <summary>The signature of a version from its session; known once the client is loaded.</summary>
    public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken)
    {
        if (!string.Equals(model, _model, StringComparison.Ordinal))
        {
            return ValueTask.FromException<ModelSignature>(new ArgumentException($"This client serves model '{_model}', not '{model}'.", nameof(model)));
        }

        if (Volatile.Read(ref _sessions) is not { } sessions)
        {
            return ValueTask.FromException<ModelSignature>(new InvalidOperationException("The client has not loaded its model files: an engine asks after the load."));
        }

        return sessions.TryGet(version, out var session)
            ? ValueTask.FromResult(session.Signature)
            : ValueTask.FromException<ModelSignature>(UnknownVersion(sessions, version!));
    }

    public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
    {
        if (Session(call, out var error) is not { } session)
        {
            return ValueTask.FromResult(ModelStatus.Failed(error!));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ModelStatus>(cancellationToken);
        }

        return new ValueTask<ModelStatus>(Task.Factory.StartNew(RunOnPool, new Invocation(session, call, outputs, cancellationToken), cancellationToken,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
    }

    /// <summary><see cref="InvokeAsync"/> on the current thread, for measuring what a run itself allocates.</summary>
    internal ModelStatus Run(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
        Session(call, out var error) is { } session ? session.Run(call, outputs, cancellationToken) : ModelStatus.Failed(error!);

    /// <summary>The session a call runs, or why there is none.</summary>
    private OnnxSession? Session(ModelCall call, out string? error)
    {
        error = null;
        if (Volatile.Read(ref _sessions) is not { } sessions)
        {
            error = $"Model '{_model}' is not loaded.";
            return null;
        }

        if (!string.Equals(call.Model, _model, StringComparison.Ordinal))
        {
            error = $"This client serves model '{_model}', not '{call.Model}'.";
            return null;
        }

        if (!sessions.TryGet(call.Version, out var session))
        {
            error = UnknownVersion(sessions, call.Version!).Reason;
            return null;
        }

        return session;
    }

    /// <summary>Releases the sessions. The engine calls it only once no request can call the client any more.</summary>
    public void Dispose()
    {
        Sessions? sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = _sessions;
            _sessions = null;
        }

        sessions?.Dispose();
    }

    /// <summary>The session of the file, opened once however many versions name it.</summary>
    private static OnnxSession Open(OnnxModelFile file, Dictionary<string, OnnxSession> opened, SessionOptions options, CancellationToken cancellationToken)
    {
        if (opened.TryGetValue(file.Path, out var session))
        {
            return session;
        }

        cancellationToken.ThrowIfCancellationRequested();
        session = OnnxSession.Open(file, options);
        opened[file.Path] = session;
        return session;
    }

    private BindingException UnknownVersion(Sessions sessions, string version)
    {
        var known = new List<string>(sessions.Versions.Keys);
        known.Sort(StringComparer.Ordinal);
        return new BindingException("/versions", $"Model '{_model}' has no version '{version}'.",
            known.Count == 0
                ? $"add \"versions\": {{ \"{version}\": \"<model file>\" }} to the binding of '{_model}'"
                : $"its versions are {string.Join(", ", known)}; add '{version}' under 'versions' in the binding with its model file");
    }

    /// <summary>Sessions open (one per distinct file); 0 before the load.</summary>
    internal int SessionCount => Volatile.Read(ref _sessions)?.Count ?? 0;

    /// <summary>What a load opened: the default model's session, the versions' and the options they share.</summary>
    private sealed class Sessions(SessionOptions options, OnnxSession model, Dictionary<string, OnnxSession> versions, Dictionary<string, OnnxSession> byPath)
        : IDisposable
    {
        public Dictionary<string, OnnxSession> Versions { get; } = versions;

        public int Count => byPath.Count;

        public bool TryGet(string? version, out OnnxSession session)
        {
            if (version is null)
            {
                session = model;
                return true;
            }

            return Versions.TryGetValue(version, out session!);
        }

        public void Dispose()
        {
            foreach (var (_, session) in byPath)
            {
                session.Dispose();
            }

            options.Dispose();
        }
    }

    /// <summary>One call's arguments, handed to the pool without a closure.</summary>
    private sealed class Invocation(OnnxSession session, ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
    {
        public ModelStatus Run() => session.Run(call, outputs, cancellationToken);
    }
}

/// <summary>
/// Creates <c>local_onnx@1</c> clients (<see cref="OnnxModelClient"/>). Register it with the plan environment's model
/// clients: <c>ModelClientRegistry.CreateDefault().Add(new OnnxModelClientFactory())</c>.
/// </summary>
public sealed class OnnxModelClientFactory : IModelClientFactory
{
    public const string Type = "local_onnx@1";

    public string ClientType => Type;

    /// <summary>Checks the settings and reads no file: the client opens its files when an engine loads it.</summary>
    /// <exception cref="BindingException">A setting is unknown, missing or malformed.</exception>
    public IModelClient Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new OnnxModelClient(context.Name, OnnxSettings.Parse(context));
    }
}
