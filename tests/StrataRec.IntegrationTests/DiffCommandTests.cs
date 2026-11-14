using System.Text.Json.Nodes;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>stratarec diff</c> (design 13.4 item 3) on the home_feed sample and modified copies of it: what differs per arm
/// combination, from the compiled plans, with RFC 6901 pointers into the combination's plan.
/// </summary>
public sealed class DiffCommandTests
{
    private const string Mmr = "exp_mmr_diversity/mmr";

    /// <summary>The baseline and each of the sample's six arms alone.</summary>
    private static readonly string[] SampleCombinations =
    {
        "baseline", "exp_freshness_calibration/calibrated", "exp_freshness_calibration/control", "exp_mmr_diversity/control", Mmr,
        "exp_quality_weight/control", "exp_quality_weight/strong",
    };

    [Fact]
    public async Task IdenticalConfigurations_ExitWith0_AndReportNothing()
    {
        var copy = HomeFeedTestHost.LoadModified(_ => { }).Root;

        var (exit, report) = await Diff(HomeFeedTestHost.SampleRoot(), copy);

        Assert.Equal(0, exit);
        Assert.False(report["differences"]!.GetValue<bool>());
        Assert.Empty(report["structure"]!.AsArray());
        Assert.Empty(report["combinations"]!.AsArray());
        Assert.Equal(SampleCombinations, Strings(report["compared"]));

        var text = new StringWriter();
        Assert.Equal(0, await DiffCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), copy, null, "local", null, Array.Empty<string>(), "text", text, CancellationToken.None));
        Assert.Contains("no differences in 7 combination(s)", text.ToString());
    }

    [Fact]
    public async Task AnArmsPatch_ChangingOneConfigLeaf_DiffersOnlyInThatArmsCombination_AtTheLeafsPointer()
    {
        var after = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "experiments/exp_mmr_diversity.jsonc", "\"window\": 20", "\"window\": 25")).Root;

        var (exit, report) = await Diff(HomeFeedTestHost.SampleRoot(), after);

        Assert.Equal(1, exit);
        Assert.Empty(report["structure"]!.AsArray());
        var combination = Assert.Single(report["combinations"]!.AsArray())!;
        Assert.Equal(Mmr, combination["combination"]!.GetValue<string>());
        Assert.Equal(new[] { Mmr }, Strings(combination["arms"]));
        var change = Assert.Single(combination["changes"]!.AsArray())!;
        Assert.Equal(("/nodes/mmr_diversifier/config/window", "changed", 20, 25),
            (change["pointer"]!.GetValue<string>(), change["change"]!.GetValue<string>(), change["from"]!.GetValue<int>(), change["to"]!.GetValue<int>()));

        var text = await DiffText(HomeFeedTestHost.SampleRoot(), after);
        Assert.Contains($"{Mmr}:\n  ~ node mmr_diversifier\n      ~ /config/window: 20 -> 25\n", text);
    }

    [Fact]
    public async Task ABaselineParameterChange_DiffersInEveryCombination()
    {
        var after = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"coarse_k\": 30,", "\"coarse_k\": 35,")).Root;

        var (exit, report) = await Diff(HomeFeedTestHost.SampleRoot(), after);

        Assert.Equal(1, exit);
        var combinations = report["combinations"]!.AsArray();
        Assert.Equal(SampleCombinations, combinations.Select(c => c!["combination"]!.GetValue<string>()));
        Assert.All(combinations, c =>
        {
            var change = Assert.Single(c!["changes"]!.AsArray())!;
            Assert.Equal(("/params/coarse_k", 30, 35), (change["pointer"]!.GetValue<string>(), change["from"]!.GetValue<int>(), change["to"]!.GetValue<int>()));
        });
        Assert.Contains("  ~ param coarse_k: 30 -> 35\n", await DiffText(HomeFeedTestHost.SampleRoot(), after));
    }

    [Fact]
    public async Task ANodeTheNewSideAdds_IsOneAddedEntry_WithItsMergedDefinition()
    {
        var after = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "    \"coarse_score\": {",
            """
                "report_filter": { "order": 250, "op": "ExpressionFilter@1", "config": { "predicate": "col.item.report_rate < 0.04" } },
                "coarse_score": {
            """)).Root;

        var (_, report) = await Diff(HomeFeedTestHost.SampleRoot(), after, "--arms", "exp_quality_weight/strong");

        Assert.Equal(new[] { "exp_quality_weight/strong" }, Strings(report["compared"])); // --arms: exactly that combination
        var change = Assert.Single(Assert.Single(report["combinations"]!.AsArray())!["changes"]!.AsArray())!;
        Assert.Equal(("/nodes/report_filter", "added"), (change["pointer"]!.GetValue<string>(), change["change"]!.GetValue<string>()));
        Assert.Null(change["from"]);
        Assert.Equal("""{"config":{"predicate":"col.item.report_rate < 0.04"},"op":"ExpressionFilter@1","order":250}""",
            StrataRec.Artifacts.Json.JsonCanonicalizer.CanonicalizeToString(change["to"]));
    }

    [Fact]
    public async Task AnAddedArm_AndAnExperimentInAnotherLayer_AreStructuralChanges_AndTheArmIsNotComparedWithAnInventedPlan()
    {
        var after = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "experiments/exp_mmr_diversity.jsonc", "\"control\": {},", "\"control\": {}, \"window_25\": { \"nodes\": { \"diversity_cap\": { \"config\": { \"window\": 25 } } } },");
            ReplayTests.Edit(root, "releases/home_feed.local.jsonc", "\"exp_quality_weight\":        { \"layer\": \"params\" }", "\"exp_quality_weight\": { \"layer\": \"fusion\" }");
        }).Root;

        var (exit, report) = await Diff(HomeFeedTestHost.SampleRoot(), after);

        Assert.Equal(1, exit);
        Assert.Equal(
            new[]
            {
                ("layer", "params", "removed", (string?)null, (string?)null),
                ("experiment", "exp_quality_weight", "moved", "params", "fusion"),
                ("arm", "exp_mmr_diversity/window_25", "added", null, "rerank"),
            },
            report["structure"]!.AsArray().Select(s => (s!["kind"]!.GetValue<string>(), s["id"]!.GetValue<string>(), s["change"]!.GetValue<string>(),
                s["from_layer"]?.GetValue<string>(), s["to_layer"]?.GetValue<string>())));
        var added = Assert.Single(report["combinations"]!.AsArray())!;
        Assert.Equal(("exp_mmr_diversity/window_25", "from", 0), (added["combination"]!.GetValue<string>(), added["missing_on"]!.GetValue<string>(), added["changes"]!.AsArray().Count));
        Assert.Contains("does not exist", added["reason"]!.GetValue<string>());
        Assert.Contains("exp_mmr_diversity/window_25", Strings(report["compared"]));

        var text = await DiffText(HomeFeedTestHost.SampleRoot(), after);
        Assert.Contains("  ~ experiment exp_quality_weight: layer params -> fusion\n", text);
        Assert.Contains("  + arm exp_mmr_diversity/window_25 (layer rerank)\n", text);
        Assert.Contains("exp_mmr_diversity/window_25: exists only in the to configuration (arm 'exp_mmr_diversity/window_25' does not exist in the from configuration); not compared", text);
    }

    [Fact]
    public async Task TapAndFunnelGroupChanges_AreReportedPerLeaf_AsExplainPlanShowsThem()
    {
        var after = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"rows\": { \"scope\": \"dropped\", \"max_rows\": 5 }", "\"rows\": { \"scope\": \"dropped\", \"max_rows\": 10 }");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"surface\": \"ctx.request.surface\" }", "\"surface\": \"ctx.request.surface\", \"session\": \"ctx.request.session_id\" }");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"deep_trace\": { \"key\": [\"ctx.request.user_id\", \"ctx.request.session_id\"], \"rate\": 1.0 }",
                "\"deep_trace\": { \"key\": [\"ctx.request.user_id\", \"ctx.request.session_id\"], \"rate\": 0.5 }");
        }).Root;

        var (_, report) = await Diff(HomeFeedTestHost.SampleRoot(), after, "--arms", "exp_freshness_calibration/calibrated");

        var changes = Assert.Single(report["combinations"]!.AsArray())!["changes"]!.AsArray();
        Assert.Equal(
            new[]
            {
                ("/funnel_groups/deep_trace/rate", "changed", "1", "0.5"),
                ("/taps/coarse_dropped/rows/max_rows", "changed", "5", "10"),
                ("/taps/served/context/session", "added", "-", "\"ctx.request.session_id\""),
            },
            changes.Select(c => (c!["pointer"]!.GetValue<string>(), c["change"]!.GetValue<string>(), c["from"]?.ToJsonString() ?? "-", c["to"]!.ToJsonString())));

        // explain-plan --arms shows the same members: a pointer of the diff reads the new value there.
        var explained = new StringWriter();
        Assert.Equal(0, await ExplainPlanCommand.RunAsync(Tool.Environment, after, "local", null, "json", new[] { "exp_freshness_calibration/calibrated" }, explained, CancellationToken.None));
        var plan = JsonNode.Parse(explained.ToString())!;
        Assert.Equal(10, plan["taps"]!["coarse_dropped"]!["rows"]!["max_rows"]!.GetValue<int>());
        Assert.Equal("ctx.request.session_id", plan["taps"]!["served"]!["context"]!["session"]!.GetValue<string>());
        Assert.Equal(0.5, plan["funnel_groups"]!["deep_trace"]!["rate"]!.GetValue<double>());
    }

    [Fact]
    public async Task WithoutFrom_AManifestIsComparedWithThePreviousManifestItNames()
    {
        var store = ManifestStores.NewStore();
        var (before, _) = await ManifestStores.BuildAsync(ManifestStores.Workspace(), store);
        var (_, file) = await ManifestStores.BuildAsync(
            ManifestStores.Workspace(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"final_top_k\": 10,", "\"final_top_k\": 12,")), store);
        var release = ManifestStores.WithPrevious(ManifestStores.Read(file), before);
        var after = await ManifestStores.PutAsync(store, release);
        var afterFile = Path.Combine(store, "release.json");
        File.WriteAllText(afterFile, StrataRec.Artifacts.Model.ArtifactSerializer.ToNode(release).ToJsonString());

        foreach (var to in new[] { after, afterFile })
        {
            var (exit, report) = await Diff(null, to, "--store", store);

            Assert.Equal(1, exit);
            Assert.Equal((before, to), (report["from"]!["ref"]!.GetValue<string>(), report["to"]!["ref"]!.GetValue<string>()));
            var baseline = report["combinations"]![0]!;
            Assert.Equal("baseline", baseline["combination"]!.GetValue<string>());
            var change = Assert.Single(baseline["changes"]!.AsArray())!;
            Assert.Equal(("/params/final_top_k", 10, 12), (change["pointer"]!.GetValue<string>(), change["from"]!.GetValue<int>(), change["to"]!.GetValue<int>()));
        }

        // A manifest without previous, and a workspace (which has none), need --from.
        var (_, withoutPrevious) = await ManifestStores.BuildAsync(ManifestStores.Workspace(), store);
        await ExpectError(withoutPrevious, store, "MANIFEST_NO_PREVIOUS", "/previous", "pass --from");
        await ExpectError(HomeFeedTestHost.SampleRoot(), null, "FROM_REQUIRED", string.Empty, "pass --from");
    }

    [Fact]
    public async Task TheJsonReport_HasAStableShape()
    {
        var after = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"coarse_k\": 30,", "\"coarse_k\": 35,");
            ReplayTests.Edit(root, "experiments/exp_mmr_diversity.jsonc", "\"control\": {},", "\"control\": {}, \"other\": {},");
        }).Root;

        var (_, report) = await Diff(HomeFeedTestHost.SampleRoot(), after);

        Assert.Equal(new[] { "from", "to", "differences", "structure", "compared", "combinations" }, report.AsObject().Select(p => p.Key));
        Assert.Equal(new[] { "ref", "pipeline" }, report["from"]!.AsObject().Select(p => p.Key));
        Assert.Equal(new[] { "kind", "id", "change", "from_layer", "to_layer" }, report["structure"]![0]!.AsObject().Select(p => p.Key));
        var combinations = report["combinations"]!.AsArray();
        Assert.Equal(new[] { "combination", "arms", "changes" }, combinations[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(new[] { "pointer", "change", "from", "to" }, combinations[0]!["changes"]![0]!.AsObject().Select(p => p.Key));
        var missing = combinations.Single(c => c!["missing_on"] is not null)!;
        Assert.Equal(new[] { "combination", "arms", "missing_on", "reason", "changes" }, missing.AsObject().Select(p => p.Key));
        Assert.True(report["differences"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ArmsThatExistOnNeitherSide_AreAUsageError()
    {
        var errors = new StringWriter();

        Assert.Equal(2, await DiffCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), HomeFeedTestHost.SampleRoot(), null, "local", null, new[] { "exp_nope/a" }, "json",
            TextWriter.Null, CancellationToken.None, errors));
        Assert.Contains("arm 'exp_nope/a' does not exist in the from configuration", errors.ToString());

        errors = new StringWriter();
        Assert.Equal(2, await DiffCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), HomeFeedTestHost.SampleRoot(), null, "local", null, new[] { Mmr, "exp_mmr_diversity/control" },
            "json", TextWriter.Null, CancellationToken.None, errors));
        Assert.Contains("are both in layer 'rerank'", errors.ToString());
    }

    [Fact]
    public async Task ARefThatNamesNothing_OrAManifestWithoutAStore_IsAUsageError()
    {
        var store = ManifestStores.NewStore();
        var missing = Path.Combine(store, "no-such-workspace");
        var errors = new StringWriter();

        Assert.Equal(2, await DiffCommand.RunAsync(Tool.Environment, missing, HomeFeedTestHost.SampleRoot(), null, "local", null, Array.Empty<string>(), "json", TextWriter.Null,
            CancellationToken.None, errors));
        var error = Assert.Single(ManifestStores.Errors(errors.ToString()));
        Assert.Equal(("CONFIG_REF_UNKNOWN", "--from"), (error.Code, error.Artifact));
        Assert.Contains("a workspace directory, a manifest content id (sha256:<64 hex digits>) or a manifest file", error.Hint);

        errors = new StringWriter();
        Assert.Equal(2, await DiffCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), "sha256:" + new string('a', 64), null, "local", null, Array.Empty<string>(), "json",
            TextWriter.Null, CancellationToken.None, errors));
        error = Assert.Single(ManifestStores.Errors(errors.ToString()));
        Assert.Equal(("STORE_REQUIRED", "--to"), (error.Code, error.Artifact));
        Assert.Contains("pass --store <dir>", error.Hint);
    }

    [Fact]
    public async Task ASideThatDoesNotCompile_IsAnError_LabelledWithTheSide()
    {
        var broken = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"k_param\": \"coarse_k\"", "\"k_param\": \"coarse_kk\"")).Root;
        var errors = new StringWriter();

        Assert.Equal(2, await DiffCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), broken, null, "local", null, Array.Empty<string>(), "json", TextWriter.Null,
            CancellationToken.None, errors));

        var error = Assert.Single(ManifestStores.Errors(errors.ToString()));
        Assert.Equal(("PARAM_UNKNOWN", "--to pipeline:home_feed", "/nodes/coarse_top_k/config/k_param"), (error.Code, error.Artifact, error.Pointer));
    }

    private static async Task ExpectError(string to, string? store, string code, string pointer, string hint)
    {
        var errors = new StringWriter();
        Assert.Equal(2, await DiffCommand.RunAsync(Tool.Environment, null, to, store, "local", null, Array.Empty<string>(), "json", TextWriter.Null, CancellationToken.None, errors));
        var error = Assert.Single(ManifestStores.Errors(errors.ToString()));
        Assert.Equal((code, pointer), (error.Code, error.Pointer));
        Assert.Contains(hint, error.Hint);
    }

    /// <param name="options">--arms and --store, as name/value pairs.</param>
    private static async Task<(int Exit, JsonNode Report)> Diff(string? from, string to, params string[] options)
    {
        var arms = Array.Empty<string>();
        string? store = null;
        for (var i = 0; i < options.Length; i += 2)
        {
            if (options[i] == "--arms")
            {
                arms = new[] { options[i + 1] };
            }
            else
            {
                store = options[i + 1];
            }
        }

        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await DiffCommand.RunAsync(Tool.Environment, from, to, store, "local", null, arms, "json", output, CancellationToken.None, errors);
        Assert.True(exit is 0 or 1, errors.ToString());
        return (exit, JsonNode.Parse(output.ToString())!);
    }

    private static async Task<string> DiffText(string from, string to)
    {
        var output = new StringWriter();
        Assert.Equal(1, await DiffCommand.RunAsync(Tool.Environment, from, to, null, "local", null, Array.Empty<string>(), "text", output, CancellationToken.None));
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(s => s!.GetValue<string>()).ToArray();
}
