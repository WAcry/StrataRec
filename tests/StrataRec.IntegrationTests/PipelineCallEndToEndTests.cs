using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A node calling another pipeline (<c>PipelineSource@1</c>, <c>OperatorContext.CallPipelineAsync</c>): pipeline
/// <c>feed</c> appends the rows of <c>promoted</c>, both added to a copy of the sample workspace and served by one engine.
/// </summary>
public sealed class PipelineCallEndToEndTests
{
    /// <summary>The called pipeline: the popular list's first rows with a bid, and how many it had.</summary>
    private const string Promoted = """
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

    /// <summary>The calling pipeline: a few rows of its own, then the called pipeline's.</summary>
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

    private const string Releases = """
        { "pipeline_id": "PIPELINE", "environment": "local", "catalog": "home_feed", "bindings": "local",
          "experiments": { "exp_PIPELINE": { "layer": "PIPELINE" } } }
        """;

    private const string Experiments = """
        { "experiment_id": "exp_PIPELINE", "pipeline_id": "PIPELINE", "owner": "team", "description": "Fewer rows.", "expires_at": "2099-12-31",
          "arms": { "control": {}, "few": { "params": { "top_k": 2 } } } }
        """;

    /// <summary>The node of <c>feed</c> that calls <c>promoted</c>.</summary>
    private const string CallNode = """
        "promoted": { "order": 200, "op": "PipelineSource@1", "on_failure": "ON_FAILURE",
          "config": { "pipeline": "promoted", "limit_param": "promoted_k", "inputs": { "slots": "3" }, "columns": { "promo.bid": "bid" }, "mark": "is_promoted" } }
        """;

    /// <summary>The sample with <c>feed</c> and <c>promoted</c> added; <paramref name="more"/> edits the copy further.</summary>
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

        // The rows the feed did not have already, in the called pipeline's order, after the feed's own, with its bids.
        var own = result.Items.Where(i => !i.Columns["promoted"].AsBool()).Select(i => i.Id).ToHashSet();
        var expected = direct.Items.Where(i => !own.Contains(i.Id)).ToList();
        var appended = PromotedRows(result);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(i => i.Id), appended.Select(i => i.Id));
        Assert.Equal(expected.Select(i => i.Columns["bid"].AsFloat64()), appended.Select(i => i.Columns["promo_bid"].AsFloat64()));
        Assert.Equal(appended.Select(i => i.Id), result.Items.Skip(result.Items.Count - appended.Count).Select(i => i.Id));
        Assert.All(result.Items.Where(i => !i.Columns["promoted"].AsBool()), i => Assert.True(i.Columns["promo_bid"].IsMissing));
        Assert.Equal("succeeded", NodeOutcomeNames.Name(result.Trace!.Single(n => n.NodeId == "promoted").Kind));

        // The same request again calls with the same seed and time: the same result.
        var again = await engine.ExecuteAsync(Request("feed"), ct);
        Assert.Equal(result.Items.Select(i => i.Id), again.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ACalledPipeline_RunsTheRequestsArms_AndTheResultHasThem()
    {
        var engine = Engine(Host());
        var ct = TestContext.Current.CancellationToken;

        // The platform's answer has the arms of every pipeline: each run takes its own, and none is ignored.
        var result = await engine.ExecuteAsync(Request("feed", assignment: ["exp_feed/control", "exp_promoted/few"]), ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.InRange(PromotedRows(result).Count, 1, 2);
        Assert.Equal(new[] { "exp_feed/control", "exp_promoted/few" }, result.Experiments.Assigned);
        Assert.Contains("exp_promoted", result.Experiments.Triggered);
        Assert.Empty(result.Experiments.Ignored);

        // An arm no pipeline has is still ignored.
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
        // The called pipeline requires 'slots' of the caller, who passes a text here: its own check of its inputs refuses it.
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

        // An engine that does not serve the called pipeline replays the request all the same: the bundle answers the call.
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording));
        var alone = Engine(host, withPromoted: false);
        var outcome = await alone.ReplayAsync(bundle, cancellationToken: ct);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Empty(outcome.Report.Gaps);
        Assert.Equal(recorded.Items.Select(i => i.Id), outcome.Result.Items.Select(i => i.Id));

        // A configuration that calls with other inputs has no recorded answer: a gap, and the node fails as configured.
        var changed = Engine(Host(callNode: CallNode.Replace("\"slots\": \"3\"", "\"slots\": \"4\"", StringComparison.Ordinal)), withPromoted: false);
        var other = await changed.ReplayAsync(bundle, cancellationToken: ct);
        Assert.False(other.Result.Succeeded);
        var gap = Assert.Single(other.Report.Gaps);
        Assert.Equal(("pipeline", "promoted", "inputs_changed"), (gap.Kind, gap.Source, gap.Reason));
    }

    [Fact]
    public void PipelinesThatCallEachOther_AreRefused_WithTheCycle()
    {
        // promoted calls feed, which calls promoted.
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

        Assert.True(result.Succeeded, result.Error); // the inner call fails its node, which is skipped
        Assert.NotEmpty(result.Items);
    }
}
