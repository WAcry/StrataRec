using System.CommandLine;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

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
    public static PlanEnvironment PlanEnvironment(ConfigWorkspace workspace) => new() { BaseDirectory = workspace.Root };

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
        return (workspace, request, compilation);
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
