using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A plan holds the catalog features it reads and what those are computed from or keyed by (design 6.3), whichever way it
/// reads them: a node, a <c>when</c>, a fallback, an arm's node, a tap, the output, another derived feature, the key of a
/// foreign-key entity. Each case reads a feature nothing else reads, checks the plan holds it (and not the user features
/// the sample declares and nothing reads), and that the value arrives where it is read.
/// </summary>
public sealed class PlanFeatureClosureTests
{
    private static readonly string[] SampleFeatures =
    [
        "author.followers", "author.log_followers", "item.age_hours", "item.author_id", "item.category", "item.clicks_7d", "item.embedding",
        "item.impressions_7d", "item.is_policy_safe", "item.report_rate", "item.smoothed_ctr",
    ];

    [Fact]
    public void TheSamplePlan_HoldsWhatItsNodesArmsAndOutputRead_NotTheUserFeaturesNothingReads()
    {
        var plan = HomeFeedTestHost.Load().Plan();

        Assert.Equal(SampleFeatures, Names(plan));
        Assert.Equal(["author_store", "item_store"], plan.Features.Groups.Select(g => g.SourceName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(plan.Schema.ContextFields, f => f.Kind == ContextFieldKind.Feature);

        // Features are registered in the order the compile first reads them: the same for the same configuration.
        var again = HomeFeedTestHost.Load().Plan();
        Assert.Equal(plan.Features.Features.Select(f => (f.Definition.Name, f.Slot)), again.Features.Features.Select(f => (f.Definition.Name, f.Slot)));
    }

    [Fact]
    public async Task AContextFeatureReadOnlyByAWhen_IsFetchedForIt()
    {
        var plan = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"order\": 515, \"op\": \"ExpressionFilter@1\", \"when\": \"ctx.request.surface == 'home'\"",
            "\"order\": 515, \"op\": \"ExpressionFilter@1\", \"when\": \"ctx.request.surface == 'home' && ctx.user.tenure_days > 100.0\"")).Plan();

        Assert.Equal([.. SampleFeatures, "user.tenure_days"], Names(plan));
        var tenure = Assert.Single(plan.ContextFeatures); // prefetched with the request
        Assert.Equal("user.tenure_days", plan.Features.Features[tenure].Definition.Name);

        // users.json: 1001 has 420 days, 1002 has 12.
        var veteran = await Run(plan, HomeFeedTestHost.NewRequest(userId: 1001));
        var newcomer = await Run(plan, HomeFeedTestHost.NewRequest(userId: 1002));
        Assert.Equal(NodeOutcomeKind.Succeeded, veteran.Outcomes.Single(o => o.NodeId == "author_cap").Kind);
        Assert.Equal(NodeOutcomeKind.SkippedWhen, newcomer.Outcomes.Single(o => o.NodeId == "author_cap").Kind);
    }

    [Fact]
    public async Task AFeatureReadOnlyByAFallback_IsFetchedWhenTheFallbackRuns()
    {
        var plan = HomeFeedTestHost.LoadModified(root =>
        {
            AddFeature(root, "item.backup_clicks", """{ "type": "float32", "source": "item_store", "field": "i_clk_7d", "default": 0.0 }""");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
                "\"config\": { \"output_col\": \"score.ctr\", \"expr\": \"col.item.smoothed_ctr\" }",
                "\"config\": { \"output_col\": \"score.ctr\", \"expr\": \"col.item.backup_clicks / 1000.0\" }");
            AddOutput(root, "ctr", "col.score.ctr");
            MetricsTests.FailModel(root);
        }).Plan();

        Assert.Contains("item.backup_clicks", Names(plan));
        Assert.DoesNotContain("user.tenure_days", Names(plan));

        var result = await Run(plan, HomeFeedTestHost.NewRequest());
        Assert.Equal(("ctr_backup", NodeOutcomeKind.Fallback), result.Outcomes.Single(o => o.NodeId == "ctr_ranker") is var o ? (o.ProducedBy, o.Kind) : default);
        var items = Fixture("items.json");
        Assert.All(result.Items, item =>
            Assert.Equal((float)(Number(items, item.Id, "i_clk_7d") / 1000.0), (float)item.Columns["ctr"].AsFloat64()));
    }

    [Fact]
    public async Task AFeatureReadOnlyByAnArmsNode_IsInThePlan_AndServedToTheArm()
    {
        // The sample's exp_freshness_calibration: only its arm's node (and that arm's tap) reads item.age_hours. The unified
        // plan compiles every arm's variants, so it holds it; the baseline alone does not (nor mmr's item.embedding).
        var host = HomeFeedTestHost.Load();
        var plan = host.Plan();
        Assert.Contains("item.age_hours", Names(plan));
        var baseline = host.CompileMerged([]);
        Assert.True(baseline.Succeeded, string.Join("\n", baseline.Diagnostics));
        Assert.Equal(SampleFeatures.Except(["item.age_hours", "item.embedding"]), Names(baseline.Plan!));

        var control = await Run(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(["exp_freshness_calibration/control"])));
        var calibrated = await Run(plan, HomeFeedTestHost.NewRequest(arms: plan.ArmsFromIds(["exp_freshness_calibration/calibrated"]), captureLogs: true));

        var items = Fixture("items.json");
        var before = control.Items.ToDictionary(i => i.Id, i => i.Columns["score"].AsFloat64());
        var compared = 0;
        foreach (var item in calibrated.Items.Where(i => before.ContainsKey(i.Id)))
        {
            var age = items[item.Id.ToString()]!["i_age_h"]?.GetValue<double>() ?? 240.0;
            Assert.Equal((float)(before[item.Id] * (1.0 + (0.5 * Math.Exp(-age / 48.0)))), (float)item.Columns["score"].AsFloat64(), 5);
            compared++;
        }

        Assert.True(compared > 0);

        // The arm's tap logs what the arm's node read.
        var served = Assert.Single(calibrated.LogBatches, b => b.TapId == "served");
        var logged = served.Columns.Single(c => c.Name == "age_hours");
        for (var row = 0; row < served.RowCount; row++)
        {
            var expected = items[served.Int64EntityIds![row].ToString(System.Globalization.CultureInfo.InvariantCulture)]!["i_age_h"];
            Assert.Equal(expected is null, logged.Values[row].IsMissing);
        }
    }

    [Fact]
    public async Task FeaturesReadOnlyByTheOutput_OneDerivedFromAnother_AreFetchedForTheReturnedRows()
    {
        var plan = HomeFeedTestHost.LoadModified(root =>
        {
            AddFeature(root, "item.out_clicks", """{ "type": "float32", "source": "item_store", "field": "i_clk_7d", "default": 0.0 }""");
            AddFeature(root, "item.chain_clicks", """{ "type": "float32", "source": "item_store", "field": "i_clk_7d", "default": 0.0 }""");
            AddFeature(root, "item.chain_double", """{ "type": "float32", "expr": "col.item.chain_clicks * 2.0" }""", "derived_features");
            AddFeature(root, "item.chain_plus", """{ "type": "float32", "expr": "col.item.chain_double + 1.0" }""", "derived_features");
            AddOutput(root, "clicks", "col.item.out_clicks");
            AddOutput(root, "chain", "col.item.chain_plus");
        }).Plan();

        Assert.Equal(SampleFeatures.Concat(["item.chain_clicks", "item.chain_double", "item.chain_plus", "item.out_clicks"]).Order(StringComparer.Ordinal), Names(plan));

        var result = await Run(plan, HomeFeedTestHost.NewRequest());
        var items = Fixture("items.json");
        Assert.All(result.Items, item =>
        {
            var clicks = Number(items, item.Id, "i_clk_7d");
            Assert.Equal(clicks, item.Columns["clicks"].AsFloat64());
            Assert.Equal((clicks * 2.0) + 1.0, item.Columns["chain"].AsFloat64());
        });
    }

    [Fact]
    public async Task AFeatureReadOnlyAsTheKeyOfAForeignKeyEntity_IsFetchedBeforeTheEntitysFeatures()
    {
        // Without author_rank and author_cap no node reads item.author_id: only author's features need it, as their key.
        var plan = HomeFeedTestHost.LoadModified(root =>
        {
            EditJson(root, "pipelines/home_feed.jsonc", pipeline =>
            {
                var nodes = pipeline["nodes"]!.AsObject();
                nodes.Remove("author_rank");
                nodes.Remove("author_cap");
            });
            AddOutput(root, "followers", "col.author.followers");
        }).Plan();

        Assert.Equal(SampleFeatures, Names(plan));
        Assert.DoesNotContain(plan.Nodes, n => n.ReadFeatures.Any(f => plan.Features.Features[f].Definition.Name == "item.author_id"));

        var result = await Run(plan, HomeFeedTestHost.NewRequest());
        var items = Fixture("items.json");
        var followers = File.ReadAllLines(Path.Combine(HomeFeedTestHost.SampleRoot(), "fixtures", "author_snapshot", "part-0000.jsonl"))
            .Select(line => JsonNode.Parse(line)!)
            .ToDictionary(r => r["key"]!.GetValue<long>(), r => r["fields"]!["a_followers"]!.GetValue<double>());
        Assert.All(result.Items, item =>
        {
            var author = items[item.Id.ToString()]!["i_author_id"]?.GetValue<long>();
            Assert.Equal(author is { } a ? followers.GetValueOrDefault(a) : 0.0, item.Columns["followers"].AsFloat64()); // default 0.0
        });
    }

    [Fact]
    public void ATapReadingAFeatureNoNodeReads_IsToldThatTapsNeverFetch()
    {
        var compilation = HomeFeedTestHost.LoadModified(root =>
        {
            AddFeature(root, "item.out_clicks", """{ "type": "float32", "source": "item_store", "field": "i_clk_7d", "default": 0.0 }""");
            EditJson(root, "pipelines/home_feed.jsonc", pipeline => pipeline["log_taps"]!["probe"] = JsonNode.Parse("""
                { "group": "all", "attach": { "after": "coarse_score" }, "columns": { "clicks": "col.item.out_clicks" } }
                """));
        }).Compile();

        // The tap's reference resolves to the catalog feature: the error is the tap's rule, not an unknown name.
        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal("TAP_FEATURE_NOT_FETCHED", error.Code);
        Assert.Contains("item.out_clicks", error.Message);
    }

    private static List<string> Names(ExecutionPlan plan) => plan.Features.Features.Select(f => f.Definition.Name).Order(StringComparer.Ordinal).ToList();

    private static async Task<PipelineResult> Run(ExecutionPlan plan, PipelineRequest request)
    {
        var result = await new PipelineExecutor().ExecuteAsync(plan, request, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotEmpty(result.Items);
        return result;
    }

    private static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(HomeFeedTestHost.SampleRoot(), "fixtures", name)))!.AsObject();

    /// <summary>A numeric field of an item in <c>items.json</c>, 0 where the item has none (the features' default).</summary>
    private static double Number(JsonObject items, Core.Keys.EntityKey id, string field) => items[id.ToString()]![field]?.GetValue<double>() ?? 0.0;

    private static void AddFeature(string root, string name, string definition, string section = "features") =>
        EditJson(root, "catalog/home_feed.jsonc", catalog => catalog[section]![name] = JsonNode.Parse(definition));

    private static void AddOutput(string root, string name, string expression) =>
        EditJson(root, "pipelines/home_feed.jsonc", pipeline => pipeline["output"]!["columns"]![name] = expression);

    /// <summary>Rewrites a JSONC file of the workspace as JSON (its comments are dropped) after <paramref name="edit"/>.</summary>
    internal static void EditJson(string root, string file, Action<JsonObject> edit)
    {
        var path = Path.Combine(root, file);
        var document = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!.AsObject();
        edit(document);
        File.WriteAllText(path, document.ToJsonString());
    }
}
