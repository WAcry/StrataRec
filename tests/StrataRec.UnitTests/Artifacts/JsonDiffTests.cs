using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class JsonDiffTests
{
    [Theory]
    [InlineData("""{"a":1,"b":{"c":[1,2],"d":"x"}}""", """{"b":{"d":"x","c":[1,2]},"a":1}""")] // member order
    [InlineData("""{"a":1}""", """{"a":1.0}""")] // numbers by value
    [InlineData("""{"a":0.1}""", """{"a":1e-1}""")]
    [InlineData("null", "null")]
    [InlineData("[1,{\"a\":2}]", "[1,{\"a\":2.0}]")]
    public void EqualDocuments_HaveNoChanges(string before, string after)
    {
        Assert.Empty(JsonDiff.Compare(JsonNode.Parse(before), JsonNode.Parse(after)));
    }

    [Fact]
    public void ANestedLeaf_IsReportedAtItsOwnPointer_WithBothValues()
    {
        var changes = JsonDiff.Compare(
            JsonNode.Parse("""{"nodes":{"fusion":{"order":450,"config":{"k":10,"expr":"a"}}}}"""),
            JsonNode.Parse("""{"nodes":{"fusion":{"order":450,"config":{"k":12,"expr":"a"}}}}"""));

        var change = Assert.Single(changes);
        Assert.Equal(("/nodes/fusion/config/k", JsonChangeKind.Changed, "10", "12"), (change.Pointer, change.Kind, Json(change.Before), Json(change.After)));
    }

    [Fact]
    public void AMemberOnOneSide_IsOneChange_CarryingItsWholeValue()
    {
        var changes = JsonDiff.Compare(
            JsonNode.Parse("""{"nodes":{"a":{"op":"X"},"gone":{"op":"Y","config":{"k":1}}}}"""),
            JsonNode.Parse("""{"nodes":{"a":{"op":"X"},"new":{"op":"Z","config":{"k":2}}}}"""));

        Assert.Equal(
            new[]
            {
                ("/nodes/gone", JsonChangeKind.Removed, """{"op":"Y","config":{"k":1}}""", "-"),
                ("/nodes/new", JsonChangeKind.Added, "-", """{"op":"Z","config":{"k":2}}"""),
            },
            changes.Select(c => (c.Pointer, c.Kind, c.Before is null ? "-" : Json(c.Before), c.After is null ? "-" : Json(c.After))));
    }

    [Fact]
    public void Arrays_AreComparedAsWholeValues()
    {
        var changes = JsonDiff.Compare(JsonNode.Parse("""{"order":["a","b","c"]}"""), JsonNode.Parse("""{"order":["x","a","b","c"]}"""));

        var change = Assert.Single(changes);
        Assert.Equal(("/order", """["a","b","c"]""", """["x","a","b","c"]"""), (change.Pointer, Json(change.Before), Json(change.After)));
    }

    [Fact]
    public void ATypeChange_IsAChangeOfTheWholeValue()
    {
        var change = Assert.Single(JsonDiff.Compare(JsonNode.Parse("""{"a":{"b":1}}"""), JsonNode.Parse("""{"a":[1]}""")));

        Assert.Equal(("/a", JsonChangeKind.Changed, """{"b":1}""", "[1]"), (change.Pointer, change.Kind, Json(change.Before), Json(change.After)));
    }

    [Fact]
    public void JsonNull_IsAValue_DistinctFromAnAbsentMember()
    {
        Assert.Equal(
            new[] { ("/a", JsonChangeKind.Changed), ("/b", JsonChangeKind.Removed), ("/c", JsonChangeKind.Added) },
            JsonDiff.Compare(JsonNode.Parse("""{"a":null,"b":null}"""), JsonNode.Parse("""{"a":0,"c":null}""")).Select(c => (c.Pointer, c.Kind)));
    }

    [Fact]
    public void Changes_ComeInOrdinalPointerOrder_AndTokensAreEscaped()
    {
        var changes = JsonDiff.Compare(
            JsonNode.Parse("""{"z":1,"a/b":1,"m~n":{"y":1,"x":1}}"""),
            JsonNode.Parse("""{"m~n":{"x":2,"y":2},"z":2,"a/b":2}"""));

        Assert.Equal(new[] { "/a~1b", "/m~0n/x", "/m~0n/y", "/z" }, changes.Select(c => c.Pointer));
    }

    [Fact]
    public void ScalarRoots_AreComparedAtTheEmptyPointer()
    {
        var change = Assert.Single(JsonDiff.Compare(JsonValue.Create(0.3), JsonValue.Create(0.5)));

        Assert.Equal((string.Empty, JsonChangeKind.Changed), (change.Pointer, change.Kind));
    }

    [Fact]
    public void ReportedValues_AreDetachedCopies()
    {
        var before = JsonNode.Parse("""{"a":{"b":1}}""")!;
        var change = Assert.Single(JsonDiff.Compare(before, JsonNode.Parse("""{}""")));

        var container = new JsonObject { ["moved"] = change.Before }; // would throw if the value still had a parent
        Assert.Equal("""{"b":1}""", Json(container["moved"]));
        Assert.Equal("""{"a":{"b":1}}""", Json(before));
    }

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";
}
