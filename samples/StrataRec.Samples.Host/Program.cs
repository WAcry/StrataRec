using StrataRec.Hosting;
using StrataRec.Samples.Host;

// A web service that embeds the StrataRec engine. The engine is a library: transport, authentication and threading are
// the host's (design 3.4). What the engine runs is configuration in a workspace (config/), whose bindings and pipeline
// name this binary's own components by type next to the built-in ones (SampleEnvironment).

var builder = WebApplication.CreateBuilder(args);
var settings = SampleSettings.Read(builder.Configuration, builder.Environment.ContentRootPath);
var plans = SampleEnvironment.Create(settings.ConfigRoot);

// The same binary checks its workspace, since the stock `stratarec` CLI does not know its components (SampleTools):
//   dotnet run -- validate                  every static check, as `stratarec validate` runs them
//   dotnet run -- schema CategoryBoost@1    the JSON Schema of an operator's config
switch (args)
{
    case ["validate", ..]:
        return SampleTools.Validate(settings.ConfigRoot, settings.Environment, plans, Console.Out);
    case ["schema", ..]:
        return SampleTools.Schema(plans, args.Length > 1 ? args[1] : null, Console.Out, Console.Error);
}

builder.Services.AddSingleton(settings);
builder.Services.AddStrataRec(options =>
{
    options.ConfigRoot = settings.ConfigRoot;
    options.Environment = settings.Environment;
    options.PlanEnvironment = plans;

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
// - Event logs need nothing: the engine logs plan loads and dependency failures to the host's ILoggerFactory.
// - Releases. AddStrataRec serves the workspace as it was at startup; a service that takes new releases without a restart
//   loads them into the same RecEngine in the background (RecEngine.LoadAsync), which swaps plans atomically.

var app = builder.Build();

// AddStrataRec loads the workspace when the engine is first resolved. Resolving it here stops a configuration with errors
// at startup (a ConfigException that lists them) instead of failing the first request, and spares that request the load.
app.Services.GetRequiredService<RecEngine>();

app.MapPost("/recommend/{pipeline}", RecommendEndpoint.HandleAsync);

// Liveness and readiness: the engine serves once every plan's tables and clients have loaded, which the resolve above
// waited for. A production host splits the two and also reports ready only after warming the process up.
app.MapGet("/healthz", (RecEngine engine) => engine.PipelineIds() is { Count: > 0 } pipelines
    ? Results.Ok(new { status = "ok", pipelines })
    : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No pipeline is served"));

app.Run();
return 0;
