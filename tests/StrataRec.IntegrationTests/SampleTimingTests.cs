using System.Text.RegularExpressions;
using StrataRec.Core.Values;
using StrataRec.Features.Runtime;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The timing policy of the sample tests (<see cref="HomeFeedTestHost"/>): the untimed copy raises every budget and changes
/// nothing else, the relaxation refuses to guess at a budget it does not know, and a comparison of two live runs that
/// decided differently names the node.
/// </summary>
public sealed class SampleTimingTests
{
    [Fact]
    public void TheUntimedSample_RaisesEveryBudget_AndChangesNothingElse()
    {
        var asWritten = HomeFeedTestHost.SampleRoot(SampleTiming.AsWritten);
        var untimed = HomeFeedTestHost.SampleRoot();
        var budget = new Regex("(\"(?:timeout|deadline)_ms\"\\s*:\\s*)[0-9]+");
        foreach (var path in Directory.GetFiles(asWritten, "*", SearchOption.AllDirectories))
        {
            var file = Path.GetRelativePath(asWritten, path);
            var expected = file.StartsWith("fixtures", StringComparison.Ordinal)
                ? File.ReadAllText(path)
                : budget.Replace(File.ReadAllText(path), "${1}" + HomeFeedTestHost.UntimedMs);
            Assert.True(expected == File.ReadAllText(Path.Combine(untimed, file)), $"{file} differs from the sample by more than its budgets");
        }

        // The sample demonstrates a real budget and its fallback; the untimed copy keeps the fallback.
        Assert.Equal((50, "ctr_backup"), Ranker(HomeFeedTestHost.Load(SampleTiming.AsWritten).Plan()));
        var plan = HomeFeedTestHost.Load().Plan();
        Assert.Equal((HomeFeedTestHost.UntimedMs, "ctr_backup"), Ranker(plan));
        Assert.All(plan.Features.Groups.Where(g => g.Supply.Mode == SupplyMode.Fetch), g => Assert.Equal(HomeFeedTestHost.UntimedMs, g.Supply.TimeoutMs));
    }

    [Fact]
    public void ABudgetTheRelaxationDoesNotKnow_FailsIt_NamingTheFileAndTheSetting()
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"order\": 520,", "\"order\": 520, \"min_remaining_ms\": 20,"),
            SampleTiming.AsWritten);

        var error = Assert.Throws<InvalidOperationException>(() => SampleBudgets.Relax(host.Root));

        Assert.Matches("^pipelines/home_feed.jsonc:[0-9]+ \"min_remaining_ms\" is a timing setting", error.Message);
    }

    [Fact]
    public void AFetchedSourceLeftWithTheFrameworksDefaultTimeout_FailsTheRelaxation()
    {
        // Without timeout_ms a fetched source gets 50 ms, and there is no number in the text to raise.
        var host = HomeFeedTestHost.LoadModified(root =>
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"fixtures/users.json\", \"timeout_ms\": 1000", "\"fixtures/users.json\""), SampleTiming.AsWritten);

        var error = Assert.Throws<InvalidOperationException>(() => SampleBudgets.Relax(host.Root));

        Assert.Contains("feature source 'user_store': timeout 50 ms", error.Message);
    }

    [Fact]
    public async Task ComparingRunsThatDecidedDifferently_FailsNamingTheNodeAndWhy()
    {
        var executor = new PipelineExecutor();
        var served = await executor.ExecuteAsync(HomeFeedTestHost.Load().Plan(), HomeFeedTestHost.NewRequest());
        var fellBack = await executor.ExecuteAsync(HomeFeedTestHost.LoadModified(MetricsTests.FailModel).Plan(), HomeFeedTestHost.NewRequest());

        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => LiveRunAssert.SameNodeOutcomes(served, fellBack));

        Assert.Contains("\n  ctr_ranker: Succeeded → Degraded by ctr_backup (", error.Message);
        Assert.Single(error.Message.Split('\n'), line => line.StartsWith("  ", StringComparison.Ordinal)); // only that node differs
        LiveRunAssert.SameNodeOutcomes(served, fellBack, except: "ctr_ranker");
    }

    [Fact]
    public async Task ATracedRunAndARecordedRun_CompareByTheirDecisions()
    {
        // A trace reports kinds (Degraded by ctr_backup), a recording categories (fallback by ctr_backup): the same
        // decisions compare equal across the two, both when the ranker serves and when its fallback does (review 14).
        var (serving, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var (fallingBack, _) = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(MetricsTests.FailModel));
        var servedTrace = await serving.ExecuteAsync(Traced("req-trace"));
        var servedRecording = await serving.ExecuteAsync(ReplayTests.Request("req-record", 1001, "home", ReplayTests.ControlArms));
        var fellBackTrace = await fallingBack.ExecuteAsync(Traced("req-trace-fallback"));
        var fellBackRecording = await fallingBack.ExecuteAsync(ReplayTests.Request("req-record-fallback", 1001, "home", ReplayTests.ControlArms));
        Assert.Null(servedRecording.Trace);
        Assert.Null(servedTrace.Recording);
        Assert.Contains(fellBackRecording.Recording!.Runs[0].Nodes, n => n.Node == "ctr_ranker" && n.Outcome == "fallback");

        LiveRunAssert.SameNodeOutcomes(servedTrace, servedRecording);
        LiveRunAssert.SameNodeOutcomes(servedRecording, servedTrace);
        LiveRunAssert.SameNodeOutcomes(fellBackTrace, fellBackRecording);

        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => LiveRunAssert.SameNodeOutcomes(servedTrace, fellBackRecording));
        Assert.Contains("\n  ctr_ranker: Succeeded → Degraded by ctr_backup (", error.Message);
        Assert.Single(error.Message.Split('\n'), line => line.StartsWith("  ", StringComparison.Ordinal));

        static RecRequest Traced(string requestId)
        {
            var recorded = ReplayTests.Request(requestId, 1001, "home", ReplayTests.ControlArms);
            return new RecRequest
            {
                PipelineId = recorded.PipelineId,
                RequestId = recorded.RequestId,
                Inputs = recorded.Inputs,
                Deadline = recorded.Deadline,
                TimeMs = recorded.TimeMs,
                Debug = new DebugOptions { Trace = true, ForceArms = ReplayTests.ControlArms },
            };
        }
    }

    [Fact]
    public async Task ATracedAndARecordedRun_ServedByTheFallbackPipeline_CompareByTheServedRun()
    {
        // A trace has only the run that served; a recording has the primary's too. Both compare by the served pipeline and
        // its run, and a request the primary served differs in which pipeline served (review 15).
        var failing = Engine(ReviewRegressionTests.FallbackWorkspace(brokenLatencyMs: 0));
        var healthyRoot = ReviewRegressionTests.FallbackWorkspace(brokenLatencyMs: 0);
        ReplayTests.Edit(healthyRoot, "bindings/local.jsonc", "\"fail\": true", "\"fail\": false");
        var healthy = Engine(healthyRoot);

        var traced = await failing.ExecuteAsync(Request(new DebugOptions { Trace = true }));
        var recorded = await failing.ExecuteAsync(Request(new DebugOptions { Record = true }));
        var servedByPrimary = await healthy.ExecuteAsync(Request(new DebugOptions { Record = true }));
        Assert.Equal(("backup", "backup", "primary"), (traced.ServedBy, recorded.ServedBy, servedByPrimary.ServedBy));

        LiveRunAssert.SameNodeOutcomes(traced, recorded);
        LiveRunAssert.SameNodeOutcomes(recorded, traced);
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => LiveRunAssert.SameNodeOutcomes(servedByPrimary, recorded));
        Assert.Contains("\n  (served by): primary → backup", error.Message);

        static RecEngine Engine(string root)
        {
            var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root });
            ServiceCollectionExtensions.LoadWorkspace(engine, root, "local", new[] { "primary", "backup" }).ThrowIfErrors("load");
            return engine;
        }

        static RecRequest Request(DebugOptions debug) => new()
        {
            PipelineId = "primary", RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(60), Debug = debug,
        };
    }

    private static (int? TimeoutMs, string? Fallback) Ranker(ExecutionPlan plan)
    {
        var ranker = plan.Timeline.Single(s => s.NodeId == "ctr_ranker").Select(RequestArms.None)!;
        return (ranker.TimeoutMs, ranker.FallbackNodeId);
    }
}
