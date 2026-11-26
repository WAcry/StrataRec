using System.Text.Json.Nodes;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The sample's chain of context keys: <c>country</c> is keyed by the request's country and
/// answers its region (geo_store); <c>region</c> is keyed by <c>ctx.country.region</c> and answers the regional weight the
/// fusion multiplies by (region_store). Fetched, recorded with the key the upstream answered, and replayed with the key
/// the replayed upstream answers.
/// </summary>
public sealed class ChainedContextKeyEndToEndTests
{
    private static RecRequest Request(string requestId, string? country) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = country is null ? HomeFeedTestHost.Inputs(1001, "home") : HomeFeedTestHost.Inputs(1001, "home").Set("country", country),
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Record = true, ForceArms = ReplayTests.ControlArms },
    };

    private static List<(long Id, double Score)> Scores(RecResult result) =>
        result.Items.Select(i => (i.Id.Int64Value, i.Columns["score"].AsFloat64())).ToList();

    [Fact]
    public async Task ACountrysRegion_KeysTheRegionalWeight_AndNoCountry_OrOneWithoutARegion_ChangesNothing()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var none = await engine.ExecuteAsync(Request("req-none", null), TestContext.Current.CancellationToken);
        var mx = await engine.ExecuteAsync(Request("req-mx", "MX"), TestContext.Current.CancellationToken); // no region, no item boosts
        var ca = await engine.ExecuteAsync(Request("req-ca", "CA"), TestContext.Current.CancellationToken); // region na, no item boosts

        Assert.True(none.Succeeded && mx.Succeeded && ca.Succeeded, none.Error ?? mx.Error ?? ca.Error);
        Assert.Equal(Scores(none), Scores(mx)); // no region key: the weight is the default, 1.0
        Assert.Equal(Scores(none).Select(s => s.Id), Scores(ca).Select(s => s.Id)); // one weight for every item: the same order
        Assert.All(Scores(none).Zip(Scores(ca)), pair => Assert.Equal(pair.First.Score * 1.1, pair.Second.Score, 4));

        // No country: neither group has a key, so neither is called. MX: the geo service is asked and has no region, so the
        // region is not. CA: each is called once, the region with the key the geo service answered, after it.
        Assert.DoesNotContain(none.Recording!.Runs[0].FeatureCalls, c => c.Source is "geo_store" or "region_store");
        Assert.Single(mx.Recording!.Runs[0].FeatureCalls, c => c.Source == "geo_store");
        Assert.DoesNotContain(mx.Recording.Runs[0].FeatureCalls, c => c.Source == "region_store");
        var geo = Assert.Single(ca.Recording!.Runs[0].FeatureCalls, c => c.Source == "geo_store");
        var region = Assert.Single(ca.Recording.Runs[0].FeatureCalls, c => c.Source == "region_store");
        Assert.Equal(("CA", "na"), (geo.Keys[0].GetString(), geo.Values!["region"][0].GetString()));
        Assert.Equal(("region", "na", "ok"), (region.Entity, region.Keys[0].GetString(), region.Status));
        Assert.True(region.Seq > geo.Seq);
    }

    [Fact]
    public async Task ARecordedChain_Replays_AndAnUpstreamThatAnswersOtherwise_IsAGapDownstream()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(Request("req-us-chain", "US"), TestContext.Current.CancellationToken);
        Assert.True(recorded.Succeeded, recorded.Error);
        var text = ReplayBundleFile.Serialize(recorded.Recording!);

        var replayed = await engine.ReplayAsync(ReplayBundleFile.Parse(text), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(replayed.Report.Diff.Identical, ReplayTests.Describe(replayed.Report));
        Assert.Empty(replayed.Report.Gaps);

        // The recorded geo service answers "eu" instead: the replay keys the region by it, which the recording never asked
        // for, so the weight is a gap and the scores differ.
        var bundle = JsonNode.Parse(text)!;
        var geo = bundle["runs"]![0]!["feature_calls"]!.AsArray().Single(c => c!["source"]!.GetValue<string>() == "geo_store")!;
        Assert.Equal("na", geo["values"]!["region"]![0]!.GetValue<string>());
        geo["values"]!["region"]![0] = "eu";
        var changed = await engine.ReplayAsync(ReplayBundleFile.Parse(bundle.ToJsonString()), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(changed.Report.Diff.Identical);
        var gap = Assert.Single(changed.Report.Gaps, g => g.Source == "region_store");
        Assert.Equal(("feature", "boost", "not_recorded"), (gap.Kind, gap.Field, gap.Reason));
        Assert.Contains("eu", gap.Examples);
        Assert.DoesNotContain(changed.Report.Gaps, g => g.Source == "geo_store"); // the upstream call itself is as recorded
    }

    [Fact]
    public async Task AChainReadOnlyByAnArmsNode_IsInTheUnifiedPlan_NotTheBaseline_AndPrefetchedLikeAnyContextFeature()
    {
        // Only exp_freshness_calibration's calibrated arm reads the regional weight (its node multiplies by it; the fusion no
        // longer does). The unified plan compiles every arm's variants, so it holds the chain and its key; the baseline alone
        // does not. The context prefetch is the plan's (every variant's) context features, so a control request asks the
        // region service too, and only the arm's scores change.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            PlanFeatureClosureTests.EditJson(root, "pipelines/home_feed.jsonc", pipeline =>
                pipeline["nodes"]!["fusion"]!["config"]!["multipliers"]!.AsObject().Remove("region"));
            PlanFeatureClosureTests.EditJson(root, "experiments/exp_freshness_calibration.jsonc", experiment =>
            {
                var arm = experiment["arms"]!["calibrated"]!.AsObject();
                arm["nodes"]!["freshness_calibration"]!["config"]!["expr"] = "col.score.final * ctx.region.boost";
                arm.Remove("log_taps"); // it logged the age the node no longer reads
            });
        });
        var plan = host.Plan();
        Assert.Equal(
            // and what every plan reads: the recall query's embedding, and the engagement boost with the CTR it is computed from
            ["country.region", "engagement.propensity", "region.boost", "user.ctr_30d", "user.embedding"],
            plan.ContextFeatures.Select(f => plan.Features.Features[f].Definition.Name).Order(StringComparer.Ordinal));
        var baseline = host.CompileMerged([]);
        Assert.True(baseline.Succeeded, string.Join("\n", baseline.Diagnostics));
        Assert.DoesNotContain(baseline.Plan!.Features.Features, f => f.Definition.Name is "country.region" or "region.boost");
        Assert.DoesNotContain(baseline.Plan.Features.Groups, g => g.SourceName is "geo_store" or "region_store");

        async Task<Pipeline.Execution.PipelineResult> Run(string arm)
        {
            var result = await new Pipeline.Execution.PipelineExecutor().ExecuteAsync(plan, new Pipeline.Execution.PipelineRequest
            {
                RequestId = "req-" + arm,
                Inputs = HomeFeedTestHost.Inputs(1001, "home").Set("country", "CA"),
                Arms = plan.ArmsFromIds([arm]),
                Deadline = HomeFeedTestHost.Deadline,
                TimeMs = 1_767_225_600_000,
                Record = true,
            }, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Error);
            return result;
        }

        var control = await Run("exp_freshness_calibration/control");
        var calibrated = await Run("exp_freshness_calibration/calibrated");

        var before = control.Items.ToDictionary(i => i.Id, i => i.Columns["score"].AsFloat64());
        Assert.Equal(control.Items.Select(i => i.Id), calibrated.Items.Select(i => i.Id)); // one weight for every item
        Assert.All(calibrated.Items, i => Assert.Equal(before[i.Id] * 1.1, i.Columns["score"].AsFloat64(), 4));
        Assert.Single(control.Recording!.FeatureCalls, c => c.Source == "region_store");
        Assert.Single(calibrated.Recording!.FeatureCalls, c => c.Source == "region_store");
    }

    [Fact]
    public void TheChain_IsInThePlan_AndPrefetched()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var region = plan.Features.Features.Single(f => f.Definition.Name == "country.region").Index;
        var boost = plan.Features.Features.Single(f => f.Definition.Name == "region.boost").Index;
        var group = plan.Features.Groups.Single(g => g.SourceName == "region_store");

        Assert.Equal([region], group.KeyFeatures);
        Assert.Equal("ctx.country.region", group.KeyLayout[0].Reference);
        Assert.Contains(region, plan.ContextFeatures);
        Assert.Contains(boost, plan.ContextFeatures);
        Assert.Equal([region], plan.Features.Features[boost].Dependencies);
        Assert.Equal("string", group.KeyLayout.TypeText);
    }
}
