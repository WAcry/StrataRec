using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The compile-time bounds on arm combinations (design 11.5, 12.3): a space too large to compile or check is a located
/// error, never an exception and never a plan that serves unchecked combinations.
/// </summary>
public sealed class CombinationLimitTests
{
    [Fact]
    public void ANodeSpaceFarBeyondTheLimit_IsReportedAtTheNode_NotAnArithmeticOverflow()
    {
        // Review 18 #5: ten layers of 100 arms each modify fusion, 101^10 combinations at the default limit of 1024.
        var host = HomeFeedTestHost.Load();
        var placements = new List<ExperimentPlacement>();
        for (var layer = 0; layer < 10; layer++)
        {
            var arms = new JsonObject();
            for (var arm = 0; arm < 100; arm++)
            {
                arms["a" + arm] = JsonNode.Parse($$"""{ "nodes": { "fusion": { "config": { "multipliers": { "x{{layer}}": "1.{{arm}}" } } } } }""");
            }

            placements.Add(new ExperimentPlacement(Experiment("e" + layer, arms), "layer" + layer));
        }

        var request = With(host.Request, placements);

        var compilation = PlanCompiler.Compile(request, host.Environment);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.Code == "NODE_TOO_MANY_COMBINATIONS");
        Assert.Equal("/nodes/fusion", error.Pointer);
        Assert.Contains("1024", error.Message);
        Assert.NotNull(error.Hint);
    }

    internal static ExperimentArtifact Experiment(string id, JsonObject arms) => ArtifactSerializer.Deserialize<ExperimentArtifact>(new JsonObject
    {
        ["experiment_id"] = id,
        ["pipeline_id"] = "home_feed",
        ["owner"] = "t",
        ["description"] = "d",
        ["expires_at"] = "2099-01-01",
        ["arms"] = arms,
    }.ToJsonString());

    /// <summary>The sample's request with <paramref name="placements"/> instead of its experiments.</summary>
    private static PlanCompileRequest With(PlanCompileRequest r, IReadOnlyList<ExperimentPlacement> placements, int? maxCombinationsChecked = null) => new()
    {
        Pipeline = r.Pipeline,
        Catalog = r.Catalog,
        Bindings = r.Bindings,
        Tables = r.Tables,
        Policy = r.Policy,
        Experiments = placements,
        ManifestId = r.ManifestId,
        ValidateArmsIndependently = false, // a thousand arms: the bound under test is the node's, not each arm's compile
        MaxCombinationsChecked = maxCombinationsChecked ?? r.MaxCombinationsChecked,
    };
}
