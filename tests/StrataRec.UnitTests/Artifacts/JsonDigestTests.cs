using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class JsonDigestTests
{
    [Fact]
    public void EveryValueHasADigest_AlsoTheOnesWithoutAContentId()
    {
        JsonNode?[] without =
        [
            JsonValue.Create(9_007_199_254_740_993L), // 2^53 + 1: RFC 8785 cannot hold it exactly
            JsonValue.Create(double.NaN),
            JsonValue.Create(float.PositiveInfinity),
            JsonValue.Create("\ud800"),
            JsonNode.Parse("1e400"),
        ];

        foreach (var value in without)
        {
            Assert.Throws<JsonCanonicalizationException>(() => ContentHash.Compute(new JsonObject { ["v"] = value!.DeepClone() }));
            Assert.Equal(32, JsonDigest.Of(new JsonObject { ["v"] = value!.DeepClone() }).Length);
        }
    }

    [Fact]
    public void TheOrderOfMembers_DoesNotMatter_AndTheOrderOfItemsDoes()
    {
        var one = JsonNode.Parse("""{ "a": 1, "b": { "c": [1, 2], "d": null } }""");
        var other = JsonNode.Parse("""{ "b": { "d": null, "c": [1, 2] }, "a": 1 }""");
        var swapped = JsonNode.Parse("""{ "a": 1, "b": { "c": [2, 1], "d": null } }""");

        Assert.Equal(JsonDigest.Of(one), JsonDigest.Of(other));
        Assert.NotEqual(JsonDigest.Of(one), JsonDigest.Of(swapped));
    }

    [Fact]
    public void ValuesThatDiffer_HaveDifferentDigests()
    {
        string[] different =
        [
            "null", "true", "false", "0", "1", "\"1\"", "\"\"", "[]", "{}", "[null]", "[[]]", "{\"a\":null}", "{\"a\":{}}", "{\"a\":[]}", "[1,2]", "[12]", "[\"1\",\"2\"]",
            "[\"12\"]", "{\"a\":1,\"b\":2}", "{\"a\":{\"b\":2}}", "{\"ab\":1}", "{\"a\":\"b\"}", "9007199254740992", "9007199254740993",
        ];

        var digests = different.Select(text => JsonDigest.Of(JsonNode.Parse(text))).ToList();

        Assert.Equal(different.Length, digests.Distinct().Count());
        Assert.Equal(JsonDigest.Of(JsonValue.Create(7L)), JsonDigest.Of(JsonNode.Parse("7")));
    }
}
