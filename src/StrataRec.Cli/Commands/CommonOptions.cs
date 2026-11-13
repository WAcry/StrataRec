using System.CommandLine;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Cache;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;
using StrataRec.Redis;

namespace StrataRec.Cli.Commands;

/// <summary>Options shared by commands that read a workspace.</summary>
internal static class CommonOptions
{
    public static Option<string> Config() => new("--config", "-c")
    {
        Description = "Configuration directory (policy.jsonc, catalog/, bindings/, pipelines/, experiments/, allocation/, tables/).",
        DefaultValueFactory = _ => "config",
    };

    public static Option<string> Environment() => new("--env", "-e")
    {
        Description = "Environment: selects allocation/<pipeline>.<env>.jsonc and bindings/<env>.jsonc.",
        DefaultValueFactory = _ => "local",
    };

    public static Option<string?> Pipeline() => new("--pipeline", "-p")
    {
        Description = "Pipeline id; defaults to every pipeline (or the only one).",
    };

    public static Option<string> Format() => new Option<string>("--format", "-f")
    {
        Description = "Output format.",
        DefaultValueFactory = _ => "text",
    }.AcceptOnlyFromAmong("text", "json");

    public static JsonWriterOptions JsonOptions => new() { Indented = true };

    /// <summary>The engine environment for a workspace: fixture paths resolve against the config root.</summary>
    public static PlanEnvironment PlanEnvironment(ConfigWorkspace workspace) => PlanEnvironment(workspace.Root);

    /// <summary>The engine environment for a config root: what this binary provides (the built-in registries and
    /// <c>redis@1</c> for distributed caches), with relative paths resolving against the root.</summary>
    public static PlanEnvironment PlanEnvironment(string configRoot) => new()
    {
        BaseDirectory = Path.GetFullPath(configRoot),
        CacheStores = new FeatureCacheStoreRegistry().AddRedis(),
    };

    /// <summary>Loads a workspace and compiles one pipeline (with its experiments).</summary>
    public static (ConfigWorkspace Workspace, PlanCompileRequest? Request, PlanCompilation? Compilation) Compile(
        string config, string environment, string? pipeline, DiagnosticBag diagnostics)
    {
        var workspace = ConfigWorkspace.Load(config, diagnostics);
        if (diagnostics.HasErrors)
        {
            return (workspace, null, null);
        }

        var id = pipeline ?? SinglePipeline(workspace, diagnostics);
        if (id is null)
        {
            return (workspace, null, null);
        }

        var request = workspace.CreateCompileRequest(id, environment, diagnostics, DateOnly.FromDateTime(DateTime.UtcNow));
        if (request is null)
        {
            return (workspace, null, null);
        }

        var compilation = PlanCompiler.Compile(request, PlanEnvironment(workspace));
        diagnostics.AddRange(compilation.Diagnostics);
        ValidateAssignment(workspace, request, compilation, environment, diagnostics);
        return (workspace, request, compilation);
    }

    /// <summary>
    /// Runs the same traffic-allocation checks serving runs at load (ranges, overlaps, units, unknown arms), so a
    /// release that serving would reject never passes validate or build.
    /// </summary>
    public static void ValidateAssignment(ConfigWorkspace workspace, PlanCompileRequest request, PlanCompilation compilation, string environment, DiagnosticBag diagnostics)
    {
        if (compilation.Plan is not { } plan || request.Assignment is not { } assignment)
        {
            return;
        }

        var label = workspace.Allocations.TryGetValue(request.Pipeline.PipelineId + "/" + environment, out var allocation) ? allocation.Path : "allocation";
        Experiments.Assignment.TrafficAssigner.Create(assignment, plan, workspace.ArmRanges(request.Pipeline.PipelineId, environment), diagnostics, label);
    }

    private static string? SinglePipeline(ConfigWorkspace workspace, DiagnosticBag diagnostics)
    {
        if (workspace.Pipelines.Count == 1)
        {
            foreach (var (id, _) in workspace.Pipelines)
            {
                return id;
            }
        }

        diagnostics.Error("PIPELINE_REQUIRED", "workspace", "/pipelines",
            workspace.Pipelines.Count == 0 ? $"No pipelines in {workspace.Root}." : "Several pipelines exist; choose one.",
            "pass --pipeline <id>");
        return null;
    }
}
