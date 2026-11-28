using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Experiments.Assignment;
using StrataRec.Pipeline.Operators;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

public sealed class PipelineCallEndToEndTests
{
    internal const string Promoted = """
        {
          "pipeline_id": "promoted",
          "entity_key_type": "int64",
          "request_schema": { "user_id": { "type": "int64?" }, "country": { "type": "string?" }, "slots": { "type": "int64" } },
          "request_columns": { "promoted.count": { "type": "int64" } },
          "columns": { "bid": { "type": "float32", "default": 0.0 } },
          "param_schema": { "recall_quotas": { "type": "map<string,int64>", "min": 0, "max": 1000 }, "top_k": { "type": "int64", "min": 1, "max": 100 } },
          "params": { "recall_quotas": { "popular": 20 }, "top_k": 5 },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
            "bid":    { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "bid", "expr": "col.item.clicks_7d / 100.0 + ctx.request.slots" } },
            "count":  { "order": 300, "op": "RequestAggregate@1", "config": { "values": { "promoted.count": { "fn": "count" } } } }
          },
          "output": { "limit_param": "top_k", "columns": { "bid": "col.bid" }, "values": { "count": "req.promoted.count" } }
        }
        """;

    private const string Feed = """
        {
          "pipeline_id": "feed",
          "entity_key_type": "int64",
          "request_schema": { "user_id": { "type": "int64?" }, "country": { "type": "string?" } },
          "columns": { "is_promoted": { "type": "bool?" }, "promo.bid": { "type": "float32?" } },
          "param_schema": { "recall_quotas": { "type": "map<string,int64>", "min": 0, "max": 1000 }, "top_k": { "type": "int64", "min": 1, "max": 100 },
                            "promoted_k": { "type": "int64", "min": 0, "max": 100 } },
          "params": { "recall_quotas": { "two_tower": 6 }, "top_k": 50, "promoted_k": 10 },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1",
                        "config": { "quotas_param": "recall_quotas", "queries": { "two_tower": { "user_embedding": "ctx.user.embedding" } } } },
            CALL_NODE
          },
          "output": { "limit_param": "top_k", "columns": { "promoted": "col.is_promoted ?? false", "promo_bid": "col.promo.bid" } }
        }
        """;

    internal const string Releases = """
        { "pipeline_id": "PIPELINE", "environment": "local", "catalog": "home_feed", "bindings": "local",
          "experiments": { "exp_PIPELINE": { "layer": "PIPELINE" } } }
        """;

    internal const string Experiments = """
        { "experiment_id": "exp_PIPELINE", "pipeline_id": "PIPELINE", "owner": "team", "description": "Fewer rows.", "expires_at": "2099-12-31",
          "arms": { "control": {}, "few": { "params": { "top_k": 2 } } } }
        """;

    private const string CallNode = """
        "promoted": { "order": 200, "op": "PipelineSource@1", "on_failure": "ON_FAILURE",
          "config": { "pipeline": "promoted", "limit_param": "promoted_k", "inputs": { "slots": "3" }, "columns": { "promo.bid": "bid" }, "mark": "is_promoted" } }
        """;

    private static HomeFeedTestHost Host(string onFailure = "fail_request", Action<string>? more = null, string? callNode = null) =>
        HomeFeedTestHost.LoadModified(root =>
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "promoted.jsonc"), Promoted);
            File.WriteAllText(Path.Combine(root, "pipelines", "feed.jsonc"),
                Feed.Replace("CALL_NODE", (callNode ?? CallNode).Replace("ON_FAILURE", onFailure, StringComparison.Ordinal), StringComparison.Ordinal));
            foreach (var id in new[] { "feed", "promoted" })
            {
                File.WriteAllText(Path.Combine(root, "releases", id + ".local.jsonc"), Releases.Replace("PIPELINE", id, StringComparison.Ordinal));
                File.WriteAllText(Path.Combine(root, "experiments", "exp_" + id + ".jsonc"), Experiments.Replace("PIPELINE", id, StringComparison.Ordinal));
            }

            more?.Invoke(root);
        });

    private static PlanCompileRequest RequestOf(HomeFeedTestHost host, string pipelineId)
    {
        var diagnostics = new DiagnosticBag();
        var request = host.Workspace.CreateCompileRequest(pipelineId, "local", diagnostics);
        Assert.True(request is not null, string.Join("\n", diagnostics.Items));
        return request!;
    }

    private static RecEngine Engine(HomeFeedTestHost host, bool withPromoted = true)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root });
        if (withPromoted)
        {
            var promoted = engine.Load(RequestOf(host, "promoted"));
            Assert.True(promoted.Applied, string.Join("\n", promoted.Diagnostics));
        }

        var feed = engine.Load(RequestOf(host, "feed"));
        Assert.True(feed.Applied, string.Join("\n", feed.Diagnostics));
        return engine;
    }

    private static RecRequest Request(string pipelineId, IReadOnlyList<string>? assignment = null, IReadOnlyList<string>? force = null, RequestInputs? inputs = null) => new()
    {
        PipelineId = pipelineId,
        RequestId = "req-call",
        Inputs = inputs ?? new RequestInputs().Set("user_id", 1001),
        Assignment = assignment ?? [],
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
        Debug = force is null ? new DebugOptions { Trace = true } : new DebugOptions { Trace = true, ForceArms = force },
    };

    private static List<RankedItem> PromotedRows(RecResult result) => result.Items.Where(i => i.Columns["promoted"].AsBool()).ToList();

    [Fact]
    public async Task ACalledPipelinesRows_AreAppendedInItsOrder_WithItsColumns()
    {
        var engine = Engine(Host());
        var ct = TestContext.Current.CancellationToken;

        var result = await engine.ExecuteAsync(Request("feed"), ct);
        var direct = await engine.ExecuteAsync(Request("promoted", inputs: new RequestInputs().Set("user_id", 1001).Set("slots", 3)), ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(direct.Succeeded, direct.Error);
        Assert.Equal(5, direct.Items.Count);

        var own = result.Items.Where(i => !i.Columns["promoted"].AsBool()).Select(i => i.Id).ToHashSet();
        var expected = direct.Items.Where(i => !own.Contains(i.Id)).ToList();
        var appended = PromotedRows(result);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(i => i.Id), appended.Select(i => i.Id));
        Assert.Equal(expected.Select(i => i.Columns["bid"].AsFloat64()), appended.Select(i => i.Columns["promo_bid"].AsFloat64()));
        Assert.Equal(appended.Select(i => i.Id), result.Items.Skip(result.Items.Count - appended.Count).Select(i => i.Id));
        Assert.All(result.Items.Where(i => !i.Columns["promoted"].AsBool()), i => Assert.True(i.Columns["promo_bid"].IsMissing));
        Assert.Equal("succeeded", NodeOutcomeNames.Name(result.Trace!.Single(n => n.NodeId == "promoted").Kind));

        var called = Assert.Single(result.Calls!);
        Assert.Equal(("feed", "promoted", 0, "promoted", true), (called.Caller, called.Node, called.Sequence, called.PipelineId, called.Succeeded));
        Assert.Equal(new[] { "recall", "bid", "count" }, called.Trace.Select(n => n.NodeId));

        var again = await engine.ExecuteAsync(Request("feed"), ct);
        Assert.Equal(result.Items.Select(i => i.Id), again.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ACalledPipeline_RunsTheRequestsArms_AndTheResultHasThem()
    {
        var engine = Engine(Host());
        var ct = TestContext.Current.CancellationToken;

        var result = await engine.ExecuteAsync(Request("feed", assignment: ["exp_feed/control", "exp_promoted/few"]), ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.InRange(PromotedRows(result).Count, 1, 2);
        Assert.Equal(new[] { "exp_feed/control", "exp_promoted/few" }, result.Experiments.Assigned);
        Assert.Contains("exp_promoted", result.Experiments.Triggered);
        Assert.Empty(result.Experiments.Ignored);

        Assert.Equal(new[] { "exp_promoted/few" }, Assert.Single(result.Calls!).Arms);

        var unknown = await engine.ExecuteAsync(Request("feed", assignment: ["exp_promoted/few", "exp_nowhere/x"]), ct);
        Assert.Equal("exp_nowhere/x", Assert.Single(unknown.Experiments.Ignored).Id);
    }

    [Fact]
    public async Task AForcedArmOfACalledPipeline_IsItsToRun_AndAnUnknownOneIsStillRefused()
    {
        var engine = Engine(Host());
        var ct = TestContext.Current.CancellationToken;

        var forced = await engine.ExecuteAsync(Request("feed", force: ["exp_promoted/few"]), ct);
        Assert.True(forced.Succeeded, forced.Error);
        Assert.InRange(PromotedRows(forced).Count, 1, 2);
        Assert.Contains("exp_promoted/few", forced.Experiments.Assigned);

        var refused = await engine.ExecuteAsync(Request("feed", force: ["exp_promoted/nope"]), ct);
        Assert.Equal(RecErrorKind.InvalidRequest, refused.ErrorKind);
    }

    [Theory]
    [InlineData("skip", true)]
    [InlineData("fail_request", false)]
    public async Task ACallOfAPipelineTheEngineDoesNotServe_FailsItsNode(string onFailure, bool served)
    {
        var engine = Engine(Host(onFailure), withPromoted: false);

        var result = await engine.ExecuteAsync(Request("feed"), TestContext.Current.CancellationToken);

        Assert.Equal(served, result.Succeeded);
        var node = result.Trace!.Single(n => n.NodeId == "promoted");
        Assert.Contains("pipeline 'promoted' is not served by this engine", node.Detail);
        var called = Assert.Single(result.Calls!);
        Assert.False(called.Succeeded);
        Assert.Empty(called.Trace);
        if (served)
        {
            Assert.Empty(PromotedRows(result));
            Assert.NotEmpty(result.Items);
        }
        else
        {
            Assert.Contains("pipeline 'promoted' is not served by this engine", result.Error);
        }
    }

    [Fact]
    public async Task ACalledPipelineThatFails_FailsTheCall_NotTheRequest()
    {
        var host = Host("skip", callNode: CallNode.Replace("\"inputs\": { \"slots\": \"3\" }, ", string.Empty, StringComparison.Ordinal));
        var feed = RequestOf(host, "feed");
        var unchecked_ = new PlanCompileRequest
        {
            Pipeline = feed.Pipeline, Catalog = feed.Catalog, Bindings = feed.Bindings, Tables = feed.Tables, Policy = feed.Policy, Experiments = feed.Experiments,
        };
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root });
        Assert.True(engine.Load(RequestOf(host, "promoted")).Applied);
        var load = engine.Load(unchecked_); // without the workspace's pipelines: the call is checked when it is made
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));

        var result = await engine.ExecuteAsync(Request("feed"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(PromotedRows(result));
        Assert.Contains("pipeline 'promoted' requires input 'slots' (int64), which the call does not pass and 'feed' does not declare",
            result.Trace!.Single(n => n.NodeId == "promoted").Detail);
    }

    [Theory]
    [InlineData("\"pipeline\": \"promoted\"", "\"pipeline\": \"promotd\"", "PIPELINE_CALL_UNKNOWN", "/nodes/promoted/config/pipeline", "promoted")]
    [InlineData("\"pipeline\": \"promoted\"", "\"pipeline\": \"feed\"", "PIPELINE_CALL_CYCLE", "/nodes/promoted/config/pipeline", null)]
    [InlineData("\"promo.bid\": \"bid\"", "\"promo.bid\": \"bidd\"", "PIPELINE_CALL_OUTPUT", "/nodes/promoted/config/columns/promo.bid", "bid")]
    [InlineData("\"inputs\": { \"slots\": \"3\" }, ", "", "PIPELINE_CALL_INPUT", "/nodes/promoted/config/pipeline", null)]
    [InlineData("\"slots\": \"3\"", "\"slots\": \"'three'\"", "PIPELINE_CALL_INPUT", "/nodes/promoted/config/inputs/slots", null)]
    [InlineData("\"slots\": \"3\"", "\"slots\": \"3\", \"slot\": \"3\"", "PIPELINE_CALL_INPUT", "/nodes/promoted/config/inputs/slot", "slots")]
    public void ACallThatDoesNotFitThePipeline_IsRefusedWhereItIsWritten(string from, string to, string code, string pointer, string? suggested)
    {
        var host = Host(callNode: CallNode.Replace(from, to, StringComparison.Ordinal));
        var diagnostics = new DiagnosticBag();
        var request = host.Workspace.CreateCompileRequest("feed", "local", diagnostics)!;

        var compilation = PlanCompiler.Compile(request, host.Environment);

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal((code, pointer), (error.Code, error.Pointer));
        if (suggested is not null)
        {
            Assert.Contains($"'{suggested}'", error.Hint);
        }
    }

    [Fact]
    public async Task ACalledRunsLogs_AreItsPipelines_AndSayWhoCalled()
    {
        var engine = Engine(Host());
        var request = new RecRequest
        {
            PipelineId = "feed", RequestId = "req-call", Inputs = new RequestInputs().Set("user_id", 1001), Assignment = ["exp_promoted/few"],
            Deadline = HomeFeedTestHost.Deadline, TimeMs = 1_767_225_600_000, Debug = new DebugOptions { CaptureLogs = true },
        };

        var result = await engine.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var own = Assert.Single(result.LogBatches!, b => b.PipelineId == "feed");
        Assert.Null(own.CalledBy);
        var called = Assert.Single(result.LogBatches!, b => b.PipelineId == "promoted");
        Assert.Equal(("$experiments", "req-call", "feed/promoted#0"), (called.TapId, called.RequestId, called.CalledBy));
        Assert.Contains("\"called_by\": \"feed/promoted#0\"", StrataRec.Logging.LogBatchJson.Encode(called));
        Assert.DoesNotContain("called_by", StrataRec.Logging.LogBatchJson.Encode(own));
    }

    [Fact]
    public async Task ARecordedCall_IsAnsweredFromTheBundleOnReplay_WithoutRunningThePipeline()
    {
        var host = Host();
        var engine = Engine(host);
        var ct = TestContext.Current.CancellationToken;
        var request = new RecRequest
        {
            PipelineId = "feed", RequestId = "req-call", Inputs = new RequestInputs().Set("user_id", 1001), Assignment = [],
            Deadline = HomeFeedTestHost.Deadline, TimeMs = 1_767_225_600_000, Debug = new DebugOptions { Record = true },
        };

        var recorded = await engine.ExecuteAsync(request, ct);

        Assert.True(recorded.Succeeded, recorded.Error);
        var call = Assert.Single(recorded.Recording!.Runs[0].PipelineCalls);
        Assert.Equal(("promoted", 0, "promoted", true), (call.Node, call.Seq, call.Pipeline, call.Succeeded));
        Assert.Equal("3", call.Inputs["slots"].Value!.Value.GetRawText());
        Assert.Equal(new[] { "bid" }, call.Columns.Select(c => c.Name));
        Assert.Equal("count", Assert.Single(call.Values).Name);

        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));
        var alone = Engine(host, withPromoted: false);
        var outcome = await alone.ReplayAsync(bundle, cancellationToken: ct);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Empty(outcome.Report.Gaps);
        Assert.Equal(recorded.Items.Select(i => i.Id), outcome.Result.Items.Select(i => i.Id));

        var changed = Engine(Host(callNode: CallNode.Replace("\"slots\": \"3\"", "\"slots\": \"4\"", StringComparison.Ordinal)), withPromoted: false);
        var other = await changed.ReplayAsync(bundle, cancellationToken: ct);
        Assert.False(other.Result.Succeeded);
        var gap = Assert.Single(other.Report.Gaps);
        Assert.Equal(("pipeline", "promoted", "inputs_changed"), (gap.Kind, gap.Source, gap.Reason));
    }

    [Fact]
    public void PipelinesThatCallEachOther_AreRefused_WithTheCycle()
    {
        var host = Host(more: root => ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"nodes\": {",
            "\"nodes\": {\n\"back\": { \"order\": 150, \"op\": \"PipelineSource@1\", \"config\": { \"pipeline\": \"feed\", \"limit_param\": \"top_k\" } },"));
        var diagnostics = new DiagnosticBag();
        var request = host.Workspace.CreateCompileRequest("feed", "local", diagnostics)!;

        var compilation = PlanCompiler.Compile(request, host.Environment);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("PIPELINE_CALL_FAILED", error.Code);
        Assert.Contains("PIPELINE_CALL_CYCLE", error.Message);
        Assert.Contains("feed → promoted → feed", error.Message);
    }

    [Fact]
    public async Task PipelinesLoadedOnTheirOwn_ThatCallEachOther_StopAtTheCall()
    {
        // Each compiled without the other, as releases are: nothing sees the cycle until a request runs into it.
        var host = Host("skip", more: root => ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"nodes\": {",
            "\"nodes\": {\n\"back\": { \"order\": 150, \"op\": \"PipelineSource@1\", \"on_failure\": \"skip\", \"config\": { \"pipeline\": \"feed\", \"limit_param\": \"top_k\" } },"));
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root });
        foreach (var id in new[] { "promoted", "feed" })
        {
            var artifact = host.Workspace.Pipelines[id].Value;
            var load = engine.Load(new PlanCompileRequest
            {
                Pipeline = artifact, Catalog = host.Request.Catalog, Bindings = host.Request.Bindings, Tables = host.Request.Tables, Policy = host.Request.Policy,
            });
            Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        }

        var result = await engine.ExecuteAsync(Request("feed"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.NotEmpty(result.Items);
    }

    private sealed class ProbeConfig
    {
        public required string Pipeline { get; init; }
    }

    private sealed class ProbeFactory(Func<OperatorContext, PipelineCall, ValueTask> body) : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("ProbeCall", 1, Effects.None, typeof(ProbeConfig), "Calls a pipeline as a test says.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<ProbeConfig>();
            if (config is null || !context.TryCallPipeline(config.Pipeline, "/pipeline", out var call) || !context.TryCallInput(call, "slots", "/pipeline", ColumnType.Int64))
            {
                return null;
            }

            return new Probe(call, body);
        }

        private sealed class Probe(PipelineCall call, Func<OperatorContext, PipelineCall, ValueTask> body) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context) => body(context, call);
        }
    }

    private const string ProbeNode = """ "probe": { "order": 200, "op": "ProbeCall@1", "config": { "pipeline": "promoted" } } """;

    private sealed class Provider(Func<AssignmentContext, CancellationToken, ValueTask<IReadOnlyList<string>>> answer) : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken) => answer(context, cancellationToken);
    }

    private static RecEngine EngineWith(HomeFeedTestHost host, IOperatorFactory? op = null, IAssignmentProvider? provider = null, params string[] pipelines)
    {
        var environment = new PlanEnvironment { BaseDirectory = host.Root, Operators = op is null ? OperatorRegistry.CreateDefault() : OperatorRegistry.CreateDefault().Add(op) };
        var engine = new RecEngine(environment, provider);
        foreach (var id in pipelines.Length == 0 ? ["promoted", "feed"] : pipelines)
        {
            var load = engine.Load(RequestOf(host, id));
            Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        }

        return engine;
    }

    private static RecRequest Recorded(IReadOnlyList<string>? assignment = null, RequestInputs? inputs = null) => new()
    {
        PipelineId = "feed", RequestId = "req-call", Inputs = inputs ?? new RequestInputs().Set("user_id", 1001), Assignment = assignment,
        Deadline = HomeFeedTestHost.Deadline, TimeMs = 1_767_225_600_000, Debug = new DebugOptions { Record = true, Trace = true },
    };

    [Fact]
    public async Task AWorkspaceLoad_ServesThePipelinesALoadedOneCalls()
    {
        var host = Host();
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root });

        var loaded = await ServiceCollectionExtensions.LoadWorkspaceAsync(engine, host.Root, "local", ["feed"], TestContext.Current.CancellationToken);

        Assert.DoesNotContain(loaded.Items, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(new[] { "feed", "promoted" }, engine.PipelineIds());
        var result = await engine.ExecuteAsync(Request("feed"), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotEmpty(PromotedRows(result));
    }

    [Fact]
    public async Task ACallMadeAfterTheCallerCancelled_IsCancelled_NotAFailedCall()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ProbeFactory(async (context, call) =>
        {
            entered.SetResult();
            await go.Task;
            _ = await context.CallPipelineAsync(call, new RequestInputs().Set("slots", 3));
        });
        var engine = EngineWith(Host(callNode: ProbeNode), probe, null, "feed"); // promoted is not served: a failed call, were it made
        using var cancel = new CancellationTokenSource();

        var running = engine.ExecuteAsync(Request("feed"), cancel.Token).AsTask();
        await entered.Task;
        await cancel.CancelAsync();
        go.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task AFallbackNodeUsedTwice_RecordsEachOfItsCalls_AndReplays()
    {
        const string Nodes = """
            "a": { "order": 200, "op": "PipelineSource@1", "on_failure": "fallback", "fallback": "backup", "config": { "pipeline": "other", "limit_param": "promoted_k", "columns": { "promo.bid": "bid" }, "mark": "is_promoted" } },
            "b": { "order": 210, "op": "PipelineSource@1", "on_failure": "fallback", "fallback": "backup", "config": { "pipeline": "other", "limit_param": "promoted_k", "columns": { "promo.bid": "bid" }, "mark": "is_promoted" } },
            "backup": { "role": "fallback", "op": "PipelineSource@1",
                        "config": { "pipeline": "promoted", "limit_param": "promoted_k", "inputs": { "slots": "3" }, "columns": { "promo.bid": "bid" }, "mark": "is_promoted" } }
            """;
        var host = Host(callNode: Nodes, more: root => File.WriteAllText(Path.Combine(root, "pipelines", "other.jsonc"),
            Promoted.Replace("\"pipeline_id\": \"promoted\"", "\"pipeline_id\": \"other\"", StringComparison.Ordinal)
                .Replace("\"slots\": { \"type\": \"int64\" }", "\"slots\": { \"type\": \"int64?\" }", StringComparison.Ordinal)
                .Replace("+ ctx.request.slots", "+ (ctx.request.slots ?? 0)", StringComparison.Ordinal)));
        var engine = EngineWith(host); // 'other' is in the workspace and not served: both calls of it fail
        var ct = TestContext.Current.CancellationToken;

        var recorded = await engine.ExecuteAsync(Recorded([]), ct);

        Assert.True(recorded.Succeeded, recorded.Error);
        var calls = recorded.Recording!.Runs[0].PipelineCalls;
        Assert.Equal(new[] { ("a", 0, "other"), ("b", 0, "other"), ("backup", 0, "promoted"), ("backup", 1, "promoted") }, calls.Select(c => (c.Node, c.Seq, c.Pipeline)));
        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording)), cancellationToken: ct);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Empty(outcome.Report.Gaps);
    }

    private static HomeFeedTestHost HostWithLists(string intoType = "list<float64>?") => Host(
        callNode: CallNode.Replace("\"inputs\": { \"slots\": \"3\" }", "\"inputs\": { \"slots\": \"3\", \"ids\": \"ctx.request.ids\" }", StringComparison.Ordinal)
            .Replace("\"promo.bid\": \"bid\"", "\"promo.bid\": \"bid\", \"promo.ids\": \"ids\"", StringComparison.Ordinal),
        more: root =>
        {
            ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"slots\": { \"type\": \"int64\" }", "\"slots\": { \"type\": \"int64\" }, \"ids\": { \"type\": \"list<int64>\" }");
            ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"columns\": { \"bid\": \"col.bid\" }", "\"columns\": { \"bid\": \"col.bid\", \"ids\": \"ctx.request.ids\" }");
            ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"country\": { \"type\": \"string?\" } },", "\"country\": { \"type\": \"string?\" }, \"ids\": { \"type\": \"list<int64>\" } },");
            ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"promo.bid\": { \"type\": \"float32?\" }", "\"promo.bid\": { \"type\": \"float32?\" }, \"promo.ids\": { \"type\": \"" + intoType + "\" }");
            ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"promo_bid\": \"col.promo.bid\"", "\"promo_bid\": \"col.promo.bid\", \"promo_ids\": \"col.promo.ids\"");
        });

    [Fact]
    public async Task AListPassedAndReturned_IsConvertedAsChecked_AndReplaysFromAFile()
    {
        var engine = EngineWith(HostWithLists());
        var ct = TestContext.Current.CancellationToken;

        var recorded = await engine.ExecuteAsync(Recorded([], new RequestInputs().Set("user_id", 1001).Set("ids", new long[] { 1, 2 })), ct);

        Assert.True(recorded.Succeeded, recorded.Error);
        var row = Assert.Single(PromotedRows(recorded).Take(1));
        Assert.Equal(new[] { 1.0, 2.0 }, row.Columns["promo_ids"].AsFloat64List().ToArray());
        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!)), cancellationToken: ct);
        Assert.Empty(outcome.Report.Gaps);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
    }

    [Fact]
    public async Task AProviderThatFailsForACalledPipeline_FailsTheCall_AsAResult()
    {
        var provider = new Provider((context, _) => context.PipelineId == "promoted"
            ? throw new IOException("provider unavailable")
            : ValueTask.FromResult<IReadOnlyList<string>>([]));
        var engine = EngineWith(Host("skip"), provider: provider);

        var result = await engine.ExecuteAsync(Recorded(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(result.Recording!.Runs[0].PipelineCalls);
        Assert.False(call.Succeeded);
        Assert.Contains("pipeline 'promoted' was not called: its assignment failed", call.Error);
        Assert.Contains("provider unavailable", call.Error);
        Assert.Contains(call.Error!, result.Trace!.Single(n => n.NodeId == "promoted").Detail);
    }

    [Fact]
    public async Task TwoForcedArmsOfOneLayerOfACalledPipeline_AreRefused()
    {
        var engine = Engine(Host());

        var result = await engine.ExecuteAsync(Request("feed", force: ["exp_promoted/control", "exp_promoted/few"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Contains("exp_promoted", result.Error);
    }

    [Fact]
    public async Task TheIgnoredArms_AreEachOnce_ById_WithACalledPipelinesOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var shared = await Engine(Host()).ExecuteAsync(Request("feed", assignment: ["zz/x", "exp_promoted/control", "exp_promoted/few"]), ct);
        Assert.Equal(new[] { ("exp_promoted/control", IgnoredArmReason.LayerConflict), ("exp_promoted/few", IgnoredArmReason.LayerConflict), ("zz/x", IgnoredArmReason.Unknown) },
            shared.Experiments.Ignored.Select(a => (a.Id, a.Reason)));

        var provider = new Provider((context, _) => ValueTask.FromResult<IReadOnlyList<string>>(context.PipelineId == "promoted" ? ["exp_promoted/few", "typo/x"] : []));
        var asked = await EngineWith(Host(), provider: provider).ExecuteAsync(
            new RecRequest { PipelineId = "feed", RequestId = "req-call", Inputs = new RequestInputs().Set("user_id", 1001), Deadline = HomeFeedTestHost.Deadline }, ct);
        Assert.Equal(new[] { "exp_promoted/few" }, asked.Experiments.Assigned);
        Assert.Equal(("typo/x", IgnoredArmReason.Unknown), Assert.Single(asked.Experiments.Ignored.Select(a => (a.Id, a.Reason))));
    }

    [Fact]
    public void ACycleThroughACallAnArmAdds_IsRefused()
    {
        var host = Host(more: root => ReplayTests.Edit(root, "experiments/exp_promoted.jsonc", "\"few\": { \"params\": { \"top_k\": 2 } }",
            "\"few\": { \"nodes\": { \"back\": { \"order\": 150, \"op\": \"PipelineSource@1\", \"config\": { \"pipeline\": \"feed\", \"limit_param\": \"top_k\" } } } }"));

        var compilation = PlanCompiler.Compile(RequestOf(host, "feed"), host.Environment);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("PIPELINE_CALL_FAILED", error.Code);
        Assert.Contains("feed → promoted → feed", error.Message);
    }

    [Theory]
    [InlineData(true, "/nodes/promoted/config/inputs/slots", "?? <default>")]
    [InlineData(false, "/nodes/promoted/config/pipeline", "?? <default>")]
    public void AnInputThatMayBeMissing_IsNotPassedAsARequiredOne(bool passed, string pointer, string hint)
    {
        var node = passed
            ? CallNode.Replace("\"slots\": \"3\"", "\"slots\": \"ctx.request.slots\"", StringComparison.Ordinal)
            : CallNode.Replace("\"inputs\": { \"slots\": \"3\" }, ", string.Empty, StringComparison.Ordinal);
        var host = Host(callNode: node, more: root => ReplayTests.Edit(root, "pipelines/feed.jsonc",
            "\"country\": { \"type\": \"string?\" } },", "\"country\": { \"type\": \"string?\" }, \"slots\": { \"type\": \"int64?\" } },"));

        var compilation = PlanCompiler.Compile(RequestOf(host, "feed"), host.Environment);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("PIPELINE_CALL_INPUT", pointer), (error.Code, error.Pointer));
        Assert.Contains(hint, error.Hint);
    }

    [Fact]
    public void AnInt32InputPassedOn_IsAnInt32()
    {
        var host = Host(callNode: CallNode.Replace("\"slots\": \"3\"", "\"slots\": \"ctx.request.n\"", StringComparison.Ordinal), more: root =>
        {
            ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"slots\": { \"type\": \"int64\" }", "\"slots\": { \"type\": \"int32\" }");
            ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"country\": { \"type\": \"string?\" } },", "\"country\": { \"type\": \"string?\" }, \"n\": { \"type\": \"int32\" } },");
        });

        var compilation = PlanCompiler.Compile(RequestOf(host, "feed"), host.Environment);

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
    }

    [Fact]
    public async Task ACallsInputs_AreRecordedAsPassed_ThoughTheOperatorChangesThemMeanwhile()
    {
        var release = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var provider = new Provider((context, _) =>
        {
            if (context.PipelineId != "promoted" || Interlocked.Increment(ref asked) != 1)
            {
                return ValueTask.FromResult<IReadOnlyList<string>>([]);
            }

            waiting.SetResult(); // the first call of promoted waits here, its inputs passed already
            return new ValueTask<IReadOnlyList<string>>(release.Task);
        });
        var probe = new ProbeFactory(async (context, call) =>
        {
            var inputs = new RequestInputs().Set("slots", 1);
            var first = context.CallPipelineAsync(call, inputs).AsTask();
            await waiting.Task;
            inputs.Set("slots", 2);
            var second = context.CallPipelineAsync(call, inputs).AsTask();
            release.SetResult([]);
            await Task.WhenAll(first, second);
        });
        var engine = EngineWith(Host(callNode: ProbeNode), probe, provider);

        var result = await engine.ExecuteAsync(Recorded(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new[] { (0, "1"), (1, "2") }, result.Recording!.Runs[0].PipelineCalls.Select(c => (c.Seq, c.Inputs["slots"].Value!.Value.GetRawText())));
    }

    [Fact]
    public async Task CallsMadeAtOnceFromSeveralThreads_EachGetAPlace()
    {
        const int Threads = 8, Each = 400;
        var probe = new ProbeFactory(async (context, call) =>
        {
            using var start = new Barrier(Threads);
            var threads = new Task[Threads];
            for (var t = 0; t < Threads; t++)
            {
                threads[t] = Task.Run(async () =>
                {
                    start.SignalAndWait();
                    for (var i = 0; i < Each; i++)
                    {
                        _ = await context.CallPipelineAsync(call, new RequestInputs().Set("slots", 3));
                    }
                });
            }

            await Task.WhenAll(threads);
        });
        var engine = EngineWith(Host(callNode: ProbeNode), probe, null, "feed"); // promoted is not served: each call fails at once

        var result = await engine.ExecuteAsync(Recorded([]), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var places = result.Recording!.Runs[0].PipelineCalls.Select(c => c.Seq).ToList();
        Assert.Equal(Threads * Each, places.Count);
        Assert.Equal(Enumerable.Range(0, Threads * Each), places.Order());
    }

    [Fact]
    public async Task TheLimit_IsOfTheRowsAppended_AndAMissingOneAppendsNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var one = Engine(Host(more: root => ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"promoted_k\": 10 }", "\"promoted_k\": 1 }")));
        var all = await Engine(Host()).ExecuteAsync(Request("feed"), ct);
        var first = await one.ExecuteAsync(Request("feed"), ct);
        Assert.Equal(PromotedRows(all).Take(1).Select(i => i.Id), PromotedRows(first).Select(i => i.Id));

        var none = Engine(Host(
            callNode: CallNode.Replace("\"limit_param\": \"promoted_k\"", "\"limit_param\": \"limits.promo\"", StringComparison.Ordinal),
            more: root =>
            {
                ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"promoted_k\": { \"type\": \"int64\", \"min\": 0, \"max\": 100 } },",
                    "\"promoted_k\": { \"type\": \"int64\", \"min\": 0, \"max\": 100 }, \"limits\": { \"type\": \"map<string,int64>\", \"min\": 0, \"max\": 100 } },");
                ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"promoted_k\": 10 }", "\"promoted_k\": 10, \"limits\": { \"promo\": null } }");
            }));
        var missing = await none.ExecuteAsync(Request("feed"), ct);
        Assert.True(missing.Succeeded, missing.Error);
        Assert.Empty(PromotedRows(missing));
    }

    [Fact]
    public void ACompileRequest_KeepsItsPipelines_AndAsksAgainForAnotherEnvironment()
    {
        var misspelled = RequestOf(Host(callNode: CallNode.Replace("\"pipeline\": \"promoted\"", "\"pipeline\": \"promotd\"", StringComparison.Ordinal)), "feed");
        var merged = PlanCompiler.CompileMerged(misspelled, [], new PlanEnvironment());
        Assert.Contains(merged.Diagnostics, d => d.Code == "PIPELINE_CALL_UNKNOWN");

        var host = Host();
        var request = RequestOf(host, "feed");
        var without = new OperatorRegistry().Add(new StrataRec.Pipeline.Operators.Standard.MultiSourceRecallFactory())
            .Add(new StrataRec.Pipeline.Operators.Standard.PipelineSourceFactory());
        var lacking = PlanCompiler.Compile(request, new PlanEnvironment { BaseDirectory = host.Root, Operators = without });
        Assert.Contains(lacking.Diagnostics, d => d.Code == "PIPELINE_CALL_FAILED");
        var complete = PlanCompiler.Compile(request, host.Environment);
        Assert.True(complete.Succeeded, string.Join("\n", complete.Diagnostics));
    }

    [Fact]
    public void ACallWhoseIdsAreReadAsTwoKeyTypes_IsRefused_AlsoWithoutTheCalledPipeline()
    {
        var host = Host(callNode: """ "probe": { "order": 200, "op": "ReadsTwice@1", "config": { "pipeline": "promoted" } } """);
        var feed = RequestOf(host, "feed");
        var unchecked_ = new PlanCompileRequest
        {
            Pipeline = feed.Pipeline, Catalog = feed.Catalog, Bindings = feed.Bindings, Tables = feed.Tables, Policy = feed.Policy, Experiments = feed.Experiments,
        };
        var environment = new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(new ReadsTwiceFactory()) };

        var compilation = PlanCompiler.Compile(unchecked_, environment);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("PIPELINE_CALL_KEY_TYPE", "/nodes/probe/config/second"), (error.Code, error.Pointer));
        Assert.Contains("as int64 and as string", error.Message);
    }

    private sealed class ReadsTwiceFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("ReadsTwice", 1, Effects.None, typeof(ProbeConfig), "Reads a call's ids as two key types.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<ProbeConfig>();
            if (config is null || !context.TryCallPipeline(config.Pipeline, "/pipeline", out var call))
            {
                return null;
            }

            var ok = context.CallReadsKeys(call, "/first", StrataRec.Core.Keys.EntityKeyType.Int64) & context.CallReadsKeys(call, "/second", StrataRec.Core.Keys.EntityKeyType.String);
            return ok ? new Nothing() : null;
        }

        private sealed class Nothing : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context) => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AReplayThatSkipsATimedOutAttempt_SkipsItsCalls()
    {
        const string Nodes = """
            "a": { "order": 200, "op": "PipelineSource@1", "on_failure": "fallback", "fallback": "backup", "config": { "pipeline": "other", "limit_param": "promoted_k", "columns": { "promo.bid": "bid" }, "mark": "is_promoted" } },
            "b": { "order": 210, "op": "PipelineSource@1", "on_failure": "fallback", "fallback": "backup", "config": { "pipeline": "other", "limit_param": "promoted_k", "columns": { "promo.bid": "bid" }, "mark": "is_promoted" } },
            "backup": { "role": "fallback", "op": "PipelineSource@1", "timeout_ms": 400, "on_failure": "skip",
                        "config": { "pipeline": "promoted", "limit_param": "promoted_k", "inputs": { "slots": "3" }, "columns": { "promo.bid": "bid" }, "mark": "is_promoted" } }
            """;
        var host = Host(callNode: Nodes, more: root => File.WriteAllText(Path.Combine(root, "pipelines", "other.jsonc"),
            Promoted.Replace("\"pipeline_id\": \"promoted\"", "\"pipeline_id\": \"other\"", StringComparison.Ordinal)
                .Replace("\"slots\": { \"type\": \"int64\" }", "\"slots\": { \"type\": \"int64?\" }", StringComparison.Ordinal)
                .Replace("+ ctx.request.slots", "+ (ctx.request.slots ?? 0)", StringComparison.Ordinal)));
        var asked = 0;
        var provider = new Provider(async (context, token) =>
        {
            if (context.PipelineId == "promoted" && Interlocked.Increment(ref asked) == 1)
            {
                await Task.Delay(Timeout.Infinite, token); // the first call of promoted waits for its arms until the node's time is up
            }

            return [];
        });
        var engine = EngineWith(host, provider: provider);
        var ct = TestContext.Current.CancellationToken;

        var recorded = await engine.ExecuteAsync(Recorded(), ct);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.NotEmpty(PromotedRows(recorded));
        Assert.Equal(new[] { ("a", 0, false), ("b", 0, false), ("backup", 0, false), ("backup", 1, true) },
            recorded.Recording!.Runs[0].PipelineCalls.Select(c => (c.Node, c.Seq, c.Succeeded)));
        var outcome = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording)), cancellationToken: ct);
        Assert.Empty(outcome.Report.Gaps);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal(recorded.Items.Select(i => i.Id), outcome.Result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task AForcedArmOfAPipelineTheFallbackPipelineCalls_IsAccepted()
    {
        var host = Host(more: root => File.WriteAllText(Path.Combine(root, "pipelines", "plain.jsonc"),
            Promoted.Replace("\"pipeline_id\": \"promoted\"", "\"pipeline_id\": \"plain\", \"fallback_pipeline\": \"feed\"", StringComparison.Ordinal)));
        var engine = EngineWith(host, null, null, "promoted", "feed", "plain");

        var result = await engine.ExecuteAsync(
            Request("plain", force: ["exp_promoted/few"], inputs: new RequestInputs().Set("user_id", 1001).Set("slots", 3)), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task AForcedArmOfAPipelineThatIsCalledAndTheFallback_IsAccepted()
    {
        var host = Host(more: root =>
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "leaf.jsonc"), Promoted.Replace("\"pipeline_id\": \"promoted\"", "\"pipeline_id\": \"leaf\"", StringComparison.Ordinal));
            ReplayTests.Edit(root, "pipelines/promoted.jsonc", "\"nodes\": {",
                "\"nodes\": {\n\"sub\": { \"order\": 150, \"op\": \"PipelineSource@1\", \"on_failure\": \"skip\", \"config\": { \"pipeline\": \"leaf\", \"limit_param\": \"top_k\" } },");
            ReplayTests.Edit(root, "pipelines/feed.jsonc", "\"pipeline_id\": \"feed\",", "\"pipeline_id\": \"feed\",\n  \"fallback_pipeline\": \"promoted\",");
        });
        var engine = EngineWith(host, null, null, "leaf", "promoted", "feed");

        var result = await engine.ExecuteAsync(Request("feed", force: ["exp_promoted/few"]), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("exp_promoted/few", result.Experiments.Assigned);
    }

    [Fact]
    public async Task ARunCalledByACalledRun_SaysTheWholePathOfCalls()
    {
        const string Outer = """
            {
              "pipeline_id": "outer",
              "entity_key_type": "int64",
              "request_schema": { "user_id": { "type": "int64?" }, "country": { "type": "string?" } },
              "param_schema": { "k": { "type": "int64", "min": 1, "max": 100 } },
              "params": { "k": 50 },
              "nodes": {
                "x": { "order": 100, "op": "PipelineSource@1", "config": { "pipeline": "feed", "limit_param": "k" } },
                "y": { "order": 200, "op": "PipelineSource@1", "config": { "pipeline": "feed", "limit_param": "k" } }
              },
              "output": { "limit_param": "k", "columns": {} }
            }
            """;
        var host = Host(more: root => File.WriteAllText(Path.Combine(root, "pipelines", "outer.jsonc"), Outer));
        var engine = EngineWith(host, null, null, "promoted", "feed", "outer");
        var request = new RecRequest
        {
            PipelineId = "outer", RequestId = "req-call", Inputs = new RequestInputs().Set("user_id", 1001), Assignment = ["exp_promoted/few"],
            Deadline = HomeFeedTestHost.Deadline, TimeMs = 1_767_225_600_000, Debug = new DebugOptions { CaptureLogs = true },
        };

        var result = await engine.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new[] { "outer/x#0 > feed/promoted#0", "outer/y#0 > feed/promoted#0" },
            result.LogBatches!.Where(b => b.PipelineId == "promoted").Select(b => b.CalledBy).Order());
    }
}
