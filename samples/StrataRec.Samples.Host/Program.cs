using StrataRec.CommandLine;
using StrataRec.Hosting;
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
});

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
// - Releases. AddStrataRec serves the workspace as it was at startup; a service that takes new releases without a restart
//   loads them into the same RecEngine in the background (RecEngine.LoadAsync), which swaps plans atomically. A request
//   may then be read with one plan's request schema and run on the next: the engine checks the inputs again against the
//   plan it runs, and RecommendEndpoint answers what the engine says.

var app = builder.Build();

app.MapPost("/recommend/{pipeline}", RecommendEndpoint.HandleAsync);

// Liveness and readiness: AddStrataRec loaded every plan before the server started listening, so a service that answers
// is ready. A production host splits the two and also reports ready only after warming the process up.
app.MapGet("/healthz", (IRecEngine engine) => engine.PipelineIds() is { Count: > 0 } pipelines
    ? Results.Ok(new { status = "ok", pipelines })
    : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No pipeline is served"));

app.Run();
return 0;
