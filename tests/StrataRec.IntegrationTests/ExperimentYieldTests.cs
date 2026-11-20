using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Experiments.Assignment;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Plans;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;

namespace StrataRec.IntegrationTests;

/// <summary>
/// One experiment yielding to another of a different layer (a release's <c>yields_to</c>, design 11.1, 11.5): on a
/// request in both, the one that yields runs in none of its arms, so a conflict between them is no error, and what a
/// request in both runs is what the experiment it yields to runs alone.
/// </summary>
public sealed class ExperimentYieldTests
{
    /// <summary>exp_short_feed (its own layer) shortens the feed to 5; exp_quality_weight/strong sets the same parameter to 8.</summary>
    private static void ShortFeed(string root, string patch = """{ "params": { "final_top_k": 5 } }""") =>
        InteractionWarningTests.Experiment(root, "exp_short_feed", "short", patch);

    private static void ShortYields(string root) => ReplayTests.Edit(root, "releases/home_feed.local.jsonc",
        "\"exp_short_feed\": { \"layer\": \"exp_short_feed\" }", "\"exp_short_feed\": { \"layer\": \"exp_short_feed\", \"yields_to\": [\"exp_quality_weight\"] }");

    private static void QualityYields(string root, string to = "exp_short_feed") => ReplayTests.Edit(root, "releases/home_feed.local.jsonc",
        "{ \"layer\": \"params\" }", $"{{ \"layer\": \"params\", \"yields_to\": [\"{to}\"] }}");

    [Fact]
    public void TwoLayersChangingOneParameter_AreAConflict_WhoseHintSaysHowOneYields()
    {
        var compilation = HomeFeedTestHost.LoadModified(root => ShortFeed(root)).Compile();

        Assert.False(compilation.Succeeded);
        var conflict = Assert.Single(compilation.Diagnostics, d => d.Code == "PATCH_CONFLICT");
        Assert.Contains("/params/final_top_k", conflict.Pointer, StringComparison.Ordinal);
        // Either may yield; the hint shows the entry for one of them.
        Assert.Contains("\"yields_to\": [\"exp_quality_weight\"]", conflict.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void OnceOneYields_TheReleaseCompilesCleanly_AndTheReportSaysWhatTheYieldKeepsApart()
    {
        var compilation = HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        }).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.Empty(compilation.Diagnostics);
        var yield = Assert.Single(compilation.Report.Yields);
        Assert.Equal(("exp_quality_weight", "exp_short_feed"), (yield.Experiment, yield.To));
        Assert.Equal(["PATCH_CONFLICT /params/final_top_k"], yield.Resolves);
    }

    /// <summary>
    /// On a request in both, the experiment that yields runs in none of its arms, its control included, and is recorded
    /// as yielded; whatever assigned the arms, forced arms too. A request in only one of them runs it as before.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestInBoth_RunsWithoutTheExperimentThatYields_InAnyOfItsArms(bool forced)
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        });
        await using var engine = MetricsTests.LoadEngine(host).Engine;

        var alone = await engine.ExecuteAsync(Request("req-alone", forced, "exp_quality_weight/strong"), TestContext.Current.CancellationToken);
        var both = await engine.ExecuteAsync(Request("req-both", forced, "exp_quality_weight/strong", "exp_short_feed/short"), TestContext.Current.CancellationToken);
        var controls = await engine.ExecuteAsync(Request("req-controls", forced, "exp_quality_weight/control", "exp_short_feed/control"), TestContext.Current.CancellationToken);

        Assert.True(alone.Succeeded && both.Succeeded && controls.Succeeded, alone.Error ?? both.Error ?? controls.Error);
        Assert.Equal(8, alone.Items.Count);
        Assert.Empty(alone.Experiments.Ignored);

        Assert.Equal(5, both.Items.Count); // exp_short_feed's value: exp_quality_weight/strong did not run
        Assert.Equal(["exp_short_feed/short"], both.Experiments.Assigned);
        Assert.Equal([new IgnoredArm("exp_quality_weight/strong", IgnoredArmReason.Yielded)], both.Experiments.Ignored);
        Assert.DoesNotContain("exp_quality_weight", both.Experiments.Triggered);

        // The control yields as the treatment does: the experiment's arms stay comparable.
        Assert.Equal(["exp_short_feed/control"], controls.Experiments.Assigned);
        Assert.Equal([new IgnoredArm("exp_quality_weight/control", IgnoredArmReason.Yielded)], controls.Experiments.Ignored);
    }

    [Fact]
    public void ToolsRefuseACombinationThatNeverRuns()
    {
        var plan = HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        }).Plan();

        var never = plan.NeverRunTogether(plan.ArmsFromIds(["exp_quality_weight/strong", "exp_short_feed/short"]));

        Assert.Equal("'exp_quality_weight' yields to 'exp_short_feed': a request in both runs without 'exp_quality_weight/strong'", never);
        Assert.Null(plan.NeverRunTogether(plan.ArmsFromIds(["exp_quality_weight/strong", "exp_freshness_calibration/calibrated"])));
    }

    /// <summary>
    /// A recording of a request in both, made where they ran together (no yield, no conflict), replays on a plan where one
    /// yields without it, as a request in them runs there now; and a recording with a yielded arm replays as recorded.
    /// </summary>
    [Fact]
    public async Task Replays_RunTheArmsAsTheReplayedPlanDoes_AndReadAYieldedArm()
    {
        await using var before = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root => ShortFeed(root, "{}"))).Engine;
        await using var after = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        })).Engine;
        var token = TestContext.Current.CancellationToken;

        var together = await before.ExecuteAsync(Request("req-together", forced: false, "exp_quality_weight/strong", "exp_short_feed/short", record: true), token);
        Assert.Equal(["exp_short_feed/short", "exp_quality_weight/strong"], together.Experiments.Assigned); // in layer order
        var replayed = await after.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(together.Recording!)), cancellationToken: token);
        Assert.Contains("arm 'exp_quality_weight/strong' yields to 'exp_short_feed' in the replayed plan; replayed without it", replayed.Report.Notes);
        Assert.Equal(5, replayed.Result.Items.Count);
        Assert.Equal([new IgnoredArm("exp_quality_weight/strong", IgnoredArmReason.Yielded)], replayed.Result.Experiments.Ignored); // as a request now records it

        var yielded = await after.ExecuteAsync(Request("req-yielded", forced: false, "exp_quality_weight/strong", "exp_short_feed/short", record: true), token);
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(yielded.Recording!));
        Assert.Equal("yielded", Assert.Single(bundle.Request.Assignment.Ignored).Reason);
        var again = await after.ReplayAsync(bundle, cancellationToken: token);
        Assert.True(again.Report.Diff.Identical, string.Join("\n", again.Report.Notes));
        Assert.Equal([new IgnoredArm("exp_quality_weight/strong", IgnoredArmReason.Yielded)], again.Result.Experiments.Ignored);
    }

    /// <summary>
    /// A replay resolves the yields of the replayed plan over the arms the recording was assigned, those that yielded
    /// included (review 96): a recording where the quality experiment yielded, replayed where the short feed yields to
    /// it instead, runs as a request in both runs there now, and differs from the recording. Arms passed to the replay
    /// yield the same way.
    /// </summary>
    [Fact]
    public async Task AReplay_ResolvesTheRecordedAssignment_OverTheYieldsOfTheReplayedPlan()
    {
        await using var recorded = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        })).Engine;
        await using var reversed = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            ShortYields(root);
        })).Engine;
        var token = TestContext.Current.CancellationToken;
        var live = await recorded.ExecuteAsync(Request("req-recorded", forced: false, "exp_quality_weight/strong", "exp_short_feed/short", record: true), token);
        var now = await reversed.ExecuteAsync(Request("req-now", forced: false, "exp_quality_weight/strong", "exp_short_feed/short"), token);
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(live.Recording!));

        var replayed = await reversed.ReplayAsync(bundle, cancellationToken: token);
        var withArms = await reversed.ReplayAsync(bundle, new ReplayOptions { Arms = ["exp_quality_weight/strong", "exp_short_feed/short"] }, token);

        Assert.Equal((5, 8), (live.Items.Count, now.Items.Count));
        Assert.Equal(8, replayed.Result.Items.Count);
        Assert.Equal(now.Experiments.Assigned, replayed.Result.Experiments.Assigned);
        Assert.Equal([new IgnoredArm("exp_short_feed/short", IgnoredArmReason.Yielded)], replayed.Result.Experiments.Ignored);
        Assert.False(replayed.Report.Diff.Identical);
        Assert.Contains("arm 'exp_quality_weight/strong' was ignored in the recording (yielded) and runs in the replayed plan", replayed.Report.Notes);
        Assert.Contains("arm 'exp_short_feed/short' yields to 'exp_quality_weight' in the replayed plan; replayed without it", replayed.Report.Notes);
        Assert.Equal(8, withArms.Result.Items.Count);
        Assert.Equal([new IgnoredArm("exp_short_feed/short", IgnoredArmReason.Yielded)], withArms.Result.Experiments.Ignored);
    }

    /// <summary>
    /// A recording of the platform's arms replays through the platform's rule (review 98): where the replayed plan puts
    /// the two experiments into one layer, both arms are a layer conflict and the request runs the baseline, as a request
    /// in them would now, not an invalid request; an arm the replayed plan does not have is ignored as unknown. Forced
    /// arms stay strict, as in a request.
    /// </summary>
    [Fact]
    public async Task AReplayOfThePlatformsArms_IgnoresThemAsTheReplayedPlanWould()
    {
        await using var recorded = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        })).Engine;
        await using var oneLayer = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            ReplayTests.Edit(root, "releases/home_feed.local.jsonc", "\"exp_short_feed\": { \"layer\": \"exp_short_feed\" }", "\"exp_short_feed\": { \"layer\": \"params\" }");
        })).Engine;
        await using var without = MetricsTests.LoadEngine(HomeFeedTestHost.Load()).Engine;
        var token = TestContext.Current.CancellationToken;
        var platform = await recorded.ExecuteAsync(Request("req-platform", forced: false, "exp_quality_weight/strong", "exp_short_feed/short", record: true), token);
        var forced = await recorded.ExecuteAsync(Request("req-forced", forced: true, "exp_quality_weight/strong", "exp_short_feed/short", record: true), token);
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(platform.Recording!));

        var conflict = await oneLayer.ReplayAsync(bundle, cancellationToken: token);
        var gone = await without.ReplayAsync(bundle, cancellationToken: token);
        var strict = await oneLayer.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(forced.Recording!)), cancellationToken: token);

        Assert.True(conflict.Result.Succeeded, conflict.Result.Error);
        Assert.Equal(10, conflict.Result.Items.Count);
        Assert.Empty(conflict.Result.Experiments.Assigned);
        Assert.Equal(
            [new IgnoredArm("exp_quality_weight/strong", IgnoredArmReason.LayerConflict), new IgnoredArm("exp_short_feed/short", IgnoredArmReason.LayerConflict)],
            conflict.Result.Experiments.Ignored);
        Assert.Contains("arm 'exp_short_feed/short' is in a layer with another arm of the request in the replayed plan (layer_conflict); replayed without it", conflict.Report.Notes);

        Assert.Equal(8, gone.Result.Items.Count);
        Assert.Equal(["exp_quality_weight/strong"], gone.Result.Experiments.Assigned);
        Assert.Equal([new IgnoredArm("exp_short_feed/short", IgnoredArmReason.Unknown)], gone.Result.Experiments.Ignored);
        Assert.Contains("arm 'exp_short_feed/short' is not in the replayed plan; replayed without it", gone.Report.Notes);
        Assert.Contains("arm 'exp_quality_weight/strong' was ignored in the recording (yielded) and runs in the replayed plan", gone.Report.Notes);

        Assert.False(strict.Result.Succeeded);
        Assert.Equal(RecErrorKind.InvalidRequest, strict.Result.ErrorKind);
    }

    /// <summary>
    /// A replay's arms come from the request's assignment record (review 99): an edited assignment replays as edited and
    /// shows as replayed arms that differ from those the primary run recorded, rather than replaying the run's arms
    /// silently; an ignored id listed twice runs once and is noted once.
    /// </summary>
    [Fact]
    public async Task AReplay_RunsTheAssignmentRecord_AndNotesAnIdOnce()
    {
        await using var sample = MetricsTests.LoadEngine(HomeFeedTestHost.Load()).Engine;
        await using var withShortFeed = MetricsTests.LoadEngine(HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        })).Engine;
        var token = TestContext.Current.CancellationToken;
        var strong = await sample.ExecuteAsync(Request("req-strong", forced: false, "exp_quality_weight/strong", record: true), token);
        var unknown = await sample.ExecuteAsync(Request("req-unknown", forced: false, "exp_short_feed/short", record: true), token);
        var edited = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(strong.Recording!));
        edited.Request.Assignment.Arms[0] = "exp_quality_weight/control";
        var twice = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(unknown.Recording!));
        twice.Request.Assignment.Ignored.Add(twice.Request.Assignment.Ignored[0]);

        var control = await sample.ReplayAsync(edited, cancellationToken: token);
        var once = await withShortFeed.ReplayAsync(twice, cancellationToken: token);

        Assert.Equal(10, control.Result.Items.Count);
        Assert.Equal(["exp_quality_weight/strong"], control.Report.RecordedArms);
        Assert.Equal(["exp_quality_weight/control"], control.Report.ReplayedArms);
        Assert.Equal(5, once.Result.Items.Count);
        Assert.Single(once.Report.Notes, n => n.Contains("was ignored in the recording", StringComparison.Ordinal));
    }

    /// <summary>
    /// A yield changes what a request in both experiments runs, which no combination the default diff compares shows, so
    /// it is a structural change; a reversed one is one removed and one added (review 96). An effective plan is the one a
    /// request in the arms runs, without the arm that yields, as the executor runs it.
    /// </summary>
    [Fact]
    public void ADiff_ReportsAYieldAddedOrReversed_AndAnEffectivePlanRunsWithoutTheArmThatYields()
    {
        var quality = HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        }).Plan();
        var shortFeed = HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            ShortYields(root);
        }).Plan();

        var diff = PlanDiff.Compare(quality, shortFeed);
        var effective = EffectivePlan.Create(quality, quality.ArmsFromIds(["exp_quality_weight/strong", "exp_short_feed/short"]));

        Assert.True(diff.HasDifferences);
        Assert.Empty(diff.Combinations); // the baseline and each arm alone run as before
        Assert.Equal(
            [("exp_quality_weight", "exp_short_feed", StructureChangeKind.Removed), ("exp_short_feed", "exp_quality_weight", StructureChangeKind.Added)],
            diff.Structure.Select(c => (c.Id, c.YieldsTo!, c.Change)).ToArray());
        Assert.All(diff.Structure, c => Assert.Equal("yield", c.Kind));
        Assert.Equal(["exp_short_feed/short"], effective.ArmIds);
        Assert.Equal(5L, effective.ParamsDocument()["final_top_k"]!.GetValue<long>());
    }

    [Fact]
    public void AReleaseYieldingToAnExperimentItDoesNotRun_IsAnErrorAtTheEntry_WithTheNameItMeant()
    {
        var root = HomeFeedTestHost.LoadModified(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        }).Root;
        ReplayTests.Edit(root, "releases/home_feed.local.jsonc", "[\"exp_short_feed\"]", "[\"exp_short_fed\"]");
        var diagnostics = new DiagnosticBag();

        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("home_feed", "local", diagnostics);

        Assert.Null(request);
        var error = Assert.Single(diagnostics.Items, d => d.IsError);
        Assert.Equal("YIELD_UNKNOWN", error.Code);
        Assert.Equal("releases/home_feed.local.jsonc", error.Artifact);
        Assert.Equal("/experiments/exp_quality_weight/yields_to/0", error.Pointer);
        Assert.Contains("exp_short_feed", error.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest carries the yields (and none when there are none, so manifests without them keep their ids); a node
    /// compiles them from it; a revocation of the experiment yielded to removes the entry's yield, which yields nothing.
    /// </summary>
    [Fact]
    public async Task AManifest_CarriesTheYields_AndARevocationOfTheOtherExperimentDropsThem()
    {
        var root = ManifestStores.Workspace(root =>
        {
            ShortFeed(root);
            QualityYields(root);
        });
        var store = ManifestStores.NewStore();
        var (id, file) = await ManifestStores.BuildAsync(root, store);
        var manifest = ManifestStores.Read(file);
        Assert.Equal(["exp_short_feed"], manifest.Experiments["exp_quality_weight"].YieldsTo);
        Assert.Null(manifest.Experiments["exp_mmr_diversity"].YieldsTo);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "\"yields_to\"")); // none written where none is declared

        var token = TestContext.Current.CancellationToken;
        var artifacts = new FileSystemArtifactStore(store);
        var loaded = await ManifestBuilder.LoadAsync(manifest, id, artifacts, new DiagnosticBag(), token);
        Assert.Equal(["exp_short_feed"], Assert.Single(loaded!.Experiments, p => p.Experiment.ExperimentId == "exp_quality_weight").YieldsTo);
        var compiled = PlanCompiler.Compile(loaded, HomeFeedTestHost.Load().Environment);
        Assert.True(compiled.Succeeded, string.Join("\n", compiled.Diagnostics));
        Assert.Single(compiled.Report.Yields);

        var revoked = await ManifestBuilder.RevokeAsync(manifest, id, ["exp_short_feed"], artifacts, new DiagnosticBag(), token);
        Assert.Null(revoked!.Experiments["exp_quality_weight"].YieldsTo);
    }

    private static RecRequest Request(string requestId, bool forced, string first, string? second = null, bool record = false)
    {
        string[] arms = second is null ? [first] : [first, second];
        return new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = requestId,
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Assignment = forced ? null : arms,
            Deadline = HomeFeedTestHost.Deadline,
            TimeMs = 1_767_225_600_000,
            Debug = forced || record ? new DebugOptions { ForceArms = forced ? arms : null, Record = record } : null,
        };
    }
}

/// <summary>What a declared yield may and may not do, compiled from experiments placed in code (design 11.1).</summary>
public sealed class ExperimentYieldDeclarationTests
{
    private const string Top5 = """{ "params": { "final_top_k": 5 } }""";
    private const string Top7 = """{ "params": { "final_top_k": 7 } }""";

    [Fact]
    public void AYieldToItself_AndACycle_AreErrorsAtTheEntry()
    {
        var self = Compile(Yields(CombinationLimitTests.Treatment("a", Top5), "a"));
        var cycle = Compile(Yields(CombinationLimitTests.Treatment("a", Top5), "b"), Yields(CombinationLimitTests.Treatment("b", Top7), "a"));

        var selfError = Assert.Single(self.Diagnostics, d => d.IsError);
        Assert.Equal(("YIELD_SELF", "release", "/experiments/a/yields_to/0"), (selfError.Code, selfError.Artifact, selfError.Pointer));
        var cycleError = Assert.Single(cycle.Diagnostics, d => d.IsError);
        Assert.Equal("YIELD_CYCLE", cycleError.Code);
        Assert.Contains("a yields to b yields to a", cycleError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AYieldThatKeepsNothingApart_IsAWarning_InOneLayerOrNot()
    {
        var sameLayer = Compile(
            Yields(new ExperimentPlacement(CombinationLimitTests.Experiment("a", Arm(Top5)), "shared"), "b"),
            new ExperimentPlacement(CombinationLimitTests.Experiment("b", Arm(Top7)), "shared"));
        var unused = Compile(
            Yields(CombinationLimitTests.Treatment("a", Top5), "b"),
            CombinationLimitTests.Treatment("b", """{ "params": { "quality_weight": 0.7 } }"""));

        Assert.True(sameLayer.Succeeded && unused.Succeeded, string.Join("\n", sameLayer.Diagnostics.Concat(unused.Diagnostics)));
        Assert.Equal("YIELD_SAME_LAYER", Assert.Single(sameLayer.Diagnostics).Code);
        var warning = Assert.Single(unused.Diagnostics);
        Assert.Equal(("YIELD_UNUSED", "/experiments/a/yields_to/0"), (warning.Code, warning.Pointer));
        Assert.Empty(Assert.Single(unused.Report.Yields).Resolves);
    }

    /// <summary>A yield to an experiment the compile does not have yields nothing: a revocation loads unchecked (design 11.6).</summary>
    [Fact]
    public void AYieldToAnExperimentNotCompiled_YieldsNothing()
    {
        var compilation = Compile(Yields(CombinationLimitTests.Treatment("a", Top5), "gone"));

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(compilation.Report.Yields);
    }

    /// <summary>An added name has one meaning everywhere (design 11.4): two definitions are an error even where the two never run together.</summary>
    [Fact]
    public void ADifferentDefinitionOfOneAddedName_IsStillAnError()
    {
        var compilation = Compile(
            Yields(CombinationLimitTests.Treatment("a", """{ "columns": { "x.added": { "type": "float64" } } }"""), "b"),
            CombinationLimitTests.Treatment("b", """{ "columns": { "x.added": { "type": "int64" } } }"""));

        Assert.Contains(compilation.Diagnostics, d => d.Code == "ADD_CONFLICT");
    }

    /// <summary>
    /// Two layers changing one multiplier of fusion differently: a node defined by both is never compiled, and a request
    /// in both runs fusion as the experiment yielded to defines it, the same as that arm alone and as the merged compile.
    /// </summary>
    [Fact]
    public async Task ANodeBothChange_RunsAsTheExperimentYieldedTo_OnARequestInBoth()
    {
        var host = HomeFeedTestHost.Load();
        var request = CombinationLimitTests.With(host.Request, [
            Yields(CombinationLimitTests.Treatment("a", """{ "nodes": { "fusion": { "config": { "multipliers": { "quality": "0.5" } } } } }"""), "b"),
            CombinationLimitTests.Treatment("b", """{ "nodes": { "fusion": { "config": { "multipliers": { "quality": "2.0" } } } } }"""),
        ]);
        var unified = host.Compile(request);
        Assert.True(unified.Succeeded, string.Join("\n", unified.Diagnostics));
        Assert.Empty(unified.Diagnostics);
        var plan = unified.Plan!;
        var lease = host.Bindings.OpenLease();
        var merged = await lease.CompileAsync(bindings => PlanCompiler.CompileMerged(request, ["a/t", "b/t"], host.Environment, bindings), TestContext.Current.CancellationToken);
        Assert.Empty(await lease.WaitLoadedAsync(TestContext.Current.CancellationToken));
        Assert.True(merged.Succeeded, string.Join("\n", merged.Diagnostics));

        var executor = new PipelineExecutor();
        var token = TestContext.Current.CancellationToken;
        var both = await executor.ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(["a/t", "b/t"])), token);
        var onlyB = await executor.ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(["b/t"])), token);
        var onlyA = await executor.ExecuteAsync(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(["a/t"])), token);
        var reference = await executor.ExecuteAsync(merged.Plan!, HomeFeedTestHost.NewRequest(), token);

        Assert.Equal(["b/t"], both.AssignedArms);
        Assert.Equal(Scores(onlyB), Scores(both));
        Assert.Equal(Scores(reference), Scores(both));
        Assert.NotEqual(Scores(onlyA), Scores(both));
    }

    /// <summary>
    /// The sample's boost writes score.final beside calibration and before MMR reads it: two interactions, which yielding
    /// to both experiments keeps apart, with no warning; the report lists them.
    /// </summary>
    [Fact]
    public void AnExperimentYieldingToThoseItInteractsWith_IsNotWarnedAbout()
    {
        var compilation = HomeFeedTestHost.LoadModified(root =>
        {
            InteractionWarningTests.Experiment(root, "exp_boost", "boost",
                """{ "nodes": { "boost": { "order": 480, "op": "ExpressionScorer@1", "config": { "output_col": "score.final", "expr": "col.score.final * 1.1" } } } }""");
            ReplayTests.Edit(root, "releases/home_feed.local.jsonc", "\"exp_boost\": { \"layer\": \"exp_boost\" }",
                "\"exp_boost\": { \"layer\": \"exp_boost\", \"yields_to\": [\"exp_freshness_calibration\", \"exp_mmr_diversity\"] }");
        }).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.Empty(compilation.Diagnostics);
        Assert.Contains("INTERACTION_FLOW_COLUMN score.final", compilation.Report.Yields.Single(y => y.To == "exp_freshness_calibration").Resolves);
        Assert.Equal(["INTERACTION_READ score.final"], compilation.Report.Yields.Single(y => y.To == "exp_mmr_diversity").Resolves);
    }

    /// <summary>
    /// The combination limit counts the combinations that run: a yield removes those with both experiments, as one layer
    /// would, so declaring it instead of merging two layers costs no more checks (review 96). Three layers changing one
    /// multiplier of fusion, each yielding to those after it, run 4 of the 8 combinations; with one yield between two of
    /// three layers changing fusion, 6 run.
    /// </summary>
    [Fact]
    public void TheCombinationLimit_CountsTheCombinationsThatRun()
    {
        var host = HomeFeedTestHost.Load();
        var chain = new[] { Yields(Fusion("a", "quality", "1.1"), "b", "c"), Yields(Fusion("b", "quality", "1.2"), "c"), Fusion("c", "quality", "1.3") };
        var one = new[] { Yields(Fusion("a", "quality", "1.1"), "b"), Fusion("b", "quality", "1.2"), Fusion("c", "locale", "1.3") };

        var four = PlanCompiler.Compile(CombinationLimitTests.With(host.Request, chain, maxCombinationsChecked: 4), host.Environment);
        var six = PlanCompiler.Compile(CombinationLimitTests.With(host.Request, one, maxCombinationsChecked: 6), host.Environment);
        var five = PlanCompiler.Compile(CombinationLimitTests.With(host.Request, one, maxCombinationsChecked: 5), host.Environment);

        Assert.True(four.Succeeded, string.Join("\n", four.Diagnostics));
        Assert.Equal(4, four.Report.CombinationsChecked);
        Assert.True(six.Succeeded, string.Join("\n", six.Diagnostics));
        Assert.Equal(6, six.Report.CombinationsChecked);
        var limit = five.Diagnostics.First(d => d.Code == "COMBINATION_CHECK_LIMIT"); // one per data-flow rule fusion decides
        Assert.Contains("too many combinations, more than the 5 that can be checked", limit.Message, StringComparison.Ordinal);
        Assert.Contains("yield", limit.Hint, StringComparison.Ordinal);
    }

    /// <summary>A review of an interaction that a yield keeps apart is unused; the hint names the yield, since the two are in different layers (review 96).</summary>
    [Fact]
    public void AReviewOfAnInteractionAYieldKeepsApart_IsUnused_AndTheHintNamesTheYield()
    {
        var compilation = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "releases/home_feed.local.jsonc",
            "\"exp_mmr_diversity\":         { \"layer\": \"rerank\" }",
            "\"exp_mmr_diversity\":         { \"layer\": \"rerank\", \"yields_to\": [\"exp_freshness_calibration\"] }")).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var warning = Assert.Single(compilation.Diagnostics, d => d.Code == "INTERACTION_REVIEW_UNUSED");
        Assert.Equal("/reviewed_interactions/exp_freshness_calibration/0", warning.Pointer);
        Assert.Contains("'exp_mmr_diversity' yields to 'exp_freshness_calibration'", warning.Hint, StringComparison.Ordinal);
    }

    private static ExperimentPlacement Fusion(string id, string multiplier, string value) =>
        CombinationLimitTests.Treatment(id, $$"""{ "nodes": { "fusion": { "config": { "multipliers": { "{{multiplier}}": "{{value}}" } } } } }""");

    private static ExperimentPlacement Yields(ExperimentPlacement placement, params string[] to) => placement with { YieldsTo = to };

    private static JsonObject Arm(string patch) => new() { ["t"] = JsonNode.Parse(patch) };

    private static PlanCompilation Compile(params ExperimentPlacement[] placements)
    {
        var host = HomeFeedTestHost.Load();
        return PlanCompiler.Compile(CombinationLimitTests.With(host.Request, placements), host.Environment);
    }

    private static string Scores(PipelineResult result) =>
        string.Join(";", result.Items.Select(i => $"{i.Rank}:{i.Id}:{string.Join(",", i.Columns.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"))}"));
}
