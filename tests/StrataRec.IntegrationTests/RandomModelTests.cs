using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

public sealed class RandomModelTests
{
    private const string Binding = """{ "client": "random@1", "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click"] }""";

    [Fact]
    public async Task TheSampleModel_BoundToRandom_Serves_AndARecordingReplaysIdentically()
    {
        var (engine, _) = MetricsTests.LoadEngine(Host(Binding));

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-random", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(10, recorded.Items.Count);
        var run = Assert.Single(recorded.Recording!.Runs);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "ctr_ranker").Outcome);
        var scores = Scores(run);
        Assert.Equal(30, scores.Count);
        Assert.All(scores.Values, s => Assert.InRange(s, 0.0, 1.0 - Math.Pow(2, -24)));

        var again = await engine.ExecuteAsync(ReplayTests.Request("req-random", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);
        Assert.Equal(scores, Scores(Assert.Single(again.Recording!.Runs)));
        var reseeded = await engine.ExecuteAsync(ReplayTests.Request("req-random-2", 1001, "home", ReplayTests.ControlArms), TestContext.Current.CancellationToken);
        Assert.NotEqual(recorded.Recording.Request.Seed, reseeded.Recording!.Request.Seed);
        var other = Scores(Assert.Single(reseeded.Recording.Runs));
        Assert.Equal(scores.Keys.Order(), other.Keys.Order());
        Assert.True(scores.Count(p => other[p.Key] != p.Value) >= 29, "a new seed draws new scores");

        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));
        var (fresh, _) = MetricsTests.LoadEngine(Host(Binding));
        var outcome = await fresh.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal(recorded.Items.Select(i => (i.Id.Int64Value, i.Rank)), outcome.Result.Items.Select(i => (i.Id.Int64Value, i.Rank)));
        Assert.Empty(outcome.Report.Gaps);
        Assert.All(outcome.Report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
    }

    [Theory]
    [InlineData("""{ "client": "random@1", "inputs": ["smoothed_ctr"], "outputs": ["p_click"] }""",
        "SERVICE_SIGNATURE", "pipeline:home_feed", "/nodes/ctr_ranker/config/inputs/log_followers")]
    [InlineData("""{ "client": "random@1", "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_like"] }""",
        "SERVICE_SIGNATURE", "pipeline:home_feed", "/nodes/ctr_ranker/config/outputs/p_click")]
    [InlineData("""{ "client": "random@1", "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click"], "seed": "42" }""",
        "BINDING_SETTINGS", "bindings:local", "/services/ctr_model/seed")]
    public void ABindingThatDoesNotFit_IsAnError_AtTheNodeOrTheSetting(string binding, string code, string artifact, string pointer)
    {
        var host = Host(binding);

        var compilation = PlanCompiler.Compile(host.Request, host.Environment);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.IsError && d.Code == code); // and the tap that reads the node's inputs
        Assert.Equal((artifact, pointer), (error.Artifact, error.Pointer));
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    private static HomeFeedTestHost Host(string binding) =>
        HomeFeedTestHost.LoadModified(root => SampleBindings.RebindService(root, "ctr_model", binding));

    private static Dictionary<long, double> Scores(ReplayRunRecord run)
    {
        var call = Assert.Single(run.ServiceCalls, c => c.Role == "model");
        Assert.Equal(("ctr_model", "ok"), (call.Service, call.Status));
        var keys = call.Keys!.Value.EnumerateArray().Select(k => k.GetInt64()).ToArray();
        var values = call.Outputs["p_click"].Values.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Assert.Equal(keys.Length, values.Length);
        return keys.Zip(values).ToDictionary(p => p.First, p => p.Second);
    }
}
