using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>A workspace of one pipeline with only recall services bound, written to a temp directory and compiled.</summary>
internal static class MiniWorkspace
{
    /// <param name="candidateSources">The members of the bindings' <c>services</c> object.</param>
    /// <param name="pipeline">The pipeline artifact; its id must be <c>p</c>.</param>
    /// <param name="files">Other files (e.g. a source's <c>data_file</c>) by path relative to the workspace root.</param>
    /// <param name="operators">The operators available; the standard library by default.</param>
    public static ExecutionPlan Compile(
        string candidateSources, string pipeline, BindingRegistry? bindings = null, IReadOnlyDictionary<string, string>? files = null,
        OperatorRegistry? operators = null)
    {
        var compilation = TryCompile(candidateSources, pipeline, bindings, files, operators);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>Like <see cref="Compile"/>, for a pipeline that may fail validation; the workspace itself must load.</summary>
    public static PlanCompilation TryCompile(
        string candidateSources, string pipeline, BindingRegistry? bindings = null, IReadOnlyDictionary<string, string>? files = null,
        OperatorRegistry? operators = null)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-mini-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), $$"""{ "environment": "local", "services": { {{candidateSources}} } }""");
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), pipeline);
        foreach (var (path, content) in files ?? new Dictionary<string, string>())
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var environment = new PlanEnvironment
        {
            BaseDirectory = root,
            Bindings = bindings ?? PlanEnvironment.DefaultBindings(),
            Operators = operators ?? OperatorRegistry.CreateDefault(),
        };
        return PlanCompiler.Compile(request!, environment);
    }
}
