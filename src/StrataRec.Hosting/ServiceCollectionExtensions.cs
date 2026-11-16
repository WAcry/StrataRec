using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Core.Bindings;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>Options for <see cref="ServiceCollectionExtensions.AddStrataRec"/>.</summary>
public sealed class StrataRecOptions
{
    /// <summary>
    /// Configuration directory (design 10.1 layout). A relative path resolves against the host's content root
    /// (<see cref="IHostEnvironment.ContentRootPath"/>), where appsettings.json is read from too, or against the current
    /// directory without a host environment. When the host serves <see cref="Releases"/>, no workspace is read, and this is
    /// only the directory relative paths in bindings resolve against (as when the workspace was validated), unless
    /// <see cref="PlanEnvironment"/> sets one.
    /// </summary>
    public string ConfigRoot { get; set; } = "config";

    /// <summary>
    /// Environment whose release file and bindings are used, e.g. <c>local</c> or <c>prod-us-east</c>; when the host serves
    /// <see cref="Releases"/>, the environment whose manifest pointers it reads.
    /// </summary>
    public string Environment { get; set; } = "local";

    /// <summary>
    /// Pipelines to serve; empty serves every pipeline in the workspace. Required when the host serves
    /// <see cref="Releases"/>, which have no workspace to list them, and then with the fallback pipelines they name.
    /// </summary>
    public List<string> Pipelines { get; } = new();

    /// <summary>
    /// Serve release manifests instead of the workspace (design 10.1 item 4, 10.3): set, the host reads the manifest
    /// pointer of each of <see cref="Pipelines"/> in <see cref="Environment"/> from the container's
    /// <see cref="IManifestSource"/>, the manifests and their artifacts from its <see cref="IArtifactStore"/>, loads them
    /// before it starts, and then polls for new ones. Null (the default) serves the workspace in <see cref="ConfigRoot"/>
    /// as it is at startup.
    /// </summary>
    public ReleaseOptions? Releases { get; set; }

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

/// <summary>
/// How a host serving release manifests polls for new ones (<see cref="StrataRecOptions.Releases"/>).
/// </summary>
/// <remarks>
/// <para>Every <see cref="PollInterval"/> the host reads each pipeline's pointer, and loads a manifest id it has not tried
/// yet (<see cref="RecEngine.LoadManifestAsync"/>). An outcome that trying again would not change is final for that id:
/// applied, stale, revoked, incompatible. A manifest that was rejected or could not be read is tried again after
/// <see cref="PollInterval"/>, then after twice as long, and so on up to <see cref="MaxRetryInterval"/>; a pointer that could
/// not be read is read again on the same schedule. A new id starts over.</para>
/// <para>Each pipeline is polled on its own, so a slow load or an unresponsive pointer of one pipeline never delays
/// another's release (an emergency revocation included); loads of different pipelines may run at the same time. The
/// <see cref="IReleaseStatusReporter"/> is told the outcomes after the fact, off the polling: a slow reporter delays only
/// the reports.</para>
/// <para>No last-known-good copy is kept on disk in this version (design 10.3 item 6): a host that must start while the
/// control plane is down composes an <see cref="IManifestSource"/> that falls back to a pointer baked into its image.</para>
/// </remarks>
public sealed class ReleaseOptions
{
    /// <summary>How often each pipeline's pointer is read; 10 seconds by default.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest wait before a failed manifest or pointer is tried again; 5 minutes by default.</summary>
    public TimeSpan MaxRetryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest a pointer read (<see cref="IManifestSource.GetManifestIdAsync"/>) may take: then its token is cancelled
    /// and the read counts as failed (read again on the retry schedule). 30 seconds by default.
    /// </summary>
    public TimeSpan PointerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a status report (<see cref="IReleaseStatusReporter.ReportAsync"/>) may take: then its token is cancelled
    /// and the next report waits only for the call to return. 30 seconds by default.
    /// </summary>
    public TimeSpan ReportTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock of the polls and retries; tests pass one they advance by hand. Timeouts use the system clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>Registers the engine in a host's service container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RecEngine"/> as a singleton <see cref="IRecEngine"/>, and a hosted service that loads every
    /// configured pipeline when the host starts, from a local workspace or from release manifests
    /// (<see cref="StrataRecOptions.Releases"/>). A configuration with errors stops the host there,
    /// with a <see cref="ConfigException"/> listing every error, so a bad configuration never serves traffic; load warnings
    /// go to the host's logger (event 1003). An <see cref="IAssignmentProvider"/> registered in the container asks the
    /// experimentation platform for the arms of requests that do not carry them, and an <see cref="ISecretResolver"/>
    /// resolves the <c>secret://</c> references of bindings (design 6.4) when plans load. An <see cref="ILoggerFactory"/>
    /// in the container (from <c>AddLogging</c>) receives the engine's event logs (design 13.2); without one the engine
    /// logs nothing. Metrics and spans need no registration: subscribe to the <c>StrataRec</c> meter and activity source.
    /// </summary>
    /// <remarks>
    /// <para>The load runs in the hosted services' starting phase (<see cref="IHostedLifecycleService.StartingAsync"/>),
    /// which the generic host completes for every service before it starts any, the web server of an ASP.NET Core app
    /// included, also when services start concurrently (<c>HostOptions.ServicesStartConcurrently</c>, review 33): a
    /// service takes no request until every plan has loaded, so once it listens, it is ready. A host that is not a generic
    /// host (a bare service provider) loads the workspace itself with <see cref="LoadWorkspaceAsync"/>.</para>
    /// <para>The engine serves the workspace as it was at startup. With <see cref="StrataRecOptions.Releases"/> it serves
    /// release manifests instead (design 10.3): it loads the manifest each pipeline's pointer names in the same starting
    /// phase, and a pipeline without one, or whose manifest is not applied (unreadable, incompatible, revoked, rejected), or
    /// whose plan falls back to a pipeline not served, stops the host with a <see cref="ConfigException"/> that gives each
    /// pipeline's status. Once the host has started, it polls the pointers in the background and swaps in each new manifest
    /// that loads (<see cref="ReleaseOptions"/>). The container must then hold an <see cref="IManifestSource"/> and an
    /// <see cref="IArtifactStore"/>, and may hold an <see cref="IReleaseStatusReporter"/>, which is told every outcome.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><see cref="StrataRecOptions.Releases"/> is set without
    /// <see cref="StrataRecOptions.Pipelines"/>, or with intervals that are not positive.</exception>
    public static IServiceCollection AddStrataRec(this IServiceCollection services, Action<StrataRecOptions> configure)
    {
        var options = new StrataRecOptions();
        configure(options);
        if (options.Releases is { } releases)
        {
            CheckReleases(options, releases);
        }

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
        if (options.Releases is { } served)
        {
            var pipelines = new List<string>(options.Pipelines);
            services.AddHostedService(provider => new StrataRecReleases(
                provider.GetRequiredService<RecEngine>(),
                provider.GetService<IManifestSource>(),
                provider.GetService<IArtifactStore>(),
                provider.GetService<IReleaseStatusReporter>(),
                options.Environment,
                pipelines,
                served,
                provider.GetService<ILoggerFactory>()?.CreateLogger<StrataRecReleases>() ?? NullLogger<StrataRecReleases>.Instance));
            return services;
        }

        services.AddHostedService(provider => new StrataRecStartup(
            provider.GetRequiredService<RecEngine>(),
            ConfigRoot(options, provider),
            options.Environment,
            options.Pipelines,
            provider.GetService<ILoggerFactory>()?.CreateLogger<StrataRecStartup>() ?? NullLogger<StrataRecStartup>.Instance));
        return services;
    }

    private static void CheckReleases(StrataRecOptions options, ReleaseOptions releases)
    {
        if (options.Pipelines.Count == 0)
        {
            throw new ArgumentException(
                "StrataRecOptions.Releases is set and StrataRecOptions.Pipelines is empty: a host serving releases has no workspace to list its pipelines. " +
                "Add each pipeline to serve, and the fallback pipelines they name, to Pipelines.", nameof(options));
        }

        if (releases.PollInterval <= TimeSpan.Zero || releases.MaxRetryInterval < releases.PollInterval)
        {
            throw new ArgumentException(
                $"ReleaseOptions.PollInterval ({releases.PollInterval}) must be positive, and MaxRetryInterval ({releases.MaxRetryInterval}) at least as long.",
                nameof(options));
        }

        var longest = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0); // a timer's limit
        if (releases.PointerTimeout <= TimeSpan.Zero || releases.ReportTimeout <= TimeSpan.Zero || releases.PointerTimeout > longest || releases.ReportTimeout > longest)
        {
            throw new ArgumentException(
                $"ReleaseOptions.PointerTimeout ({releases.PointerTimeout}) and ReportTimeout ({releases.ReportTimeout}) must be positive and at most {longest}.", nameof(options));
        }

        if (releases.TimeProvider is null)
        {
            throw new ArgumentException("ReleaseOptions.TimeProvider is null; leave it at TimeProvider.System, or pass a clock.", nameof(options));
        }
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

            var request = workspace.CreateCompileRequest(id, environment, diagnostics); // no expiry check: a calendar date must not stop a host starting
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
/// <remarks>
/// The load is in <see cref="StartingAsync"/>, not <see cref="StartAsync"/>: with
/// <c>HostOptions.ServicesStartConcurrently</c> the host does not wait for one service's StartAsync before the next
/// one's, so a web server registered later could listen with no plan loaded (review 33). Every StartingAsync completes
/// before any StartAsync runs, in either mode.
/// </remarks>
internal sealed partial class StrataRecStartup(
    RecEngine engine, string configRoot, string environment, IReadOnlyCollection<string> pipelines, ILogger<StrataRecStartup> logger) : IHostedLifecycleService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartingAsync(CancellationToken cancellationToken)
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
    internal static partial void LogWarning(ILogger logger, string code, string artifact, string pointer, string message, string hint);
}
