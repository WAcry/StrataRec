using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddStrataRec"/> in a generic host: the workspace loads when the host starts, a
/// configuration with errors stops it, warnings go to the host's log (event 1003), and paths resolve where the host's
/// files are.
/// </summary>
public sealed class StrataRecStartupTests : IDisposable
{
    private readonly string _contentRoot = Directory.CreateTempSubdirectory("stratarec-startup-").FullName;

    public void Dispose() => Directory.Delete(_contentRoot, recursive: true);

    [Fact]
    public async Task TheHost_LoadsTheWorkspaceWhenItStarts_RelativeToItsContentRoot()
    {
        Workspace(Path.Combine(_contentRoot, "config"));
        var log = new CapturedLog();
        using var host = Build(log, options => options.ConfigRoot = "config"); // the default, relative to the content root

        var engine = host.Services.GetRequiredService<IRecEngine>();
        Assert.Empty(engine.PipelineIds()); // nothing loads before the host starts
        await host.StartAsync();

        Assert.Equal(new[] { "p" }, engine.PipelineIds());
        Assert.True(engine.TryGetRequestSchema("p", out var schema));
        Assert.Equal(new[] { "user_id" }, schema.Names);

        // The source's data_file is relative: without a BaseDirectory, it resolves against the config root.
        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "p", RequestId = "r", Inputs = new StrataRec.Core.Values.RequestInputs().Set("user_id", 1L), Deadline = TimeSpan.FromSeconds(30),
        });
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 9, 8 }, result.Items.Select(i => i.Id.Int64Value));
        Assert.Empty(log.Entries);
        await host.StopAsync();
    }

    [Fact]
    public async Task AWorkspaceWithErrors_StopsTheHost_WithEveryError()
    {
        var root = Path.Combine(_contentRoot, "config");
        Workspace(root, data: """{ "items": [9, 8] }""", topK: "\"k\": \"two\"");
        using var host = Build(new CapturedLog(), options => options.ConfigRoot = root);

        var thrown = await Assert.ThrowsAsync<ConfigException>(() => host.StartAsync());

        Assert.StartsWith($"Loading StrataRec configuration from {root} (local)", thrown.Message, StringComparison.Ordinal);
        var error = Assert.Single(thrown.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("NODE_BAD_CONFIG", "/nodes/top/config/k"), (error.Code, error.Pointer));
    }

    [Fact]
    public async Task Warnings_GoToTheHostsLog_AsEvent1003()
    {
        var root = Path.Combine(_contentRoot, "config");
        Workspace(root, op: "Warned@1");
        var log = new CapturedLog();
        var environment = new PlanEnvironment { Operators = OperatorRegistry.CreateDefault().Add(new WarnedTopK()) };
        using var host = Build(log, options =>
        {
            options.ConfigRoot = root;
            options.PlanEnvironment = environment; // its BaseDirectory is null: the config root is used
        });

        await host.StartAsync();

        var (level, eventId, message) = Assert.Single(log.Entries);
        Assert.Equal((LogLevel.Warning, 1003), (level, eventId.Id));
        Assert.Equal("Configuration warning TEST_WARNING at pipeline:p/nodes/top/config: Mind the gap. (hint: none)", message);
        Assert.Equal(new[] { "p" }, host.Services.GetRequiredService<IRecEngine>().PipelineIds());
        await host.StopAsync();
    }

    private IHost Build(CapturedLog log, Action<StrataRecOptions> configure)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = _contentRoot,
            DisableDefaults = true,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders().AddProvider(log);
        builder.Services.AddStrataRec(configure);
        return builder.Build();
    }

    /// <summary>One pipeline <c>p</c>: a source reading a data file, then a TopK (or another operator with its config).</summary>
    private static void Workspace(string root, string data = """{ "items": [8, 9] }""", string topK = "\"k\": 2", string op = "TopK@1")
    {
        foreach (var dir in new[] { "catalog", "bindings", "pipelines", "fixtures" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(root, "fixtures", "ids.json"), data);
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), """
            { "environment": "local", "candidate_sources": { "s": { "client": "memory_list@1", "data_file": "fixtures/ids.json" } } }
            """);
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "request_schema": { "user_id": { "type": "int64" } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "s": 10 }, "k": 10 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "top": { "order": 200, "op": "{{op}}", "config": { "score_col": "entity_id", {{topK}} } } },
              "output": { "limit_param": "k", "columns": { "source": "col.recall.primary_source" } } }
            """);
    }

    /// <summary><c>TopK@1</c> under another name, which warns when it compiles.</summary>
    private sealed class WarnedTopK : IOperatorFactory
    {
        private readonly TopKFactory _inner = new();

        public OperatorDescriptor Descriptor => _inner.Descriptor with { Name = "Warned" };

        public Operator? Compile(OperatorCompileContext context)
        {
            context.Warning(string.Empty, "TEST_WARNING", "Mind the gap.");
            return _inner.Compile(context);
        }
    }

    private sealed class CapturedLog : ILoggerProvider
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) =>
            categoryName.StartsWith("StrataRec.Hosting.StrataRecStartup", StringComparison.Ordinal) ? new Logger(Entries) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(List<(LogLevel, EventId, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                {
                    entries.Add((logLevel, eventId, formatter(state, exception)));
                }
            }
        }
    }
}
