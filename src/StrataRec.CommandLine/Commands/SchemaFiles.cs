using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

internal sealed record SchemaFile(string Name, string Text);

internal static class SchemaFiles
{
    public const string RequestKind = "request";

    public static List<OperatorSchemaSource> Operators(PlanEnvironment environment)
    {
        var operators = new List<OperatorSchemaSource>();
        foreach (var factory in environment.Operators.Factories)
        {
            var descriptor = factory.Descriptor;
            operators.Add(new OperatorSchemaSource(descriptor.Id, descriptor.Description, descriptor.ConfigType));
        }

        return operators;
    }

    public static JsonObject? Schema(string kind, PlanEnvironment environment)
    {
        if (kind == RequestKind)
        {
            return ArtifactSchemas.File(typeof(RunCommand.RequestFile), RequestKind);
        }

        foreach (var (name, _) in ArtifactSerializer.ArtifactTypes)
        {
            if (name == kind)
            {
                return ArtifactSchemas.Artifact(kind, Operators(environment), environment.BindingSchemaSources());
            }
        }

        return null;
    }

    public static List<string> Kinds()
    {
        var kinds = new List<string>();
        foreach (var kind in ArtifactSchemas.WorkspaceKinds)
        {
            kinds.Add(kind);
        }

        kinds.Add(RequestKind);
        foreach (var (name, _) in ArtifactSerializer.ArtifactTypes)
        {
            if (!kinds.Contains(name))
            {
                kinds.Add(name);
            }
        }

        return kinds;
    }

    /// <summary>The files of <c>schemas/</c>, in a stable order.</summary>
    public static List<SchemaFile> Generate(PlanEnvironment environment)
    {
        var operators = Operators(environment);
        var bindings = environment.BindingSchemaSources();
        var files = new List<SchemaFile>();
        foreach (var kind in ArtifactSchemas.WorkspaceKinds)
        {
            files.Add(new SchemaFile(ArtifactSchemas.FileName(kind), ArtifactSchemas.ToText(ArtifactSchemas.Artifact(kind, operators, bindings))));
        }

        files.Add(new SchemaFile(ArtifactSchemas.FileName(RequestKind), ArtifactSchemas.ToText(ArtifactSchemas.File(typeof(RunCommand.RequestFile), RequestKind))));
        return files;
    }

    public static List<string> Write(string directory, IReadOnlyList<SchemaFile> files)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file.Name);
            if (File.Exists(path) && File.ReadAllText(path) == file.Text)
            {
                continue;
            }

            File.WriteAllText(path, file.Text);
            written.Add(file.Name);
        }

        return written;
    }

    /// <summary>
    /// Line ends are not compared (a checkout may turn <c>\n</c> into <c>\r\n</c>), and files of other names are left
    /// alone.
    /// </summary>
    public static void Check(string config, PlanEnvironment environment, DiagnosticBag diagnostics)
    {
        var directory = Path.Combine(config, ArtifactSchemas.DirectoryName);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var differ = new List<string>();
        var missing = new List<string>();
        foreach (var file in Generate(environment))
        {
            var path = Path.Combine(directory, file.Name);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                missing.Add(file.Name);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                differ.Add(file.Name);
                continue;
            }

            if (text.Replace("\r\n", "\n", StringComparison.Ordinal) != file.Text)
            {
                differ.Add(file.Name);
            }
        }

        if (differ.Count == 0 && missing.Count == 0)
        {
            return;
        }

        var problems = new List<string>(2);
        if (differ.Count > 0)
        {
            problems.Add($"{JsonWords.List(differ)} {(differ.Count == 1 ? "differs" : "differ")}");
        }

        if (missing.Count > 0)
        {
            problems.Add($"{JsonWords.List(missing)} {(missing.Count == 1 ? "is" : "are")} missing");
        }

        var regenerate = InitCommand.Quote(Path.Join(config, ArtifactSchemas.DirectoryName));
        diagnostics.Warning("SCHEMAS_STALE", ArtifactSchemas.DirectoryName + "/", string.Empty,
            $"The JSON Schemas editors check this workspace's files with are not the ones this binary writes: {string.Join("; ", problems)}.",
            $"write them again with the binary that validates the workspace, and commit them: stratarec schema --out {regenerate}");
    }
}
