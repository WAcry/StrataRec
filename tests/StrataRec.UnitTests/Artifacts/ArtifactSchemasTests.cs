using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// The generated schemas (design 9.1): an operator's config chosen by a node's op, configs that repeat or contain a type
/// made self-contained, an arm as a merge patch that never requires a member and lets any value be null, and the
/// editor's "$schema" taken out of a document before it is bound and hashed.
/// </summary>
public sealed class ArtifactSchemasTests
{
    private static readonly OperatorSchemaSource[] Operators =
    {
        new("Repeated@1", "Two parts of one type.", typeof(RepeatedConfig)),
        new("Optional@1", "Nothing required.", typeof(OptionalConfig)),
        new("Recursive@1", "Contains itself.", typeof(RecursiveConfig)),
    };

    [Fact]
    public void ThePipelineSchema_ListsTheOperators_AndChecksAConfigByItsOp()
    {
        var schema = ArtifactSchemas.Artifact("pipeline", Operators);
        var node = schema["properties"]!["nodes"]!["additionalProperties"]!.AsObject();
        var op = node["properties"]!["op"]!;

        Assert.Equal("""["Optional@1","Recursive@1","Repeated@1"]""", op["enum"]!.ToJsonString()); // by id
        Assert.Equal("""["Nothing required.","Contains itself.","Two parts of one type."]""", op["enumDescriptions"]!.ToJsonString());
        Assert.Equal("Operator type and version, e.g. TopK@1.", op["description"]!.GetValue<string>()); // its XML documentation

        var branches = node["allOf"]!.AsArray();
        Assert.Equal(3, branches.Count);
        var repeated = Branch(branches, "Repeated@1");
        Assert.Equal("""{"properties":{"op":{"const":"Repeated@1"}},"required":["op"]}""", repeated["if"]!.ToJsonString());
        Assert.Equal("""["config"]""", repeated["then"]!["required"]!.ToJsonString()); // a missing config is {}, which lacks 'first'
        var config = repeated["then"]!["properties"]!["config"]!;
        Assert.Equal("Two parts of one type.", config["description"]!.GetValue<string>());
        Assert.Equal("\"object\"", config["type"]!.ToJsonString());
        Assert.False(config["additionalProperties"]!.GetValue<bool>());

        // Nothing required: a missing or null config is read as {}.
        var optional = Branch(branches, "Optional@1")["then"]!;
        Assert.Null(optional["required"]);
        Assert.Equal("""["object","null"]""", optional["properties"]!["config"]!["type"]!.ToJsonString());
    }

    [Fact]
    public void ARepeatedOrRecursiveType_IsCopiedInPlace_SoNoPointerGoesStale()
    {
        var text = ArtifactSchemas.ToText(ArtifactSchemas.Artifact("pipeline", Operators));
        Assert.DoesNotContain("$ref", text, StringComparison.Ordinal);

        var schema = ArtifactSchemas.Artifact("pipeline", Operators);
        var branches = schema["properties"]!["nodes"]!["additionalProperties"]!["allOf"]!.AsArray();
        var repeated = Branch(branches, "Repeated@1")["then"]!["properties"]!["config"]!["properties"]!;
        Assert.Equal(repeated["first"]!["properties"]!.ToJsonString(), repeated["second"]!["properties"]!.ToJsonString());
        Assert.NotNull(repeated["second"]!["properties"]!["name"]);

        // Described to the first repetition; below it any value is accepted rather than a pointer into another schema.
        var recursive = Branch(branches, "Recursive@1")["then"]!["properties"]!["config"]!["properties"]!;
        Assert.NotNull(recursive["depth"]);
        Assert.NotNull(recursive["child"]);
    }

    [Theory]
    [InlineData("pipeline", true)]
    [InlineData("catalog", true)]
    [InlineData("bindings", true)]
    [InlineData("experiment", true)]
    [InlineData("release", true)]
    [InlineData("policy", true)]
    [InlineData("table", true)]
    [InlineData("manifest", false)]
    [InlineData("replay_bundle", false)]
    public void AWorkspaceFile_MayNameItsSchema_AndBeJsonc(string kind, bool workspaceFile)
    {
        var schema = ArtifactSchemas.Artifact(kind, Operators);

        Assert.Equal(ArtifactSchemas.Dialect, schema["$schema"]!.GetValue<string>());
        Assert.Equal(workspaceFile, schema["properties"]!.AsObject().ContainsKey("$schema"));
        Assert.Equal(workspaceFile, schema["allowComments"]?.GetValue<bool>() ?? false);
        Assert.Equal(workspaceFile, schema["allowTrailingCommas"]?.GetValue<bool>() ?? false);
        if (workspaceFile)
        {
            var member = schema["properties"]!["$schema"]!;
            Assert.Equal("\"string\"", member["type"]!.ToJsonString());
            Assert.Contains($"../schemas/{kind}.schema.json", member["description"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("$schema", schema["properties"]!.AsObject().First().Key);
        }
    }

    [Fact]
    public void AnUnknownKind_IsAnArgumentError() =>
        Assert.Throws<ArgumentException>(() => ArtifactSchemas.Artifact("pipelines", Operators));

    [Fact]
    public void AnArm_IsAMergePatchOfThePatchableView()
    {
        var arm = ArtifactSchemas.Artifact("experiment", Operators)["properties"]!["arms"]!["additionalProperties"]!.AsObject();

        Assert.Equal(ExperimentArtifact.PipelineSections.Concat(ExperimentArtifact.CatalogSections), arm["properties"]!.AsObject().Select(p => p.Key));
        Assert.False(arm["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("\"object\"", arm["properties"]!["nodes"]!["type"]!.ToJsonString()); // a section is patched, never deleted

        var node = arm["properties"]!["nodes"]!["additionalProperties"]!;
        Assert.Equal("""["object","null"]""", node["type"]!.ToJsonString()); // null deletes a node
        Assert.Null(node["required"]);
        Assert.Contains(node["properties"]!["op"]!["enum"]!.AsArray(), v => v is null);
        var config = Branch(node["allOf"]!.AsArray(), "Repeated@1")["then"]!;
        Assert.Null(config["required"]);
        Assert.Null(config["properties"]!["config"]!["required"]);
        Assert.Equal("""{"properties":{"op":{"const":"Repeated@1"}},"required":["op"]}""", Branch(node["allOf"]!.AsArray(), "Repeated@1")["if"]!.ToJsonString());

        var feature = arm["properties"]!["features"]!["additionalProperties"]!;
        Assert.Null(feature["required"]);
        Assert.False(feature["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void MergePatch_RequiresNothing_LetsAnyValueBeNull_AndKeepsWhatAValueMayNotBe()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "a": { "type": "string", "enum": ["x", "y"] },
                "b": { "type": ["integer", "null"] },
                "c": { "type": "array", "items": { "type": "object", "properties": { "d": { "type": "string" } }, "required": ["d"] } },
                "e": { "type": "object", "properties": { "f": { "type": "boolean" } }, "required": ["f"], "additionalProperties": false },
                "g": { "const": "k" },
                "h": { "description": "any value" },
                "m": { "type": "object", "additionalProperties": { "type": "object", "properties": { "n": { "type": "number" } }, "required": ["n"] } }
              },
              "required": ["a"],
              "additionalProperties": false,
              "allOf": [ { "if": { "properties": { "a": { "const": "x" } }, "required": ["a"] }, "then": { "properties": { "e": { "required": ["f"] } }, "required": ["e"] } } ]
            }
            """)!.AsObject();

        ArtifactSchemas.MergePatch(schema);

        Assert.Equal("""["object","null"]""", schema["type"]!.ToJsonString());
        Assert.Null(schema["required"]);
        Assert.False(schema["additionalProperties"]!.GetValue<bool>()); // an unknown key is still an error
        var properties = schema["properties"]!;
        Assert.Equal("""{"type":["string","null"],"enum":["x","y",null]}""", properties["a"]!.ToJsonString());
        Assert.Equal("""{"type":["integer","null"]}""", properties["b"]!.ToJsonString());
        Assert.Equal("""["array","null"]""", properties["c"]!["type"]!.ToJsonString());
        Assert.Equal("""["d"]""", properties["c"]!["items"]!["required"]!.ToJsonString()); // a patch replaces an array whole
        Assert.Equal("""{"type":["object","null"],"properties":{"f":{"type":["boolean","null"]}},"additionalProperties":false}""", properties["e"]!.ToJsonString());
        Assert.Equal("""{"enum":["k",null]}""", properties["g"]!.ToJsonString());
        Assert.Equal("""{"description":"any value"}""", properties["h"]!.ToJsonString());
        Assert.Null(properties["m"]!["additionalProperties"]!["required"]);
        Assert.Equal("""["number","null"]""", properties["m"]!["additionalProperties"]!["properties"]!["n"]!["type"]!.ToJsonString());

        var branch = schema["allOf"]![0]!;
        Assert.Equal("""{"properties":{"a":{"const":"x"}},"required":["a"]}""", branch["if"]!.ToJsonString()); // a condition, not a value
        Assert.Equal("""{"properties":{"e":{}}}""", branch["then"]!.ToJsonString());
    }

    [Theory]
    [InlineData("""{ "$schema": "../schemas/pipeline.schema.json", "pipeline_id": "p" }""", """{"pipeline_id":"p"}""")]
    [InlineData("""{ "pipeline_id": "p" }""", """{"pipeline_id":"p"}""")]
    [InlineData("""{ "nodes": { "a": { "$schema": "x" } } }""", """{"nodes":{"a":{"$schema":"x"}}}""")] // only the top level is the editor's
    [InlineData("""[1]""", """[1]""")]
    public void TheEditorsSchemaMember_IsTakenOut(string json, string expected)
    {
        var document = JsonNode.Parse(json);

        Assert.True(ArtifactSchemas.TryRemoveSchemaMember(document, "../schemas/pipeline.schema.json", out var error));
        Assert.Null(error);
        Assert.Equal(expected, document!.ToJsonString());
    }

    [Theory]
    [InlineData("3", "the number 3")]
    [InlineData("null", "null")]
    [InlineData("{}", "an object")]
    public void ASchemaMemberThatIsNotAString_IsAnErrorAtIt_WithWhatToWrite(string value, string got)
    {
        var document = JsonNode.Parse($$"""{ "$schema": {{value}}, "table_id": "t" }""");

        Assert.False(ArtifactSchemas.TryRemoveSchemaMember(document, "../schemas/table.schema.json", out var error));
        Assert.Equal("/$schema", error.Pointer);
        Assert.Equal($"'$schema' must be a string, the path or URL of the file's JSON Schema; got {got}.", error.Message);
        Assert.Equal("write \"$schema\": \"../schemas/table.schema.json\" ('stratarec schema --out schemas' writes the schemas there), or remove it", error.Hint);
    }

    [Theory]
    [InlineData("policy.jsonc", "policy", "schemas/policy.schema.json")]
    [InlineData("pipelines/home_feed.jsonc", "pipeline", "../schemas/pipeline.schema.json")]
    [InlineData("requests/a/b.json", "request", "../../schemas/request.schema.json")]
    public void AReference_IsRelativeToTheFile(string path, string kind, string expected) =>
        Assert.Equal(expected, ArtifactSchemas.Reference(path, kind));

    [Fact]
    public void TheText_IsStable_WithUnixLineEnds()
    {
        var first = ArtifactSchemas.ToText(ArtifactSchemas.Artifact("experiment", Operators));
        var second = ArtifactSchemas.ToText(ArtifactSchemas.Artifact("experiment", Operators));

        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"$schema\": \"https://json-schema.org/draft/2020-12/schema\",\n  \"title\": \"ExperimentArtifact\",\n", first, StringComparison.Ordinal);
        Assert.EndsWith("\n}\n", first, StringComparison.Ordinal);
    }

    private static JsonObject Branch(JsonArray branches, string op) =>
        branches.Single(b => b!["if"]!["properties"]!["op"]!["const"]!.GetValue<string>() == op)!.AsObject();

    public sealed class RepeatedConfig
    {
        public required Part First { get; init; }

        public Part? Second { get; init; }
    }

    public sealed class Part
    {
        public string? Name { get; init; }
    }

    public sealed class OptionalConfig
    {
        public int? K { get; init; }
    }

    public sealed class RecursiveConfig
    {
        public RecursiveConfig? Child { get; init; }

        public int Depth { get; init; }
    }
}
