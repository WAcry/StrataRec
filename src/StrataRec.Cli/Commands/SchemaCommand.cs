using System.CommandLine;
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
        ("allocation", typeof(AllocationArtifact)),
        ("policy", typeof(PolicyArtifact)),
        ("table", typeof(DataTableArtifact)),
        ("manifest", typeof(ReleaseManifest)),
    };

    public static Command Create()
    {
        var kind = new Argument<string>("kind") { Description = "pipeline, catalog, bindings, experiment, allocation, policy, table, manifest, or operator." };
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

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(ArtifactSerializer.Options, type, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        if (schema is JsonObject obj)
        {
            obj.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
            obj.Insert(1, "title", title);
        }

        output.WriteLine(schema.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
