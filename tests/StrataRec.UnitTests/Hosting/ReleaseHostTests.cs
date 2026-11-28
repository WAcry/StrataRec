using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;

namespace StrataRec.UnitTests.Hosting;

public sealed class ReleaseHostTests : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    private readonly ReleaseFixture _fixture = new();
    private readonly Reports _reports = new();
    private readonly ManualTimers _clock = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task TheHost_StartsFromThePublishedManifests_AndReportsThem()
    {
        var (_, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", id);
        using var host = Build(["p"]);
        var engine = host.Services.GetRequiredService<IRecEngine>();

        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "p" }, engine.PipelineIds());
        var response = await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken);
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal(id, response.ManifestId);
        var report = Assert.Single(_reports.All);
        Assert.Equal((ManifestLoadStatus.Applied, id, 1L), (report.Result.Status, report.Result.ManifestId, report.Result.ServedSequence));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnIncompatibleManifest_StopsTheHost_NamingTheCapability()
    {
        var (manifest, _) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var incompatible = ReleaseFixture.With(manifest, requires: ["Teleport@1"]);
        var id = await _fixture.PutAsync(incompatible);
        await _fixture.PublishAsync("p", id);
        using var host = Build(["p"]);

        var thrown = await Assert.ThrowsAsync<ConfigException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith($"Loading StrataRec releases of environment 'local' (p: incompatible manifest {id}: this binary lacks Teleport@1) failed with 1 error(s)",
            thrown.Message, StringComparison.Ordinal);
        var error = Assert.Single(thrown.Diagnostics);
        Assert.Equal(("MANIFEST_INCOMPATIBLE", "/requires"), (error.Code, error.Pointer));
        Assert.Contains("Teleport@1", error.Message);
        Assert.Equal(ManifestLoadStatus.Incompatible, Assert.Single(_reports.All).Result.Status);
        Assert.Empty(host.Services.GetRequiredService<IRecEngine>().PipelineIds());
    }

    [Fact]
    public async Task EveryPipeline_MustBeApplied_WithItsFallback()
    {
        var (_, a) = await _fixture.Pipeline("a", fallback: "b").Pipeline("b").BuildAsync("a", seq: 1);
        await _fixture.PublishAsync("a", a);
        using var host = Build(["a", "c"]);

        var thrown = await Assert.ThrowsAsync<ConfigException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith($"Loading StrataRec releases of environment 'local' (a: applied manifest {a} (sequence 1); c: nothing published) failed with 2 error(s)",
            thrown.Message, StringComparison.Ordinal);
        Assert.Equal(
            new[] { ("RELEASE_NOT_PUBLISHED", "pipeline:c", ""), ("RELEASE_FALLBACK_NOT_SERVED", "pipeline:a", "/fallback_pipeline") },
            thrown.Diagnostics.Select(d => (d.Code, d.Artifact, d.Pointer)));
        Assert.Contains("add 'b' to StrataRecOptions.Pipelines", thrown.Diagnostics[1].Hint);
    }

    [Fact]
    public async Task WithTheFallbackServed_TheHostStarts()
    {
        var (_, a) = await _fixture.Pipeline("a", fallback: "b").Pipeline("b").BuildAsync("a", seq: 1);
        var (_, b) = await _fixture.BuildAsync("b", seq: 1);
        await _fixture.PublishAsync("a", a);
        await _fixture.PublishAsync("b", b);
        using var host = Build(["a", "b"]);

        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "a", "b" }, host.Services.GetRequiredService<IRecEngine>().PipelineIds());
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFallbackLoadedInTheBackground_IsCheckedAgainstThePipelinesFallingBackToIt()
    {
        var (_, a) = await _fixture.Pipeline("a", fallback: "b").Pipeline("b").BuildAsync("a", seq: 1);
        var (_, b) = await _fixture.BuildAsync("b", seq: 1);
        await _fixture.PublishAsync("a", a);
        await _fixture.PublishAsync("b", b);
        var log = new ReleaseLog();
        using var host = Build(["a", "b"], _clock, log: log);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(2).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(log.Entries, e => e.EventId.Id == 1003);

        var (_, b2) = await _fixture.Pipeline("b", inputs: """ "user_id": { "type": "int64?" }, "tenant_id": { "type": "int64" } """).BuildAsync("b", seq: 2);
        await _fixture.PublishAsync("b", b2);
        var scheduled = _clock.Scheduled;
        _clock.Advance(Poll);
        await _clock.WhenScheduledAsync(scheduled + 2).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(host.Services.GetRequiredService<IRecEngine>().TryGetRequestSchema("b", out var served) && served.Contains("tenant_id"));
        await _reports.WhenCountAsync(3); // a and b at startup, b2
        Assert.Equal(ManifestLoadStatus.Applied, Assert.Single(_reports.All, r => r.Result.ManifestId == b2).Result.Status);
        var warning = Assert.Single(log.Entries, e => e.EventId.Id == 1003);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("Configuration warning FALLBACK_INPUT_UNDECLARED at pipeline:a/fallback_pipeline: Fallback pipeline 'b' requires input 'tenant_id'",
            warning.Message, StringComparison.Ordinal);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AHostWithoutWhatReleasesNeed_SaysWhatToAdd()
    {
        var empty = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddStrataRec(options => options.Releases = new ReleaseOptions()));
        Assert.Contains("Pipelines is empty", empty.Message);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<IArtifactStore>(_fixture.Store);
        builder.Services.AddStrataRec(options =>
        {
            options.Pipelines.Add("p");
            options.Releases = new ReleaseOptions();
        });
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("no IManifestSource", thrown.Message);
        Assert.Contains("new FileSystemManifestSource(", thrown.Message);
    }

    [Fact]
    public async Task ANewlyPublishedManifest_IsLoadedInTheBackground()
    {
        var (_, first) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", first);
        using var host = Build(["p"], _clock);
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await _clock.AdvanceAsync(Poll);
        var (_, second) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        await _fixture.PublishAsync("p", second);
        await _clock.AdvanceAsync(Poll);

        Assert.Equal(second, (await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken)).ManifestId);
        await _reports.WhenCountAsync(2);
        Assert.Equal(new[] { (0.0, first, ManifestLoadStatus.Applied), (2.0, second, ManifestLoadStatus.Applied) }, _reports.Attempts());
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFailedManifest_IsTriedAgainWithGrowingIntervals_UntilItLoads()
    {
        var (_, first) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", first);
        using var host = Build(["p"], _clock, maxRetry: TimeSpan.FromSeconds(4));
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var (second, secondId) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        var blob = _fixture.Store.GetBlobPath(second.Artifacts.Pipeline);
        var bytes = File.ReadAllBytes(blob);
        File.Delete(blob);
        await _fixture.PublishAsync("p", secondId);
        for (var t = 1; t <= 13; t++)
        {
            if (t == 12)
            {
                File.WriteAllBytes(blob, bytes);
            }

            await _clock.AdvanceAsync(Poll);
        }

        await _reports.WhenCountAsync(6);
        var attempts = _reports.Attempts().Skip(1).ToList();
        Assert.Equal(new[] { 1.0, 2, 4, 8, 12 }, attempts.Select(a => a.Seconds));
        Assert.All(attempts.Take(4), a => Assert.Equal(ManifestLoadStatus.Rejected, a.Status));
        Assert.Equal(ManifestLoadStatus.Applied, attempts[4].Status);
        Assert.Contains("ARTIFACT_MISSING", _reports.All[1].Result.Reason);
        Assert.Equal(secondId, (await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken)).ManifestId);
    }

    [Fact]
    public async Task AFinalOutcome_IsNotTriedAgain_UntilThePointerMoves()
    {
        var (first, firstId) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", firstId);
        using var host = Build(["p"], _clock);
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var incompatible = ReleaseFixture.With(first, seq: 2, requires: ["Teleport@1"]);
        var incompatibleId = await _fixture.PutAsync(incompatible);
        await _fixture.PublishAsync("p", incompatibleId);
        for (var i = 0; i < 5; i++)
        {
            await _clock.AdvanceAsync(Poll);
        }

        await _fixture.PublishAsync("p", firstId);
        for (var i = 0; i < 3; i++)
        {
            await _clock.AdvanceAsync(Poll);
        }

        var (_, thirdId) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 3);
        await _fixture.PublishAsync("p", thirdId);
        await _clock.AdvanceAsync(Poll);

        await _reports.WhenCountAsync(4);
        Assert.Equal(
            new[] { (0.0, firstId, ManifestLoadStatus.Applied), (1, incompatibleId, ManifestLoadStatus.Incompatible), (6, firstId, ManifestLoadStatus.Stale),
                (9, thirdId, ManifestLoadStatus.Applied) },
            _reports.Attempts());
        Assert.Equal(thirdId, (await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken)).ManifestId);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnUnreadablePointer_IsReadAgainWithGrowingIntervals()
    {
        var (_, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", id);
        var log = new ReleaseLog();
        using var host = Build(["p"], _clock, maxRetry: TimeSpan.FromSeconds(4), log: log);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        File.WriteAllText(_fixture.Pointers.GetPointerPath("p", "local"), "{ not json");
        for (var i = 0; i < 8; i++)
        {
            await _clock.AdvanceAsync(Poll);
        }

        var failures = log.Entries.Where(e => e.Category == "StrataRec.Hosting.StrataRecReleases" && e.EventId.Id == 1009).ToList();
        Assert.Equal(4, failures.Count);
        Assert.All(failures, f => Assert.IsType<InvalidDataException>(f.Exception));
        Assert.Equal(new[] { 1000, 2000, 4000, 4000 }.Select(ms => $"reading it again in {ms} ms"), failures.Select(f => f.Message[f.Message.IndexOf("reading", StringComparison.Ordinal)..]));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // disposed without being stopped: the polling stops before the engine is disposed
    public async Task StoppingOrDisposingTheHost_CancelsALoadInProgress_AndKeepsThePlan(bool stop)
    {
        var (_, first) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", first);
        var store = new WatchedStore(_fixture.Store);
        var host = Build(["p"], _clock, store: store);
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var (second, secondId) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        store.Hold = second.Artifacts.Pipeline;
        await _fixture.PublishAsync("p", secondId);
        _clock.Advance(Poll);
        await store.HeldReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        if (stop)
        {
            await host.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(store.HeldReadCancelled.Task.IsCompleted);
            Assert.Equal(first, (await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken)).ManifestId);
        }

        await Task.Run(host.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(store.HeldReadCancelled.Task.IsCompleted);
        Assert.Single(_reports.All);
    }

    [Fact]
    public async Task ALoggerThatThrows_ChangesNothingThePollingDoes()
    {
        var (_, first) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", first);
        var log = new ReleaseLog();
        using var host = Build(["p"], _clock, log: log);
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        log.Fails = true;

        // 1 s: the pointer is unreadable (event 1009).
        File.WriteAllText(_fixture.Pointers.GetPointerPath("p", "local"), "{ not json");
        await _clock.AdvanceAsync(Poll);

        // 2 s: a manifest whose pipeline artifact is not in the store yet (events 1008 and 1012); 3 s: uploaded, applied (1001).
        var (second, secondId) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        var blob = _fixture.Store.GetBlobPath(second.Artifacts.Pipeline);
        var bytes = File.ReadAllBytes(blob);
        File.Delete(blob);
        await _fixture.PublishAsync("p", secondId);
        await _clock.AdvanceAsync(Poll);
        File.WriteAllBytes(blob, bytes);
        await _clock.AdvanceAsync(Poll);

        log.Fails = false;
        await _reports.WhenCountAsync(3);
        Assert.Equal(
            new[] { (0.0, first, ManifestLoadStatus.Applied), (2, secondId, ManifestLoadStatus.Rejected), (3, secondId, ManifestLoadStatus.Applied) },
            _reports.Attempts());
        Assert.Equal(secondId, (await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken)).ManifestId);
        var events = log.Entries.Select(e => e.EventId.Id).ToList();
        Assert.Contains(1009, events);
        Assert.Contains(1012, events);
        Assert.DoesNotContain(1013, events); // nothing escaped to the polling's last guard
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASourceCallbackThatThrows_DoesNotComeOutOfTheHostsCancellation()
    {
        var (_, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", id);
        var source = new HeldSource(_fixture.Pointers) { Hold = true, ThrowOnCancel = true };
        using var host = Build(["p"], source: source);
        using var starting = new CancellationTokenSource();

        var start = host.StartAsync(starting.Token);
        await source.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        starting.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Empty(host.Services.GetRequiredService<IRecEngine>().PipelineIds());
    }

    [Fact]
    public async Task AStop_IsNotHeldByASourceCallbackThatBlocks()
    {
        var (_, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", id);
        using var blocked = new ManualResetEventSlim();
        var source = new HeldSource(_fixture.Pointers) { BlockOnCancel = blocked };
        var host = Build(["p"], _clock, source: source);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        source.Hold = true;
        _clock.Advance(Poll);
        await source.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        try
        {
            using var stopBy = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await host.StopAsync(stopBy.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            blocked.Set();
        }

        await Task.Run(host.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AStop_WaitsForTheSourcesCleanup_BeforeTheContainerDisposesIt()
    {
        var (_, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", id);
        using var blocked = new ManualResetEventSlim();
        var source = new HeldSource(_fixture.Pointers) { BlockOnCancel = blocked };
        var host = Build(["p"], _clock, source: source);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        source.Hold = true;
        _clock.Advance(Poll);
        await source.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var stopping = host.StopAsync(TestContext.Current.CancellationToken); // the default stop timeout: 30 s
        _ = Task.Delay(200, TestContext.Current.CancellationToken).ContinueWith(_ => blocked.Set(), TaskScheduler.Default);
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Task.Run(host.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(source.CallbackEndedAtDisposal);
    }

    [Fact]
    public async Task AStop_WaitsForTheCleanupOfAReadThatTimedOut()
    {
        var (_, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", id);
        using var blocked = new ManualResetEventSlim();
        var log = new ReleaseLog();
        var source = new HeldSource(_fixture.Pointers) { BlockOnCancel = blocked };
        var host = Build(["p"], _clock, log: log, source: source, pointerTimeout: TimeSpan.FromMilliseconds(100));
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        source.Hold = true;
        await _clock.AdvanceAsync(Poll);
        Assert.Single(log.Entries, e => e.EventId.Id == 1009);

        var stopping = host.StopAsync(TestContext.Current.CancellationToken); // the default stop timeout: 30 s
        _ = Task.Delay(200, TestContext.Current.CancellationToken).ContinueWith(_ => blocked.Set(), TaskScheduler.Default);
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Task.Run(host.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(source.CallbackEndedAtDisposal);
    }

    [Fact]
    public async Task AStartupReadThatHangs_FailsTheStart_AtThePointerTimeout()
    {
        var (_, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", id);
        using var source = new HeldSource(_fixture.Pointers) { Hold = true };
        using var host = Build(["p"], _clock, source: source, pointerTimeout: TimeSpan.FromMilliseconds(100));

        var thrown = await Assert.ThrowsAsync<ConfigException>(() => host.StartAsync(TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var error = Assert.Single(thrown.Diagnostics);
        Assert.Equal(("RELEASE_POINTER_UNREADABLE", "pipeline:p"), (error.Code, error.Artifact));
        Assert.Contains("did not answer within 100 ms (ReleaseOptions.PointerTimeout)", error.Message);
        Assert.True(source.HeldStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task AHostOnAnEnvironmentStartingWithADot_IsToldHowToMoveTheRelease()
    {
        using var host = Build(["p"], _clock, environment: ".preview");

        var thrown = await Assert.ThrowsAsync<ConfigException>(() => host.StartAsync(TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var error = Assert.Single(thrown.Diagnostics);
        Assert.Equal(("RELEASE_POINTER_UNREADABLE", "pipeline:p"), (error.Code, error.Artifact));
        Assert.Contains("'.preview' cannot name a pointer file: it starts with '.'", error.Message);
        Assert.Equal(FileSystemManifestSource.NameHint, error.Hint);
        Assert.Contains("choose a new environment name nothing uses yet", error.Hint);
        Assert.Contains("copy releases/<pipeline>.<old name>.jsonc to releases/<pipeline>.<new name>.jsonc with its \"environment\" set to the new name " +
            "and bindings for that environment", error.Hint);
        Assert.Contains("leave out of it each experiment the old release revoked (in the store, pointers/<old name>/<pipeline>.json names the manifest " +
            "sha256:<hex>, blobs/sha256/<hex> holds it", error.Hint);
    }

    [Fact]
    public async Task AnAttemptThatThrew_IsTriedAgain_AndItsManifestApplied()
    {
        var (_, first) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", first);
        var store = new FailingOnceStore(_fixture.Store);
        var log = new ReleaseLog();
        using var host = Build(["p"], _clock, store: store, log: log);
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var (_, second) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        store.FailOn = second;
        await _fixture.PublishAsync("p", second);
        await _clock.AdvanceAsync(Poll);
        await _clock.AdvanceAsync(Poll);

        await _reports.WhenCountAsync(3);
        Assert.Equal(new[] { (0.0, first, ManifestLoadStatus.Applied), (1, second, ManifestLoadStatus.Rejected), (2, second, ManifestLoadStatus.Applied) },
            _reports.Attempts());
        Assert.Contains("(reading its message threw)", _reports.All[1].Result.Reason);
        Assert.Equal(second, (await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken)).ManifestId);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A store whose read of <see cref="FailOn"/> throws once, an exception whose message cannot be read.</summary>
    private sealed class FailingOnceStore(IArtifactStore inner) : IArtifactStore
    {
        private int _failed;

        public string? FailOn { get; set; }

        public ValueTask<byte[]?> TryGetAsync(string contentId, CancellationToken cancellationToken = default) =>
            contentId == FailOn && Interlocked.Exchange(ref _failed, 1) == 0 ? throw new UnreadableException() : inner.TryGetAsync(contentId, cancellationToken);

        public ValueTask<string> PutAsync(System.Text.Json.Nodes.JsonNode artifact, CancellationToken cancellationToken = default) =>
            inner.PutAsync(artifact, cancellationToken);

        private sealed class UnreadableException : Exception
        {
            public override string Message => throw new InvalidOperationException("no message");
        }
    }

    private IHost Build(
        string[] pipelines, TimeProvider? clock = null, TimeSpan? maxRetry = null, IArtifactStore? store = null, ReleaseLog? log = null,
        IManifestSource? source = null, IReleaseStatusReporter? reporter = null, TimeSpan? pointerTimeout = null, TimeSpan? reportTimeout = null,
        string environment = "local")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = _fixture.Root,
            DisableDefaults = true,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders().AddProvider(log ?? new ReleaseLog());
        builder.Services.AddSingleton<IManifestSource>(_ => source ?? _fixture.Pointers); // created by the container, so disposed by it
        builder.Services.AddSingleton<IArtifactStore>(store ?? _fixture.Store);
        builder.Services.AddSingleton(reporter ?? new Reporter(_reports, clock ?? _clock));
        builder.Services.AddStrataRec(options =>
        {
            options.ConfigRoot = _fixture.Root;
            options.Environment = environment;
            options.Pipelines.AddRange(pipelines);
            options.Releases = new ReleaseOptions
            {
                PollInterval = Poll,
                MaxRetryInterval = maxRetry ?? TimeSpan.FromMinutes(5),
                PointerTimeout = pointerTimeout ?? TimeSpan.FromSeconds(30),
                ReportTimeout = reportTimeout ?? TimeSpan.FromSeconds(30),
                TimeProvider = clock ?? TimeProvider.System,
            };
        });
        return builder.Build();
    }

    [Fact]
    public async Task AReporterThatBlocks_DelaysNoRelease()
    {
        var (_, p1) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var (_, q1) = await _fixture.Pipeline("q").BuildAsync("q", seq: 1);
        await _fixture.PublishAsync("p", p1);
        await _fixture.PublishAsync("q", q1);
        var reporter = new BlockingReporter(new Reporter(_reports, _clock));
        using var host = Build(["p", "q"], _clock, reporter: reporter);
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(2).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        reporter.Block = true;
        var (_, p2) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        await _fixture.PublishAsync("p", p2);
        _clock.Advance(Poll);
        await _clock.WhenScheduledAsync(4).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await reporter.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var (_, q2) = await _fixture.Pipeline("q", k: 3).BuildAsync("q", seq: 2);
        var (_, p3) = await _fixture.Pipeline("p", k: 4).BuildAsync("p", seq: 3);
        await _fixture.PublishAsync("q", q2);
        await _fixture.PublishAsync("p", p3);
        _clock.Advance(Poll);
        await _clock.WhenScheduledAsync(6).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(q2, (await engine.ExecuteAsync(ReleaseFixture.Request("q"), TestContext.Current.CancellationToken)).ManifestId);
        Assert.Equal(p3, (await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken)).ManifestId);
        Assert.Equal(2, _reports.All.Count); // the startup's: the reporter is still on p2

        reporter.Release();
        await _reports.WhenCountAsync(5);
        var reported = _reports.All.Select(r => r.Result.ManifestId).ToList();
        Assert.Equal(new[] { p1, q1, p2 }, reported.Take(3));
        Assert.Equal(new[] { p3, q2 }.Order(), reported.Skip(3).Order());
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AReportThatHangs_IsCancelled_AndAFullQueueDropsTheOldest()
    {
        var (_, first) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", first);
        var log = new ReleaseLog();
        var reporter = new BlockingReporter(new Reporter(_reports, _clock));
        using var host = Build(["p"], _clock, maxRetry: Poll, log: log, reporter: reporter, reportTimeout: TimeSpan.FromMilliseconds(300));
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        reporter.Block = true;
        reporter.HonorsCancellation = false;
        var (second, secondId) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        File.Delete(_fixture.Store.GetBlobPath(second.Artifacts.Pipeline));
        await _fixture.PublishAsync("p", secondId);

        // The first outcome must be in the call before the others arrive: the reporter runs on its own task, and one
        // not yet scheduled leaves the first outcome in the queue, where it is dropped too.
        await _clock.AdvanceAsync(Poll);
        await reporter.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        for (var t = 1; t < 20; t++)
        {
            await _clock.AdvanceAsync(Poll);
        }

        // One outcome in the call, 16 waiting, the 3 oldest of the others dropped.
        Assert.Equal(3, log.Entries.Count(e => e.EventId.Id == 1014));
        reporter.HonorsCancellation = true;
        reporter.Release();
        await _reports.WhenCountAsync(2 + 16); // the startup's, then the one in the call and the 16 waiting
        var seconds = _reports.Attempts().Skip(1).Select(a => a.Seconds).ToList();
        Assert.Equal(new[] { 1.0 }.Concat(Enumerable.Range(5, 16).Select(s => (double)s)), seconds);

        reporter.Block = true;
        await _clock.AdvanceAsync(Poll);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!log.Entries.Any(e => e.EventId.Id == 1015))
        {
            Assert.True(DateTime.UtcNow < deadline, "the report did not time out");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        reporter.Block = false;
        await _clock.AdvanceAsync(Poll);
        await _reports.WhenCountAsync(2 + 16 + 1);
        Assert.Equal(22.0, _reports.Attempts()[^1].Seconds); // 21 s was the call that timed out
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APointerReadThatHangs_DelaysNoOtherPipeline()
    {
        var (_, p1) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var (_, q1) = await _fixture.Pipeline("q").BuildAsync("q", seq: 1);
        await _fixture.PublishAsync("p", p1);
        await _fixture.PublishAsync("q", q1);
        using var source = new HeldSource(_fixture.Pointers) { Only = "p" };
        using var host = Build(["p", "q"], _clock, source: source);
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(2).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        source.Hold = true;
        var (_, q2) = await _fixture.Pipeline("q", k: 3).BuildAsync("q", seq: 2);
        await _fixture.PublishAsync("q", q2);
        _clock.Advance(Poll);
        await source.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(3).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); // q's polling waits again

        Assert.Equal(q2, (await engine.ExecuteAsync(ReleaseFixture.Request("q"), TestContext.Current.CancellationToken)).ManifestId);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APointerReadThatHangs_IsCancelled_AndReadAgain()
    {
        var (_, first) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        await _fixture.PublishAsync("p", first);
        var log = new ReleaseLog();
        using var source = new HeldSource(_fixture.Pointers);
        using var host = Build(["p"], _clock, log: log, source: source, pointerTimeout: TimeSpan.FromMilliseconds(200));
        var engine = host.Services.GetRequiredService<IRecEngine>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await _clock.WhenScheduledAsync(1).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        source.Hold = true;
        await _clock.AdvanceAsync(Poll);
        var failure = Assert.Single(log.Entries, e => e.EventId.Id == 1009);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.Exception);

        source.Hold = false;
        var (_, second) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        await _fixture.PublishAsync("p", second);
        await _clock.AdvanceAsync(Poll);
        Assert.Equal(second, (await engine.ExecuteAsync(ReleaseFixture.Request("p"), TestContext.Current.CancellationToken)).ManifestId);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class Reports
    {
        private readonly List<(DateTimeOffset At, ManifestLoadResult Result)> _all = new();

        public IReadOnlyList<(DateTimeOffset At, ManifestLoadResult Result)> All
        {
            get
            {
                lock (_all)
                {
                    return _all.ToList();
                }
            }
        }

        public void Add(DateTimeOffset at, ManifestLoadResult result)
        {
            lock (_all)
            {
                _all.Add((at, result));
            }
        }

        /// <summary>
        /// Completes once <paramref name="count"/> outcomes are reported: background outcomes are reported after the
        /// fact, off the polling, so a poll the clock woke may have returned before its outcome is.
        /// </summary>
        public async Task WhenCountAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (All.Count < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"{All.Count} of {count} outcomes reported");
                await Task.Delay(5);
            }
        }

        public List<(double Seconds, string ManifestId, ManifestLoadStatus Status)> Attempts() =>
            All.Select(r => ((r.At - ManualTimers.Start).TotalSeconds, r.Result.ManifestId, r.Result.Status)).ToList();
    }

    private sealed class HeldSource(IManifestSource inner) : IManifestSource, IDisposable
    {
        private volatile bool _hold;

        public bool Hold
        {
            get => _hold;
            set => _hold = value;
        }

        public bool ThrowOnCancel { get; init; }

        public string? Only { get; init; }

        public ManualResetEventSlim? BlockOnCancel { get; init; }

        public TaskCompletionSource HeldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool? CallbackEndedAtDisposal { get; private set; }

        private volatile bool _callbackEnded;

        public void Dispose() => CallbackEndedAtDisposal ??= _callbackEnded;

        public async ValueTask<string?> GetManifestIdAsync(string pipelineId, string environment, CancellationToken cancellationToken)
        {
            if (Hold && (Only is null || Only == pipelineId))
            {
                // Not disposed when the wait ends: the wait's continuation may run inside the cancellation, before this
                // callback, and would unregister it.
                _ = ThrowOnCancel
                    ? cancellationToken.Register(static () => throw new InvalidOperationException("pointer cleanup failed"))
                    : BlockOnCancel is { } blocked ? cancellationToken.Register(() =>
                    {
                        blocked.Wait();
                        _callbackEnded = true;
                    }) : default;
                HeldStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await inner.GetManifestIdAsync(pipelineId, environment, cancellationToken);
        }
    }

    private sealed class BlockingReporter(IReleaseStatusReporter inner) : IReleaseStatusReporter
    {
        private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _block;
        private volatile bool _honorsCancellation = true;

        public bool Block
        {
            get => _block;
            set => _block = value;
        }

        public bool HonorsCancellation
        {
            get => _honorsCancellation;
            set => _honorsCancellation = value;
        }

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            _block = false;
            Interlocked.Exchange(ref _released, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        public async ValueTask ReportAsync(ManifestLoadResult result, CancellationToken cancellationToken)
        {
            if (_block)
            {
                Blocked.TrySetResult();
                var released = Volatile.Read(ref _released).Task;
                if (_honorsCancellation)
                {
                    await released.WaitAsync(cancellationToken);
                }
                else
                {
                    await released;
                }
            }

            await inner.ReportAsync(result, cancellationToken);
        }
    }

    private sealed class Reporter(Reports reports, TimeProvider clock) : IReleaseStatusReporter
    {
        public ValueTask ReportAsync(ManifestLoadResult result, CancellationToken cancellationToken)
        {
            reports.Add(result.AttemptedAt ?? clock.GetUtcNow(), result);
            return ValueTask.CompletedTask;
        }
    }
}
