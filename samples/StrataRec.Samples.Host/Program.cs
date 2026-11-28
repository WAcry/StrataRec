using StrataRec.Artifacts.Storage;
using StrataRec.CommandLine;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;
using StrataRec.Samples.Host;

// A web service that embeds the StrataRec engine. The engine is a library: transport, authentication and threading are
// the host's. What the engine runs is configuration in a workspace (config/), whose bindings and pipeline
// name this binary's own components by type next to the built-in ones (SampleEnvironment).

// The stratarec commands, run with this binary's components: the stock tool does not know them, so it rejects this
// workspace.
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

    // Releases: with StrataRec:Store set, the service serves what is published to that store, as a production node
    // does, and polls for what is published next; config/ is then only where the bindings' relative paths resolve.
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

var app = builder.Build();

app.MapPost("/recommend/{pipeline}", RecommendEndpoint.HandleAsync);

// Liveness and readiness: AddStrataRec loaded every plan before the server started listening, so a service that answers
// is ready. A production host splits the two and also reports ready only after warming the process up.
app.MapGet("/healthz", (IRecEngine engine) => engine.PipelineIds() is { Count: > 0 } pipelines
    ? Results.Ok(new { status = "ok", pipelines })
    : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No pipeline is served"));

app.Run();
return 0;
