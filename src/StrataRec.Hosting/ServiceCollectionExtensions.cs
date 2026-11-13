using Microsoft.Extensions.DependencyInjection;
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
}

/// <summary>Registers the engine in a host's service container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RecEngine"/> as a singleton <see cref="IRecEngine"/>, loading every configured pipeline
    /// from a local workspace on first use. Startup fails fast with <see cref="ConfigException"/> listing every
    /// error, so a bad configuration never serves traffic. An <see cref="IAssignmentProvider"/> registered in the
    /// container replaces the built-in assignment.
    /// </summary>
    public static IServiceCollection AddStrataRec(this IServiceCollection services, Action<StrataRecOptions> configure)
    {
        var options = new StrataRecOptions();
        configure(options);
        services.AddSingleton(provider =>
        {
            var environment = options.PlanEnvironment ?? new PlanEnvironment { BaseDirectory = Path.GetFullPath(options.ConfigRoot) };
            var engine = new RecEngine(environment, provider.GetService<IAssignmentProvider>());
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

        foreach (var id in ids)
        {
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
