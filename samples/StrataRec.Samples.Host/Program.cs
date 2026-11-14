using StrataRec.Artifacts.Storage;
using StrataRec.CommandLine;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;
using StrataRec.Samples.Host;

// A web service that embeds the StrataRec engine. The engine is a library: transport, authentication and threading are
// the host's (design 3.4). What the engine runs is configuration in a workspace (config/), whose bindings and pipeline
// name this binary's own components by type next to the built-in ones (SampleEnvironment).

// The stratarec commands, run with this binary's components: the stock tool does not know them, so it rejects this
// workspace. Every command works as with the tool, e.g.
//   dotnet run -- stratarec validate -c config                every static check, as CI runs them
//   dotnet run -- stratarec schema operator CategoryBoost@1   the JSON Schema of a custom operator's config
//   dotnet run -- stratarec operators                         the built-in and this binary's components
if (args is ["stratarec", .. var command])
{
    return await StrataRecCommandLine.InvokeAsync(command, SampleEnvironment.Create());
}

var builder = WebApplication.CreateBuilder(args);
var settings = SampleSettings.Read(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(settings);

// Loads the workspace when the host starts, before the server listens: a configuration with errors stops the service
// with a ConfigException that lists them, warnings go to the log, and the first request finds every plan loaded.
builder.Services.AddStrataRec(options =>
{
    options.ConfigRoot = settings.ConfigRoot;
    options.Environment = settings.Environment;
    options.PlanEnvironment = SampleEnvironment.Create();

    // A production host also sends the funnel logs its pipelines' log_taps capture somewhere durable:
    //   options.LogSink = new KafkaLogSink(...);   // an ILogSink; batches are written off the request path

    // Releases (design 10.1, 10.3): with StrataRec:Store set, the service serves what is published to that store, as a
    // production node does, and polls for what is published next; config/ is then only where the bindings' relative
    // paths resolve. Try the whole loop from this directory:
    //   dotnet run -- stratarec build -c config -s store            validates, uploads, and prints the publish command
    //   dotnet run -- stratarec publish -s store <manifest id>
    //   dotnet run --StrataRec:Store=store                          serves it; requests answer with its manifest id
    //   dotnet run -- stratarec revoke -s store -p trending_feed exp_local_boost
    //                                                               an emergency stop: the service drops the experiment
    //                                                               within a poll, and its arm is ignored from then on
    //   dotnet run -- stratarec status -s store                     what is published, and the stops in force
    if (settings.Store is not null)
    {
        options.Releases = new ReleaseOptions { PollInterval = settings.ReleasePoll };
        options.Pipelines.AddRange(settings.Pipelines);
    }
});

// Where a node serving releases reads the pointers and the artifacts; a production node names its object store here, or
// implements IManifestSource and IArtifactStore over it.
if (settings.Store is { } store)
{
    builder.Services.AddSingleton<IManifestSource>(new FileSystemManifestSource(store));
    builder.Services.AddSingleton<IArtifactStore>(new FileSystemArtifactStore(store));
}

// What a production host adds, none of which the engine needs to run:
// - The experiment platform. Callers that know their arms pass them as "assignment"; for the others the engine asks an
//   IAssignmentProvider, within the request's deadline:
//     builder.Services.AddSingleton<IAssignmentProvider, ExperimentPlatformClient>();
// - Credentials. Bindings name secrets by reference ("secret://feature-store-token"), resolved when an instance loads:
//     builder.Services.AddSingleton<ISecretResolver, VaultSecretResolver>();
// - Metrics and traces. The engine records to the "StrataRec" meter and activity source; nothing is recorded or allocated
//   until a listener subscribes, e.g. with OpenTelemetry:
//     builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("StrataRec")).WithTracing(t => t.AddSource("StrataRec"));
// - Event logs need nothing: the engine logs plan loads, load warnings and dependency failures to the host's ILoggerFactory.
// - Releases (StrataRec:Store above). Plans then change while the service runs, atomically: a request may be read with
//   one plan's request schema and run on the next; the engine checks the inputs again against the plan it runs, and
//   RecommendEndpoint answers what it says. The node loads every pipeline's manifest before the server listens, and does
//   not start when one is not applied.

var app = builder.Build();

app.MapPost("/recommend/{pipeline}", RecommendEndpoint.HandleAsync);

// Liveness and readiness: AddStrataRec loaded every plan before the server started listening, so a service that answers
// is ready. A production host splits the two and also reports ready only after warming the process up.
app.MapGet("/healthz", (IRecEngine engine) => engine.PipelineIds() is { Count: > 0 } pipelines
    ? Results.Ok(new { status = "ok", pipelines })
    : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No pipeline is served"));

app.Run();
return 0;
