using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;

namespace StrataRec.Artifacts.Model;

/// <summary>An operator as the schemas describe it: the id a node's <c>op</c> names, what it does, and the type of its config.</summary>
/// <param name="Id"><c>Name@Version</c>, e.g. <c>TopK@1</c>.</param>
/// <param name="Description">One line, which an editor shows next to the id when it completes <c>op</c>.</param>
/// <param name="ConfigType">The C# type the operator binds its <c>config</c> to: the single source of the config's schema (design 9.1).</param>
public sealed record OperatorSchemaSource(string Id, string Description, Type ConfigType);

/// <summary>
/// JSON Schema (2020-12) of the artifacts and operator configs, generated from the C# types that bind them (design 9.1),
/// so that an editor completes and checks a file against exactly what the engine accepts. One place builds them all:
/// <c>stratarec schema</c> prints them, writes them into a workspace's <c>schemas/</c> and checks that the ones there are
/// current.
/// </summary>
/// <remarks>
/// <para>What a type cannot say is added here: each member's description (its <see cref="DescriptionAttribute"/>, else its
/// XML documentation summary, as errors already use: <see cref="MemberDocs"/>), allowed values and patterns; in the
/// pipeline, the config of every operator of the binary, chosen by the node's <c>op</c>; in the experiment, each arm as a
/// merge patch of the pipeline (<see cref="MergePatch"/>). A workspace file may name its schema in a top-level
/// <c>$schema</c>, which its schema declares and the engine ignores (<see cref="TryRemoveSchemaMember"/>).</para>
/// <para>Beyond 2020-12, the schemas carry the keywords VS Code's JSON service reads and other validators ignore as
/// unknown keywords: <c>allowComments</c> and <c>allowTrailingCommas</c> (files are JSONC) and <c>enumDescriptions</c>
/// (what each operator does). The text is stable (<see cref="ToText"/>): the same binary writes the same bytes, so a
/// committed copy can be compared.</para>
/// </remarks>
public static class ArtifactSchemas
{
    /// <summary>The JSON Schema dialect of every schema here.</summary>
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The top-level member with which a workspace file names its schema for editors.</summary>
    public const string SchemaMember = "$schema";

    /// <summary>The directory of a workspace that holds the schemas its files name.</summary>
    public const string DirectoryName = "schemas";

    /// <summary>
    /// The artifact kinds a workspace holds as files (design 10.1); each may name its schema in <c>$schema</c>, and their
    /// schemas declare it. Release manifests and replay bundles are written by tools, not by hand, and do not.
    /// </summary>
    public static IReadOnlyList<string> WorkspaceKinds { get; } = new[] { "pipeline", "catalog", "bindings", "experiment", "release", "policy", "table" };

    private static readonly JsonSerializerOptions TextOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // descriptions stay readable
    };

    /// <summary>The file name of a kind's schema in <see cref="DirectoryName"/>, e.g. <c>pipeline.schema.json</c>.</summary>
    public static string FileName(string kind) => kind + ".schema.json";

    /// <summary>
    /// What a workspace file writes in <c>$schema</c> to name the schema of <paramref name="kind"/> in the workspace's
    /// <see cref="DirectoryName"/>: a path relative to the file, as editors resolve it, e.g.
    /// <c>../schemas/pipeline.schema.json</c> for <c>pipelines/home_feed.jsonc</c>.
    /// </summary>
    /// <param name="path">The file's path relative to the workspace root, with <c>/</c> separators.</param>
    public static string Reference(string path, string kind)
    {
        var up = string.Empty;
        foreach (var c in path)
        {
            if (c == '/')
            {
                up += "../";
            }
        }

        return up + DirectoryName + "/" + FileName(kind);
    }

    /// <summary>
    /// The schema of an artifact kind (<see cref="ArtifactSerializer.ArtifactTypes"/>). The pipeline's nodes are checked
    /// against the config of each of <paramref name="operators"/>, and so are the nodes an experiment's arms patch.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not an artifact kind.</exception>
    public static JsonObject Artifact(string kind, IReadOnlyList<OperatorSchemaSource> operators)
    {
        ArgumentNullException.ThrowIfNull(operators);
        Type? type = null;
        foreach (var (name, artifactType) in ArtifactSerializer.ArtifactTypes)
        {
            if (name == kind)
            {
                type = artifactType;
            }
        }

        if (type is null)
        {
            throw new ArgumentException($"'{kind}' is not an artifact kind.", nameof(kind));
        }

        var editable = IsWorkspaceKind(kind);
        var schema = kind switch
        {
            "pipeline" => Pipeline(Sorted(operators)),
            "experiment" => Experiment(Sorted(operators)),
            _ => Export(type, Describe, inline: editable),
        };
        return Document(schema, type.Name, editable ? kind : null);
    }

    /// <summary>
    /// The schema of a file a workspace holds that is not an artifact, such as <c>stratarec run</c>'s request file: like
    /// an artifact's, it may name its schema in <c>$schema</c>.
    /// </summary>
    /// <param name="kind">What the file is called among the schemas, e.g. <c>request</c>.</param>
    public static JsonObject File(Type type, string kind)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Document(Export(type, Describe, inline: true), type.Name, kind);
    }

    /// <summary>The schema of an operator's config, as the pipeline schema checks a node's <c>config</c> with it.</summary>
    public static JsonObject Operator(OperatorSchemaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Document(Config(source), source.Id + " config", editable: null);
    }

    /// <summary>
    /// The text of a schema as it is printed and written: indented by two spaces, <c>\n</c> line ends, a final newline,
    /// and text unescaped where JSON allows. The same schema always gives the same bytes, on every platform.
    /// </summary>
    public static string ToText(JsonNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.ToJsonString(TextOptions) + "\n";
    }

    /// <summary>
    /// Takes a workspace file's top-level <c>$schema</c> out of its document, before the document is bound and hashed:
    /// it tells an editor where the file's schema is, and is not part of the artifact, so a file that only gains or loses
    /// it has the same content id and builds the same release. Only a string at the top level is the editor's; a
    /// <c>$schema</c> anywhere else is an unknown key like any other. False, with the error at <c>/$schema</c>, when
    /// the member is not a string.
    /// </summary>
    /// <param name="document">The parsed file; changed in place.</param>
    /// <param name="reference">What the file would write to name its schema (<see cref="Reference"/>), for the hint; null when unknown.</param>
    public static bool TryRemoveSchemaMember(JsonNode? document, string? reference, [NotNullWhen(false)] out JsonBindingError? error)
    {
        error = null;
        if (document is not JsonObject obj || !obj.TryGetPropertyValue(SchemaMember, out var value))
        {
            return true;
        }

        if (value is JsonValue text && text.GetValueKind() == JsonValueKind.String)
        {
            obj.Remove(SchemaMember);
            return true;
        }

        var example = reference ?? "../" + DirectoryName + "/" + FileName("pipeline");
        error = new JsonBindingError("/" + SchemaMember,
            $"'{SchemaMember}' must be a string, the path or URL of the file's JSON Schema; got {JsonWords.Value(value)}.",
            $"write \"{SchemaMember}\": \"{example}\" ('stratarec schema --out {DirectoryName}' writes the schemas there), or remove it");
        return false;
    }

    /// <summary>
    /// Turns the schema of a value into the schema of an RFC 7396 merge patch of it, in place: no member is required
    /// (an object changes only the members it names), and every value may be <c>null</c> (which deletes it). What the
    /// value may not be stays: an unknown key or a value of another type is still an error. Array items are not patches
    /// (a patch replaces an array whole), and an <c>if</c> is a condition, not a value: both are left as they are.
    /// </summary>
    /// <remarks>
    /// Whether a patched entry is new, and must then be complete (design 11.3), depends on the baseline, which a schema of
    /// a kind does not see: <c>stratarec validate</c> checks that. So a patch schema never rejects a valid patch.
    /// </remarks>
    internal static JsonObject MergePatch(JsonObject schema)
    {
        schema.Remove("required");
        AllowNull(schema);
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (_, property) in properties)
            {
                if (property is JsonObject member)
                {
                    MergePatch(member);
                }
            }
        }

        if (schema["additionalProperties"] is JsonObject additional)
        {
            MergePatch(additional);
        }

        foreach (var keyword in Applicators)
        {
            if (schema[keyword] is JsonArray alternatives)
            {
                foreach (var alternative in alternatives)
                {
                    if (alternative is JsonObject subschema)
                    {
                        MergePatch(subschema);
                    }
                }
            }
        }

        foreach (var keyword in Branches)
        {
            if (schema[keyword] is JsonObject branch)
            {
                MergePatch(branch);
            }
        }

        return schema;
    }

    private static readonly string[] Applicators = { "allOf", "anyOf", "oneOf" };

    private static readonly string[] Branches = { "then", "else" };

    private static bool IsWorkspaceKind(string kind)
    {
        foreach (var workspaceKind in WorkspaceKinds)
        {
            if (workspaceKind == kind)
            {
                return true;
            }
        }

        return false;
    }

    private static OperatorSchemaSource[] Sorted(IReadOnlyList<OperatorSchemaSource> operators)
    {
        var sorted = new OperatorSchemaSource[operators.Count];
        for (var i = 0; i < sorted.Length; i++)
        {
            sorted[i] = operators[i];
        }

        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return sorted;
    }

    /// <summary>
    /// A schema made a document: the dialect and title first; for the kind of a file a person edits
    /// (<paramref name="editable"/>), the file's <c>$schema</c> and JSONC.
    /// </summary>
    private static JsonObject Document(JsonObject schema, string title, string? editable)
    {
        schema.Insert(0, SchemaMember, Dialect);
        schema.Insert(1, "title", title);
        if (editable is null)
        {
            return schema;
        }

        schema.Insert(2, "allowComments", true);
        schema.Insert(3, "allowTrailingCommas", true);
        if (schema["properties"] is JsonObject properties)
        {
            properties.Insert(0, SchemaMember, new JsonObject
            {
                ["description"] = $"The JSON Schema of this file, for editors: a path relative to the file (e.g. ../{DirectoryName}/{FileName(editable)}, " +
                                  $"as 'stratarec schema --out {DirectoryName}' writes them) or a URL. The engine ignores it: it is not part of the " +
                                  "file's content id.",
                ["type"] = "string",
            });
        }

        return schema;
    }

    private static JsonObject Pipeline(OperatorSchemaSource[] operators) => Export(typeof(PipelineArtifact), (context, schema) =>
    {
        schema = Describe(context, schema);
        if (context.TypeInfo.Type == typeof(NodeSpec) && schema is JsonObject node)
        {
            AddOperators(node, operators);
        }

        return schema;
    }, inline: true);

    /// <summary>
    /// A node's <c>op</c> lists the operators, and its <c>config</c> is checked against the one <c>op</c> names: one
    /// <c>if</c>/<c>then</c> per operator, the construct VS Code's JSON service follows for completion, hover and errors
    /// (it does not narrow a <c>oneOf</c> by a <c>const</c> member). An unknown <c>op</c> fails the enum, and
    /// <c>stratarec validate</c> says which operator was meant.
    /// </summary>
    private static void AddOperators(JsonObject node, OperatorSchemaSource[] operators)
    {
        if (node["properties"] is not JsonObject properties || properties["op"] is not JsonObject op)
        {
            throw new InvalidOperationException("The schema of a node has no 'op'.");
        }

        if (operators.Length == 0)
        {
            return;
        }

        var ids = new JsonArray();
        var descriptions = new JsonArray();
        var branches = new JsonArray();
        foreach (var source in operators)
        {
            ids.Add(source.Id);
            descriptions.Add(source.Description);
            var config = Config(source);
            var then = new JsonObject { ["properties"] = new JsonObject { ["config"] = config } };
            if (config["required"] is JsonArray { Count: > 0 })
            {
                then["required"] = new JsonArray("config"); // without one, the config is {} and misses what it requires
            }
            else
            {
                config["type"] = new JsonArray("object", "null"); // the engine reads a missing or null config as {}
            }

            branches.Add(new JsonObject
            {
                ["if"] = new JsonObject
                {
                    ["properties"] = new JsonObject { ["op"] = new JsonObject { ["const"] = source.Id } },
                    ["required"] = new JsonArray("op"),
                },
                ["then"] = then,
            });
        }

        op["enum"] = ids;
        op["enumDescriptions"] = descriptions;
        node["allOf"] = branches;
    }

    private static JsonObject Config(OperatorSchemaSource source)
    {
        var config = Export(source.ConfigType, Describe, inline: true);
        if (!config.ContainsKey("description"))
        {
            config.Insert(0, "description", source.Description);
        }

        return config;
    }

    /// <summary>The experiment: each arm is a merge patch of the patchable view (design 11.3, 11.4).</summary>
    private static JsonObject Experiment(OperatorSchemaSource[] operators)
    {
        var experiment = Export(typeof(ExperimentArtifact), Describe, inline: true);
        var pipeline = Member(Pipeline(operators), "properties");
        var catalog = Member(Export(typeof(FeatureCatalogArtifact), Describe, inline: true), "properties");
        var sections = new JsonObject();
        AddSections(sections, pipeline, ExperimentArtifact.PipelineSections);
        AddSections(sections, catalog, ExperimentArtifact.CatalogSections);
        Member(Member(experiment, "properties"), "arms")["additionalProperties"] = new JsonObject
        {
            ["description"] = "An RFC 7396 merge patch of the pipeline and catalog: an object changes only the members it names, null deletes, " +
                              "an array is replaced whole. A new node or entry must be complete; 'stratarec validate' checks what the " +
                              "patch does to the baseline.",
            ["type"] = "object",
            ["properties"] = sections,
            ["additionalProperties"] = false,
        };
        return experiment;

        static void AddSections(JsonObject sections, JsonObject properties, IReadOnlyList<string> names)
        {
            foreach (var name in names)
            {
                // A section itself is never deleted or replaced: its entries are patched.
                var section = (JsonObject)Member(properties, name).DeepClone();
                if (section["additionalProperties"] is JsonObject entry)
                {
                    MergePatch(entry);
                }

                sections[name] = section;
            }
        }
    }

    private static JsonObject Member(JsonObject schema, string name) =>
        schema[name] as JsonObject ?? throw new InvalidOperationException($"The schema has no '{name}'.");

    private static void AllowNull(JsonObject schema)
    {
        switch (schema["type"])
        {
            case JsonValue single when single.GetValue<string>() is var type && type != "null":
                schema["type"] = new JsonArray(type, "null");
                break;
            case JsonArray types when !Contains(types, "null"):
                types.Add("null");
                break;
        }

        if (schema.TryGetPropertyValue("const", out var constant))
        {
            schema.Remove("const");
            schema["enum"] = new JsonArray(constant, null);
        }
        else if (schema["enum"] is JsonArray values && !Contains(values, null))
        {
            values.Add(null);
        }
    }

    private static bool Contains(JsonArray array, string? value)
    {
        foreach (var item in array)
        {
            if (value is null ? item is null : item is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == value)
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject Export(Type type, Func<JsonSchemaExporterContext, JsonNode, JsonNode> transform, bool inline)
    {
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(ArtifactSerializer.Options, type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = transform,
        });
        if (node is not JsonObject schema)
        {
            throw new InvalidOperationException($"The schema of {type} is not an object.");
        }

        return inline ? Inline(schema) : schema;
    }

    /// <summary>
    /// Replaces each <c>$ref</c> the exporter writes for a type it already described (a JSON pointer into the same
    /// schema) with a copy of what it points at, so that a schema can be placed inside another (a config in a node, a
    /// section in an arm) and patched without a pointer going stale. A type that contains itself is described to its
    /// first repetition, below which any value is accepted.
    /// </summary>
    private static JsonObject Inline(JsonObject schema)
    {
        var original = schema.DeepClone();
        return (JsonObject)Inline(schema, original, new List<string>());
    }

    private static JsonNode Inline(JsonNode node, JsonNode original, List<string> resolving)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var target) && target.StartsWith('#'))
            {
                var resolved = new JsonObject();
                if (!resolving.Contains(target) && JsonPointer.TryParse(Uri.UnescapeDataString(target[1..]), out var pointer)
                    && pointer.TryEvaluate(original, out var found) && found is JsonObject definition)
                {
                    resolving.Add(target);
                    resolved = (JsonObject)Inline(definition.DeepClone(), original, resolving);
                    resolving.RemoveAt(resolving.Count - 1);
                }

                foreach (var (key, value) in obj)
                {
                    if (key != "$ref")
                    {
                        resolved[key] = value?.DeepClone(); // what the referring property adds (its description) wins
                    }
                }

                return resolved;
            }

            var keys = new List<string>(obj.Count);
            foreach (var (key, _) in obj)
            {
                keys.Add(key);
            }

            foreach (var key in keys)
            {
                if (obj[key] is { } child && Inline(child, original, resolving) is var inlined && !ReferenceEquals(inlined, child))
                {
                    obj[key] = inlined;
                }
            }

            return obj;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is { } item && Inline(item, original, resolving) is var inlined && !ReferenceEquals(inlined, item))
                {
                    array[i] = inlined;
                }
            }
        }

        return node;
    }

    /// <summary>
    /// Adds a member's description (its <see cref="DescriptionAttribute"/>, else its XML documentation summary), a type's
    /// <see cref="DescriptionAttribute"/>, and a property's <see cref="AllowedValuesAttribute"/> and
    /// <see cref="RegularExpressionAttribute"/> as <c>enum</c> and <c>pattern</c>, so the schema says what the engine
    /// accepts without a hand-written copy.
    /// </summary>
    private static JsonNode Describe(JsonSchemaExporterContext context, JsonNode schema)
    {
        // A type with [JsonExtensionData] takes members it does not declare (a connector's or update source's own
        // settings): the exporter marks it closed because unknown members are otherwise rejected.
        if (schema is JsonObject open && HasExtensionData(context.TypeInfo.Type))
        {
            open.Remove("additionalProperties");
        }

        string? description = null;
        object?[]? allowed = null;
        string? pattern = null;
        if (context.PropertyInfo is { } property)
        {
            if (property.AttributeProvider is MemberInfo member)
            {
                description = MemberDocs.Describe(member, ArtifactSerializer.Options.PropertyNamingPolicy);
            }

            foreach (var attribute in property.AttributeProvider?.GetCustomAttributes(inherit: true) ?? Array.Empty<object>())
            {
                switch (attribute)
                {
                    case AllowedValuesAttribute a:
                        allowed = a.Values;
                        break;
                    case RegularExpressionAttribute r:
                        pattern = r.Pattern;
                        break;
                }
            }
        }
        else if (context.TypeInfo.Type.GetCustomAttribute<DescriptionAttribute>(inherit: true) is { Description: { Length: > 0 } text })
        {
            description = text;
        }

        if (description is null && allowed is null && pattern is null)
        {
            return schema;
        }

        var described = schema as JsonObject ?? new JsonObject(); // `true` (any value) becomes {} with a description
        if (allowed is not null)
        {
            var values = new JsonArray();
            foreach (var value in allowed)
            {
                values.Add(JsonValue.Create(value?.ToString()));
            }

            if (described["type"] is JsonArray types && Contains(types, "null"))
            {
                values.Add(null); // an optional setting may be written as null, which the engine reads as absent
            }

            described["enum"] = values;
        }

        if (pattern is not null)
        {
            described["pattern"] = pattern;
        }

        if (description is not null)
        {
            described.Remove("description");
            described.Insert(0, "description", description);
        }

        return described;
    }

    private static bool HasExtensionData(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.IsDefined(typeof(JsonExtensionDataAttribute), inherit: true))
            {
                return true;
            }
        }

        return false;
    }
}
