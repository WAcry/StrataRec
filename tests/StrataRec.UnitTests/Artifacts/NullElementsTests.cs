using System.Text.Json;
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
        // The document is walked, not the object: no code of the bound object runs, even with a null in the document.
        Assert.True(ArtifactSerializer.TryBind<WithComputed>(JsonNode.Parse("""{ "nickname": null }"""), "the config", out _, out var withNull), withNull?.Message);
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
    // Review 46: the position is where the document writes the null, not where the bound collection holds it.
    [InlineData("""{ "by_kind": { "One": [null] } }""", "/by_kind/One/0", "Item 0 of 'One' must be a string, got null.")]
    [InlineData("""{ "by_number": { "01": [null] } }""", "/by_number/01/0", "Item 0 of '01' must be a string, got null.")]
    [InlineData("""{ "by_date": { "2024-01-02T03:04:05.0000000": [null] } }""", "/by_date/2024-01-02T03:04:05.0000000/0", "Item 0 of '2024-01-02T03:04:05.0000000' must be a string, got null.")]
    [InlineData("""{ "set": ["a", "a", null] }""", "/set/2", "Item 2 of 'set' must be a string, got null.")]
    [InlineData("""{ "sorted": ["z", null, "a"] }""", "/sorted/1", "Item 1 of 'sorted' must be a string, got null.")]
    [InlineData("""{ "stack": ["a", null] }""", "/stack/1", "Item 1 of 'stack' must be a string, got null.")]
    [InlineData("""{ "by_tag": { "only:one": [null] } }""", "/by_tag/only:one/0", "Item 0 of 'only:one' must be a string, got null.")] // a key converter: not written as a value
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

        public string? Nickname { get; init; }

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

        public Dictionary<Kind, List<string>> ByKind { get; init; } = [];

        public Dictionary<int, List<string>> ByNumber { get; init; } = [];

        public SortedSet<string> Sorted { get; init; } = [];

        public Stack<string> Stack { get; init; } = [];

        public Dictionary<Tag, List<string>> ByTag { get; init; } = [];

        [JsonExtensionData]
        public Dictionary<string, object> Other { get; init; } = [];
    }

    public enum Kind
    {
        One = 1,
    }

    /// <summary>A key read and written only as a property name.</summary>
    [JsonConverter(typeof(TagConverter))]
    public readonly record struct Tag(string Value);

    public sealed class TagConverter : JsonConverter<Tag>
    {
        public override Tag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Tag value, JsonSerializerOptions options) => throw new NotSupportedException();

        public override Tag ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, Tag value, JsonSerializerOptions options) => writer.WritePropertyName(value.Value);
    }

    public record struct Pair(List<string> Items);
}
