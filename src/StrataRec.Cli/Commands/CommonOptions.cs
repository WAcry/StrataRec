using System.CommandLine;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Cache;
using StrataRec.Hosting.Workspace;
using StrataRec.Llm;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Redis;

namespace StrataRec.Cli.Commands;

/// <summary>Options shared by commands that read a workspace.</summary>
internal static class CommonOptions
{
    public static Option<string> Config() => new("--config", "-c")
    {
        Description = "Configuration directory (policy.jsonc, catalog/, bindings/, pipelines/, experiments/, releases/, tables/).",
        DefaultValueFactory = _ => "config",
    };

    public static Option<string> Environment() => new("--env", "-e")
    {
        Description = "Environment: selects releases/<pipeline>.<env>.jsonc and bindings/<env>.jsonc.",
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

    /// <summary>
    /// <c>-c</c> where <c>--manifest</c> is the alternative: no default value, so that giving both is an error; neither is
    /// the workspace <c>config</c>.
    /// </summary>
    public static Option<string?> ConfigOrManifest() => new("--config", "-c")
    {
        Description = "Configuration directory (policy.jsonc, catalog/, bindings/, pipelines/, experiments/, releases/, tables/); " +
                      "default 'config'. Not with --manifest.",
    };

    public static Option<string?> Manifest() => new("--manifest", "-m")
    {
        Description = "A release manifest instead of a workspace: its content id (sha256:<hex>) in --store, or a manifest file whose artifacts are in --store. " +
                      "Not with -c. The manifest names its pipeline and environment (-e does not apply); relative paths in its bindings resolve against the current directory.",
    };

    public static Option<string?> Store() => new("--store", "-s")
    {
        Description = "Artifact store directory (blobs/sha256/...) that 'stratarec build --store' uploaded manifests and artifacts to.",
    };

    /// <summary>The engine environment for a workspace: fixture paths resolve against the config root.</summary>
    public static PlanEnvironment PlanEnvironment(ConfigWorkspace workspace) => PlanEnvironment(workspace.Root);

    /// <summary>The engine environment for a config root: what this binary provides (the built-in registries,
    /// <c>redis@1</c> for distributed caches and the generative operators of StrataRec.Llm), with relative paths resolving
    /// against the root. A manifest has no root (null): its bindings are compiled, never loaded, by the commands that read
    /// manifests. <c>operators</c> and <c>schema</c> use it too, so they list what the other commands accept.</summary>
    public static PlanEnvironment PlanEnvironment(string? configRoot) => new()
    {
        BaseDirectory = configRoot is null ? null : Path.GetFullPath(configRoot),
        Operators = OperatorRegistry.CreateDefault().AddLlm(),
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

        var request = CreateRequest(workspace, environment, pipeline, diagnostics);
        if (request is null)
        {
            return (workspace, null, null);
        }

        var compilation = PlanCompiler.Compile(request, PlanEnvironment(workspace));
        diagnostics.AddRange(compilation.Diagnostics);
        return (workspace, request, compilation);
    }

    /// <summary>The compile request of <paramref name="pipeline"/> (or the workspace's only pipeline); null after reporting errors.</summary>
    public static PlanCompileRequest? CreateRequest(ConfigWorkspace workspace, string environment, string? pipeline, DiagnosticBag diagnostics)
    {
        var id = pipeline ?? SinglePipeline(workspace, diagnostics);
        return id is null ? null : workspace.CreateCompileRequest(id, environment, diagnostics, DateOnly.FromDateTime(DateTime.UtcNow));
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
