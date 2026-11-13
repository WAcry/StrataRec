using System.CommandLine;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Cli.Commands;

/// <summary>
/// <c>stratarec schema</c>: JSON Schema (2020-12) of every artifact and operator config, generated from the C# types
/// that deserialize them (design 9.1), so editors and agents validate against exactly what the engine accepts.
/// </summary>
internal static class SchemaCommand
{
    private static readonly (string Name, Type Type)[] Artifacts =
    {
        ("pipeline", typeof(PipelineArtifact)),
        ("catalog", typeof(FeatureCatalogArtifact)),
        ("bindings", typeof(SourceBindingArtifact)),
        ("experiment", typeof(ExperimentArtifact)),
        ("release", typeof(ReleaseArtifact)),
        ("policy", typeof(PolicyArtifact)),
        ("table", typeof(DataTableArtifact)),
        ("manifest", typeof(ReleaseManifest)),
        ("replay_bundle", typeof(ReplayBundle)),
    };

    public static Command Create()
    {
        var kind = new Argument<string>("kind") { Description = "pipeline, catalog, bindings, experiment, release, policy, table, manifest, replay_bundle, or operator." };
        var op = new Argument<string?>("operator") { Description = "Operator id for 'operator', e.g. TopK@1.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("schema", "Print the JSON Schema of an artifact type or an operator's config.") { kind, op };
        command.SetAction(parse => Run(parse.GetValue(kind)!, parse.GetValue(op), Console.Out, Console.Error));
        return command;
    }

    internal static int Run(string kind, string? op, TextWriter output, TextWriter error)
    {
        Type? type = null;
        string title;
        if (kind == "operator")
        {
            var operators = new PlanEnvironment().Operators;
            if (op is null || !operators.TryGet(op, out var factory))
            {
                error.WriteLine($"Unknown operator '{op}'. Known: {string.Join(", ", operators.Ids)}");
                return 2;
            }

            type = factory.Descriptor.ConfigType;
            title = $"{factory.Descriptor.Id} config";
        }
        else
        {
            foreach (var (name, t) in Artifacts)
            {
                if (name == kind)
                {
                    type = t;
                }
            }

            if (type is null)
            {
                var names = new List<string>();
                foreach (var (name, _) in Artifacts)
                {
                    names.Add(name);
                }

                error.WriteLine($"Unknown kind '{kind}'. Known: {string.Join(", ", names)}, operator");
                return 2;
            }

            title = type.Name;
        }

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(ArtifactSerializer.Options, type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Describe,
        });
        if (schema is JsonObject obj)
        {
            obj.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
            obj.Insert(1, "title", title);
        }

        output.WriteLine(schema.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // descriptions stay readable
        }));
        return 0;
    }

    /// <summary>
    /// Adds a type's or property's <see cref="DescriptionAttribute"/> as the schema's <c>description</c>, and a
    /// property's <see cref="AllowedValuesAttribute"/> and <see cref="RegularExpressionAttribute"/> as <c>enum</c> and
    /// <c>pattern</c>, so the schema says what the engine accepts without a hand-written copy.
    /// </summary>
    private static JsonNode Describe(JsonSchemaExporterContext context, JsonNode schema)
    {
        // A type with [JsonExtensionData] takes members it does not declare (a connector's or update source's own
        // settings): the exporter marks it closed because unknown members are otherwise rejected.
        if (schema is JsonObject open && HasExtensionData(context.TypeInfo.Type))
        {
            open.Remove("additionalProperties");
        }

        ICustomAttributeProvider? provider = context.PropertyInfo is { } property ? property.AttributeProvider : context.TypeInfo.Type;
        string? description = null;
        object?[]? allowed = null;
        string? pattern = null;
        foreach (var attribute in provider?.GetCustomAttributes(inherit: true) ?? Array.Empty<object>())
        {
            switch (attribute)
            {
                case DescriptionAttribute d:
                    description = d.Description;
                    break;
                case AllowedValuesAttribute a when context.PropertyInfo is not null:
                    allowed = a.Values;
                    break;
                case RegularExpressionAttribute r when context.PropertyInfo is not null:
                    pattern = r.Pattern;
                    break;
            }
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

            if (AllowsNull(described))
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
            described.Insert(0, "description", description);
        }

        return described;
    }

    private static bool AllowsNull(JsonObject schema)
    {
        if (schema["type"] is JsonArray types)
        {
            foreach (var type in types)
            {
                if (type?.GetValue<string>() == "null")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasExtensionData(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.IsDefined(typeof(System.Text.Json.Serialization.JsonExtensionDataAttribute), inherit: true))
            {
                return true;
            }
        }

        return false;
    }
}
