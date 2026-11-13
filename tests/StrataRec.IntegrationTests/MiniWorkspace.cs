using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>A workspace of one pipeline with only candidate sources bound, written to a temp directory and compiled.</summary>
internal static class MiniWorkspace
{
    /// <param name="candidateSources">The members of the bindings' <c>candidate_sources</c> object.</param>
    /// <param name="pipeline">The pipeline artifact; its id must be <c>p</c>.</param>
    /// <param name="files">Other files (e.g. a source's <c>data_file</c>) by path relative to the workspace root.</param>
    /// <param name="operators">The operators available; the standard library by default.</param>
    public static ExecutionPlan Compile(
        string candidateSources, string pipeline, CandidateSourceRegistry? sources = null, IReadOnlyDictionary<string, string>? files = null,
        OperatorRegistry? operators = null)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-mini-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), $$"""{ "environment": "local", "candidate_sources": { {{candidateSources}} } }""");
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
            CandidateSources = sources ?? CandidateSourceRegistry.CreateDefault(),
            Operators = operators ?? OperatorRegistry.CreateDefault(),
        };
        var compilation = PlanCompiler.Compile(request!, environment);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }
}
