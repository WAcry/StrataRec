using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// A null element where the model says a value is required is a binding error at the element's own pointer, for any
/// config an extension binds; what the document did not write (a computed property) is not walked, nor what the
/// serializer does not read.
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
    // The position is where the document writes the null, not where the bound collection holds it.
    [InlineData("""{ "by_kind": { "One": [null] } }""", "/by_kind/One/0", "Item 0 of 'One' must be a string, got null.")]
    [InlineData("""{ "by_number": { "01": [null] } }""", "/by_number/01/0", "Item 0 of '01' must be a string, got null.")]
    [InlineData("""{ "by_date": { "2024-01-02T03:04:05.0000000": [null] } }""", "/by_date/2024-01-02T03:04:05.0000000/0", "Item 0 of '2024-01-02T03:04:05.0000000' must be a string, got null.")]
    [InlineData("""{ "set": ["a", "a", null] }""", "/set/2", "Item 2 of 'set' must be a string, got null.")]
    [InlineData("""{ "sorted": ["z", null, "a"] }""", "/sorted/1", "Item 1 of 'sorted' must be a string, got null.")]
    [InlineData("""{ "stack": ["a", null] }""", "/stack/1", "Item 1 of 'stack' must be a string, got null.")]
    [InlineData("""{ "by_tag": { "only:one": [null] } }""", "/by_tag/only:one/0", "Item 0 of 'only:one' must be a string, got null.")] // a key converter: not written as a value
    public void TheErrorIsAtTheElementAsTheDocumentSpellsIt(string json, string pointer, string message) =>
        AssertBinds<Collections>(json, pointer, message);

    [Theory]
    // Only what the serializer reads is checked. An ignored member and a computed one are skipped (the
    // document's value is not read); one populated, or filled through a constructor parameter, is read.
    [InlineData("""{ "ignored": [null] }""", null, null)]
    [InlineData("""{ "computed": [null] }""", null, null)]
    [InlineData("""{ "populated": [null] }""", "/populated/0", "Item 0 of 'populated' must be a string, got null.")]
    // A present Nullable<T> is read as its T: its members and elements are checked; a null one is allowed.
    [InlineData("""{ "held": { "items": [null] } }""", "/held/items/0", "Item 0 of 'items' must be a string, got null.")]
    [InlineData("""{ "held": null }""", null, null)]
    [InlineData("""{ "helds": [{ "items": [null] }] }""", "/helds/0/items/0", "Item 0 of 'items' must be a string, got null.")]
    [InlineData("""{ "helds": [null] }""", null, null)]
    [InlineData("""{ "names": [null] }""", "/names/0", "Item 0 of 'names' must be a string, got null.")]
    [InlineData("""{ "counts": [1, null] }""", null, null)]
    public void WhatTheSerializerReads_IsChecked(string json, string? pointer, string? message) =>
        AssertBinds<Contracts>(json, pointer, message);

    [Theory]
    // A member filled through a constructor parameter follows the parameter's annotations: the document is read into it.
    [InlineData(typeof(FilteringConstructor), null)]
    [InlineData(typeof(PlainConstructor), "/items/0")]
    public void AConstructorParameter_IsTheContract(Type type, string? pointer) =>
        Assert.Equal(pointer, NullElements.Find(JsonDocument.Parse("""{ "items": [null] }""").RootElement, type, ArtifactSerializer.Options)?.Pointer);

    [Fact]
    public void APopulatePreference_IsReadOnlyWhereTheSerializerPopulates()
    {
        // A type's preference falls back to replacing where a member's value cannot be populated (an array,
        // an immutable or read-only collection, a struct); without a setter, the serializer then skips the value.
        AssertCheckedAsRead<PopulatedList>(read: true);
        AssertCheckedAsRead<PopulatedSet>(read: true);
        AssertCheckedAsRead<PopulatedArray>(read: false);
        AssertCheckedAsRead<PopulatedImmutableArray>(read: false);
        AssertCheckedAsRead<PopulatedReadOnlyList>(read: false);
        AssertCheckedAsRead<PopulatedEnumerable>(read: false);
        AssertCheckedAsRead<PopulatedImmutableList>(read: false);
        AssertCheckedAsRead<PopulatedQueue>(read: true);
        AssertCheckedAsRead<PopulatedBag>(read: false);

        // Filled, though the serializer cannot create one (no constructor without arguments).
        AssertCheckedAsRead<PopulatedListWithoutDefaultConstructor>(read: true);
        AssertCheckedAsRead<PopulatedCollectionWithoutDefaultConstructor>(read: true);
        AssertCheckedAsRead<PopulatedMapWithoutDefaultConstructor>(read: true, map: true);
        AssertCheckedAsRead<PopulatedReadOnlyMap>(read: false, map: true);
        Assert.Null(NullElements.Find(JsonDocument.Parse("""{ "held": { "items": [null] } }""").RootElement, typeof(PopulatedStruct), ArtifactSerializer.Options));
    }

    [Fact]
    public void AnIgnoredMember_IsNotRead_EvenThroughAConstructorParameter()
    {
        const string json = """{ "items": [null] }""";

        Assert.Empty(JsonSerializer.Deserialize<IgnoredWithParameter>(json, ArtifactSerializer.Options)!.Items);
        Assert.True(ArtifactSerializer.TryBind<IgnoredWithParameter>(JsonNode.Parse(json), "the config", out _, out var error), error?.Message);
    }

    [Fact]
    public void ExtensionDataTheSerializerDoesNotFill_IsNotChecked()
    {
        const string json = """{ "other": null }""";

        Assert.Empty(JsonSerializer.Deserialize<UnfilledExtension>(json, ArtifactSerializer.Options)!.Other);
        Assert.True(ArtifactSerializer.TryBind<UnfilledExtension>(JsonNode.Parse(json), "the config", out _, out var error), error?.Message);
    }

    /// <summary>The check reads a member as the serializer does: a null in what it reads is an error, in what it skips nothing.</summary>
    private static void AssertCheckedAsRead<T>(bool read, bool map = false)
        where T : class, IReadsItems
    {
        var (item, missing, pointer) = map ? ("""{ "k": "a" }""", """{ "k": null }""", "/items/k") : ("""["a"]""", "[null]", "/items/0");
        Assert.Equal(read, JsonSerializer.Deserialize<T>($$"""{ "items": {{item}} }""", ArtifactSerializer.Options)!.Seen.Contains("a"));
        var error = NullElements.Find(JsonDocument.Parse($$"""{ "items": {{missing}} }""").RootElement, typeof(T), ArtifactSerializer.Options);
        Assert.Equal(read ? pointer : null, error?.Pointer);
    }

    [Fact]
    public void ANullableStructAtTheRoot_IsReadAsTheStruct()
    {
        var error = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<Held?>("""{ "items": [null] }"""));

        Assert.Equal("/items/0", error.Pointer);
    }

    [Fact]
    public void AValueHoldingAList_IsCheckedAsTheListItWrites()
    {
        // The same JSON as { "set": ["a", null] }, built from a CLR array: one JsonValue, not a JsonArray.
        var node = new JsonObject { ["set"] = JsonValue.Create(new string?[] { "a", null }) };

        Assert.False(ArtifactSerializer.TryBind<Collections>(node, "the config", out _, out var error));
        Assert.Equal("/set/1", error.Pointer);
    }

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

    public readonly record struct Held(List<string> Items);

    public sealed class Contracts
    {
        [JsonIgnore]
        public List<string> Ignored { get; init; } = [];

        public List<string> Computed => ["x"];

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public List<string> Populated { get; } = [];

        public Held? Held { get; init; }

        public Held?[] Helds { get; init; } = [];

        public ImmutableArray<string>? Names { get; init; }

        public int?[] Counts { get; init; } = [];
    }

    public interface IReadsItems
    {
        IEnumerable<string> Seen { get; }
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedList : IReadsItems
    {
        public List<string> Items { get; } = [];

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedSet : IReadsItems
    {
        public ISet<string> Items { get; } = new HashSet<string>();

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedArray : IReadsItems
    {
        public string[] Items { get; } = [];

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedImmutableArray : IReadsItems
    {
        public ImmutableArray<string> Items { get; } = [];

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedReadOnlyList : IReadsItems
    {
        public IReadOnlyList<string> Items { get; } = new List<string>();

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedEnumerable : IReadsItems
    {
        public IEnumerable<string> Items { get; } = new List<string>();

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedImmutableList : IReadsItems
    {
        public ImmutableList<string> Items { get; } = [];

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedQueue : IReadsItems
    {
        public Queue<string> Items { get; } = new();

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedBag : IReadsItems
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Items { get; } = [];

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedListWithoutDefaultConstructor : IReadsItems
    {
        public Values<string> Items { get; } = new(4);

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedCollectionWithoutDefaultConstructor : IReadsItems
    {
        public Bag<string> Items { get; } = new(4);

        IEnumerable<string> IReadsItems.Seen => Items;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedMapWithoutDefaultConstructor : IReadsItems
    {
        public ValuesMap<string, string> Items { get; } = new(4);

        IEnumerable<string> IReadsItems.Seen => Items.Values;
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedReadOnlyMap : IReadsItems
    {
        public IReadOnlyDictionary<string, string> Items { get; } = new Dictionary<string, string>();

        IEnumerable<string> IReadsItems.Seen => Items.Values;
    }

    public sealed class Values<T>(int capacity) : List<T>(capacity);

    public sealed class ValuesMap<TKey, TValue>(int capacity) : Dictionary<TKey, TValue>(capacity)
        where TKey : notnull;

    /// <summary>A collection only through <see cref="ICollection{T}"/>, without a constructor the serializer can call.</summary>
    public sealed class Bag<T>(int capacity) : ICollection<T>
    {
        private readonly List<T> _items = new(capacity);

        public int Count => _items.Count;

        public bool IsReadOnly => false;

        public void Add(T item) => _items.Add(item);

        public void Clear() => _items.Clear();

        public bool Contains(T item) => _items.Contains(item);

        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        public bool Remove(T item) => _items.Remove(item);

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatedStruct
    {
        public Held Held { get; } = new([]);
    }

    public sealed class IgnoredWithParameter(List<string>? items)
    {
        [JsonIgnore]
        public List<string> Items { get; } = items ?? [];
    }

    public sealed class UnfilledExtension
    {
        [JsonExtensionData]
        public Dictionary<string, object> Other { get; private set; } = [];
    }

    /// <summary>Its constructor takes nulls and drops them.</summary>    /// <summary>Its constructor takes nulls and drops them.</summary>
    public sealed class FilteringConstructor(List<string?> items)
    {
        public List<string> Items { get; } = Without(items);

        private static List<string> Without(List<string?> items) => [.. items.Where(item => item is not null).Select(item => item!)];
    }

    public sealed class PlainConstructor(List<string> items)
    {
        public List<string> Items { get; } = items;
    }
}
