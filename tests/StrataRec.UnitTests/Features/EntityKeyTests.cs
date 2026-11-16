using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Catalog;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// An entity declares only its key; whether it is per request or per candidate, and the key's type, follow from it
/// (audit B2). Declared separately, they could disagree: <c>"key_type": "int64"</c> on <c>col.entity_id</c> in a pipeline of
/// string ids made every lookup of the entity's features miss, with no error.
/// </summary>
public sealed class EntityKeyTests
{
    private static readonly Dictionary<string, ColumnType> Inputs = new(StringComparer.Ordinal)
    {
        ["user_id"] = ColumnType.Int64, ["session_id"] = ColumnType.String, ["region_code"] = ColumnType.Int32, ["score"] = ColumnType.Float64,
    };

    [Theory]
    [InlineData(EntityKeyType.Int64)]
    [InlineData(EntityKeyType.String)]
    public void TheKeyDecidesTheEntitysKindAndKeyType(EntityKeyType pipelineKeys)
    {
        var catalog = Build("""
            {
              "user": { "key": "ctx.request.user_id" },
              "session": { "key": "ctx.request.session_id" },
              "region": { "key": "ctx.request.region_code" },
              "item": { "key": "col.entity_id" },
              "author": { "key": "col.item.author_id" },
              "handle": { "key": "col.item.handle" },
              "user_item": { "key": ["ctx.request.user_id", "col.entity_id"] }
            }
            """, pipelineKeys, out var diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        Assert.Equal((EntityKind.Context, EntityKeyType.Int64), Of(catalog, "user.x", context: true));
        Assert.Equal((EntityKind.Context, EntityKeyType.String), Of(catalog, "session.x", context: true));
        Assert.Equal((EntityKind.Context, EntityKeyType.Int64), Of(catalog, "region.x", context: true)); // int32 keys as int64
        Assert.Equal((EntityKind.Candidate, pipelineKeys), Of(catalog, "item.x")); // the pipeline's entity_key_type
        Assert.Equal((EntityKind.ForeignKey, EntityKeyType.Int64), Of(catalog, "author.x")); // its key feature's type
        Assert.Equal((EntityKind.ForeignKey, EntityKeyType.String), Of(catalog, "handle.x"));
        Assert.Equal((EntityKind.Cross, pipelineKeys), Of(catalog, "user_item.x"));
        Assert.True(catalog.TryGet("user_item.x", out var cross));
        Assert.Equal(EntityKeyType.Int64, cross.Entity.ContextKeyType);
    }

    [Theory]
    [InlineData("""{ "e": { "key": "ctx.request.score" } }""", "/entities/e/key", "has type float64; an entity key is an int64 or a string")]
    [InlineData("""{ "e": { "key": ["ctx.request.user_id", "ctx.request.session_id"] } }""", "/entities/e/key/1", "a cross entity is keyed by one request input")]
    [InlineData("""{ "e": { "key": ["col.entity_id", "col.item.author_id"] } }""", "/entities/e/key/1", "a cross entity is keyed by one request input")]
    [InlineData("""{ "e": { "key": "request.user_id" } }""", "/entities/e/key", "must reference a request input (ctx.request.*) or a candidate column")]
    [InlineData("""{ "e": { "key": "col.item.ratio" } }""", "/entities/e/key", "must be a candidate feature of type int64 or string, got float32")]
    public void AKeyThatCannotKeyAnEntity_IsAnErrorAtTheKey(string entities, string pointer, string message)
    {
        Build(entities, EntityKeyType.Int64, out var diagnostics);

        var error = Assert.Single(diagnostics.Items, d => d.Code == "ENTITY_BAD_KEY");
        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
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

    private static (EntityKind, EntityKeyType?) Of(FeatureCatalog catalog, string feature, bool context = false)
    {
        Assert.True(catalog.TryGet(feature, out var f), feature);
        return (f.Entity.Kind, context ? f.Entity.ContextKeyType : f.Entity.CandidateKeyType);
    }

    /// <summary>A catalog of <paramref name="entities"/> (plus <c>item</c>, the foreign keys' entity), with a float
    /// feature <c>x</c> on each, and the key features <c>item.author_id</c> (int64), <c>item.handle</c> (string) and
    /// <c>item.ratio</c> (float32).</summary>
    private static FeatureCatalog Build(string entities, EntityKeyType pipelineKeys, out DiagnosticBag diagnostics)
    {
        var declared = JsonNode.Parse(entities)!.AsObject();
        declared["item"] ??= JsonNode.Parse("""{ "key": "col.entity_id" }""");
        var features = new JsonObject
        {
            ["item.author_id"] = JsonNode.Parse("""{ "type": "int64", "source": "s", "field": "a" }"""),
            ["item.handle"] = JsonNode.Parse("""{ "type": "string", "source": "s", "field": "h" }"""),
            ["item.ratio"] = JsonNode.Parse("""{ "type": "float32", "source": "s", "field": "r" }"""),
        };
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
