using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// A null element where the model says a value is required is a binding error at the element's own pointer, for any
/// config an extension binds; what the document did not write (a computed property) is not walked (review 45).
/// </summary>
public sealed class NullElementsTests
{
    [Fact]
    public void AComputedProperty_IsNotRead()
    {
        // One returned the config itself: the walk recursed until the stack overflowed; one that throws threw.
        Assert.True(ArtifactSerializer.TryBind<WithComputed>(JsonNode.Parse("{}"), "the config", out var config, out var error), error?.Message);
        Assert.Equal("name", config.Name);
    }

    [Theory]
    // A map at the root (a patch's section): its objects are walked; its own null entries may be deletions.
    [InlineData("""{ "a": { "key": [null] } }""", "/a/key/0", "Item 0 of 'key' must be a string, got null.")]
    [InlineData("""{ "a": null }""", null, null)]
    public void AMapAtTheRoot_HasItsObjectsWalked(string json, string? pointer, string? message) =>
        AssertBinds<Dictionary<string, Group>>(json, pointer, message);

    [Theory]
    [InlineData("""{ "set": ["a", null] }""", "/set/1", "Item 1 of 'set' must be a string, got null.")]
    [InlineData("""{ "pair": { "items": [null] } }""", "/pair/items/0", "Item 0 of 'items' must be a string, got null.")]
    [InlineData("""{ "by_date": { "2024-01-02T03:04:05": [null] } }""", "/by_date/2024-01-02T03:04:05/0", "Item 0 of '2024-01-02T03:04:05' must be a string, got null.")]
    [InlineData("""{ "other": null }""", "/other", "'other' must be a JSON value, got null.")] // extension data: at the object's level, not under the member's name
    public void TheErrorIsAtTheElementAsTheDocumentSpellsIt(string json, string pointer, string message) =>
        AssertBinds<Collections>(json, pointer, message);

    private static void AssertBinds<T>(string json, string? pointer, string? message)
        where T : class
    {
        var bound = ArtifactSerializer.TryBind<T>(JsonNode.Parse(json), "the config", out _, out var error);

        Assert.Equal(pointer is null, bound);
        Assert.Equal((pointer, message), (error?.Pointer, error?.Message));
        if (pointer is not null)
        {
            Assert.True(JsonPointer.Parse(pointer).TryEvaluate(JsonNode.Parse(json), out _), $"{pointer} is in the document");
        }
    }

    public sealed class WithComputed
    {
        public string Name { get; init; } = "name";

        public WithComputed Self => this;

        public string[] Throws => throw new InvalidOperationException("read");
    }

    public sealed class Group
    {
        public required List<string> Key { get; init; }
    }

    public sealed class Collections
    {
        public HashSet<string> Set { get; init; } = [];

        public Pair Pair { get; init; }

        public Dictionary<DateTime, List<string>> ByDate { get; init; } = [];

        [JsonExtensionData]
        public Dictionary<string, object> Other { get; init; } = [];
    }

    public record struct Pair(List<string> Items);
}
