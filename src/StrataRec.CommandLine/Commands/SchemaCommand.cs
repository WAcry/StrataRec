using System.CommandLine;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec schema</c>: the JSON Schema (2020-12) of each kind of file and of each operator's config, generated from
/// the C# types that deserialize them with the operators of the binary that runs it (<see cref="ArtifactSchemas"/>,
/// design 9.1), so editors and agents validate against exactly what the engine accepts. <c>--out</c> writes the schemas
/// a workspace's files name in <c>$schema</c> (<see cref="SchemaFiles"/>).
/// </summary>
internal static class SchemaCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var kind = new Argument<string?>("kind")
        {
            Description = $"{string.Join(", ", SchemaFiles.Kinds())}, or operator. Not with --out.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var op = new Argument<string?>("operator") { Description = "Operator id for 'operator', e.g. TopK@1.", Arity = ArgumentArity.ZeroOrOne };
        var directory = new Option<string?>("--out", "-o")
        {
            Description = "Write the schema of each kind of workspace file into this directory, e.g. schemas (one <kind>.schema.json each), " +
                          "for the files to name in \"$schema\"; 'stratarec validate' warns when the ones there are not this binary's.",
        };
        var command = new Command("schema", "Print the JSON Schema of a kind of file or of an operator's config, or write every workspace file's schema with --out.")
        {
            kind, op, directory,
        };
        command.SetAction(parse =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            return (parse.GetValue(kind), parse.GetValue(directory)) switch
            {
                (null, { } path) => Write(provided, path, output, error),
                ({ } name, null) => Run(provided, name, parse.GetValue(op), output, error),
                (null, null) => Usage(error, "Give a kind (e.g. 'stratarec schema pipeline'), or --out <directory> to write every schema."),
                _ => Usage(error, "Give a kind or --out, not both: --out writes the schema of every kind."),
            };
        });
        return command;
    }

    /// <summary>Prints the schema of <paramref name="kind"/> (or of the config of operator <paramref name="op"/>), as <c>--out</c> writes it.</summary>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    internal static int Run(PlanEnvironment provided, string kind, string? op, TextWriter output, TextWriter error)
    {
        JsonObject? schema;
        if (kind == "operator")
        {
            var operators = provided.Operators;
            if (op is null || !operators.TryGet(op, out var factory))
            {
                error.WriteLine($"Unknown operator '{op}'. Known: {string.Join(", ", operators.Ids)}");
                return 2;
            }

            var descriptor = factory.Descriptor;
            schema = ArtifactSchemas.Operator(new OperatorSchemaSource(descriptor.Id, descriptor.Description, descriptor.ConfigType));
        }
        else if (SchemaFiles.Schema(kind, provided) is { } found)
        {
            schema = found;
        }
        else
        {
            error.WriteLine($"Unknown kind '{kind}'. Known: {string.Join(", ", SchemaFiles.Kinds())}, operator");
            return 2;
        }

        output.Write(ArtifactSchemas.ToText(schema));
        return 0;
    }

    /// <summary>
    /// Writes the schema of each kind of workspace file into <paramref name="directory"/> (<see cref="SchemaFiles"/>):
    /// the same bytes every time for the same binary, so a committed copy is compared with what it would write.
    /// </summary>
    internal static int Write(PlanEnvironment provided, string directory, TextWriter output, TextWriter error)
    {
        var files = SchemaFiles.Generate(provided);
        List<string> written;
        try
        {
            written = SchemaFiles.Write(directory, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"{directory}: {ex.Message}");
            return 1;
        }

        output.WriteLine(written.Count == 0
            ? $"{directory}: the {files.Count} schemas are current"
            : $"wrote {written.Count} of {files.Count} schemas to {directory}: {string.Join(", ", written)}");
        return 0;
    }

    private static int Usage(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 2;
    }
}
