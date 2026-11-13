using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

public sealed class HomeFeedEndToEndTests
{
    private static readonly string[] AllArms =
    {
        "exp_freshness_calibration/control", "exp_freshness_calibration/calibrated",
        "exp_mmr_diversity/control", "exp_mmr_diversity/mmr",
        "exp_quality_weight/control", "exp_quality_weight/strong",
    };

    [Fact]
    public void SampleCompilesWithOnlyTheExpectedInteractionWarning()
    {
        var compilation = HomeFeedTestHost.Load().Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.DoesNotContain(compilation.Diagnostics, d => d.IsError);

        // Calibration writes score.final at 470; MMR's added node reads it at 550 (design 11.5 example).
        var warning = Assert.Single(compilation.Diagnostics, d => d.Code == "INTERACTION_READ");
        Assert.Contains("mmr_diversifier", warning.Message);
        Assert.Contains("exp_freshness_calibration/calibrated", warning.Message);
        Assert.True(compilation.Report.CombinationCheckExhaustive);
    }

    [Fact]
    public void TimelineHasVariantsForAddedDeletedAndModifiedNodes()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var orders = plan.Timeline.Select(s => (s.Order, s.NodeId)).ToArray();

        Assert.Equal(new[]
        {
            (100, "recall"), (200, "safety_filter"), (300, "coarse_score"), (350, "coarse_top_k"), (400, "ctr_ranker"),
            (450, "fusion"), (470, "freshness_calibration"), (500, "rank_sort"), (520, "diversity_cap"), (550, "mmr_diversifier"),
        }, orders);

        var mmr = plan.ArmsFromIds(new[] { "exp_mmr_diversity/mmr" });
        var control = plan.ArmsFromIds(new[] { "exp_mmr_diversity/control" });
        Assert.Null(Slot(plan, "diversity_cap").Select(mmr));
        Assert.NotNull(Slot(plan, "diversity_cap").Select(control));
        Assert.NotNull(Slot(plan, "mmr_diversifier").Select(mmr));
        Assert.Null(Slot(plan, "mmr_diversifier").Select(control));
        Assert.Null(Slot(plan, "freshness_calibration").Select(RequestArms.None));
        Assert.Contains("ctr_backup", plan.Fallbacks.Keys);
    }

    [Fact]
    public async Task BaselineRequestReturnsDeterministicRankedItems()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var executor = new PipelineExecutor();

        var first = await executor.ExecuteAsync(plan, HomeFeedTestHost.NewRequest());
        var second = await executor.ExecuteAsync(plan, HomeFeedTestHost.NewRequest());

        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(Enumerable.Range(1, 10), first.Items.Select(i => i.Rank));
        Assert.Equal(Describe(first), Describe(second));
        Assert.All(first.Items, item => Assert.False(item.Columns["score"].IsMissing));
        Assert.Equal("fusion", first.ColumnProducers["score"]);
        Assert.All(first.Outcomes, o => Assert.Contains(o.Kind, new[] { NodeOutcomeKind.Succeeded, NodeOutcomeKind.Skipped }));
    }

    [Fact]
    public async Task DiversityCapLimitsCategoriesPerWindowOnHome()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(surface: "home"));

        var categories = result.Items.Select(i => i.Columns["category"].AsString()).ToArray();
        for (var i = 0; i + 2 < categories.Length; i++)
        {
            Assert.Equal(3, categories.Skip(i).Take(3).Distinct().Count());
        }
    }

    [Fact]
    public async Task UnifiedPlanMatchesThePlainCompileOfTheMergedConfigForEveryArmCombination()
    {
        // The differential anchor of design 16.2.
        var host = HomeFeedTestHost.Load();
        var unified = host.Plan();
        var executor = new PipelineExecutor();
        foreach (var fresh in new[] { "control", "calibrated" })
        {
            foreach (var mmr in new[] { "control", "mmr" })
            {
                foreach (var quality in new[] { "control", "strong" })
                {
                    var arms = new[] { $"exp_freshness_calibration/{fresh}", $"exp_mmr_diversity/{mmr}", $"exp_quality_weight/{quality}" };
                    var reference = PlanCompiler.CompileMerged(host.Request, arms, host.Environment);
                    Assert.True(reference.Succeeded, string.Join("\n", reference.Diagnostics));

                    foreach (var (user, surface) in new[] { (1001L, "home"), (1002L, "explore") })
                    {
                        var actual = await executor.ExecuteAsync(unified, HomeFeedTestHost.NewRequest(user, surface, unified.ArmsFromIds(arms)));
                        var expected = await executor.ExecuteAsync(reference.Plan!, HomeFeedTestHost.NewRequest(user, surface));
                        Assert.True(actual.Succeeded, actual.Error);
                        Assert.Equal(Describe(expected), Describe(actual));
                    }
                }
            }
        }
    }

    [Fact]
    public async Task ParameterOnlyArmChangesTheSnapshotAndTriggersAtTheOutput()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var strong = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(new[] { "exp_quality_weight/strong" })));
        var control = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(new[] { "exp_quality_weight/control" })));

        Assert.Equal(8, strong.Items.Count);
        Assert.Equal(10, control.Items.Count);
        Assert.Contains("exp_quality_weight", strong.TriggeredExperiments);
        Assert.Contains("exp_quality_weight", control.TriggeredExperiments); // symmetric across arms (design 12.6)
    }

    [Fact]
    public async Task TriggersAreSymmetricBetweenMmrArms()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        foreach (var surface in new[] { "home", "explore" })
        {
            foreach (var arm in new[] { "exp_mmr_diversity/control", "exp_mmr_diversity/mmr" })
            {
                var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(surface: surface, arms: plan.ArmsFromIds(new[] { arm })));
                // The site at 550 is unconditional (mmr has no `when`), so every request that reaches it triggers.
                Assert.Contains("exp_mmr_diversity", result.TriggeredExperiments);
            }
        }

        var unassigned = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest());
        Assert.Empty(unassigned.TriggeredExperiments);
    }

    [Fact]
    public async Task FailingModelIsServedByItsFallback()
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            var path = Path.Combine(root, "bindings", "local.jsonc");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"client\": \"linear@1\",", "\"client\": \"linear@1\", \"fail\": true,", StringComparison.Ordinal));
        });
        var plan = host.Plan();

        var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(captureLogs: true));

        Assert.True(result.Succeeded, result.Error);
        var ranker = Assert.Single(result.Outcomes, o => o.NodeId == "ctr_ranker");
        Assert.Equal(NodeOutcomeKind.Degraded, ranker.Kind);
        Assert.Equal("ctr_backup", ranker.ProducedBy);

        var inputs = Assert.Single(result.LogBatches, b => b.TapId == "ranker_inputs");
        Assert.Equal("Failed", inputs.NodeOutcomes["ctr_ranker"]);
        Assert.Equal("Succeeded", inputs.NodeOutcomes["ctr_backup"]);
        Assert.Equal("ctr_backup", inputs.ColumnProducers["p_click"]);
    }

    [Fact]
    public async Task TapsCaptureRowsWithEnvelopeAndLineage()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var arms = plan.ArmsFromIds(new[] { "exp_freshness_calibration/calibrated" });
        var result = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: arms, captureLogs: true));

        Assert.True(result.Succeeded, result.Error);
        var served = Assert.Single(result.LogBatches, b => b.TapId == "served");
        Assert.Equal(LogDelivery.Durable, served.Delivery);
        Assert.Equal(result.Items.Select(i => i.Id.Int64Value), served.Int64EntityIds!);
        Assert.Equal(Enumerable.Range(1, 10), served.Ranks);
        Assert.Contains(served.Columns, c => c.Name == "pre_calibration"); // the arm's tap variant
        Assert.Contains(served.Columns, c => c.Name == "age_hours");
        Assert.Equal("freshness_calibration", served.ColumnProducers["final"]);
        Assert.Equal("fusion", served.ColumnProducers["pre_calibration"]);
        Assert.Equal(new[] { "exp_freshness_calibration/calibrated" }, served.AssignedArms);
        Assert.Contains("exp_freshness_calibration", served.TriggeredExperiments);

        var dropped = Assert.Single(result.LogBatches, b => b.TapId == "coarse_dropped");
        Assert.InRange(dropped.RowCount, 1, 5);
        // TopK reorders before truncating, so a dropped row's pre-node rank is its recall position: any valid rank.
        Assert.All(dropped.RanksBefore!, rank => Assert.InRange(rank, 1, 60));
        Assert.Equal(dropped.RanksBefore!.Length, dropped.RanksBefore!.Distinct().Count());
        Assert.All(dropped.Ranks, rank => Assert.Equal(0, rank));
        Assert.All(dropped.RowProbabilities, p => Assert.InRange(p, 0.0, 1.0));

        var ranker = Assert.Single(result.LogBatches, b => b.TapId == "ranker_inputs");
        Assert.Equal(new[] { "log_followers", "p_click", "smoothed_ctr" }, ranker.Columns.Select(c => c.Name).Order());

        Assert.Single(result.LogBatches, b => b.TapId == "$experiments");

        // Without the arm the tap uses the baseline variant.
        var baseline = await new PipelineExecutor().ExecuteAsync(plan, HomeFeedTestHost.NewRequest(captureLogs: true));
        Assert.DoesNotContain(Assert.Single(baseline.LogBatches, b => b.TapId == "served").Columns, c => c.Name == "pre_calibration");
    }

    [Fact]
    public async Task LogPipelineReceivesBatchesWithSequenceNumbers()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var sink = new InMemoryLogSink();
        await using (var logs = new LogPipeline(sink, producerId: "test/1"))
        {
            var executor = new PipelineExecutor(logs: logs);
            for (var user = 1001; user <= 1002; user++)
            {
                await executor.ExecuteAsync(plan, HomeFeedTestHost.NewRequest(user));
            }

            await logs.FlushAsync();
        }

        var durable = sink.Snapshot().Where(b => b.Delivery == LogDelivery.Durable).Select(b => b.Seq).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, durable.Length).Select(i => (long)i), durable);
        Assert.Contains(sink.Snapshot(), b => b.TapId == "coarse_dropped" && b.Delivery == LogDelivery.BestEffort);
    }

    [Fact]
    public async Task EngineAssignsArmsAndServesThroughTheHostApi()
    {
        var engine = new RecEngine(HomeFeedTestHost.Load().Environment);
        var diagnostics = ServiceCollectionExtensions.LoadWorkspace(engine, Samples.HomeFeed, "local");
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));

        var assignedArms = new HashSet<string>();
        for (var user = 0; user < 200; user++)
        {
            var result = await engine.ExecuteAsync(new RecRequest
            {
                PipelineId = "home_feed",
                RequestId = "req-" + user,
                Inputs = HomeFeedTestHost.Inputs(1001 + (user % 2), "home").Set("session_id", "s-" + user).Set("user_id", user),
                Deadline = TimeSpan.FromSeconds(5),
            });

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(3, result.Experiments.Assigned.Count); // 100% of traffic is allocated in each layer
            assignedArms.UnionWith(result.Experiments.Assigned);
        }

        Assert.Equal(AllArms.Order(), assignedArms.Order());
    }

    [Fact]
    public async Task MissingRequiredInputFailsWithAClearError()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var request = new PipelineRequest { RequestId = "r", Inputs = new RequestInputs().Set("user_id", 1001L), Deadline = TimeSpan.FromSeconds(1) };

        var result = await new PipelineExecutor().ExecuteAsync(plan, request);

        Assert.False(result.Succeeded);
        Assert.Contains("surface", result.Error);
    }

    [Fact]
    public void ConflictingExperimentsInDifferentLayersAreRejected()
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            File.WriteAllText(Path.Combine(root, "experiments", "exp_conflict.jsonc"), """
                { "experiment_id": "exp_conflict", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {}, "k": { "params": { "quality_weight": 0.5 } } } }
                """);
            var allocation = Path.Combine(root, "allocation", "home_feed.local.jsonc");
            File.WriteAllText(allocation, File.ReadAllText(allocation).Replace(
                "\"exp_quality_weight\":",
                "\"exp_conflict\": { \"layer\": \"rerank\", \"arms\": { \"control\": [0, 0], \"k\": [1, 1] } },\n    \"exp_quality_weight\":",
                StringComparison.Ordinal));
        });

        var compilation = host.Compile();

        Assert.False(compilation.Succeeded);
        var conflict = Assert.Single(compilation.Diagnostics, d => d.Code == "PATCH_CONFLICT");
        Assert.EndsWith("/params/quality_weight", conflict.Pointer, StringComparison.Ordinal);
        Assert.Contains("exp_quality_weight/strong", conflict.Message);
        Assert.Contains("exp_conflict/k", conflict.Message);
    }

    [Fact]
    public void ArmReferencingAnotherArmsAdditionViolatesIndependence()
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            // Reads mmr_lambda, which only exp_mmr_diversity/mmr declares (design 11.6).
            File.WriteAllText(Path.Combine(root, "experiments", "exp_borrow.jsonc"), """
                { "experiment_id": "exp_borrow", "pipeline_id": "home_feed", "owner": "t", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {}, "borrow": { "nodes": { "fusion": { "config": { "multipliers": { "lambda": "param.mmr_lambda" } } } } } } }
                """);
            var allocation = Path.Combine(root, "allocation", "home_feed.local.jsonc");
            File.WriteAllText(allocation, File.ReadAllText(allocation).Replace(
                "\"exp_quality_weight\":",
                "\"exp_borrow\": { \"layer\": \"extra\" },\n    \"exp_quality_weight\":",
                StringComparison.Ordinal));
        });

        var compilation = host.Compile();

        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, d => d.IsError && d.Artifact == "experiment:exp_borrow" && d.Pointer.StartsWith("/arms/borrow/nodes/fusion", StringComparison.Ordinal));
    }

    private static string Describe(PipelineResult result) =>
        string.Join(";", result.Items.Select(i => $"{i.Rank}:{i.Id}:{string.Join(",", i.Columns.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"))}"));

    private static TimelineSlot Slot(ExecutionPlan plan, string nodeId) => plan.Timeline.Single(s => s.NodeId == nodeId);
}
