using System.CommandLine;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

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

    /// <summary>
    /// The bundles a command replays: paths given as arguments (<c>stratarec replay -c feed b.json</c>, a shell glob, a
    /// directory) or with <c>-b</c>, at least one in all.
    /// </summary>
    public static (Argument<string[]> Arguments, Option<string[]> Option) Bundles(Command command, string description)
    {
        var arguments = new Argument<string[]>("bundles") { Description = description, Arity = ArgumentArity.ZeroOrMore };
        var option = new Option<string[]>("--bundle", "-b") { Description = description + " The same as giving them as arguments." };
        command.Arguments.Add(arguments);
        command.Options.Add(option);
        command.Validators.Add(result =>
        {
            if ((result.GetValue(arguments)?.Length ?? 0) + (result.GetValue(option)?.Length ?? 0) == 0)
            {
                result.AddError($"Give the bundles to replay: 'stratarec {command.Name} -c <workspace> <bundle.json or directory>...'.");
            }
        });
        return (arguments, option);
    }

    /// <summary>The bundle paths of <see cref="Bundles"/>: the arguments, then those of <c>-b</c>.</summary>
    public static string[] BundlePaths(ParseResult parse, (Argument<string[]> Arguments, Option<string[]> Option) bundles) =>
        [.. parse.GetValue(bundles.Arguments) ?? [], .. parse.GetValue(bundles.Option) ?? []];

    public static Option<string> Format() => new Option<string>("--format", "-f")
    {
        Description = "Output format.",
        DefaultValueFactory = _ => "text",
    }.AcceptOnlyFromAmong("text", "json");

    /// <summary>
    /// The CLI's JSON output: indented, and with quotes, '&lt;' and non-ASCII text as written, since it goes to a terminal or a
    /// program, never into HTML (the default encoder writes <c>'</c> as <c>\u0027</c>).
    /// </summary>
    public static JsonWriterOptions JsonOptions => new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    /// <summary>
    /// What the binary provides (<paramref name="provided"/>, <see cref="StrataRecCommandLine"/>) placed in a config root:
    /// relative paths in its bindings resolve against the root. A manifest has no root (null): its bindings are compiled,
    /// never loaded, by the commands that read manifests, and the few that read a file when created resolve against the
    /// current directory.
    /// </summary>
    public static PlanEnvironment ForRoot(PlanEnvironment provided, string? configRoot) =>
        provided with { BaseDirectory = configRoot is null ? null : Path.GetFullPath(configRoot) };

    /// <summary>Loads a workspace and compiles one pipeline (with its experiments).</summary>
    public static (ConfigWorkspace Workspace, PlanCompileRequest? Request, PlanCompilation? Compilation) Compile(
        PlanEnvironment provided, string config, string environment, string? pipeline, DiagnosticBag diagnostics)
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

        var compilation = PlanCompiler.Compile(request, ForRoot(provided, workspace.Root));
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
