using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The sample's <c>ctr_model</c> bound to <c>random@1</c> instead of <c>linear@1</c>, as a dev or test environment binds a
/// real model's name to the stand-in (design 6.4 item 1): only the binding changes, the plan loads because the stand-in
/// declares the signature the scorer maps, the request is served with its scores, and a recording replays identically.
/// </summary>
public sealed class RandomModelTests
{
    private const string Binding = """{ "client": "random@1", "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click"] }""";

    [Fact]
    public async Task TheSampleModel_BoundToRandom_Serves_AndARecordingReplaysIdentically()
    {
        var (engine, _) = MetricsTests.LoadEngine(Host(Binding));

        var recorded = await engine.ExecuteAsync(ReplayTests.Request("req-random", 1001, "home", ReplayTests.ControlArms));

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(10, recorded.Items.Count);
        var run = Assert.Single(recorded.Recording!.Runs);
        Assert.Equal("succeeded", Assert.Single(run.Nodes, n => n.Node == "ctr_ranker").Outcome); // the stand-in answered: no fallback
        var scores = Scores(run);
        Assert.Equal(30, scores.Count);
        Assert.All(scores.Values, s => Assert.InRange(s, 0.0, 1.0 - Math.Pow(2, -24)));

        // The same request scores the same; a request with another seed scores the same candidates differently.
        var again = await engine.ExecuteAsync(ReplayTests.Request("req-random", 1001, "home", ReplayTests.ControlArms));
        Assert.Equal(scores, Scores(Assert.Single(again.Recording!.Runs)));
        var reseeded = await engine.ExecuteAsync(ReplayTests.Request("req-random-2", 1001, "home", ReplayTests.ControlArms));
        Assert.NotEqual(recorded.Recording.Request.Seed, reseeded.Recording!.Request.Seed);
        var other = Scores(Assert.Single(reseeded.Recording.Runs));
        Assert.Equal(scores.Keys.Order(), other.Keys.Order());
        Assert.True(scores.Count(p => other[p.Key] != p.Value) >= 29, "a new seed draws new scores");

        // Through JSON, on a fresh engine.
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));
        var (fresh, _) = MetricsTests.LoadEngine(Host(Binding));
        var outcome = await fresh.ReplayAsync(bundle);

        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal(recorded.Items.Select(i => (i.Id.Int64Value, i.Rank)), outcome.Result.Items.Select(i => (i.Id.Int64Value, i.Rank)));
        Assert.Empty(outcome.Report.Gaps);
        Assert.All(outcome.Report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
    }

    /// <summary>What <c>stratarec validate</c> finds without loading anything: the stand-in answers its signature at once.</summary>
    [Theory]
    [InlineData("""{ "client": "random@1", "inputs": ["smoothed_ctr"], "outputs": ["p_click"] }""",
        "MODEL_SIGNATURE", "pipeline:home_feed", "/nodes/ctr_ranker/config/inputs/log_followers")]
    [InlineData("""{ "client": "random@1", "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_like"] }""",
        "MODEL_SIGNATURE", "pipeline:home_feed", "/nodes/ctr_ranker/config/outputs/p_click")]
    [InlineData("""{ "client": "random@1", "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click"], "seed": "42" }""",
        "MODEL_CONFIG", "bindings:local", "/models/ctr_model/seed")]
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
        HomeFeedTestHost.LoadModified(root => SampleBindings.RebindModel(root, "ctr_model", binding));

    /// <summary>The recorded <c>p_click</c> of the run's one model call, by candidate id.</summary>
    private static Dictionary<long, double> Scores(ReplayRunRecord run)
    {
        var call = Assert.Single(run.ModelCalls);
        Assert.Equal(("ctr_model", "ok"), (call.Model, call.Status));
        var keys = call.Keys!.Value.EnumerateArray().Select(k => k.GetInt64()).ToArray();
        var values = call.Outputs["p_click"].EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Assert.Equal(keys.Length, values.Length);
        return keys.Zip(values).ToDictionary(p => p.First, p => p.Second);
    }
}
