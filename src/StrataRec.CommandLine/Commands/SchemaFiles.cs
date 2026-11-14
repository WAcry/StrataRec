using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>One schema file of a workspace's <c>schemas/</c>.</summary>
/// <param name="Name">The file name, e.g. <c>pipeline.schema.json</c>.</param>
/// <param name="Text">The file's text (<see cref="ArtifactSchemas.ToText"/>).</param>
internal sealed record SchemaFile(string Name, string Text);

/// <summary>
/// The schemas a workspace's files name in <c>$schema</c>: one file per kind in <c>schemas/</c>, for every artifact kind a
/// workspace holds and <c>stratarec run</c>'s request file, with the operators of the binary that writes them. What
/// <c>stratarec schema --out</c> and <c>stratarec init</c> write, and what <c>stratarec validate</c> compares the
/// workspace's <c>schemas/</c> with.
/// </summary>
/// <remarks>
/// Release manifests and replay bundles are written by tools, not edited, and no workspace file names their schemas:
/// <c>stratarec schema manifest</c> prints one when wanted.
/// </remarks>
internal static class SchemaFiles
{
    /// <summary>The kind of <c>stratarec run</c>'s request file, which is not an artifact.</summary>
    public const string RequestKind = "request";

    /// <summary>The operators of <paramref name="environment"/>, as the schemas describe them.</summary>
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

    /// <summary>The schema of <paramref name="kind"/> (an artifact kind or <see cref="RequestKind"/>), or null for another word.</summary>
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
                return ArtifactSchemas.Artifact(kind, Operators(environment));
            }
        }

        return null;
    }

    /// <summary>The words <c>stratarec schema</c> takes besides <c>operator</c>, in the order it lists them.</summary>
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
        var files = new List<SchemaFile>();
        foreach (var kind in ArtifactSchemas.WorkspaceKinds)
        {
            files.Add(new SchemaFile(ArtifactSchemas.FileName(kind), ArtifactSchemas.ToText(ArtifactSchemas.Artifact(kind, operators))));
        }

        files.Add(new SchemaFile(ArtifactSchemas.FileName(RequestKind), ArtifactSchemas.ToText(ArtifactSchemas.File(typeof(RunCommand.RequestFile), RequestKind))));
        return files;
    }

    /// <summary>
    /// Writes <paramref name="files"/> into <paramref name="directory"/> (created if missing), each only when its text
    /// changed, and leaves other files there as they are. Returns the names of the files written.
    /// </summary>
    /// <exception cref="IOException">A file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory may not be written.</exception>
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
    /// Warns when the workspace has a <c>schemas/</c> whose files are not the ones this binary writes: editors would
    /// complete and check the workspace's files against what an older binary, or one with other operators, accepted. A
    /// warning, since what is deployed does not depend on it; CI makes it fail with <c>--warnings-as-errors</c>. Line ends
    /// are not compared (a checkout may turn <c>\n</c> into <c>\r\n</c>), and files of other names are left alone.
    /// </summary>
    /// <param name="config">The workspace directory, as the user gave it (the hint repeats it).</param>
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
                differ.Add(file.Name); // what cannot be read is not known to be current
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
