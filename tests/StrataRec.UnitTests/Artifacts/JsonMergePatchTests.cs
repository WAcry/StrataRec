using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class JsonMergePatchTests
{
    // RFC 7396 Appendix A, every example.
    [Theory]
    [InlineData("""{"a":"b"}""", """{"a":"c"}""", """{"a":"c"}""")]
    [InlineData("""{"a":"b"}""", """{"b":"c"}""", """{"a":"b","b":"c"}""")]
    [InlineData("""{"a":"b"}""", """{"a":null}""", """{}""")]
    [InlineData("""{"a":"b","b":"c"}""", """{"a":null}""", """{"b":"c"}""")]
    [InlineData("""{"a":["b"]}""", """{"a":"c"}""", """{"a":"c"}""")]
    [InlineData("""{"a":"c"}""", """{"a":["b"]}""", """{"a":["b"]}""")]
    [InlineData("""{"a":{"b":"c"}}""", """{"a":{"b":"d","c":null}}""", """{"a":{"b":"d"}}""")]
    [InlineData("""{"a":[{"b":"c"}]}""", """{"a":[1]}""", """{"a":[1]}""")]
    [InlineData("""["a","b"]""", """["c","d"]""", """["c","d"]""")]
    [InlineData("""{"a":"b"}""", """["c"]""", """["c"]""")]
    [InlineData("""{"a":"foo"}""", "null", "null")]
    [InlineData("""{"a":"foo"}""", "\"bar\"", "\"bar\"")]
    [InlineData("""{"e":null}""", """{"a":1}""", """{"e":null,"a":1}""")]
    [InlineData("""[1,2]""", """{"a":"b","c":null}""", """{"a":"b"}""")]
    [InlineData("""{}""", """{"a":{"bb":{"ccc":null}}}""", """{"a":{"bb":{}}}""")]
    public void Apply_Rfc7396AppendixA(string target, string patch, string expected)
    {
        var result = JsonMergePatch.Apply(Jsonc.Parse(target), Jsonc.Parse(patch));

        Assert.Equal(Canonical(Jsonc.Parse(expected)), Canonical(result));
    }

    [Fact]
    public void Apply_Rfc7396Section3Example()
    {
        var target = Jsonc.Parse("""
            {
              "title": "Goodbye!",
              "author" : {
                "givenName" : "John",
                "familyName" : "Doe"
              },
              "tags":[ "example", "sample" ],
              "content": "This will be unchanged"
            }
            """);
        var patch = Jsonc.Parse("""
            {
              "title": "Hello!",
              "phoneNumber": "+01-123-456-7890",
              "author": {
                "familyName": null
              },
              "tags": [ "example" ]
            }
            """);

        var result = JsonMergePatch.Apply(target, patch);

        var expected = Jsonc.Parse("""
            {
              "title": "Hello!",
              "author" : {
                "givenName" : "John"
              },
              "tags": [ "example" ],
              "content": "This will be unchanged",
              "phoneNumber": "+01-123-456-7890"
            }
            """);
        Assert.Equal(Canonical(expected), Canonical(result));
        Assert.Equal(new[] { "title", "author", "tags", "content", "phoneNumber" }, result!.AsObject().Select(m => m.Key));
    }

    [Fact]
    public void Apply_KeepsTargetOrderAndAppendsNewMembersInPatchOrder()
    {
        var result = JsonMergePatch.Apply(Jsonc.Parse("""{"b":1,"a":2}"""), Jsonc.Parse("""{"d":4,"a":3,"c":5}"""));

        Assert.Equal("""{"b":1,"a":3,"d":4,"c":5}""", result!.ToJsonString());
    }

    [Fact]
    public void Apply_DoesNotMutateInputsAndSharesNoNodes()
    {
        var target = Jsonc.Parse("""{"keep":{"x":[1,2]},"change":{"y":1},"drop":true}""")!;
        var patch = Jsonc.Parse("""{"change":{"y":2,"z":{"w":1}},"drop":null,"new":[3]}""")!;
        var targetBefore = target.ToJsonString();
        var patchBefore = patch.ToJsonString();

        var result = JsonMergePatch.Apply(target, patch)!;

        Assert.Equal(targetBefore, target.ToJsonString());
        Assert.Equal(patchBefore, patch.ToJsonString());
        Assert.NotSame(target["keep"], result["keep"]);
        Assert.NotSame(patch["new"], result["new"]);
        Assert.NotSame(patch["change"]!["z"], result["change"]!["z"]);

        result["keep"]!["x"]!.AsArray().Add(99);
        result["new"]!.AsArray().Add(99);
        result["change"]!["z"]!["w"] = 2;

        Assert.Equal(targetBefore, target.ToJsonString());
        Assert.Equal(patchBefore, patch.ToJsonString());
    }

    [Fact]
    public void Apply_ComparesMemberNamesExactly()
    {
        var result = JsonMergePatch.Apply(Jsonc.Parse("""{"a":1}"""), Jsonc.Parse("""{"A":null,"B":2}"""));

        Assert.Equal("""{"a":1,"B":2}""", result!.ToJsonString());
    }

    [Fact]
    public void Apply_IgnoresCaseInsensitiveNodeOptions()
    {
        var options = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
        var target = new JsonObject(options) { ["Mode"] = "a" };
        var patch = new JsonObject(options) { ["mode"] = "b" };

        var result = JsonMergePatch.Apply(target, patch);

        Assert.Equal("""{"Mode":"a","mode":"b"}""", result!.ToJsonString());
    }

    [Fact]
    public void Apply_NullTarget_IsTreatedAsNonObject()
    {
        var result = JsonMergePatch.Apply(null, Jsonc.Parse("""{"a":{"b":null,"c":1}}"""));

        Assert.Equal("""{"a":{"c":1}}""", result!.ToJsonString());
    }

    [Fact]
    public void Apply_ScalarPatch_ReturnsDetachedCopy()
    {
        var patch = Jsonc.Parse("[1,2]")!;

        var result = JsonMergePatch.Apply(Jsonc.Parse("""{"a":1}"""), patch);

        Assert.NotSame(patch, result);
        Assert.Null(result!.Parent);
        Assert.Equal("[1,2]", result.ToJsonString());
    }

    [Fact]
    public void Apply_IsIdempotentForObjectPatches()
    {
        var target = Jsonc.Parse("""{"nodes":{"a":{"order":1},"b":{"order":2}}}""");
        var patch = Jsonc.Parse("""{"nodes":{"a":{"order":5},"b":null,"c":{"order":3}}}""");

        var once = JsonMergePatch.Apply(target, patch);
        var twice = JsonMergePatch.Apply(once, patch);

        Assert.Equal(Canonical(once), Canonical(twice));
    }

    private static string Canonical(JsonNode? node) => JsonCanonicalizer.CanonicalizeToString(node);
}
