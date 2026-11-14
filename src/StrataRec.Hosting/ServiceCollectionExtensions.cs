using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>Options for <see cref="ServiceCollectionExtensions.AddStrataRec"/>.</summary>
public sealed class StrataRecOptions
{
    /// <summary>
    /// Configuration directory (design 10.1 layout). A relative path resolves against the host's content root
    /// (<see cref="IHostEnvironment.ContentRootPath"/>), where appsettings.json is read from too, or against the current
    /// directory without a host environment.
    /// </summary>
    public string ConfigRoot { get; set; } = "config";

    /// <summary>Environment whose release file and bindings are used, e.g. <c>local</c> or <c>prod-us-east</c>.</summary>
    public string Environment { get; set; } = "local";

    /// <summary>Pipelines to serve; empty serves every pipeline in the workspace.</summary>
    public List<string> Pipelines { get; } = new();

    /// <summary>
    /// Operators, connectors and clients this binary provides; register custom ones here. Defaults to the standard
    /// library. Without a <see cref="PlanEnvironment.BaseDirectory"/>, relative paths in bindings resolve against the
    /// config root.
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
    /// Registers <see cref="RecEngine"/> as a singleton <see cref="IRecEngine"/>, and a hosted service that loads every
    /// configured pipeline from a local workspace when the host starts. A configuration with errors stops the host there,
    /// with a <see cref="ConfigException"/> listing every error, so a bad configuration never serves traffic; load warnings
    /// go to the host's logger (event 1003). An <see cref="IAssignmentProvider"/> registered in the container asks the
    /// experimentation platform for the arms of requests that do not carry them, and an <see cref="ISecretResolver"/>
    /// resolves the <c>secret://</c> references of bindings (design 6.4) when plans load. An <see cref="ILoggerFactory"/>
    /// in the container (from <c>AddLogging</c>) receives the engine's event logs (design 13.2); without one the engine
    /// logs nothing. Metrics and spans need no registration: subscribe to the <c>StrataRec</c> meter and activity source.
    /// </summary>
    /// <remarks>
    /// <para>Hosted services start in the order they were registered and before the web server of an ASP.NET Core app
    /// listens, so a service that calls this before it builds the app takes no request until every plan has loaded: once
    /// it listens, it is ready. A host that is not a generic host (a bare service provider) loads the workspace itself with
    /// <see cref="LoadWorkspaceAsync"/>.</para>
    /// <para>The engine serves the workspace as it was at startup; a service that takes new releases without a restart
    /// loads them into the same engine in the background (<see cref="RecEngine.LoadAsync"/>), which swaps plans atomically.</para>
    /// </remarks>
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
            var root = ConfigRoot(options, provider);
            var environment = options.PlanEnvironment ?? new PlanEnvironment();
            if (environment.BaseDirectory is null)
            {
                environment = environment with { BaseDirectory = root };
            }

            var loggerFactory = provider.GetService<ILoggerFactory>();
            var executor = new Pipeline.Execution.PipelineExecutor(logs: provider.GetService<Logging.LogPipeline>(), loggerFactory: loggerFactory);
            return new RecEngine(environment, provider.GetService<IAssignmentProvider>(), executor, loggerFactory,
                bindingOptions: new BindingHostOptions { Secrets = provider.GetService<ISecretResolver>() });
        });
        services.AddSingleton<IRecEngine>(provider => provider.GetRequiredService<RecEngine>());
        services.AddHostedService(provider => new StrataRecStartup(
            provider.GetRequiredService<RecEngine>(),
            ConfigRoot(options, provider),
            options.Environment,
            options.Pipelines,
            provider.GetService<ILoggerFactory>()?.CreateLogger<StrataRecStartup>() ?? NullLogger<StrataRecStartup>.Instance));
        return services;
    }

    /// <summary>The config root, absolute: a relative one against the host's content root.</summary>
    private static string ConfigRoot(StrataRecOptions options, IServiceProvider provider) =>
        Path.GetFullPath(options.ConfigRoot, provider.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory());

    /// <summary>
    /// Loads pipelines from a workspace into <paramref name="engine"/>; returns every diagnostic (warnings included).
    /// Pipelines that fail keep their current plan, if any. Blocks while tables and instances load: call it from a
    /// background thread, or <see cref="LoadWorkspaceAsync"/>.
    /// </summary>
    public static DiagnosticBag LoadWorkspace(RecEngine engine, string configRoot, string environment, IReadOnlyCollection<string>? pipelines = null) =>
        LoadWorkspaceAsync(engine, configRoot, environment, pipelines).GetAwaiter().GetResult();

    /// <summary>
    /// Loads pipelines from a workspace into <paramref name="engine"/>; returns every diagnostic (warnings included).
    /// Pipelines that fail keep their current plan, if any. A pipeline's <c>fallback_pipeline</c> is loaded with it.
    /// </summary>
    /// <param name="pipelines">The pipelines to load; null or empty loads every pipeline of the workspace.</param>
    /// <param name="cancellationToken">Cancels the load of the pipeline being loaded and of those not yet started.</param>
    public static async Task<DiagnosticBag> LoadWorkspaceAsync(
        RecEngine engine, string configRoot, string environment, IReadOnlyCollection<string>? pipelines = null, CancellationToken cancellationToken = default)
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

        // A pipeline's fallback_pipeline is served with it. A pipeline whose fallback cannot be resolved is not loaded: it
        // keeps its current plan, like any failed load.
        ids = workspace.WithFallbacks(ids, diagnostics, out var unresolved);

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

            var result = await engine.LoadAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics);
        }

        // Against the plans served now: a pipeline that failed to load is judged by the plan it keeps.
        workspace.CheckFallbackInputs(ids, id => engine.TryGetRequestSchema(id, out var schema) ? schema : null, diagnostics);
        return diagnostics;
    }
}

/// <summary>
/// The startup of <see cref="ServiceCollectionExtensions.AddStrataRec"/>: loads the workspace into the engine when the
/// host starts, so the host fails to start on a configuration with errors instead of failing requests, and logs what
/// loads with warnings.
/// </summary>
internal sealed partial class StrataRecStartup(
    RecEngine engine, string configRoot, string environment, IReadOnlyCollection<string> pipelines, ILogger<StrataRecStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var diagnostics = await ServiceCollectionExtensions.LoadWorkspaceAsync(engine, configRoot, environment, pipelines, cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in diagnostics.Items)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Warning)
            {
                LogWarning(logger, diagnostic.Code, diagnostic.Artifact, diagnostic.Pointer, diagnostic.Message, diagnostic.Hint ?? "none");
            }
        }

        diagnostics.ThrowIfErrors($"Loading StrataRec configuration from {configRoot} ({environment})");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask; // the container disposes the engine

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "Configuration warning {Code} at {Artifact}{Pointer}: {Message} (hint: {Hint})")]
    private static partial void LogWarning(ILogger logger, string code, string artifact, string pointer, string message, string hint);
}
