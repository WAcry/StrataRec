using System.Text.Json.Nodes;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Replay;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A request's configuration override (<see cref="DebugOptions.ConfigOverride"/>, design 13.3): a merge patch of the pipeline
/// applied after the request's arms, compiled for that request alone, on the home_feed sample.
/// </summary>
public sealed class ConfigOverrideTests
{
    private static readonly string[] TreatmentArms = { "exp_freshness_calibration/calibrated", "exp_mmr_diversity/mmr", "exp_quality_weight/strong" };

    [Fact]
    public async Task AnOverride_WinsOverTheArms_KeepsWhatItDoesNotTouch_AndLeavesTheServedPlanAsItWas()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        // exp_quality_weight/strong sets final_top_k 8 and quality_weight 0.8; the override sets final_top_k 3.
        var overridden = await engine.ExecuteAsync(Request("ov", TreatmentArms, Override("""{ "params": { "final_top_k": 3 } }""")),
            TestContext.Current.CancellationToken);
        var served = await engine.ExecuteAsync(Request("plain", TreatmentArms, null), TestContext.Current.CancellationToken);

        Assert.True(overridden.Succeeded, overridden.Error);
        Assert.Equal(3, overridden.Items.Count);
        var parameters = overridden.Recording!.Runs[0].Params;
        Assert.Equal("3", parameters["final_top_k"].Value!.Value.GetRawText());
        Assert.Equal("0.8", parameters["quality_weight"].Value!.Value.GetRawText()); // the arm's, which the override does not touch
        Assert.Equal(TreatmentArms.Order(), overridden.Experiments.Assigned.Order());
        Assert.Empty(overridden.Experiments.Triggered); // the plan it ran has no experiments
        Assert.Equal(served.ManifestId, overridden.ManifestId);

        Assert.True(served.Succeeded, served.Error);
        Assert.Equal(8, served.Items.Count);
        Assert.Equal(overridden.Items.Take(3).Select(i => i.Id), served.Items.Take(3).Select(i => i.Id)); // the same ranking, cut shorter
    }

    [Fact]
    public async Task AnOverride_CanDeleteANode()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var overridden = await engine.ExecuteAsync(Request("ov", null, Override("""{ "nodes": { "diversity_cap": null } }""")), TestContext.Current.CancellationToken);
        var served = await engine.ExecuteAsync(Request("plain", null, null), TestContext.Current.CancellationToken);

        Assert.True(overridden.Succeeded, overridden.Error);
        Assert.DoesNotContain(overridden.Trace!, n => n.NodeId == "diversity_cap");
        Assert.Contains(served.Trace!, n => n.NodeId == "diversity_cap");
    }

    [Theory]
    [InlineData("""{ "params": { "final_top_k": "three" } }""", "PARAM_BAD_VALUE pipeline:home_feed/params/final_top_k", null)]
    [InlineData("""{ "nodez": {} }""", "CONFIG_OVERRIDE_UNKNOWN_SECTION request/config_override/nodez", "/config_override/nodez")]
    [InlineData("""{ "nodes": { "rank_sort": { "config": { "score_col": "no.such_column" } } } }""", "pipeline:home_feed/nodes/rank_sort", null)]
    // An integer beyond 2^53 RFC 8785 cannot hold exactly: the compiler's hash of the node threw out of the request.
    [InlineData("""{ "nodes": { "coarse_top_k": { "order": 1152921504606846976 } } }""",
        "CONFIG_OVERRIDE_NOT_CANONICAL request/config_override/nodes/coarse_top_k/order", "/config_override/nodes/coarse_top_k/order")]
    public async Task AnOverrideThatDoesNotCompile_RejectsTheRequestAsInvalid_NamingTheError(string patch, string error, string? pointer)
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var result = await engine.ExecuteAsync(Request("bad", TreatmentArms, Override(patch)), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecErrorKind.InvalidRequest, result.ErrorKind);
        Assert.StartsWith("the configuration override does not compile", result.Error, StringComparison.Ordinal);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
        Assert.Equal(pointer, result.ErrorPointer);
        Assert.Equal(TreatmentArms.Order(), result.Experiments.Assigned.Order());
        Assert.Null(result.Recording); // nothing ran
    }

    /// <summary>What a configuration nobody released produced never reaches the log sinks; asked for, it is captured.</summary>
    [Fact]
    public async Task AnOverride_WritesNoLogs_AndCapturesThemWhenAsked()
    {
        var host = HomeFeedTestHost.Load();
        var sink = new InMemoryLogSink();
        await using var logs = new LogPipeline(sink);
        await using var engine = new RecEngine(host.Environment, logs: logs);
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        var patch = """{ "params": { "final_top_k": 3 } }""";

        var silent = await engine.ExecuteAsync(Request("ov", null, Override(patch)), TestContext.Current.CancellationToken);
        var captured = await engine.ExecuteAsync(Request("ov-logs", null, Override(patch), captureLogs: true), TestContext.Current.CancellationToken);
        await logs.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True(silent.Succeeded && captured.Succeeded, silent.Error ?? captured.Error);
        Assert.Empty(sink.Snapshot());
        Assert.Null(silent.LogBatches);
        Assert.NotEmpty(captured.LogBatches!);

        var plain = await engine.ExecuteAsync(Request("plain", null, null), TestContext.Current.CancellationToken);
        await logs.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True(plain.Succeeded, plain.Error);
        Assert.NotEmpty(sink.Snapshot()); // the served configuration logs as before
    }

    [Fact]
    public async Task ARecordedOverride_ReplaysExactly_OnAFreshEngine()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(
            Request("ov", TreatmentArms, Override("""{ "params": { "final_top_k": 4 }, "nodes": { "diversity_cap": null } }""")), TestContext.Current.CancellationToken);
        Assert.True(recorded.Succeeded, recorded.Error);

        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        Assert.Equal(4, (int)bundle.Request.ConfigOverride!["params"]!["final_top_k"]!);
        var (fresh, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var outcome = await fresh.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);

        var report = outcome.Report;
        Assert.True(report.Diff.Identical, ReplayTests.Describe(report));
        Assert.Equal(recorded.Items.Select(i => i.Id), outcome.Result.Items.Select(i => i.Id));
        Assert.Equal(4, outcome.Result.Items.Count);
        Assert.Equal(report.RecordedArms.Order(), report.ReplayedArms.Order());
        Assert.Equal(TreatmentArms.Order(), report.ReplayedArms.Order());
        Assert.Empty(report.Params);
        Assert.Empty(report.Gaps);
        Assert.All(report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
    }

    /// <summary>
    /// An override builds on the configuration as it was loaded, not on the host's objects (review 58): the host edits them
    /// after the load (a parameter, an arm removed), and an empty override still runs what is served, takes the arm the
    /// served plan has, and replays identically on a fresh engine loaded from the files.
    /// </summary>
    [Fact]
    public async Task AnOverride_BuildsOnTheConfigurationAsLoaded_NotOnTheHostsObjectsEditedSince()
    {
        var host = HomeFeedTestHost.Load();
        var (engine, _) = MetricsTests.LoadEngine(host);
        host.Request.Pipeline.Params["final_top_k"] = 2;
        Assert.True(host.Request.Experiments.Single(e => e.Experiment.ExperimentId == "exp_quality_weight").Experiment.Arms.Remove("strong"));

        var served = await engine.ExecuteAsync(Request("plain", null, null), TestContext.Current.CancellationToken);
        var empty = await engine.ExecuteAsync(Request("empty", null, Override("{}")), TestContext.Current.CancellationToken);
        var strong = await engine.ExecuteAsync(Request("strong", TreatmentArms, Override("{}")), TestContext.Current.CancellationToken);

        Assert.True(served.Succeeded && empty.Succeeded, served.Error ?? empty.Error);
        Assert.Equal(served.Items.Select(i => i.Id), empty.Items.Select(i => i.Id));
        Assert.True(strong.Succeeded, strong.Error);
        Assert.Equal(8, strong.Items.Count); // the served strong arm's final_top_k

        var (fresh, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var outcome = await fresh.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(empty.Recording!)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
    }

    /// <summary>
    /// The override's plan takes over what the served plan has loaded and holds nothing after its request: once the engine
    /// is disposed, nothing of the binding host is left (a plan the request kept would keep its tables and instances).
    /// </summary>
    [Fact]
    public async Task AnOverride_SharesTheServedBindings_AndReleasesThemWithItsRequest()
    {
        var host = HomeFeedTestHost.Load();
        await using var bindings = new BindingHost();
        var engine = new RecEngine(host.Environment, bindings: bindings);
        Assert.True(engine.Load(host.Request).Applied);
        var (tables, instances) = (bindings.TableCount, bindings.InstanceCount);
        Assert.True(tables > 0 && instances > 0);

        var result = await engine.ExecuteAsync(Request("ov", null, Override("""{ "params": { "final_top_k": 3 } }""")), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal((tables, instances), (bindings.TableCount, bindings.InstanceCount)); // nothing loaded twice

        await engine.DisposeAsync();
        Assert.True(SpinWait.SpinUntil(() => bindings.TableCount == 0 && bindings.InstanceCount == 0, TimeSpan.FromSeconds(30)),
            $"{bindings.TableCount} tables and {bindings.InstanceCount} instances are still held");
    }

    private static JsonObject Override(string json) => JsonNode.Parse(json)!.AsObject();

    private static RecRequest Request(string requestId, IReadOnlyList<string>? arms, JsonObject? configOverride, bool captureLogs = false) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Deadline = TimeSpan.FromMilliseconds(HomeFeedTestHost.UntimedMs),
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Record = true, Trace = true, ForceArms = arms, ConfigOverride = configOverride, CaptureLogs = captureLogs },
    };
}
