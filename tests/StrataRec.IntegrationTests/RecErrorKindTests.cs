using System.Text.Json;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// How a request ends, as a host maps it to its transport (<see cref="RecResult.ErrorKind"/>): one kind for each way, the
/// caller's mistakes checked against the plan the request runs before anything else, and a fallback pipeline given the
/// inputs it declares.
/// </summary>
public sealed class RecErrorKindTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly string _root = Workspace();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ASuccess_HasNoKind_AndAFallbackThatServed_IsASuccess()
    {
        await using var engine = Load(_root);

        var served = await engine.ExecuteAsync(Request("feed", Inputs(1001)));
        var degraded = await engine.ExecuteAsync(Request("broken", Inputs(1001, region: "us")));

        Assert.True(served.Succeeded, served.Error);
        Assert.Equal((RecErrorKind.None, null, null), (served.ErrorKind, served.Error, served.ErrorPointer));
        Assert.Equal("req", served.RequestId);
        Assert.True(degraded.Succeeded, degraded.Error);
        Assert.Equal(RecErrorKind.None, degraded.ErrorKind);
        Assert.Equal("backup", degraded.ServedBy);
        Assert.Contains("pipeline 'broken' failed", degraded.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownPipeline_IsUnknownPipeline()
    {
        await using var engine = Load(_root);

        var result = await engine.ExecuteAsync(Request("home_feed", Inputs(1001)));

        Assert.Equal((false, RecErrorKind.UnknownPipeline, "Unknown pipeline 'home_feed'."), (result.Succeeded, result.ErrorKind, result.Error));
        Assert.Null(result.ErrorPointer);
    }

    [Theory]
    [InlineData("regoin", "/inputs/regoin", "Input 'regoin' is not declared in the request_schema of pipeline 'broken'. Did you mean 'region'?")]
    [InlineData("user_id", "/inputs/user_id", "Input 'user_id' must be an integer (int64), got the string \"1001\".")]
    [InlineData(null, "/inputs/user_id", "Input 'user_id' is required by the request_schema of pipeline 'broken'. Give it an integer (int64); only an input declared nullable ('int64?') may be null or absent.")]
    public async Task InputsThePlanRejects_AreAnInvalidRequest_AtTheirPointer_BeforeAssignmentOrTheFallback(string? input, string pointer, string error)
    {
        var provider = new CountingProvider();
        await using var engine = Load(_root, provider);
        var inputs = new RequestInputs();
        switch (input)
        {
            case "regoin":
                inputs.Set("user_id", ScalarValue.FromInt64(1001)).Set("regoin", ScalarValue.FromString("us"));
                break;
            case "user_id":
                inputs.Set("user_id", ScalarValue.FromString("1001"));
                break;
        }

        // "broken" fails when it runs and has a fallback: a request that cannot run is not one for the fallback to serve.
        var result = await engine.ExecuteAsync(Request("broken", inputs));

        Assert.False(result.Succeeded);
        Assert.Equal((RecErrorKind.InvalidRequest, pointer, error), (result.ErrorKind, result.ErrorPointer, result.Error));
        Assert.Equal("broken", result.ServedBy);
        Assert.Empty(result.Items);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ForcedArmsThePlanDoesNotHave_AreAnInvalidRequest()
    {
        var host = HomeFeedTestHost.Load();
        await using var engine = new RecEngine(host.Environment);
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed", RequestId = "r", Inputs = HomeFeedTestHost.Inputs(1001, "home"), Deadline = HomeFeedTestHost.Deadline,
            Debug = new DebugOptions { ForceArms = new[] { "exp_gone/treatment" } },
        });

        Assert.Equal((false, RecErrorKind.InvalidRequest), (result.Succeeded, result.ErrorKind));
        Assert.Contains("exp_gone/treatment", result.Error, StringComparison.Ordinal);
        Assert.Null(result.ErrorPointer); // a debug option, not a member of the request body
    }

    [Fact]
    public async Task AProviderThatSpendsTheDeadline_IsDeadlineExceeded()
    {
        var provider = new CountingProvider(answers: false);
        await using var engine = Load(_root, provider);

        var result = await engine.ExecuteAsync(Request("feed", Inputs(1001), TimeSpan.FromMilliseconds(100)));

        Assert.Equal((false, RecErrorKind.DeadlineExceeded), (result.Succeeded, result.ErrorKind));
        Assert.Contains("deadline exceeded", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task APipelineThatFails_IsFailed_AndSoIsOneWhoseFallbackFails()
    {
        await using var engine = Load(_root);

        var alone = await engine.ExecuteAsync(Request("lonely", Inputs(1001)));
        var both = await engine.ExecuteAsync(Request("strict_broken", Inputs(1001)));

        Assert.Equal((false, RecErrorKind.Failed), (alone.Succeeded, alone.ErrorKind));
        Assert.Null(alone.ErrorPointer);
        Assert.Equal((false, RecErrorKind.Failed), (both.Succeeded, both.ErrorKind));

        // The fallback declares an input its primary does not: the request was valid for the pipeline it asked for, so the
        // fallback's rejection is the engine's failure to serve it (a 500), not the caller's mistake (a 400).
        Assert.Contains("fallback 'strict_backup' failed", both.Error, StringComparison.Ordinal);
        Assert.Contains("Input 'tenant_id' is required by the request_schema of pipeline 'strict_backup'", both.Error, StringComparison.Ordinal);
        Assert.Null(both.ErrorPointer);
    }

    [Fact]
    public async Task AFallback_IsGivenTheInputsItDeclares()
    {
        await using var engine = Load(_root);

        // "region" is declared by "broken" and not by "backup": the backup runs with user_id only.
        var result = await engine.ExecuteAsync(Request("broken", Inputs(1001, region: "us")));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("backup", result.ServedBy);
        Assert.Equal(new long[] { 7, 8, 9 }, result.Items.Select(i => i.Id.Int64Value));
    }

    [Fact]
    public async Task InputsReadWithOnePlan_AndRunOnTheNext_AreCheckedAgainstTheNext()
    {
        // Review 32: a host reads the body with the schema served now; a release loaded before ExecuteAsync no longer
        // declares one of the inputs. The engine's own check, against the plan it runs, is the answer: invalid, at the pointer.
        await using var engine = Load(_root);
        Assert.True(engine.TryGetRequestSchema("feed", out var before));
        using var body = JsonDocument.Parse("""{ "user_id": 1001, "tenant_id": 7 }""");
        Assert.True(before.TryRead(body.RootElement, out var inputs, out var errors), string.Join("\n", errors));

        Edit(Path.Combine(_root, "pipelines", "feed.jsonc"), "\"tenant_id\": { \"type\": \"int64?\" }, ", string.Empty);
        var reload = await ServiceCollectionExtensions.LoadWorkspaceAsync(engine, _root, "local", new[] { "feed" });
        Assert.False(reload.HasErrors, string.Join("\n", reload.Items));
        Assert.True(engine.TryGetRequestSchema("feed", out var after));
        Assert.False(after.Contains("tenant_id"));

        var result = await engine.ExecuteAsync(Request("feed", inputs));

        Assert.Equal((false, RecErrorKind.InvalidRequest, "/inputs/tenant_id"), (result.Succeeded, result.ErrorKind, result.ErrorPointer));
        Assert.Equal("Input 'tenant_id' is not declared in the request_schema of pipeline 'feed'. Declared inputs: 'user_id'.", result.Error);
    }

    [Fact]
    public async Task TheEngine_ServesItsPipelinesIdsAndRequestSchemas()
    {
        await using var engine = Load(_root);

        Assert.Equal(new[] { "backup", "broken", "feed", "lonely", "strict_backup", "strict_broken" }, engine.PipelineIds().Order(StringComparer.Ordinal));
        Assert.True(engine.TryGetRequestSchema("broken", out var schema));
        Assert.Equal("broken", schema.PipelineId);
        Assert.Equal(new[] { "region", "user_id" }, schema.Names);
        Assert.True(schema.TryGetType("region", out var region) && region.Nullable);
        Assert.False(engine.TryGetRequestSchema("home_feed", out _));
    }

    private static RecEngine Load(string root, IAssignmentProvider? provider = null)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root }, provider);
        var diagnostics = ServiceCollectionExtensions.LoadWorkspace(engine, root, "local");
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return engine;
    }

    private static RecRequest Request(string pipeline, RequestInputs inputs, TimeSpan? deadline = null) => new()
    {
        PipelineId = pipeline, RequestId = "req", Inputs = inputs, Deadline = deadline ?? Deadline,
    };

    private static RequestInputs Inputs(long userId, string? region = null)
    {
        var inputs = new RequestInputs().Set("user_id", ScalarValue.FromInt64(userId));
        return region is null ? inputs : inputs.Set("region", ScalarValue.FromString(region));
    }

    /// <summary>
    /// Pipelines over one source that serves and one that fails: <c>feed</c> serves; <c>broken</c> fails and falls back to
    /// <c>backup</c>, which declares fewer inputs; <c>lonely</c> fails with no fallback; <c>strict_broken</c> falls back to
    /// <c>strict_backup</c>, which requires an input the primary does not declare.
    /// </summary>
    private static string Workspace()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-error-kinds-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), """{ "catalog_id": "c" }""");
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), """
            { "environment": "local",
              "candidate_sources": {
                "good":   { "client": "memory_list@1", "items": [7, 8, 9] },
                "failing": { "client": "memory_list@1", "items": [1, 2, 3], "fail": true } } }
            """);
        var pipelines = new[]
        {
            ("feed", "good", null, """ "tenant_id": { "type": "int64?" }, "user_id": { "type": "int64" } """),
            ("broken", "failing", "backup", """ "user_id": { "type": "int64" }, "region": { "type": "string?" } """),
            ("backup", "good", null, """ "user_id": { "type": "int64" } """),
            ("lonely", "failing", null, """ "user_id": { "type": "int64" } """),
            ("strict_broken", "failing", "strict_backup", """ "user_id": { "type": "int64" } """),
            ("strict_backup", "good", null, """ "user_id": { "type": "int64" }, "tenant_id": { "type": "int64" } """),
        };
        foreach (var (id, source, fallback, schema) in pipelines)
        {
            var fallbackMember = fallback is null ? string.Empty : $"\"fallback_pipeline\": \"{fallback}\",";
            File.WriteAllText(Path.Combine(root, "pipelines", id + ".jsonc"), $$"""
                { "pipeline_id": "{{id}}", {{fallbackMember}}
                  "request_schema": { {{schema}} },
                  "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
                  "params": { "recall_quotas": { "{{source}}": 10 }, "k": 5 },
                  "nodes": { "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } } },
                  "output": { "limit_param": "k", "columns": { "source": "col.recall.primary_source" } } }
                """);
        }

        return root;
    }

    private static void Edit(string path, string from, string to)
    {
        var text = File.ReadAllText(path);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    /// <summary>Counts its calls; answers no arms, or never (until its token is cancelled).</summary>
    private sealed class CountingProvider(bool answers = true) : IAssignmentProvider
    {
        public int Calls;

        public async ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (!answers)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Array.Empty<string>();
        }
    }
}
