using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Services;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Entities with args (IO step 5): a service computes their features once per request from the arguments, a request input
/// or a context feature of any type, through the service's own path: its cache, the source's and its own timeout, the
/// recording and the replay. A failed argument fails the entity without a call; a failed call fails its features.
/// </summary>
public sealed class ComputedEntityEndToEndTests
{
    [Fact]
    public async Task AnEntityWithArgs_IsComputedOncePerRequest_FromItsArguments_AndItsOutputsTakeTheFeaturesTypes()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: [11, 12]));

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(service.Calls);
        Assert.Equal(0, call.BatchSize);
        Assert.Equal(0, call.Limit);
        Assert.Equal(["label", "score", "topics"], call.Outputs.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["ctr:Float32[]=0.25", "query:String[]=shoes", "recent:Int64[2]=11,12"],
            call.Inputs.Order(StringComparer.Ordinal));
        Assert.All(result.Items, item =>
        {
            Assert.Equal(0.75, item.Columns["score"].AsFloat64(), 6); // a float64 output, stored as the float32 feature holds it
            Assert.Equal("sporty", item.Columns["label"].AsString()); // a text output
            Assert.Equal([3L, 4L], item.Columns["topics"].AsInt64List().ToArray()); // int32 elements, widened
        });
    }

    [Fact]
    public async Task AMissingArgument_IsSentAsMissing_AndTheServiceStillAnswers()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(), service);

        var result = await Execute(plan, Inputs(user: 8, query: null, recent: null)); // user 8 has no record: ctr is absent

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(service.Calls);
        Assert.Equal(["ctr:Float32[]=missing", "query:String[]=missing", "recent:Int64[-1]=missing"], call.Inputs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AFailedArgument_FailsTheEntity_WithoutACall()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(users: """{ "connector": "memory@1", "data": {}, "fail": true, "timeout_ms": 1000 }"""), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        Assert.False(result.Succeeded);
        Assert.Contains("output reads a feature that failed", result.Error);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task AFailedCall_FailsItsFeatures_WhichANodeThatSkipsOnFailureGoesOnWithout()
    {
        var service = new IntentService { Fail = true };
        var plan = Compile(Workspace(readInOutput: false), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        Assert.True(result.Succeeded, result.Error);
        Assert.Single(service.Calls);
        var boost = Assert.Single(result.Outcomes, n => n.NodeId == "boost");
        Assert.Equal(NodeOutcomeKind.Failed, boost.Kind);
        Assert.Contains("source 'intent' failed: service 'intent_model': the model is down", boost.Detail);
        Assert.All(result.Items, item => Assert.True(item.Columns["s"].IsMissing)); // not written
    }

    [Fact]
    public async Task ASlowService_IsCutOffAtTheSourcesTimeout()
    {
        var service = new IntentService { DelayMs = 10_000 };
        var plan = Compile(Workspace(readInOutput: false, intentTimeoutMs: 100), service);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        var boost = Assert.Single(result.Outcomes, n => n.NodeId == "boost");
        Assert.Equal(NodeOutcomeKind.Failed, boost.Kind);
        Assert.Contains("source 'intent' timed out", boost.Detail);
    }

    [Theory]
    [InlineData("string", "output 'score' of String values, which a float32 feature cannot take")]
    [InlineData("two", "output 'score' with 2 values; a float32 feature takes one")]
    public async Task AnOutputTheFeatureCannotTake_FailsTheCall(string answer, string error)
    {
        var service = new IntentService { Score = answer };
        var plan = Compile(Workspace(readInOutput: false), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        var boost = Assert.Single(result.Outcomes, n => n.NodeId == "boost");
        Assert.Equal(NodeOutcomeKind.Failed, boost.Kind);
        Assert.Contains(error, boost.Detail);
    }

    /// <summary>A service's cache keys the answer by the arguments: requests with the same ones share it.</summary>
    [Fact]
    public async Task TheServicesCache_AnswersRequestsWithTheSameArguments()
    {
        var service = new IntentService();
        var root = Workspace(serviceCache: """, "cache": { "ttl_ms": 600000, "max_bytes": "1MiB" }""");
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(service) });
        Assert.True(engine.Load(request!).Applied);

        async Task<int> CallsAfter(string query)
        {
            var result = await engine.ExecuteAsync(new RecRequest
            {
                PipelineId = "p", RequestId = "r-" + query, Inputs = Inputs(user: 7, query: query, recent: null), Deadline = TimeSpan.FromSeconds(20),
            }, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Error);
            return service.Calls.Count;
        }

        Assert.Equal(1, await CallsAfter("shoes"));
        for (var attempt = 0; await CallsAfter("shoes") > 1; attempt++)
        {
            Assert.True(attempt < 2_000, "no request was answered from the cache"); // the write-back is applied in the background
            await Task.Delay(5, TestContext.Current.CancellationToken);
            service.Forget(1);
        }

        Assert.Equal(2, await CallsAfter("hats"));
        Assert.Equal(["query:String[]=shoes", "query:String[]=hats"], service.Calls.Select(c => c.Inputs.Single(i => i.StartsWith("query", StringComparison.Ordinal))));
    }

    /// <summary>
    /// Values past the TTL that the service's cache answers with, when the service fails, degrade the entity's features
    /// (and the node reading them), as a connector's partial answer does; a replay of the request is identical.
    /// </summary>
    [Fact]
    public async Task AStaleAnswerOfTheServicesCache_DegradesItsFeatures_AndReplaysAsServed()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new IntentService();
        var clock = new MovableClock();
        var root = Workspace(readInOutput: false, serviceCache: """, "cache": { "ttl_ms": 1000, "max_bytes": "1MiB", "stale_if_error_ms": 60000 }""");
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(service), Clock = clock });
        Assert.True(engine.Load(request!).Applied);
        RecRequest Request(string id) => new()
        {
            PipelineId = "p", RequestId = id, Inputs = Inputs(user: 7, query: "shoes", recent: null), Deadline = TimeSpan.FromSeconds(20),
            Debug = new DebugOptions { Record = true },
        };

        for (var attempt = 0; ; attempt++)
        {
            var calls = service.Calls.Count;
            Assert.True((await engine.ExecuteAsync(Request("warm-" + attempt), token)).Succeeded);
            if (service.Calls.Count == calls)
            {
                break; // answered from the cache
            }

            Assert.True(attempt < 2_000, "no request was answered from the cache");
            await Task.Delay(5, token);
        }

        clock.Advance(2_000); // past the TTL, within stale_if_error_ms
        service.Fail = true;
        var served = await engine.ExecuteAsync(Request("stale"), token);

        Assert.True(served.Succeeded, served.Error);
        var boost = Assert.Single(served.Recording!.Runs[0].Nodes, n => n.Node == "boost");
        Assert.Equal("degraded", boost.Outcome);
        var replay = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(served.Recording)), cancellationToken: token);
        Assert.True(replay.Report.Diff.Identical, ReplayTests.Describe(replay.Report));
        Assert.All(replay.Report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
    }

    /// <summary>
    /// The call is recorded as the source's (no node), in role fetch, and a replay answers it from the recording without
    /// calling the service; a replay whose arguments changed has no answer for it: a gap, and the features fail.
    /// </summary>
    [Fact]
    public async Task ARecordedCall_IsTheSourcesAndReplaysAsRecorded_AndChangedArgumentsAreAGap()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(), service);
        var live = await Execute(plan, Inputs(user: 7, query: "shoes", recent: [11]), record: true);
        Assert.True(live.Succeeded, live.Error);

        var recorded = Assert.Single(live.Recording!.ServiceCalls, c => c.Service == "intent_model");
        Assert.Equal((null, "intent", IoRoles.Fetch, "ok"), (recorded.Node, recorded.Source, recorded.Role, recorded.Status));

        var replay = RunReplay.Create(live.Recording, "/runs/0");
        var again = await Execute(plan, Inputs(user: 7, query: "shoes", recent: [11]), replay: replay);
        Assert.True(again.Succeeded, again.Error);
        Assert.Empty(replay.Gaps);
        Assert.Single(service.Calls); // the live run's
        Assert.Equal(live.Items.Select(i => i.Columns["label"].AsString()), again.Items.Select(i => i.Columns["label"].AsString()));

        var changed = RunReplay.Create(live.Recording, "/runs/0");
        var other = await Execute(plan, Inputs(user: 7, query: "hats", recent: [11]), replay: changed);
        Assert.False(other.Succeeded);
        var gap = Assert.Single(changed.Gaps);
        Assert.Equal((IoRoles.Fetch, "intent_model", "inputs_changed"), (gap.Kind, gap.Source, gap.Reason));
        Assert.Single(service.Calls);
    }

    [Theory]
    [InlineData("""{ "key": "ctx.request.user_id", "args": { "q": "ctx.request.query" } }""", "ENTITY_BAD_ARGS", "/entities/intent", "both a key and args")]
    [InlineData("""{ }""", "ENTITY_BAD_KEY", "/entities/intent", "neither a key nor args")]
    [InlineData("""{ "args": { } }""", "ENTITY_BAD_ARGS", "/entities/intent/args", "has no args")]
    [InlineData("""{ "args": { "q": "ctx.request.qeury" } }""", "ENTITY_BAD_ARGS", "/entities/intent/args/q", "'qeury' is not declared")]
    [InlineData("""{ "args": { "q": "col.entity_id" } }""", "ENTITY_BAD_ARGS", "/entities/intent/args/q", "a candidate value")]
    [InlineData("""{ "args": { "q": "ctx.user.nope" } }""", "ENTITY_BAD_ARGS", "/entities/intent/args/q", "not a declared feature")]
    [InlineData("""{ "args": { "q": "ctx.item.pop" } }""", "ENTITY_BAD_ARGS", "/entities/intent/args/q", "a candidate feature")]
    public void AnEntitysArgs_AreCheckedAtTheirPointer(string entity, string code, string pointer, string message)
    {
        var diagnostics = Diagnostics(Workspace(intent: entity));

        Assert.Contains(diagnostics, d => d.Code == code && d.Pointer == pointer && d.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "mode": "service" }""", "SOURCE_SERVICE_MISSING", "/sources/intent", "names no 'service'")]
    [InlineData("""{ "mode": "service", "service": "intent_modle" }""", "SERVICE_UNBOUND", "/sources/intent/service", "has no binding")]
    [InlineData("""{ "mode": "service", "service": "intent_model", "connector": "memory@1" }""", "SOURCE_SETTING_WRONG_MODE", "/sources/intent/connector", "mode 'fetch' and 'cache'")]
    [InlineData("""{ "mode": "service", "service": "intent_model", "max_bytes": "1MiB" }""", "SOURCE_SETTING_WRONG_MODE", "/sources/intent/max_bytes", "is bound with mode 'service'")]
    [InlineData("""{ "connector": "memory@1", "data": {} }""", "SOURCE_SERVICE_ENTITY", "/sources/intent", "a service computes its features")]
    public void ASourceInModeService_IsChecked_AndOnlyAServiceComputesAnEntityWithArgs(string binding, string code, string pointer, string message)
    {
        var diagnostics = Diagnostics(Workspace(intentSource: binding));

        Assert.Contains(diagnostics, d => d.Code == code && d.Pointer == pointer && d.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyedEntitysSource_IsNotAService()
    {
        var diagnostics = Diagnostics(Workspace(users: """{ "mode": "service", "service": "intent_model" }"""));

        Assert.Contains(diagnostics, d => d.Code == "SOURCE_SERVICE_ENTITY" && d.Pointer == "/sources/users/service" && d.Message.Contains("entity 'user' has a key"));
    }

    /// <summary>An argument that leads back to its entity is a cycle, as a key part that does.</summary>
    [Fact]
    public void AnArgumentLeadingBackToItsEntity_IsAKeyCycle()
    {
        var diagnostics = Diagnostics(Workspace(
            intent: """{ "args": { "q": "ctx.other.x" } }""",
            extraEntities: """, "other": { "key": "ctx.intent.label" }""",
            extraFeatures: """, "other.x": { "type": "string", "source": "users", "field": "x" }"""));

        Assert.Contains(diagnostics, d => d.Code == "ENTITY_KEY_CYCLE" && d.Message.Contains("leads to a key cycle", StringComparison.Ordinal));
    }

    private static RequestInputs Inputs(long user, string? query, long[]? recent)
    {
        var inputs = new RequestInputs().Set("user_id", user);
        if (query is not null)
        {
            inputs.Set("query", query);
        }

        if (recent is not null)
        {
            inputs.Set("recent", recent);
        }

        return inputs;
    }

    private static ValueTask<PipelineResult> Execute(ExecutionPlan plan, RequestInputs inputs, bool record = false, RunReplay? replay = null) =>
        new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = inputs,
            Deadline = replay is null ? TimeSpan.FromSeconds(20) : RunReplay.Deadline,
            TimeMs = 1_767_225_600_000,
            Record = record,
            Replay = replay,
        }, TestContext.Current.CancellationToken);

    private static ExecutionPlan Compile(string root, IntentService service)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(service) });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>Every diagnostic of loading and compiling the workspace.</summary>
    private static List<ConfigDiagnostic> Diagnostics(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        var all = new List<ConfigDiagnostic>(diagnostics.Items);
        if (request is not null)
        {
            all.AddRange(PlanCompiler.Compile(request, new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(new IntentService()) }).Diagnostics);
        }

        return all;
    }

    /// <summary>
    /// A workspace whose entity <c>intent</c> has args (a string and a list input, and the user's CTR, fetched first) and
    /// three features of three kinds, which the boost node and the output read.
    /// </summary>
    private static string Workspace(
        string intent = """{ "args": { "query": "ctx.request.query", "ctr": "ctx.user.ctr", "recent": "ctx.request.recent" } }""",
        string intentSource = """{ "mode": "service", "service": "intent_model", "timeout_ms": 5000 }""",
        string users = """{ "connector": "memory@1", "data": { "7": { "ctr": 0.25 } }, "timeout_ms": 5000 }""",
        string serviceCache = "",
        bool readInOutput = true,
        int? intentTimeoutMs = null,
        string extraEntities = "",
        string extraFeatures = "")
    {
        if (intentTimeoutMs is { } timeout)
        {
            intentSource = intentSource.Replace("\"timeout_ms\": 5000", $"\"timeout_ms\": {timeout}", StringComparison.Ordinal);
        }

        var root = Directory.CreateTempSubdirectory("stratarec-computed-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), $$"""
            { "catalog_id": "c",
              "entities": { "user": { "key": "ctx.request.user_id" }, "item": { "key": "col.entity_id" }, "intent": {{intent}}{{extraEntities}} },
              "features": {
                "user.ctr":      { "type": "float32", "source": "users", "field": "ctr" },
                "item.pop":      { "type": "string", "source": "users", "field": "pop" },
                "intent.score":  { "type": "float32", "source": "intent", "field": "score", "default": 0.5 },
                "intent.label":  { "type": "string", "source": "intent", "field": "label" },
                "intent.topics": { "type": "list<int64>", "source": "intent", "field": "topics" }{{extraFeatures}}
              } }
            """);
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), $$"""
            { "environment": "local",
              "sources": { "users": {{users}}, "intent": {{intentSource}} },
              "services": {
                "pool": { "client": "memory_list@1", "items": [1, 2] },
                "intent_model": { "client": "{{IntentService.TypeName}}"{{serviceCache}} } } }
            """);
        var output = readInOutput
            ? """{ "s": "col.out.s", "score": "ctx.intent.score", "label": "ctx.intent.label", "topics": "ctx.intent.topics" }"""
            : """{ "s": "col.out.s" }""";
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), $$"""
            { "pipeline_id": "p",
              "request_schema": { "user_id": { "type": "int64?" }, "query": { "type": "string?" }, "recent": { "type": "list<int64>?" } },
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "pool": 10 }, "k": 5 },
              "columns": { "out.s": { "type": "float32?" } },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
                "boost":  { "order": 200, "op": "ExpressionScorer@1", "on_failure": "skip", "config": { "output_col": "out.s", "expr": "ctx.intent.score * 2.0" } }
              },
              "output": { "limit_param": "k", "columns": {{output}} } }
            """);
        return root;
    }

    /// <summary>
    /// <c>intent_test@1</c>: records each call (its inputs as <c>name:Type[shape]=values</c>) and answers <c>score</c>
    /// 0.75 (float64), <c>label</c> "sporty" (a text) and <c>topics</c> [3, 4] (int32); or fails, or answers late.
    /// </summary>
    private sealed class IntentService : IBindingFactory<IServiceClient>, IServiceClient
    {
        public const string TypeName = "intent_test@1";

        private readonly List<(int BatchSize, int Limit, string[] Outputs, string[] Inputs)> _calls = [];

        public string Type => TypeName;

        public Type SettingsType => typeof(NoSettings);

        public bool Fail { get; set; }

        public int DelayMs { get; init; }

        /// <summary><c>number</c>, <c>string</c> (a string tensor) or <c>two</c> (two numbers).</summary>
        public string Score { get; init; } = "number";

        public List<(int BatchSize, int Limit, string[] Outputs, string[] Inputs)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public IServiceClient Create(BindingContext context) => this;

        /// <summary>Forgets the calls after the first <paramref name="kept"/>.</summary>
        public void Forget(int kept)
        {
            lock (_calls)
            {
                _calls.RemoveRange(kept, _calls.Count - kept);
            }
        }

        public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
        {
            var inputs = new List<string>();
            foreach (var input in call.Inputs.Span)
            {
                inputs.Add(Describe(input));
            }

            lock (_calls)
            {
                _calls.Add((call.BatchSize, call.Limit, call.Outputs.ToArray(), [.. inputs]));
            }

            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, cancellationToken);
            }

            if (Fail)
            {
                return ServiceStatus.Failed("the model is down");
            }

            switch (Score)
            {
                case "string":
                    response.GetValues<string>("score", 1).Span[0] = "high";
                    break;
                case "two":
                    response.GetValues<double>("score", 2).Span.Fill(0.75);
                    break;
                default:
                    response.GetValues<double>("score", 1).Span[0] = 0.75;
                    break;
            }

            response.SetText("label", "sporty");
            var topics = response.GetValues<int>("topics", 2).Span;
            topics[0] = 3;
            topics[1] = 4;
            return ServiceStatus.Ok;
        }

        private static string Describe(NamedInput input)
        {
            var head = $"{input.Name}:{input.ElementType}[{string.Join(",", input.Shape.ToArray())}]=";
            if (input.IsMissing)
            {
                return head + "missing";
            }

            return head + input.ElementType switch
            {
                TensorElementType.Float32 => string.Join(",", input.Float32.ToArray().Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                TensorElementType.Int64 => string.Join(",", input.Int64.ToArray()),
                TensorElementType.String => string.Join(",", input.String.ToArray()),
                TensorElementType.Text => input.Text,
                _ => "?",
            };
        }

        public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromException<ServiceSignature>(new NotSupportedException());
    }

    private sealed class NoSettings;

    private sealed class MovableClock : TimeProvider
    {
        private long _timeMs = 1_720_000_000_000L;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _timeMs));

        public void Advance(long milliseconds) => Interlocked.Add(ref _timeMs, milliseconds);
    }
}
