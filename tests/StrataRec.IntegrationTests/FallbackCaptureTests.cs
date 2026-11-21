using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Logging;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Review 79: the log batches of a request whose fallback pipeline served. Pipeline <c>primary</c> recalls (tap
/// <c>recalled</c> captures after it), then a node whose source fails fails it; <c>backup</c> serves, with an output tap.
/// A request that captures its logs gets both runs' batches; one whose fallback is cancelled records that the batches it
/// captured went nowhere.
/// </summary>
public sealed class FallbackCaptureTests
{
    /// <summary>
    /// Written to the sink, a request writes both runs' batches; captured, it got only the fallback's, and the primary's
    /// taps were lost. They are returned in run order, each with its own pipeline and manifest, and the sink stays empty.
    /// </summary>
    [Fact]
    public async Task ACapturingRequest_GetsTheBatchesOfBothRuns_InRunOrder()
    {
        var root = Workspace();
        var sink = new InMemoryLogSink();
        RecResult written;
        RecResult captured;
        await using (var logs = new LogPipeline(sink))
        await using (var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root }, logs: logs))
        {
            ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", ["primary"]).ThrowIfErrors("load");
            written = await engine.ExecuteAsync(Request("written", captureLogs: false), TestContext.Current.CancellationToken);
            await logs.FlushAsync(TestContext.Current.CancellationToken);
            Assert.Equal([("backup", "served"), ("primary", "recalled")], sink.Snapshot().Where(b => b.RequestId == "written").Select(b => (b.PipelineId, b.TapId)).Order());

            captured = await engine.ExecuteAsync(Request("captured", captureLogs: true), TestContext.Current.CancellationToken);
            await logs.FlushAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(("backup", "backup"), (written.ServedBy, captured.ServedBy));
        Assert.True(captured.Succeeded, captured.Error);
        var manifests = sink.Snapshot().Where(b => b.RequestId == "written").ToDictionary(b => b.PipelineId, b => b.ManifestId); // each run's own
        Assert.Equal([("primary", "recalled", manifests["primary"]), ("backup", "served", manifests["backup"])],
            captured.LogBatches!.Select(b => (b.PipelineId, b.TapId, b.ManifestId)));
        Assert.Equal(captured.ManifestId, manifests["backup"]);
        Assert.DoesNotContain(sink.Snapshot(), b => b.RequestId == "captured");
    }

    /// <summary>
    /// A capturing request cancelled while its fallback pipeline ran: the primary had finished, and recorded its batches
    /// as captured, for a result the caller never got. Neither run's batches were returned or written: both say none,
    /// and keep their sampling decisions.
    /// </summary>
    [Fact]
    public async Task ACapturingRequestCancelledInItsFallback_RecordsThatItsBatchesWentNowhere()
    {
        var root = Workspace(backupSource: BlockingSources.TypeName);
        var sources = new BlockingSources();
        await using var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = root,
            Bindings = PlanEnvironment.DefaultBindings().Add(sources),
        });
        ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", ["primary"]).ThrowIfErrors("load");
        ReplayBundle? delivered = null;
        using var cancel = new CancellationTokenSource();

        var running = engine.ExecuteAsync(Request("cancelled", captureLogs: true, onRecording: bundle => delivered = bundle), cancel.Token).AsTask();
        await sources.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.NotNull(delivered);
        Assert.Equal(["primary", "backup"], delivered.Runs.Select(r => r.PipelineId));
        Assert.All(delivered.Runs, run => Assert.Equal("none", run.Sampling.Logs));
        Assert.All(delivered.Runs, run => Assert.Contains(run.Sampling.Groups, g => g is { Group: "all", EffectiveRate: 1.0, Sampled: true }));
    }

    private static RecRequest Request(string requestId, bool captureLogs, Action<ReplayBundle>? onRecording = null) => new()
    {
        PipelineId = "primary",
        RequestId = requestId,
        Inputs = new Core.Values.RequestInputs(),
        Deadline = TimeSpan.FromSeconds(60),
        Debug = new DebugOptions { CaptureLogs = captureLogs, OnRecording = onRecording },
    };

    /// <param name="backupSource">The client of the fallback pipeline's source.</param>
    private static string Workspace(string backupSource = "memory_list@1")
    {
        var root = Directory.CreateTempSubdirectory("stratarec-fallback-capture-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines", "fixtures" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.Copy(Path.Combine(HomeFeedTestHost.SampleRoot(), "fixtures", "recall_popular.json"), Path.Combine(root, "fixtures", "recall_popular.json"));
        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), $$"""
            { "environment": "local",
              "candidate_sources": {
                "broken":  { "client": "memory_list@1", "items": [1, 2, 3], "fail": true },
                "popular": { "client": "memory_list@1", "data_file": "fixtures/recall_popular.json" },
                "backup":  { "client": "{{backupSource}}", "data_file": "fixtures/recall_popular.json" } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "primary.jsonc"), """
            { "pipeline_id": "primary", "fallback_pipeline": "backup",
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "more_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "popular": 10 }, "more_quotas": { "broken": 10 }, "k": 5 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "more":   { "order": 200, "op": "MultiSourceRecall@1", "config": { "quotas_param": "more_quotas" }, "on_failure": "fail_request" } },
              "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
              "log_taps": { "recalled": { "group": "all", "attach": { "after": "recall" }, "columns": { "source": "col.recall.primary_source" } } },
              "output": { "limit_param": "k", "columns": { "source": "col.recall.primary_source" } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "backup.jsonc"), """
            { "pipeline_id": "backup",
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "backup": 10 }, "k": 5 },
              "nodes": { "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } } },
              "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
              "log_taps": { "served": { "group": "all", "attach": { "output": true }, "columns": { "source": "col.recall.primary_source" } } },
              "output": { "limit_param": "k", "columns": { "source": "col.recall.primary_source" } } }
            """);
        return root;
    }

    /// <summary>A list source that says it started and then waits until its call is cancelled.</summary>
    private sealed class BlockingSources : IBindingFactory<ICandidateSource>
    {
        public const string TypeName = "blocking_list@1";

        private readonly MemoryListCandidateSourceFactory _inner = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => TypeName;

        public Type SettingsType => _inner.SettingsType;

        public ICandidateSource Create(BindingContext context) => new Source(this);

        private sealed class Source(BlockingSources owner) : ICandidateSource
        {
            public string ClientType => TypeName;

            public async ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken)
            {
                owner.Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return CandidateSourceStatus.Ok;
            }
        }
    }
}
