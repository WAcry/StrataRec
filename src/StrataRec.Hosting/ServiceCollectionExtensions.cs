using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>Options for <see cref="ServiceCollectionExtensions.AddStrataRec"/>.</summary>
public sealed class StrataRecOptions
{
    /// <summary>Configuration directory (design 10.1 layout). Required.</summary>
    public string ConfigRoot { get; set; } = "config";

    /// <summary>Environment whose allocation and bindings are used, e.g. <c>local</c> or <c>prod-us-east</c>.</summary>
    public string Environment { get; set; } = "local";

    /// <summary>Pipelines to serve; empty serves every pipeline in the workspace.</summary>
    public List<string> Pipelines { get; } = new();

    /// <summary>
    /// Operators, connectors and clients; register custom ones here. Defaults to the standard library with fixture
    /// paths resolved against <see cref="ConfigRoot"/>.
    /// </summary>
    public PlanEnvironment? PlanEnvironment { get; set; }

    /// <summary>Where tap batches and trigger records go (design 8.6); null disables persistent logging.</summary>
    public Logging.ILogSink? LogSink { get; set; }

    public Logging.LogPipelineOptions LogPipeline { get; set; } = new();
}

/// <summary>Registers the engine in a host's service container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RecEngine"/> as a singleton <see cref="IRecEngine"/>, loading every configured pipeline
    /// from a local workspace on first use. Startup fails fast with <see cref="ConfigException"/> listing every
    /// error, so a bad configuration never serves traffic. An <see cref="IAssignmentProvider"/> registered in the
    /// container replaces the built-in assignment. An <see cref="ILoggerFactory"/> in the container (from
    /// <c>AddLogging</c>) receives the engine's event logs (design 13.2); without one the engine logs nothing. Metrics and
    /// spans need no registration: subscribe to the <c>StrataRec</c> meter and activity source.
    /// </summary>
    public static IServiceCollection AddStrataRec(this IServiceCollection services, Action<StrataRecOptions> configure)
    {
        var options = new StrataRecOptions();
        configure(options);
        if (options.LogSink is { } sink)
        {
            // Disposed by the container on shutdown, which drains durable batches (design 8.6 item 4).
            services.AddSingleton(provider => new Logging.LogPipeline(sink, options.LogPipeline, loggerFactory: provider.GetService<ILoggerFactory>()));
        }

        services.AddSingleton(provider =>
        {
            var environment = options.PlanEnvironment ?? new PlanEnvironment { BaseDirectory = Path.GetFullPath(options.ConfigRoot) };
            var loggerFactory = provider.GetService<ILoggerFactory>();
            var executor = new Pipeline.Execution.PipelineExecutor(logs: provider.GetService<Logging.LogPipeline>(), loggerFactory: loggerFactory);
            var engine = new RecEngine(environment, provider.GetService<IAssignmentProvider>(), executor, loggerFactory);
            LoadWorkspace(engine, options.ConfigRoot, options.Environment, options.Pipelines).ThrowIfErrors("Loading StrataRec configuration");
            return engine;
        });
        services.AddSingleton<IRecEngine>(provider => provider.GetRequiredService<RecEngine>());
        return services;
    }

    /// <summary>
    /// Loads pipelines from a workspace into <paramref name="engine"/>; returns every diagnostic (warnings included).
    /// Pipelines that fail keep their current plan, if any.
    /// </summary>
    public static DiagnosticBag LoadWorkspace(RecEngine engine, string configRoot, string environment, IReadOnlyCollection<string>? pipelines = null)
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(configRoot, diagnostics);
        if (diagnostics.HasErrors)
        {
            return diagnostics;
        }

        var ids = new List<string>();
        if (pipelines is { Count: > 0 })
        {
            ids.AddRange(pipelines);
        }
        else
        {
            ids.AddRange(workspace.Pipelines.Keys);
        }

        // A pipeline's fallback_pipeline must be served too, or the fallback silently never happens (design 5.6). A
        // pipeline whose fallback cannot be resolved is not loaded: it keeps its current plan, like any failed load.
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
        {
            if (workspace.Pipelines.TryGetValue(ids[i], out var loaded) && loaded.Value.FallbackPipeline is { } fallback && !ids.Contains(fallback))
            {
                if (workspace.Pipelines.ContainsKey(fallback))
                {
                    ids.Add(fallback);
                }
                else
                {
                    diagnostics.Error("FALLBACK_PIPELINE_UNKNOWN", loaded.Path, "/fallback_pipeline", $"Fallback pipeline '{fallback}' is not in the workspace.",
                        NameSuggestions.DidYouMean(fallback, workspace.Pipelines.Keys) ?? $"create pipelines/{fallback}.jsonc or remove fallback_pipeline");
                    unresolved.Add(ids[i]);
                }
            }
        }

        foreach (var id in ids)
        {
            if (unresolved.Contains(id))
            {
                continue;
            }

            var request = workspace.CreateCompileRequest(id, environment, diagnostics, DateOnly.FromDateTime(DateTime.UtcNow));
            if (request is null)
            {
                continue;
            }

            var result = engine.Load(request, workspace.ArmRanges(id, environment));
            diagnostics.AddRange(result.Diagnostics);
        }

        return diagnostics;
    }
}
