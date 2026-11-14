using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Samples.Host;

/// <summary>
/// The configuration checks of this binary: <c>dotnet run -- validate</c> and <c>dotnet run -- schema &lt;Op@1&gt;</c>.
/// </summary>
/// <remarks>
/// The stock <c>stratarec</c> CLI knows only the built-in components, so it rejects a workspace that names
/// <c>CategoryBoost@1</c> or <c>kv_store@1</c>. A binary that adds components therefore validates its workspace itself,
/// with the same Hosting APIs the CLI uses and its own <see cref="PlanEnvironment"/>: what passes here is exactly what the
/// service accepts. CI runs <c>dotnet run --project &lt;host&gt; -- validate</c> where it would run <c>stratarec validate</c>.
/// </remarks>
public static class SampleTools
{
    /// <summary>
    /// Every static check of every pipeline in the workspace (schemas, types, data flow, operator configs, binding settings,
    /// experiment patches and arm combinations), as <c>stratarec validate</c> runs them. Nothing is loaded and no I/O is
    /// done: a binding's file is opened, and a model signature that needs a loaded client checked, when the service loads
    /// the plan.
    /// </summary>
    public static IReadOnlyList<ConfigDiagnostic> Validate(string configRoot, string environment, PlanEnvironment plans)
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(configRoot, diagnostics);
        if (diagnostics.HasErrors)
        {
            return diagnostics.Items;
        }

        foreach (var (id, _) in workspace.Pipelines)
        {
            var request = workspace.CreateCompileRequest(id, environment, diagnostics, DateOnly.FromDateTime(DateTime.UtcNow));
            if (request is not null)
            {
                diagnostics.AddRange(PlanCompiler.Compile(request, plans).Diagnostics);
            }
        }

        return diagnostics.Items;
    }

    /// <summary>Prints <see cref="Validate(string, string, PlanEnvironment)"/>; exit code 1 when there are errors.</summary>
    public static int Validate(string configRoot, string environment, PlanEnvironment plans, TextWriter output)
    {
        var errors = 0;
        var warnings = 0;
        foreach (var diagnostic in Validate(configRoot, environment, plans))
        {
            output.WriteLine(diagnostic);
            errors += diagnostic.IsError ? 1 : 0;
            warnings += diagnostic.Severity == DiagnosticSeverity.Warning ? 1 : 0;
        }

        output.WriteLine($"{configRoot} ({environment}): {errors} error(s), {warnings} warning(s)");
        return errors > 0 ? 1 : 0;
    }

    /// <summary>
    /// The JSON Schema (2020-12) of an operator's config, generated from its config class with the serializer options the
    /// engine binds configs with, so it says exactly what the engine accepts; null for an operator this binary does not have.
    /// </summary>
    public static JsonNode? OperatorSchema(PlanEnvironment plans, string operatorId)
    {
        if (!plans.Operators.TryGet(operatorId, out var factory))
        {
            return null;
        }

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(ArtifactSerializer.Options, factory.Descriptor.ConfigType, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Describe,
        });
        if (schema is JsonObject root)
        {
            root.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
            root.Insert(1, "title", $"{factory.Descriptor.Id} config");
        }

        return schema;
    }

    /// <summary>Prints <see cref="OperatorSchema"/>; exit code 2 for a missing or unknown operator.</summary>
    public static int Schema(PlanEnvironment plans, string? operatorId, TextWriter output, TextWriter error)
    {
        if (operatorId is null || OperatorSchema(plans, operatorId) is not { } schema)
        {
            error.WriteLine((operatorId is null ? "Usage: schema <Name@Version>." : $"Unknown operator '{operatorId}'.") +
                            $" Known: {string.Join(", ", plans.Operators.Ids)}");
            return 2;
        }

        output.WriteLine(schema.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // descriptions stay readable
        }));
        return 0;
    }

    /// <summary>Adds a <see cref="DescriptionAttribute"/> of a config class or member as the schema's <c>description</c>.</summary>
    private static JsonNode Describe(JsonSchemaExporterContext context, JsonNode schema)
    {
        ICustomAttributeProvider? provider = context.PropertyInfo is { } property ? property.AttributeProvider : context.TypeInfo.Type;
        if (provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true) is not [DescriptionAttribute description, ..])
        {
            return schema;
        }

        var described = schema as JsonObject ?? new JsonObject(); // `true` (any value) becomes {} with a description
        described.Insert(0, "description", description.Description);
        return described;
    }
}
