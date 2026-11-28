using System.Text.Json;
using StrataRec.Hosting.Plans;

namespace StrataRec.IntegrationTests;

public sealed class EffectivePlanDocumentTests
{
    [Fact]
    public void EditingADocument_ChangesNeitherThePlansDefinitions_NorTheNextDocument()
    {
        var plan = HomeFeedTestHost.Load().Plan();
        var effective = EffectivePlan.Create(plan, plan.ArmsFromIds(["exp_mmr_diversity/mmr"]));
        var mmr = effective.Nodes.Single(n => n.Id == "mmr_diversifier").Node;
        var definition = mmr.Definition.GetRawText();
        var expected = effective.ToDocument().ToJsonString();

        var document = effective.ToDocument();
        document["nodes"]!["mmr_diversifier"]!["config"]!["window"] = 99;
        document["nodes"]!.AsObject().Remove("recall");

        Assert.Equal(expected, effective.ToDocument().ToJsonString());
        Assert.Equal(definition, mmr.Definition.GetRawText());
        Assert.Equal(20, mmr.Definition.GetProperty("config").GetProperty("window").GetInt32());
        Assert.Equal(JsonValueKind.Object, plan.Nodes.Single(n => n.NodeId == "recall").Definition.ValueKind);
    }
}
