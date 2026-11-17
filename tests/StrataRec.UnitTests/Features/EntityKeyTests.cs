using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Catalog;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// An entity declares only its key, an ordered tuple of 1 to 8 parts; whether it is per request or per candidate, and each
/// part's type, follow from it (audit B2). Declared separately, they could disagree: <c>"key_type": "int64"</c> on
/// <c>col.entity_id</c> in a pipeline of string ids made every lookup of the entity's features miss, with no error.
/// </summary>
public sealed class EntityKeyTests
{
    private static readonly Dictionary<string, ColumnType> Inputs = new(StringComparer.Ordinal)
    {
        ["user_id"] = ColumnType.Int64, ["session_id"] = ColumnType.String, ["region_code"] = ColumnType.Int32, ["score"] = ColumnType.Float64,
        ["country"] = ColumnType.String.AsNullable(), ["day"] = ColumnType.Int64.AsNullable(),
    };

    [Theory]
    [InlineData(EntityKeyType.Int64)]
    [InlineData(EntityKeyType.String)]
    public void TheKeyDecidesTheEntitysScopeAndPartTypes(EntityKeyType pipelineKeys)
    {
        var catalog = Build("""
            {
              "user": { "key": "ctx.request.user_id" },
              "session": { "key": "ctx.request.session_id" },
              "region": { "key": "ctx.request.region_code" },
              "item": { "key": "col.entity_id" },
              "author": { "key": "col.item.author_id" },
              "handle": { "key": "col.item.handle" },
              "user_item": { "key": ["ctx.request.user_id", "col.entity_id"] },
              "item_country": { "key": ["col.entity_id", "ctx.request.country"] },
              "user_day": { "key": ["ctx.request.user_id", "ctx.request.day"] },
              "single_list": { "key": ["col.entity_id"] },
              "author_item_country": { "key": ["col.item.author_id", "col.entity_id", "ctx.request.country"] }
            }
            """, pipelineKeys, out var diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var p = pipelineKeys == EntityKeyType.String ? "string" : "int64";
        Assert.Equal((true, "int64"), Of(catalog, "user.x", context: true));
        Assert.Equal((true, "string"), Of(catalog, "session.x", context: true));
        Assert.Equal((true, "int64"), Of(catalog, "region.x", context: true)); // int32 keys as int64
        Assert.Equal((false, p), Of(catalog, "item.x")); // the pipeline's entity_key_type
        Assert.Equal((false, "int64"), Of(catalog, "author.x")); // its key feature's type
        Assert.Equal((false, "string"), Of(catalog, "handle.x"));
        Assert.Equal((false, $"(int64,{p})"), Of(catalog, "user_item.x"));
        Assert.Equal((false, $"({p},string)"), Of(catalog, "item_country.x")); // a nullable input keys an entity: a missing one, no key
        Assert.Equal((true, "(int64,int64)"), Of(catalog, "user_day.x", context: true)); // every part per request: context-scoped
        Assert.Equal((false, p), Of(catalog, "single_list.x"));
        Assert.Equal((false, $"(int64,{p},string)"), Of(catalog, "author_item_country.x"));

        var layout = catalog.Entities["item_country"].KeyLayout;
        Assert.Equal(["col.entity_id", "ctx.request.country"], layout.Parts.Select(part => part.Reference));
        Assert.Equal([false, true], layout.Parts.Select(part => part.IsShared));
        Assert.Equal(1, layout.VaryingCount);
        Assert.Equal(
            [EntityKeyPartKind.Feature, EntityKeyPartKind.EntityId, EntityKeyPartKind.RequestInput],
            catalog.Entities["author_item_country"].Key.Select(part => part.Kind));
    }

    [Fact]
    public void AKeyOfEightParts_IsAKey()
    {
        var parts = string.Join(", ", Enumerable.Range(0, 8).Select(i => $"\"col.item.k{i}\""));
        var catalog = Build($$"""{ "e": { "key": [{{parts}}] } }""", EntityKeyType.Int64, out var diagnostics, keyFeatures: 8);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        Assert.Equal(8, catalog.Entities["e"].KeyLayout.PartCount);
    }

    [Theory]
    [InlineData("""{ "e": { "key": "ctx.request.score" } }""", "/entities/e/key", "has type float64; an entity key part is an int64 or a string")]
    [InlineData("""{ "e": { "key": ["col.entity_id", "ctx.request.score"] } }""", "/entities/e/key/1", "has type float64")]
    [InlineData("""{ "e": { "key": "ctx.request.nope" } }""", "/entities/e/key", "Request input 'nope' is not declared")]
    [InlineData("""{ "e": { "key": "request.user_id" } }""", "/entities/e/key", "is not a reference an entity can be keyed by")]
    [InlineData("""{ "e": { "key": ["col.entity_id", "col."] } }""", "/entities/e/key/1", "is not a reference an entity can be keyed by")]
    [InlineData("""{ "e": { "key": "col.item.ratio" } }""", "/entities/e/key", "must be a candidate feature of type int64 or string, got float32")]
    [InlineData("""{ "e": { "key": ["col.entity_id", "col.item.ratio"] } }""", "/entities/e/key/1", "got float32")]
    [InlineData("""{ "e": { "key": ["col.entity_id", "col.item.nope"] } }""", "/entities/e/key/1", "is not a declared feature")]
    [InlineData("""{ "e": { "key": [] } }""", "/entities/e/key", "has 0 parts; a key has 1 to 8")]
    [InlineData("""{ "e": { "key": ["col.entity_id", 7] } }""", "/entities/e/key/1", "A key part is a reference")]
    [InlineData("""{ "e": { "key": ["col.entity_id", "ctx.request.user_id", "col.entity_id"] } }""", "/entities/e/key/2", "repeats part 0")]
    [InlineData("""{ "e": { "key": "ctx.u.y" } }""", "/entities/e/key", "is a context feature; an entity key cannot read a context feature yet")]
    [InlineData("""{ "e": { "key": ["col.entity_id", "ctx.u.y"] } }""", "/entities/e/key/1", "is a context feature")]
    [InlineData("""{ "e": { "key": "col.u.y" } }""", "/entities/e/key", "got a context feature")]
    public void AKeyThatCannotKeyAnEntity_IsAnErrorAtThePartWithAHint(string entities, string pointer, string message)
    {
        Build(entities, EntityKeyType.Int64, out var diagnostics);

        var error = Assert.Single(diagnostics.Items, d => d.Code == "ENTITY_BAD_KEY");
        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint), error.ToString());
    }

    [Fact]
    public void AKeyOfNineParts_IsAnErrorAtTheKey()
    {
        var parts = string.Join(", ", Enumerable.Range(0, 9).Select(i => $"\"col.item.k{i}\""));
        Build($$"""{ "e": { "key": [{{parts}}] } }""", EntityKeyType.Int64, out var diagnostics, keyFeatures: 9);

        var error = Assert.Single(diagnostics.Items, d => d.Code == "ENTITY_BAD_KEY");
        Assert.Equal("/entities/e/key", error.Pointer);
        Assert.Contains("has 9 parts; a key has 1 to 8", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void AnEntityWithABadPart_IsRemovedWithItsFeatures_AndSoIsOneKeyedByThem()
    {
        var catalog = Build("""
            {
              "bad": { "key": ["col.entity_id", "col.item.ratio"] },
              "follower": { "key": ["col.bad.k", "ctx.request.user_id"] }
            }
            """, EntityKeyType.Int64, out var diagnostics, extraFeatures: """{ "bad.k": { "type": "int64", "source": "s", "field": "k" } }""");

        Assert.Single(diagnostics.Items, d => d.Code == "ENTITY_BAD_KEY"); // the follower fails with the bad key, silently
        Assert.False(catalog.Entities.ContainsKey("bad"));
        Assert.False(catalog.Entities.ContainsKey("follower"));
        Assert.False(catalog.TryGet("bad.x", out _));
        Assert.False(catalog.TryGet("follower.x", out _));
    }

    [Fact]
    public void ACycleThroughAnyPartOfAKey_IsAnErrorAtThatPart()
    {
        Build("""
            {
              "a": { "key": ["col.entity_id", "col.b.k"] },
              "b": { "key": ["ctx.request.user_id", "col.a.k"] }
            }
            """, EntityKeyType.Int64, out var diagnostics, extraFeatures: """
            { "a.k": { "type": "int64", "source": "s", "field": "k" }, "b.k": { "type": "int64", "source": "s", "field": "k" } }
            """);

        var cycles = diagnostics.Items.Where(d => d.Code == "ENTITY_KEY_CYCLE").ToArray();
        Assert.Equal(["/entities/a/key/1", "/entities/b/key/1"], cycles.Select(d => d.Pointer).Order(StringComparer.Ordinal));
        Assert.All(cycles, d => Assert.NotNull(d.Hint));
    }

    [Fact]
    public void LayoutEquality_IsOverReferencesTypesAndSharing()
    {
        var a = new EntityKeyLayout([new EntityKeyPart("col.entity_id", EntityKeyType.Int64, false), new EntityKeyPart("ctx.request.c", EntityKeyType.String, true)]);
        var b = new EntityKeyLayout([new EntityKeyPart("col.entity_id", EntityKeyType.Int64, false), new EntityKeyPart("ctx.request.c", EntityKeyType.String, true)]);
        var other = new EntityKeyLayout([new EntityKeyPart("col.entity_id", EntityKeyType.Int64, false), new EntityKeyPart("ctx.request.d", EntityKeyType.String, true)]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, other);
        Assert.True(a.HasSameTypes(other)); // what a table stores: the part types in order
        Assert.Equal("(int64,string)", a.TypeText);
        Assert.Equal(0b10, a.SharedMask);
        Assert.Contains("ctx.request.c string (shared)", a.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new EntityKeyLayout([]));
        Assert.Throws<ArgumentException>(() => new EntityKeyLayout([new EntityKeyPart("a", EntityKeyType.Int64, false), new EntityKeyPart("a", EntityKeyType.Int64, false)]));
    }

    [Theory]
    [InlineData("scope", "\"candidate\"")]
    [InlineData("key_type", "\"int64\"")]
    public void TheRemovedScopeAndKeyType_AreUnknownMembers(string member, string value)
    {
        var json = JsonNode.Parse($$"""{ "key": "col.entity_id", "{{member}}": {{value}} }""");

        Assert.False(ArtifactSerializer.TryBind<EntitySpec>(json, "entity", out _, out var error));
        Assert.Equal("/" + member, error.Pointer);
    }

    private static (bool Context, string Types) Of(FeatureCatalog catalog, string feature, bool context = false)
    {
        Assert.True(catalog.TryGet(feature, out var f), feature);
        Assert.Equal(context, f.IsContext);
        return (f.Entity.IsContextScoped, f.Entity.KeyLayout.TypeText);
    }

    /// <summary>A catalog of <paramref name="entities"/> (plus <c>item</c>, the foreign keys' entity, and <c>u</c>, a context
    /// entity), with a float feature <c>x</c> on each, the key features <c>item.author_id</c> (int64), <c>item.handle</c>
    /// (string) and <c>item.ratio</c> (float32), <c>item.k0</c>… (int64), and the context feature <c>u.y</c> (int64).</summary>
    private static FeatureCatalog Build(
        string entities, EntityKeyType pipelineKeys, out DiagnosticBag diagnostics, int keyFeatures = 0, string? extraFeatures = null)
    {
        var declared = JsonNode.Parse(entities)!.AsObject();
        declared["item"] ??= JsonNode.Parse("""{ "key": "col.entity_id" }""");
        declared["u"] ??= JsonNode.Parse("""{ "key": "ctx.request.user_id" }""");
        var features = new JsonObject
        {
            ["item.author_id"] = JsonNode.Parse("""{ "type": "int64", "source": "s", "field": "a" }"""),
            ["item.handle"] = JsonNode.Parse("""{ "type": "string", "source": "s", "field": "h" }"""),
            ["item.ratio"] = JsonNode.Parse("""{ "type": "float32", "source": "s", "field": "r" }"""),
            ["u.y"] = JsonNode.Parse("""{ "type": "int64", "source": "s", "field": "y" }"""),
        };
        for (var i = 0; i < keyFeatures; i++)
        {
            features["item.k" + i] = JsonNode.Parse($$"""{ "type": "int64", "source": "s", "field": "k{{i}}" }""");
        }

        foreach (var (name, value) in extraFeatures is null ? new JsonObject() : JsonNode.Parse(extraFeatures)!.AsObject())
        {
            features[name] = value!.DeepClone();
        }

        foreach (var (name, _) in declared)
        {
            features[name + ".x"] = JsonNode.Parse("""{ "type": "float32", "source": "s", "field": "x" }""");
        }

        var artifact = ArtifactSerializer.Deserialize<FeatureCatalogArtifact>(
            new JsonObject { ["catalog_id"] = "c", ["entities"] = declared, ["features"] = features });
        diagnostics = new DiagnosticBag();
        return FeatureCatalog.Build(artifact, Inputs, pipelineKeys, diagnostics);
    }
}
