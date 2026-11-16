using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// A small workspace (pipelines reading a <c>memory_list@1</c> fixture, then a TopK) built into a file-system store, as
/// <c>stratarec build --store</c> builds it, with the pointers <c>stratarec publish</c> writes in the same directory.
/// </summary>
internal sealed class ReleaseFixture : IDisposable
{
    public ReleaseFixture()
    {
        Root = Directory.CreateTempSubdirectory("stratarec-release-ws-").FullName;
        StoreRoot = Directory.CreateTempSubdirectory("stratarec-release-store-").FullName;
        Store = new FileSystemArtifactStore(StoreRoot);
        Pointers = new FileSystemManifestSource(StoreRoot);
        foreach (var dir in new[] { "catalog", "bindings", "pipelines", "fixtures" })
        {
            Directory.CreateDirectory(Path.Combine(Root, dir));
        }

        File.WriteAllText(Path.Combine(Root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(Root, "fixtures", "ids.json"), """{ "items": [7, 8, 9] }""");
        File.WriteAllText(Path.Combine(Root, "bindings", "local.jsonc"),
            """{ "environment": "local", "candidate_sources": { "s": { "client": "memory_list@1", "data_file": "fixtures/ids.json" } } }""");
    }

    /// <summary>The workspace; relative paths in its bindings resolve against it.</summary>
    public string Root { get; }

    public string StoreRoot { get; }

    public FileSystemArtifactStore Store { get; }

    public FileSystemManifestSource Pointers { get; }

    /// <summary>The engine environment a node of this workspace runs with.</summary>
    public PlanEnvironment Environment => new() { BaseDirectory = Root };

    /// <summary>Writes pipeline <paramref name="id"/>: its <c>k</c> makes its artifact (and its manifests) differ.</summary>
    public ReleaseFixture Pipeline(string id, int k = 2, string? fallback = null)
    {
        var fallbackMember = fallback is null ? string.Empty : $"\"fallback_pipeline\": \"{fallback}\",";
        File.WriteAllText(Path.Combine(Root, "pipelines", id + ".jsonc"), $$"""
            { "pipeline_id": "{{id}}", "entity_key_type": "int64", {{fallbackMember}}
              "request_schema": { "user_id": { "type": "int64?" } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "s": 10 }, "k": {{k}} },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "top": { "order": 200, "op": "TopK@1", "config": { "score_col": "entity_id", "k": 3 } } },
              "output": { "limit_param": "k", "columns": { "source": "col.recall.primary_source" } } }
            """);
        return this;
    }

    /// <summary>Builds the pipeline as it is now into the store, manifest included; its content id.</summary>
    public async Task<(ReleaseManifest Manifest, string Id)> BuildAsync(string pipelineId, long seq)
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(Root, diagnostics);
        var manifest = await ManifestBuilder.BuildAsync(workspace, pipelineId, "local", new PlanEnvironment { BaseDirectory = Root }, Store, diagnostics, seq);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return (manifest!, await PutAsync(manifest!));
    }

    /// <summary>Stores a manifest, as a release controller does; its content id.</summary>
    public async Task<string> PutAsync(ReleaseManifest manifest)
    {
        var id = await Store.PutAsync(ArtifactSerializer.ToNode(manifest));
        Assert.Equal(ManifestBuilder.ContentId(manifest), id);
        return id;
    }

    /// <summary>Points the pipeline at a manifest, as <c>stratarec publish</c> does (without its checks).</summary>
    public Task PublishAsync(string pipelineId, string manifestId) => Pointers.WritePointerAsync(pipelineId, "local", manifestId);

    /// <summary>A copy of <paramref name="manifest"/> with other release fields.</summary>
    public static ReleaseManifest With(ReleaseManifest manifest, long? seq = null, IEnumerable<string>? requires = null) => new()
    {
        PipelineId = manifest.PipelineId,
        Environment = manifest.Environment,
        Seq = seq ?? manifest.Seq,
        Previous = manifest.Previous,
        Requires = requires is null ? manifest.Requires : [.. manifest.Requires, .. requires],
        Artifacts = manifest.Artifacts,
        Experiments = manifest.Experiments,
    };

    public static RecRequest Request(string pipelineId, string requestId = "r") => new()
    {
        PipelineId = pipelineId,
        RequestId = requestId,
        Inputs = new StrataRec.Core.Values.RequestInputs(),
        Deadline = TimeSpan.FromSeconds(30),
    };

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
        Directory.Delete(StoreRoot, recursive: true);
    }
}

/// <summary>An artifact store that counts reads and can hold the read of one blob until it is cancelled.</summary>
internal sealed class WatchedStore(IArtifactStore inner) : IArtifactStore
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    /// <summary>A read of this id waits until its token is cancelled.</summary>
    public string? Hold { get; set; }

    /// <summary>Completes when a held read has started.</summary>
    public TaskCompletionSource HeldReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a held read was cancelled.</summary>
    public TaskCompletionSource HeldReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A held read registers a callback on its token that throws, as a transport's cleanup may.</summary>
    public bool ThrowOnCancel { get; set; }

    public async ValueTask<byte[]?> TryGetAsync(string contentId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        if (contentId == Hold)
        {
            if (ThrowOnCancel)
            {
                // Not disposed when the wait ends: the wait's continuation may run inside the cancellation, before this
                // callback, and would unregister it.
                _ = cancellationToken.Register(static () => throw new InvalidOperationException("artifact cleanup failed"));
            }

            HeldReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                HeldReadCancelled.TrySetResult();
                throw;
            }
        }

        return await inner.TryGetAsync(contentId, cancellationToken);
    }

    public ValueTask<string> PutAsync(System.Text.Json.Nodes.JsonNode artifact, CancellationToken cancellationToken = default) => inner.PutAsync(artifact, cancellationToken);
}

/// <summary>
/// A clock whose time and timers move only when the test says (<see cref="Advance"/>), and that counts the timers set, so
/// a test can wait until a polling loop is waiting again (<see cref="AdvanceAsync"/>).
/// </summary>
internal sealed class ManualTimers : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private readonly List<(int Count, TaskCompletionSource Done)> _waiters = new();
    private DateTimeOffset _now = Start;
    private int _scheduled;

    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>How many timers have been set so far.</summary>
    public int Scheduled
    {
        get
        {
            lock (_gate)
            {
                return _scheduled;
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Completes once <paramref name="count"/> timers have been set in all.</summary>
    public Task WhenScheduledAsync(int count)
    {
        lock (_gate)
        {
            if (_scheduled >= count)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, done));
            return done.Task;
        }
    }

    /// <summary>Moves time and fires the timers that are due, on this thread.</summary>
    public void Advance(TimeSpan by)
    {
        var due = new List<ManualTimer>();
        lock (_gate)
        {
            _now += by;
            foreach (var timer in _timers)
            {
                if (timer.Due <= _now)
                {
                    due.Add(timer);
                }
            }

            foreach (var timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>Moves time, then waits until the next timer is set: the loop the timer woke is waiting again.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        var scheduled = Scheduled;
        Advance(by);
        await WhenScheduledAsync(scheduled + 1).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private void Set(ManualTimer timer, TimeSpan dueTime)
    {
        List<TaskCompletionSource>? ready = null;
        lock (_gate)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            timer.Due = _now + dueTime;
            _timers.Add(timer);
            _scheduled++;
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Count <= _scheduled)
                {
                    (ready ??= new()).Add(_waiters[i].Done);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var done in ready ?? [])
        {
            done.TrySetResult();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimers owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Set(this, dueTime); // one-shot: the polling sets a new delay each time
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Keeps every log entry, with its category; thread-safe. While <see cref="Fails"/> is set, it throws after keeping one.</summary>
internal sealed class ReleaseLog : ILoggerProvider
{
    private volatile bool _fails;

    public ConcurrentQueue<(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = new();

    /// <summary>Every entry throws, as a logger whose sink failed may.</summary>
    public bool Fails
    {
        get => _fails;
        set => _fails = value;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ReleaseLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            log.Entries.Enqueue((category, logLevel, eventId, formatter(state, exception), exception));
            if (log.Fails)
            {
                throw new InvalidOperationException("the log sink failed");
            }
        }
    }
}
