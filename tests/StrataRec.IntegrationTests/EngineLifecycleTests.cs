using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Local;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The engine's lifecycle with local tables (design 6.6, 10.3): loads, publishing and disposal under one lock. The
/// sample's <c>author_store</c> is loaded by a source the test drives, so every interleaving here is forced, not timed.
/// </summary>
public sealed class EngineLifecycleTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ADisposalDuringALoad_CancelsIt_ReleasesItsTables_AndLeavesASharedHostServing()
    {
        var sources = new GatedSources();
        var root = Workspace();
        using var host = new BindingHost();
        var engine = new RecEngine(Environment(root, sources), bindings: host);
        var loading = engine.LoadAsync(Compile(root));
        var source = await sources.Next().WaitAsync(Wait); // the load waits for the first load of the table

        await engine.DisposeAsync().AsTask().WaitAsync(Wait);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => loading.WaitAsync(Wait));
        Assert.True(source.Token.IsCancellationRequested);
        Assert.False(engine.TryGetPipeline("home_feed", out _));
        Assert.Equal(0, host.TableCount);
        await engine.DisposeAsync(); // once is enough
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.LoadAsync(Compile(root)));
        Assert.Equal(0, host.TableCount);

        // The host was passed in: the disposal left it running for its other engines.
        sources.Open();
        await using var other = new RecEngine(Environment(root, sources), bindings: host);
        Assert.True((await other.LoadAsync(Compile(root))).Applied);
        Assert.True((await other.ExecuteAsync(MetricsTests.Request("req-other"))).Succeeded);
    }

    [Fact]
    public async Task ADisposalRightBeforeTheLoadPublishes_PublishesNothing_AndReleasesTheLoadsTables()
    {
        var sources = new GatedSources();
        sources.Open();
        var root = Workspace();
        using var host = new BindingHost();
        var engine = new RecEngine(Environment(root, sources), bindings: host);
        engine.Publishing = () => engine.Dispose(); // the tables are loaded; the publish lock is next

        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.LoadAsync(Compile(root)));

        Assert.False(engine.TryGetPipeline("home_feed", out _));
        Assert.Equal(0, host.TableCount);
        Assert.True((await sources.Next()).Token.IsCancellationRequested);
    }

    [Fact]
    public async Task TwoEnginesSharingAHost_EachReturnOnlyTheirOwnReferences()
    {
        var sources = new GatedSources();
        sources.Open();
        var root = Workspace();
        using var host = new BindingHost();
        var first = new RecEngine(Environment(root, sources), bindings: host);
        var second = new RecEngine(Environment(root, sources), bindings: host);
        Assert.True((await first.LoadAsync(Compile(root))).Applied);
        Assert.True((await second.LoadAsync(Compile(root))).Applied);
        var source = await sources.Next();
        Assert.Equal(1, host.TableCount); // one table, held by both

        await first.DisposeAsync();

        Assert.False(source.Token.IsCancellationRequested); // a cancellation would be requested synchronously
        Assert.Equal(1, host.TableCount);
        Assert.True((await second.ExecuteAsync(MetricsTests.Request("req-second"))).Succeeded);

        await second.DisposeAsync();

        Assert.True(source.Token.IsCancellationRequested);
        Assert.Equal(0, host.TableCount);
        host.OpenLease().Dispose(); // not disposed: it is the caller's
        Assert.Equal(1, sources.Runs);
    }

    [Fact]
    public async Task TwoEnginesSharingAHost_WithSourceFactoriesOfTheirOwn_EachLoadAndServeTheirOwnTable()
    {
        // The same configuration, so the same table identity, but each engine's registry has its own factory for the
        // source type: the second engine's source must run, and its plan read what that source loaded.
        var mine = new GatedSources(followers: 5);
        var theirs = new GatedSources(followers: 7);
        mine.Open();
        theirs.Open();
        var root = Workspace();
        using var host = new BindingHost();
        await using var first = new RecEngine(Environment(root, mine), bindings: host);
        await using var second = new RecEngine(Environment(root, theirs), bindings: host);
        Assert.True((await first.LoadAsync(Compile(root))).Applied);
        Assert.True((await second.LoadAsync(Compile(root))).Applied);

        Assert.Equal((1, 1), (mine.Runs, theirs.Runs));
        Assert.Equal(2, host.TableCount);
        Assert.Equal((5.0, 7.0), (Followers(first), Followers(second)));
        Assert.True((await second.ExecuteAsync(MetricsTests.Request("req-second"))).Succeeded);
    }

    [Fact]
    public async Task ACancelledLoad_LeavesTheServedPlanAndTheSharedTableAsTheyWere()
    {
        var sources = new GatedSources();
        sources.Open();
        var root = Workspace();
        using var host = new BindingHost();
        await using var engine = new RecEngine(Environment(root, sources), bindings: host);
        Assert.True((await engine.LoadAsync(Compile(root), sequence: 1)).Applied);
        var source = await sources.Next();

        // Another configuration that shares the (ready) table, so nothing has to be waited for.
        ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"coarse_k\": 30,", "\"coarse_k\": 25,");
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAsync(Compile(root), sequence: 2, before.Token));

        using var late = new CancellationTokenSource();
        engine.Publishing = late.Cancel; // after compiling and waiting, right before the publish lock
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAsync(Compile(root), sequence: 3, late.Token));
        engine.Publishing = null;

        Assert.True(engine.TryGetPipeline("home_feed", out var served));
        Assert.Equal(1, served.Sequence);
        Assert.False(source.Token.IsCancellationRequested); // the served plan still holds the table: its source runs on
        Assert.Equal(1, host.TableCount);
        Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-after-cancel"))).Succeeded);
        Assert.True((await engine.LoadAsync(Compile(root), sequence: 4)).Applied);
    }

    [Fact]
    public async Task ACancelledLoad_OfAPlanWithoutTables_IsNotApplied()
    {
        var root = HomeFeedTestHost.LoadModified(root =>
        {
            File.WriteAllText(Path.Combine(root, "fixtures", "authors.json"), "{}");
            SampleBindings.Rebind(root, "author_store", $$"""{ "connector": "memory@1", "data_file": "fixtures/authors.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}} }""");
        }).Root;
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
        using var before = new CancellationTokenSource();
        before.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAsync(Compile(root), cancellationToken: before.Token));
        using var late = new CancellationTokenSource();
        engine.Publishing = late.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAsync(Compile(root), cancellationToken: late.Token));

        Assert.False(engine.TryGetPipeline("home_feed", out _));
        Assert.Equal(0, engine.Bindings.TableCount);
    }

    [Fact]
    public async Task APlanSwap_DoesNotWaitForTheCancellationCallbacksOfTheTablesItReleases()
    {
        var sources = new GatedSources();
        sources.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanup = new ManualResetEventSlim();
        sources.Loaded = (run, token) =>
        {
            if (run == 1)
            {
                token.Register(() =>
                {
                    entered.TrySetResult();
                    cleanup.Wait(); // the first table's source cleans up slowly
                });
            }
        };
        var root = Workspace();
        var host = new BindingHost(new BindingHostOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(50) });
        var engine = new RecEngine(Environment(root, sources), bindings: host);
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);

        try
        {
            // A changed binding: a new table, and the replaced plan's table is released by the swap. Loaded off the test
            // thread, so a swap that blocks fails the test instead of hanging it.
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"max_bytes\":\"16MiB\"", "\"max_bytes\":\"32MiB\"");
            var swap = await Task.Run(() => engine.LoadAsync(Compile(root))).WaitAsync(Wait);

            Assert.True(swap.Applied);
            await entered.Task.WaitAsync(Wait);
            Assert.False(cleanup.IsSet);
            Assert.Equal(1, host.TableCount);
            Assert.True((await engine.ExecuteAsync(MetricsTests.Request("req-swapped"))).Succeeded);

            // Shutdown is bounded by its timeout, also for the table still stopping.
            await engine.DisposeAsync();
            await Task.Run(() => host.DisposeAsync().AsTask()).WaitAsync(Wait);
            Assert.False(cleanup.IsSet);
        }
        finally
        {
            cleanup.Set();
        }
    }

    /// <summary>A copy of the sample whose <c>author_store</c> is loaded by <see cref="GatedSources"/>.</summary>
    private static string Workspace() => HomeFeedTestHost.LoadModified(root => SampleBindings.Rebind(root, "author_store",
        $$"""{ "mode": "local", "max_bytes": "16MiB", "bootstrap": { "source": "{{GatedSources.Type}}" } }""")).Root;

    /// <summary>Author 100's followers in the table the engine's served plan reads.</summary>
    private static double Followers(RecEngine engine)
    {
        Assert.True(engine.TryGetPipeline("home_feed", out var pipeline));
        var table = Assert.Single(pipeline.Bindings.Tables, t => t.Schema.Source == "author_store");
        var version = table.Pin();
        return version.Read(table.Schema.FieldIndex("a_followers"), version.Find(EntityKey.FromInt64(100))).AsFloat64();
    }

    private static PlanEnvironment Environment(string root, GatedSources sources) => new()
    {
        BaseDirectory = root,
        UpdateSources = FeatureUpdateSourceRegistry.CreateDefault().Add(sources),
    };

    private static PlanCompileRequest Compile(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return request!;
    }

    /// <summary>
    /// Update sources the test drives: each run is announced (<see cref="Next"/>), loads author 100 (with
    /// <paramref name="followers"/>) once the gate is open, calls <see cref="Loaded"/>, and then runs until it is cancelled.
    /// </summary>
    private sealed class GatedSources(double followers = 5) : IFeatureUpdateSourceFactory
    {
        public const string Type = "gated@1";

        private readonly double _followers = followers;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TaskCompletionSource<Run>> _runs = new();
        private int _runCount;
        private int _taken;

        public string SourceType => Type;

        public int Runs => Volatile.Read(ref _runCount);

        /// <summary>Called with the run's number (1 for the first) and its token, after it loaded.</summary>
        public Action<int, CancellationToken>? Loaded { get; set; }

        public void Open() => _gate.TrySetResult();

        /// <summary>The next run, in the order they started.</summary>
        public Task<Run> Next()
        {
            var index = Interlocked.Increment(ref _taken) - 1;
            return Slot(index).Task;
        }

        public IFeatureUpdateSource Create(string sourceName, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory) => new Source(this);

        private TaskCompletionSource<Run> Slot(int index)
        {
            lock (_runs)
            {
                while (_runs.Count <= index)
                {
                    _runs.Add(new TaskCompletionSource<Run>(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _runs[index];
            }
        }

        public sealed record Run(CancellationToken Token);

        private sealed class Source(GatedSources owner) : IFeatureUpdateSource
        {
            public string SourceType => Type;

            public async Task RunAsync(UpdatePosition from, IFeatureTableWriter writer, CancellationToken cancellationToken)
            {
                var run = Interlocked.Increment(ref owner._runCount);
                owner.Slot(run - 1).TrySetResult(new Run(cancellationToken));
                await owner._gate.Task.WaitAsync(cancellationToken);
                writer.BeginReload();
                var builder = writer.Begin(100L, 1);
                builder.Set(writer.Schema.FieldIndex("a_followers"), ScalarValue.FromFloat64(owner._followers));
                builder.Commit();
                writer.CompleteReload(UpdatePosition.Empty);
                owner.Loaded?.Invoke(run, cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}
